// bir2cir — lower Backend IR (BIR) JSON into CLR IR (CIR) JSON.
//
// The default output is BIR-compatible CIR so existing ilemit-based pipelines keep
// working while lowering responsibilities move into this stage.
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

static class Bir2Cir
{
    static int Main(string[] args)
    {
        try
        {
            var options = DriverOptions.Parse(args);
            new Pipeline(options).Run();
            return 0;
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("usage: bir2cir <out-dir> [--compat-bir|--native-cir] [--ref <dll>]... <file.bir.json>...");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"bir2cir: {ex.Message}");
            return 1;
        }
    }
}

sealed class Pipeline
{
    readonly DriverOptions _options;

    public Pipeline(DriverOptions options) => _options = options;

    public void Run()
    {
        Directory.CreateDirectory(_options.OutDir);

        var birFiles = LoadBirFiles(_options.Inputs);
        var refs = ReferenceMetadataIndex.Build(_options.References);
        var cirFiles = TransformFiles(birFiles, refs);
        WriteCirFiles(cirFiles);

        var suspend = SuspendShapeAnalysis.Combine(birFiles.Select(f => f.Suspend));
        Console.Error.WriteLine(
            $"bir2cir: lowered {birFiles.Count} BIR file(s) -> {_options.OutDir} ({refs.Count} ref(s), mode: {_options.ModeName}, suspend: {suspend.FunctionCount} fn/{suspend.AwaitCount} await)");
    }

    static List<BirFile> LoadBirFiles(IReadOnlyList<string> inputs)
    {
        var files = new List<BirFile>();
        foreach (var input in inputs)
        {
            var path = Path.GetFullPath(input);
            var json = File.ReadAllText(path);
            var root = JsonNode.Parse(json) ?? throw new UsageException($"bir2cir: invalid JSON root: {path}");
            files.Add(new BirFile(
                path,
                json,
                root,
                SuspendShapeAnalyzer.Analyze(root),
                CallSiteAnalyzer.Analyze(root),
                TypeSiteAnalyzer.Analyze(root)));
        }

        return files;
    }

    List<CirFile> TransformFiles(IReadOnlyList<BirFile> birFiles, ReferenceMetadataIndex refs)
    {
        var files = new List<CirFile>();
        foreach (var bir in birFiles)
        {
            var outputName = OutputNameFor(bir.Path);
            var json = _options.Mode == OutputMode.CompatBir
                ? bir.Json
                : NativeCirEnvelope.Create(bir, refs).ToJsonString(JsonOptions.Indented);
            files.Add(new CirFile(outputName, json));
        }

        return files;
    }

    void WriteCirFiles(IReadOnlyList<CirFile> files)
    {
        foreach (var file in files)
            File.WriteAllText(Path.Combine(_options.OutDir, file.OutputName), file.Json);
    }

    static string OutputNameFor(string inputPath)
    {
        var name = Path.GetFileName(inputPath);
        if (name.EndsWith(".bir.json", StringComparison.Ordinal))
            return name[..^".bir.json".Length] + ".cir.json";
        if (name.EndsWith(".json", StringComparison.Ordinal))
            return name[..^".json".Length] + ".cir.json";
        return name + ".cir.json";
    }
}

sealed record DriverOptions(string OutDir, OutputMode Mode, IReadOnlyList<string> References, IReadOnlyList<string> Inputs)
{
    public string ModeName => Mode == OutputMode.CompatBir ? "compat-bir" : "native-cir";

    public static DriverOptions Parse(string[] args)
    {
        if (args.Length < 2)
            throw new UsageException("bir2cir: missing output directory or input files");

        var outDir = args[0];
        var refs = new List<string>();
        var inputs = new List<string>();
        var mode = OutputMode.CompatBir;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ref" when i + 1 < args.Length:
                    refs.Add(Path.GetFullPath(args[++i]));
                    break;
                case "--ref":
                    throw new UsageException("bir2cir: --ref requires a DLL path");
                case "--compat-bir":
                    mode = OutputMode.CompatBir;
                    break;
                case "--native-cir":
                    mode = OutputMode.NativeCir;
                    break;
                default:
                    inputs.Add(args[i]);
                    break;
            }
        }

        if (inputs.Count == 0)
            throw new UsageException("bir2cir: no BIR input files");

        return new DriverOptions(outDir, mode, refs, inputs);
    }
}

enum OutputMode
{
    CompatBir,
    NativeCir,
}

sealed record BirFile(string Path, string Json, JsonNode Root, SuspendShapeAnalysis Suspend, CallSiteAnalysis Calls, TypeSiteAnalysis Types);
sealed record CirFile(string OutputName, string Json);

sealed class ReferenceMetadataIndex
{
    const string KotlinFileClassAttr = "DotKt.Runtime.CompilerServices.KotlinFileClassAttribute";
    const string KotlinFunctionAttr = "DotKt.Runtime.CompilerServices.KotlinFunctionAttribute";
    const string KotlinInlineAttr = "DotKt.Runtime.CompilerServices.KotlinInlineAttribute";
    const string DotKtNamespaceProjectionAttr = "DotKt.Runtime.CompilerServices.DotKtNamespaceProjectionAttribute";

    readonly List<ReferenceAssembly> _assemblies;

    ReferenceMetadataIndex(List<ReferenceAssembly> assemblies) => _assemblies = assemblies;

    public int Count => _assemblies.Count;
    public IReadOnlyList<ReferenceAssembly> Assemblies => _assemblies;

    public IReadOnlyList<ResolutionCandidate> Resolve(CallSite site)
    {
        if (site.Status != "kotlin-symbol") return Array.Empty<ResolutionCandidate>();

        var matches = new List<ResolutionCandidate>();
        foreach (var asm in _assemblies)
        {
            foreach (var member in asm.DotKt.Members)
            {
                if (!MemberKindMatches(site.Kind, member.Kind)) continue;
                if (site.Kind == "new")
                {
                    if (!OwnerMatches(site.TargetOwner, member.Owner)) continue;
                }
                else
                {
                    if (site.TargetName != member.Name) continue;
                    if (site.TargetOwner.Length > 0 && !OwnerMatches(site.TargetOwner, member.Owner)) continue;
                }
                if (!ArityMatches(site, member)) continue;

                matches.Add(new ResolutionCandidate(
                    member.Kind,
                    asm.Name,
                    member.Owner,
                    member.Name,
                    member.IsStatic,
                    member.IsFileClass,
                    member.ParameterTypes,
                    member.ReturnType));
            }
        }

        return matches;
    }

