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
        // The top-level funs DEFINED in this compilation (every file-class's own static methods, across all input
        // files). A `callStatic owner=null` to one of these must stay owner-less (ilemit's FindStatic finds the
        // sibling); only a name absent here is eligible for referenced-stdlib file-class attribution (Gap B).
        var localTopLevelFns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in birFiles)
            if (b.Root is JsonObject ro && ro["methods"] is JsonArray ms)
                foreach (var m in ms)
                    if (m is JsonObject mo && (mo["name"] as JsonValue)?.GetValue<string>() is string mn)
                        localTopLevelFns.Add(mn);
        // Attribute referenced top-level stdlib funs to their file-class owner only in an APP build: the stdlib self-
        // build (DOTKT_STDLIB_COMPILE set) defines them locally, so owner=null is correct there. The reference build
        // never runs MemberCallSubstitution at all (see the RefBuild gate below).
        var attributeTopLevelOwner = Environment.GetEnvironmentVariable("DOTKT_STDLIB_COMPILE") == null;

        // Does THIS assembly declare a user `class S : CharSequence` (a type whose `interfaces` names the synthetic
        // `<>dotkt_CharSequence`)? If so, CharSequence must stay the polymorphic synthetic ASSEMBLY-WIDE: a
        // CharSequence param/local in such an assembly may hold that user impl and be read polymorphically
        // (`show(cs: CharSequence) = cs.length` with `show(S("hello"))` == 5) — collapsing it to `string` would
        // snapshot the value via `.toString()` and lose the length. So the CharSequence -> System.String lowering
        // (CharSeqStringLowering) is DISABLED for such assemblies (they keep the synthetic, unchanged), and enabled
        // only for a "pure" app assembly with no user implementer. Sealed System.String forbids a real `: string`
        // supertype, so this synthetic-retention is a technical necessity, not a preference. (docs/design-charsequence-clr-string.md)
        var hasUserCharSeqImpl = birFiles.Any(f => DeclaresCharSeqImplementer(f.Root));

        var files = new List<CirFile>();
        foreach (var bir in birFiles)
        {
            var outputName = OutputNameFor(bir.Path);
            // NULLABLE-GENERIC-RETURN erasure (ALL builds, so ref.dll + rt.dll signatures agree): a Kotlin method
            // declaring a nullable generic-parameter return (`fun <T> …(): T?`) has its nullability erased by kotc to
            // a bare `gp:T` return (Nullable<T> is inexpressible for an unconstrained T). That is CORRECT for a
            // reference T (`ldnull` is a real null) but for a VALUE T `ldnull; ret !!T` collapses to default(T)=0 —
            // null-ness is LOST (firstOrNull on a value-type list returns 0, not the element / not null-for-empty).
            // The CLR-faithful representation of a generic `T?` is `System.Object` (the boxed/erased nullable form).
            // Rewrite the return to `object`; ilemit boxes value returns and the CALL boundary converts object ->
            // the caller's Nullable<V> / reference type. Runs BEFORE the rest so type-lowering/substitution see it.
            NullableGenericReturnErasure.Apply(bir.Root);
            // CALL substitution (substitute/app builds only): a member call / construction whose OWNER is a CLR-bound
            // type in the ref.dll (@ClrTypeAlias, or the legacy class-level @ClrIntrinsic) is rewritten to a plain BCL
            // call/new. This is the bir2cir home of what kotc's clrName() member routing used to do — sourced from the
            // ref.dll's @ClrIntrinsic labels, NOT from kotc. Runs BEFORE type lowering so it sees the kotlin.* owners.
            // RULE-3 HOIST (all CLR-bound alias classes): kotc emits EVERY @ClrTypeAlias class with hoistable bodies as a
            // PLAIN BIR type — alias-only files (String/Char/Boolean) AND the MIXED files (StringBuilder/collections/
            // Regex/unsigned) alike — and synthesizes NO <>dotkt_ClrH_* helper itself. This pass reads the ref.dll
            // @ClrTypeAlias index, turns each such plain type into the static helper + drops it, BEFORE call substitution
            // so the (already-BCL) member bodies and the rule-3 call routing both see a consistent helper. No-op for ref.
            // MEMBER-STRIP (clrName migration): drop the @ClrIntrinsic-bound stub declarations kotc used to exclude
            // (once it stops reading @ClrIntrinsic). BEFORE the hoist so an alias class's bound stubs / @ClrIntrinsic
            // overrides don't over-hoist into the rule-3 helper.
            if (!_options.RefBuild) MemberStrip.Apply(bir.Root, refs);
            var hoisted = _options.RefBuild ? bir.Root : AliasHelperHoist.Apply(bir.Root, refs);
            // DECLARATION + CALL-NAME rename (clrName migration): a member declaration that overrides a CLR-bound
            // interface member carrying @ClrIntrinsic gets the BCL slot name (a `size` getter override -> get_Count,
            // `resumeWith` -> ResumeWith), AND the corresponding implementor-side call (`AbstractList.get_size` ->
            // `get_Count`) — the job kotc's clrName/annClr does today. Derived from the `overrides` marker (the pure-Kotlin
            // override closure) + the ref.dll @ClrIntrinsic bindings. Runs BEFORE MemberCallSubstitution so a now-get_Count
            // call on a CLR-bound owner still falls through to clrPropGet. While annClr STILL runs in kotc this is
            // IDEMPOTENT (reproduces the name annClr already set) -> CIR byte-identical. Never in ref (there annClr is null
            // and members keep their plain Kotlin names — renaming would corrupt the pure-Kotlin ref shapes).
            if (!_options.RefBuild) DeclarationRename.Apply(hoisted, refs);
            var substituted = _options.RefBuild ? hoisted : MemberCallSubstitution.Apply(hoisted, refs, localTopLevelFns, attributeTopLevelOwner);
            // Gap A — the for-loop iterator protocol over a referenced collection: re-point the desugared `<iterator>`
            // var + its synthetic hasNext/next owner at the REAL referenced kotlin.collections.Iterator<E> (app build
            // only; the stdlib self-build emits Iterator itself, so it is left synthetic there).
            if (attributeTopLevelOwner) IteratorConsumerNormalization.Apply(substituted);
            // Cross-module default-argument splice: fill a call's OMITTED defaulted args from the callee's @KotlinDefault
            // BIR (ref.dll), for a non-null object/CharSequence default the metadata backfill can't carry. Runs before the
            // CharSequence bridge + type lowering so a spliced String default is coerced/lowered like an explicit arg.
            if (attributeTopLevelOwner) DefaultArgSplice.Apply(substituted, refs);
            // String -> CharSequence adapter bridge: materialize a bare `System.String` flowing into a synthetic
            // `<>dotkt_CharSequence` slot as `new <>dotkt_StringCharSequence(str)` (String is sealed, can't implement
            // the synthetic interface). Runs on EVERY non-ref build — app AND the RT stdlib self-build. The RT build
            // NEEDS it too: the stdlib's own CharSequence-extension bodies widen a `String` into a `<>dotkt_CharSequence`
            // slot INTERNALLY (`CharSequence.indexOf(string: String)` -> the private `indexOf(other: CharSequence)`;
            // `String.trim()` -> `(this as CharSequence).trim()`), and without the wrap those compiled rt.dll bodies pass
            // a raw String where the interface is required -> InvalidProgram / EntryPointNotFound at run. The adapter is
            // injected into the rt assembly exactly once (dedup), implementing the RT's canonical `<>dotkt_CharSequence`,
            // so an app that then routes a String op to a real stdlib body works. Skipped only for the ref build (its
            // bodies are squashed to `throw` anyway). Purely additive: only positively-String values are wrapped.
            // CharSequence -> System.String (the 3-point model, point ①/②). In a "pure" APP assembly (no user
            // `class S : CharSequence`, so no polymorphic implementer can flow through a CharSequence slot) an app's
            // OWN CharSequence-typed param/return/local is lowered to `System.String`, its member reads
            // (length/get/subSequence) resolve to System.String.Length/get_Chars/Substring, and a non-String value
            // (a StringBuilder) flowing into such a now-`string` slot is snapshot with an implicit `.toString()`.
            // Runs BEFORE the StringCharSequenceBridge so a now-`string` value flowing into a *stdlib* CharSequence-ext
            // (whose param stays the synthetic in the un-rebuilt stdlib) is still adapter-wrapped by the bridge — the
            // two compose. Skipped for the stdlib self-build (attributeTopLevelOwner) and for any assembly that
            // declares a user CharSequence implementer (hasUserCharSeqImpl) — those keep the synthetic verbatim.
            if (!_options.RefBuild && attributeTopLevelOwner && !hasUserCharSeqImpl)
                substituted = CharSeqStringLowering.Apply(substituted, localTopLevelFns);
            if (!_options.RefBuild) substituted = StringCharSequenceBridge.Apply(substituted);
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
            // A file whose ENTIRE content was @ClrTypeAlias types (e.g. Primitives.kt, Comparable.kt) is now empty after
            // AliasHelperHoist dropped them — emit no CIR file for it (an empty file-class would be a pointless empty
            // static type in the assembly). Skips only when types AND methods AND fields are all empty; never in ref.
            if (!_options.RefBuild && IsEmptyCir(lowered)) continue;
            files.Add(new CirFile(outputName, lowered.ToJsonString(JsonOptions.Indented)));
        }

        return files;
    }

    // A lowered CIR root that carries no types, no methods and no fields contributes nothing — its file-class would be
    // an empty static type. True once AliasHelperHoist has dropped a file whose sole content was @ClrTypeAlias types.
    static bool IsEmptyCir(JsonNode root)
    {
        if (root is not JsonObject o) return false;
        static bool Empty(JsonNode? n) => n is not JsonArray a || a.Count == 0;
        return Empty(o["types"]) && Empty(o["methods"]) && Empty(o["fields"]);
    }

    // True iff this file declares a type whose `interfaces` names the synthetic `<>dotkt_CharSequence` — i.e. a user
    // `class S : CharSequence`. Such a type is a genuine polymorphic implementer, so the whole assembly must keep the
    // synthetic (CharSeqStringLowering is disabled). Only kotc's `interfaces` array carries this name at the top level
    // of a type; the synthetic interface DEFINITION itself (name == the synthetic) has an EMPTY interfaces list, so it
    // is not counted.
    static bool DeclaresCharSeqImplementer(JsonNode root)
    {
        if (root is not JsonObject o || o["types"] is not JsonArray types) return false;
        foreach (var t in types)
            if (t is JsonObject to && to["interfaces"] is JsonArray ifaces)
                foreach (var i in ifaces)
                    if (i is JsonValue v && v.TryGetValue<string>(out var s) &&
                        s.TrimStart('@') == "<>dotkt_CharSequence")
                        return true;
        return false;
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
    const string JvmInlineAttr = "kotlin.jvm.JvmInline";

    readonly List<ReferenceAssembly> _assemblies;

    // Aggregate CALL-SUBSTITUTION index across all reference assemblies.
    readonly Dictionary<string, string> _ownerAlias = new(StringComparer.Ordinal);   // Kotlin FQN -> BCL alias
    readonly Dictionary<string, string> _ownerKind = new(StringComparer.Ordinal);    // Kotlin FQN -> class/struct/...
    readonly Dictionary<string, int> _ownerArity = new(StringComparer.Ordinal);      // Kotlin FQN -> generic arity
    readonly Dictionary<string, string[]> _ownerTypeParams = new(StringComparer.Ordinal); // Kotlin FQN -> generic param names
    readonly HashSet<string> _helperTypes = new(StringComparer.Ordinal);             // emitted "<>dotkt_ClrH_*"
    readonly Dictionary<string, List<MemberBinding>> _membersByOwner = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _topLevelIntrinsics = new(StringComparer.Ordinal); // top-level fun name -> FQ static
    readonly Dictionary<string, string> _topLevelIntrinsicsBySig = new(StringComparer.Ordinal); // "name|paramKeys" -> FQ static (overload-disambiguated)
    readonly HashSet<string> _ambiguousTopLevelIntrinsics = new(StringComparer.Ordinal); // names whose overloads bind to DIFFERENT statics (Math vs MathF)
    readonly Dictionary<string, int[]> _topLevelIntrinsicByref = new(StringComparer.Ordinal); // top-level fun name -> byref param positions
    readonly Dictionary<string, string> _extMemberIntrinsics = new(StringComparer.Ordinal); // "name|recvKey|paramCount" -> bare member
    readonly Dictionary<string, (string Getter, string Conv)> _inlineBacking = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<(string Owner, string RecvKey)>> _topLevelStatics = new(StringComparer.Ordinal); // non-intrinsic top-level fun name -> [(file-class, recvKey)]
    readonly Dictionary<string, Dictionary<int, string>> _kotlinDefaults = new(StringComparer.Ordinal); // "owner|name|paramCount" -> (argPos -> default BIR)

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
            foreach (var kv in asm.DotKt.TypeArity) _ownerArity[kv.Key] = kv.Value;
            foreach (var kv in asm.DotKt.TypeParamNames) _ownerTypeParams[kv.Key] = kv.Value;
            foreach (var h in asm.DotKt.HelperTypes) _helperTypes.Add(h);
            foreach (var m in asm.DotKt.MemberBindings)
            {
                if (!_membersByOwner.TryGetValue(m.Owner, out var list))
                    _membersByOwner[m.Owner] = list = new List<MemberBinding>();
                list.Add(m);
            }
            foreach (var kv in asm.DotKt.TopLevelIntrinsics) _topLevelIntrinsics.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.TopLevelIntrinsicsBySig) _topLevelIntrinsicsBySig.TryAdd(kv.Key, kv.Value);
            foreach (var n in asm.DotKt.AmbiguousTopLevelIntrinsics) _ambiguousTopLevelIntrinsics.Add(n);
            foreach (var kv in asm.DotKt.TopLevelIntrinsicByref) _topLevelIntrinsicByref.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.ExtMemberIntrinsics) _extMemberIntrinsics.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.InlineBacking) _inlineBacking.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.TopLevelStatics)
            {
                if (!_topLevelStatics.TryGetValue(kv.Key, out var lst))
                    _topLevelStatics[kv.Key] = lst = new List<(string, string)>();
                lst.AddRange(kv.Value);
            }
            foreach (var kv in asm.DotKt.KotlinDefaults) _kotlinDefaults.TryAdd(kv.Key, kv.Value);
        }
    }

    // The @KotlinDefault BIR splice map for a call's callee — (argPosition -> default-expression BIR-json). Matched by
    // owner FQN + method name + total parameter count (the emitted-call arity, extension receiver included). Null when
    // the callee carries no @KotlinDefault (a function with only metadata-representable defaults).
    public Dictionary<int, string> KotlinDefaultsFor(string owner, string method, int paramCount) =>
        _kotlinDefaults.TryGetValue(owner + "|" + method + "|" + paramCount, out var m) ? m : null;

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

    public int OwnerArity(string ownerFqn) => _ownerArity.GetValueOrDefault(ownerFqn, 0);
    public string[] OwnerTypeParamNames(string ownerFqn) => _ownerTypeParams.GetValueOrDefault(ownerFqn);

    // The @ClrProperty accessor binding for owner.member: its READ/WRITE access flags + the .NET property name. Routes the
    // call EXPLICITLY to clrPropGet/clrPropSet (no get_/set_ string-prefix sniff). Overload-disambiguated by arg count.
    public bool TryMemberProperty(string ownerFqn, string memberName, int argCount, out int access, out string name)
    {
        access = 0; name = null;
        if (!_membersByOwner.TryGetValue(ownerFqn, out var list)) return false;
        var cands = list.Where(m => m.Name == memberName && m.PropertyName != null).ToList();
        if (cands.Count == 0) return false;
        var pick = cands.FirstOrDefault(m => m.ParamCount == argCount) ?? cands[0];
        access = pick.PropertyAccess; name = pick.PropertyName;
        return true;
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

    // STRICT overload-exact @ClrIntrinsic lookup for the DECLARATION rename: the marker's arity is precise (Kotlin
    // override resolution), so `add(element)` (arity 1, ->Add) must NOT fall through to `add(index,element)` (arity 2,
    // ->Insert). Unlike TryMemberIntrinsic there is no `?? cands[0]` arity fallback — no exact-arity match = no rename.
    public bool TryMemberIntrinsicExact(string ownerFqn, string memberName, int argCount, out string intrinsic)
    {
        intrinsic = _membersByOwner.TryGetValue(ownerFqn, out var list)
            ? list.FirstOrDefault(m => m.Name == memberName && m.Intrinsic != null && m.ParamCount == argCount)?.Intrinsic
            : null;
        return intrinsic != null;
    }

    // FULL-SIGNATURE @ClrIntrinsic lookup for the member-STRIP: is owner.name(paramKeys) a bound stub? Matches the
    // @ClrIntrinsic member whose canonicalized param types equal the emitted method's — so `StringBuilder.append(Char)`
    // (@ClrIntrinsic, dropped) is distinguished from `append(CharSequence?)` (rule-3, kept), which share name+arity.
    public bool IsBoundStub(string ownerFqn, string memberName, IReadOnlyList<string> birParamKeys)
    {
        if (!_membersByOwner.TryGetValue(ownerFqn, out var list)) return false;
        return list.Any(m => m.Name == memberName && m.Intrinsic != null && m.ParamTypes != null
            && m.ParamTypes.Length == birParamKeys.Count
            && m.ParamTypes.Select(ParamKey).SequenceEqual(birParamKeys));
    }

    // Canonicalize a type token (a kotc birType or a ref.dll reflected TypeName) to a comparable identity for signature
    // matching: unwrap byref/array/nullable, drop the clr/@ marker + generic args, collapse a type param, fold primitives.
    // Deliberately shallow (top-level identity) — enough to separate the real overloads without full structural matching.
    public static string ParamKey(string t)
    {
        t = t.Trim();
        if (t.EndsWith("?", StringComparison.Ordinal)) t = t[..^1];
        foreach (var w in new[] { "byref:", "array:", "nullable:" })
            if (t.StartsWith(w, StringComparison.Ordinal)) return w + ParamKey(t[w.Length..]);
        foreach (var p in new[] { "clrg:", "clr:", "@" })
            if (t.StartsWith(p, StringComparison.Ordinal)) { t = t[p.Length..]; break; }
        if (t.StartsWith("func:", StringComparison.Ordinal)) return "func";
        var br = t.IndexOf('[');
        if (br >= 0) t = t[..br];
        if (t.StartsWith("gp:", StringComparison.Ordinal)) return "gp";
        return t switch
        {
            "kotlin.Byte" or "System.SByte" or "sbyte" or "byte" => "i8",   // kotc BIR shorthand "byte" IS kotlin.Byte (SByte)
            "kotlin.Short" or "System.Int16" or "short" => "i16",
            "kotlin.Int" or "System.Int32" or "int" => "i32",
            "kotlin.Long" or "System.Int64" or "long" => "i64",
            "kotlin.Float" or "System.Single" or "float" => "f32",
            "kotlin.Double" or "System.Double" or "double" => "f64",
            "kotlin.Boolean" or "System.Boolean" or "bool" => "bool",
            "kotlin.Char" or "System.Char" or "char" => "char",
            "kotlin.String" or "System.String" or "string" => "str",
            "kotlin.Unit" or "System.Void" or "void" => "void",
            "kotlin.Any" or "System.Object" or "object" => "obj",
            // Primitive-array class spellings (kotc lowers to `array:int`, but the ref.dll may reflect the kotlin.IntArray
            // class) -> the same array key so a top-level `sort(IntArray)`@ClrIntrinsic matches by signature.
            "kotlin.IntArray" => "array:i32",
            "kotlin.LongArray" => "array:i64",
            "kotlin.ByteArray" => "array:i8",
            "kotlin.ShortArray" => "array:i16",
            "kotlin.FloatArray" => "array:f32",
            "kotlin.DoubleArray" => "array:f64",
            "kotlin.BooleanArray" => "array:bool",
            "kotlin.CharArray" => "array:char",
            _ => StripGenericArity(t),
        };
    }


    // A top-level fun (file-class static, called as `callStatic owner=null`) bound by @ClrIntrinsic to a
    // fully-qualified BCL static (e.g. clrTimestamp -> "System.Diagnostics.Stopwatch.GetTimestamp").
    public bool TryTopLevelIntrinsic(string funName, out string fqStatic) =>
        _topLevelIntrinsics.TryGetValue(funName, out fqStatic);

    // Overload-disambiguated variant: a top-level @ClrIntrinsic name that binds to DIFFERENT BCL statics per overload
    // — kotlin.math `sqrt`/`abs`/`pow`/... -> System.Math.* for Double/Int/Long but System.MathF.* for Float. Keyed by
    // name|<ParamKey-joined signature> so a call resolves the EXACT intrinsic overload (and a non-intrinsic sibling
    // overload, e.g. `Double.pow(Int)`, correctly MISSES here and falls through to its real Kotlin body). `sigKey` is
    // the call's ParamKey-normalized signature. This is what lets the by-name-first-wins map stop shadowing MathF.
    public bool TryTopLevelIntrinsicBySig(string funName, string sigKey, out string fqStatic) =>
        _topLevelIntrinsicsBySig.TryGetValue(funName + "|" + sigKey, out fqStatic);

    // Whether a top-level intrinsic NAME binds to more than one distinct BCL static across its overloads (sqrt/abs/
    // pow -> Math vs MathF). For such names the name-only fallback is UNSAFE (it would pick an arbitrary overload), so
    // the caller must require an exact signature match; single-static names still fall back by name.
    public bool IsAmbiguousTopLevelIntrinsic(string funName) => _ambiguousTopLevelIntrinsics.Contains(funName);

    // The 0-based parameter positions a top-level @ClrIntrinsic fun's bound BCL static takes BY REFERENCE
    // (@ClrRefArgument). Empty when none — the substituted call then wraps no argTypes.
    public int[] TopLevelByrefPositions(string funName) =>
        _topLevelIntrinsicByref.TryGetValue(funName, out var pos) ? pos : Array.Empty<int>();

    // The 0-based parameter positions a bound MEMBER (owner.member, overload-matched by arg count) takes BY REFERENCE
    // (@ClrRefArgument). Empty when none.
    public int[] MemberByrefPositions(string ownerFqn, string memberName, int argCount)
    {
        if (!_membersByOwner.TryGetValue(ownerFqn, out var list)) return Array.Empty<int>();
        var cands = list.Where(m => m.Name == memberName && m.ByrefPositions != null && m.ByrefPositions.Length > 0).ToList();
        if (cands.Count == 0) return Array.Empty<int>();
        return (cands.FirstOrDefault(m => m.ParamCount == argCount) ?? cands[0]).ByrefPositions;
    }

    // A NON-intrinsic top-level fun (real Kotlin body) resolved to the file-class it lives in, so an APP's
    // `callStatic owner=null` gets an explicit owner ilemit reflects against the referenced runtime stdlib. When the
    // name is defined in multiple file-classes (getOrElse in CollectionsKt/ArraysKt/MapsKt/...), the call's receiver
    // type (recvKey = its first sig param's bare owner) disambiguates. A single candidate needs no receiver match.
    public bool TryResolveTopLevelStatic(string funName, string recvKey, out string owner)
    {
        owner = null;
        if (!_topLevelStatics.TryGetValue(funName, out var cands) || cands.Count == 0) return false;
        if (cands.Count == 1) { owner = cands[0].Owner; return true; }
        // The candidate RecvKey is the ref.dll's Kotlin receiver type (`kotlin.collections.List`); the call site's
        // recvKey may already be that type's @ClrTypeAlias CLR form (`System.Collections.Generic.IReadOnlyList`), when
        // kotc rendered the receiver local as its CLR alias (e.g. `val xs = listOf(...)` used only via an extension).
        // Match through the alias so the overload disambiguates in either representation. (The forward alias map is
        // unambiguous; a bare-Kotlin recvKey still matches the plain `c.RecvKey == recvKey` arm.)
        foreach (var c in cands)
            if (c.RecvKey == recvKey || (_ownerAlias.TryGetValue(c.RecvKey, out var aliased) && aliased == recvKey))
            { owner = c.Owner; return true; }
        // The receiver key didn't disambiguate the OVERLOAD, but if every candidate lives in the SAME file-class the
        // OWNER is still unambiguous (e.g. both `runCatching(Func)` and `T.runCatching(Func)` are in kotlin.ResultKt).
        // Emit the shared owner; ilemit's FindMethod then selects the exact overload by signature.
        var owners = cands.Select(c => c.Owner).Distinct().ToList();
        if (owners.Count == 1) { owner = owners[0]; return true; }
        return false;
    }

    // A bare-@ClrIntrinsic extension fun resolved by name + the receiver-type key (the call's first-arg type) + the
    // FULL parameter count (receiver + args), so `set` on a MutableMap receiver -> set_Item (not StringBuilder's
    // set_Chars) AND a same-name/same-receiver overload of a DIFFERENT arity does not collide: `substring(String,Int)`
    // @ClrIntrinsic("Substring") must NOT capture the 3-param `substring(String,Int,Int)` real-body call (which would
    // wrongly emit Substring(start,end) with end read as a LENGTH). The paramCount disambiguates them; the real-bodied
    // overload misses here and falls through to its stdlib file-class attribution.
    public bool TryExtMemberIntrinsic(string funName, string recvKey, int paramCount, out string member) =>
        _extMemberIntrinsics.TryGetValue(funName + "|" + recvKey + "|" + paramCount, out member);

    // An @JvmInline value class's backing-field getter call (`x.get_data()`): the inline UNBOX. Returns the CLR conv
    // token for the field's declared type so the call collapses to `conv(recv)` (the erased primitive IS the value).
    public bool TryInlineFieldGetter(string ownerFqn, string member, out string conv)
    {
        conv = null;
        return _inlineBacking.TryGetValue(ownerFqn, out var info) && member == info.Getter && (conv = info.Conv) != null;
    }

    // Whether the owner is an @JvmInline value class erased to a primitive CLR form (so `new T(arg)` is the inline BOX).
    public bool IsInlineValueClass(string ownerFqn) => _inlineBacking.ContainsKey(ownerFqn);

    // A rule-3 hoist candidate: owner.member exists, is concrete (non-abstract) and carries NEITHER @ClrIntrinsic NOR
    // @ClrProperty, so its real Kotlin body was hoisted by kotc to the static helper `<>dotkt_ClrH_<owner>`. A @ClrProperty
    // accessor (setLength/capacity/nativeSetCapacity/ticks) is a BOUND stub — its call substitutes to clrPropGet/clrPropSet
    // (Rule 2p) — so it must NOT hoist its throwing TODO body into the helper (the same exclusion @ClrIntrinsic gets).
    public bool IsRule3Member(string ownerFqn, string memberName) =>
        _membersByOwner.TryGetValue(ownerFqn, out var list) &&
        list.Any(m => m.Name == memberName && m.Intrinsic == null && m.PropertyName == null && !m.IsAbstract);

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
                    if (type.IsGenericType)
                    {
                        metadata.TypeArity[ownerFqn] = type.GetGenericArguments().Length;
                        metadata.TypeParamNames[ownerFqn] = type.GetGenericArguments().Select(g => g.Name).ToArray();
                    }
                    var classAlias = ClrAliasOf(type.GetCustomAttributesData());
                    if (classAlias != null) metadata.Aliases[ownerFqn] = classAlias;
                    if (ownerFqn.StartsWith("<>dotkt_ClrH_", StringComparison.Ordinal)) metadata.HelperTypes.Add(ownerFqn);
                    var isFileClass = HasAttribute(type.GetCustomAttributesData(), KotlinFileClassAttr);

                    // @JvmInline value class: its single instance backing field IS the erased value. Record the field
                    // getter + the field's CLR conv token so a `get_<field>()` call collapses to `conv(<recv>)`.
                    if (HasAttribute(type.GetCustomAttributesData(), JvmInlineAttr))
                    {
                        var backing = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).FirstOrDefault();
                        if (backing != null && InlineFieldConv(backing.FieldType) is string conv)
                            metadata.InlineBacking[ownerFqn] = ("get_" + backing.Name, conv);
                    }

                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        var intrinsic = ClrIntrinsicOf(method.GetCustomAttributesData());
                        var prop = ClrPropertyOf(method.GetCustomAttributesData());
                        var byrefPositions = ByrefPositionsOf(method);
                        // @KotlinDefault(index, bir) on the method's params -> the cross-module default-arg splice source.
                        var kdefaults = KotlinDefaultsOf(method);
                        if (kdefaults != null)
                            metadata.KotlinDefaults[ownerFqn + "|" + method.Name + "|" + method.GetParameters().Length] = kdefaults;
                        metadata.MemberBindings.Add(new MemberBinding(
                            ownerFqn,
                            method.Name,
                            method.GetParameters().Length,
                            intrinsic,
                            method.IsAbstract,
                            method.IsStatic,
                            method.GetParameters().Select(p => TypeName(p.ParameterType)).ToArray(),
                            prop?.Access ?? 0,
                            prop?.Name,
                            byrefPositions));
                        // A top-level fun (file-class static) with @ClrIntrinsic. TWO shapes:
                        //   FQ "System.X.Y"  -> a fully-qualified BCL static (isNaN, clrTimestamp); keyed by NAME.
                        //   bare "Name"      -> a member on an EXTENSION receiver (`Array<T>.nativeClone()` ->
                        //                       @ClrIntrinsic("Clone")). Keyed by NAME|recvKey (the first param's type),
                        //                       because the name alone collides across receivers (MutableMap.set->set_Item
                        //                       vs StringBuilder.set->set_Chars). recvKey of the call site is its first arg.
                        if (isFileClass && method.IsStatic && intrinsic != null)
                        {
                            var ps = method.GetParameters();
                            if (intrinsic.Contains('.'))
                            {
                                // Name-only map (first-wins) is retained for single-static intrinsics (isNaN,
                                // clrTimestamp); when a name is seen binding to a DIFFERENT static, mark it ambiguous so
                                // the caller requires an exact-signature match instead (sqrt/abs/pow -> Math vs MathF).
                                if (metadata.TopLevelIntrinsics.TryGetValue(method.Name, out var prior))
                                {
                                    if (prior != intrinsic) metadata.AmbiguousTopLevelIntrinsics.Add(method.Name);
                                }
                                else metadata.TopLevelIntrinsics[method.Name] = intrinsic;
                                // ALSO key by name|<full ParamKey signature> so a call resolves the EXACT overload
                                // (sqrt(Double)->System.Math.Sqrt, sqrt(Float)->System.MathF.Sqrt) and a non-intrinsic
                                // sibling (Double.pow(Int)) misses -> falls through to its real Kotlin body.
                                metadata.TopLevelIntrinsicsBySig.TryAdd(method.Name + "|" + SigKeyOf(ps), intrinsic);
                                if (byrefPositions.Length > 0) metadata.TopLevelIntrinsicByref.TryAdd(method.Name, byrefPositions);
                            }
                            else if (ps.Length >= 1)
                                metadata.ExtMemberIntrinsics.TryAdd(method.Name + "|" + RecvKey(ps[0].ParameterType) + "|" + ps.Length, intrinsic);
                        }
                        // A NON-intrinsic top-level fun (a real Kotlin body in a file-class) -> index it by name so an APP
                        // build can attribute a referenced `callStatic owner=null` to this file-class (disambiguated by the
                        // first-param receiver type when overloaded across file-classes). The stdlib self-build never reads it.
                        if (isFileClass && method.IsStatic && intrinsic == null)
                        {
                            var ps = method.GetParameters();
                            var rk = ps.Length >= 1 ? RecvKey(ps[0].ParameterType) : "";
                            if (!metadata.TopLevelStatics.TryGetValue(method.Name, out var lst))
                                metadata.TopLevelStatics[method.Name] = lst = new List<(string, string)>();
                            lst.Add((ownerFqn, rk));
                        }
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

    // The PARAMETER positions (0-based, over the method's declared params) marked @ClrRefArgument — a plain-typed
    // parameter the bound BCL member takes BY REFERENCE (`ref`/`out`). The substituted call wraps these argTypes
    // positions `byref:` so ilemit resolves the ref/out overload + emits the address-load. Empty when none.
    static int[] ByrefPositionsOf(MethodBase method)
    {
        var ps = method.GetParameters();
        List<int> hits = null;
        for (var i = 0; i < ps.Length; i++)
            if (ps[i].GetCustomAttributesData().Any(a => a.AttributeType.FullName == "kotlin.clr.ClrRefArgument"))
                (hits ??= new List<int>()).Add(i);
        return hits?.ToArray() ?? Array.Empty<int>();
    }

    // @KotlinDefault(index, bir) on the method's parameters -> (argPosition -> default-expression BIR-json). Returns null
    // when no parameter carries it. `index` is the parameter's position in the emitted call (extension receiver first);
    // `bir` is the default expression as a raw BIR-json string (opaque here — spliced pre-lowering by DefaultArgSplice).
    static Dictionary<int, string> KotlinDefaultsOf(MethodBase method)
    {
        Dictionary<int, string> map = null;
        foreach (var p in method.GetParameters())
        {
            var a = p.GetCustomAttributesData().FirstOrDefault(x => x.AttributeType.FullName == "kotlin.clr.KotlinDefault");
            if (a == null || a.ConstructorArguments.Count < 2) continue;
            if (a.ConstructorArguments[0].Value is null || a.ConstructorArguments[1].Value is not string bir) continue;
            (map ??= new Dictionary<int, string>())[Convert.ToInt32(a.ConstructorArguments[0].Value)] = bir;
        }
        return map;
    }

    // The member-level PROPERTY-accessor binding: @ClrProperty(access, name). `access` is the READ(1)/WRITE(2) flag word;
    // `name` is the .NET property. Returns (access, name) or null when the member carries no @ClrProperty.
    static (int Access, string Name)? ClrPropertyOf(IList<CustomAttributeData> attrs)
    {
        var a = attrs.FirstOrDefault(x => x.AttributeType.FullName == "kotlin.clr.ClrProperty");
        if (a == null || a.ConstructorArguments.Count < 2) return null;
        if (a.ConstructorArguments[1].Value is not string name) return null;
        var access = a.ConstructorArguments[0].Value is null ? 0 : Convert.ToInt32(a.ConstructorArguments[0].Value);
        return (access, name);
    }

    // A receiver-type key for an extension fun's first param, matched against a call's first-arg type. Arrays collapse
    // to "[]", generic params to "gp", a generic type to its open def's stripped FQN.
    static string RecvKey(Type t)
    {
        if (t.IsByRef && t.GetElementType() is Type e) t = e;
        if (t.IsArray) return "[]";
        if (t.IsGenericParameter) return "gp";
        var def = t.IsGenericType ? t.GetGenericTypeDefinition() : t;
        return StripGenericArity(def.FullName ?? def.Name);
    }

    // A method's full ParamKey-normalized signature ("f64", "f64,f64", "i32", ...), used to overload-disambiguate a
    // top-level @ClrIntrinsic (sqrt(Double) vs sqrt(Float); pow(Double,Double) intrinsic vs pow(Double,Int) real-body).
    // Runs each param's TypeName through ParamKey so the ref.dll declaration and the call's kotc `sig` agree.
    static string SigKeyOf(ParameterInfo[] ps) => string.Join(",", ps.Select(p => ParamKey(TypeName(p.ParameterType))));

    // An @JvmInline backing-field's CLR `conv` target — the ilemit conv opcode token for the field's primitive type
    // (kotlin.Int -> "int", kotlin.Byte -> "byte"=sbyte, ...). Null if the field is not a primitive ilemit conv'able.
    static string InlineFieldConv(Type fieldType) => fieldType.FullName switch
    {
        "kotlin.Int" => "int", "kotlin.Long" => "long", "kotlin.Short" => "short", "kotlin.Byte" => "byte",
        "kotlin.Char" => "char", "kotlin.Double" => "double", "kotlin.Float" => "float",
        "System.Int32" => "int", "System.Int64" => "long", "System.Int16" => "short", "System.SByte" => "byte",
        "System.Char" => "char", "System.Double" => "double", "System.Single" => "float",
        _ => null,
    };

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
    public readonly Dictionary<string, int> TypeArity = new(StringComparer.Ordinal);       // ownerFqn -> generic arity
    public readonly Dictionary<string, string[]> TypeParamNames = new(StringComparer.Ordinal); // ownerFqn -> generic param names
    public readonly HashSet<string> HelperTypes = new(StringComparer.Ordinal);            // emitted "<>dotkt_ClrH_*" rule-3 helpers
    public readonly List<MemberBinding> MemberBindings = new();                           // per-member @ClrIntrinsic + shape
    // Top-level fun name -> its @ClrIntrinsic fully-qualified static target ("System.Diagnostics.Stopwatch.GetTimestamp").
    // A top-level fun is a static method of a [KotlinFileClass] type; its call site is `callStatic owner=null`.
    public readonly Dictionary<string, string> TopLevelIntrinsics = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> TopLevelIntrinsicsBySig = new(StringComparer.Ordinal);
    public readonly HashSet<string> AmbiguousTopLevelIntrinsics = new(StringComparer.Ordinal);
    // Top-level @ClrIntrinsic fun name -> the 0-based parameter positions its bound BCL static takes BY REFERENCE
    // (@ClrRefArgument). The substituted clrStatic wraps these argTypes positions `byref:` (tryParseInt32's `out result`,
    // Interlocked's `ref location`, Math.DivRem's `out remainder`). Absent when the fun has no byref parameter.
    public readonly Dictionary<string, int[]> TopLevelIntrinsicByref = new(StringComparer.Ordinal);
    // Bare-@ClrIntrinsic extension fun, keyed "funName|recvKey" (recvKey = the receiver/first-param type) -> the BCL
    // member name. Receiver-keyed because the bare name collides across receivers (set->set_Item vs set->set_Chars).
    public readonly Dictionary<string, string> ExtMemberIntrinsics = new(StringComparer.Ordinal);
    // @JvmInline value-class owner FQN -> (its single backing-field getter "get_data", the field's CLR conv token).
    // The class is ERASED to its primitive CLR form, so `get_data()` is the inline unbox: it collapses to the receiver
    // value conv'd to the field's declared type (a `conv`, never a `ldfld data` — the erased primitive has no field).
    public readonly Dictionary<string, (string Getter, string Conv)> InlineBacking = new(StringComparer.Ordinal);
    // NON-intrinsic top-level funs (real Kotlin bodies in a [KotlinFileClass]) -> their (file-class owner FQN, first-
    // param recvKey). Keyed by fun name. Lets an APP build resolve a referenced `callStatic owner=null` to the file-
    // class it actually lives in (getOrElse -> kotlin.collections._CollectionsKt), disambiguated by the call's receiver
    // type when the name is defined across multiple file-classes (CollectionsKt vs ArraysKt vs MapsKt). NOT consulted in
    // a stdlib self-build (the fun is local there; owner=null + FindStatic finds the sibling).
    public readonly Dictionary<string, List<(string Owner, string RecvKey)>> TopLevelStatics = new(StringComparer.Ordinal);
    // A defaulted parameter's default-value expression as BIR (from @KotlinDefault), for CROSS-MODULE splice of an
    // omitted argument. Keyed "ownerFqn|methodName|paramCount" -> (argPosition -> BIR-json string). The DefaultArgSplice
    // pass reads this to fill trailing omitted args BEFORE the CharSequence bridge + type lowering (so a String default
    // is coerced exactly like an explicit arg). Rides the ref.dll only (param attrs stripped in the rt build).
    public readonly Dictionary<string, Dictionary<int, string>> KotlinDefaults = new(StringComparer.Ordinal);

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
// @ClrIntrinsic BCL name or null (null + no @ClrProperty + !IsAbstract = a rule-3 hoist candidate). PropertyName (+ the
// READ/WRITE access flags) is set when the member carries @ClrProperty — an EXPLICIT .NET property accessor binding.
sealed record MemberBinding(string Owner, string Name, int ParamCount, string Intrinsic, bool IsAbstract, bool IsStatic, string[] ParamTypes = null, int PropertyAccess = 0, string PropertyName = null, int[] ByrefPositions = null);

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
                // STEP-1 clrName migration: kotc emits a pure-Kotlin `overrides` marker (the override closure) so a
                // future bir2cir decl-rename pass can derive BCL slot names from the ref.dll @ClrIntrinsic. It is
                // bir2cir-internal metadata — strip it here so it never reaches the CIR/ilemit (keeps emit byte-identical).
                if (kv.Key == "overrides") continue;
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
// GAP A — the for-loop iterator protocol over a referenced (rt-dll) collection. kotc desugars `for (x in xs)` to a
// `<iterator>` var initialized by the stdlib bridge `kotlin.collections.ClrIteratorBridgeKt.iteratorOverEnumerable`
// (which RETURNS the real generic `kotlin.collections.Iterator<E>`), then routes hasNext/next to a synthetic
// monomorphized `<>dotkt_KIterator_<elem>` interface kotc emits into the app — a legacy "IL can't define a generic
// interface" workaround, now false since the rt dll defines `Iterator`1`. In an APP build that synthetic owner (and
// the `@kotlin.collections.Iterator` var type) KeyNotFounds in ilemit's `_types` (they're referenced, not emitted).
// Re-point BOTH at the real referenced generic `clrg:kotlin.collections.Iterator[E]` so ilemit resolves hasNext/next
// by reflection against the runtime stdlib — symmetric to how the List local already lowers to IReadOnlyList. The
// element type comes from the bridge call's typeArgs (still in the source vocabulary; the later type-lowering pass
// lowers the inner). Scoped per method (the `<iterator>` name is per-loop synthetic); the stdlib self-build is gated
// OFF at the call site (it emits Iterator itself). The now-unreferenced synthetic interface emits as harmless dead
// metadata. Producer-side (`class C : Iterator<T>`) is a separate, deeper gap and is intentionally not touched here.
static class IteratorConsumerNormalization
{
    const string Bridge = "kotlin.collections.ClrIteratorBridgeKt";
    const string SynthPrefix = "<>dotkt_KIterator_";

