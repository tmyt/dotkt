// bir2cir — lower Backend IR (BIR) JSON into CLR IR (CIR) JSON.
//
// bir2cir owns the Kotlin -> CLR type substitution. Its SINGLE, sole transform rewrites the Kotlin type
// vocabulary in the BIR into the CLR-codegen vocabulary ilemit consumes, emitting a BIR-SHAPED CIR (same node
// shape; only type strings change). There is no verbatim-copy / envelope alternative — that dual track is retired.
using System.Reflection;
using System.Runtime.InteropServices;
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
            // CALL substitution (substitute/app builds only): a member call / construction whose OWNER is a CLR-bound
            // type in the ref.dll (@ClrTypeAlias, or the legacy class-level @ClrIntrinsic) is rewritten to a plain BCL
            // call/new. This is the bir2cir home of what kotc's clrName() member routing used to do — sourced from the
            // ref.dll's @ClrIntrinsic labels, NOT from kotc. Runs BEFORE type lowering so it sees the kotlin.* owners.
            var substituted = _options.RefBuild ? bir.Root : MemberCallSubstitution.Apply(bir.Root, refs);
            // The type transform: lower the Kotlin type vocabulary into ilemit's CLR-codegen vocabulary, emitting a
            // BIR-SHAPED CIR (same node shape; only type strings change). No verbatim/envelope track. The ref.dll
            // @ClrTypeAlias index lowers EVERY CLR-bound type (collections/StringBuilder/Regex/... not just the
            // hardcoded primitives) wherever it appears as a type token.
            var lowered = BirTypeLowering.Lower(substituted, _options.RefBuild, refs.Aliases);
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

    // Aggregate CALL-SUBSTITUTION index across all reference assemblies.
    readonly Dictionary<string, string> _ownerAlias = new(StringComparer.Ordinal);   // Kotlin FQN -> BCL alias
    readonly Dictionary<string, string> _ownerKind = new(StringComparer.Ordinal);    // Kotlin FQN -> class/struct/...
    readonly HashSet<string> _helperTypes = new(StringComparer.Ordinal);             // emitted "<>dotkt_ClrH_*"
    readonly Dictionary<string, List<MemberBinding>> _membersByOwner = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _topLevelIntrinsics = new(StringComparer.Ordinal); // top-level fun name -> FQ static

    // Foundational REFERENCE-type aliases known to bir2cir directly (the same principle as the foundational
    // kotlin.* -> CLR type map already hardcoded in this file). Listed here so member-call / construction
    // substitution works even before kotc preserves the class @ClrTypeAlias attribute on the ref.dll. Only the
    // reference primitives (Any/String) — value primitives keep their identity and are handled by type lowering.
    static readonly IReadOnlyDictionary<string, string> FoundationalRefAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["kotlin.Any"] = "System.Object",
        ["kotlin.String"] = "System.String",
        ["kotlin.Nothing"] = "System.Object",
    };

    ReferenceMetadataIndex(List<ReferenceAssembly> assemblies)
    {
        _assemblies = assemblies;
        foreach (var asm in assemblies)
        {
            foreach (var kv in asm.DotKt.Aliases) _ownerAlias[kv.Key] = kv.Value;
            foreach (var kv in asm.DotKt.TypeKinds) _ownerKind[kv.Key] = kv.Value;
            foreach (var h in asm.DotKt.HelperTypes) _helperTypes.Add(h);
            foreach (var m in asm.DotKt.MemberBindings)
            {
                if (!_membersByOwner.TryGetValue(m.Owner, out var list))
                    _membersByOwner[m.Owner] = list = new List<MemberBinding>();
                list.Add(m);
            }
            foreach (var kv in asm.DotKt.TopLevelIntrinsics) _topLevelIntrinsics.TryAdd(kv.Key, kv.Value);
        }
    }

    public int Count => _assemblies.Count;
    public IReadOnlyList<ReferenceAssembly> Assemblies => _assemblies;

    // The ref.dll @ClrTypeAlias index (Kotlin FQN -> BCL), the SINGLE source of truth shared by both the member-call
    // substitution (owner identity) and the TYPE-TOKEN lowering (supertypes/interfaces/type-args/fields). Keyed on the
    // stripped FQN (no generic-arity backtick), matching a BIR type token's bare owner.
    public IReadOnlyDictionary<string, string> Aliases => _ownerAlias;

    // ---- Call-substitution lookups (consumed by MemberCallSubstitution) ----

    // A BIR owner token ("@kotlin.text.StringBuilder", "kotlin.collections.ArrayList[gp:E]", "clr:System.X") ->
    // its bare Kotlin FQN ("kotlin.text.StringBuilder"). Strips decoration, the clr:/clrg: marker, and type args.
    public static string BareOwnerFqn(string token)
    {
        var t = token.Trim().TrimStart('@');
        foreach (var p in new[] { "clrg:", "clr:" })
            if (t.StartsWith(p, StringComparison.Ordinal)) t = t[p.Length..];
        var br = t.IndexOf('[');
        if (br >= 0) t = t[..br];
        return StripGenericArity(t);
    }

    // Resolve a member-call/construction OWNER to its BCL type. True for a @ClrTypeAlias / class-@ClrIntrinsic owner
    // (or a foundational reference primitive). `kind` is the ref.dll type kind (class/struct/interface/enum).
    public bool TryResolveClrOwner(string ownerToken, out string bcl, out string kind)
    {
        var fqn = BareOwnerFqn(ownerToken);
        if (FoundationalRefAliases.TryGetValue(fqn, out bcl)) { kind = "class"; return true; }
        if (_ownerAlias.TryGetValue(fqn, out bcl)) { kind = _ownerKind.GetValueOrDefault(fqn, "class"); return true; }
        bcl = null; kind = null; return false;
    }

    // The @ClrIntrinsic BCL member name for owner.member (overload-disambiguated by arg count when possible).
    public bool TryMemberIntrinsic(string ownerFqn, string memberName, int argCount, out string intrinsic)
    {
        intrinsic = null;
        if (!_membersByOwner.TryGetValue(ownerFqn, out var list)) return false;
        var cands = list.Where(m => m.Name == memberName && m.Intrinsic != null).ToList();
        if (cands.Count == 0) return false;
        intrinsic = (cands.FirstOrDefault(m => m.ParamCount == argCount) ?? cands[0]).Intrinsic;
        return true;
    }

    // A top-level fun (file-class static, called as `callStatic owner=null`) bound by @ClrIntrinsic to a
    // fully-qualified BCL static (e.g. clrTimestamp -> "System.Diagnostics.Stopwatch.GetTimestamp").
    public bool TryTopLevelIntrinsic(string funName, out string fqStatic) =>
        _topLevelIntrinsics.TryGetValue(funName, out fqStatic);

    // A rule-3 hoist candidate: owner.member exists, is concrete (non-abstract) and carries NO @ClrIntrinsic, so its
    // real Kotlin body was hoisted by kotc to the static helper `<>dotkt_ClrH_<owner>`.
    public bool IsRule3Member(string ownerFqn, string memberName) =>
        _membersByOwner.TryGetValue(ownerFqn, out var list) &&
        list.Any(m => m.Name == memberName && m.Intrinsic == null && !m.IsAbstract);

    public static string HelperTypeName(string ownerFqn) =>
        "<>dotkt_ClrH_" + System.Text.RegularExpressions.Regex.Replace(ownerFqn, "[^A-Za-z0-9]", "_");

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

        // CALL-SUBSTITUTION index. Read via MetadataLoadContext (a metadata-only reflection read) — the runtime
        // Assembly.LoadFrom above throws TypeLoadException on the metadata-only ref stdlib (throw-stub bodies +
        // kotlin.* signatures) and aborts early, so the @ClrTypeAlias/@ClrIntrinsic labels never load through it.
        ScanSubstitutionMetadata(reference, metadata);

        return metadata;
    }

    // Populate the substitution index (Aliases / TypeKinds / HelperTypes / MemberBindings) from the ref.dll using a
    // MetadataLoadContext so the metadata-only assembly reads cleanly. Per-type try/catch: one malformed type is
    // skipped, never aborting the whole scan (the failure mode that left Assembly.LoadFrom's index empty).
    static void ScanSubstitutionMetadata(string reference, ReferenceDotKtMetadata metadata)
    {
        try
        {
            var full = Path.GetFullPath(reference);
            var paths = new List<string>(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));
            var dir = Path.GetDirectoryName(full);
            if (dir != null) paths.AddRange(Directory.GetFiles(dir, "*.dll"));
            paths.Add(full);
            using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths.Distinct(StringComparer.Ordinal)));
            var asm = mlc.LoadFromAssemblyPath(full);

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).Cast<Type>().ToArray(); }

            foreach (var type in types)
            {
                try
                {
                    // Index by the REAL Kotlin FQN (kotc emits "kotlin.String" etc. as the type name) so a BIR
                    // member-call owner token matches. A CLR-bound owner carries @ClrTypeAlias (the type-identity
                    // binding) or, for any not-yet-renamed bound class, a class-level @ClrIntrinsic.
                    var ownerFqn = StripGenericArity(type.FullName ?? type.Name);
                    metadata.TypeKinds[ownerFqn] = TypeKind(type);
                    var classAlias = ClrAliasOf(type.GetCustomAttributesData());
                    if (classAlias != null) metadata.Aliases[ownerFqn] = classAlias;
                    if (ownerFqn.StartsWith("<>dotkt_ClrH_", StringComparison.Ordinal)) metadata.HelperTypes.Add(ownerFqn);
                    var isFileClass = HasAttribute(type.GetCustomAttributesData(), KotlinFileClassAttr);

                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        var intrinsic = ClrIntrinsicOf(method.GetCustomAttributesData());
                        metadata.MemberBindings.Add(new MemberBinding(
                            ownerFqn,
                            method.Name,
                            method.GetParameters().Length,
                            intrinsic,
                            method.IsAbstract,
                            method.IsStatic));
                        // A top-level fun (file-class static) with @ClrIntrinsic naming a fully-qualified BCL static.
                        if (isFileClass && method.IsStatic && intrinsic != null && intrinsic.Contains('.'))
                            metadata.TopLevelIntrinsics.TryAdd(method.Name, intrinsic);
                    }
                }
                catch (Exception ex)
                {
                    metadata.Diagnostics.Add($"subst scan skip {type?.FullName}: {ex.GetType().Name}");
                }
            }
        }
        catch (Exception ex)
        {
            metadata.Diagnostics.Add($"{Path.GetFileName(reference)}: subst scan failed: {ex.GetType().Name}: {ex.Message}");
        }
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

    // The class-level CLR binding: @ClrTypeAlias (the type-identity binding); a class-level @ClrIntrinsic is also
    // accepted for any not-yet-renamed bound class. Returns the single ctor-arg (the .NET FQN), or null if not CLR-bound.
    static string ClrAliasOf(IList<CustomAttributeData> attrs)
    {
        var a = attrs.FirstOrDefault(x => x.AttributeType.FullName is "kotlin.clr.ClrTypeAlias" or "kotlin.clr.ClrIntrinsic");
        return a != null && a.ConstructorArguments.Count > 0 ? a.ConstructorArguments[0].Value as string : null;
    }

    // The member-level CLR binding: @ClrIntrinsic("Name") (or AsDynamic). Returns the BCL member name (the call is
    // rewritten to owner.Name), or null when the member carries no intrinsic (a rule-3 candidate).
    static string ClrIntrinsicOf(IList<CustomAttributeData> attrs)
    {
        var a = attrs.FirstOrDefault(x => x.AttributeType.FullName is "kotlin.clr.ClrIntrinsic" or "kotlin.clr.ClrIntrinsicAsDynamic");
        return a != null && a.ConstructorArguments.Count > 0 ? a.ConstructorArguments[0].Value as string : null;
    }

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

    // CALL-SUBSTITUTION metadata (sourced from the ref.dll, consumed by MemberCallSubstitution; NOT serialized).
    // ownerFqn (the Kotlin FQN, e.g. "kotlin.String") -> the BCL alias it binds to ("System.String"), from a
    // class-level @ClrTypeAlias (the type-identity binding) or, for a not-yet-renamed bound class, a class-level @ClrIntrinsic.
    public readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> TypeKinds = new(StringComparer.Ordinal);   // ownerFqn -> class/struct/interface/enum
    public readonly HashSet<string> HelperTypes = new(StringComparer.Ordinal);            // emitted "<>dotkt_ClrH_*" rule-3 helpers
    public readonly List<MemberBinding> MemberBindings = new();                           // per-member @ClrIntrinsic + shape
    // Top-level fun name -> its @ClrIntrinsic fully-qualified static target ("System.Diagnostics.Stopwatch.GetTimestamp").
    // A top-level fun is a static method of a [KotlinFileClass] type; its call site is `callStatic owner=null`.
    public readonly Dictionary<string, string> TopLevelIntrinsics = new(StringComparer.Ordinal);

    public JsonObject ToJson() => new()
    {
        ["fileClasses"] = new JsonArray(FileClasses.Select(s => JsonValue.Create(s)).Cast<JsonNode>().ToArray()),
        ["types"] = new JsonArray(Types.Select(t => t.ToJson()).Cast<JsonNode>().ToArray()),
        ["members"] = new JsonArray(Members.Select(m => m.ToJson()).Cast<JsonNode>().ToArray()),
        ["functions"] = new JsonArray(Functions.Select(f => f.ToJson()).Cast<JsonNode>().ToArray()),
        ["diagnostics"] = new JsonArray(Diagnostics.Select(s => JsonValue.Create(s)).Cast<JsonNode>().ToArray()),
    };
}