    public IReadOnlyList<TypeResolutionCandidate> Resolve(TypeSite site)
    {
        if (site.Status != "kotlin-symbol") return Array.Empty<TypeResolutionCandidate>();

        var matches = new List<TypeResolutionCandidate>();
        foreach (var asm in _assemblies)
        {
            foreach (var type in asm.DotKt.Types)
            {
                if (!OwnerMatches(site.NormalizedType, type.Name)) continue;
                matches.Add(new TypeResolutionCandidate(asm.Name, type.Name, type.Kind, type.IsFileClass));
            }
        }

        return matches;
    }

    static bool MemberKindMatches(string siteKind, string memberKind) => siteKind switch
    {
        "new" => memberKind == "constructor",
        "field" or "staticField" or "setFieldExpr" => memberKind == "field",
        _ => memberKind == "method",
    };

    static bool ArityMatches(CallSite site, DotKtMemberMetadata member)
    {
        if (member.Kind == "field") return true;
        if (site.ArgCount >= 0 && member.ParameterTypes.Count != site.ArgCount) return false;
        if (site.ArgTypes.Count == 0 || site.ArgTypes.Any(t => t.Length == 0)) return true;
        if (member.ParameterTypes.Count != site.ArgTypes.Count) return false;

        for (var i = 0; i < site.ArgTypes.Count; i++)
            if (!TypeMatches(site.ArgTypes[i], member.ParameterTypes[i]))
                return false;

        return true;
    }

    static bool TypeMatches(string siteType, string memberType) =>
        NormalizeType(siteType) == NormalizeType(memberType);

    static string NormalizeType(string type)
    {
        var value = type.Trim();
        if (value.StartsWith("clr:", StringComparison.Ordinal)) value = value["clr:".Length..];
        if (value.StartsWith("@", StringComparison.Ordinal)) value = value[1..];
        if (value.StartsWith("clrg:", StringComparison.Ordinal)) return value;
        return value switch
        {
            "bool" => "System.Boolean",
            "byte" => "System.Byte",
            "char" => "System.Char",
            "double" => "System.Double",
            "float" => "System.Single",
            "int" => "System.Int32",
            "long" => "System.Int64",
            "object" => "System.Object",
            "short" => "System.Int16",
            "string" => "System.String",
            "ubyte" => "System.Byte",
            "uint" => "System.UInt32",
            "ulong" => "System.UInt64",
            "ushort" => "System.UInt16",
            "void" => "System.Void",
            _ => value,
        };
    }

    public static ReferenceMetadataIndex Build(IReadOnlyList<string> refs)
    {
        var assemblies = new List<ReferenceAssembly>();
        foreach (var reference in refs)
        {
            if (!File.Exists(reference))
                throw new UsageException($"bir2cir: reference not found: {reference}");

            var identity = AssemblyName.GetAssemblyName(reference);
            assemblies.Add(new ReferenceAssembly(
                reference,
                identity.Name ?? Path.GetFileNameWithoutExtension(reference),
                identity.Version?.ToString() ?? "",
                ReadDotKtMetadata(reference)));
        }

        return new ReferenceMetadataIndex(assemblies);
    }

    static ReferenceDotKtMetadata ReadDotKtMetadata(string reference)
    {
        var metadata = new ReferenceDotKtMetadata();
        try
        {
            var asm = Assembly.LoadFrom(reference);

            foreach (var attr in asm.GetCustomAttributesData())
                if (attr.AttributeType.FullName == DotKtNamespaceProjectionAttr && attr.ConstructorArguments.Count == 2)
                    metadata.NamespaceProjections.Add(new NamespaceProjection(
                        attr.ConstructorArguments[0].Value?.ToString() ?? "",
                        attr.ConstructorArguments[1].Value?.ToString() ?? ""));

            foreach (var type in SafeTypes(asm, metadata))
            {
                if (HasAttribute(type.GetCustomAttributesData(), KotlinFileClassAttr))
                {
                    metadata.FileClasses.Add(type.FullName ?? type.Name);
                }

                var isFileClass = HasAttribute(type.GetCustomAttributesData(), KotlinFileClassAttr);
                metadata.Types.Add(new DotKtTypeMetadata(
                    type.FullName ?? type.Name,
                    TypeKind(type),
                    isFileClass));

                foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    metadata.Members.Add(new DotKtMemberMetadata(
                        "constructor",
                        type.FullName ?? type.Name,
                        ".ctor",
                        false,
                        isFileClass,
                        ctor.GetParameters().Select(p => TypeName(p.ParameterType)).ToList(),
                        type.FullName ?? type.Name));
                }

                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    metadata.Members.Add(new DotKtMemberMetadata(
                        "field",
                        type.FullName ?? type.Name,
                        field.Name,
                        field.IsStatic,
                        isFileClass,
                        Array.Empty<string>(),
                        TypeName(field.FieldType)));
                }

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (method.IsPublic)
                        metadata.Members.Add(new DotKtMemberMetadata(
                            "method",
                            type.FullName ?? type.Name,
                            method.Name,
                            method.IsStatic,
                            isFileClass,
                            method.GetParameters().Select(p => TypeName(p.ParameterType)).ToList(),
                            TypeName(method.ReturnType)));

                    var attrs = method.GetCustomAttributesData();
                    var flags = KotlinFunctionFlags(attrs);
                    var hasInline = HasAttribute(attrs, KotlinInlineAttr);
                    if (flags != 0 || hasInline)
                        metadata.Functions.Add(new KotlinFunctionMetadata(
                            type.FullName ?? type.Name,
                            method.Name,
                            flags,
                            hasInline));
                }
            }
        }
        catch (Exception ex)
        {
            metadata.Diagnostics.Add($"{Path.GetFileName(reference)}: metadata scan failed: {ex.GetType().Name}: {ex.Message}");
        }

        return metadata;
    }

    static IEnumerable<Type> SafeTypes(Assembly asm, ReferenceDotKtMetadata metadata)
    {
        try
        {
            return asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            metadata.Diagnostics.Add($"{asm.GetName().Name}: partial type load: {ex.LoaderExceptions.Length} loader exception(s)");
            return ex.Types.Where(t => t != null).Cast<Type>();
        }
    }

    static bool HasAttribute(IList<CustomAttributeData> attrs, string fullName) =>
        attrs.Any(a => a.AttributeType.FullName == fullName);

    static int KotlinFunctionFlags(IList<CustomAttributeData> attrs)
    {
        var attr = attrs.FirstOrDefault(a => a.AttributeType.FullName == KotlinFunctionAttr);
        if (attr == null || attr.ConstructorArguments.Count == 0) return 0;
        var value = attr.ConstructorArguments[0].Value;
        return value is int i ? i : 0;
    }

    static string TypeName(Type type) =>
        type.FullName ?? type.Name;

    static string TypeKind(Type type)
    {
        if (type.IsInterface) return "interface";
        if (type.IsEnum) return "enum";
        if (type.IsValueType) return "struct";
        return "class";
    }

    static bool OwnerMatches(string requested, string candidate)
    {
        if (requested == candidate) return true;
        var bareRequested = StripTypeArgs(requested).TrimStart('@');
        var bareCandidate = StripTypeArgs(candidate);
        return bareRequested == bareCandidate || bareCandidate.EndsWith("." + bareRequested, StringComparison.Ordinal);
    }

    static string StripTypeArgs(string value)
    {
        var idx = value.IndexOf('[');
        return idx >= 0 ? value[..idx] : value;
    }
}