    public static void Apply(JsonNode root) => Process(root, new Dictionary<string, string>(StringComparer.Ordinal));

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    // A SINGLE document-order walk: a `var <name>` initialized by the bridge records name->elem (and retypes the var)
    // BEFORE its for-loop body is reached, so a hasNext/next on that local is rewritten with the elem current at that
    // point. This is order-sensitive on purpose: sibling/nested for-loops reuse the synthetic `<iterator>` name with
    // DIFFERENT element types, and each loop's body sits AFTER its own var-decl and BEFORE the next one — so the
    // forward walk always rewrites a call with its own loop's element (a two-pass collect-then-rewrite conflated them).
    static void Process(JsonNode node, Dictionary<string, string> map)
    {
        if (node is JsonObject obj)
        {
            var k = Str(obj["k"]);
            if (k == "var" && Str(obj["name"]) is string vn && obj["init"] is JsonObject init &&
                Str(init["k"]) == "callStatic" && Str(init["owner"]) == Bridge &&
                Str(init["method"]) == "iteratorOverEnumerable" &&
                init["typeArgs"] is JsonArray ta && ta.Count == 1 && Str(ta[0]) is string elem)
            {
                map[vn] = elem;
                obj["type"] = "clrg:kotlin.collections.Iterator[" + elem + "]";
            }
            // A hasNext/next `callInstance` whose synthetic owner addresses one of those iterator locals -> a
            // `clrInstance` on the real referenced generic interface. callInstance routes through ResolveMethod/
            // ParseOwner (an EMITTED-type `_types` lookup that KeyNotFounds on a clrg: owner); the CLR-bound member path
            // is `clrInstance` (EmitClrCall), exactly how the substituted IReadOnlyList's get_Item/get_Count resolve.
            // next() returns the element, hasNext() returns Boolean; argTypes are empty. `type`/`ret` stay in the source
            // vocabulary — the later type-lowering pass lowers them.
            else if (k == "callInstance" && Str(obj["ownerType"]) is string owner &&
                owner.StartsWith(SynthPrefix, StringComparison.Ordinal) && obj["recv"] is JsonObject recv &&
                Str(recv["k"]) == "local" && Str(recv["name"]) is string rn && map.TryGetValue(rn, out var e))
            {
                var method = Str(obj["method"]);
                obj["k"] = "clrInstance";
                obj.Remove("ownerType");
                obj.Remove("virtual");
                obj["type"] = "clrg:kotlin.collections.Iterator[" + e + "]";
                obj["method"] = method;
                obj["argTypes"] = new JsonArray();
                obj["ret"] = method == "next" ? e : "kotlin.Boolean";
            }
            foreach (var kv in obj) if (kv.Value != null) Process(kv.Value, map);
        }
        else if (node is JsonArray arr)
            foreach (var it in arr) if (it != null) Process(it, map);
    }
}

