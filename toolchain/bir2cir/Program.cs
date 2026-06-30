// bir2cir — lower Backend IR (BIR) JSON into CLR IR (CIR) JSON.
//
// bir2cir owns the Kotlin -> CLR type substitution. Its SINGLE, sole transform rewrites the Kotlin type
// vocabulary in the BIR into the CLR-codegen vocabulary ilemit consumes, emitting a BIR-SHAPED CIR (same node
// shape; only type strings change). There is no verbatim-copy / envelope alternative — that dual track is retired.
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
            Console.Error.WriteLine("usage: bir2cir <out-dir> [--ref <dll>]... <file.bir.json>...");
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
            $"bir2cir: lowered {birFiles.Count} BIR file(s) -> {_options.OutDir} ({refs.Count} ref(s), build: {(_options.RefBuild ? "reference" : "substitute/app")}, suspend: {suspend.FunctionCount} fn/{suspend.AwaitCount} await)");
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
            // The single, sole transform: lower the Kotlin type vocabulary into ilemit's CLR-codegen vocabulary,
            // emitting a BIR-SHAPED CIR (same node shape; only type strings change). No verbatim/envelope track.
            var lowered = BirTypeLowering.Lower(bir.Root, _options.RefBuild);
            // REFERENCE build only: squash every declaration body to `throw NotImplementedException()` so the ref
            // assembly is metadata-only. Keeps ALL metadata (signatures/types/supertypes/generics/attrs) intact —
            // only the body STATEMENTS change. This is what makes it safe for a bare-value kotlin.* primitive kept
            // verbatim in the ref to appear in a signature without any real body ever emitting arithmetic/box/conv IL.
            if (_options.RefBuild) RefBodySquash.Squash(lowered);
            files.Add(new CirFile(outputName, lowered.ToJsonString(JsonOptions.Indented)));
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

sealed record DriverOptions(string OutDir, IReadOnlyList<string> References, IReadOnlyList<string> Inputs)
{
    // The lowering mode is a property of the BUILD, not a CLI flag. The pure-Kotlin REFERENCE stdlib surface
    // (DOTKT_STDLIB_COMPILE set AND DOTKT_STDLIB_SUBSTITUTE unset) keeps kotlin.* type tokens verbatim; EVERY
    // other invocation — the runtime stdlib build and all app builds — lowers kotlin.* to the CLR vocabulary.
    // The build scripts export these env vars. There is no --compat-bir/--native-cir output selection any more.
    public bool RefBuild =>
        Environment.GetEnvironmentVariable("DOTKT_STDLIB_COMPILE") != null &&
        Environment.GetEnvironmentVariable("DOTKT_STDLIB_SUBSTITUTE") == null;

    public static DriverOptions Parse(string[] args)
    {
        if (args.Length < 2)
            throw new UsageException("bir2cir: missing output directory or input files");

        var outDir = args[0];
        var refs = new List<string>();
        var inputs = new List<string>();

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ref" when i + 1 < args.Length:
                    refs.Add(Path.GetFullPath(args[++i]));
                    break;
                case "--ref":
                    throw new UsageException("bir2cir: --ref requires a DLL path");
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                        throw new UsageException($"bir2cir: unknown option '{args[i]}'");
                    inputs.Add(args[i]);
                    break;
            }
        }

        if (inputs.Count == 0)
            throw new UsageException("bir2cir: no BIR input files");

        return new DriverOptions(outDir, refs, inputs);
    }
}

sealed record BirFile(string Path, string Json, JsonNode Root, SuspendShapeAnalysis Suspend, CallSiteAnalysis Calls, TypeSiteAnalysis Types);
sealed record CirFile(string OutputName, string Json);

sealed class ReferenceMetadataIndex
{
    const string KotlinFileClassAttr = "DotKt.Runtime.CompilerServices.KotlinFileClassAttribute";
    const string KotlinFunctionAttr = "DotKt.Runtime.CompilerServices.KotlinFunctionAttribute";
    const string KotlinInlineAttr = "DotKt.Runtime.CompilerServices.KotlinInlineAttribute";

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