sealed record ReferenceAssembly(string Path, string Name, string Version, ReferenceDotKtMetadata DotKt)
{
    public JsonObject ToJson() => new()
    {
        ["path"] = Path,
        ["name"] = Name,
        ["version"] = Version,
        ["dotkt"] = DotKt.ToJson(),
    };
}

sealed class ReferenceDotKtMetadata
{
    public readonly List<NamespaceProjection> NamespaceProjections = new();
    public readonly List<string> FileClasses = new();
    public readonly List<DotKtTypeMetadata> Types = new();
    public readonly List<DotKtMemberMetadata> Members = new();
    public readonly List<KotlinFunctionMetadata> Functions = new();
    public readonly List<string> Diagnostics = new();

    public JsonObject ToJson() => new()
    {
        ["namespaceProjections"] = new JsonArray(NamespaceProjections.Select(p => p.ToJson()).Cast<JsonNode>().ToArray()),
        ["fileClasses"] = new JsonArray(FileClasses.Select(s => JsonValue.Create(s)).Cast<JsonNode>().ToArray()),
        ["types"] = new JsonArray(Types.Select(t => t.ToJson()).Cast<JsonNode>().ToArray()),
        ["members"] = new JsonArray(Members.Select(m => m.ToJson()).Cast<JsonNode>().ToArray()),
        ["functions"] = new JsonArray(Functions.Select(f => f.ToJson()).Cast<JsonNode>().ToArray()),
        ["diagnostics"] = new JsonArray(Diagnostics.Select(s => JsonValue.Create(s)).Cast<JsonNode>().ToArray()),
    };
}

sealed record NamespaceProjection(string KotlinPrefix, string DotNetPrefix)
{
    public JsonObject ToJson() => new()
    {
        ["kotlinPrefix"] = KotlinPrefix,
        ["dotNetPrefix"] = DotNetPrefix,
    };
}

sealed record KotlinFunctionMetadata(string Owner, string Name, int Flags, bool HasInlineBody)
{
    public JsonObject ToJson() => new()
    {
        ["owner"] = Owner,
        ["name"] = Name,
        ["flags"] = Flags,
        ["hasInlineBody"] = HasInlineBody,
    };
}

sealed record DotKtTypeMetadata(string Name, string Kind, bool IsFileClass)
{
    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["kind"] = Kind,
        ["isFileClass"] = IsFileClass,
    };
}

sealed record DotKtMemberMetadata(
    string Kind,
    string Owner,
    string Name,
    bool IsStatic,
    bool IsFileClass,
    IReadOnlyList<string> ParameterTypes,
    string ReturnType)
{
    public JsonObject ToJson() => new()
    {
        ["kind"] = Kind,
        ["owner"] = Owner,
        ["name"] = Name,
        ["isStatic"] = IsStatic,
        ["isFileClass"] = IsFileClass,
        ["parameterTypes"] = new JsonArray(ParameterTypes.Select(s => JsonValue.Create(s)).Cast<JsonNode>().ToArray()),
        ["returnType"] = ReturnType,
    };
}

sealed record TypeResolutionCandidate(string Assembly, string Name, string Kind, bool IsFileClass)
{
    public JsonObject ToJson() => new()
    {
        ["assembly"] = Assembly,
        ["name"] = Name,
        ["kind"] = Kind,
        ["isFileClass"] = IsFileClass,
    };

    public JsonObject ToTypeRefJson() => new()
    {
        ["k"] = "clr.typeRef",
        ["assembly"] = Assembly,
        ["name"] = Name,
        ["kind"] = Kind,
        ["isFileClass"] = IsFileClass,
    };
}

sealed record ResolutionCandidate(
    string Kind,
    string Assembly,
    string Owner,
    string Name,
    bool IsStatic,
    bool IsFileClass,
    IReadOnlyList<string> ParameterTypes,
    string ReturnType)
{
    public JsonObject ToJson() => new()
    {
        ["kind"] = Kind,
        ["assembly"] = Assembly,
        ["owner"] = Owner,
        ["name"] = Name,
        ["isStatic"] = IsStatic,
        ["isFileClass"] = IsFileClass,
        ["parameterTypes"] = new JsonArray(ParameterTypes.Select(s => JsonValue.Create(s)).Cast<JsonNode>().ToArray()),
        ["returnType"] = ReturnType,
    };

    public JsonObject ToMemberRefJson() => new()
    {
        ["k"] = Kind switch
        {
            "constructor" => "clr.constructorRef",
            "field" => "clr.fieldRef",
            _ => "clr.methodRef",
        },
        ["assembly"] = Assembly,
        ["owner"] = Owner,
        ["name"] = Name,
        ["isStatic"] = IsStatic,
        ["isFileClass"] = IsFileClass,
        ["parameterTypes"] = new JsonArray(ParameterTypes.Select(s => JsonValue.Create(s)).Cast<JsonNode>().ToArray()),
        ["returnType"] = ReturnType,
    };
}