// STRING -> CharSequence adapter bridge. `kotlin.String` is @ClrTypeAlias("System.String") — a SEALED BCL type whose
// CharSequence face is bound in-place (@ClrIntrinsic Length/get_Chars). `kotlin.CharSequence` has NO BCL equivalent, so
// kotc synthesizes the monomorphic interface `<>dotkt_CharSequence` (get_length/get/subSequence). A `System.String`
// (sealed) cannot implement that interface, so a bare String flowing into a `@<>dotkt_CharSequence` slot crashes
// (InvalidProgram / InvalidCast). This pass MATERIALIZES the coercion: wherever a value whose STATIC type is String
// flows into a CharSequence slot, it inserts `new <>dotkt_StringCharSequence(theString)` — an App-local adapter class
// this pass ALSO injects, modeled on the proven user `class S : CharSequence` shape (String-backed length/get/
// subSequence delegating to get_Length/get_Chars/Substring). Five sites — a call's CharSequence-typed arg (covers an
// extension receiver, which is arg[0] + sig[0], AND an ordinary CharSequence param), a return into a CharSequence
// return type, a store into a CharSequence-typed local, and an `as CharSequence` cast. It wraps ONLY when the value is
// POSITIVELY a bare String (const string literal, a String-typed local/param read, a String cast, or a String-returning
// call) — never when the value is already a <>dotkt_CharSequence (StringBuilder / a user CharSequence / another
// wrapper), so it is purely additive: genuine intra-assembly polymorphism (`val cs: CharSequence = "abc"; cs.length`)
// now works, and no existing statically-String-receiver path (kotc's STRING_OPS lowering, which dispatches on the
// String directly) is touched.
//
// WHY app-LOCAL (not a stdlib class): the synthetic `<>dotkt_CharSequence` is emitted PER-ASSEMBLY — the app defines
// its OWN copy, distinct from the one in the rt stdlib dll. A stdlib adapter would implement the rt-dll copy, which the
// app's interface dispatch (`callvirt <app>::<>dotkt_CharSequence::get_length`) can't find on it -> EntryPointNotFound.
// So the adapter MUST implement the app's own synthetic -> it is injected into the app assembly, exactly where kotc
// injects the synthetic interface. (This same per-assembly boundary is why calling a *stdlib* CharSequence-extension
// with an app value is a SEPARATE, deeper blocker for the retire-B follow-up — see docs/master-task-inventory.md 4-A.)
//
// APP builds ONLY (gated on attributeTopLevelOwner at the call site — DOTKT_STDLIB_COMPILE unset), so the ref/rt stdlib
// self-builds stay byte-identical. Runs AFTER MemberCallSubstitution (its emitted `new` is never re-substituted — the
// adapter is not @ClrTypeAlias) and BEFORE BirTypeLowering (so it still sees the kotlin.* / @<>dotkt_CharSequence type
// vocabulary; the injected type's kotlin.* signature tokens and the wrap node's `type`/`argTypes` are lowered
// afterwards — the injected method bodies are already in CLR-call form, exactly as kotc emits them for `class S`).
// CROSS-MODULE DEFAULT-ARGUMENT SPLICE. A call that OMITS a defaulted argument reaches bir2cir with fewer args than
// the callee's signature (kotc emitted only the provided args — correct). For a callee whose defaulted params carry
// @KotlinDefault (a non-null object/CharSequence default the frontend jar dropped + .NET [DefaultParameterValue]
// metadata cannot carry), this pass reads the default-expression BIR from the ref.dll and SPLICES it as each trailing
// omitted argument. Runs in the app build AFTER MemberCallSubstitution (owner attributed, so the ref.dll callee is
// identifiable) and BEFORE StringCharSequenceBridge + BirTypeLowering (so a spliced String default is CharSequence-
// coerced and type-lowered exactly like an explicit argument). Mirrors the [KotlinInline] body-splice mechanism, but
// for default arguments. Callees with only metadata-representable defaults carry no @KotlinDefault -> untouched (their
// omitted args still ride ilemit's [DefaultParameterValue] backfill). Omission is TRAILING (kotc emits positional
// cross-module calls); a default expression that references earlier params is out of scope (the stdlib RC1 defaults
// are all self-contained constants) — a mixed/gap map bails, leaving the call unchanged.
static class DefaultArgSplice
{
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs) => Walk(root, refs);

    static void Walk(JsonNode node, ReferenceMetadataIndex refs)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value, refs);
            TrySplice(obj, refs);
        }
        else if (node is JsonArray arr) foreach (var it in arr) if (it != null) Walk(it, refs);
    }

    static void TrySplice(JsonObject node, ReferenceMetadataIndex refs)
    {
        var k = Str(node["k"]);
        if (k != "callStatic" && k != "callInstance") return;
        if (node["args"] is not JsonArray args || Str(node["sig"]) is not string sig) return;
        var sigCount = SplitTopLevel(sig).Count;
        if (args.Count >= sigCount) return;                              // no omitted arg
        var owner = Str(node["owner"]) ?? Str(node["ownerType"]);
        var method = Str(node["method"]);
        if (owner == null || method == null) return;
        var defaults = refs.KotlinDefaultsFor(owner, method, sigCount);
        if (defaults == null) return;
        var spliced = new List<JsonNode>();
        for (var pos = args.Count; pos < sigCount; pos++)
        {
            if (!defaults.TryGetValue(pos, out var bir)) return;         // gap -> bail (leave the call unchanged)
            JsonNode parsed; try { parsed = JsonNode.Parse(bir); } catch { return; }
            spliced.Add(parsed);
        }
        foreach (var n in spliced) args.Add(n);
    }

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    static IReadOnlyList<string> SplitTopLevel(string value)
    {
        var result = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '[' || c == '<' || c == '(') depth++;
            else if (c == ']' || c == '>' || c == ')') depth--;
            else if (c == ',' && depth == 0) { result.Add(value[start..i].Trim()); start = i + 1; }
        }
        result.Add(value[start..].Trim());
        return result;
    }
}