    public IReadOnlyList<ResolutionCandidate> ResolveClrProperty(string owner, string name, bool setter, bool isStatic)
    {
        var matches = new List<ResolutionCandidate>();
        var accessorName = (setter ? "set_" : "get_") + name;
        foreach (var asm in _assemblies)
        {
            foreach (var member in asm.DotKt.Members)
            {
                if (!OwnerMatches(owner, member.Owner)) continue;
                if (member.IsStatic != isStatic) continue;
                if (member.Kind == "method" &&
                    member.Name == accessorName &&
                    member.ParameterTypes.Count == (setter ? 1 : 0))
                {
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
        }
        if (matches.Count > 0) return matches;

        foreach (var asm in _assemblies)
        {
            foreach (var member in asm.DotKt.Members)
            {
                if (member.Kind != "field") continue;
                if (!OwnerMatches(owner, member.Owner)) continue;
                if (member.IsStatic != isStatic) continue;
                if (member.Name != name) continue;
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

    public IReadOnlyList<ResolutionCandidate> ResolveClrEvent(string owner, string name, bool add, bool isStatic)
    {
        var accessorName = (add ? "add_" : "remove_") + name;
        var matches = new List<ResolutionCandidate>();
        foreach (var asm in _assemblies)
        {
            foreach (var member in asm.DotKt.Members)
            {
                if (member.Kind != "method") continue;
                if (!OwnerMatches(owner, member.Owner)) continue;
                if (member.IsStatic != isStatic) continue;
                if (member.Name != accessorName) continue;
                if (member.ParameterTypes.Count != 1) continue;
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

    static bool MemberKindMatches(string siteKind, string memberKind) => siteKind switch
    {
        "new" => memberKind == "constructor",
        "field" or "staticField" or "setFieldExpr" or "staticFieldSet" => memberKind == "field",
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

    static bool TypeMatches(string siteType, string memberType)
    {
        var normalizedMember = NormalizeType(memberType);
        if (normalizedMember.StartsWith("gp:", StringComparison.Ordinal))
            return true;
        return NormalizeType(siteType) == normalizedMember;
    }

    static string NormalizeType(string type)
    {
        var value = type.Trim();
        if (value.StartsWith("@", StringComparison.Ordinal)) return NormalizeType(value[1..]);
        if (value.StartsWith("clr:", StringComparison.Ordinal)) return NormalizeType(value["clr:".Length..]);
        if (value.StartsWith("byref:", StringComparison.Ordinal)) return "byref:" + NormalizeType(value["byref:".Length..]);
        if (value.StartsWith("array:", StringComparison.Ordinal)) return "array:" + NormalizeType(value["array:".Length..]);
        if (value.StartsWith("nullable:", StringComparison.Ordinal)) return "nullable:" + NormalizeType(value["nullable:".Length..]);
        if (value.StartsWith("gp:", StringComparison.Ordinal)) return value;
        if (value.StartsWith("func:", StringComparison.Ordinal))
            return NormalizeFuncType(value["func:".Length..]);
        if (value.StartsWith("clrg:", StringComparison.Ordinal))
            return NormalizeGenericType(value["clrg:".Length..]);
        return value switch
        {
            // CLR-codegen shorthand (bir2cir's OUTPUT vocabulary, also what kotc still emits today). KEEP these.
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
            // The pure-Kotlin INPUT vocabulary (the symbols kotc emits once switched, and the FullName of the
            // emitted reference primitive classes — "kotlin.Int" etc.). Converge onto the same BCL token so
            // TypeMatches sees ONE vocabulary regardless of whether a site speaks shorthand or kotlin.*.
            "kotlin.Boolean" => "System.Boolean",
            "kotlin.Byte" => "System.Byte",
            "kotlin.Char" => "System.Char",
            "kotlin.Double" => "System.Double",
            "kotlin.Float" => "System.Single",
            "kotlin.Int" => "System.Int32",
            "kotlin.Long" => "System.Int64",
            "kotlin.Any" => "System.Object",
            "kotlin.Nothing" => "System.Object",
            "kotlin.Short" => "System.Int16",
            "kotlin.String" => "System.String",
            "kotlin.UByte" => "System.Byte",
            "kotlin.UInt" => "System.UInt32",
            "kotlin.ULong" => "System.UInt64",
            "kotlin.UShort" => "System.UInt16",
            "kotlin.Unit" => "System.Void",
            _ => StripGenericArity(value),
        };
    }

    static string NormalizeGenericType(string spec)
    {
        var br = spec.IndexOf('[');
        if (br < 0) return "clrg:" + StripGenericArity(spec);

        var open = StripGenericArity(spec[..br]);
        var inner = spec[(br + 1)..^1];
        return "clrg:" + open + "[" + string.Join(",", SplitTopLevel(inner).Select(NormalizeType)) + "]";
    }

    static string NormalizeFuncType(string spec)
    {
        var colon = FuncRetEnd(spec);
        var ret = spec[..colon];
        var args = colon + 1 < spec.Length ? spec[(colon + 1)..] : "";
        return "func:" + NormalizeType(ret) + ":" + string.Join(",", SplitTopLevel(args).Select(NormalizeType));
    }

    static int FuncRetEnd(string value)
    {
        var start = PrefixLength(value);
        var depth = 0;
        for (var i = start; i < value.Length; i++)
        {
            if (value[i] == '[') depth++;
            else if (value[i] == ']') depth--;
            else if (value[i] == ':' && depth == 0) return i;
        }

        return value.Length;
    }

    static int PrefixLength(string value)
    {
        foreach (var prefix in new[] { "clrg:", "clr:", "array:", "nullable:", "func:", "gp:", "byref:" })
            if (value.StartsWith(prefix, StringComparison.Ordinal))
                return prefix.Length;
        return 0;
    }

    static IReadOnlyList<string> SplitTopLevel(string value)
    {
        if (value.Length == 0) return Array.Empty<string>();

        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '[') depth++;
            else if (value[i] == ']') depth--;
            else if (value[i] == ',' && depth == 0)
            {
                result.Add(value[start..i].Trim());
                start = i + 1;
            }
        }

        result.Add(value[start..].Trim());
        return result;
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

            foreach (var type in SafeTypes(asm, metadata))
            {
                if (HasAttribute(type.GetCustomAttributesData(), KotlinFileClassAttr))
                {
                    metadata.FileClasses.Add(type.FullName ?? type.Name);
                }

                var isFileClass = HasAttribute(type.GetCustomAttributesData(), KotlinFileClassAttr);
                metadata.Types.Add(new DotKtTypeMetadata(
                    TypeName(type),
                    TypeKind(type),
                    isFileClass));

                foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    metadata.Members.Add(new DotKtMemberMetadata(
                        "constructor",
                        TypeName(type),
                        ".ctor",
                        false,
                        isFileClass,
                        ctor.GetParameters().Select(p => TypeName(p.ParameterType)).ToList(),
                        TypeName(type)));
                }

                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    metadata.Members.Add(new DotKtMemberMetadata(
                        "field",
                        TypeName(type),
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
                            TypeName(type),
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
                            TypeName(type),
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

    static string TypeName(Type type)
    {
        if (type.IsByRef)
            return "byref:" + TypeName(type.GetElementType()!);
        if (type.IsArray)
            return "array:" + TypeName(type.GetElementType()!);
        if (type.IsGenericParameter)
            return "gp:" + type.Name;
        if (IsDelegate(type))
            return DelegateTypeName(type);
        if (type.IsConstructedGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments().Select(TypeName).ToList();
            if (def == typeof(Nullable<>))
                return "nullable:" + args[0];
            if (IsFunc(def))
                return "func:" + args[^1] + ":" + string.Join(",", args.Take(args.Count - 1));
            if (IsAction(def))
                return "func:void:" + string.Join(",", args);
            return "clrg:" + StripGenericArity(def.FullName ?? def.Name) + "[" + string.Join(",", args) + "]";
        }

        return PrimitiveBirName(type) ?? StripGenericArity(type.FullName ?? type.Name);
    }

    static bool IsFunc(Type type) =>
        type.Namespace == "System" && type.Name.StartsWith("Func`", StringComparison.Ordinal);

    static bool IsAction(Type type) =>
        type.Namespace == "System" && type.Name.StartsWith("Action`", StringComparison.Ordinal);

    static bool IsDelegate(Type type)
    {
        for (var cur = type; cur != null; cur = cur.BaseType)
            if (cur.FullName == "System.MulticastDelegate")
                return true;
        return false;
    }

    static string DelegateTypeName(Type type)
    {
        var invoke = type.GetMethod("Invoke");
        if (invoke == null) return PrimitiveBirName(type) ?? StripGenericArity(type.FullName ?? type.Name);
        return "func:" + TypeName(invoke.ReturnType) + ":" + string.Join(",", invoke.GetParameters().Select(p => TypeName(p.ParameterType)));
    }

    static string PrimitiveBirName(Type type)
    {
        if (type == typeof(bool)) return "bool";
        if (type == typeof(byte)) return "byte";
        if (type == typeof(char)) return "char";
        if (type == typeof(double)) return "double";
        if (type == typeof(float)) return "float";
        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "long";
        if (type == typeof(object)) return "object";
        if (type == typeof(short)) return "short";
        if (type == typeof(string)) return "string";
        if (type == typeof(void)) return "void";
        // The REFERENCE stdlib emits the pure-Kotlin primitives as real types whose FullName is literally
        // "kotlin.Int" / "kotlin.String" / ... When such a ref dll is read back, converge those onto the SAME
        // CLR-shorthand token as their BCL twin so a member signature speaks one vocabulary for TypeMatches.
        return PrimitiveBirNameByFullName(type.FullName);
    }

    static string PrimitiveBirNameByFullName(string fullName) => fullName switch
    {
        "kotlin.Boolean" => "bool",
        "kotlin.Byte" => "byte",
        "kotlin.Char" => "char",
        "kotlin.Double" => "double",
        "kotlin.Float" => "float",
        "kotlin.Int" => "int",
        "kotlin.Long" => "long",
        "kotlin.Any" => "object",
        "kotlin.Short" => "short",
        "kotlin.String" => "string",
        "kotlin.UByte" => "ubyte",
        "kotlin.UInt" => "uint",
        "kotlin.ULong" => "ulong",
        "kotlin.UShort" => "ushort",
        "kotlin.Unit" => "void",
        _ => null,
    };

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
        foreach (var prefix in new[] { "clrg:", "clr:" })
            if (value.StartsWith(prefix, StringComparison.Ordinal))
                value = value[prefix.Length..];
        var idx = value.IndexOf('[');
        return StripGenericArity(idx >= 0 ? value[..idx] : value);
    }

    static string StripGenericArity(string value)
    {
        var idx = value.IndexOf('`');
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
    public readonly List<string> FileClasses = new();
    public readonly List<DotKtTypeMetadata> Types = new();
    public readonly List<DotKtMemberMetadata> Members = new();
    public readonly List<KotlinFunctionMetadata> Functions = new();
    public readonly List<string> Diagnostics = new();

    public JsonObject ToJson() => new()
    {
        ["fileClasses"] = new JsonArray(FileClasses.Select(s => JsonValue.Create(s)).Cast<JsonNode>().ToArray()),
        ["types"] = new JsonArray(Types.Select(t => t.ToJson()).Cast<JsonNode>().ToArray()),
        ["members"] = new JsonArray(Members.Select(m => m.ToJson()).Cast<JsonNode>().ToArray()),
        ["functions"] = new JsonArray(Functions.Select(f => f.ToJson()).Cast<JsonNode>().ToArray()),
        ["diagnostics"] = new JsonArray(Diagnostics.Select(s => JsonValue.Create(s)).Cast<JsonNode>().ToArray()),
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

sealed class TypeSiteAnalyzer
{
    // The type-bearing JSON keys whose string (or string[]) values carry a type token. SHARED with
    // BirTypeLowering, which rewrites exactly these. `type` also covers nested params[].type / fields[].type;
    // `interfaces` and `argTypes` are string arrays.
    internal static readonly HashSet<string> TypeProperties = new(StringComparer.Ordinal)
    {
        "type",
        "ownerType",
        "ret",
        "retType",
        "resultType",
        "base",
        "interfaces",
        "argTypes",
    };

    static readonly HashSet<string> PrimitiveTypes = new(StringComparer.Ordinal)
    {
        // CLR-codegen shorthand (bir2cir's output vocabulary).
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
        // The pure-Kotlin input vocabulary — a bare kotlin.* primitive is a recognised primitive (bir2cir lowers
        // it directly), not an unresolved kotlin-symbol that needs a reference lookup.
        "kotlin.Boolean",
        "kotlin.Byte",
        "kotlin.Char",
        "kotlin.Double",
        "kotlin.Float",
        "kotlin.Int",
        "kotlin.Long",
        "kotlin.Any",
        "kotlin.Nothing",
        "kotlin.Short",
        "kotlin.String",
        "kotlin.UByte",
        "kotlin.UInt",
        "kotlin.ULong",
        "kotlin.UShort",
        "kotlin.Unit",
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
        "staticFieldSet",
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
        return "kotlin-symbol";
    }

    static string StringProp(JsonObject obj, string name) => obj[name]?.GetValue<string>();

    static IReadOnlyList<string> ArgumentTypes(JsonObject node, string signature)
    {
        if (!string.IsNullOrWhiteSpace(signature))
            return SplitTopLevel(signature);

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

    static IReadOnlyList<string> SplitTopLevel(string value)
    {
        if (value.Length == 0) return Array.Empty<string>();

        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '[') depth++;
            else if (value[i] == ']') depth--;
            else if (value[i] == ',' && depth == 0)
            {
                result.Add(value[start..i].Trim());
                start = i + 1;
            }
        }

        result.Add(value[start..].Trim());
        return result;
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

// bir2cir's single, sole transform. Rewrites the Kotlin type vocabulary in a BIR-shaped JSON tree into the
// CLR-codegen vocabulary ilemit consumes, producing a BIR-SHAPED CIR (same node shape; only type strings change).
//
// Mode gate (a property of the build, exported as env by the build scripts):
//   refBuild = DOTKT_STDLIB_COMPILE set AND DOTKT_STDLIB_SUBSTITUTE unset  -> the pure-Kotlin REFERENCE surface.
// In the REFERENCE build a kotlin.* primitive token is kept VERBATIM (pure-Kotlin metadata; the bare FQN
// "kotlin.Int" stays "kotlin.Int"); the rewrite is a pure passthrough. In EVERY other build (the runtime stdlib,
// and all app builds) a bare kotlin.* primitive lowers to its CLR token (kotlin.Int -> int, ...).
//
// COMPREHENSIVE WALK — kotc emits a bare `kotlin.*` FQN for every source-type primitive at EVERY position:
// signatures, expression/statement type tokens (call owners, conv targets, generic constraints, array elem
// types, lambda/func types, ...). So the lowering recurses the WHOLE node tree and rewrites every type-bearing
// string (see TypeKeys + the `sig` comma-list + the `attrs`/attribute-class force path), not just the signature
// keys — a primitive left un-lowered in an expression position reaches ilemit as `kotlin.Byte` and fails to
// resolve ("cannot resolve .NET type kotlin.Byte").
static class BirTypeLowering
{
    // The bare kotlin.* PRIMITIVE tokens and their CLR-codegen lowering. Consulted only in the non-reference
    // (substitute/app) build; the reference build keeps every kotlin.* primitive verbatim.
    //
    // DEFERRED from the general map — kotlin.String / kotlin.Any / kotlin.Unit and the unsigned set
    // (kotlin.UInt/ULong/UByte/UShort) are NOT here. In the substitute build kotc has already substituted those
    // @Clr-bound types to System.* at the source level; in the reference build they stay as the stdlib's own
    // emitted value-classes and ilemit resolves them as bare emitted types. They DO appear in KotlinAllToClr,
    // used only on the attribute-metadata force path (a CLR attribute blob needs a concrete System.* type).
    static readonly IReadOnlyDictionary<string, string> KotlinToClr = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["kotlin.Int"] = "int",
        ["kotlin.Long"] = "long",
        ["kotlin.Short"] = "short",
        ["kotlin.Byte"] = "byte",
        ["kotlin.Double"] = "double",
        ["kotlin.Float"] = "float",
        ["kotlin.Boolean"] = "bool",
        ["kotlin.Char"] = "char",
        ["kotlin.Nothing"] = "object",
    };

    // The FULL kotlin.* -> CLR map, used UNCONDITIONALLY (both modes) on the attribute-metadata force path. A
    // custom-attribute's constructor-argument / field / property types are encoded into the assembly's attribute
    // blob, which the CLR custom-attribute encoder accepts ONLY for concrete System.* types — never the emitted
    // pure-Kotlin class. So even in the reference build (where every other kotlin.* primitive is kept verbatim)
    // an attribute-carried type must lower to its real CLR token, including String/Any/Unit and the unsigned set.
    static readonly IReadOnlyDictionary<string, string> KotlinAllToClr = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["kotlin.Int"] = "int",
        ["kotlin.Long"] = "long",
        ["kotlin.Short"] = "short",
        ["kotlin.Byte"] = "byte",
        ["kotlin.Double"] = "double",
        ["kotlin.Float"] = "float",
        ["kotlin.Boolean"] = "bool",
        ["kotlin.Char"] = "char",
        ["kotlin.Nothing"] = "object",
        ["kotlin.String"] = "string",
        ["kotlin.Any"] = "object",
        ["kotlin.Unit"] = "void",
        ["kotlin.UInt"] = "uint",
        ["kotlin.ULong"] = "ulong",
        ["kotlin.UByte"] = "ubyte",
        ["kotlin.UShort"] = "ushort",
    };

    // Every JSON key whose string (or string[]) value is a TYPE reference, across signatures, expressions and
    // statements. SHARED-IN-SPIRIT with TypeSiteAnalyzer.TypeProperties but a strict superset: the analyzer only
    // ever needed the signature keys, whereas lowering must catch a primitive WHEREVER it sits. Identity/data keys
    // that may carry a kotlin.*-looking string but are NOT types (name/value/var/method/id/kind/...) are
    // deliberately excluded — lowering them would corrupt a declaration name or a string literal. `sig` (a
    // comma-joined type list) and `attrs` (attribute applications) get their own handling below.
    static readonly HashSet<string> TypeKeys = new(StringComparer.Ordinal)
    {
        // signature positions (the original TypeProperties set)
        "type", "ownerType", "ret", "retType", "resultType", "base", "interfaces", "argTypes",
        // expression / statement type positions
        "dynRet", "funcType", "typeArgs", "constraints", "recvType", "iface", "excType",
        "keyType", "valType", "iterType", "accessOwner", "elem", "to", "owner",
        "samType", "closureType",
        // additional type-reference keys ilemit reads (absent in today's BIR but lowered for robustness)
        "elemType", "accType", "clrType", "tupleType", "selRet", "parameterTypes", "returnType",
    };

    // The kotlin.* VALUE-CLASS types whose representation IS a concrete BCL type: kotlin.String == System.String,
    // kotlin.Any == System.Object. Unlike the 9 primitives, kotc still emits them as bare kotlin.* references to the
    // stdlib's own emitted classes (which carry Kotlin-only members — CharSequence.subSequence, String.plus — that
    // do NOT exist on the BCL type). So they get a SPLIT representation in the non-reference build:
    //   - VALUE positions (ret / params / fields / type args / ...) lower to the BCL FQN, so a member that OVERRIDES
    //     System.Object (ToString : System.String, Equals(System.Object)) / implements IComparable<System.String> has
    //     a signature the CLR accepts -- otherwise the emitted class fails to load ("Signature of the body and
    //     declaration in a method implementation do not match").
    //   - OWNER positions (ownerType / recvType / ...) keep kotlin.* so a Kotlin-only member still resolves on the
    //     emitted class (FindMethod would not find subSequence/plus on System.String).
    // The FQN form (not the `string`/`object` shorthand) is used so a kept-kotlin.* owner that DID lower a nested
    // type arg, and any value site, resolves through ilemit's reflection (ResolveType) as well as MapType.
    static readonly IReadOnlyDictionary<string, string> KotlinRefToClr = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["kotlin.String"] = "System.String",
        ["kotlin.Any"] = "System.Object",
    };

    // The OWNER/receiver type-reference keys: the type here is the method-call/field owner (or constrained-call
    // receiver), which must keep its emitted-class identity (KotlinRefToClr is NOT applied at the top leaf). Nested
    // type args inside a generic owner are still value positions and DO lower (the recursion re-enables it).
    static readonly HashSet<string> OwnerKeys = new(StringComparer.Ordinal)
    {
        "ownerType", "owner", "recvType", "accessOwner",
    };

    static readonly string[] ModifierPrefixes = { "byref:", "array:", "nullable:" };

    public static JsonNode Lower(JsonNode root, bool refBuild) => LowerNode(root, refBuild, force: false);

    // `force` == "this subtree carries attribute-blob metadata": lower with the FULL map, ignoring refBuild. It is
    // set when entering an attribute-class declaration (base : System.Attribute) or an `attrs` application array,
    // and propagates to the whole subtree.
    static JsonNode LowerNode(JsonNode node, bool refBuild, bool force)
    {
        if (node is JsonObject obj)
        {
            var here = force || IsAttributeClass(obj);
            var copy = new JsonObject();
            foreach (var kv in obj)
            {
                if (kv.Value == null) { copy[kv.Key] = null; continue; }
                if (kv.Key == "attrs")
                    copy[kv.Key] = LowerNode(kv.Value, refBuild, force: true);   // attribute application -> blob metadata
                else if (kv.Key == "sig")
                    copy[kv.Key] = LowerSigValue(kv.Value, refBuild, here, lowerRef: true);   // sig = param types (values)
                else if (TypeKeys.Contains(kv.Key))
                    copy[kv.Key] = LowerTypeValued(kv.Value, refBuild, here, lowerRef: !OwnerKeys.Contains(kv.Key));
                else
                    copy[kv.Key] = LowerNode(kv.Value, refBuild, here);
            }
            return copy;
        }

        if (node is JsonArray arr)
        {
            var copy = new JsonArray();
            foreach (var item in arr)
                copy.Add(item == null ? null : LowerNode(item, refBuild, force));
            return copy;
        }

        return node.DeepClone();
    }

    // A type declaration is an attribute class iff it extends System.Attribute (kotc lowers every Kotlin
    // `annotation class` to `: System.Attribute`). Its ctor params / fields / property accessors must carry
    // concrete CLR types so the attribute is emittable — hence the force path.
    static bool IsAttributeClass(JsonObject obj) =>
        obj["base"] is JsonValue b && b.TryGetValue<string>(out var s) && s != null &&
        s.EndsWith("System.Attribute", StringComparison.Ordinal);

    // A `sig` value is a top-level comma-joined list of parameter type tokens (the overload key ilemit matches by
    // STRING EQUALITY against a method def's lowered `params[].type`). Lower each element so the call-side sig and
    // the def-side params stay in the SAME vocabulary, else overload resolution misses.
    static JsonNode LowerSigValue(JsonNode val, bool refBuild, bool force, bool lowerRef)
    {
        if (val is JsonValue scalar && scalar.TryGetValue<string>(out var s))
            return JsonValue.Create(string.Join(",", SplitTopLevel(s).Select(p => LowerTypeString(p, refBuild, force, lowerRef))));
        return LowerNode(val, refBuild, force);
    }

    // A type-bearing key's value: a scalar type string, an array of type strings (interfaces/argTypes/constraints/
    // typeArgs), or — for a few node shapes — a nested object, which is recursed structurally. `lowerRef` = whether
    // the KotlinRefToClr (String/Any -> BCL) map applies at the TOP leaf; false at owner/receiver keys.
    static JsonNode LowerTypeValued(JsonNode val, bool refBuild, bool force, bool lowerRef)
    {
        if (val is JsonValue scalar && scalar.TryGetValue<string>(out var s))
            return JsonValue.Create(LowerTypeString(s, refBuild, force, lowerRef));

        if (val is JsonArray arr)
        {
            var copy = new JsonArray();
            foreach (var item in arr)
            {
                if (item is JsonValue iv && iv.TryGetValue<string>(out var its))
                    copy.Add(JsonValue.Create(LowerTypeString(its, refBuild, force, lowerRef)));
                else
                    copy.Add(item == null ? null : LowerNode(item, refBuild, force));
            }
            return copy;
        }

        return LowerNode(val, refBuild, force);
    }

    // Recurse the BIR type grammar, rewriting only bare kotlin.* tokens in the active map. Every other shape
    // (gp:, clr:, clrg:[...], @Name[...], func:ret:args, array:/byref:/nullable: modifiers, the CLR shorthand,
    // and user/stdlib FQNs like kotlin.collections.List) is structurally preserved; nested type arguments are
    // recursed so a kotlin.* primitive inside a generic lowers too.
    public static string LowerTypeString(string raw, bool refBuild, bool force = false, bool lowerRef = true)
    {
        // The reference build keeps kotlin.* primitives verbatim (general path); the attribute force path lowers
        // unconditionally. A token with no "kotlin." substring can never contain a mappable token, so skip it.
        if ((!force && refBuild) || !raw.Contains("kotlin.", StringComparison.Ordinal)) return raw;

        var t = raw.Trim();
        if (t.Length == 0) return raw;

        foreach (var p in ModifierPrefixes)
            if (t.StartsWith(p, StringComparison.Ordinal))
                return p + LowerTypeString(t[p.Length..], refBuild, force, lowerRef);

        if (t.StartsWith("gp:", StringComparison.Ordinal)) return t;
        if (t.StartsWith("clr:", StringComparison.Ordinal)) return t;
        if (t.StartsWith("func:", StringComparison.Ordinal)) return LowerFuncString(t, refBuild, force);

        var br = t.IndexOf('[');
        if (br >= 0 && t.EndsWith("]", StringComparison.Ordinal))
        {
            // Nested type ARGS are value positions (re-enable the ref map even under an owner-key head).
            var head = t[..br];
            var inner = t[(br + 1)..^1];
            var args = string.Join(",", SplitTopLevel(inner).Select(a => LowerTypeString(a, refBuild, force, lowerRef: true)));
            return head + "[" + args + "]";
        }

        return LowerLeaf(t, force, lowerRef);
    }

    static string LowerFuncString(string t, bool refBuild, bool force)
    {
        // func:<ret>:<arg,arg,...>  — the ret/args separator is the first top-level ':' AFTER the ret's own
        // leading type prefix (so a ret like clrg:Foo[int] is not split on its clrg: colon).
        var body = t["func:".Length..];
        var sep = FuncRetEnd(body);
        var ret = sep >= body.Length ? body : body[..sep];
        var args = sep >= body.Length ? "" : body[(sep + 1)..];
        // func: ret/args are value positions (a delegate's signature), so the ref map applies to them.
        var loweredArgs = string.Join(",", SplitTopLevel(args).Select(a => LowerTypeString(a, refBuild, force, lowerRef: true)));
        return "func:" + LowerTypeString(ret, refBuild, force, lowerRef: true) + ":" + loweredArgs;
    }

    static string LowerLeaf(string t, bool force, bool lowerRef)
    {
        // @-decorated and clrg: references are emitted/CLR type references whose head is never a bare primitive
        // (any bracket args were recursed above) — keep verbatim. A bare kotlin.* leaf lowers via the active map;
        // all other leaves (CLR shorthand, user/stdlib FQNs) pass through.
        if (t.StartsWith("@", StringComparison.Ordinal)) return t;
        if (t.StartsWith("clrg:", StringComparison.Ordinal)) return t;
        if (force) return KotlinAllToClr.TryGetValue(t, out var all) ? all : t;
        // The 9 primitives lower at EVERY position (they never appear as an owner). String/Any lower only at
        // VALUE positions (lowerRef) -- an owner/receiver keeps the emitted-class identity for member resolution.
        if (KotlinToClr.TryGetValue(t, out var prim)) return prim;
        if (lowerRef && KotlinRefToClr.TryGetValue(t, out var rf)) return rf;
        return t;
    }

    // First top-level ':' after the leading type prefix; bracket depth aware. Mirrors the matcher's FuncRetEnd.
    static int FuncRetEnd(string value)
    {
        var start = PrefixLength(value);
        var depth = 0;
        for (var i = start; i < value.Length; i++)
        {
            if (value[i] == '[') depth++;
            else if (value[i] == ']') depth--;
            else if (value[i] == ':' && depth == 0) return i;
        }
        return value.Length;
    }

    static int PrefixLength(string value)
    {
        foreach (var prefix in new[] { "clrg:", "clr:", "array:", "nullable:", "func:", "gp:", "byref:" })
            if (value.StartsWith(prefix, StringComparison.Ordinal))
                return prefix.Length;
        return 0;
    }

    static IReadOnlyList<string> SplitTopLevel(string value)
    {
        if (value.Length == 0) return Array.Empty<string>();

        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '[') depth++;
            else if (value[i] == ']') depth--;
            else if (value[i] == ',' && depth == 0)
            {
                result.Add(value[start..i].Trim());
                start = i + 1;
            }
        }

        result.Add(value[start..].Trim());
        return result;
    }
}

// REFERENCE-build body squashing. The pure-Kotlin reference stdlib (DotKt.Private.Stdlib.dll) is a METADATA-ONLY
// surface: every declaration keeps its full signature/type/supertype/generic/attribute metadata, but its BODY is
// replaced with a single `throw NotImplementedException()` statement. The ref dll is never executed (it is loaded
// compile-time only and substituted away at app-emit), so a thrown stub is the correct, minimal body.
//
// WHY this is a prerequisite for kotc emitting bare `kotlin.Int`: in the reference build bir2cir keeps `kotlin.*`
// primitive tokens VERBATIM (they are not lowered to the CLR primitive). If a real method body were emitted, IL
// operating on such a bare-value `kotlin.Int` (arithmetic / box / conv) would have no valid CLR primitive to act
// on. Squashing every body to a throw guarantees no such IL is ever produced — the signature carries `kotlin.Int`
// purely as metadata.
//
// Mutates the (already deep-cloned) lowered tree in place. Only the declaration hierarchy that ilemit emits as IL
// bodies is touched: file-level methods, and per-type methods + constructors, recursively through nested types.
// Property accessors are already lowered to `get_X`/`set_X` methods, so they are covered by the method pass.
static class RefBodySquash
{
    public static void Squash(JsonNode root)
    {
        if (root is not JsonObject file) return;
        SquashMethods(file["methods"] as JsonArray);
        SquashTypes(file["types"] as JsonArray);
    }

    static void SquashTypes(JsonArray types)
    {
        if (types == null) return;
        foreach (var t in types)
        {
            if (t is not JsonObject type) continue;
            SquashMethods(type["methods"] as JsonArray);
            SquashCtors(type["ctors"] as JsonArray);
            SquashTypes(type["types"] as JsonArray);   // nested types (local/object/companion)
        }
    }

    static void SquashMethods(JsonArray methods)
    {
        if (methods == null) return;
        foreach (var m in methods)
        {
            if (m is not JsonObject method) continue;
            // Abstract/interface members have NO IL body — ilemit refuses a body for them; adding one would be
            // emitted-as-nothing at best and is semantically wrong. A suspend member carries `steps`/`cpsFields`
            // and NO `body` (ilemit emits its own throwing stub under stdlib-compile); leave it untouched. We only
            // squash a member that actually carries a `body` statement array.
            if (IsAbstract(method)) continue;
            if (method["body"] is JsonArray) method["body"] = ThrowStubBody();
        }
    }

    static void SquashCtors(JsonArray ctors)
    {
        if (ctors == null) return;
        foreach (var c in ctors)
        {
            if (c is not JsonObject ctor) continue;
            // Squash ONLY the body. Keep `baseArgs`/`thisArgs`: ilemit always emits the base/this constructor call
            // from that metadata before the body, and a base without a default constructor would make a nulled-out
            // base call un-resolvable. The chain-up is the minimal structurally-required prologue; the body throws.
            if (ctor["body"] is JsonArray) ctor["body"] = ThrowStubBody();
        }
    }

    static bool IsAbstract(JsonObject method) =>
        method["abstract"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    // A one-statement body: `throw new System.NotImplementedException()`. Mirrors the existing throw-statement
    // shape ilemit already consumes (see the stdlib's NotSupportedException intrinsic stubs); the same shape kotc
    // emits for `kotlin.TODO()`, only as a statement rather than an expression.
    static JsonArray ThrowStubBody() => new()
    {
        new JsonObject
        {
            ["k"] = "throw",
            ["value"] = new JsonObject
            {
                ["k"] = "clrNew",
                ["type"] = "System.NotImplementedException",
                ["argTypes"] = new JsonArray(),
                ["args"] = new JsonArray(),
            },
        },
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
