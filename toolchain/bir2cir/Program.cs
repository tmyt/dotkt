// bir2cir — lower Backend IR (BIR) JSON into CLR IR (CIR) JSON.
//
// bir2cir owns the Kotlin -> CLR type substitution. Its SINGLE, sole transform rewrites the Kotlin type
// vocabulary in the BIR into the CLR-codegen vocabulary ilemit consumes, emitting a BIR-SHAPED CIR (same node
// shape; only type strings change). There is no verbatim-copy / envelope alternative — that dual track is retired.
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Toolchain;

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
            Console.Error.WriteLine("usage: bir2cir <out-dir> [--compile-refs <dll;dll;...>] <file.bir.json>...");
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
        var refs = ReferenceMetadataIndex.Build(_options.CompileReferences);
        // Fail-loud: a ref.dll scan swallows load/type failures into Diagnostics (so ONE malformed type never aborts the
        // whole scan). Surface them here — a silent ref-scan miss otherwise surfaces as a distant EntryPointNotFound/NRE
        // with no "ref scan failed" signal. An empty Diagnostics stays silent (the happy path prints nothing).
        var diagnostics = refs.Diagnostics.ToList();
        foreach (var d in diagnostics) Console.Error.WriteLine($"bir2cir: WARNING ref-scan diagnostic: {d}");
        var cirFiles = TransformFiles(birFiles, refs);
        // Release the long-lived .NET-interop MetadataLoadContext (kept alive across all transform passes for
        // NetInteropBinding's owner resolution — A2 / #61) now that no pass needs metadata reflection.
        refs.DisposeNet();
        // #112 Phase 4: run the SHARED IR sanity gate on the CIR we just produced — the EARLIEST catch, at the
        // bir2cir/CIR boundary (ilemit re-runs the SAME bir-common IrSanity at the head of EmitAssembly). A malformed
        // CIR (undeclared local, dangling goto, missing owner) fails LOUD here with a precise invariant message
        // instead of surfacing two stages downstream. Pure fail-fast validation — no effect on a valid CIR.
        CheckCirSanity(cirFiles);
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
            var root = JsonNode.Parse(json, documentOptions: BirJson.DocOptions) ?? throw new UsageException($"bir2cir: invalid JSON root: {path}");
            files.Add(new BirFile(
                path,
                json,
                root,
                SuspendShapeAnalyzer.Analyze(root),
                CallSiteAnalyzer.Analyze(root)));
        }

        return files;
    }

    // #68 (PART 2): rewrite every `{t:"fqn","name":"kotlin.CharSequence"}` type REFERENCE to the synthetic
    // `dotkt$CharSequence` identity SharedSyntheticSynthesis materializes. A type reference is any object carrying `t=="fqn"`;
    // a type DECLARATION (which has `kind`, not `t`) is left alone. This IS the CharSequence substitution — bir2cir owns the
    // Kotlin<->CLR relation, so recognizing the one type with no faithful .NET supertype belongs here, not in kotc.
    static void SubstituteCharSeqIdentity(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                if ((o["t"] as JsonValue)?.GetValue<string>() == "fqn"
                    && (o["name"] as JsonValue)?.GetValue<string>() == "kotlin.CharSequence")
                    o["name"] = SharedSyntheticSynthesis.CharSeq;
                foreach (var kv in o) SubstituteCharSeqIdentity(kv.Value);
                break;
            case JsonArray a:
                foreach (var it in a) SubstituteCharSeqIdentity(it);
                break;
        }
    }

    List<CirFile> TransformFiles(IReadOnlyList<BirFile> birFiles, ReferenceMetadataIndex refs)
    {
        // #68 (PART 2): kotc emits the PLAIN Kotlin identity `kotlin.CharSequence` at every CharSequence use site (no CLR
        // synthetic name — kotc knows nothing of the synthetic). Recognizing `kotlin.CharSequence` as a synthesize-target is
        // a bir2cir concern (the Kotlin<->CLR layer), so SUBSTITUTE it here — as a one-type hardcode, exactly like the ref.dll
        // @ClrTypeAlias types substitute `kotlin.String` -> `System.String`. It runs FIRST (before the `hasUserCharSeqImpl`
        // detection, CharSeqStringLowering, and the per-file SharedSyntheticSynthesis trigger) so every downstream pass sees
        // the canonical `dotkt$CharSequence` identity exactly as kotc's retired charSeqIface() mapping used to emit it. Only a
        // `{t:"fqn"}` type-reference NAME is rewritten (a type DECLARATION's own `name` sits under `kind`, not `t`, so real
        // kotlin.CharSequence declarations — if any — are untouched).
        foreach (var b in birFiles) SubstituteCharSeqIdentity(b.Root);

        // The top-level funs DEFINED in this compilation (every file-class's own static methods, across all input
        // files). A `callStatic owner=null` to one of these must stay owner-less (ilemit's FindStatic finds the
        // sibling); only a name absent here is eligible for referenced-stdlib file-class attribution (Gap B).
        var localTopLevelFns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in birFiles)
            if (b.Root is JsonObject ro && ro["methods"] is JsonArray ms)
                foreach (var m in ms)
                    if (m is JsonObject mo && (mo["name"] as JsonValue)?.GetValue<string>() is string mn)
                        localTopLevelFns.Add(mn);
        // The type FQNs DECLARED in this compilation (every input file's own `types`). SuspendColdLowering
        // uses it to decide whether the cold-core base (kotlin.coroutines.clr.internal.ContinuationImpl) is
        // a LOCAL type (rt-stdlib self-build -> bare base + local slot override) or a REFERENCED one
        // (app build -> clr: base + clrOverride linkage).
        var localTypeFqns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in birFiles)
            if (b.Root is JsonObject ro && ro["types"] is JsonArray ts)
                foreach (var t in ts)
                    if (t is JsonObject to && (to["name"] as JsonValue)?.GetValue<string>() is string tn)
                        localTypeFqns.Add(tn);
        // LOCAL-OVER-REF (#15): tell the refs index which FQNs are this-assembly-emitted, so ResolveNetType refuses to
        // bind a locally-declared type to a referenced dll of the same identity — `new`/callInstance/callStatic/field
        // on a source-compiled `demo.Plain` route to the emitted type, not `newClr`/`clr*` against the ref copy.
        refs.SetLocalEmittedTypes(localTypeFqns);
        // The DIRECT supertypes (base + interfaces, by FQN) of each type DECLARED in this compilation, aggregated
        // across all input files. ForInLowering walks it to decide whether a stdlib self-build for-loop source is a
        // kotlin.collections iterable (a concrete subtype such as ArrayList : MutableList matches even though its own
        // FQN is not a collection interface) — the supertype walk kotc's retired isStdlibCollectionIterable did over
        // the IR hierarchy, reconstructed here from the BIR type defs.
        var typeSupers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var b in birFiles)
            if (b.Root is JsonObject ro && ro["types"] is JsonArray ts)
                foreach (var t in ts)
                    if (t is JsonObject to && (to["name"] as JsonValue)?.GetValue<string>() is string tn)
                    {
                        var sup = new List<string>();
                        if (to["base"] is JsonNode bn && TypeJson.Read(bn) is TypeNode.Fqn bf) sup.Add(bf.Name);
                        if (to["interfaces"] is JsonArray ifs)
                            foreach (var iface in ifs)
                                if (iface is JsonNode inode && TypeJson.Read(inode) is TypeNode.Fqn iff) sup.Add(iff.Name);
                        typeSupers[tn] = sup;
                    }
        // The LOCAL VALUE-type FQNs declared in this compilation (kotc emits `kind:"enum"` for a real CLR enum,
        // `kind:"struct"` for a value type). These are value types not present on the ref.dll, so the struct-ness
        // ORACLE must know them — a nullable local enum `E?` is `Nullable<E>`, not a bare reference. Collected across
        // ALL input files (a cross-file `E?` in file B references an enum declared in file A).
        var localValueTypeFqns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in birFiles)
            CollectLocalValueTypes(b.Root, localValueTypeFqns);
        // The combined value-type oracle: ref.dll struct/enum + foundational primitives, OR a local enum/struct.
        Func<string, bool> isValueFqn = name => refs.IsValueTypeFqn(name) || localValueTypeFqns.Contains(name);
        // Attribute referenced top-level stdlib funs to their file-class owner only in an APP build: a stdlib self-
        // build (`--build-stdlib=metadata|runtime`) defines them locally, so owner=null is correct there. The reference
        // build never runs MemberCallSubstitution at all (see the RefBuild gate below).
        var attributeTopLevelOwner = _options.StdlibMode == BuildStdlibMode.App;

        // Does THIS assembly declare a user `class S : CharSequence` (a type whose `interfaces` names the synthetic
        // `dotkt$CharSequence`)? If so, CharSequence must stay the polymorphic synthetic ASSEMBLY-WIDE: a
        // CharSequence param/local in such an assembly may hold that user impl and be read polymorphically
        // (`show(cs: CharSequence) = cs.length` with `show(S("hello"))` == 5) — collapsing it to `string` would
        // snapshot the value via `.toString()` and lose the length. So the CharSequence -> System.String lowering
        // (CharSeqStringLowering) is DISABLED for such assemblies (they keep the synthetic, unchanged), and enabled
        // only for a "pure" app assembly with no user implementer. Sealed System.String forbids a real `: string`
        // supertype, so this synthetic-retention is a technical necessity, not a preference. (docs/design-charsequence-clr-string.md)
        var hasUserCharSeqImpl = birFiles.Any(f => DeclaresCharSeqImplementer(f.Root));

        // The callee generic-param ORDER index (funName|arity -> ordered type-param names), aggregated across ALL input
        // BIR files: a same-assembly cross-file `callStatic owner=null` may target a fun defined in another input, and
        // MapVarianceRealign needs the callee's declared type-param order to map a sig's `gp:NAME` to its typeArg index.
        var calleeTypeParams = MapVarianceRealign.CollectCalleeTypeParams(birFiles.Select(f => f.Root));

        // Snapshot every LOCAL generic type's declared member returns BEFORE the per-file DEF-side EraseNullableTv
        // (NullableGenericReturnErasure runs inside the transform loop, mutating declarations in place). Feeds
        // NullableTvErasureCallRealign so a `Box<Int>.get_a()` call across the generic boundary re-derives its
        // return from the (erased) declaration instead of kotc's over-substituted `Ref<Nullable<Int>>` (#4).
        var nullableTvDeclRets = NullableTvErasureCallRealign.CollectDeclaredMemberRets(birFiles.Select(f => f.Root));

        // The local RICH-enum type names (a `kind:"class"` decl carrying the faithful `enumRich:true` marker), across
        // ALL input files: EnumIntrinsicLowering lowers `enumValues<RichEnum>()` to the synthesized static values()
        // (not the System.Enum-reflection semantic node — a rich enum is a plain singleton class).
        var localRichEnums = EnumIntrinsicLowering.CollectRichEnums(birFiles.Select(f => f.Root));

        // The local BASIC (value-type, `kind:"enum"`) enum type names across ALL files — EnumMemberBinding rebinds a
        // `System.Enum`-inherited Object-slot call (`E.A.toString()`) on such an owner to an `objMethod`. Must be
        // module-wide (not per-file): the call site can be in a different .bir.json than the enum declaration. Only the
        // LOCAL enums need it — a CROSS-ASSEMBLY enum's inherited-member call is closed one pass earlier by
        // NetInteropBinding (facadegen-injected owner -> `clrInstance`, a `constrained. callvirt`), and a klib-external
        // `kotlin.*` enum arrives from kotc already as an `objMethod`; neither reaches this local `callInstance` gap.
        var localBasicEnums = EnumMemberBinding.CollectBasicEnums(birFiles.Select(f => f.Root));

        // The local reference TYPES (classes + interfaces) module-wide (name -> declared universal slots + base) —
        // AnySlotRebind rebinds a dead-ending `callInstance <UserType>.GetHashCode/ToString/Equals` (a fake override
        // inherited from the implicit kotlin.Any, which ilemit cannot resolve because the base field is absent) to an
        // `objMethod`, exactly as EnumMemberBinding does for value-type enums (#96). Module-wide because the call site
        // may live in a different .bir.json than the type declaration, while ilemit's emitted-type table is assembly-wide.
        var localRefTypes = AnySlotRebind.CollectLocalTypes(birFiles.Select(f => f.Root));

        // #63 (F4): the app-local file-class method names a `newDelegate` target `ldftn`-resolves against, collected
        // MODULE-WIDE across every input file — ilemit's FindStatic binds a delegate method by bare name against ALL
        // IsFileClass types in the module (and the inline stash spans all files), so a carrier materializing a SIBLING
        // file's lifted `__lambdaN` is app-local. Pre-collect ONCE (InlineSplice.Apply runs per file) and pass in, so a
        // cross-file materialization is not mis-judged non-app-local and refused loud. Nested-TYPE member methods stay
        // excluded (ilemit's file-class-only ldftn universe) — only the FILE scope was wrong (regression from 923a820).
        var appLocalFileClassMethods = InlineSplice.CollectAppLocalMethodNames(birFiles.Select(f => f.Root));

        // INLINE-BIR STASH (#71/#75 S1): BEFORE any lowering pass runs, capture every `mods.inline` method's RAW
        // pre-lowering body into an OPAQUE `inlineBir` base64 string (ilemit stamps it verbatim as the raw-BIR
        // [KotlinInline] carrier) + an in-memory `owner|name|pc|ga -> raw decl` index (dormant same-module infra).
        // Runs across ALL files here so the index spans the compilation, and so every downstream walker sees the
        // captured body already inert (a JsonValue string) — RefBodySquash then squashes only the executable `body`.
        InlineBirStash.Reset();
        foreach (var b in birFiles) InlineBirStash.Stash(b.Root);

        // CROSS-FILE STATIC-TYPE AGGREGATION (#149): seed StaticType's assembly-wide GlobalTypes / GlobalFileClasses
        // from every input file's RAW root, so a receiver whose static type is declared in a SIBLING .kt (a cross-file
        // user-class property `c.body`, a cross-file top-level fun result) resolves through the per-file LocalTypes
        // MISS -> this global fallback. Consumed by the StringCharSequenceBridge (and every StaticType.Surface caller)
        // to adapter-wrap a cross-file String receiver that would else reach the body-less `dotkt$CharSequence` slot.
        StaticType.CollectGlobal(birFiles.Select(b => b.Root));

        // PHASE 1: per-file transforms up through the CharSequence bridge. Collect the staged roots so the
        // suspend cold lowering can run GLOBALLY (a same-assembly cross-file suspend call keeps `owner:null`,
        // so its cold-entry callee may live in another file — the transformability fixpoint spans all files).
        var staged = new List<(JsonNode Root, string OutputName)>();
        foreach (var bir in birFiles)
        {
            var outputName = OutputNameFor(bir.Path);
            // SYNTHETIC CLR-REPRESENTATION TYPES (#52 kotc-purity): kotc emits only the FACTS — a capturing lambda's
            // `newClosure` carries a transient `synthClass` ingredient bag; a CharSequence / KProperty use references
            // the identity; a heap ref-cell rides the `refTypes` registry. Assemble the actual closure / interface /
            // cell TYPE definitions HERE, in the Kotlin<->CLR layer, and inject them into the file `types`. Runs FIRST
            // (before every other transform) so the synthesized types are present exactly as kotc's old liftedTypes /
            // charSeqIfaceDefs / kPropertyDefs / refDefs used to be — and, crucially, before Phase-1.5
            // SuspendColdLowering builds its `closures` lookup from `types`. ClosureSynthesis first so a closure invoke
            // body that references KProperty is in `types` when SharedSyntheticSynthesis scans for it.
            // HIGH-ARITY FUNCTION-TYPE DECL FILTER (#72): drop (stdlib) / reject (app) any decl whose signature mentions
            // a function type with >16 params — no System.Func/Action exists for it. Runs BEFORE ClosureSynthesis so a
            // dropped body's lambdas are never synthesized into orphan closure types (moved here from kotc, which now
            // emits every decl faithfully; the Func/Action 16-cap is a CLR-representation fact).
            HighArityFunctionFilter.Apply(bir.Root, _options.StdlibMode);
            // OBJECT-SLOT RENAME (#73 M5): restore the System.Object BCL slot names (ToString/GetHashCode/Equals) that
            // kotc stopped emitting — it now emits the Kotlin names (toString/hashCode/equals) + the pure-Kotlin facts
            // `objectOverride:true` (decl) / `anySlot:true` (call). Runs FIRST and UNCONDITIONALLY (ref + rt + app): the
            // former kotc rename was unconditional, so the ref.dll decl names + emitted-name-keyed member index must stay
            // byte-identical; placing it first lets every downstream pass see the same BCL-spelled trees as before.
            ObjectSlotRename.Apply(bir.Root);
            // PRECONDITION / ERROR FAMILY (#73 M6): kotc emits the FAITHFUL top-level call (require/check/error/TODO/
            // requireNotNull/checkNotNull as `callStatic owner:null`, noWhenBranchMatchedException as the faithful
            // `kotlin.internal.ir` intrinsic). These @InlineOnly helpers have NO rt.dll body, so bir2cir SYNTHESIZES the
            // throw/condition FQN-keyed — the exact cond/throwExpr/valueBlock CIR kotc used to emit over bare Kotlin
            // exception FQNs (the IllegalArgumentException->System.ArgumentException BCL mapping happens downstream off
            // the ref.dll @ClrTypeAlias). Runs BEFORE ClosureSynthesis so a discarded `require(cond){ lazyMessage }`
            // closure is never synthesized into an orphan type, and before MemberCallSubstitution (which would else
            // 0-candidate the bodiless helper).
            PreconditionLowering.Apply(bir.Root, localTopLevelFns, attributeTopLevelOwner);
            // TOP-LEVEL `repeat(n){}` INLINE LOOP (#73 M7): kotc emits the FAITHFUL `callStatic owner:null method:repeat
            // args:[n, <lambda>]`. Re-emit the counted loop (n once, index 0..n-1) invoking the action delegate — shape-
            // agnostic over the lambda's newClosure/newDelegate form. Runs BEFORE ClosureSynthesis so the action closure
            // (moved into the hoist var) is synthesized there exactly once, and before MemberCallSubstitution.
            RepeatInlineLowering.Apply(bir.Root, localTopLevelFns, attributeTopLevelOwner);
            // INLINE SPLICE (#71/#75): kotc emits a `callInline` node for an inline fn whose lambda body must live inline
            // at the call site (a NON-LOCAL `return`/suspend through the lambda). `kotlin.repeat` -> the counted loop; the
            // GENERIC cross-module arm RESOLVES the callee's RAW BIR body ([KotlinInline] off `refs`, or InlineBirStash's
            // same-module index) and SPLICES it (positional tv-subst, temp-bound params, lambda-invoke splicing, routed
            // returns, hygiene) into a value-producing valueBlock — which then re-lowers IN THIS app's context. Runs here
            // (before ClosureSynthesis, like RepeatInlineLowering) so nested closures in the spliced body synthesize once.
            InlineSplice.Apply(bir.Root, refs, appLocalFileClassMethods);
            // CROSS-MODULE DEFAULT-ARG SPLICE (#146): fill a call's OMITTED defaulted args (kotc's `defaultArg`
            // placeholders) from the callee's `[kotlin.clr.KotlinDefault]` BIR on the referenced .dll. Runs HERE — phase 1,
            // right after InlineSplice, before ObjectSlotRename/ClosureSynthesis/MemberCallSubstitution/BirTypeLowering —
            // so the spliced RAW default expression (a `newDelegate` re-hoisted app-local, a `callStatic owner:null`, a
            // const) re-lowers IN THIS app's context, exactly like an inline-body splice. Ownerless (name|arity), because
            // the owner is not yet attributed. APP builds only (user libraries build in App mode too — Metadata/Runtime are
            // stdlib-self-build flags): a `defaultArg` placeholder is born ONLY on a call to a facadegen-INJECTED callee
            // (the cross-module IrErrorExpression path), and the ref/rt stdlib self-builds reference no DotKt assembly, so no
            // injected callee — hence no placeholder — exists there. The gate is not merely "harmless off": running on a
            // self-build would also disturb its byte-stable RefBodySquash/RoundtripMetadata decl set for zero benefit.
            if (attributeTopLevelOwner) DefaultArgSplice.Apply(bir.Root, refs);
            // RE-NORMALIZE the just-spliced RAW payload bodies: InlineSplice runs AFTER ObjectSlotRename (219), so a
            // cross-module inline body carries kotc's raw `objMethod toString`/`hashCode`/`equals` (and `anySlot` calls)
            // un-renamed — ilemit's EmitObjMethod keys on the BCL spelling (`ToString`), so an un-renamed `toString`
            // silently drops the call (the receiver flows through -> a wrong-type cast). ObjectSlotRename is idempotent
            // (already-BCL names + already-stripped `anySlot` are no-ops), so a second whole-tree pass only fixes the
            // spliced-in nodes. (#75: splice-all made this live — e.g. `buildString{}` losing its `.toString()`.)
            ObjectSlotRename.Apply(bir.Root);
            ClosureSynthesis.Apply(bir.Root);
            SharedSyntheticSynthesis.Apply(bir.Root);
            // FOR-LOOP SOURCE CLASSIFICATION (#73/#73-w3): kotc emits ONE faithful `forIn` carrying the source's
            // runtime type token (`srcType`) for every non-array source — it no longer decides range-vs-collection nor
            // the .NET/Sequence-enumerable case (each needs the kotlin.ranges FQN or a `@Clr`/.NET-type resolution off
            // the refs, a Kotlin<->CLR relation). Dispatch it: a counted range -> `forRange` (realized by
            // RangeForLowering next); a `kotlin.sequences.Sequence`/.NET-enumerable/stdlib-collection -> `forEachInline`
            // (GetEnumerator); anything else -> the iterator `fallback`. Runs BEFORE RangeForLowering /
            // RangeConstructionLowering / SequenceForEachLowering so the produced forms flow through every downstream
            // pass exactly as the equivalent kotc-emitted forms did.
            ForInLowering.Apply(bir.Root, !attributeTopLevelOwner, typeSupers, localTopLevelFns, refs);
            // RANGE FOR-LOOP (#52 Phase 5 "range partial"): kotc emits a FAITHFUL `forRange` (range VALUE + loop var +
            // Kotlin `rangeType`, NO CLR accessor names/owner). Realize the IntProgression get_first/get_last/get_step
            // access HERE — the stdlib form keeps `forRange` + injects the accessors (ilemit resolves off `_types`);
            // the app form rewrites to a cross-module counter loop. Runs FIRST so the produced callInstance / forRange
            // flow through every downstream pass exactly as the equivalent kotc-emitted forms did (byte-identical IL).
            RangeForLowering.Apply(bir.Root, !attributeTopLevelOwner);
            // RANGE MEMBERSHIP (#73 M2): kotc emits the FAITHFUL `contains` member call for `x in a..b` (by identity,
            // NO comparison synthesis — its old bare-name lowering MISCOMPILED a user rangeTo/contains type). Lower the
            // membership to the short-circuit `(x >= a && x <op> b)` fast path FQN-keyed — only for a stdlib primitive
            // range (`kotlin.ranges.{Int,Long,Char}Range` contains over an un-materialized `rangeTo`/`until`/`rangeUntil`).
            // Runs BEFORE RangeConstructionLowering (which would else materialize the recv rangeTo into `new IntRange`)
            // so the recv still carries the inline bounds; the produced binOp/cond flows through every downstream pass
            // exactly as kotc's retired membership lowering did (byte-identical).
            RangeMembershipLowering.Apply(bir.Root, localTopLevelFns, attributeTopLevelOwner);
            // VALUE-POSITION RANGE CONSTRUCTION (#73 Phase 2b-1): kotc emits the FAITHFUL `callInstance
            // kotlin.Int.rangeTo(b)` for `a..b` / `a..<b`; materialize the stdlib `new IntRange/LongRange/CharRange`
            // HERE (the Kotlin<->CLR realization). Runs before MemberCallSubstitution (whose Rule-4 gate would refuse
            // the unbound `kotlin.Int.rangeTo`) so the recv/arg nodes flow through every downstream pass as the
            // equivalent kotc-emitted `new` did (byte-identical).
            RangeConstructionLowering.Apply(bir.Root);
            // PRIMITIVE OPERATORS (#52 Phase 5): re-emit the binOp/unaryOp kotc used to synthesize for a primitive's
            // arithmetic/bitwise/unary operator (kotc now emits the faithful `callInstance kotlin.Int.plus`). Runs
            // FIRST and UNCONDITIONALLY (ref + app) so every downstream pass sees the old tree shape, and a ref-build
            // ctor field-init / base-arg (not body-squashed) carries a raw IL op, not an unresolvable builtin call.
            PrimitiveOperatorLowering.Apply(bir.Root, refs);
            // ENUM REIFIED INTRINSICS (#73): kotc emits the faithful top-level `callStatic owner:null method:enumValues
            // typeArgs:[T]` for `enumValues<T>()`/`enumValueOf<T>()`/`enums.enumEntries<T>()`/`enumEntriesIntrinsic<T>()`.
            // Re-emit the same BIR vocabulary — rich enum -> static values()/valueOf(), basic/generic-param -> semantic
            // enumValues/enumParse — deriving rich-vs-basic from the enum's emitted shape (local `enumRich:true`). Runs
            // BEFORE ArrayConstructionLowering (#77): a `for (x in enumValues<Color>())` / `.entries` for-loop wraps
            // this call in a `forArray` whose element ArrayConstructionLowering derives via StaticType off the ALREADY-
            // lowered `enumValues`/rich-`values()` node — so the reified top-level intrinsic must already be in its
            // final semantic shape when elem-derivation runs, exactly as kotc's retired call-site interception order
            // implied. entries family: App-build sites only (stdlib self-build keeps the filler body — see
            // EnumIntrinsicLowering).
            EnumIntrinsicLowering.Apply(bir.Root, localRichEnums, localTopLevelFns, attributeTopLevelOwner);
            // ARRAY CONSTRUCTION + INTRINSIC ELEMENT (#73 Phase 2b-A): kotc emits the faithful `kotlin.IntArray`
            // identity — the sized ctor as `new kotlin.IntArray(size, init)`, the arrayGet/arraySet/forArray
            // intrinsics with NO `elem`. Derive the sized-array construction (newArrayInit/newArraySized) + stamp the
            // intrinsic element off the array operand's static type — including a basic/generic-param `enumValues`
            // array (StaticType.Surface's `enumValues` case, #77) now that the pass above has already produced it.
            // Runs EARLY (before FaithfulHintRecognition / SuspendColdLowering / BirTypeLowering) so every `elem`
            // consumer sees the stamped element.
            ArrayConstructionLowering.Apply(bir.Root, refs);
            // FAITHFUL-HINT RECOGNITION (#52 Phase 4b / #59): kotc emits the faithful op (`objMethod` toString/equals —
            // BCL-restored to ToString/Equals by ObjectSlotRename above, `concat`, `callStatic println/print`, Double/Float
            // `callInstance compareTo`) with NO type hint; bir2cir
            // RECOVERS the collection/Map/Double/Float/null operand types via StaticType (StaticTypeResolver.cs) and
            // reproduces the SAME stdlib-helper `callStatic` node kotc used to emit (clrCollToString/clrMapToString/
            // clrCollStructEquals/clrDoubleCompare/LibraryKt.toString…). (The EQEQ family is handled by
            // PrimitiveOperatorLowering above.) Runs SECOND — right after the primitive-op restore, before the compareTo
            // callInstance reaches MemberCallSubstitution's primitive-compareTo -> System.Double.CompareTo routing, and
            // before any type-erasing pass — so the inner value nodes stay pure kotlin.* and lower normally downstream.
            FaithfulHintRecognition.Apply(bir.Root, refs, localTopLevelFns);
            // CHAR.CODE + FUNCTION.INVOKE (#73 Phase 2b-2): two single-node recognitions kotc used to do — `c.code`
            // (faithful `callStatic get_code(Char)`) -> `{k:conv, to:kotlin.Int}`, and `f(x)` (faithful `callInstance
            // kotlin.FunctionN.invoke`) -> `{k:delegateInvoke}`. Runs EARLY (before NetInteropBinding / the suspend
            // + closure passes that CONSUME delegateInvoke / any type-erasing pass) and UNCONDITIONALLY (ref + app),
            // reproducing the flow that existed when kotc emitted conv/delegateInvoke directly.
            CharCodeInvokeLowering.Apply(bir.Root, refs);
            // .NET-INTEROP CALL BINDING (A2 / #61): bind a facadegen-injected .NET member call — which kotc now emits as
            // a PLAIN `callStatic`/`callInstance` by the .NET owner's FQN identity — to its CLR call SHAPE
            // (clrStatic/clrInstance/clrPropGet/clrPropSet/clrGeneric*), resolved off the loaded .NET reference
            // assemblies (ReferenceMetadataIndex's long-lived MetadataLoadContext). THIS is where .NET binding belongs
            // (the Kotlin<->CLR layer); kotc is .NET-agnostic. Runs EARLY — before ShapeSynthesis (so a generic .NET
            // method's `shapeTypes` is derived) and before every type-erasing / substitution pass — reproducing the flow
            // that existed when kotc emitted the `clr*` nodes directly. A no-op for a `kotlin.*`/local/unresolvable owner
            // (the stdlib is bound by MemberCallSubstitution off the ref.dll) and for the three CLR-only-vocab synthetics
            // kotc lowers itself (ClrEvent<T>/ClrRef<T>/byref — they don't exist in any ref, so they never resolve here).
            // Non-ref only (the stdlib self-build injects no facadegen .NET interop).
            if (!_options.RefBuild) NetInteropBinding.Apply(bir.Root, refs);
            // #11 — VALUE-TYPE PLATFORM SLOT WRITE COERCION: a `Nullable<V>`/`null` source assigned to a bare value-type
            // platform property/field slot (`ThreadLocal<Int>.Value = someIntQ`) — the WRITE twin of #8's oblivious read.
            // Unwrap a `Nullable<V>` source to the bare `V` the setter expects (`nullableValue`), and fail loud on a
            // literal `null` into a null-less value slot. Runs right after NetInteropBinding (consumes its `clrPropSet`
            // nodes) and before BirTypeLowering (owner args + the wrap's elem are still `kotlin.*`). Non-ref only.
            if (!_options.RefBuild) ValueSlotNullableWrite.Apply(bir.Root, refs);
            // #55 §4 — DERIVE the `clrGeneric*` overload-matcher `shapes` from kotc's pure-Kotlin `shapeTypes` (the
            // DECLARED parameter identities) via the @ClrTypeAlias index. kotc no longer knows the .NET shape names
            // (Int64/SByte/…) — that CLR knowledge lives HERE. Runs FIRST in the per-file loop, before ANY type-erasing
            // pass (NullableGenericReturnErasure sweeps a `nullable:gp` shapeType to `object`) and before the suspend
            // passes that read the resulting `shapes`. Pure identity in -> reflection-island string out; drops shapeTypes.
            ShapeSynthesis.Apply(bir.Root, refs, _options.RefBuild);
            // VALUE-TYPE NULLABLE-COLLECTION receiver boxing (bundle-6 BUG-1 Part A): a value-type-element collection
            // (`List<Int?>`) passed to a nullable-generic collection extension (`Iterable<T?>.filterNotNull()`) is NOT
            // covariantly `IEnumerable<object>` on the CLR — wrap the receiver in `Enumerable.Cast<object>` so it boxes
            // into a real object-enumerable. Runs FIRST, before NullableGenericReturnErasure sweeps the `nullable:gp:`
            // receiver token to `object` (this pass keys on that token). Self-gates to concrete value instantiations
            // (an open `gp:T` arg is not a value type) so it is a no-op in the rt-stdlib self-build.
            if (!_options.RefBuild) ValueTypeNullableCollectionArg.Apply(bir.Root);
            // ARRAY-ELEMENT NULLABILITY realign (C2): kotc emits `arrayOfNulls<Int>(3)` as `newArraySized elem=kotlin.Int`
            // (the non-null element) while the declaring var is `Array<Int?>` = `array:nullable:int` = `Nullable<int>[]`.
            // The array-creation then builds an `int[]`, but element stores `stelem Nullable<int>` — a struct-size mismatch
            // that corrupts memory. Realign the creation's `elem` to the declared array type's `nullable:` element so a
            // real `Nullable<int>[]` is allocated. Runs BEFORE type lowering (elem tokens are still `kotlin.*`).
            ArrayNullableElemRealign.Apply(bir.Root);
            // GENERIC-ENUM member binding (C2): `e.name` on a `T : Enum<T>` receiver -> `System.Enum.ToString()`
            // (kotc lowers a CONCRETE enum receiver directly, but a generic `gp:T` receiver falls through to a
            // `callInstance kotlin.Enum.get_name` that TypeLoadExceptions — `kotlin.Enum` lives only in the stdlib).
            if (!_options.RefBuild) EnumMemberBinding.Apply(bir.Root, localBasicEnums);
            // INHERITED kotlin.Any universal-method rebind for REFERENCE types (#96): the reference-type sibling of
            // EnumMemberBinding. A `callInstance <UserType>.GetHashCode/ToString/Equals` on a class/interface that neither
            // declares the slot nor has a resolvable base (its base is the implicit kotlin.Any) dead-ends in ilemit ->
            // rebind to an `objMethod` (virtual dispatch to the runtime slot). Fires ONLY where FindMethod would throw, so
            // a type declaring its own override is untouched. Runs AFTER ObjectSlotRename (call `method` is BCL here).
            if (!_options.RefBuild) AnySlotRebind.Apply(bir.Root, localRefTypes);
            // NULLABLE-GENERIC-RETURN erasure (ALL builds, so ref.dll + rt.dll signatures agree): a Kotlin method
            // declaring a nullable generic-parameter return (`fun <T> …(): T?`) has its nullability erased by kotc to
            // a bare `gp:T` return (Nullable<T> is inexpressible for an unconstrained T). That is CORRECT for a
            // reference T (`ldnull` is a real null) but for a VALUE T `ldnull; ret !!T` collapses to default(T)=0 —
            // null-ness is LOST (firstOrNull on a value-type list returns 0, not the element / not null-for-empty).
            // The CLR-faithful representation of a generic `T?` is `System.Object` (the boxed/erased nullable form).
            // Rewrite the return to `object`; ilemit boxes value returns and the CALL boundary converts object ->
            // the caller's Nullable<V> / reference type. Runs BEFORE the rest so type-lowering/substitution see it.
            NullableGenericReturnErasure.Apply(bir.Root);
            // GENERIC-BOUNDARY nullable-Tv READ realignment (#4; #113/#117/#120/#142 read side). The DEF-side erasure
            // above turns a member's `…Ref<T?>…` into `…Ref<object>…`, but a CALL site kotc emitted with T already
            // substituted carries the concrete `…Ref<Nullable(kotlin.Int)>…` (no bare `Tv` for the sweep to catch),
            // which lowers to the irreconcilable `Ref<Nullable<int32>>` where the member actually returns `Ref<object>`
            // (ilverify StackUnexpected). Re-derive each such call's return by substituting the owner's type-args into
            // the EraseNullableTv-applied declaration, gated to the exact object-erasure boundary, and flow the corrected
            // receiver type through so a chained read re-stamps `get_v`'s owner/return too. Each rewrite is gated to
            // the exact object-erasure boundary (IsObjectErasureOf); local generic declarations plus member flow from
            // a corrected receiver. BEFORE BirTypeLowering.
            NullableTvErasureCallRealign.Apply(bir.Root, nullableTvDeclRets);
            // FUNC-SLOT nullable-return erasure (ALL builds — the transform-side twin of the pass above): a function
            // TYPE with a nullable return (`(T) -> R?`) is kotc-tokenized `func:nullable:<ret>:<args>` (the open
            // generic view `nullable:gp:R`; a value instantiation `nullable:int`). Its only CLR rep that agrees
            // across open/value/reference instantiations is Func<…, object> (reference instantiations stay bare-typed
            // and ride Func's `out TResult` covariance into the object slot). Rewrites every such token (param slots,
            // sig strings, newDelegate/delegateInvoke funcTypes), erases the backing lambda methods' returns to
            // object, and repairs the local dataflow (see the class). MUST consume every `nullable:`-marked func ret:
            // ilemit's FuncRetEnd parses a single leading prefix and would misparse a stacked `nullable:gp:R`.
            NullableFuncReturnErasure.Apply(bir.Root);
            // VARIANCE -> INVARIANCE type-arg REALIGNMENT (il-bymap): kotc approximates a use-site `in`/`out` variance
            // projection to `kotlin.Any` (JVM-erased, harmless), so a call into an INVARIANT @ClrTypeAlias collection
            // generic (`getOrImplicitDefault<K,V>` on a `Map<String,V>` receiver) carries a `K = Any` typeArg while the
            // actual arg pins `K = String`. On the CLR `IDictionary<,>` is invariant, so the mismatch -> EntryPointNotFound
            // at `ContainsKey`. Realign each such typeArg to the actual argument's concrete type-argument. BIR-space,
            // before MemberCallSubstitution + type lowering; non-ref only. A no-op when the arg already agrees (genuine
            // `<Any>` calls) or the callee isn't a local input (an app never re-lowers a referenced stdlib body).
            if (!_options.RefBuild) MapVarianceRealign.Apply(bir.Root, calleeTypeParams, refs);
            // CALL substitution (substitute/app builds only): a member call / construction whose OWNER is a CLR-bound
            // type in the ref.dll (@ClrTypeAlias, or the legacy class-level @ClrIntrinsic) is rewritten to a plain BCL
            // call/new. This is the bir2cir home of what kotc's clrName() member routing used to do — sourced from the
            // ref.dll's @ClrIntrinsic labels, NOT from kotc. Runs BEFORE type lowering so it sees the kotlin.* owners.
            // RULE-3 HOIST (all CLR-bound alias classes): kotc emits EVERY @ClrTypeAlias class with hoistable bodies as a
            // PLAIN BIR type — alias-only files (String/Char/Boolean) AND the MIXED files (StringBuilder/collections/
            // Regex/unsigned) alike — and synthesizes NO dotkt$ClrH_* helper itself. This pass reads the ref.dll
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
            // STAR-PROJECTION LOWERING (bundle-6 `iscoll`): `x is Collection<*>` + the guarded smart-cast member access
            // (`.size`/`.iterator()`/`[i]`/…) -> the non-generic BCL interface (ICollection/IList/IEnumerable/IDictionary),
            // which a value-type collection implements regardless of element type (reified generics have no value-type
            // covariance). App build only — the ref/rt stdlib keeps the reified form, so collectionSizeOrDefault's harmless
            // capacity-hint default is preserved and map/filter do not regress. Runs before MemberCallSubstitution so it
            // sees the raw `callInstance` on the kotlin.collections.* alias.
            if (attributeTopLevelOwner) StarProjectionLowering.Apply(hoisted);
            // .NET EVENT `+=`/`-=` BINDING: kotc surfaces a .NET event as a `kotlin.clr.ClrEvent<T>` property and emits
            // the idiomatic `w.Changed += h` as the PLAIN operator call `callInstance(kotlin.clr.ClrEvent.plusAssign,
            // recv = <clrPropGet w Changed>, [h])`. This pass BINDS that to the .NET add/remove accessor — the existing
            // clrEventAdd/clrEventRemove node (ilemit unchanged), reading owner .NET type + event name straight off the
            // clrPropGet member-access. The ClrEvent<T> value is never materialized (a .NET event isn't first-class);
            // the clrPropGet receiver is consumed here, not emitted. Runs BEFORE MemberCallSubstitution so the operator
            // call — which has no ref.dll owner — is bound here. A no-op for the ref/rt stdlib self-build (no .NET events).
            hoisted = ClrEventOperatorBinding.Apply(hoisted);
            // KCLASS MEMBER BINDING: kotc emits `T::class.simpleName`/`.qualifiedName` as the PLAIN Kotlin property read
            // `callInstance(kotlin.reflect.KClass.get_simpleName/get_qualifiedName, recv = <a System.Type value>)` (the
            // `::class` receiver is already a System.Type token). A KClass is @ClrTypeAlias-ed onto System.Type, so this
            // pass derives the CLR resolution — a `clrPropGet` of `System.Type.Name`/`.FullName`. The `System.Type` /
            // BCL-member knowledge lives HERE (the Kotlin<->CLR layer), never in the kotc frontend (layer purity, mirrors
            // the exception-map / annotation-base migrations). Non-ref only: the ref stdlib keeps KClass pure Kotlin.
            if (!_options.RefBuild) hoisted = KClassMemberBinding.Apply(hoisted);
            var substituted = _options.RefBuild ? PropertyMarkerReconstruct.Apply(hoisted) : MemberCallSubstitution.Apply(hoisted, refs, localTopLevelFns, attributeTopLevelOwner);
            // Gap A — the for-loop iterator protocol over a referenced collection: re-point the desugared `<iterator>`
            // var + its synthetic hasNext/next owner at the REAL referenced kotlin.collections.Iterator<E> (app build
            // only; the stdlib self-build emits Iterator itself, so it is left synthetic there).
            if (attributeTopLevelOwner) IteratorConsumerNormalization.Apply(substituted);
            // String -> CharSequence adapter bridge: materialize a bare `System.String` flowing into a synthetic
            // `dotkt$CharSequence` slot as `new dotkt$StringCharSequence(str)` (String is sealed, can't implement
            // the synthetic interface). Runs on EVERY non-ref build — app AND the RT stdlib self-build. The RT build
            // NEEDS it too: the stdlib's own CharSequence-extension bodies widen a `String` into a `dotkt$CharSequence`
            // slot INTERNALLY (`CharSequence.indexOf(string: String)` -> the private `indexOf(other: CharSequence)`;
            // `String.trim()` -> `(this as CharSequence).trim()`), and without the wrap those compiled rt.dll bodies pass
            // a raw String where the interface is required -> InvalidProgram / EntryPointNotFound at run. The adapter is
            // injected into the rt assembly exactly once (dedup), implementing the RT's canonical `dotkt$CharSequence`,
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
            if (!_options.RefBuild) substituted = StringCharSequenceBridge.Apply(substituted, refs);
            // CATCH-CLAUSE WIDENING (bundle-6 ④): a Kotlin `catch (IndexOutOfBoundsException)` @ClrTypeAlias-es to a
            // SINGLE .NET type, but .NET index ops throw TWO distinct ones (List.get_Item -> ArgumentOutOfRangeException,
            // array -> IndexOutOfRangeException). Expand each such clause into two clauses covering BOTH so the Kotlin
            // "one catch handles any out-of-range access" semantics hold. Non-ref only (the ref surface stays pure Kotlin).
            if (!_options.RefBuild) CatchClauseWidening.Apply(substituted);
            // TRY-VALUE OPERAND HOIST (bundle-6 `tryexprop`): a value-producing try/catch used in a non-first
            // OPERAND slot (`1 + try{..}`, `"x" + try{..}`, `f(try{..})`) is hoisted out to a PRECEDING statement so
            // the CLR protected region is entered with an empty eval stack (a `leave` clears the stack -> a pushed
            // left operand would be lost -> InvalidProgram). kotc emits the correct value-form (a try-bearing
            // valueBlock + result local); this is pure CLR eval-order normalization, so it lives in bir2cir.
            if (!_options.RefBuild) TryValueOperandHoist.Apply(substituted);
            staged.Add((substituted, outputName));
        }

        // PHASE 1.5 — SUSPEND COLD LOWERING (bundle-6 P2/P3/P3-wave2a): rewrite eligible `suspend fun`s (top-level
        // statics, extensions, INSTANCE members) into the cold Continuation state-machine shape (SM class +
        // `f$dotkt_suspend` cold entry + suspend-main drain), and rewrite member/cross-file/cross-assembly suspend
        // CALLS to the callee's cold shape. Runs GLOBALLY across all files (a same-assembly cross-file suspend call
        // keeps `owner:null`, so the transformability fixpoint must span every input file). After call substitution
        // (its synthesized calls are already-final sibling/BCL shapes) and before type lowering (its kotlin.* type
        // tokens flow through BirTypeLowering). Non-v1 suspend funs are left untouched (they keep `"suspend":true`
        // for the existing ilemit throw-stub path).
        //
        // GATE (bundle-6 P5, Codex-confirmed): runs in BOTH the app build AND the rt-stdlib build — the real
        // SequenceBuilder cold coroutine code is stdlib code that must be cold-transformed in the rt build (else its
        // suspend members stay `suspend:true` -> ilemit throw-stub). Skipped ONLY in the REFERENCE build (metadata-
        // only; its bodies are body-squashed). The rt-stdlib's CLR-interop suspend fns (kotlin.clr.await/delay) are
        // NOT genuine cold bodies and are excluded INSIDE ApplyAll (InteropBridgeFileClass), so this does not corrupt
        // their ABI. (yield/yieldAll are generic-class override members, still correctly deferred by the v1 shape gate.)
        IReadOnlyDictionary<string, DotKt.Bir.TypeNode> suspendCalleeRet = null;
        if (!_options.RefBuild)
            suspendCalleeRet = SuspendColdLowering.ApplyAll(staged.Select(s => s.Root).ToList(), refs, localTypeFqns, attributeTopLevelOwner, typeSupers);

        // PHASE 1.6 — SUSPEND LAMBDA LOWERING (bundle-6 P3 wave-2b, LIVE): replace each `newSuspendLambda`
        // node with `new <mangled>_lambdaN$sm(captures..., null)` + synthesize its SuspendLambda state machine
        // (SuspendColdLowering.BuildLambdaSm, the shared FunGen machinery). Runs after the cold lowering (so a
        // suspend-lambda relocated into a synthesized SM invokeSuspend body is still caught — this pass walks
        // the newly-added SM types too) and before type lowering. kotc emits `newSuspendLambda` for every
        // `suspend` lambda literal (exercised by cases/il-lam1, il-lam2); same (non-ref) gate as the cold lowering.
        if (!_options.RefBuild)
            SuspendLambdaLowering.ApplyAll(staged.Select(s => s.Root).ToList(), localTypeFqns, suspendCalleeRet, refs);

        // PHASE 1.7 — CROSS-CLASS PRIVATE WIDENING (bundle-6 P5 BUG A): a LIFTED anon-object / closure class
        // (`dotkt_obj*`) is a SEPARATE top-level CLR class that reads its enclosing class's PRIVATE members
        // via its captured `__outer` — legal on the JVM (nested class), a System.MethodAccessException on the
        // CLR. Widen exactly the private members reached CROSS-CLASS to `internal` (assembly-visible). Runs
        // GLOBALLY, in non-ref builds, AFTER the suspend passes (so synthesized SM types are covered too) and
        // BEFORE type lowering (owner tokens are still the kotlin.* FQN that match local type names).
        if (!_options.RefBuild)
            CrossClassPrivateWidening.ApplyAll(staged.Select(s => s.Root).ToList());

        // PHASE 1.8 — GENERIC SELF INSTANTIATION (bundle-6 P5 BUG A part-2): a lifted GENERIC anon-object emits
        // its self instance accesses with the BARE type name (`dotkt_obj144`, no type args) -> ".NET method/type
        // not fully instantiated" at runtime. Derive the constructed self `dotkt_obj144[gp:T]` for those
        // executable instance accesses (kotc emits the FQN identity; bir2cir derives the CLR instantiation).
        if (!_options.RefBuild)
            GenericSelfInstantiation.ApplyAll(staged.Select(s => s.Root).ToList());

        // STAR-PROJECTION BOUND index (#2): the in-assembly generic type-param BOUNDS (`interface Key<E : Element>`
        // -> {Key: [Element]}), collected across ALL staged roots (a `Key<*>` use may live in a sibling file from Key's
        // declaration). Feeds StarProjectionBoundLowering so a `Key<object>` (kotc's star-projection erasure) is
        // repointed to `Key<Element>` for the stdlib's OWN Key; a REFERENCED Key resolves via refs.TvBound instead.
        var starProjBounds = StarProjectionBoundLowering.CollectTypeParamBounds(staged.Select(s => s.Root));

        // PHASE 2 — per-file type lowering onwards.
        var files = new List<CirFile>();
        foreach (var (substituted, outputName) in staged)
        {
            // §11 CONTINUATION-ERASURE (bundle-6 bug #5 ROOT): make the coroutine ABI monomorphic on
            // kotlin.coroutines.Continuation<object>. Every Continuation[X] type token -> Continuation[kotlin.Any]
            // (all positions), and the resumeWith(Result<X>) protocol boundary -> Result<object> (Option A: the
            // resumeWith method + its Result-construction call args). ALL builds (ref/rt agree), BEFORE type lowering
            // (kotlin.Any then lowers to object in rt/app, verbatim in ref). Un-blocks BlockOnSink/startCoroutine/
            // resumeWith dispatch (CLR interface variance does not lift value types; uniform erasure is the fix).
            ContinuationErasure.Apply(substituted);
            // SEQUENCE for-in dispatch (#37 m1 wave-2, cases/il-seqforin): a `for (x in seq)` over a Kotlin Sequence
            // lowers to `forEachInline` with a typed `IEnumerable<elem>::GetEnumerator` dispatch, but the anon Sequence
            // `sequence { .. }` returns is erased to `IEnumerable<object>` at runtime (its lifted class carries no type
            // param yet declares `IEnumerable<T>` over the enclosing method's T) -> the typed slot is absent
            // (EntryPointNotFound). Re-point such a forEachInline onto the variance-immune non-generic
            // `System.Collections.IEnumerable`/`IEnumerator` + an element cast. Non-ref; before type lowering (the src's
            // `kotlin.sequences.Sequence` FQN is still in the source vocabulary).
            if (!_options.RefBuild) SequenceForEachLowering.Apply(substituted);
            // DECL-position NRT byte collection (#37/#48): stamp `nullableFlags`/`retNullableFlags` from the SEMANTIC
            // `{t:nullable}` reference wrappers BEFORE BirTypeLowering strips them to bare types. Runs in ALL builds so
            // the ref.dll + rt.dll + app views of a signature's nullability agree (the scalar decl flags are retired).
            DeclNullableFlags.Apply(substituted, isValueFqn);
            // COMPREHENSIVE reference-nullable strip (#37/#48): remove EVERY `{t:nullable,of:<reference>}` from the whole
            // tree — decl slots AND usage positions (owner generic type-args, argTypes/typeArgs, cast/expression types)
            // that LowerNode walks as generic JSON without routing through LowerType. ilemit's MapType asserts a value
            // inner, so a reference nullable in ANY position (a `Continuation<Any?>` owner arg crashed the ref emit) must
            // be gone. Runs AFTER DeclNullableFlags (byte walk already captured the semantic nullability) and BEFORE type
            // lowering (oracle unambiguous on kotlin.* names). Value/struct/enum `{t:nullable}` stays for ilemit.
            ReferenceNullableStrip.Apply(substituted, isValueFqn);
            // #66 — RUNTIME stdlib build only: drop the `kotlin.Comparable` upper bound + `in` declaration-site variance
            // that kotc used to strip under DOTKT_STDLIB_SUBSTITUTE. kotc now emits the pure-Kotlin type params in EVERY
            // build (ref==rt BIR); this reproduces the substitution consequence so the rt.dll stays byte-identical. Runs
            // BEFORE BirTypeLowering (the constraint is still the pure `kotlin.Comparable` token here).
            if (_options.SubstituteStdlibBuild) StdlibSubstituteTypeParams.Apply(substituted);
            // STAR-PROJECTION BOUND LOWERING (#2): a `T<*>` on a self-ref-bounded generic (`Key<E : Element>`) that kotc
            // erased to `Key<object>` violates `E : Element` (illegal reified CLR instantiation). Repoint the objectish
            // arg to the type-param BOUND (`Key<Element>`), reading the constraint from the in-assembly declaration (its
            // self-build) or refs.TvBound (a referenced owner). ALL builds, BEFORE BirTypeLowering (still kotlin.Any /
            // dotted Kotlin FQNs here), so ref.dll + rt.dll + app agree on the corrected signature.
            StarProjectionBoundLowering.Apply(substituted, starProjBounds, refs);
            // #29 ROUND-TRIP RECORD: before the type transform collapses a nested read-only `kotlin.collections.List/
            // Set/Collection` (Root V) to its invariant sibling `IList`/`ICollection` — colliding with the mutable
            // sibling's own alias and losing the Kotlin read-only-vs-mutable identity — stash the PRE-collapse Kotlin
            // type of each affected decl-surface slot as an opaque string. RoundtripMetadata reads it into
            // [KotlinCollectionIdentity] so facadegen restores `List` vs `MutableList` cross-module. APP builds only
            // (the collapse is non-ref; only an app-emitted library is facadegen-re-consumed). Mirrors the #18
            // [KotlinNullableGeneric] pre-erasure record. Runs on kotlin.* names (BEFORE BirTypeLowering).
            if (attributeTopLevelOwner) CollectionIdentityRecord.Apply(substituted);
            // The type transform: lower the Kotlin type vocabulary into ilemit's CLR-codegen vocabulary, emitting a
            // BIR-SHAPED CIR (same node shape; only type strings change). No verbatim/envelope track. The ref.dll
            // @ClrTypeAlias index lowers EVERY CLR-bound type (collections/StringBuilder/Regex/... not just the
            // hardcoded primitives) wherever it appears as a type token. The struct-ness oracle drives the reference
            // `{t:nullable}` strip (a value `T?` stays `Nullable<T>`; a reference `T?` -> bare + the NRT byte above).
            var lowered = BirTypeLowering.Lower(substituted, _options.RefBuild, refs.Aliases, isValueFqn);
            // `.size` (Count) on a STAR-PROJECTED / `Any`-erased collection receiver: StarProjectionLowering already
            // re-pointed the receiver `cast` at a non-generic BCL collection interface, but MemberCallSubstitution bound
            // Count to the GENERIC `IReadOnly*<object>.Count`, absent on a value-type-arg collection (`List<int>`)
            // -> EntryPointNotFound. Re-point such Count reads at the VARIANCE-IMMUNE non-generic
            // `System.Collections.ICollection.Count`. App build only; runs AFTER MemberCallSubstitution so Count is bound.
            if (attributeTopLevelOwner) StarProjectionCountLowering.Apply(lowered);
            // Non-generic `System.IComparable` bridge (non-ref builds): a Kotlin `class C : Comparable<C>` lowers to
            // `C : System.IComparable<C>` ONLY, but the CLR dispatch spine for natural ordering goes through the
            // NON-generic `System.IComparable` (compareValues' `as IComparable` + ilemit's constrained-compareTo
            // value-type-safe fallback — boxed primitives implement IComparable but not a reified IComparable<object>).
            // Every comparable BCL type (Int32/String/DateTime) implements BOTH; a user Kotlin type must too, or a
            // stdlib body sorting it hits EntryPointNotFound/InvalidCast on `IComparable.CompareTo(object)`. Add the
            // missing interface + a `CompareTo(object)` bridge that casts and forwards to the generic CompareTo.
            if (!_options.RefBuild) ComparableBridgeSynthesis.Apply(lowered);
            // BCL-only collection slots (non-ref builds): a CONCRETE Kotlin class implementing @ClrTypeAlias'd
            // `MutableCollection`/`MutableList` (ICollection<E>/IList<E>) is missing the BCL members Kotlin's collection
            // interfaces lack — `Contains`/`CopyTo`/`get_IsReadOnly` (ICollection) and `IndexOf` (IList) — so the concrete
            // type (kotlin.collections.ArrayDeque, the AbstractMutable* bases, a MutableMap keys/values view) fails to LOAD
            // ("... does not have an implementation"), surfacing at the referencing app as "cannot resolve .NET type". Fill
            // each missing slot with an ordinary public forwarding member (wired by name by ilemit's interface loop). The
            // return-DROPPING slots (Add/set_Item/RemoveAt) are the separate family ilemit's void-drop bridge handles.
            if (!_options.RefBuild) CollectionBclSlotSynthesis.Apply(lowered);
            // #128: a Kotlin class implementing a facadegen-injected .NET generic interface instantiated with a
            // VALUE-TYPE arg (`class C : IComparer<Int>`) declares its override with the injected member's `T?` params,
            // which lower to `Compare(Nullable<int32>,…)` — but the CONSTRUCTED CLR slot wants BARE `int32`. Synthesize a
            // bare-value-signature bridge that forwards to the Nullable method so the slot binds (ilemit re-wraps args);
            // else DefineMethodOverride mismatches the slot -> TypeLoadException. Value-type type-arg positions only.
            if (!_options.RefBuild) ValueTypeIfaceSlotBridge.Apply(lowered, refs);
            // REFERENCE build only: squash every declaration body to `throw NotImplementedException()` so the ref
            // assembly is metadata-only. Keeps ALL metadata (signatures/types/supertypes/generics/attrs) intact —
            // only the body STATEMENTS change. This is what makes it safe for a bare-value kotlin.* primitive kept
            // verbatim in the ref to appear in a signature without any real body ever emitting arithmetic/box/conv IL.
            if (_options.RefBuild) RefBodySquash.Squash(lowered);
            // ROUNDTRIP METADATA (#71 S2): GENERATE every [Kotlin*]/[Nullable]/[NullableContext] attribute as ordinary
            // CIR `attrs`/`retAttrs` entries (ilemit then only STAMPS them via its generic BuildCab path — no Kotlin
            // knowledge left in ilemit). Runs on the fully-lowered decls so the materialized facts (nullableFlags,
            // suspendFnType, inlineBir, mods, suspendBridge, readOnly) are all present. SKIPPED in the runtime build
            // (`!SubstituteStdlibBuild`) — the gate that REPLACES ilemit's deleted `_stripMetadata`. Placed after
            // RefBodySquash so the squash (bodies only) does not disturb the stamped attrs. The attribute-class DEFS
            // are emitted ONCE below (SynthDefsFile), not per-file.
            if (!_options.SubstituteStdlibBuild) RoundtripMetadata.Stamp(lowered);
            // RUNTIME build: strip every applied user annotation (kotc's kotlin.Deprecated/SinceKotlin/InlineOnly/…) —
            // the job ilemit's deleted `_stripMetadata` did. DotKt.Stdlib.dll is the shipping runtime assembly (never
            // metadata-read); keep it lean, matching the old strip.
            else RoundtripMetadata.StripRuntimeAttrs(lowered);
            // #146: an APP/user-library build only REFERENCES kotlin.clr.KotlinDefault (defined in the stdlib) — re-point
            // its applied attr to the clr:-imported form so ilemit stamps it from the referenced stdlib rather than
            // skipping it. The stdlib self-build DEFINES the type locally, so it is left as the bare-FQN local stamp.
            if (_options.StdlibMode == BuildStdlibMode.App) KotlinDefaultAttrRef.Apply(lowered);
            // A file whose ENTIRE content was @ClrTypeAlias types (e.g. Primitives.kt, Comparable.kt) is now empty after
            // AliasHelperHoist dropped them — emit no CIR file for it (an empty file-class would be a pointless empty
            // static type in the assembly). Skips only when types AND methods AND fields are all empty; never in ref.
            if (!_options.RefBuild && IsEmptyCir(lowered)) continue;
            files.Add(new CirFile(outputName, lowered.ToJsonString(JsonOptions.Indented)));
        }

        // #71 S2: emit the embedded round-trip attribute-class defs ONCE per assembly, as a dedicated synthetic CIR
        // file (glob-sorted first via the `000-` prefix so its TypeDefs precede the user types, minimizing dump churn).
        // ilemit defines them like any type (no EnsureKotlinAttrs). Ref + app only — the runtime build stamps nothing.
        if (!_options.SubstituteStdlibBuild)
        {
            const string synthName = "000-dotkt-roundtrip-attrs.cir.json";
            // A real source file must never map to the reserved synthetic name — the two CirFiles would clobber on disk
            // and every Kotlin stamp would then silently vanish (its attr class absent -> BuildCab skips).
            if (files.Any(f => f.OutputName == synthName))
                throw new InvalidOperationException($"bir2cir: reserved synthetic CIR name '{synthName}' collides with an input file");
            files.Insert(0, new CirFile(synthName, RoundtripMetadata.SynthDefsFile().ToJsonString(JsonOptions.Indented)));
        }

        return files;
    }

    // Collect the FQNs of every LOCAL value type (a `kind:"enum"` real CLR enum, or a `kind:"struct"` value type),
    // recursively including nested types. Feeds the struct-ness oracle so a nullable local enum/struct keeps its
    // `System.Nullable<T>` wrapper. A `kind:"class"` (incl. a rich enum lowered to a singleton class) is a reference.
    static void CollectLocalValueTypes(JsonNode node, HashSet<string> into)
    {
        if (node is not JsonObject o || o["types"] is not JsonArray types) return;
        foreach (var t in types)
            if (t is JsonObject to)
            {
                if ((to["kind"] as JsonValue)?.GetValue<string>() is "enum" or "struct"
                    && (to["name"] as JsonValue)?.GetValue<string>() is string tn)
                    into.Add(tn);
                CollectLocalValueTypes(to, into);
            }
    }

    // A lowered CIR root that carries no types, no methods and no fields contributes nothing — its file-class would be
    // an empty static type. True once AliasHelperHoist has dropped a file whose sole content was @ClrTypeAlias types.
    static bool IsEmptyCir(JsonNode root)
    {
        if (root is not JsonObject o) return false;
        static bool Empty(JsonNode? n) => n is not JsonArray a || a.Count == 0;
        return Empty(o["types"]) && Empty(o["methods"]) && Empty(o["fields"]);
    }

    // True iff this file declares a type whose `interfaces` names the synthetic `dotkt$CharSequence` — i.e. a user
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
                    // interfaces are STRUCTURED `{t:fqn,name:…}` nodes after the m1 TYPE FLIP (was a legacy string);
                    // read via OwnerName so a user `class S : CharSequence` is still detected. A stale `as JsonValue`
                    // read missed it -> hasUserCharSeqImpl wrongly false -> CharSeqStringLowering ran on an assembly
                    // with a real polymorphic implementer, lowering its `subSequence(): CharSequence` override return to
                    // System.String (+ toString coercion) while it overrides a `dotkt$CharSequence` slot -> TypeLoad
                    // "signature of the body and declaration do not match" (il-charseq/charseqx).
                    if (TypeJson.OwnerName(i)?.TrimStart('@') == "dotkt$CharSequence")
                        return true;
        return false;
    }

    // #112 Phase 4: run the shared bir-common IrSanity over the produced CIR. Parse each CirFile once, hold the
    // JsonDocuments alive across the check (JsonElement borrows from its document), and surface an IrSanityException
    // as a plain message that Main's top-level catch renders `bir2cir: <File.kt:line: Decl>: sanity: <invariant>`.
    static void CheckCirSanity(IReadOnlyList<CirFile> files)
    {
        var docs = new List<JsonDocument>();
        try
        {
            var roots = new List<JsonElement>();
            foreach (var f in files) { var d = JsonDocument.Parse(f.Json, BirJson.DocOptions); docs.Add(d); roots.Add(d.RootElement); }
            try { IrSanity.Check(roots); }
            catch (IrSanityException ex) { throw new InvalidOperationException($"{ex.Decl}: sanity: {ex.Message}"); }
        }
        finally { foreach (var d in docs) d.Dispose(); }
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

// The three semantic build modes, selected by the single `--build-stdlib` CLI flag (absent = an app build). These
// REPLACE the old env-var soup (the stdlib-build + substitute env flags): metadata = COMPILE-set/SUBSTITUTE-unset,
// runtime = COMPILE-set/SUBSTITUTE-set, app = COMPILE-unset. (The fourth raw env combination — COMPILE-unset yet
// SUBSTITUTE-set — was never a real mode; the flag makes it unrepresentable.)
enum BuildStdlibMode { App, Metadata, Runtime }

sealed record DriverOptions(string OutDir, IReadOnlyList<string> CompileReferences, IReadOnlyList<string> Inputs, BuildStdlibMode StdlibMode)
{
    // The pure-Kotlin REFERENCE stdlib surface (`--build-stdlib=metadata` -> DotKt.Private.Stdlib.dll) keeps kotlin.*
    // type tokens verbatim and squashes bodies to a throw; EVERY other invocation — the runtime stdlib build and all
    // app builds — lowers kotlin.* to the CLR vocabulary.
    public bool RefBuild => StdlibMode == BuildStdlibMode.Metadata;

    // The RUNTIME stdlib build (`--build-stdlib=runtime` -> DotKt.Stdlib.dll) — NOT an app build. Since #66 kotc emits
    // one substitute-independent BIR, the rt-only type-param drops (kotlin.Comparable bound / `in` variance) that kotc
    // used to do live here (StdlibSubstituteTypeParams). App builds keep those, substituting the Comparable bound to
    // System.IComparable — so this is the stdlib-runtime build ONLY. (The @Clr*/NRT/[Kotlin*] metadata strip stays in
    // ilemit, gated on the SAME `--build-stdlib=runtime` flag passed to ilemit — see ilemit's _stripMetadata.)
    public bool SubstituteStdlibBuild => StdlibMode == BuildStdlibMode.Runtime;

    public static DriverOptions Parse(string[] args)
    {
        if (args.Length < 2)
            throw new UsageException("bir2cir: missing output directory or input files");

        var outDir = args[0];
        var refs = new List<string>();
        var inputs = new List<string>();
        var mode = BuildStdlibMode.App;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--compile-refs" when i + 1 < args.Length:
                    refs.AddRange(ManagedReferenceCatalog.Split(args[++i]));
                    break;
                case "--compile-refs":
                    throw new UsageException("bir2cir: --compile-refs requires a semicolon-separated path list");
                case "--ref":
                    throw new UsageException("bir2cir: --ref was replaced by --compile-refs");
                case "--build-stdlib=metadata":
                    mode = BuildStdlibMode.Metadata;
                    break;
                case "--build-stdlib=runtime":
                    mode = BuildStdlibMode.Runtime;
                    break;
                default:
                    if (args[i].StartsWith("--build-stdlib", StringComparison.Ordinal))
                        throw new UsageException($"bir2cir: --build-stdlib requires 'metadata' or 'runtime' (got '{args[i]}')");
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                        throw new UsageException($"bir2cir: unknown option '{args[i]}'");
                    inputs.Add(args[i]);
                    break;
            }
        }

        if (inputs.Count == 0)
            throw new UsageException("bir2cir: no BIR input files");

        return new DriverOptions(outDir, refs, inputs, mode);
    }
}

sealed record BirFile(string Path, string Json, JsonNode Root, SuspendShapeAnalysis Suspend, CallSiteAnalysis Calls);
sealed record CirFile(string OutputName, string Json);

static class JsonOptions
{
    // MaxDepth raised off the STJ default (64) so a deeply-nested-lambda CIR document serializes (#147).
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, MaxDepth = DotKt.Bir.BirJson.MaxDepth };
}

sealed class UsageException : Exception
{
    public UsageException(string message) : base(message) { }
}