// CHARSEQUENCE -> System.String (docs/design-charsequence-clr-string.md, the 3-point model). `kotlin.CharSequence` is
// a JVM-shaped polymorphic char view with no faithful .NET equivalent; on the CLR DotKt models it as `string` (an
// immutable snapshot). kotc emits it as the synthetic monomorphic interface `<>dotkt_CharSequence` in every type
// position. In a "pure" APP assembly (no user `class S : CharSequence` — verified by the driver's hasUserCharSeqImpl)
// this pass collapses that synthetic to `System.String`:
//   ① a CharSequence-typed param / return / local / field DECLARATION -> System.String (via kotlin.String, which the
//      subsequent BirTypeLowering renders as the CLR `string`);
//   member reads on such a now-`string` value — `cs.length` / `cs[i]` / `cs.subSequence(a,b)` (emitted by kotc as a
//      callInstance whose ownerType is the synthetic) -> System.String.Length / get_Chars / Substring(a, b-a);
//   ② a NON-String value (a StringBuilder) flowing into a now-`string` slot (a local call's CharSequence arg, a
//      CharSequence-return, an `as CharSequence` cast, a CharSequence-local init) -> an implicit `.toString()` snapshot
//      (an `objMethod ToString`, virtual — StringBuilder's override yields its content). A String flows directly.
// It touches ONLY this assembly's own declarations + LOCAL calls (a top-level fn in localTopLevelFns) + member reads on
// the synthetic; a call to an EXTERNAL stdlib CharSequence-extension keeps its synthetic `sig` untouched so the
// following StringCharSequenceBridge still adapter-wraps the (now-`string`) argument for the un-rebuilt stdlib. Lowering
// the STDLIB's own CharSequence-ext params to `string` (which would let the retire-B string ops route cleanly) needs a
// stdlib rebuild + a cross-assembly call-site coercion and is a documented follow-up — NOT done here.
static class CharSeqStringLowering
{
    const string CharSeq = "<>dotkt_CharSequence";
    static readonly HashSet<string> StringTokens = new(StringComparer.Ordinal)
        { "kotlin.String", "System.String", "string" };

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    // Strip a leading `nullable:`/`array:` modifier then a `@` (this-assembly-emitted) marker, so `@<>dotkt_CharSequence`
    // / `nullable:<>dotkt_CharSequence` compare by bare identity.
    static string Bare(string t)
    {
        if (t == null) return null;
        t = t.Trim();
        foreach (var p in new[] { "nullable:", "array:" })
            if (t.StartsWith(p, StringComparison.Ordinal)) t = t[p.Length..];
        if (t.StartsWith("@", StringComparison.Ordinal)) t = t[1..];
        return t;
    }

    static bool IsCharSeq(string t) => Bare(t) == CharSeq;
    static bool IsStringTok(string t) => Bare(t) is string b && StringTokens.Contains(b);

    // Replace a CharSequence type token with `kotlin.String` (BirTypeLowering renders it as `string`), preserving a
    // leading `nullable:`/`array:` modifier; drops the `@` (String is foundational, not this-assembly-emitted).
    static string LowerTok(string t)
    {
        if (t == null) return null;
        foreach (var p in new[] { "nullable:", "array:" })
            if (t.StartsWith(p, StringComparison.Ordinal)) return p + LowerTok(t[p.Length..]);
        return "kotlin.String";
    }

    // Lexical name -> declared type (params + local vars, with CharSequence already mapped to kotlin.String), plus
    // whether the enclosing method's return type was CharSequence. Copy-on-extend (mirrors StringCharSequenceBridge.Env).
    sealed class Env
    {
        public readonly Dictionary<string, string> Vars;
        public readonly bool RetWasCharSeq;
        public Env() { Vars = new(StringComparer.Ordinal); RetWasCharSeq = false; }
        Env(Dictionary<string, string> vars, bool ret) { Vars = vars; RetWasCharSeq = ret; }

        public Env WithDecl(JsonObject decl)
        {
            if (decl["params"] is not JsonArray ps) return this;
            var vars = new Dictionary<string, string>(Vars, StringComparer.Ordinal);
            foreach (var p in ps)
                if (p is JsonObject po && Str(po["name"]) is string pn && Str(po["type"]) is string pt)
                    vars[pn] = IsCharSeq(pt) ? "kotlin.String" : pt;
            var ret = decl["ret"] is JsonValue rv && rv.TryGetValue<string>(out var rs) ? IsCharSeq(rs) : RetWasCharSeq;
            return new Env(vars, ret);
        }

        public Env WithVar(string name, string type)
        {
            var vars = new Dictionary<string, string>(Vars, StringComparer.Ordinal) { [name] = type };
            return new Env(vars, RetWasCharSeq);
        }
    }

    static HashSet<string> _localFns = new(StringComparer.Ordinal);

    public static JsonNode Apply(JsonNode root, HashSet<string> localTopLevelFns)
    {
        _localFns = localTopLevelFns ?? new HashSet<string>(StringComparer.Ordinal);
        return Walk(root, new Env());
    }

    static JsonNode Walk(JsonNode node, Env env)
    {
        if (node is JsonObject obj)
        {
            var childEnv = env.WithDecl(obj);
            var copy = new JsonObject();
            foreach (var kv in obj)
                copy[kv.Key] = kv.Value is JsonArray arr ? WalkArray(arr, childEnv)
                             : kv.Value == null ? null : Walk(kv.Value, childEnv);
            return Transform(copy, env);
        }
        if (node is JsonArray topArr) return WalkArray(topArr, env);
        return node.DeepClone();
    }

    // Thread each `var` decl's (already-lowered) name->type forward so a later sibling's read resolves its static type.
    static JsonArray WalkArray(JsonArray arr, Env env)
    {
        var copy = new JsonArray();
        var cur = env;
        foreach (var item in arr)
        {
            var walked = item == null ? null : Walk(item, cur);
            copy.Add(walked);
            if (walked is JsonObject wo && Str(wo["k"]) == "var"
                && Str(wo["name"]) is string vn && Str(wo["type"]) is string vt)
                cur = cur.WithVar(vn, IsCharSeq(vt) ? "kotlin.String" : vt);
        }
        return copy;
    }

    static JsonNode Transform(JsonObject node, Env env)
    {
        var k = Str(node["k"]);

        // A member READ on a CharSequence value (kotc: callInstance whose ownerType is the synthetic). A stdlib
        // CharSequence-EXTENSION is a callStatic (receiver as arg[0]), never this shape, so this only ever hits the
        // synthetic interface's own length/get/subSequence.
        if (k == "callInstance" && IsCharSeq(Str(node["ownerType"])))
        {
            var rewritten = RewriteMemberRead(node);
            if (rewritten != null) return rewritten;
        }

        switch (k)
        {
            case null:   // a declaration node (method/lambda def, field): lower its own signature tokens
                LowerDeclTypes(node);
                return node;
            case "var":
                if (node["type"] is JsonValue vt && vt.TryGetValue<string>(out var vts) && IsCharSeq(vts))
                {
                    node["type"] = LowerTok(vts);
                    if (node["init"] is JsonNode init && CoerceOrNull(init, env) is JsonNode w) node["init"] = w;
                }
                return node;
            case "callStatic":
                LowerLocalCall(node, env);
                return node;
            case "return":
                if (env.RetWasCharSeq && node["value"] is JsonNode rvv && CoerceOrNull(rvv, env) is JsonNode rw)
                    node["value"] = rw;
                return node;
            case "cast":
                if (IsCharSeq(Str(node["type"])) && node["e"] is JsonNode ce)
                    return CoerceOrNull(ce, env) ?? ce.DeepClone();
                return node;
            default:
                return node;
        }
    }

    // Lower a declaration's own type tokens: params[].type, ret, and a bare `type` (a field). Never a call `sig`.
    static void LowerDeclTypes(JsonObject node)
    {
        if (node["params"] is JsonArray ps)
            foreach (var p in ps)
                if (p is JsonObject po && Str(po["type"]) is string pt && IsCharSeq(pt)) po["type"] = LowerTok(pt);
        if (Str(node["ret"]) is string ret && IsCharSeq(ret)) node["ret"] = LowerTok(ret);
        if (node["k"] == null && Str(node["type"]) is string ft && IsCharSeq(ft) && node["name"] != null)
            node["type"] = LowerTok(ft);   // a field {name,type}
    }

    // A LOCAL top-level call (owner null, method in this assembly): lower each CharSequence `sig` slot to kotlin.String
    // and coerce the matching arg (a non-String value -> implicit .toString()). An EXTERNAL stdlib call (attributed
    // owner, or a name absent from localTopLevelFns) is left untouched -> the StringCharSequenceBridge handles it.
    static void LowerLocalCall(JsonObject node, Env env)
    {
        if (node["owner"] is JsonValue ov && ov.TryGetValue<string>(out _)) return;   // attributed -> external
        if (Str(node["method"]) is not string method || !_localFns.Contains(method)) return;
        if (Str(node["sig"]) is not string sig) return;
        var parts = SplitTopLevel(sig).ToList();
        var args = node["args"] as JsonArray;
        var changed = false;
        for (var i = 0; i < parts.Count; i++)
            if (IsCharSeq(parts[i]))
            {
                parts[i] = LowerTok(parts[i]);
                changed = true;
                if (args != null && i < args.Count && args[i] is JsonNode a && CoerceOrNull(a, env) is JsonNode w)
                    args[i] = w;
            }
        if (changed) node["sig"] = string.Join(",", parts);
        if (Str(node["dynRet"]) is string dr && IsCharSeq(dr)) node["dynRet"] = LowerTok(dr);
    }

    // `cs.length` -> System.String.Length; `cs[i]` (get) -> get_Chars; `cs.subSequence(a,b)` -> Substring(a, b-a).
    // Structurally identical to the <>dotkt_StringCharSequence adapter's proven bodies. Returns null for an
    // unrecognized member (leave as-is).
    static JsonObject RewriteMemberRead(JsonObject node)
    {
        var recv = node["recv"];
        var args = node["args"] as JsonArray;
        switch (Str(node["method"]))
        {
            case "get_length":
                return new JsonObject
                {
                    ["k"] = "clrPropGet", ["type"] = "System.String", ["name"] = "Length",
                    ["retType"] = "System.Int32", ["static"] = false, ["recv"] = recv?.DeepClone(),
                };
            case "get":
                return new JsonObject
                {
                    ["k"] = "clrInstance", ["type"] = "System.String", ["method"] = "get_Chars",
                    ["argTypes"] = new JsonArray { "System.Int32" }, ["ret"] = "System.Char",
                    ["recv"] = recv?.DeepClone(),
                    ["args"] = new JsonArray { args != null && args.Count > 0 ? args[0].DeepClone() : null },
                };
            case "subSequence":
                if (args == null || args.Count < 2) return null;
                return new JsonObject
                {
                    ["k"] = "clrInstance", ["type"] = "System.String", ["method"] = "Substring",
                    ["argTypes"] = new JsonArray { "System.Int32", "System.Int32" }, ["ret"] = "System.String",
                    ["recv"] = recv?.DeepClone(),
                    ["args"] = new JsonArray
                    {
                        args[0].DeepClone(),
                        new JsonObject { ["k"] = "bin", ["op"] = "-", ["l"] = args[1].DeepClone(), ["r"] = args[0].DeepClone() },
                    },
                };
            default:
                return null;
        }
    }

    // A value flowing into a now-`string` slot: a provably-String value needs NO coercion (return null); anything else
    // (a StringBuilder, an Any) is snapshot via a virtual `.toString()` (objMethod ToString — the returned wrapper is a
    // fresh, detached node). Callers assign the wrapper only when non-null, avoiding a JsonNode reparenting error.
    static JsonNode CoerceOrNull(JsonNode value, Env env)
    {
        if (IsStaticString(value, env)) return null;
        return new JsonObject { ["k"] = "objMethod", ["method"] = "ToString", ["recv"] = value.DeepClone() };
    }

    // POSITIVE static-String detection (mirrors StringCharSequenceBridge.IsStaticString, extended with dynRet and the
    // already-rewritten clr* String result nodes).
    static bool IsStaticString(JsonNode n, Env env)
    {
        if (n is not JsonObject o) return false;
        switch (Str(o["k"]))
        {
            case "const": return IsStringTok(Str(o["type"]));
            case "local": return Str(o["name"]) is string nm && env.Vars.TryGetValue(nm, out var t) && IsStringTok(t);
            case "cast": return IsStringTok(Str(o["type"]));
            case "concat": return true;   // string concatenation
            case "this": return false;
            default:
                return IsStringTok(Str(o["ret"]) ?? Str(o["retType"]) ?? Str(o["dynRet"]));
        }
    }

    static IReadOnlyList<string> SplitTopLevel(string value)
    {
        if (value.Length == 0) return Array.Empty<string>();
        var result = new List<string>();
        int depth = 0, start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is '[' or '<' or '(') depth++;
            else if (c is ']' or '>' or ')') depth--;
            else if (c == ',' && depth == 0) { result.Add(value[start..i].Trim()); start = i + 1; }
        }
        result.Add(value[start..].Trim());
        return result;
    }
}

static class StringCharSequenceBridge
{
    const string CharSeq = "<>dotkt_CharSequence";
    const string Adapter = "<>dotkt_StringCharSequence";
    static readonly HashSet<string> StringTokens = new(StringComparer.Ordinal)
        { "kotlin.String", "System.String", "string" };