static class NativeCirEnvelope
{
    public static JsonObject Create(BirFile bir, ReferenceMetadataIndex refs)
    {
        return new JsonObject
        {
            ["cirVersion"] = 1,
            ["mode"] = "native-cir-skeleton",
            ["sourcePath"] = bir.Path,
            ["references"] = new JsonArray(refs.Assemblies
                .Select(r => r.ToJson())
                .Cast<JsonNode>()
                .ToArray()),
            ["analysis"] = bir.Suspend.ToJson(),
            ["callSites"] = bir.Calls.ToJson(),
            ["typeSites"] = bir.Types.ToJson(),
            ["resolutionDraft"] = ResolutionDraft.Create(bir.Calls, refs),
            ["typeResolutionDraft"] = TypeResolutionDraft.Create(bir.Types, refs),
            ["cirDraft"] = NativeCirDraft.Create(bir, refs),
            ["bir"] = bir.Root.DeepClone(),
        };
    }
}

static class NativeCirDraft
{
    public static JsonObject Create(BirFile bir, ReferenceMetadataIndex refs) => new()
    {
        ["asyncFunctions"] = NativeAsyncCirDraft.Create(bir.Root),
        ["resolvedCalls"] = ResolvedCallCirDraft.Create(bir.Calls, refs),
        ["resolvedTypes"] = ResolvedTypeCirDraft.Create(bir.Types, refs),
        ["loweredBir"] = NativeExpressionCirDraft.Create(bir.Root, bir.Calls, bir.Types, refs),
    };
}

static class ResolutionDraft
{
    public static JsonObject Create(CallSiteAnalysis calls, ReferenceMetadataIndex refs)
    {
        var resolutions = new JsonArray();
        foreach (var site in calls.Sites.Where(s => s.Status == "kotlin-symbol"))
        {
            var candidates = refs.Resolve(site);
            resolutions.Add(new JsonObject
            {
                ["site"] = site.ToJson(),
                ["candidateCount"] = candidates.Count,
                ["status"] = candidates.Count switch
                {
                    0 => "unresolved-in-references",
                    1 => "resolved-in-reference",
                    _ => "ambiguous-in-references",
                },
                ["candidates"] = new JsonArray(candidates.Select(c => c.ToJson()).Cast<JsonNode>().ToArray()),
            });
        }

        return new JsonObject
        {
            ["kotlinSymbolSites"] = resolutions,
        };
    }
}

static class TypeResolutionDraft
{
    public static JsonObject Create(TypeSiteAnalysis types, ReferenceMetadataIndex refs)
    {
        var resolutions = new JsonArray();
        foreach (var site in types.Sites.Where(s => s.Status == "kotlin-symbol"))
        {
            var candidates = refs.Resolve(site);
            resolutions.Add(new JsonObject
            {
                ["site"] = site.ToJson(),
                ["candidateCount"] = candidates.Count,
                ["status"] = candidates.Count switch
                {
                    0 => "unresolved-in-references",
                    1 => "resolved-in-reference",
                    _ => "ambiguous-in-references",
                },
                ["candidates"] = new JsonArray(candidates.Select(c => c.ToJson()).Cast<JsonNode>().ToArray()),
            });
        }

        return new JsonObject
        {
            ["kotlinTypeSites"] = resolutions,
        };
    }
}

static class ResolvedCallCirDraft
{
    public static JsonObject Create(CallSiteAnalysis calls, ReferenceMetadataIndex refs)
    {
        var lowerable = new JsonArray();
        var unresolved = 0;
        var ambiguous = 0;

        foreach (var site in calls.Sites.Where(s => s.Status == "kotlin-symbol"))
        {
            var candidates = refs.Resolve(site);
            if (candidates.Count == 1)
            {
                lowerable.Add(CreateResolvedCall(site, candidates[0]));
                continue;
            }

            if (candidates.Count == 0) unresolved++;
            else ambiguous++;
        }

        return new JsonObject
        {
            ["summary"] = new JsonObject
            {
                ["resolved"] = lowerable.Count,
                ["unresolved"] = unresolved,
                ["ambiguous"] = ambiguous,
            },
            ["calls"] = lowerable,
        };
    }

    static JsonObject CreateResolvedCall(CallSite site, ResolutionCandidate candidate)
    {
        var nodeKind = site.Kind switch
        {
            "new" => "clr.newobj",
            "field" => "clr.ldfld",
            "staticField" => "clr.ldsfld",
            "setFieldExpr" => "clr.stfld",
            _ => "clr.call",
        };

        return new JsonObject
        {
            ["k"] = nodeKind,
            ["sourcePath"] = site.Path,
            ["sourceKind"] = site.Kind,
            ["sourceOwner"] = site.Owner,
            ["sourceMethod"] = site.Method,
            ["memberRef"] = candidate.ToMemberRefJson(),
            ["dispatch"] = DispatchFor(site, candidate),
        };
    }

    static string DispatchFor(CallSite site, ResolutionCandidate candidate)
    {
        if (site.Kind == "new") return "constructor";
        if (candidate.IsStatic || site.Kind is "callStatic" or "staticField") return "static";
        return "instance";
    }
}

static class ResolvedTypeCirDraft
{
    public static JsonObject Create(TypeSiteAnalysis types, ReferenceMetadataIndex refs)
    {
        var lowerable = new JsonArray();
        var unresolved = 0;
        var ambiguous = 0;

        foreach (var site in types.Sites.Where(s => s.Status == "kotlin-symbol"))
        {
            var candidates = refs.Resolve(site);
            if (candidates.Count == 1)
            {
                lowerable.Add(new JsonObject
                {
                    ["k"] = "clr.typeRef",
                    ["sourcePath"] = site.Path,
                    ["sourceProperty"] = site.Property,
                    ["sourceType"] = site.Type,
                    ["typeRef"] = candidates[0].ToTypeRefJson(),
                });
                continue;
            }

            if (candidates.Count == 0) unresolved++;
            else ambiguous++;
        }

        return new JsonObject
        {
            ["summary"] = new JsonObject
            {
                ["resolved"] = lowerable.Count,
                ["unresolved"] = unresolved,
                ["ambiguous"] = ambiguous,
            },
            ["types"] = lowerable,
        };
    }
}