// A single ref.dll member's call-substitution shape. Owner is the Kotlin FQN ("kotlin.String"); Intrinsic is the
// @ClrIntrinsic BCL name or null (null + !IsAbstract = a rule-3 hoist candidate).
sealed record MemberBinding(string Owner, string Name, int ParamCount, string Intrinsic, bool IsAbstract, bool IsStatic);

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
    // The bare kotlin.* tokens and their CLR-codegen lowering. Consulted only in the non-reference
    // (substitute/app) build; the reference build keeps every kotlin.* token verbatim.
    //
    // kotc emits ONLY the type's FQN identity (kotlin.String / kotlin.Any / kotlin.UInt / ...), never a CLR
    // resolution marker — so EVERY @Clr-bound foundational type lowers HERE, uniformly, exactly like the signed/
    // bool/char primitives: kotlin.String -> string, kotlin.Any -> object, and the unsigned set (note
    // kotlin.UByte is an UNSIGNED byte = System.Byte, token "ubyte", NOT the signed "byte"). The whole set is
    // mode-gated by refBuild (LowerTypeString below): the reference surface keeps kotlin.* verbatim, every other
    // build lowers. kotlin.Unit is the ONE token NOT here: it is position-dependent (return -> void via the
    // ReturnKeys path; a Unit VALUE keeps the emitted Unit type — you cannot have a `void` field), handled
    // separately. KotlinAllToClr (the attribute-blob force map) additionally carries kotlin.Unit -> void and is
    // applied UNCONDITIONALLY because an attribute blob needs a concrete System.* type even in the ref build.
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
        ["kotlin.String"] = "string",
        ["kotlin.Any"] = "object",
        ["kotlin.UInt"] = "uint",
        ["kotlin.ULong"] = "ulong",
        ["kotlin.UByte"] = "ubyte",
        ["kotlin.UShort"] = "ushort",
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

    // The RETURN-slot keys. kotlin.Unit is the ONE position-dependent token: kotc's birType change made it emit
    // bare "kotlin.Unit" everywhere (it was "void" in a return slot before). A Unit RETURN is the Kotlin "no value"
    // convention -> CLR `void` (a Unit-returning fun is a void method; the entry point `fun main(): Unit` MUST be
    // void or the CLR rejects the program). This is UNIFORM across ref AND substitute/app — a Unit-returning method
    // is void in both, matching the prior behaviour — so it is NOT mode-gated. A kotlin.Unit VALUE (a field, a
    // generic arg like Sequence<Unit>, a receiver) keeps the emitted Unit type (you cannot have a `void` field), and
    // an already-decorated `@kotlin.Unit` type-arg passes through unchanged. (Mirrors kotc birTypeDeleg's
    // "kotlin.Unit -> void in return, @kotlin.Unit in type-arg" split.) The numeric primitives are NOT
    // position-dependent — they lower uniformly everywhere via KotlinToClr.
    static readonly HashSet<string> ReturnKeys = new(StringComparer.Ordinal)
    {
        "ret", "retType", "dynRet", "selRet", "returnType", "resultType",
    };

    static readonly string[] ModifierPrefixes = { "byref:", "array:", "nullable:" };

    // The ref.dll @ClrTypeAlias index (Kotlin FQN -> BCL), set per top-level Lower() call. Consulted for ANY CLR-bound
    // type token beyond the hardcoded foundational primitives (collections -> System...IReadOnlyCollection, StringBuilder,
    // Regex, ...). Single-threaded per bir2cir run, so a static binding is sufficient. The foundational primitives stay
    // shadowed by KotlinToClr (checked first), keeping their CLR shorthand ("int"/"string"/"object").
    static IReadOnlyDictionary<string, string> _aliases = new Dictionary<string, string>(StringComparer.Ordinal);

    static string AliasBcl(string fqn) => _aliases.TryGetValue(fqn, out var bcl) ? bcl : null;

    public static JsonNode Lower(JsonNode root, bool refBuild, IReadOnlyDictionary<string, string> aliases = null)
    {
        _aliases = aliases ?? new Dictionary<string, string>(StringComparer.Ordinal);
        return LowerNode(root, refBuild, force: false);
    }

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
                    copy[kv.Key] = LowerSigValue(kv.Value, refBuild, here);   // sig = param types
                else if (ReturnKeys.Contains(kv.Key))
                    copy[kv.Key] = LowerReturnValued(kv.Value, refBuild, here);   // Unit-in-return -> void (uniform)
                else if (TypeKeys.Contains(kv.Key))
                    copy[kv.Key] = LowerTypeValued(kv.Value, refBuild, here);
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
    static JsonNode LowerSigValue(JsonNode val, bool refBuild, bool force)
    {
        if (val is JsonValue scalar && scalar.TryGetValue<string>(out var s))
            return JsonValue.Create(string.Join(",", SplitTopLevel(s).Select(p => LowerTypeString(p, refBuild, force))));
        return LowerNode(val, refBuild, force);
    }

    // A return-slot value: a bare top-level `kotlin.Unit` -> `void` (UNIFORM, both modes); otherwise the normal type
    // lowering (so a return like clrg:List[kotlin.Int] still lowers its inner Int).
    static JsonNode LowerReturnValued(JsonNode val, bool refBuild, bool force)
    {
        if (val is JsonValue scalar && scalar.TryGetValue<string>(out var s))
            return JsonValue.Create(LowerReturnSlot(s, refBuild, force));
        return LowerTypeValued(val, refBuild, force);
    }

    static string LowerReturnSlot(string s, bool refBuild, bool force) =>
        s == "kotlin.Unit" ? "void" : LowerTypeString(s, refBuild, force);

    // A type-bearing key's value: a scalar type string, an array of type strings (interfaces/argTypes/constraints/
    // typeArgs), or — for a few node shapes — a nested object, which is recursed structurally.
    static JsonNode LowerTypeValued(JsonNode val, bool refBuild, bool force)
    {
        if (val is JsonValue scalar && scalar.TryGetValue<string>(out var s))
            return JsonValue.Create(LowerTypeString(s, refBuild, force));

        if (val is JsonArray arr)
        {
            var copy = new JsonArray();
            foreach (var item in arr)
            {
                if (item is JsonValue iv && iv.TryGetValue<string>(out var its))
                    copy.Add(JsonValue.Create(LowerTypeString(its, refBuild, force)));
                else
                    copy.Add(item == null ? null : LowerNode(item, refBuild, force));
            }
            return copy;
        }

        return LowerNode(val, refBuild, force);
    }

    // Recurse the BIR type grammar, rewriting bare kotlin.* foundational tokens (numeric/bool/char + String/Any +
    // the unsigned set) in the active map. Every other shape (gp:, clr:, clrg:[...], @Name[...], func:ret:args,
    // array:/byref:/nullable: modifiers, the CLR shorthand, the position-dependent kotlin.Unit value, and user/
    // stdlib FQNs like kotlin.collections.List) is structurally preserved; nested type arguments are recursed so a
    // bare kotlin.* foundational token inside a generic lowers too.
    public static string LowerTypeString(string raw, bool refBuild, bool force = false)
    {
        // The reference build keeps kotlin.* primitives verbatim (general path); the attribute force path lowers
        // unconditionally. A token with no "kotlin." substring can never contain a mappable token, so skip it.
        if ((!force && refBuild) || !raw.Contains("kotlin.", StringComparison.Ordinal)) return raw;

        var t = raw.Trim();
        if (t.Length == 0) return raw;

        foreach (var p in ModifierPrefixes)
            if (t.StartsWith(p, StringComparison.Ordinal))
                return p + LowerTypeString(t[p.Length..], refBuild, force);

        if (t.StartsWith("gp:", StringComparison.Ordinal)) return t;
        if (t.StartsWith("clr:", StringComparison.Ordinal)) return t;
        if (t.StartsWith("func:", StringComparison.Ordinal)) return LowerFuncString(t, refBuild, force);

        var br = t.IndexOf('[');
        if (br >= 0 && t.EndsWith("]", StringComparison.Ordinal))
        {
            var head = t[..br];
            var inner = t[(br + 1)..^1];
            var args = string.Join(",", SplitTopLevel(inner).Select(a => LowerTypeString(a, refBuild, force)));
            // A @ClrTypeAlias GENERIC type used as a type constructor (supertype/interface/type-arg/field), e.g.
            // kotlin.collections.Collection[E] -> clrg:System.Collections.Generic.IReadOnlyCollection[E]. kotc may carry
            // an `@` (this-assembly-emitted) marker even on a substituted type (a CLR-resolution marker that belongs
            // below kotc) — strip it for the alias lookup and DROP it when the type is BCL-aliased; a non-alias `@`
            // head is a genuine emitted type and keeps its `@`. ilemit builds the generic by arg count. The foundational
            // primitives never appear as a generic head, so KotlinToClr need not gate here.
            var bareHead = head.StartsWith("@", StringComparison.Ordinal) ? head[1..] : head;
            if (!head.StartsWith("clr", StringComparison.Ordinal) && AliasBcl(bareHead) is string genericBcl)
                return "clrg:" + genericBcl + "[" + args + "]";
            return head + "[" + args + "]";
        }

        return LowerLeaf(t, force);
    }

    static string LowerFuncString(string t, bool refBuild, bool force)
    {
        // func:<ret>:<arg,arg,...>  — the ret/args separator is the first top-level ':' AFTER the ret's own
        // leading type prefix (so a ret like clrg:Foo[int] is not split on its clrg: colon). The func RETURN is a
        // return slot -> a Unit ret lowers to void (Action vs Func); args are value positions.
        var body = t["func:".Length..];
        var sep = FuncRetEnd(body);
        var ret = sep >= body.Length ? body : body[..sep];
        var args = sep >= body.Length ? "" : body[(sep + 1)..];
        var loweredArgs = string.Join(",", SplitTopLevel(args).Select(a => LowerTypeString(a, refBuild, force)));
        return "func:" + LowerReturnSlot(ret, refBuild, force) + ":" + loweredArgs;
    }

    static string LowerLeaf(string t, bool force)
    {
        // @-decorated and clrg: references are emitted/CLR type references whose head is never a bare primitive
        // (any bracket args were recursed above) — keep verbatim. A bare kotlin.* foundational leaf (numeric/bool/
        // char + String/Any + the unsigned set) lowers via the active map; all other leaves (CLR shorthand, the
        // position-dependent kotlin.Unit value, user/stdlib FQNs like kotlin.collections.List) pass through.
        if (t.StartsWith("clrg:", StringComparison.Ordinal)) return t;
        // An `@`-decorated PRIMITIVE is the dual-representation type-arg form (Comparable<@kotlin.Int>) and MUST stay
        // verbatim — never lowered to the bare CLR primitive. A bare primitive lowers to its CLR shorthand.
        var decorated = t.StartsWith("@", StringComparison.Ordinal);
        var bare = decorated ? t[1..] : t;
        var map = force ? KotlinAllToClr : KotlinToClr;
        if (map.TryGetValue(bare, out var clr)) return decorated ? t : clr;
        // A non-primitive @ClrTypeAlias type used bare (a non-generic BCL: StringBuilder/Regex/Match/IComparable/
        // TextWriter/...) -> clr:<bcl>. Applies whether or not it carried the `@` marker (BCL-aliased -> drop `@`).
        if (AliasBcl(bare) is string bcl) return "clr:" + bcl;
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

// CALL SUBSTITUTION. The bir2cir home of what kotc's clrName() member routing used to do: a member call /
// construction whose OWNER is a CLR-bound type in the ref.dll is rewritten to a plain BCL call/new that ilemit
// resolves against the runtime BCL. Sourced ENTIRELY from the ref.dll's @ClrTypeAlias (owner identity) and
// @ClrIntrinsic (member name) labels — ilemit receives only `System.X.Member`, never a kotlin.* label.
//
// Three rewrites (mirrors docs/clr-stdlib-intrinsic-audit.md's three binding rules):
//   1. construction `new T(..)` on a CLR-bound REFERENCE owner T -> `clrNew System.X(..)`.
//   2. member `r.m(..)` / `T.m(..)` where m carries @ClrIntrinsic("Name") -> `clrInstance`/`clrStatic` System.X.Name.
//   3. member m with NO @ClrIntrinsic but concrete (a real Kotlin body kotc hoisted to `<>dotkt_ClrH_<T>`) ->
//      a static call to that helper, with the receiver threaded as the helper's first arg. Gated on the helper
//      actually being present in the ref.dll (it is for @Clr-bound classes; for @ClrTypeAlias classes once kotc
//      keys helper emission on @ClrTypeAlias) so we never emit a call to a non-existent helper.
//
// Runs ONLY in the substitute/app build (never the pure-Kotlin reference build) and BEFORE type lowering, so it
// sees the kotlin.* owners. The emitted clr* nodes carry already-BCL `type` tokens; their argTypes/ret stay in the
// kotlin.* vocabulary and are lowered by the subsequent BirTypeLowering pass (those keys are in its TypeKeys).
static class MemberCallSubstitution
{
    public static JsonNode Apply(JsonNode root, ReferenceMetadataIndex refs) => Rewrite(root, refs);

    static JsonNode Rewrite(JsonNode node, ReferenceMetadataIndex refs)
    {
        if (node is JsonObject obj)
        {
            var copy = new JsonObject();
            foreach (var kv in obj)
                copy[kv.Key] = kv.Value == null ? null : Rewrite(kv.Value, refs);   // children first (bottom-up)
            return Transform(copy, refs);
        }
        if (node is JsonArray arr)
        {
            var copy = new JsonArray();
            foreach (var item in arr) copy.Add(item == null ? null : Rewrite(item, refs));
            return copy;
        }
        return node.DeepClone();
    }

    static JsonNode Transform(JsonObject node, ReferenceMetadataIndex refs)
    {
        return (node["k"] as JsonValue)?.GetValue<string>() switch
        {
            "new" => TransformNew(node, refs) ?? node,
            "callInstance" => TransformCall(node, refs, instance: true) ?? node,
            "callStatic" => TransformCall(node, refs, instance: false) ?? node,
            _ => node,
        };
    }

    // `new T(..)` on a CLR-bound REFERENCE owner -> clrNew. A value-type (struct) owner is left untouched: a value
    // primitive keeps its identity (the inline-value-class / unsigned representation is a primitive concern handled
    // by type lowering + kotc, not a member-call substitution).
    static JsonNode TransformNew(JsonObject node, ReferenceMetadataIndex refs)
    {
        if (node["type"] is not JsonValue tv || !tv.TryGetValue<string>(out var ownerToken)) return null;
        if (!refs.TryResolveClrOwner(ownerToken, out var bcl, out var kind)) return null;
        if (kind is "struct" or "enum") return null;

        // A GENERIC @ClrTypeAlias owner (`new HashSet<E>()` -> token `kotlin.collections.HashSet[gp:E]`) must carry
        // its element args so ilemit reconstructs the instantiation: emit clrg:<bcl>[<args>] (the SAME generic-alias
        // form BirTypeLowering produces for type positions). The args stay in the source vocabulary — the subsequent
        // type-lowering pass lowers them (the clrNew `type` is a TypeKey). A non-generic owner stays a bare BCL type.
        var typeTok = bcl;
        var br = ownerToken.IndexOf('[');
        if (br >= 0 && ownerToken.EndsWith("]", StringComparison.Ordinal))
            typeTok = "clrg:" + bcl + "[" + ownerToken[(br + 1)..^1] + "]";

        var args = node["args"] as JsonArray ?? new JsonArray();
        return new JsonObject
        {
            ["k"] = "clrNew",
            ["type"] = typeTok,
            ["argTypes"] = InferArgTypes(node, args),
            ["args"] = args.DeepClone(),
        };
    }

    static JsonNode TransformCall(JsonObject node, ReferenceMetadataIndex refs, bool instance)
    {
        var ownerToken = (node[instance ? "ownerType" : "owner"] as JsonValue)?.GetValue<string>();
        if (string.IsNullOrEmpty(ownerToken))
        {
            // Top-level fun call (`callStatic owner=null`): a @ClrIntrinsic top-level fun -> a fully-qualified BCL
            // static. Split the intrinsic at the last '.' into owner type + method (mirrors kotc's old declaringClass
            // == null path, BirEmitter.kt:3190, but sourced from the ref.dll). Instance calls always carry an owner.
            var fn = (node["method"] as JsonValue)?.GetValue<string>();
            if (instance || string.IsNullOrEmpty(fn) || !refs.TryTopLevelIntrinsic(fn, out var fq)) return null;
            var dot = fq.LastIndexOf('.');
            if (dot <= 0) return null;
            var args0 = node["args"] as JsonArray ?? new JsonArray();
            return ClrCallNode(node, fq[..dot], fq[(dot + 1)..], fq[(dot + 1)..], args0, instance: false);
        }
        if (!refs.TryResolveClrOwner(ownerToken, out var bcl, out var _)) return null;

        var member = (node["method"] as JsonValue)?.GetValue<string>();
        if (string.IsNullOrEmpty(member)) return null;
        var ownerFqn = ReferenceMetadataIndex.BareOwnerFqn(ownerToken);
        var args = node["args"] as JsonArray ?? new JsonArray();

        // Rule 2: the member carries @ClrIntrinsic -> a direct BCL call.
        if (refs.TryMemberIntrinsic(ownerFqn, member, args.Count, out var intrinsic))
            return ClrCallNode(node, bcl, intrinsic, member, args, instance);

        // Rule 3: a concrete member of a CLR-bound class with NO @ClrIntrinsic carries a real Kotlin body, which kotc
        // hoists to the static helper `<>dotkt_ClrH_<owner>` (driven by the SAME class binding that brought us here).
        // `IsRule3Member` (ref.dll: the member is concrete + intrinsic-less) is the signal kotc hoisted it; the helper
        // is emitted into the same runtime assembly. (A ref.dll helper-presence check is uselessly always-false: the
        // ref assembly is metadata-only and emits no helper bodies — see kotc's clrHelperClassJson gate.)
        if (refs.IsRule3Member(ownerFqn, member))
            return Rule3HelperCall(node, refs, ownerFqn, member, args, instance);

        // Rule 4 (universal object/comparable members): kotc renames Kotlin's compareTo/equals/hashCode/toString to the
        // BCL interface/object member names (CompareTo/Equals/GetHashCode/ToString) at the CALL site, so the ref.dll's
        // own member (kept as `compareTo` etc.) doesn't match by that name. These exist on EVERY BCL type the alias
        // targets (a value primitive like System.UInt32, or a reference type) -> route to a direct clrInstance/clrStatic
        // on the BCL type. Without this, the owner lowers to a bare shorthand (`uint`) that ilemit cannot resolve.
        if (BclUniversalMembers.Contains(member))
            return ClrCallNode(node, bcl, member, member, args, instance);

        return null;
    }

    static readonly HashSet<string> BclUniversalMembers = new(StringComparer.Ordinal)
    {
        "CompareTo", "Equals", "GetHashCode", "ToString",
    };

    // A clrInstance / clrStatic node. For a property accessor call (`get_X`/`set_X` whose intrinsic is the bare
    // property name) emit clrPropGet/clrPropSet per the property-name convention; otherwise a plain method call.
    static JsonNode ClrCallNode(JsonObject node, string bcl, string intrinsic, string member, JsonArray args, bool instance)
    {
        var argTypes = InferArgTypes(node, args);
        var ret = RetToken(node);

        var isGet = member.StartsWith("get_", StringComparison.Ordinal) && args.Count == 0;
        var isSet = member.StartsWith("set_", StringComparison.Ordinal) && args.Count == 1;
        if (instance && (isGet || isSet))
        {
            var prop = intrinsic.StartsWith("get_", StringComparison.Ordinal) || intrinsic.StartsWith("set_", StringComparison.Ordinal)
                ? intrinsic[4..] : intrinsic;
            var pg = new JsonObject
            {
                ["k"] = isGet ? "clrPropGet" : "clrPropSet",
                ["type"] = bcl,
                ["name"] = prop,
                ["static"] = false,
                ["recv"] = node["recv"]?.DeepClone(),
            };
            if (isGet && ret != null) pg["retType"] = ret;
            if (isSet) pg["value"] = args[0].DeepClone();
            return pg;
        }

        var call = new JsonObject
        {
            ["k"] = instance ? "clrInstance" : "clrStatic",
            ["type"] = bcl,
            ["method"] = intrinsic,
            ["argTypes"] = argTypes,
        };
        if (ret != null) call["ret"] = ret;
        if (instance) call["recv"] = node["recv"]?.DeepClone();
        call["args"] = args.DeepClone();
        return call;
    }

    // Rule-3: route to `<>dotkt_ClrH_<owner>.<member>(recv?, args..)`. The receiver is threaded as the helper's
    // first argument (the hoisted static's `__self`); type args are carried through when present.
    static JsonNode Rule3HelperCall(JsonObject node, ReferenceMetadataIndex refs, string ownerFqn, string member, JsonArray args, bool instance)
    {
        var hargs = new JsonArray();
        if (instance && node["recv"] != null) hargs.Add(node["recv"].DeepClone());
        foreach (var a in args) hargs.Add(a?.DeepClone());

        var call = new JsonObject
        {
            ["k"] = "callStatic",
            ["owner"] = ReferenceMetadataIndex.HelperTypeName(ownerFqn),
            ["method"] = member,
            ["args"] = hargs,
        };
        if (node["typeArgs"] is JsonArray ta) call["typeArgs"] = ta.DeepClone();
        return call;
    }

    // The call's parameter types, used as the clr* argTypes overload key. Prefer kotc's `sig` (a comma-joined
    // param-type list); else infer each arg's own type token; else empty. Left in the kotlin.* vocabulary —
    // BirTypeLowering lowers `argTypes` afterwards.
    static JsonArray InferArgTypes(JsonObject node, JsonArray args)
    {
        var sig = (node["sig"] as JsonValue)?.GetValue<string>();
        var result = new JsonArray();
        if (!string.IsNullOrWhiteSpace(sig))
        {
            foreach (var p in SplitTopLevel(sig)) result.Add(p);
            if (result.Count == args.Count) return result;
            result = new JsonArray();
        }
        foreach (var a in args) result.Add(InferExpressionType(a));
        return result;
    }

    static string RetToken(JsonObject node)
    {
        foreach (var key in new[] { "dynRet", "retType", "ret" })
            if (node[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                return s;
        return null;
    }

    static string InferExpressionType(JsonNode node)
    {
        if (node is not JsonObject obj) return "object";
        foreach (var key in new[] { "type", "retType", "resultType", "ret", "dynRet" })
            if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                return s;
        return "object";
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
            else if (value[i] == ',' && depth == 0) { result.Add(value[start..i].Trim()); start = i + 1; }
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