    // Injected exactly once per app assembly (dedup below). Pre-BirTypeLowering vocabulary: kotlin.* signature tokens
    // (lowered by the next pass), CLR-call bodies (String.get_Chars/Length/Substring — the SAME shape kotc emits for a
    // user `class S(val s:String): CharSequence`). Structurally mirrors that verified S class, renamed s->value.
    const string AdapterTypeJson = """
    {
      "name": "<>dotkt_StringCharSequence",
      "kind": "class", "abstract": false, "vis": "public", "isSealed": false, "base": null,
      "interfaces": ["<>dotkt_CharSequence"],
      "fields": [{"name": "value", "type": "kotlin.String", "vis": "internal"}],
      "ctors": [{
        "params": [{"name": "value", "type": "kotlin.String"}],
        "baseArgs": null, "thisArgs": null, "vis": "public",
        "body": [{"k": "setField", "ownerType": "<>dotkt_StringCharSequence", "recv": {"k": "this"}, "name": "value", "value": {"k": "local", "name": "value"}}]
      }],
      "methods": [
        {"name": "get", "static": false, "override": false, "virtual": true, "abstract": false, "objectOverride": false, "vis": "public", "operator": true,
         "params": [{"name": "index", "type": "kotlin.Int"}], "ret": "kotlin.Char",
         "body": [{"k": "return", "value": {"k": "clrInstance", "type": "System.String", "method": "get_Chars", "argTypes": ["System.Int32"], "ret": "System.Char",
           "recv": {"k": "callInstance", "ownerType": "<>dotkt_StringCharSequence", "virtual": false, "recv": {"k": "this"}, "method": "get_value", "args": []},
           "args": [{"k": "local", "name": "index"}]}}], "attrs": []},
        {"name": "subSequence", "static": false, "override": false, "virtual": true, "abstract": false, "objectOverride": false, "vis": "public",
         "params": [{"name": "startIndex", "type": "kotlin.Int"}, {"name": "endIndex", "type": "kotlin.Int"}], "ret": "@<>dotkt_CharSequence",
         "body": [{"k": "return", "value": {"k": "new", "type": "<>dotkt_StringCharSequence", "argTypes": ["kotlin.String"],
           "args": [{"k": "clrInstance", "type": "System.String", "method": "Substring", "argTypes": ["System.Int32", "System.Int32"], "ret": "System.String",
             "recv": {"k": "callInstance", "ownerType": "<>dotkt_StringCharSequence", "virtual": false, "recv": {"k": "this"}, "method": "get_value", "args": []},
             "args": [{"k": "local", "name": "startIndex"}, {"k": "bin", "op": "-", "l": {"k": "local", "name": "endIndex"}, "r": {"k": "local", "name": "startIndex"}}]}]}}], "attrs": []},
        {"name": "get_value", "static": false, "override": false, "virtual": false, "abstract": false, "objectOverride": false, "vis": "public",
         "params": [], "ret": "kotlin.String",
         "body": [{"k": "return", "value": {"k": "field", "ownerType": "<>dotkt_StringCharSequence", "recv": {"k": "this"}, "name": "value"}}]},
        {"name": "get_length", "static": false, "override": true, "virtual": true, "abstract": false, "objectOverride": false, "vis": "public",
         "params": [], "ret": "kotlin.Int",
         "body": [{"k": "return", "value": {"k": "clrPropGet", "type": "System.String", "name": "Length", "retType": "System.Int32", "static": false,
           "recv": {"k": "callInstance", "ownerType": "<>dotkt_StringCharSequence", "virtual": false, "recv": {"k": "this"}, "method": "get_value", "args": []}}}]},
        {"name": "ToString", "static": false, "override": true, "virtual": true, "abstract": false, "objectOverride": true, "vis": "public",
         "params": [], "ret": "kotlin.String",
         "body": [{"k": "return", "value": {"k": "field", "ownerType": "<>dotkt_StringCharSequence", "recv": {"k": "this"}, "name": "value"}}]}
      ],
      "properties": [
        {"name": "value", "type": "kotlin.String", "get": "get_value", "set": null},
        {"name": "length", "type": "kotlin.Int", "get": "get_length", "set": null}
      ],
      "attrs": []
    }
    """;

    // Process-wide: the app-local adapter type is emitted into EXACTLY ONE file's `types` per assembly (all of an app's
    // BIR files are lowered by a single bir2cir process; other files that also wrap resolve the type assembly-wide via
    // ilemit's `_types`). Fresh per process; app builds only. `_fired` tracks whether the file just walked wrapped.
    static bool _adapterEmitted;
    static bool _fired;

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    // A lexical name -> declared-type environment (method/lambda params + local `var` decls), plus the enclosing
    // method's return type (for the return-site wrap). Copy-on-extend so a child scope never mutates its parent.
    sealed class Env
    {
        public readonly Dictionary<string, string> Vars;
        public readonly string RetType;
        public Env() { Vars = new(StringComparer.Ordinal); RetType = null; }
        Env(Dictionary<string, string> vars, string retType) { Vars = vars; RetType = retType; }

        // A declaration node (has a `params` array — methods/lambdas always emit one, even empty) opens a child scope
        // seeded with its params and return type. A non-decl node (call/expr — no `params`) returns `this` unchanged.
        public Env WithDecl(JsonObject decl)
        {
            if (decl["params"] is not JsonArray ps) return this;
            var vars = new Dictionary<string, string>(Vars, StringComparer.Ordinal);
            foreach (var p in ps)
                if (p is JsonObject po && Str(po["name"]) is string pn && Str(po["type"]) is string pt)
                    vars[pn] = pt;
            return new Env(vars, Str(decl["ret"]) ?? RetType);
        }

        public Env WithVar(string name, string type)
        {
            var vars = new Dictionary<string, string>(Vars, StringComparer.Ordinal) { [name] = type };
            return new Env(vars, RetType);
        }
    }

    public static JsonNode Apply(JsonNode root)
    {
        _fired = false;
        var walked = Walk(root, new Env());
        // Emit the app-local adapter type into this file's `types` if a wrap fired here and no other file already got
        // it (one per assembly). ilemit resolves a wrap in a sibling file against it via the assembly-wide `_types`.
        if (_fired && !_adapterEmitted && walked is JsonObject fileObj)
        {
            var types = fileObj["types"] as JsonArray;
            if (types == null) { types = new JsonArray(); fileObj["types"] = types; }
            types.Add(JsonNode.Parse(AdapterTypeJson));
            _adapterEmitted = true;
        }
        return walked;
    }

    static JsonNode Walk(JsonNode node, Env env)
    {
        if (node is JsonObject obj)
        {
            var childEnv = env.WithDecl(obj);
            var copy = new JsonObject();
            foreach (var kv in obj)
                copy[kv.Key] = kv.Value is JsonArray arr ? WalkArray(arr, childEnv)
                             : kv.Value == null ? null : Walk(kv.Value, childEnv);
            return Transform(copy, env);   // this node's own coercion sites use its ENCLOSING env
        }
        if (node is JsonArray topArr) return WalkArray(topArr, env);
        return node.DeepClone();
    }

    // Walk an array's elements in document order, threading each `var` decl's name->type forward so a LATER sibling
    // statement's read of that local resolves its static type (a `var`'s own init is walked BEFORE the var is added,
    // so `val x = <x>` can't see itself). Non-body arrays (args/params/…) contain no `var` nodes, so this is a no-op
    // for them.
    static JsonArray WalkArray(JsonArray arr, Env env)
    {
        var copy = new JsonArray();
        var cur = env;
        foreach (var item in arr)
        {
            var walked = item == null ? null : Walk(item, cur);
            copy.Add(walked);
            if (walked is JsonObject wo && Str(wo["k"]) == "var"
                && Str(wo["name"]) is string vn && Str(wo["type"]) is string vt)
                cur = cur.WithVar(vn, vt);
        }
        return copy;
    }

    static JsonNode Transform(JsonObject node, Env env)
    {
        switch (Str(node["k"]))
        {
            case "callStatic":
            case "callInstance":
                WrapCallArgs(node, env);
                return node;
            case "var":
                WrapVarInit(node, env);
                return node;
            case "return":
                WrapReturn(node, env);
                return node;
            case "cast":
                return WrapCast(node, env) ?? node;
            default:
                return node;
        }
    }

    // (a)+(b): a call arg whose DECLARED slot (positional in `sig`, the comma-joined param types with the extension
    // receiver first) is a CharSequence and whose value is statically a String. `sig` may be LONGER than `args` when
    // trailing defaulted params were dropped — pair only the present args.
    static void WrapCallArgs(JsonObject node, Env env)
    {
        if (node["args"] is not JsonArray args || Str(node["sig"]) is not string sig) return;
        var parts = SplitTopLevel(sig);
        var n = Math.Min(parts.Count, args.Count);
        for (var i = 0; i < n; i++)
            if (IsCharSeqSlot(parts[i]) && args[i] is JsonNode a && IsStaticString(a, env))
                args[i] = WrapAdapter(a);
    }

    // (d): a store into a CharSequence-typed local `var cs: CharSequence = <String>`.
    static void WrapVarInit(JsonObject node, Env env)
    {
        if (IsCharSeqSlot(Str(node["type"])) && node["init"] is JsonNode init && IsStaticString(init, env))
            node["init"] = WrapAdapter(init);
    }

    // (c): a return of a static String into a CharSequence return type.
    static void WrapReturn(JsonObject node, Env env)
    {
        if (IsCharSeqSlot(env.RetType) && node["value"] is JsonNode v && IsStaticString(v, env))
            node["value"] = WrapAdapter(v);
    }

    // (e): `as CharSequence` on a static String -> REPLACE the (would-be InvalidCast) `castclass <>dotkt_CharSequence`
    // with the materializing adapter. A non-statically-String cast (an `Any?`->CharSequence runtime check) is left as
    // the plain cast — a runtime-type-check adapter helper for that is a follow-up (see docs 【4-A】).
    static JsonNode WrapCast(JsonObject node, Env env)
    {
        if (IsCharSeqSlot(Str(node["type"])) && node["e"] is JsonNode e && IsStaticString(e, env))
            return WrapAdapter(e);
        return null;
    }

    // `new kotlin.StringCharSequence(<str>)`. Not @ClrTypeAlias, so MemberCallSubstitution.TransformNew (already run)
    // leaves it; BirTypeLowering lowers `type`/`argTypes` (kotlin.String -> System.String); ilemit reflects the ctor
    // against the runtime stdlib.
    static JsonObject WrapAdapter(JsonNode strExpr)
    {
        _fired = true;   // request the app-local adapter type injection for this file (Apply)
        return new JsonObject
        {
            ["k"] = "new",
            ["type"] = Adapter,
            ["argTypes"] = new JsonArray { "kotlin.String" },
            ["args"] = new JsonArray { strExpr.DeepClone() },
        };
    }

    // POSITIVE static-String detection: only forms whose static type is provably a bare String. Anything else (a
    // StringBuilder, a user CharSequence, an already-wrapped value, an unknown expr) returns false -> no wrap.
    static bool IsStaticString(JsonNode n, Env env)
    {
        if (n is not JsonObject o) return false;
        switch (Str(o["k"]))
        {
            case "const": return IsStringTok(Str(o["type"]));
            case "local": return Str(o["name"]) is string nm && env.Vars.TryGetValue(nm, out var t) && IsStringTok(t);
            case "cast": return IsStringTok(Str(o["type"]));
            case "this": return false;
            default:
                // A CLR/Kotlin call node carrying an explicit result type (`ret`/`retType` = System.String).
                return IsStringTok(Str(o["ret"]) ?? Str(o["retType"]));
        }
    }

    static bool IsStringTok(string t) => Bare(t) is string b && StringTokens.Contains(b);
    static bool IsCharSeqSlot(string t) => Bare(t) == CharSeq;