static class NativeExpressionCirDraft
{
    public static JsonNode Create(JsonNode root, CallSiteAnalysis calls, TypeSiteAnalysis types, ReferenceMetadataIndex refs)
    {
        var resolvedCalls = calls.Sites
            .Where(s => s.Status == "kotlin-symbol")
            .Select(s => new ResolvedSite(s, refs.Resolve(s)))
            .Where(r => r.Candidates.Count == 1)
            .ToDictionary(r => r.Site.Path, r => r, StringComparer.Ordinal);
        var resolvedTypes = types.Sites
            .Where(s => s.Status == "kotlin-symbol")
            .Select(s => new ResolvedTypeSite(s, refs.Resolve(s)))
            .Where(r => r.Candidates.Count == 1)
            .ToDictionary(r => r.Site.Path, r => r, StringComparer.Ordinal);

        return LowerNode(root, "$", resolvedCalls, resolvedTypes);
    }

    static JsonNode LowerNode(
        JsonNode node,
        string path,
        IReadOnlyDictionary<string, ResolvedSite> resolvedCalls,
        IReadOnlyDictionary<string, ResolvedTypeSite> resolvedTypes)
    {
        if (node is JsonObject obj)
        {
            if (resolvedCalls.TryGetValue(path, out var site))
                return LowerResolvedExpression(obj, path, site.Site, site.Candidates[0], resolvedCalls, resolvedTypes);

            var copy = new JsonObject();
            foreach (var child in obj)
            {
                var childPath = path + "." + EscapePathSegment(child.Key);
                copy[child.Key] = child.Value == null
                    ? null
                    : LowerTypeOrNode(child.Value, childPath, resolvedCalls, resolvedTypes);
            }
            return copy;
        }

        if (node is JsonArray arr)
        {
            var copy = new JsonArray();
            for (var i = 0; i < arr.Count; i++)
            {
                var itemPath = path + "[" + i + "]";
                copy.Add(arr[i] == null ? null : LowerTypeOrNode(arr[i], itemPath, resolvedCalls, resolvedTypes));
            }
            return copy;
        }

        return node.DeepClone();
    }

    static JsonNode LowerTypeOrNode(
        JsonNode node,
        string path,
        IReadOnlyDictionary<string, ResolvedSite> resolvedCalls,
        IReadOnlyDictionary<string, ResolvedTypeSite> resolvedTypes)
    {
        if (resolvedTypes.TryGetValue(path, out var type))
            return new JsonObject
            {
                ["k"] = "clr.typeRef",
                ["sourcePath"] = type.Site.Path,
                ["sourceType"] = type.Site.Type,
                ["typeRef"] = type.Candidates[0].ToTypeRefJson(),
            };

        return LowerNode(node, path, resolvedCalls, resolvedTypes);
    }

    static JsonObject LowerResolvedExpression(
        JsonObject obj,
        string path,
        CallSite site,
        ResolutionCandidate candidate,
        IReadOnlyDictionary<string, ResolvedSite> resolvedCalls,
        IReadOnlyDictionary<string, ResolvedTypeSite> resolvedTypes)
    {
        var lowered = new JsonObject
        {
            ["k"] = NodeKindFor(site.Kind),
            ["sourcePath"] = site.Path,
            ["sourceKind"] = site.Kind,
            ["memberRef"] = candidate.ToMemberRefJson(),
        };

        if (obj["recv"] is JsonNode recv)
            lowered["recv"] = LowerTypeOrNode(recv, path + ".recv", resolvedCalls, resolvedTypes);
        if (obj["args"] is JsonNode args)
            lowered["args"] = LowerTypeOrNode(args, path + ".args", resolvedCalls, resolvedTypes);
        if (obj["value"] is JsonNode value)
            lowered["value"] = LowerTypeOrNode(value, path + ".value", resolvedCalls, resolvedTypes);

        if (site.Kind is "callStatic" or "callInstance")
        {
            lowered["dispatch"] = candidate.IsStatic || site.Kind == "callStatic" ? "static" : "instance";
            if (obj["virtual"] != null)
                lowered["virtual"] = obj["virtual"]?.DeepClone();
        }

        return lowered;
    }

    static string NodeKindFor(string siteKind) => siteKind switch
    {
        "new" => "clr.newobj",
        "field" => "clr.ldfld",
        "staticField" => "clr.ldsfld",
        "setFieldExpr" => "clr.stfld",
        _ => "clr.call",
    };

    static string EscapePathSegment(string segment) =>
        segment.Replace("~", "~0", StringComparison.Ordinal).Replace(".", "~1", StringComparison.Ordinal);
}

sealed record ResolvedSite(CallSite Site, IReadOnlyList<ResolutionCandidate> Candidates);
sealed record ResolvedTypeSite(TypeSite Site, IReadOnlyList<TypeResolutionCandidate> Candidates);

sealed class TypeSiteAnalyzer
{
    static readonly HashSet<string> TypeProperties = new(StringComparer.Ordinal)
    {
        "type",
        "ownerType",
        "ret",
        "retType",
        "resultType",
        "base",
        "interfaces",
    };

    static readonly HashSet<string> PrimitiveTypes = new(StringComparer.Ordinal)
    {
        "bool",
        "byte",
        "char",
        "double",
        "float",
        "int",
        "long",
        "object",
        "short",
        "string",
        "ubyte",
        "uint",
        "ulong",
        "ushort",
        "void",
    };

    public static TypeSiteAnalysis Analyze(JsonNode root)
    {
        var sites = new List<TypeSite>();
        Collect(root, "$", sites);
        return new TypeSiteAnalysis(sites);
    }

    static void Collect(JsonNode node, string path, List<TypeSite> sites)
    {
        if (node is JsonObject obj)
        {
            foreach (var child in obj)
            {
                if (child.Value == null) continue;
                var childPath = path + "." + EscapePathSegment(child.Key);
                if (TypeProperties.Contains(child.Key))
                    CollectTypeProperty(child.Key, child.Value, childPath, sites);
                Collect(child.Value, childPath, sites);
            }
        }
        else if (node is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
                if (arr[i] != null)
                    Collect(arr[i], path + "[" + i + "]", sites);
        }
    }