    // Strip a leading `nullable:` then `@` (the this-assembly-emitted marker) so `@<>dotkt_CharSequence` /
    // `nullable:kotlin.String` compare by their bare identity.
    static string Bare(string t)
    {
        if (t == null) return null;
        t = t.Trim();
        if (t.StartsWith("nullable:", StringComparison.Ordinal)) t = t["nullable:".Length..];
        if (t.StartsWith("@", StringComparison.Ordinal)) t = t[1..];
        return t;
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

// Erase a nullable generic-parameter return (`fun <T> …(): T?`, kotc-lowered to `ret=gp:X` + `retNullable=true`)
// to a `System.Object` return — the only CLR representation of a generic `T?` that can carry a real null for a
// VALUE-type instantiation. The method body's `ldnull` (null case) then stays a genuine null; value returns are
// boxed by ilemit's return/cond emitters; and the CALL boundary (ilemit) converts the object back to the caller's
// statically-known Nullable<V> (unbox.any) or reference type (castclass). Runs in EVERY build so the ref.dll and
// rt.dll signatures — and the app's view of them — agree. A no-op for a method that is not a nullable-generic return.
static class NullableGenericReturnErasure
{
    public static void Apply(JsonNode root)
    {
        if (root is not JsonObject o) return;
        if (o["methods"] is JsonArray methods)
            foreach (var m in methods) ApplyToMethod(m);
        // Nested types (a generic class' member methods) carry their own method list.
        if (o["types"] is JsonArray types)
            foreach (var t in types) Apply(t);
    }

    static void ApplyToMethod(JsonNode m)
    {
        if (m is not JsonObject mo) return;
        var ret = (mo["ret"] as JsonValue)?.TryGetValue<string>(out var rs) == true ? rs : null;
        if (ret == null || !ret.StartsWith("gp:", StringComparison.Ordinal)) return;
        if ((mo["retNullable"] as JsonValue)?.TryGetValue<bool>(out var rn) != true || !rn) return;
        mo["ret"] = "object";
        // A return-value expression whose STATIC type is the (now-erased) `gp:X` must also flow as object so its
        // null/value coercion targets object: a `return (cond typed gp:X)` (if-empty-null-else-elem) and a
        // `return (delegating call retType=gp:X)` (find -> firstOrNull) both become object end-to-end.
        RetypeReturns(mo["body"], ret);
    }

    static void RetypeReturns(JsonNode node, string gp)
    {
        switch (node)
        {
            case JsonObject obj:
                if ((obj["k"] as JsonValue)?.TryGetValue<string>(out var k) == true && k == "return"
                    && obj["value"] is JsonObject v)
                {
                    if ((v["type"] as JsonValue)?.TryGetValue<string>(out var vt) == true && vt == gp) v["type"] = "object";
                    if ((v["retType"] as JsonValue)?.TryGetValue<string>(out var vr) == true && vr == gp) v["retType"] = "object";
                }
                foreach (var kv in obj) RetypeReturns(kv.Value, gp);
                break;
            case JsonArray arr:
                foreach (var it in arr) RetypeReturns(it, gp);
                break;
        }
    }
}

static class MemberCallSubstitution
{
    // Top-level fun names DEFINED in the current compilation (this assembly's file-class statics). A `callStatic
    // owner=null` to one of these stays owner-less (ilemit's FindStatic finds the local sibling) — only a name NOT
    // defined here is a candidate for referenced-stdlib owner attribution. Single-threaded per run, so static is fine.
    static IReadOnlySet<string> _localTopLevelFns = new HashSet<string>(StringComparer.Ordinal);
    // Whether to attribute referenced top-level stdlib funs to their file-class owner (APP build only; OFF for the
    // stdlib self-build, where every such fun is local — see DOTKT_STDLIB_COMPILE gate at the call site in the Driver).
    static bool _attributeTopLevelOwner;

    public static JsonNode Apply(JsonNode root, ReferenceMetadataIndex refs,
        IReadOnlySet<string> localTopLevelFns, bool attributeTopLevelOwner)
    {
        _localTopLevelFns = localTopLevelFns;
        _attributeTopLevelOwner = attributeTopLevelOwner;
        return Rewrite(root, refs, new SubstCtx());
    }

    // Lexical type environment carried DOWN the walk: a name->type-token map for the enclosing decl's params, and a
    // type-param-name->constraint-tokens map for its generic parameters. Populated at each declaration node (anything
    // carrying `params`/`typeParams`) so a call site can recover its receiver's STATIC type — needed to route a call
    // whose receiver is a generic parameter (`destination: C where C : MutableCollection<R>`) through constrained
    // dispatch instead of a plain callvirt on a padded ICollection<object> owner (which mis-dispatches; see Constrainify).
    sealed class SubstCtx
    {
        public readonly Dictionary<string, string> VarTypes;
        public readonly Dictionary<string, List<string>> TpConstraints;
        public SubstCtx()
        {
            VarTypes = new Dictionary<string, string>(StringComparer.Ordinal);
            TpConstraints = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }
        SubstCtx(SubstCtx parent)
        {
            VarTypes = new Dictionary<string, string>(parent.VarTypes, StringComparer.Ordinal);
            TpConstraints = new Dictionary<string, List<string>>(parent.TpConstraints, StringComparer.Ordinal);
        }
        // A child scope extended with this declaration's params + generic-parameter constraints. Returns `this`
        // unchanged when the node introduces no bindings (so plain nodes don't allocate a scope).
        public SubstCtx Extend(JsonObject decl)
        {
            var ps = decl["params"] as JsonArray;
            var tps = decl["typeParams"] as JsonArray;
            if ((ps == null || ps.Count == 0) && (tps == null || tps.Count == 0)) return this;
            var child = new SubstCtx(this);
            if (ps != null)
                foreach (var p in ps)
                    if (p is JsonObject po && (po["name"] as JsonValue)?.GetValue<string>() is string pn
                        && (po["type"] as JsonValue)?.GetValue<string>() is string pt)
                        child.VarTypes[pn] = pt;
            if (tps != null)
                foreach (var tp in tps)
                    if (tp is JsonObject to && (to["name"] as JsonValue)?.GetValue<string>() is string tn
                        && to["constraints"] is JsonArray cs)
                        child.TpConstraints[tn] = cs.Select(c => (c as JsonValue)?.GetValue<string>())
                                                    .Where(c => c != null).ToList();
            return child;
        }
    }

    static JsonNode Rewrite(JsonNode node, ReferenceMetadataIndex refs, SubstCtx ctx)
    {
        if (node is JsonObject obj)
        {
            var childCtx = ctx.Extend(obj);   // params/typeParams of THIS decl scope its children (the body / sub-exprs)
            var copy = new JsonObject();
            foreach (var kv in obj)
                copy[kv.Key] = kv.Value == null ? null : Rewrite(kv.Value, refs, childCtx);   // children first (bottom-up)
            return Transform(copy, refs, childCtx);
        }
        if (node is JsonArray arr)
        {
            var copy = new JsonArray();
            foreach (var item in arr) copy.Add(item == null ? null : Rewrite(item, refs, ctx));
            return copy;
        }
        return node.DeepClone();
    }

    static JsonNode Transform(JsonObject node, ReferenceMetadataIndex refs, SubstCtx ctx)
    {
        return (node["k"] as JsonValue)?.GetValue<string>() switch
        {
            "new" => TransformNew(node, refs) ?? node,
            "callInstance" => TransformCall(node, refs, instance: true, ctx) ?? node,
            "callStatic" => TransformCall(node, refs, instance: false, ctx) ?? node,
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

        // Inline-class CONSTRUCTION erasure (the BOX, mirror of the `.data` unbox collapse): an @JvmInline value class
        // erases to its single backing field's primitive CLR form, so `new UByte(arg)` IS `arg` (no System.Byte(byte)
        // ctor exists). Collapse to the lone arg UNCHANGED — never a conv: the int32 stack bits are already the value,
        // and a signed conv (Conv_I1) would sign-extend and corrupt an unsigned high bit (UByte 200 -> -56). Width is
        // truncated/masked at the byte-typed store/use sites. (Codex-confirmed: identity, not conv.)
        if (refs.IsInlineValueClass(ReferenceMetadataIndex.BareOwnerFqn(ownerToken)) &&
            node["args"] is JsonArray ctorArgs && ctorArgs.Count == 1)
            return ctorArgs[0].DeepClone();

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
            ["argTypes"] = CtorArgTypes(node, args, refs, ownerToken),
            ["args"] = args.DeepClone(),
        };
    }

    // The clrNew's ctor-overload key. kotc emits the ctor's DECLARED param types on the `new` node's `argTypes`, but they
    // reference the class's OWN type parameters (`ArrayList<E>`'s copy ctor -> `Collection[gp:E]`). Substitute those with
    // the instantiation's type args (`ArrayList[kotlin.Int]` => E:=kotlin.Int) so the lowered argType is a RESOLVABLE,
    // precise overload key (`IReadOnlyCollection[int]`) — this disambiguates List's `IEnumerable<T>` ctor from its `int`
    // capacity ctor (a bare `object`/unbound-`gp:E` argType matches neither, so ilemit mis-picked `List(int)` ->
    // InvalidProgramException). Falls back to InferArgTypes when the node has no declared argTypes (older shape).
    static JsonArray CtorArgTypes(JsonObject node, JsonArray args, ReferenceMetadataIndex refs, string ownerToken)
    {
        if (node["argTypes"] is not JsonArray declared || declared.Count != args.Count)
            return InferArgTypes(node, args);
        var map = ClassTypeParamMap(refs, ownerToken);
        var result = new JsonArray();
        foreach (var a in declared)
        {
            var s = (a as JsonValue)?.GetValue<string>();
            result.Add(s == null ? a?.DeepClone() : SubstituteGenericParams(s, map));
        }
        return result;
    }

    // Positional map from a generic owner token's class type-param NAMES (from the ref.dll) to its instantiation args:
    // `kotlin.collections.ArrayList[kotlin.Int]` + names [E] => { "E" -> "kotlin.Int" }. Empty when the owner is
    // non-generic, unbound, or the ref.dll has no param names for it.
    static Dictionary<string, string> ClassTypeParamMap(ReferenceMetadataIndex refs, string ownerToken)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var br = ownerToken.IndexOf('[');
        if (br < 0 || !ownerToken.EndsWith("]", StringComparison.Ordinal)) return map;
        var names = refs.OwnerTypeParamNames(ReferenceMetadataIndex.BareOwnerFqn(ownerToken));
        if (names == null || names.Length == 0) return map;
        var targs = SplitTopLevel(ownerToken[(br + 1)..^1]).ToList();
        for (var i = 0; i < names.Length && i < targs.Count; i++) map[names[i]] = targs[i];
        return map;
    }

    // Replace each `gp:<name>` type token (a class type parameter) with its instantiation type, leaving unrelated
    // generic params (a METHOD's own gp:T/gp:R, absent from the class map) untouched. Word-boundary-safe: a gp name is
    // an identifier terminated by `[`, `]`, `,`, or end.
    static string SubstituteGenericParams(string type, Dictionary<string, string> map)
    {
        if (map.Count == 0 || !type.Contains("gp:", StringComparison.Ordinal)) return type;
        var sb = new System.Text.StringBuilder(type.Length);
        for (var i = 0; i < type.Length;)
        {
            if (i + 3 <= type.Length && type[i] == 'g' && type[i + 1] == 'p' && type[i + 2] == ':')
            {
                var j = i + 3;
                while (j < type.Length && (char.IsLetterOrDigit(type[j]) || type[j] == '_')) j++;
                var name = type[(i + 3)..j];
                if (map.TryGetValue(name, out var repl)) { sb.Append(repl); i = j; continue; }
            }
            sb.Append(type[i]); i++;
        }
        return sb.ToString();
    }

    static JsonNode TransformCall(JsonObject node, ReferenceMetadataIndex refs, bool instance, SubstCtx ctx = null)
    {
        var ownerToken = (node[instance ? "ownerType" : "owner"] as JsonValue)?.GetValue<string>();
        if (string.IsNullOrEmpty(ownerToken))
        {
            // Top-level fun call (`callStatic owner=null`) bound by @ClrIntrinsic. Two shapes (sourced from the ref.dll):
            //   FQ "System.X.Y"  -> a fully-qualified BCL static: split at the last '.' -> clrStatic System.X.Y(args).
            //   bare "Name"      -> an EXTENSION receiver's instance method (`Array<T>.nativeClone()`@ClrIntrinsic("Clone")
            //                       -> recv.Clone()): clrInstance on the first arg (the extension receiver). The first
            //                       sig type is the receiver type; the rest are the method args.
            var fn = (node["method"] as JsonValue)?.GetValue<string>();
            if (instance || string.IsNullOrEmpty(fn)) return null;
            var args0 = node["args"] as JsonArray ?? new JsonArray();
            var sigParts0 = SplitSig(node);
            // A top-level @ClrIntrinsic bound to a FQ BCL static. Resolve the EXACT overload by the call's full
            // ParamKey signature first (sqrt/abs/pow -> System.Math.* for Double/Int/Long but System.MathF.* for
            // Float; a non-intrinsic sibling like Double.pow(Int) MISSES here). Fall back to the name-only map only for
            // UNAMBIGUOUS names (isNaN, clrTimestamp) — never for a name whose overloads split across Math/MathF.
            var sigKey0 = string.Join(",", sigParts0.Select(ReferenceMetadataIndex.ParamKey));
            if ((refs.TryTopLevelIntrinsicBySig(fn, sigKey0, out var fq)
                    || (!refs.IsAmbiguousTopLevelIntrinsic(fn) && refs.TryTopLevelIntrinsic(fn, out fq)))
                && fq.LastIndexOf('.') is var dot && dot > 0)
                return ClrCallNode(node, fq[..dot], fq[(dot + 1)..], fq[(dot + 1)..], args0, instance: false, refs.TopLevelByrefPositions(fn));
            // bare-intrinsic extension: resolve by name + the first-arg's receiver key + full param count (disambiguates
            // `set`, and keeps `substring(String,Int)`@ClrIntrinsic from capturing the 3-arg `substring(String,Int,Int)`).
            if (sigParts0.Count >= 1 && refs.TryExtMemberIntrinsic(fn, RecvKeyOf(sigParts0[0]), sigParts0.Count, out var extMember))
                return TopLevelExtensionInstance(node, refs, extMember, args0, sigParts0);
            // A NON-intrinsic referenced top-level stdlib fun (getOrElse/first/...): kotc emits owner=null (it cannot
            // know the file-class — that is CLR/ref knowledge). In an APP build, attribute it to the file-class the
            // ref.dll says it lives in, so ilemit's owner-present FindMethod reflects it against the runtime stdlib —
            // exactly how the iterator bridge `callStatic kotlin.collections.ClrIteratorBridgeKt.*` already resolves.
            // Skipped when the fun is locally defined (the sibling wins) or in the stdlib self-build (flag off).
            if (_attributeTopLevelOwner && !_localTopLevelFns.Contains(fn))
            {
                var recvKey = sigParts0.Count >= 1 ? RecvKeyOf(sigParts0[0]) : "";
                if (refs.TryResolveTopLevelStatic(fn, recvKey, out var fileClassOwner))
                {
                    node["owner"] = fileClassOwner;
                    return node;
                }
            }
            return null;
        }
        if (!refs.TryResolveClrOwner(ownerToken, out var bcl, out var kind)) return null;

        var member = (node["method"] as JsonValue)?.GetValue<string>();
        if (string.IsNullOrEmpty(member)) return null;
        var ownerFqn = ReferenceMetadataIndex.BareOwnerFqn(ownerToken);
        var args = node["args"] as JsonArray ?? new JsonArray();

        // Rule 0 (inline-class ERASURE / unbox): the backing-field getter of an @JvmInline value class erased to its
        // primitive CLR form (`uint.get_data()`) is the unbox — the receiver value IS the field. Collapse it to a
        // `conv` of the receiver to the field's declared type (never a `ldfld data` — System.UInt32 has no `data`). This
        // is the GENERAL inline-erasure rule, not a UInt.toInt special-case; it fixes both the inlined `x.data` and the
        // rule-3 helper body's `self.data`, after which all the unsigned conversions fold to a plain cast.
        if (instance && refs.TryInlineFieldGetter(ownerFqn, member, out var inlineConv))
            return new JsonObject { ["k"] = "conv", ["to"] = inlineConv, ["e"] = node["recv"]?.DeepClone() };

        // The CLR owner TYPE the call addresses (a ClrRef-resolvable BCL token; see ClrOwnerType).
        var clrOwner = ClrOwnerType(refs, ownerToken) ?? bcl;

        // Rule 2p (explicit PROPERTY accessor): the member carries @ClrProperty(access, name) -> route EXPLICITLY to
        // clrPropGet(name) [READ] / clrPropSet(name) [WRITE] on the BCL owner, from the stated access role — NOT the old
        // get_/set_ intrinsic-string-prefix sniff. Handled before Rule 2/3 so a @ClrProperty stub (setLength/capacity/
        // ticks) is neither routed as a plain method nor hoisted as a rule-3 body.
        if (instance && refs.TryMemberProperty(ownerFqn, member, args.Count, out var pAccess, out var pName))
            return ClrPropNode(node, clrOwner, pName, pAccess, member, args);

        // Rule 2: the member carries @ClrIntrinsic -> a direct BCL call.
        if (refs.TryMemberIntrinsic(ownerFqn, member, args.Count, out var intrinsic))
            return Constrainify(ClrCallNode(node, clrOwner, intrinsic, member, args, instance, refs.MemberByrefPositions(ownerFqn, member, args.Count)), node, refs, ctx, ownerToken);

        // Rule 3: a concrete member of a CLR-bound CLASS with NO @ClrIntrinsic carries a real Kotlin body, which kotc
        // hoists to the static helper `<>dotkt_ClrH_<owner>` (driven by the SAME class binding that brought us here).
        // `IsRule3Member` (ref.dll: the member is concrete + intrinsic-less) is the signal kotc hoisted it; the helper
        // is emitted into the same runtime assembly. NEVER for an INTERFACE owner: an @ClrTypeAlias interface's members
        // are abstract in source (kotc emits NO helper for it — confirmed: every emitted <>dotkt_ClrH_* is a class), so
        // its abstract collection members (isEmpty/contains/iterator/...) need kotc's ClrCollectionDefaults routing, not
        // a non-existent helper. (The ref.dll mis-reports these as non-abstract, so IsRule3Member alone false-positives.)
        if (kind != "interface" && refs.IsRule3Member(ownerFqn, member))
            return Rule3HelperCall(node, refs, ownerFqn, member, args, instance);

        // Rule 5 (collection-interface defaults): the substituted BCL IReadOnly*/I* interfaces lack isEmpty/contains/
        // containsAll/indexOf/lastIndexOf/subList/listIterator/iterator, so an @ClrTypeAlias collection-interface call
        // routes to the rt's ClrCollectionDefaults / ClrIteratorBridge helpers — the SAME targets kotc's collDefault
        // path uses (its `clrName(declaringClass) != null` gate is now null for the @ClrTypeAlias collection interfaces,
        // so it no longer fires; this is the bir2cir home of that Kotlin<->CLR relation). The element type is the
        // owner token's first type arg; the helper is generic over it.
        if (instance && kind == "interface" && ownerFqn.StartsWith("kotlin.collections.", StringComparison.Ordinal))
        {
            var elem = OwnerElemArg(ownerToken);
            if (member == "iterator" && args.Count == 0)
                return CollDefaultCall(node, "kotlin.collections.ClrIteratorBridgeKt", "iteratorOverEnumerable", elem, args);
            if (member == "listIterator")
            {
                var idx = args.Count >= 1 ? args : new JsonArray { new JsonObject { ["k"] = "const", ["type"] = "int", ["value"] = 0 } };
                return CollDefaultCall(node, "kotlin.collections.ClrCollectionDefaultsKt", "clrListListIterator", elem, idx);
            }
            if (CollectionDefaults.TryGetValue(member, out var helperMethod))
                return CollDefaultCall(node, "kotlin.collections.ClrCollectionDefaultsKt", helperMethod, elem, args);
        }

        // Rule 4 (already-BCL member name): kotc emits the BCL member NAME for a member it knows is CLR-bound — both the
        // universal object/comparable renames (compareTo/equals/hashCode/toString -> CompareTo/Equals/GetHashCode/
        // ToString) and the collection accessors/methods (get_Item/get_Count/Add/set_Item/RemoveAt/Insert/Remove/Clear/
        // GetEnumerator/...). The ref.dll member is kept under its Kotlin name (`get`/`compareTo`), so rules 2/3 miss by
        // name; but the emitted name is already the BCL member, which exists on the alias's BCL type. A BCL name is
        // PascalCase or a get_/set_ accessor (Kotlin members are lowercase camelCase) -> route to clrInstance/clrPropGet
        // on the BCL type. A lowercase-camelCase name that reaches here is an UNBOUND Kotlin member with no BCL
        // equivalent by that name (MutableCollection.addAll/removeAll/retainAll on ICollection) -> still route it to a
        // clrInstance on the BCL owner: ilemit resolves the BCL member when one matches, and falls to dynamic dispatch
        // (recv.GetType().GetMethod(name)) when none does. EITHER WAY this is correct AND it rescues the call from the
        // clrg:/shorthand owner that plain `callInstance` resolution (ilemit ParseOwner / ResolveMethod) cannot handle.
        return Constrainify(ClrCallNode(node, clrOwner, member, member, args, instance), node, refs, ctx, ownerToken);
    }

    // Generic-parameter receiver on a CLR-aliased INTERFACE: bir2cir would emit `clrInstance` on the interface owner
    // padded to <object> (ClrOwnerType has no receiver type args to fill), and ilemit's plain `callvirt
    // ICollection<object>::Add` MIS-DISPATCHES — the runtime value (`List<R>`) implements `ICollection<R>`, not <object>,
    // so the JIT finds no slot and throws EntryPointNotFoundException. This is the collection-BUILDING crash:
    // `mapTo`/`filterTo`/`toCollection`'s `destination.add(...)` where `destination: C` and `C : MutableCollection<R>`.
    // Re-express it as constrained dispatch — `constrained. !!C ; callvirt ICollection<R>::Add` — instantiating the
    // interface with the receiver type-parameter's own constraint args (its constraint chain reaches the call owner).
    // Fires ONLY for a local/param receiver whose STATIC type is `gp:X` and whose constraint is a CLR-bound interface;
    // a concrete-class receiver (`ArrayList().add`) already dispatches fine and is left as a plain clrInstance.
    static JsonNode Constrainify(JsonNode built, JsonObject node, ReferenceMetadataIndex refs, SubstCtx ctx, string ownerToken)
    {
        if (ctx == null || built is not JsonObject call) return built;
        if ((call["k"] as JsonValue)?.GetValue<string>() != "clrInstance") return built;
        if (node["recv"] is not JsonObject recv || (recv["k"] as JsonValue)?.GetValue<string>() != "local") return built;
        var vn = (recv["name"] as JsonValue)?.GetValue<string>();
        if (vn == null || !ctx.VarTypes.TryGetValue(vn, out var vt) || !vt.StartsWith("gp:", StringComparison.Ordinal))
            return built;
        if (!ctx.TpConstraints.TryGetValue(vt.Substring(3), out var cons)) return built;
        // The call's declaring owner must itself be a CLR-bound INTERFACE (concrete-class members dispatch fine already).
        if (!refs.TryResolveClrOwner(ownerToken, out var ownerBcl, out var ownerKind) || ownerKind != "interface")
            return built;
        // The receiver type-parameter's element args come from its collection-interface constraint
        // (`MutableCollection[gp:R]` -> "gp:R"). Instantiate the CALL's owner interface with them.
        string cargs = null;
        foreach (var c in cons)
        {
            if (!refs.TryResolveClrOwner(c, out _, out var ck) || ck != "interface") continue;
            var b = c.IndexOf('[');
            if (b >= 0 && c.EndsWith("]", StringComparison.Ordinal)) { cargs = c.Substring(b + 1, c.Length - b - 2); break; }
        }
        if (cargs == null) return built;

        var cc = new JsonObject
        {
            ["k"] = "constrainedCall",
            ["recvType"] = vt,
            ["iface"] = "clrg:" + ownerBcl + "[" + cargs + "]",
            ["method"] = (call["method"] as JsonValue)?.GetValue<string>(),
            ["recv"] = call["recv"]?.DeepClone(),
            ["args"] = (call["args"] as JsonArray)?.DeepClone() ?? new JsonArray(),
        };
        if (call["argTypes"] is JsonArray at) cc["argTypes"] = at.DeepClone();
        if (call["ret"] is JsonValue rv) cc["ret"] = rv.DeepClone();
        return cc;
    }

    // Kotlin collection-interface member -> the rt ClrCollectionDefaults static (recv-first, generic over elem).
    // iterator() and listIterator() are handled separately (different owner / default index).
    static readonly Dictionary<string, string> CollectionDefaults = new(StringComparer.Ordinal)
    {
        ["isEmpty"] = "clrCollIsEmpty",
        ["contains"] = "clrCollContains",
        ["containsAll"] = "clrCollContainsAll",
        ["indexOf"] = "clrListIndexOf",
        ["lastIndexOf"] = "clrListLastIndexOf",
        ["subList"] = "clrListSubList",
    };

    // A `callStatic <helperOwner>.<helperMethod>(recv, args...)` typed over the collection's element. Mirrors kotc's
    // collDefault emission shape (owner=ClrCollectionDefaultsKt / ClrIteratorBridgeKt, recv prepended, typeArgs=[elem]).
    static JsonNode CollDefaultCall(JsonObject node, string helperOwner, string helperMethod, string elem, JsonArray args)
    {
        var hargs = new JsonArray();
        if (node["recv"] != null) hargs.Add(node["recv"].DeepClone());
        foreach (var a in args) hargs.Add(a?.DeepClone());
        return new JsonObject
        {
            ["k"] = "callStatic",
            ["owner"] = helperOwner,
            ["method"] = helperMethod,
            ["args"] = hargs,
            ["typeArgs"] = new JsonArray { elem },
        };
    }

    // The first top-level type argument of an owner token (`kotlin.collections.List[gp:E]` -> `gp:E`); `object` if none.
    static string OwnerElemArg(string ownerToken)
    {
        var br = ownerToken.IndexOf('[');
        if (br < 0 || !ownerToken.EndsWith("]", StringComparison.Ordinal)) return "object";
        var inner = ownerToken[(br + 1)..^1];
        var parts = SplitTopLevel(inner);
        return parts.Count > 0 && parts[0].Length > 0 ? parts[0] : "object";
    }

    // A bare-@ClrIntrinsic top-level EXTENSION fun: `fn(recv, rest...)` -> `recv.<intrinsic>(rest...)`. The extension
    // receiver is the first arg; the first `sig` type is its (CLR) type, the rest are the method's arg types. ilemit
    // resolves the BCL member on that receiver type (incl. its array-Clone / dynamic-dispatch fallbacks).
    static List<string> SplitSig(JsonObject node)
    {
        var sig = (node["sig"] as JsonValue)?.GetValue<string>();
        return string.IsNullOrWhiteSpace(sig) ? new List<string>() : SplitTopLevel(sig).ToList();
    }

    // The receiver-type key of a call's first-arg type (mirrors ReferenceMetadataIndex.RecvKey on the ref.dll side).
    static string RecvKeyOf(string sig0)
    {
        if (sig0.StartsWith("array:", StringComparison.Ordinal)) return "[]";
        if (sig0.StartsWith("gp:", StringComparison.Ordinal)) return "gp";
        return ReferenceMetadataIndex.BareOwnerFqn(sig0);
    }

    // A CLR-bound owner token's ClrRef-resolvable BCL type: a non-generic alias is its bare BCL FQN ("System.String"
    // -- NOT the "string" shorthand, which ilemit ClrRef can't resolve as a clr* `type`); a generic alias keeps its
    // element args (clrg:<bcl>[<args>], or [object x arity] when the token erased them). Null if not CLR-bound.
    static string ClrOwnerType(ReferenceMetadataIndex refs, string ownerToken)
    {
        if (!refs.TryResolveClrOwner(ownerToken, out var bcl, out _)) return null;
        var fqn = ReferenceMetadataIndex.BareOwnerFqn(ownerToken);
        var br = ownerToken.IndexOf('[');
        if (br >= 0 && ownerToken.EndsWith("]", StringComparison.Ordinal))
            return "clrg:" + bcl + "[" + ownerToken[(br + 1)..^1] + "]";
        if (refs.OwnerArity(fqn) is var ar && ar > 0)
            return "clrg:" + bcl + "[" + string.Join(",", Enumerable.Repeat("object", ar)) + "]";
        return bcl;
    }

    static JsonNode TopLevelExtensionInstance(JsonObject node, ReferenceMetadataIndex refs, string intrinsic, JsonArray args, List<string> sigParts)
    {
        if (args.Count == 0) return null;   // no receiver -> not an extension shape; leave for FindStatic to report
        var sig0 = sigParts.Count > 0 ? sigParts[0] : InferExpressionType(args[0]);
        // The receiver type must be a ClrRef-resolvable BCL token (System.String / clrg:... / array:...), not a bare
        // shorthand the later type-lowering would produce (`kotlin.String` -> "string" doesn't resolve in ClrRef).
        var recvType = ClrOwnerType(refs, sig0) ?? sig0;

        var argTypes = new JsonArray();
        for (var i = 1; i < sigParts.Count; i++) argTypes.Add(sigParts[i]);
        var rest = new JsonArray();
        for (var i = 1; i < args.Count; i++) rest.Add(args[i]?.DeepClone());

        var call = new JsonObject
        {
            ["k"] = "clrInstance",
            ["type"] = recvType,
            ["method"] = intrinsic,
            ["argTypes"] = argTypes,
            ["recv"] = args[0].DeepClone(),
            ["args"] = rest,
        };
        if (RetToken(node) is string ret) call["ret"] = ret;
        return call;
    }

    // @ClrProperty(access) flag values (mirror `kotlin.clr.READ`/`WRITE`): a get accessor / a set accessor; `READ|WRITE`
    // (both bits) is a get+set property whose specific call is disambiguated by the accessor member prefix / arg count.
    const int ClrPropRead = 1, ClrPropWrite = 2;

    // Build a clrPropGet/clrPropSet node for a .NET property `prop` on the BCL owner `bcl`. Used by BOTH the explicit
    // @ClrProperty accessor (Rule 2p; `prop` is the bare BCL property "Length") and the genuine `val X` member-prefix
    // accessor (trigger ①), where `prop` may arrive as the full BCL accessor name kotc emits for a CLR-bound property
    // (Rule 4: `get_Count`) — strip a leading get_/set_ so the clrProp `name` is the bare property. `access` = READ/WRITE
    // flags; when BOTH are set (a var property) the accessor member prefix (`set_` -> write) or arg count (1 = write)
    // picks the direction. WRITE takes the single value arg; READ carries the return type.
    static JsonNode ClrPropNode(JsonObject node, string bcl, string prop, int access, string member, JsonArray args)
    {
        if (prop.StartsWith("get_", StringComparison.Ordinal) || prop.StartsWith("set_", StringComparison.Ordinal))
            prop = prop[4..];
        var wantRead = (access & ClrPropRead) != 0;
        var wantWrite = (access & ClrPropWrite) != 0;
        var write = wantRead && wantWrite
            ? (member.StartsWith("set_", StringComparison.Ordinal) || args.Count == 1)
            : wantWrite;
        var pg = new JsonObject
        {
            ["k"] = write ? "clrPropSet" : "clrPropGet",
            ["type"] = bcl,
            ["name"] = prop,
            ["static"] = false,
            ["recv"] = node["recv"]?.DeepClone(),
        };
        if (!write && RetToken(node) is string ret) pg["retType"] = ret;
        if (write && args.Count >= 1) pg["value"] = args[0].DeepClone();
        return pg;
    }

    // A clrInstance / clrStatic node. A property-accessor call whose MEMBER carries the `get_`/`set_` prefix (kotc's
    // property convention: a `val length` -> the accessor call `get_length`, intrinsic bare "Length") emits clrPropGet/
    // clrPropSet on the bare intrinsic; otherwise a plain method call. A standalone accessor FUN bound to a property is
    // routed EXPLICITLY by @ClrProperty (Rule 2p) BEFORE this node is built, so there is no intrinsic-prefix sniff here.
    // Prefix `byref:` onto the argTypes at each @ClrRefArgument position (idempotent), so ilemit resolves the `ref`/`out`
    // BCL overload and emits the address-load for that arg (the byref shape the removed `ClrRef<T>` param used to carry).
    static void WrapByref(JsonArray argTypes, int[] byrefPositions)
    {
        if (byrefPositions == null) return;
        foreach (var i in byrefPositions)
            if (i >= 0 && i < argTypes.Count && argTypes[i] is JsonValue v && v.TryGetValue<string>(out var s)
                && !s.StartsWith("byref:", StringComparison.Ordinal))
                argTypes[i] = "byref:" + s;
    }

    static JsonNode ClrCallNode(JsonObject node, string bcl, string intrinsic, string member, JsonArray args, bool instance, int[] byrefPositions = null)
    {
        var argTypes = InferArgTypes(node, args);
        WrapByref(argTypes, byrefPositions);
        var ret = RetToken(node);

        // Trigger ①: a genuine `val X` accessor — kotc emits the call on the MEMBER as `get_x`/`set_x`. The intrinsic is
        // the bare property name (convention: property @ClrIntrinsic values are bare, e.g. "Length"), so it becomes the
        // clrProp `name` verbatim. (Indexers reaching here have member "get"/"set" with an index arg -> args.Count != 0/1,
        // so they fall through to the method call below, not this branch.)
        var isGet = member.StartsWith("get_", StringComparison.Ordinal) && args.Count == 0;
        var isSet = member.StartsWith("set_", StringComparison.Ordinal) && args.Count == 1;
        if (instance && (isGet || isSet))
            return ClrPropNode(node, bcl, intrinsic, isSet ? ClrPropWrite : ClrPropRead, member, args);

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

// DECLARATION-NAME RENAME (clrName migration, Step 2a). kotc tags each emitted method/accessor with a pure-Kotlin
// `overrides` marker (the transitive override closure, in Kotlin terms). This pass derives the BCL slot name from the
// ref.dll @ClrIntrinsic on the FIRST overridden member that carries one (a `size` getter override of
// Collection.size@ClrIntrinsic("Count") -> get_Count; resumeWith -> ResumeWith) — replacing what kotc's clrName/annClr
// resolves today. While annClr still runs in kotc the rename is IDEMPOTENT (it reproduces the existing name), so the
// emit stays byte-identical; once annClr is removed (Step 3) this becomes the sole source of the slot name. Mutates the
// method nodes in place; the `overrides` marker is stripped later by BirTypeLowering. (Object-method names like ToString
// and the hardcoded close->Dispose map are NOT @ClrIntrinsic, so TryMemberIntrinsic returns false and the kotc-supplied
// name is left untouched — those stay kotc's concern, a separate netType-layer cleanup.)
static class DeclarationRename
{
    // Recursively rename to the BCL slot every node carrying an `overrides` marker: a method/accessor DECLARATION (its
    // `name`) and a CALL node (`callInstance`'s `method`) alike, so the implementor-side call `AbstractList.get_size`
    // tracks the renamed declaration `get_Count`. Runs BEFORE MemberCallSubstitution so a now-`get_Count` call on a
    // CLR-bound owner still falls through to clrPropGet. Idempotent while annClr is active (reproduces the kotc name).
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs) => Walk(root, refs, false);

    static void Walk(JsonNode node, ReferenceMetadataIndex refs, bool inIface)
    {
        if (node is JsonObject obj)
        {
            // Track whether we're inside an INTERFACE type def: kotc's ifaceMethod hardcodes `override:false` for
            // interface members (even ones that bind a CLR slot), so bir2cir must NOT stamp override:true there.
            if ((obj["kind"] as JsonValue)?.GetValue<string>() is string k) inIface = k == "interface";
            if (obj["overrides"] is JsonArray ovs)
            {
                // A `properties:[{name,get,set,overrides}]` entry (kotc's CLR-property record): rename its accessor
                // references get_<name>/set_<name> -> get_/set_ + the property intrinsic ("Count"); its `name` stays the
                // Kotlin property name (matching what annClr emits). Distinguished from a method decl by having `get`.
                if (obj.ContainsKey("get") && !obj.ContainsKey("params") && ResolveBareIntrinsic(ovs, refs) is string pintr)
                {
                    obj["get"] = "get_" + pintr;
                    if (obj["set"] is JsonValue) obj["set"] = "set_" + pintr;   // null set stays null
                }
                else if (ResolveSlot(ovs, refs) is string slot)
                {
                    if ((obj["k"] as JsonValue)?.GetValue<string>() == "callInstance") obj["method"] = slot;
                    else if (obj.ContainsKey("name"))
                    {
                        obj["name"] = slot;
                        // A CLASS member that overrides a @ClrIntrinsic ancestor is a CLR override -> `override:true` AND
                        // `vis:public` (the flags kotc's `clrIfaceName != null` set via method()/accessorMethod: an
                        // interface impl must be a public virtual). Without annClr kotc emits override:false / vis:visOf(fn)
                        // for this case, so bir2cir restores them here, exactly when the rename fires. NOT in an interface
                        // (kotc's ifaceMethod keeps override:false and emits no vis). isOverride/objName keep kotc's.
                        if (!inIface)
                        {
                            if (obj.ContainsKey("override")) obj["override"] = true;
                            if (obj.ContainsKey("vis")) obj["vis"] = "public";
                        }
                    }
                }
            }
            foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value, refs, inIface);
        }
        else if (node is JsonArray arr)
            foreach (var it in arr) if (it != null) Walk(it, refs, inIface);
    }

    // The BARE property intrinsic ("Count") for a property record's override closure: the @ClrIntrinsic is on the
    // get_<name> accessor method in the ref.dll, so look that up (arity 0) and return the raw value (no get_/set_ prefix,
    // which the caller applies for both accessors). null = the overridden property carries no @ClrIntrinsic.
    static string ResolveBareIntrinsic(JsonArray ovs, ReferenceMetadataIndex refs)
    {
        foreach (var o in ovs)
        {
            if (o is not JsonObject oo) continue;
            if ((oo["owner"] as JsonValue)?.GetValue<string>() is not string owner) continue;
            if ((oo["member"] as JsonValue)?.GetValue<string>() is not string member) continue;
            if (refs.TryMemberIntrinsicExact(owner, "get_" + member, 0, out var intr)) return intr;
        }
        return null;
    }

    // The first override entry whose (owner, Kotlin member name, arity) carries an @ClrIntrinsic in the ref.dll, mapped
    // to its CLR slot: a getter/setter -> get_/set_ + the intrinsic; a method -> the intrinsic verbatim. null = no
    // CLR-bound member in the closure (leave the kotc name).
    internal static string ResolveSlot(JsonArray ovs, ReferenceMetadataIndex refs)
    {
        foreach (var o in ovs)
        {
            if (o is not JsonObject oo) continue;
            if ((oo["owner"] as JsonValue)?.GetValue<string>() is not string owner) continue;
            if ((oo["member"] as JsonValue)?.GetValue<string>() is not string member) continue;
            var kind = (oo["kind"] as JsonValue)?.GetValue<string>();
            var arity = (oo["arity"] as JsonValue)?.GetValue<int>() ?? 0;
            // The @ClrIntrinsic lives on the EMITTED member as the ref.dll exposes it: for a property it is on the
            // get_<name>/set_<name> ACCESSOR METHOD (not the property), and its value is the BCL PROPERTY name ("Count"),
            // so the slot is get_/set_ + that. A plain method's intrinsic is the BCL method name verbatim. EXACT arity
            // overload-matching (getter=arity 0, setter=arity 1) so `add(element)`->Add never grabs `add(i,e)`->Insert.
            // A property's @ClrIntrinsic lives on the get_<name> accessor (arity 0) in the ref.dll — for a SETTER too
            // (a `var` overriding a `val` base has no set_<name> to key on), so look up the getter and re-prefix. A plain
            // method's intrinsic is on the method itself by exact arity.
            var isAccessor = kind is "getter" or "setter";
            var lookupName = isAccessor ? "get_" + member : member;
            if (!refs.TryMemberIntrinsicExact(owner, lookupName, isAccessor ? 0 : arity, out var intr)) continue;
            return kind switch { "getter" => "get_" + intr, "setter" => "set_" + intr, _ => intr };
        }
        return null;
    }
}

// MEMBER-STRIP (clrName migration) — the member-level mirror of the @ClrTypeAlias type-strip. Once kotc stops reading
// @ClrIntrinsic it can no longer exclude a bound-stub declaration (the `clrName(it)==null` filters in BirEmitter), so
// those @ClrIntrinsic-bound members/top-level funs get EMITTED (with throwing TODO bodies). This pass DROPS them: the
// call sites are substituted to the BCL member by MemberCallSubstitution, so the stub itself must not survive. Matched
// by FULL SIGNATURE (name + canonical param types) so StringBuilder.append(Char)@ClrIntrinsic is dropped while
// append(CharSequence?) (rule-3, real body) is kept. For an ALIAS-class owner a member that merely OVERRIDES a
// @ClrIntrinsic member is ALSO a bound stub (its call substitutes to the BCL), so it is dropped too (else it over-hoists
// into the rule-3 helper). Runs BEFORE AliasHelperHoist. Never in ref.
static class MemberStrip
{
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs)
    {
        if (root is not JsonObject obj) return;
        if ((obj["fileClass"] as JsonValue)?.GetValue<string>() is string fc && obj["methods"] is JsonArray rootMethods)
            StripFrom(rootMethods, fc, refs, null, false);
        if (obj["types"] is not JsonArray types) return;
        foreach (var t in types)
            if (t is JsonObject td && (td["name"] as JsonValue)?.GetValue<string>() is string owner)
            {
                // NEVER strip an INTERFACE's members: a non-alias interface (EnumEntries, MatchGroupCollection) declares
                // the CLR slot (renamed get_Item/get_Count) that implementers bind to — it is not a throwing bound stub.
                // (A @ClrTypeAlias interface is dropped whole by AliasHelperHoist anyway.)
                if ((td["kind"] as JsonValue)?.GetValue<string>() == "interface") continue;
                var stripped = new HashSet<string>(StringComparer.Ordinal);
                var isAlias = ReferenceMetadataIndex.BareOwnerFqn(owner) is string bo && refs.Aliases.ContainsKey(bo);
                if (td["methods"] is JsonArray methods) StripFrom(methods, owner, refs, stripped, isAlias);
                if (td["properties"] is JsonArray props && stripped.Count > 0) DropDanglingProps(props, stripped);
            }
    }