    static void CollectTypeProperty(string property, JsonNode value, string path, List<TypeSite> sites)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var type))
        {
            AddType(property, path, type, sites);
        }
        else if (value is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
                if (arr[i] is JsonValue item && item.TryGetValue<string>(out var itemType))
                    AddType(property, path + "[" + i + "]", itemType, sites);
        }
    }

    static void AddType(string property, string path, string type, List<TypeSite> sites)
    {
        if (string.IsNullOrWhiteSpace(type)) return;
        sites.Add(new TypeSite(
            path,
            property,
            type,
            NormalizeTypeName(type),
            StatusFor(type)));
    }

    static string StatusFor(string type)
    {
        var normalized = NormalizeTypeName(type);
        if (PrimitiveTypes.Contains(normalized)) return "already-clr";
        if (normalized.StartsWith("clr:", StringComparison.Ordinal)) return "already-clr";
        if (normalized.StartsWith("clrg:", StringComparison.Ordinal)) return "already-clr";
        if (normalized.StartsWith("array:", StringComparison.Ordinal)) return "already-clr";
        if (normalized.StartsWith("func:", StringComparison.Ordinal)) return "already-clr";
        if (normalized.StartsWith("gp:", StringComparison.Ordinal)) return "already-clr";
        return "kotlin-symbol";
    }

    static string NormalizeTypeName(string type) =>
        type.Trim().TrimStart('@');

    static string EscapePathSegment(string segment) =>
        segment.Replace("~", "~0", StringComparison.Ordinal).Replace(".", "~1", StringComparison.Ordinal);
}

sealed record TypeSiteAnalysis(IReadOnlyList<TypeSite> Sites)
{
    public JsonObject ToJson()
    {
        var byStatus = Sites
            .GroupBy(s => s.Status)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());

        return new JsonObject
        {
            ["total"] = Sites.Count,
            ["byStatus"] = new JsonObject(byStatus.ToDictionary(kv => kv.Key, kv => (JsonNode)JsonValue.Create(kv.Value))),
            ["sites"] = new JsonArray(Sites.Select(s => s.ToJson()).Cast<JsonNode>().ToArray()),
        };
    }
}

sealed record TypeSite(string Path, string Property, string Type, string NormalizedType, string Status)
{
    public JsonObject ToJson() => new()
    {
        ["path"] = Path,
        ["property"] = Property,
        ["type"] = Type,
        ["normalizedType"] = NormalizedType,
        ["status"] = Status,
    };
}

sealed class CallSiteAnalyzer
{
    static readonly HashSet<string> InterestingKinds = new(StringComparer.Ordinal)
    {
        "callStatic",
        "callInstance",
        "new",
        "field",
        "staticField",
        "setFieldExpr",
        "clrStatic",
        "clrGenericStatic",
        "clrInstance",
        "clrGenericInstance",
        "clrNew",
        "clrPropGet",
        "clrPropSet",
        "clrStaticField",
    };

    public static CallSiteAnalysis Analyze(JsonNode root)
    {
        var sites = new List<CallSite>();
        Collect(root, owner: null, method: null, path: "$", sites);
        return new CallSiteAnalysis(sites);
    }

    static void Collect(JsonNode node, string owner, string method, string path, List<CallSite> sites)
    {
        if (node is JsonObject obj)
        {
            var nextOwner = owner;
            var nextMethod = method;

            if (obj["kind"]?.GetValue<string>() is "class" or "interface")
                nextOwner = StringProp(obj, "name") ?? owner;
            if ((obj["params"] is JsonArray && obj["body"] is JsonArray) || obj["steps"] is JsonArray)
                nextMethod = StringProp(obj, "name") ?? method;

            var kind = StringProp(obj, "k");
            if (kind != null && InterestingKinds.Contains(kind))
                sites.Add(CallSite.From(kind, path, nextOwner, nextMethod, obj));

            foreach (var child in obj)
                if (child.Value != null)
                    Collect(child.Value, nextOwner, nextMethod, path + "." + EscapePathSegment(child.Key), sites);
        }
        else if (node is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                var item = arr[i];
                if (item != null)
                    Collect(item, owner, method, path + "[" + i + "]", sites);
            }
        }
    }

    static string EscapePathSegment(string segment) =>
        segment.Replace("~", "~0", StringComparison.Ordinal).Replace(".", "~1", StringComparison.Ordinal);

    static string StringProp(JsonObject obj, string name) => obj[name]?.GetValue<string>();
}

sealed record CallSiteAnalysis(IReadOnlyList<CallSite> Sites)
{
    public JsonObject ToJson()
    {
        var byStatus = Sites
            .GroupBy(s => s.Status)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());

        return new JsonObject
        {
            ["total"] = Sites.Count,
            ["byStatus"] = new JsonObject(byStatus.ToDictionary(kv => kv.Key, kv => (JsonNode)JsonValue.Create(kv.Value))),
            ["sites"] = new JsonArray(Sites.Select(s => s.ToJson()).Cast<JsonNode>().ToArray()),
        };
    }
}

sealed record CallSite(
    string Kind,
    string Path,
    string Status,
    string Owner,
    string Method,
    string TargetOwner,
    string TargetName,
    string Signature,
    int ArgCount,
    IReadOnlyList<string> ArgTypes)
{
    public static CallSite From(string kind, string path, string owner, string method, JsonObject node)
    {
        var targetOwner = StringProp(node, "owner")
            ?? StringProp(node, "ownerType")
            ?? StringProp(node, "type")
            ?? "";
        var targetName = StringProp(node, "method")
            ?? StringProp(node, "name")
            ?? "";
        var signature = StringProp(node, "sig") ?? "";
        var argTypes = ArgumentTypes(node, signature);

        return new CallSite(
            kind,
            path,
            StatusFor(kind, targetOwner),
            owner ?? "",
            method ?? "",
            targetOwner,
            targetName,
            signature,
            node["args"] is JsonArray args ? args.Count : -1,
            argTypes);
    }

    public JsonObject ToJson() => new()
    {
        ["kind"] = Kind,
        ["path"] = Path,
        ["status"] = Status,
        ["owner"] = Owner,
        ["method"] = Method,
        ["targetOwner"] = TargetOwner,
        ["targetName"] = TargetName,
        ["signature"] = Signature,
        ["argCount"] = ArgCount,
        ["argTypes"] = new JsonArray(ArgTypes.Select(t => JsonValue.Create(t)).Cast<JsonNode>().ToArray()),
    };

    static string StatusFor(string kind, string targetOwner)
    {
        if (kind.StartsWith("clr", StringComparison.Ordinal)) return "already-clr";
        if (targetOwner.StartsWith("clr:", StringComparison.Ordinal) || targetOwner.StartsWith("clrg:", StringComparison.Ordinal)) return "already-clr";
        return "kotlin-symbol";
    }

    static string StringProp(JsonObject obj, string name) => obj[name]?.GetValue<string>();

    static IReadOnlyList<string> ArgumentTypes(JsonObject node, string signature)
    {
        if (!string.IsNullOrWhiteSpace(signature))
            return signature.Split(',', StringSplitOptions.TrimEntries).ToList();

        if (node["args"] is not JsonArray args) return Array.Empty<string>();

        var inferred = new List<string>();
        foreach (var arg in args)
            inferred.Add(InferExpressionType(arg));
        return inferred;
    }

    static string InferExpressionType(JsonNode node)
    {
        if (node is not JsonObject obj) return "";
        return StringProp(obj, "type")
            ?? StringProp(obj, "retType")
            ?? StringProp(obj, "resultType")
            ?? StringProp(obj, "ret")
            ?? "";
    }
}

static class NativeAsyncCirDraft
{
    public static JsonArray Create(JsonNode root)
    {
        var functions = new JsonArray();
        CollectFileMethods(root, owner: null, functions);
        return functions;
    }

    static void CollectFileMethods(JsonNode node, string owner, JsonArray functions)
    {
        if (node is not JsonObject obj) return;

        if (obj["methods"] is JsonArray methods)
            foreach (var method in methods)
                CollectMethod(method, owner, functions);

        if (obj["types"] is JsonArray types)
            foreach (var type in types)
                CollectType(type, functions);
    }

    static void CollectType(JsonNode type, JsonArray functions)
    {
        if (type is not JsonObject obj) return;

        var owner = StringProp(obj, "name");
        if (obj["methods"] is JsonArray methods)
            foreach (var method in methods)
                CollectMethod(method, owner, functions);

        if (obj["types"] is JsonArray nested)
            foreach (var child in nested)
                CollectType(child, functions);
    }

    static void CollectMethod(JsonNode method, string owner, JsonArray functions)
    {
        if (method is not JsonObject obj || !BoolProp(obj, "suspend")) return;
        var steps = obj["steps"] as JsonArray;
        var shape = AsyncDraftShape.Classify(steps);

        functions.Add(new JsonObject
        {
            ["k"] = "clr.asyncFunction",
            ["owner"] = owner,
            ["name"] = StringProp(obj, "name") ?? "<anonymous>",
            ["loweringStatus"] = shape.Status,
            ["unknownSteps"] = new JsonArray(shape.UnknownSteps.Select(s => JsonValue.Create(s)).Cast<JsonNode>().ToArray()),
            ["resultType"] = StringProp(obj, "resultType") ?? StringProp(obj, "ret") ?? "void",
            ["taskType"] = TaskTypeFor(StringProp(obj, "resultType") ?? StringProp(obj, "ret") ?? "void"),
            ["params"] = obj["params"]?.DeepClone(),
            ["locals"] = obj["cpsFields"]?.DeepClone() ?? new JsonArray(),
            ["body"] = LowerSteps(steps),
        });
    }

    static JsonArray LowerSteps(JsonArray steps)
    {
        var body = new JsonArray();
        if (steps == null) return body;

        foreach (var step in steps)
            body.Add(LowerStep(step));
        return body;
    }

    static JsonObject LowerStep(JsonNode step)
    {
        if (step is not JsonObject obj)
            return UnknownStep(step);

        return StringProp(obj, "k") switch
        {
            "coSuspend" => new JsonObject
            {
                ["k"] = "clr.await",
                ["state"] = obj["state"]?.DeepClone(),
                ["awaitable"] = obj["awaitable"]?.DeepClone(),
                ["assignTo"] = obj["assignTo"]?.DeepClone(),
                ["resultType"] = obj["resultType"]?.DeepClone(),
            },
            "coSuspendIntrinsic" => new JsonObject
            {
                ["k"] = "clr.awaitIntrinsic",
                ["state"] = obj["state"]?.DeepClone(),
                ["pre"] = obj["pre"]?.DeepClone(),
                ["value"] = obj["value"]?.DeepClone(),
                ["assignTo"] = obj["assignTo"]?.DeepClone(),
                ["resultType"] = obj["resultType"]?.DeepClone(),
            },
            "coReturn" => new JsonObject
            {
                ["k"] = "return",
                ["value"] = obj["value"]?.DeepClone(),
            },
            "coLabel" => new JsonObject
            {
                ["k"] = "clr.label",
                ["id"] = obj["id"]?.DeepClone(),
            },
            "coGoto" => new JsonObject
            {
                ["k"] = "clr.goto",
                ["target"] = obj["id"]?.DeepClone(),
            },
            "coCondGoto" => new JsonObject
            {
                ["k"] = "clr.brfalse",
                ["target"] = obj["id"]?.DeepClone(),
                ["cond"] = obj["cond"]?.DeepClone(),
            },
            "coTryBegin" => new JsonObject
            {
                ["k"] = "clr.asyncTryBegin",
                ["id"] = obj["id"]?.DeepClone(),
            },
            "coCatchBegin" => new JsonObject
            {
                ["k"] = "clr.asyncCatchBegin",
                ["id"] = obj["id"]?.DeepClone(),
                ["excType"] = obj["excType"]?.DeepClone(),
                ["var"] = obj["var"]?.DeepClone(),
            },
            "coTryEnd" => new JsonObject
            {
                ["k"] = "clr.asyncTryEnd",
                ["id"] = obj["id"]?.DeepClone(),
                ["finally"] = obj["finally"]?.DeepClone(),
            },
            "var" => new JsonObject
            {
                ["k"] = "clr.asyncLocalInit",
                ["name"] = obj["name"]?.DeepClone(),
                ["type"] = obj["type"]?.DeepClone(),
                ["init"] = obj["init"]?.DeepClone(),
            },
            "exprStmt" => new JsonObject
            {
                ["k"] = "clr.exprStmt",
                ["expr"] = obj["expr"]?.DeepClone(),
            },
            "setLocal" => new JsonObject
            {
                ["k"] = "clr.setLocal",
                ["name"] = obj["name"]?.DeepClone(),
                ["value"] = obj["value"]?.DeepClone(),
            },
            _ => UnknownStep(step),
        };
    }