    static void StripFrom(JsonArray methods, string owner, ReferenceMetadataIndex refs, HashSet<string> stripped, bool alias)
    {
        for (var i = methods.Count - 1; i >= 0; i--)
        {
            if (methods[i] is not JsonObject mo) continue;
            if ((mo["name"] as JsonValue)?.GetValue<string>() is not string name) continue;
            var keys = (mo["params"] as JsonArray ?? new JsonArray())
                .Select(p => ReferenceMetadataIndex.ParamKey((p as JsonObject)?["type"]?.GetValue<string>() ?? "")).ToList();
            var drop = refs.IsBoundStub(owner, name, keys)
                || (alias && mo["overrides"] is JsonArray ovs && DeclarationRename.ResolveSlot(ovs, refs) != null);
            if (drop) { stripped?.Add(name); methods.RemoveAt(i); }
        }
    }

    // A property record whose accessor method was stripped (a bound-stub property) is itself bound — drop the record.
    static void DropDanglingProps(JsonArray props, HashSet<string> stripped)
    {
        for (var i = props.Count - 1; i >= 0; i--)
            if (props[i] is JsonObject po
                && (((po["get"] as JsonValue)?.GetValue<string>() is string g && stripped.Contains(g))
                 || ((po["set"] as JsonValue)?.GetValue<string>() is string s && stripped.Contains(s))))
                props.RemoveAt(i);
    }
}

// RULE-3 HOIST (ALL CLR-bound alias classes). kotc no longer synthesizes the `<>dotkt_ClrH_<owner>` helper for ANY
// @ClrTypeAlias class whose concrete intrinsic-less members carry real bodies — the alias-only files (kotlin.String's
// subSequence, plus kotlin.Boolean/kotlin.Char operator stubs) AND the MIXED files (StringBuilder/UInt/collections/
// Regex). kotc emits each such alias class as a PLAIN BIR type; this pass reads the ref.dll @ClrTypeAlias index, hoists
// those rule-3 members into the static helper (the dispatch `this` becomes a leading `__self` param), and DROPS the
// original alias type def — it must NEVER reach ilemit as a real CLR type (its equals(Any?)/toString()/length members
// would clash with System.String/System.Object). The rule-3 CALL routing in MemberCallSubstitution already targets
// `<>dotkt_ClrH_<owner>.<member>(recv, ..)` by name, so emitting the helper here closes the loop. This is the SOLE home
// of rule-3 helper synthesis (kotc's clrHelperClassJson is deleted). Runs only in substitute/app builds (never ref).
static class AliasHelperHoist
{
    public static JsonNode Apply(JsonNode root, ReferenceMetadataIndex refs)
    {
        if (root is not JsonObject obj || obj["types"] is not JsonArray types) return root;
        var rebuilt = new JsonArray();
        var changed = false;
        foreach (var t in types)
        {
            if (t is JsonObject td && IsAliasTypeDef(td, refs, out var fqn))
            {
                changed = true;                                  // alias type def -> dropped (and possibly hoisted)
                var helper = BuildHelper(td, fqn, refs);
                if (helper != null) rebuilt.Add(helper);         // null = no rule-3 members (e.g. kotlin.Any) -> just dropped
            }
            else rebuilt.Add(t?.DeepClone());
        }
        if (!changed) return root;
        var outObj = new JsonObject();
        foreach (var kv in obj) outObj[kv.Key] = kv.Key == "types" ? rebuilt : kv.Value?.DeepClone();
        return outObj;
    }

    // A top-level type def whose FQN is a @ClrTypeAlias owner in the ref.dll (the same index the type-token lowering and
    // member-call substitution use). Only such a def is dropped/hoisted, so a non-alias plain type can never be lost.
    static bool IsAliasTypeDef(JsonObject td, ReferenceMetadataIndex refs, out string fqn)
    {
        fqn = null;
        if ((td["name"] as JsonValue)?.GetValue<string>() is not string name) return false;
        var bare = ReferenceMetadataIndex.BareOwnerFqn(name);
        if (!refs.Aliases.ContainsKey(bare)) return false;
        fqn = bare;
        return true;
    }

    static JsonObject BuildHelper(JsonObject td, string fqn, ReferenceMetadataIndex refs)
    {
        // ONLY a CLASS alias gets a rule-3 helper. kotc now emits @ClrTypeAlias INTERFACES (Comparable/Iterable/
        // Collection/List/…) too (it no longer strips them); those are dropped here with NO helper — an interface's
        // members are abstract in source, and a ref.dll default-interface-method would otherwise false-positive as a
        // rule-3 member and produce a bogus interface "helper". A non-class kind => return null => the alias is just
        // dropped (its use-site references are lowered to the BCL type by BirTypeLowering).
        if ((td["kind"] as JsonValue)?.GetValue<string>() != "class") return null;
        var classTps = td["typeParams"] as JsonArray;
        var aliasToken = (td["name"] as JsonValue)!.GetValue<string>();   // kotlin FQN; lowered to its BCL form downstream
        // An @JvmInline value-class alias (UInt/UByte/ULong/UShort -> System.UInt32/Byte/...) erases to its backing
        // primitive; its Object-method overrides (Equals/GetHashCode/ToString) operate on the boxed Kotlin value and
        // read the now-erased `.data` field, so hoisting them produces a `<self>.data` access on the value-type
        // shorthand (`ubyte`) that ilemit cannot resolve. They must NOT be hoisted — a call `u.toString()` defers to
        // the BCL primitive's ToString via member-call substitution. (A non-value alias like Boolean DOES hoist its
        // Equals/GetHashCode/ToString — those carry real Kotlin bodies and no erased field.)
        var isInlineValue = refs.IsInlineValueClass(fqn);
        var methods = new JsonArray();
        foreach (var m in td["methods"] as JsonArray ?? new JsonArray())
        {
            if (m is not JsonObject mo) continue;
            if ((mo["name"] as JsonValue)?.GetValue<string>() is not string mn) continue;
            if (mn.StartsWith("get_", StringComparison.Ordinal) || mn.StartsWith("set_", StringComparison.Ordinal)) continue;
            if ((mo["static"] as JsonValue)?.GetValue<bool>() == true) continue;   // a top-level/companion static, not a member
            if (mo["body"] is not JsonArray) continue;                              // abstract / no body
            if (isInlineValue && (mo["objectOverride"] as JsonValue)?.GetValue<bool>() == true) continue;  // see note above
            if (!refs.IsRule3Member(fqn, mn)) continue;   // ref.dll: concrete + intrinsic-less (matches the rule-3 call routing)
            methods.Add(HoistMethod(mo, aliasToken, classTps));
        }
        if (methods.Count == 0) return null;
        return new JsonObject
        {
            ["name"] = ReferenceMetadataIndex.HelperTypeName(fqn),
            ["kind"] = "class",
            ["abstract"] = false,
            ["vis"] = "public",
            ["base"] = null,
            ["interfaces"] = new JsonArray(),
            ["fields"] = new JsonArray(),
            ["ctors"] = new JsonArray(),
            ["methods"] = methods,
        };
    }

    // An instance member -> a static helper method: prepend a `__self` param typed as the alias owner, rewrite the
    // dispatch `this` to that `__self`, and declare the class type params ahead of the method's own (a generic alias's
    // helper needs them for `__self`). Mirrors kotc's clrHelperMethod shape so ilemit sees an identical helper.
    static JsonObject HoistMethod(JsonObject m, string aliasToken, JsonArray classTps)
    {
        // A GENERIC alias owner (ArrayList<E>, HashMap<K,V>) must type `__self` as the CONSTRUCTED generic
        // `kotlin.collections.ArrayList[gp:E]` — BirTypeLowering then lowers it to `clrg:System...List[gp:E]` (with
        // arity). A bare `kotlin.collections.ArrayList` token would lower to a non-generic `clr:System...List` that
        // ilemit cannot resolve. The class type params (bare-string entries like "E") become the `gp:` args; they are
        // declared on the method via MergeTypeParams below, so `gp:E` is in scope. (Mirrors kotc's old birType(__self).)
        var selfType = aliasToken;
        if (classTps is { Count: > 0 })
            selfType = aliasToken + "[" + string.Join(",", classTps.Select(tp => "gp:" + (tp as JsonValue)?.GetValue<string>())) + "]";
        var ps = new JsonArray { new JsonObject { ["name"] = "__self", ["type"] = selfType } };
        foreach (var p in m["params"] as JsonArray ?? new JsonArray()) ps.Add(p?.DeepClone());
        var outM = new JsonObject
        {
            ["name"] = (m["name"] as JsonValue)!.DeepClone(),
            ["static"] = true,
            ["override"] = false,
            ["virtual"] = false,
            ["abstract"] = false,
            ["objectOverride"] = false,
            ["vis"] = "public",
        };
        var tps = MergeTypeParams(classTps, m["typeParams"] as JsonArray);
        if (tps != null) outM["typeParams"] = tps;
        outM["params"] = ps;
        outM["ret"] = m["ret"]?.DeepClone();
        outM["body"] = RewriteThis(m["body"]);
        return outM;
    }

    static JsonArray MergeTypeParams(JsonArray a, JsonArray b)
    {
        if ((a == null || a.Count == 0) && (b == null || b.Count == 0)) return null;
        var r = new JsonArray();
        if (a != null) foreach (var x in a) r.Add(x?.DeepClone());
        if (b != null) foreach (var x in b) r.Add(x?.DeepClone());
        return r;
    }

    // Rewrite every dispatch-receiver node {"k":"this"} to the hoisted static's leading `__self` local. kotc lifts all
    // lambdas/local funs to separate methods, so within a single member body every {"k":"this"} is THIS receiver.
    static JsonNode RewriteThis(JsonNode n)
    {
        if (n is JsonObject o)
        {
            if ((o["k"] as JsonValue)?.GetValue<string>() == "this")
                return new JsonObject { ["k"] = "local", ["name"] = "__self" };
            var c = new JsonObject();
            foreach (var kv in o) c[kv.Key] = kv.Value == null ? null : RewriteThis(kv.Value);
            return c;
        }
        if (n is JsonArray a)
        {
            var c = new JsonArray();
            foreach (var i in a) c.Add(i == null ? null : RewriteThis(i));
            return c;
        }
        return n?.DeepClone();
    }
}

static class JsonOptions
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}

sealed class UsageException : Exception
{
    public UsageException(string message) : base(message) { }
}