    static JsonObject UnknownStep(JsonNode step) => new()
    {
        ["k"] = "bir.step",
        ["node"] = step?.DeepClone(),
    };

    static string TaskTypeFor(string resultType) =>
        resultType == "void" ? "System.Threading.Tasks.Task" : $"clrg:System.Threading.Tasks.Task[{resultType}]";

    static string StringProp(JsonObject obj, string name) => obj[name]?.GetValue<string>();
    static bool BoolProp(JsonObject obj, string name) => obj[name]?.GetValue<bool>() == true;
}

sealed record AsyncDraftShape(string Status, IReadOnlyList<string> UnknownSteps)
{
    static readonly HashSet<string> LinearKinds = new(StringComparer.Ordinal)
    {
        "var",
        "coSuspend",
        "coSuspendIntrinsic",
        "coReturn",
        "exprStmt",
        "setLocal",
    };

    static readonly HashSet<string> ControlFlowKinds = new(StringComparer.Ordinal)
    {
        "coLabel",
        "coGoto",
        "coCondGoto",
    };

    static readonly HashSet<string> TryKinds = new(StringComparer.Ordinal)
    {
        "coTryBegin",
        "coCatchBegin",
        "coTryEnd",
    };

    public static AsyncDraftShape Classify(JsonArray steps)
    {
        if (steps == null) return new AsyncDraftShape("linear", Array.Empty<string>());

        var hasControlFlow = false;
        var hasTry = false;
        var unknown = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var step in steps)
        {
            if (step is not JsonObject obj)
            {
                unknown.Add("<non-object>");
                continue;
            }

            var kind = obj["k"]?.GetValue<string>() ?? "<missing>";
            if (LinearKinds.Contains(kind)) continue;
            if (ControlFlowKinds.Contains(kind))
            {
                hasControlFlow = true;
                continue;
            }
            if (TryKinds.Contains(kind))
            {
                hasTry = true;
                continue;
            }

            unknown.Add(kind);
        }

        if (unknown.Count > 0) return new AsyncDraftShape("unsupported", unknown.ToList());
        if (hasTry) return new AsyncDraftShape("try", Array.Empty<string>());
        if (hasControlFlow) return new AsyncDraftShape("control-flow", Array.Empty<string>());
        return new AsyncDraftShape("linear", Array.Empty<string>());
    }
}

sealed class SuspendShapeAnalyzer
{
    public static SuspendShapeAnalysis Analyze(JsonNode root)
    {
        var functions = new List<SuspendFunctionShape>();
        CollectFileMethods(root, owner: null, functions);
        return new SuspendShapeAnalysis(functions);
    }

    static void CollectFileMethods(JsonNode node, string owner, List<SuspendFunctionShape> functions)
    {
        if (node is not JsonObject obj) return;

        if (obj["methods"] is JsonArray methods)
            foreach (var method in methods)
                CollectMethod(method, owner, functions);

        if (obj["types"] is JsonArray types)
            foreach (var type in types)
                CollectType(type, functions);
    }

    static void CollectType(JsonNode type, List<SuspendFunctionShape> functions)
    {
        if (type is not JsonObject obj) return;

        var owner = StringProp(obj, "name");
        if (obj["methods"] is JsonArray methods)
            foreach (var method in methods)
                CollectMethod(method, owner, functions);

        if (obj["types"] is JsonArray nested)
            foreach (var child in nested)
                CollectType(child, functions);
    }

    static void CollectMethod(JsonNode method, string owner, List<SuspendFunctionShape> functions)
    {
        if (method is not JsonObject obj || !BoolProp(obj, "suspend")) return;

        var awaits = CountKind(obj, "coSuspend");
        var intrinsicAwaits = CountKind(obj, "coSuspendIntrinsic");
        var returns = CountKind(obj, "coReturn");
        var cpsFields = obj["cpsFields"] is JsonArray fields ? fields.Count : 0;
        functions.Add(new SuspendFunctionShape(
            owner,
            StringProp(obj, "name") ?? "<anonymous>",
            StringProp(obj, "resultType") ?? StringProp(obj, "ret") ?? "void",
            awaits,
            intrinsicAwaits,
            returns,
            cpsFields));
    }

    static int CountKind(JsonNode node, string kind)
    {
        if (node is JsonObject obj)
        {
            var self = StringProp(obj, "k") == kind ? 1 : 0;
            return self + obj.Sum(kv => CountKind(kv.Value, kind));
        }

        if (node is JsonArray arr)
            return arr.Sum(item => CountKind(item, kind));

        return 0;
    }

    static string StringProp(JsonObject obj, string name) => obj[name]?.GetValue<string>();
    static bool BoolProp(JsonObject obj, string name) => obj[name]?.GetValue<bool>() == true;
}

sealed record SuspendShapeAnalysis(IReadOnlyList<SuspendFunctionShape> Functions)
{
    public int FunctionCount => Functions.Count;
    public int AwaitCount => Functions.Sum(f => f.Awaits + f.IntrinsicAwaits);

    public static SuspendShapeAnalysis Combine(IEnumerable<SuspendShapeAnalysis> analyses) =>
        new(analyses.SelectMany(a => a.Functions).ToList());

    public JsonObject ToJson() => new()
    {
        ["suspendFunctions"] = new JsonArray(Functions.Select(f => f.ToJson()).Cast<JsonNode>().ToArray()),
        ["totalSuspendFunctions"] = FunctionCount,
        ["totalAwaits"] = AwaitCount,
    };
}

sealed record SuspendFunctionShape(
    string Owner,
    string Name,
    string ResultType,
    int Awaits,
    int IntrinsicAwaits,
    int Returns,
    int CpsFields)
{
    public JsonObject ToJson() => new()
    {
        ["owner"] = Owner,
        ["name"] = Name,
        ["resultType"] = ResultType,
        ["awaits"] = Awaits,
        ["intrinsicAwaits"] = IntrinsicAwaits,
        ["returns"] = Returns,
        ["cpsFields"] = CpsFields,
    };
}

static class JsonOptions
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}

sealed class UsageException : Exception
{
    public UsageException(string message) : base(message) { }
}
