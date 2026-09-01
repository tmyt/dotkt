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
            args = ResponseFileArguments.Expand(args);
            // The shared BIR/CIR codec (bir-common) is <Compile Link/>-shared, so it has no project of its own
            // and no test host. Running its self-tests HERE exercises the exact compiled copy this tool reads and
            // writes documents with; tests/ir/run-schema.sh drives it beside the document-level freeze checks.
            if (args.Length == 1 && args[0] == "--selftest")
            {
                TypeNodeSelfTest.Run();
                MemberRefNodeSelfTest.Run();
                AliasConstructorDelegationExpansion.SelfTest();
                StdlibBindingOverlay.SelfTest();
                MaterializedBirPayload.SelfTest();
                MaterializedExecutable.SelfTest();
                NullableWitnessDemand.SelfTest();
                DriverOptions.SelfTest();
                return 0;
            }
            var options = DriverOptions.Parse(args);
            new Pipeline(options).Run();
            return 0;
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("usage: bir2cir <out-dir> [--compile-refs <dll;dll;...>] [--build-stdlib=metadata|runtime --stdlib-bindings <path>] [--reflection-restricted] <file.bir.json>...");
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
        if (_options.ReflectionRestricted
            && (ForeignStarProjectionBinding.UsedRuntimeFallback || StarProjectionLowering.UsedRuntimeFallback))
            Console.Error.WriteLine(
                "bir2cir: warning DOTKTSTAR001: a CLR star projection uses reflection; "
                + "NativeAOT/trimming must preserve the referenced generic type and member metadata");
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
        var birRoots = birFiles.Select(b => b.Root).ToList();
        // CLR-only facts for the compiler-provided stdlib are authored in a checked sidecar so the common Kotlin
        // sources remain upstream-identical. Apply those exact declaration-identity bindings before any pass snapshots
        // source names or annotations. Ordinary app/library builds cannot opt into this trusted-stdlib input.
        StdlibBindingOverlay.Apply(birRoots, _options.StdlibBindings);
        // #395: snapshot frontend declaration identity before ANY Kotlin-to-CLR representation pass can rename,
        // move, clone, or synthesize a declaration. These are source facts, never a physical-name reverse inference.
        var declarationSemanticSignatures = DeclarationIdentityBinding.PreserveSourceFacts(birRoots);
        var localDeclarationIds = DeclarationIdentityBinding.CollectDeclarationIds(birRoots);
        // Kotlin `reified` is a declaration fact; the hidden nullable-instantiation Boolean is a distinct CLR ABI
        // demand. Derive the latter from nullable-sensitive operations and exact call/lift correspondences as one
        // module-wide fixed point before per-file materialization starts.
        var nullableWitnessDemand = NullableWitnessDemand.Collect(birRoots, refs);
        // The combined value-type oracle covers referenced/foundational values plus local structs/enums across every
        // input file. The Sequence element-view boundary asks the nullable-generic rule whether its source element will
        // be object-reified, so it shares this oracle instead of restating today's two stdlib source spellings. The
        // declaration/use-axis nullable-generic passes below retain the same oracle.
        var localValueTypeFqns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in birFiles) CollectLocalValueTypes(b.Root, localValueTypeFqns);
        ValueTypeOracle isValueFqn = type => refs.IsValueType(type) || localValueTypeFqns.Contains(type.Name);
        if (!_options.RefBuild) SequenceElementAdapterLowering.Apply(birRoots, isValueFqn);
        var companionRepresentations = CompanionRepresentationLowering.Apply(birRoots);
        // CLR multiplies static storage and .cctors on a generic TypeDef per constructed type. Kotlin companion-block
        // statics are one declaration independent of the owner's T, so materialize their non-generic carrier before
        // any module-wide declaration index is captured.
        GenericStaticOwnerBinding.Materialize(birRoots);
        // A receiverless companion extension needs its associated Kotlin classifier in the physical member identity.
        // Consume that explicit declaration/use fact before any name-keyed index or inline payload is captured.
        var companionExtensionBindings = CompanionExtensionBinding.Apply(birRoots, refs);
        // #397: accessor declarations arrive in Kotlin vocabulary (source property identity + get/set role).
        // Materialize the dedicated CLR spelling once, after #389 has selected any companion-extension core/container
        // representation and before a name-keyed declaration index is captured.  The explicit identity remains on
        // each declaration for semantic consumers and is stripped only at the BIR->CIR boundary.
        KotlinPropertyAccessors.AllocateDeclarationsAndProperties(birRoots);
        var localPropertyDeclarations = MemberCallSubstitution.CollectLocalPropertyAccessors(birRoots);
        var ownershipFacts = TypeOwnershipLowering.PrepareOwnershipFacts(birRoots);
        // #68 (PART 2): kotc emits the PLAIN Kotlin identity `kotlin.CharSequence` at every CharSequence use site (no CLR
        // synthetic name — kotc knows nothing of the synthetic). Recognizing `kotlin.CharSequence` as a synthesize-target is
        // a bir2cir concern (the Kotlin<->CLR layer), so SUBSTITUTE it here — as a one-type hardcode, exactly like the ref.dll
        // @ClrTypeAlias types substitute `kotlin.String` -> `System.String`. It runs FIRST (before the `hasUserCharSeqImpl`
        // detection, CharSeqStringLowering, and the per-file SharedSyntheticSynthesis trigger) so every downstream pass sees
        // the canonical `dotkt$CharSequence` identity defined by SharedSyntheticSynthesis. Only a
        // `{t:"fqn"}` type-reference NAME is rewritten (a type DECLARATION's own `name` sits under `kind`, not `t`, so real
        // kotlin.CharSequence declarations — if any — are untouched).
        foreach (var b in birFiles) SubstituteCharSeqIdentity(b.Root);
        // A delegated CLR-event declaration and a subscription through that Kotlin wrapper may be in sibling source
        // files. Snapshot the module-wide relation before any declaration-normalizing pass can consume or rebuild the
        // kotc directive; ClrEventSubscriptionBinding later consumes this immutable physical-owner index per file.
        var clrEventForwardedOwners = ClrEventSubscriptionBinding.CollectForwardedOwners(birFiles.Select(f => f.Root));

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
        // (app build -> clr: base + pendingOverrideOwner linkage).
        var localTypeFqns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in birFiles)
        {
            if (b.Root is not JsonObject ro) continue;
            if (ro["types"] is JsonArray ts)
                foreach (var t in ts)
                    if (t is JsonObject to && (to["name"] as JsonValue)?.GetValue<string>() is string tn)
                        localTypeFqns.Add(tn);
            // A FILE CLASS is a type this compilation emits just as much as a declared one — it simply has no
            // row in `types`, because ilemit builds it from the file's top-level members. Leaving it out let a
            // reference assembly declaring the same file class win over the source in front of us, which is
            // precisely the precedence #15 exists to fix; it stayed invisible only while the mis-binding
            // produced a signature rather than a named member.
            if ((ro["fileClass"] as JsonValue)?.GetValue<string>() is string fileClass && fileClass.Length > 0)
                localTypeFqns.Add(fileClass);
        }
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

        // OVERRIDE SLOTS, THE HALF THAT MOVES A DECLARATION (#86 D3). An override narrowing a base `T?` NESTED in a
        // constructed generic (`Box<T?>` overridden as `Box<Int?>`) has no conversion available in either direction —
        // `Box<object>` and `Box<Nullable<int32>>` are unrelated invariant reified generics — so that slot must adopt
        // the base's shape. It happens over every file at once, because a base may be declared in another one, and
        // BEFORE the declaration snapshot below: a call reached through the DERIVED type is typed against that
        // declaration, so a snapshot taken first would type it against the slot the override no longer has. The
        // top-level `object` seam is bridged instead, beside the other bridge synthesizers.
        KotlinOverrideSlotBridge.PropagateErasedSlots(birFiles.Select(f => f.Root), isValueFqn, refs);

        // Snapshot every LOCAL generic type's declared member returns BEFORE the per-file DEF-side EraseNullableTv
        // (NullableGenericErasure runs inside the transform loop, mutating declarations in place). Feeds
        // NullableTvErasureCallRealign so a `Box<Int>.get_a()` call across the generic boundary re-derives its
        // return from the (erased) declaration instead of kotc's over-substituted `Ref<Nullable<Int>>` (#4).
        var nullableTvDeclRets = NullableTvErasureCallRealign.CollectDeclaredMemberRets(birFiles.Select(f => f.Root));
        // Snapshot every method whose source-level `T` return is produced by an unchecked cast from a nullable/object
        // carrier.  Its physical CLR ABI must return object so narrowing can be delayed until the value is consumed;
        // the exact Kotlin `T` return is retained through [KotlinType].  Collected module-wide so cross-file local calls
        // are matched by owner + substituted signature, never by a library/name heuristic.
        var uncheckedGenericCastRets = UncheckedGenericCastReturnErasure.Collect(birFiles.Select(f => f.Root));

        // The local RICH-enum type names (a `kind:"class"` decl carrying the faithful `enumRich:true` marker), across
        // ALL input files: EnumIntrinsicLowering lowers `enumValues<RichEnum>()` to the synthesized static values()
        // (not the System.Enum-reflection semantic node — a rich enum is a plain singleton class).
        var localRichEnums = EnumIntrinsicLowering.CollectRichEnums(birFiles.Select(f => f.Root));

        // The local BASIC (value-type, `kind:"enum"`) enum type names across ALL files — EnumMemberBinding rebinds a
        // `System.Enum`-inherited Object-slot call (`E.A.toString()`) on such an owner to an `objMethod`. Must be
        // module-wide (not per-file): the call site can be in a different .bir.json than the enum declaration. Only the
        // LOCAL enums need it — a CROSS-ASSEMBLY enum's inherited-member call is closed one pass earlier by
        // NetInteropBinding (reference-KLIB-projected owner -> `clrInstance`, a `constrained. callvirt`), and a klib-external
        // `kotlin.*` enum arrives from kotc already as an `objMethod`; neither reaches this local `callInstance` gap.
        var localBasicEnums = EnumMemberBinding.CollectBasicEnums(birFiles.Select(f => f.Root));

        // EXPLICIT CLR ENUMS (#526): validate kotc's source-owned ordered constant map and resolve the Kotlin
        // integral type to its exact CLR underlying type before any use-site enum operation is lowered.
        var localExplicitEnums = ClrEnumLowering.Apply(birFiles.Select(f => f.Root));

        // The local reference TYPES (classes + interfaces) module-wide (name -> declared universal slots + base) —
        // AnySlotRebind rebinds a dead-ending `callInstance <UserType>.GetHashCode/ToString/Equals` (a fake override
        // inherited from the implicit kotlin.Any, which ilemit cannot resolve because the base field is absent) to an
        // `objMethod`, exactly as EnumMemberBinding does for value-type enums (#96). Module-wide because the call site
        // may live in a different .bir.json than the type declaration, while ilemit's emitted-type table is assembly-wide.
        var localRefTypes = AnySlotRebind.CollectLocalTypes(birFiles.Select(f => f.Root));

        // The static members every LOCALLY DECLARED type carries, module-wide: a `C.f()` call site may live in another
        // .bir.json than C's declaration. Feeds LocalStaticOwnerBinding, which moves such a call's declaring type onto
        // the CLR owner axis before the ownerless (reference-universe) recognizers get a look at it.
        var localStatics = LocalStaticOwnerBinding.Collect(birFiles.Select(f => f.Root));
        // Property-reference values may be written into a function-typed field declared by a sibling BIR file.
        // Preserve the module's declared field slots before per-file lowering so the representation boundary never
        // has to infer a field type from a use or from a generated property's physical class.
        var propertyFunctionFields = PropertyReferenceFunctionLowering.CollectFieldSlots(
            birFiles.Select(f => f.Root));

        // #63 (F4): the app-local file-class method names a `newDelegate` target `ldftn`-resolves against, collected
        // MODULE-WIDE across every input file — ilemit's FindStatic binds a delegate method by bare name against ALL
        // IsFileClass types in the module (and the inline stash spans all files), so a carrier materializing a SIBLING
        // file's lifted generated method is app-local. Pre-collect ONCE (InlineSplice.Apply runs per file) and pass in, so a
        // cross-file materialization is not mis-judged non-app-local and refused loud. Nested-TYPE member methods stay
        // excluded (ilemit's file-class-only ldftn universe) — only the FILE scope was wrong (regression from 923a820).
        var appLocalFileClassMethods = InlineSplice.CollectAppLocalMethodNames(birFiles.Select(f => f.Root));
        var inlineDispatchHierarchy = InlineSplice.CollectDispatchHierarchy(birFiles.Select(f => f.Root));
        var genericDowncastHierarchy = GenericDowncastRealignment.Collect(birFiles.Select(f => f.Root));
        // INLINE-BIR STASH (#71/#75 S1): after module-wide companion representation selection and before ordinary
        // per-file lowering, capture every `mods.inline` method's representation-selected BIR body into an OPAQUE
        // `inlineBir` base64 string (ilemit stamps it verbatim as the [KotlinInline] carrier) plus an in-memory
        // `owner|name|pc|ga -> decl` index (dormant same-module infra).
        // Runs across ALL files here so the index spans the compilation, and so every downstream walker sees the
        // captured body already inert (a JsonValue string) — RefBodySquash then squashes only the executable `body`.
        InlineBirStash.Reset();
        foreach (var b in birFiles) InlineBirStash.Stash(b.Root);

        // An @ClrTypeAlias constructor may be a Kotlin adapter whose physical target has a different signature.
        // Capture the module-wide declaration graph before AliasHelperHoist drops those TypeDefs.  A consumer expands
        // it at the head of phase 1 so every expression copied out of an alias constructor flows through the same
        // semantic/representation lowerings as an expression authored directly in that consumer.
        var aliasConstructorDelegations = AliasConstructorDelegationExpansion.Collect(
            birFiles.Select(file => file.Root), refs, isValueFqn, carryForReference: _options.RefBuild);

        // PHASE 1: per-file transforms up through the CharSequence bridge. Collect the staged roots so the
        // suspend cold lowering can run GLOBALLY (a same-assembly cross-file suspend call keeps `owner:null`,
        // so its cold-entry callee may live in another file — the suspend-member registry spans all files).
        var staged = new List<(JsonNode Root, string OutputName)>();
        foreach (var bir in birFiles)
        {
            var outputName = OutputNameFor(bir.Path);
            if (!_options.RefBuild) aliasConstructorDelegations.Apply(bir.Root);
            // SYNTHETIC CLR-REPRESENTATION TYPES (#52 kotc-purity): kotc emits only the FACTS — a capturing lambda's
            // `newClosure` carries a transient `synthClass` ingredient bag; a CharSequence use references the generated
            // bridge identity; a heap ref-cell rides the `refTypes` registry. After raw payload splicing and witness
            // capture below, ClosureSynthesis and SharedSyntheticSynthesis materialize those CLR types into `types`.
            // They run before ForIn classification and the global SuspendColdLowering declaration index; closure
            // synthesis runs first so SharedSyntheticSynthesis can scan the completed invoke bodies.
            // OBJECT-SLOT RENAME (#73 M5): restore the System.Object BCL slot names (ToString/GetHashCode/Equals) that
            // kotc stopped emitting — it now emits the Kotlin names (toString/hashCode/equals) + the pure-Kotlin facts
            // `objectOverride:true` (decl) / `anySlot:true` (call). Runs before semantic recognition passes and
            // unconditionally (ref + rt + app): physical object slots are one cross-build contract, so ref.dll
            // declarations and the emitted-name-keyed member index must be canonicalized together; placing it first
            // gives every downstream pass BCL spellings.
            ObjectSlotRename.Apply(bir.Root);
            // PRECONDITION / ERROR FAMILY (#73 M6): kotc emits the FAITHFUL top-level call (require/check/error/TODO/
            // requireNotNull/checkNotNull as `callStatic owner:null`, noWhenBranchMatchedException as the faithful
            // `kotlin.internal.ir` intrinsic). These @InlineOnly helpers have NO rt.dll body, so bir2cir realizes their
            // Kotlin failure semantics as FQN-keyed cond/throwExpr/valueBlock CIR over bare Kotlin
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
            if (_options.StdlibMode == BuildStdlibMode.App)
                CompanionRepresentationLowering.BindSpliceUses(bir.Root, refs);
            InlineSplice.Apply(
                bir.Root, refs, appLocalFileClassMethods, inlineDispatchHierarchy, companionExtensionBindings);
            // VALUE-POSITION JOIN WIDENING (#86 §3): a `try`/`catch` or `if/when` join the frontend resolved to a
            // NON-nullable type while one branch yields a literal `null` — kotc records exactly that fact on the
            // declaration it mints for the join, and the physical consequence is decided HERE: a VALUE join widens to
            // `Nullable<V>` (a null into a bare `int32` slot is a reference stored over a value), a reference join
            // does not. Runs immediately after InlineSplice, so a spliced stdlib inline body's own joins — the shape
            // that drops the `?` — are widened before anything downstream reads their type, and long before type
            // lowering, while the join type is still `kotlin.*`.
            ValueJoinNullWidening.Apply(bir.Root, isValueFqn);
            // CROSS-MODULE DEFAULT-ARG SPLICE (#146): fill a call's OMITTED defaulted args (kotc's `defaultArg`
            // placeholders) from the callee's `[kotlin.clr.KotlinDefault]` BIR on the referenced .dll. Runs HERE — phase 1,
            // after InlineSplice/join widening and before payload renormalization, ClosureSynthesis,
            // MemberCallSubstitution, and BirTypeLowering —
            // so the spliced RAW default expression (a `newDelegate` re-hoisted app-local, a `callStatic owner:null`, a
            // const) re-lowers IN THIS app's context, exactly like an inline-body splice. Ownerless (name|arity), because
            // the owner is not yet attributed. APP builds only (user libraries build in App mode too — Metadata/Runtime are
            // stdlib-self-build flags): a `defaultArg` placeholder is born ONLY on a reference-KLIB callee
            // (the cross-module IrErrorExpression path), and the ref/rt stdlib self-builds reference no DotKt assembly, so no
            // external callee — hence no placeholder — exists there. Running on a self-build would mutate its
            // RefBodySquash/RoundtripMetadata declaration set without any default payload to consume.
            if (attributeTopLevelOwner)
            {
                DefaultArgSplice.Apply(bir.Root, refs);
                // A @KotlinDefault payload is opaque BIR during the module-wide companion-extension pass above.
                // It has now become an ordinary use subtree, so consume its explicit receiver/name/role facts before
                // any downstream name-keyed lowering sees it.
                CompanionExtensionBinding.BindMaterializedUses(bir.Root, companionExtensionBindings, refs);
            }
            // Inline/default payloads are authored before their consumer lexical owner exists. The splice transfers
            // that exact semanticOwner fact above; now normalize any newly materialized synthClass generic prefix
            // before ClosureSynthesis turns it into a CLR class. The module-wide first pass normalized declarations;
            // only this consumer root changed, so reuse its owner index instead of re-walking every file per splice.
            TypeOwnershipLowering.PrepareSplicedOwnershipFacts(bir.Root, ownershipFacts);
            // Kotlin local functions remain lexical BIR declarations through every raw-body splice above. Consume the
            // explicit declaration-id relation now and select their CLR MethodDef owner before representation synthesis
            // (closures/suspend lambdas may carry calls to those declarations). No FileClass/name heuristic is involved.
            LocalFunctionLowering.Apply((JsonObject)bir.Root, refs);
            // Bind local and ProjectReference companion values after every raw-BIR splice and before an evaluation
            // plan can materialize a representation-less flat receiver as a CLR local.
            CompanionRepresentationLowering.BindUses(
                bir.Root, companionRepresentations, refs,
                bindExternal: _options.StdlibMode == BuildStdlibMode.App);
            // CLR [Flags] ENUM OPERATIONS (#496): dll2klib publishes metadata-only typed Kotlin declarations and kotc
            // carries the exact selected declaration's semantic role. Resolve the target enum and underlying width
            // here, then author an ordered call-evaluation plan around explicit bit/comparison CIR vocabulary. This
            // must run immediately before CallEvalLowering: contains reads its mask twice, so that existing single
            // evaluation authority materializes receiver/argument values once in Kotlin order.
            ClrFlagsOperationLowering.Apply(bir.Root, refs);
            // CALL-EVALUATION PLAN LOWERING (§2.7). EVERY splice that can add a reader to one of a call's values has
            // now run, so each plan's readers are final and its bindings lower to locals in Kotlin order: a
            // single-reader binding back into its own slot, a shared one into a `var`, a constructor delegation's into
            // `preStmts`. Unconditional (ref + rt + app): kotc emits a plan wherever a fill can duplicate a value, and
            // a stdlib self-build has same-module fills of exactly that shape. From here down no pass sees plan
            // vocabulary — the pass asserts that itself, and verify-schema enforces the same phase split.
            CallEvalLowering.Apply(bir.Root, isValueFqn);
            // NOTHING-VALUE TERMINATION (#197): a `kotlin.Nothing`-typed expression delivers no value, but its CLR
            // erasure (`object`) still reaches whatever slot reads it — the other arm of an if/when merge, a `ret`, a
            // typed local — so the verifier sees an `object` where a `string` belongs. Terminate such a position in
            // place (`else boom()` -> `else throw boom()`) so nothing is merged at all. Runs HERE: every splice that
            // can introduce a `Nothing` call has run, the plan vocabulary is gone, and the type is still spelled
            // `kotlin.Nothing`. BEFORE the suspend transform, whose `__cond$` machinery already stores nothing for a
            // `throwExpr` arm — so one rule covers the plain and the state-machine lowering alike.
            NothingValueTermination.Apply(bir.Root);
            // NULLABLE-INSTANTIATION WITNESS ABI (#316/#466): every raw inline/default payload is materialized now,
            // while exact declaration identities and Kotlin type arguments are still present. Record structurally
            // demanded call witnesses and capture lifted-frame witnesses before ClosureSynthesis/physical type
            // lowering can erase those facts; Kotlin `reified` remains a separate round-trip declaration fact.
            ReifiedNullabilityWitnessLowering.Apply(bir.Root, nullableWitnessDemand, refs);
            ClosureSynthesis.Apply(bir.Root, refs);
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
            // the app form rewrites to a cross-module counter loop. Runs FIRST so the realized callInstance / forRange
            // nodes enter the ordinary downstream CIR pipeline.
            RangeForLowering.Apply(bir.Root, !attributeTopLevelOwner);
            // RANGE MEMBERSHIP (#73 M2): kotc emits the FAITHFUL `contains` member call for `x in a..b` (by identity,
            // NO comparison synthesis — its old bare-name lowering MISCOMPILED a user rangeTo/contains type). Lower the
            // membership to the short-circuit `(x >= a && x <op> b)` fast path FQN-keyed — only for a stdlib primitive
            // range (`kotlin.ranges.{Int,Long,Char}Range` contains over an un-materialized `rangeTo`/`until`/`rangeUntil`).
            // Runs BEFORE RangeConstructionLowering (which would else materialize the recv rangeTo into `new IntRange`)
            // so the recv still carries the inline bounds. The fast path must not reorder the membership: `x in a()..b()`
            // builds the range first, so both bounds run, in order, before the subject — the pass binds lo/hi/subject to
            // temps to keep that, and splices only an operand re-reading cannot move (ValueStability.IsReReadable).
            RangeMembershipLowering.Apply(bir.Root, localTopLevelFns, attributeTopLevelOwner);
            // VALUE-POSITION RANGE CONSTRUCTION (#73 Phase 2b-1): kotc emits the FAITHFUL `callInstance
            // kotlin.Int.rangeTo(b)` for `a..b` / `a..<b`; materialize the stdlib `new IntRange/LongRange/CharRange`
            // HERE (the Kotlin<->CLR realization). Runs before MemberCallSubstitution (whose Rule-4 gate would refuse
            // the unbound `kotlin.Int.rangeTo`) so the realized construction enters the ordinary downstream pipeline.
            RangeConstructionLowering.Apply(bir.Root);
            // PRIMITIVE OPERATORS (#52 Phase 5): realize faithful primitive member calls as CIR binOp/unaryOp nodes.
            // Runs in ref + app before body-squash/type/suspend consumers, so a ref-build ctor field-init or base-arg
            // (not body-squashed) carries a raw IL operation rather than an unresolvable bodyless builtin call.
            PrimitiveOperatorLowering.Apply(bir.Root, refs);
            // ENUM REIFIED INTRINSICS (#73): kotc emits the faithful top-level `callStatic owner:null method:enumValues
            // typeArgs:[T]` for `enumValues<T>()`/`enumValueOf<T>()`/`enums.enumEntries<T>()`/`enumEntriesIntrinsic<T>()`.
            // Re-emit the same BIR vocabulary — rich enum -> static values()/valueOf(), basic/generic-param -> semantic
            // enumValues/enumParse — deriving rich-vs-basic from the explicit producer fact (local `enumRich:true`,
            // referenced trusted [KotlinRichEnum]). Runs
            // BEFORE ArrayConstructionLowering (#77): a `for (x in enumValues<Color>())` / `.entries` for-loop wraps
            // this call in a `forArray` whose element ArrayConstructionLowering derives via StaticType off the ALREADY-
            // lowered `enumValues`/rich-`values()` node — so the reified top-level intrinsic must already be in its
            // final semantic shape when element derivation runs. Entries family: App-build sites only (stdlib
            // self-build keeps the filler body — see
            // EnumIntrinsicLowering).
            EnumIntrinsicLowering.Apply(
                bir.Root, localRichEnums, localTopLevelFns, attributeTopLevelOwner, refs);
            // ENUM ENTRY VALUES: kotc preserves owner + entry-name Kotlin identity. Resolve a referenced rich enum's
            // carrier-mapped singleton field, or a CLR enum's potentially sparse/negative/aliased physical constant,
            // from the exact compile reference here.
            EnumValueLowering.Apply(bir.Root, refs, localBasicEnums, localExplicitEnums);
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
            // recovers the collection/Map/Double/Float/null operand types via StaticType (StaticTypeResolver.cs) and
            // selects the stdlib helper required by the semantic operation (clrCollToString/clrMapToString/
            // clrCollStructEquals/clrDoubleCompare/LibraryKt.toString…). (The EQEQ family is handled by
            // PrimitiveOperatorLowering above.) Runs SECOND — right after the primitive-op restore, before the compareTo
            // callInstance reaches MemberCallSubstitution's primitive-compareTo -> System.Double.CompareTo routing, and
            // before any type-erasing pass — so the inner value nodes stay pure kotlin.* and lower normally downstream.
            FaithfulHintRecognition.Apply(bir.Root, refs, localTopLevelFns);
            // CHAR.CODE + FUNCTION.INVOKE (#73 Phase 2b-2): realize two faithful Kotlin operations — `c.code`
            // (faithful `callStatic get_code(Char)`) -> `{k:conv, to:kotlin.Int}`, and `f(x)` (faithful `callInstance
            // kotlin.FunctionN.invoke`) -> `{k:delegateInvoke}`. Runs EARLY (before NetInteropBinding / the suspend
            // + closure passes that CONSUME delegateInvoke / any type-erasing pass) and unconditionally (ref + app).
            CharCodeInvokeLowering.Apply(bir.Root, refs);
            // .NET-INTEROP CALL BINDING (A2 / #61): bind a reference-KLIB-projected .NET member call — which kotc now emits as
            // a PLAIN `callStatic`/`callInstance` by the .NET owner's FQN identity — to its CLR call SHAPE
            // (clrStatic/clrInstance/clrPropGet/clrPropSet/clrGeneric*), resolved off the loaded .NET reference
            // assemblies (ReferenceMetadataIndex's long-lived MetadataLoadContext). THIS is where .NET binding belongs
            // (the Kotlin<->CLR layer); kotc is .NET-agnostic. Runs EARLY — before ShapeSynthesis (so a generic .NET
            // method's `shapeTypes` is derived) and before every type-erasing / substitution pass — reproducing the flow
            // that existed when kotc emitted the `clr*` nodes directly. A no-op for a `kotlin.*`/local/unresolvable owner
            // (the stdlib is bound by MemberCallSubstitution off the ref.dll) and for the three CLR-only-vocab synthetics
            // kotc lowers itself (ClrEvent<T>/ClrRef<T>/byref — they don't exist in any ref, so they never resolve here).
            // Non-ref only (the stdlib self-build injects no dll2klib .NET interop).
            if (!_options.RefBuild) NetInteropBinding.Apply(bir.Root, refs);
            // PROPERTY-REFERENCE FUNCTION MATERIALIZATION (#242): kotc emits a genuine KProperty construction plus
            // its callable KProperty interface fact. Once every inline/default payload has been spliced and .NET calls
            // expose their declared parameter vectors, adapt any such value that fills a function-typed slot to a
            // forwarding CLR closure. The walk is structural over locals/fields/returns/call and delegate arguments/
            // array writes/joins, so one representation rule covers aliases, vararg packs, and nested values alike.
            PropertyReferenceFunctionLowering.Apply(bir.Root, propertyFunctionFields);
            // A Kotlin static member call whose declaring type is emitted by THIS compilation: the declaring identity
            // IS the CLR owner, so move it onto the owner axis now — after the .NET binder has had first refusal and
            // before the ownerless recognizers below, which resolve names against the reference universe and must
            // never be offered a call whose owner is already known. Runs in every build: a ref build declares the same
            // statics, and its bodies are squashed later.
            LocalStaticOwnerBinding.Apply(bir.Root, localStatics);
            if (!_options.RefBuild) CompanionRepresentationLowering.AssertNoCompanionValues(bir.Root);
            // #11/#501 — VALUE-TYPE PLATFORM SLOT ACCESS COERCION: reconcile reflected bare/Nullable<V> property or
            // field slots with the Kotlin platform view on both writes and reads. Writes receive explicit wrap/unwrap;
            // reads carry the reflected physical result so the receiving local cannot reinterpret Nullable<V> as V.
            // Runs right after NetInteropBinding and before BirTypeLowering. Non-ref only.
            if (!_options.RefBuild) ValueSlotNullableWrite.Apply(bir.Root, refs, isValueFqn);
            // W1-S1 (#46/#44): the `clrGeneric*` overload-matcher is now the STRUCTURED `resolvedMemberParams` descriptor
            // NetInteropBinding carries (the callee's declared param TypeNodes) — BirTypeLowering lowers it and ilemit
            // exact-matches it. The retired ShapeSynthesis pass (lossy `shapes` string derived off the @ClrTypeAlias
            // index) is DELETED; ilemit no longer re-resolves the overload by name/arity/shape-string.
            // KOTLIN COVARIANCE OVER A VALUE ELEMENT at an object-erased `Iterable<T?>` slot: `List<Int>` IS an
            // `Iterable<Int?>` in Kotlin, while an `IReadOnlyList<int32>` is not an `IEnumerable<object>` on the CLR
            // (a reified argument is invariant for a value type), so the callee's `GetEnumerator` is not found. Wrap
            // that argument in `Enumerable.Cast<object>`, which boxes each element into a real object-enumerable.
            // Only an `Iterable<T?>` slot, per position — that is the one slot the wrap's own `IEnumerable<object>`
            // inhabits. Runs FIRST, before the erasure sweeps the slot's `Nullable(Tv)` to `object` (this pass keys
            // on it); self-gates to concrete value instantiations, so it is a no-op in the rt-stdlib self-build.
            if (!_options.RefBuild) ValueElementIterableCoercion.Apply(bir.Root, isValueFqn);
            // ARRAY-ELEMENT CANONICALIZATION (#86 D2): an `Array<X?>` with a possibly-value `X` is `object[]`, so an
            // array CREATION filling such a slot allocates `object[]` too. kotc writes the source's own element there
            // (`arrayOf(1,2,3)` into an `Array<Int?>` says `kotlin.Int`), which is not a `Nullable(...)` the erasure
            // sweep below could see — leaving it allocates an `int32[]` under an `object[]` slot, and the boxed
            // element stores then corrupt memory rather than failing to type-check. Runs BEFORE type lowering (elem
            // tokens are still `kotlin.*`) and before the erasure, which then finds the two already agreeing.
            ArrayNullableElemCanonicalization.Apply(bir.Root, isValueFqn);
            // NULLABLE IS-TEST (`x is T?`): null IS a member of a nullable type in Kotlin, and the frontend's
            // else-branch smart-cast to a NON-null `x` depends on it. `isinst` never matches null, so mark the node
            // and let ilemit add the null-accepting branch. Runs BEFORE type lowering, which erases the `?` on the
            // type operand (every CLR reference is nullable, so the lowered type cannot carry the signal), and AFTER
            // InlineSplice so a spliced inline body's own is-tests are marked too.
            NullableIsInstMatch.Apply(bir.Root);
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
            // NULLABLE-GENERIC erasure — THE DECLARATION AXIS of #86's erasure invariant (ALL builds, so ref.dll +
            // rt.dll + the app's view of a signature agree). `Nullable(Tv)` (a nullable UNCONSTRAINED type variable
            // `T?`) has no CLR type of its own: `Nullable<T>` is inexpressible for an unconstrained T, and a bare `!T`
            // slot collapses a null to `default(T)` = 0 at a value instantiation (firstOrNull on a value-type list
            // returning 0 rather than null-for-empty; `ldnull` into an int32 slot failing JIT verification). The one
            // CLR-faithful representation is `System.Object` — the spec-defined boxed form of `Nullable<V>`, which
            // carries a real null for a value AND a reference instantiation alike. This pass applies
            // `physical(s) = Erase(declaredKotlinType(s))` to EVERY declaration slot (return, method param, ctor param,
            // field, property, body local, nested type-arg, array element, function-type position, call `sig`) and
            // records the pre-erasure Kotlin type — plus, at an erased HEAD, its NRT byte — for the
            // [KotlinNullableGeneric] round-trip. Runs BEFORE the rest so type-lowering/substitution see it.
            if (attributeTopLevelOwner) ClrMemberResolution.CaptureReferencedStaticCallSignatures(bir.Root);
            // A suspend result is Kotlin declaration meaning. Freeze it while the frontend TypeNode is still pristine;
            // NullableGenericErasure below is allowed to change the declaration's physical CLR representation but not
            // the cross-module fact. Later synthesis must copy this explicit fact rather than reconstructing it from an
            // already-erased `suspendRet`.
            RoundtripMetadata.FreezeSuspendResults(new[] { bir.Root });
            NullableGenericErasure.Apply(bir.Root, isValueFqn);
            // GENERIC-BOUNDARY nullable-Tv USE realignment — THE USE AXIS of #86's erasure invariant (#4;
            // #113/#117/#120/#142). The DEF-side erasure above turns a member's `T?`/`…Ref<T?>…` into
            // `object`/`…Ref<object>…`, but a CALL site kotc emitted with T already substituted carries the concrete
            // `Nullable(kotlin.Int)`/`…Ref<Nullable(kotlin.Int)>…` (no bare `Tv` for the sweep to catch), which lowers
            // to a slot the member neither returns nor accepts — an ilverify StackUnexpected at a nested position, a
            // member that does not exist at all when the call's `sig` descriptor is what drifted. So every USE is
            // re-derived as `Subst(Erase(declaration), typeArgs)` and never `Erase(Subst(...))`: call returns, call
            // and constructor ARGUMENTS (descriptor included), field reads, and the WRITE positions — setLocal,
            // setField (statement/expression/static forms), arraySet, return (statement/expression forms), and the
            // `if/else` value join. Object-erasure rewrites stay gated to the exact bare-object boundary; concrete
            // V <-> Nullable<V> fixed slots use explicit construction/extraction with complete type identity.
            // BEFORE BirTypeLowering.
            NullableTvErasureCallRealign.ApplySourceUses(bir.Root, nullableTvDeclRets, isValueFqn, refs);
            // DELEGATE-TARGET slot alignment (ALL builds — the declaration half neither axis above can reach on its
            // own): a delegate's parameters and return are reified ARGUMENTS, so `(Int?) -> String` is
            // `Func<object, string>` and `(T?) -> String` is `Func<object, string>` at every instantiation; the
            // LIFTED method bound into that delegate declares ordinary slots, where a direct `Int?` is a
            // `Nullable<int32>` and a `String?` is a `string`. ECMA-335 II.14.6 admits neither pair, so the target's
            // slot follows the delegate's `object` — every parameter (contravariant: only `object` is assignable
            // from `object`) and a value/`Nullable`/type-variable return (covariant: a reference already reaches
            // `object`, and rewriting it is what broke #189). Runs AFTER the use axis, which is what corrects a
            // construction's own `funcType` to the slot it fills; the declared transition below then types the newly-`object`
            // lambda slots' BODIES — narrowing each read and boxing each `return` — and happens only when a slot
            // actually moved.
            if (DelegateTargetSlotAlignment.Apply(bir.Root, isValueFqn))
                NullableTvErasureCallRealign.ApplyAfterDelegateSlotAlignment(
                    bir.Root, nullableTvDeclRets, isValueFqn, refs);
            // UNCHECKED OBJECT->Tv RETURN ERASURE: the non-null-T sibling of nullable-generic return erasure.  A JVM
            // `Any? as T` physically returns Object; spelling the CLR return as reified T would insert `unbox.any T`
            // inside the callee and throw even when a null result is stored but never consumed.  Emit object physically,
            // preserve T in round-trip metadata, and keep a directly initialized Tv local object-typed until its actual
            // typed use.  All matching is structural and this pass fully states the CIR types ilemit emits 1:1.
            UncheckedGenericCastReturnErasure.Apply(bir.Root, uncheckedGenericCastRets);
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
            // Kotlin IR fake overrides are BIR facts, not CLR declarations. Consume the frontend's explicit selected
            // implementation fact instead of rediscovering a DIM from hierarchy bodies or reference metadata.
            // This is representation-independent and therefore applies to reference builds too.
            InheritedDefaultFakeOverrideElision.Apply(bir.Root);
            var hoisted = _options.RefBuild ? bir.Root : AliasHelperHoist.Apply(bir.Root, refs);
            // CLR override allocation: ordinary functions carrying @ClrIntrinsic receive the external slot name;
            // Kotlin property accessors keep their dedicated name and receive an explicit interface/base MethodImpl
            // binding instead. Derived from the frontend's `overrides` closure plus reference metadata. Runs before
            // MemberCallSubstitution so CLR-bound calls can still be shaped from the exact external identity. Never in
            // ref builds, whose declarations remain a pure Kotlin surface.
            if (!_options.RefBuild) DeclarationRename.Apply(hoisted, refs);
            // STAR-PROJECTION COLLECTION CLASSIFIERS: use faithful non-generic BCL faces where one exists; otherwise
            // author the Collection/Set/MutableSet composite classifier plus the following smart-cast member access.
            // App build only, before MemberCallSubstitution while the Kotlin owner is still visible.
            if (attributeTopLevelOwner) StarProjectionLowering.Apply(hoisted, refs);
            // .NET EVENT `subscribe` BINDING: kotc surfaces a .NET event as a `kotlin.clr.ClrEvent<T>` property and emits
            // `w.Changed.subscribe(h)` as the PLAIN call `callInstance(kotlin.clr.ClrEvent.subscribe,
            // recv = <clrEventGet w Changed>, [h])`. This pass BINDS that to the .NET add/remove accessor — the existing
            // clrEventAdd/clrEventRemove node (ilemit unchanged), reading owner .NET type + event name straight off the
            // clrEventGet member-access. The ClrEvent<T> value is never materialized (a .NET event isn't first-class);
            // the clrEventGet receiver is consumed here, not emitted. Runs BEFORE MemberCallSubstitution so the synthetic
            // call — which has no ref.dll owner — is bound here. `subscribe` also constructs the stdlib close token with
            // a synthesized remove callback. A no-op for the ref/rt stdlib self-build (no .NET events).
            hoisted = ClrEventSubscriptionBinding.Apply(
                hoisted, refs, clrEventForwardedOwners, localTypeFqns, out var materializedEventSubscriptions);
            // Event binding has just synthesized receiver/handler locals plus add/remove member accesses. Feed those
            // NEW fixed slots through the same nullable-value use-axis rule as the original tree. In particular, a
            // proven-present Nullable<V> event receiver must enter the synthesized bare-V spill as V; the first pass
            // could not see a node that did not exist yet. Submit the exact materialized work items rather than
            // repairing the whole file after construction.
            NullableTvErasureCallRealign.ApplyMaterialized(
                materializedEventSubscriptions, nullableTvDeclRets, isValueFqn, refs);
            // `ClrEvent.subscribe` synthesizes the remove callback as a normal `newClosure` ingredient bag. The main
            // ClosureSynthesis pass ran earlier, before event binding. Give the exact materialized roots to its late
            // entry contract so only these newly-created callback classes are assembled.
            var materializedEventClosureTypes = ClosureSynthesis.ApplyMaterialized(
                hoisted, materializedEventSubscriptions, refs);
            // ClosureSynthesis stamps the transient lifted-frame correspondence on a GENERIC closure class, and the pass
            // that consumes it (SharedSyntheticSynthesis) already ran. Drop it here so the invariant "it never reaches
            // CIR" holds for a class assembled by this late pass too, rather than only for the main one.
            foreach (var materializedType in materializedEventClosureTypes)
                SharedSyntheticSynthesis.DropSyntheticTypeArgs(materializedType);
            // .NET EVENT IMPLEMENT/RAISE (§4.2/§4.3): a Kotlin class implementing/declaring a CLR event via `by clrEvent()`.
            // kotc synthesized add_/remove_/raise_<E> + a `clrEvents` backing directive (pure-Kotlin identities); this pass —
            // the ref.dll-reading layer — resolves the concrete delegate `D` (the interface event's EventHandlerType) and
            // rewrites the accessor bodies to CIR `clrEventAccessorImpl` directives + inserts the `<E>$delegate : D` field +
            // a type-level `clrEventDecl`. It also binds `clrEventRaise` to a `raise_<E>` call. App/rt only (no .NET events
            // in the ref/rt stdlib self-build).
            if (!_options.RefBuild) hoisted = ClrEventImplBinding.BindImplementations(hoisted, refs);
            // KCLASS MEMBER BINDING: kotc emits `T::class.simpleName`/`.qualifiedName` as the PLAIN Kotlin property read
            // `callInstance(kotlin.reflect.KClass.get_simpleName/get_qualifiedName, recv = <a System.Type value>)`. This
            // pass owns the Kotlin<->CLR NAME reversal (#138): where the receiver's Kotlin type is statically known — an
            // UNBOUND `Int::class`/`Foo::class`, or a BOUND `1::class`/`"x"::class` on a known-final builtin — it CONST-
            // FOLDS the accessor to the Kotlin name string ("Int"/"kotlin.Int") off the still-Kotlin FQN token (runs
            // BEFORE BirTypeLowering), not the .NET reflection name. A genuinely-dynamic `x::class` (open/interface
            // static type) keeps the faithful `System.Type.Name`/`.FullName` read (the CLR->Kotlin run-time helper is a
            // sequenced stdlib follow-up, §5g). The System.Type/BCL knowledge lives HERE, never in the kotc frontend
            // (layer purity, mirrors the exception-map / annotation-base migrations). Non-ref only: ref keeps KClass pure.
            if (!_options.RefBuild) hoisted = KClassMemberBinding.Apply(hoisted);
            // Consume CharSequence property semantics while calls still carry the explicit Kotlin property name and
            // accessor role. MemberCallSubstitution is the physical binding boundary for those calls; no later pass may
            // recover `length` from the allocated MethodRef spelling.
            CharSeqStringLowering.CharSeqRetLambdas charSeqRetLambdas = null;
            if (!_options.RefBuild && attributeTopLevelOwner && !hasUserCharSeqImpl)
                hoisted = CharSeqStringLowering.Apply(hoisted, localTopLevelFns, out charSeqRetLambdas);
            // #395: bind an externally selected FIR declaration before MemberCallSubstitution can consult the erased
            // receiver/signature overload set. Local identities remain untouched for the module-wide allocator below.
            DeclarationIdentityBinding.BindReferenced(hoisted, refs, localDeclarationIds, deferUnknown: true);
            var substituted = _options.RefBuild ? hoisted : MemberCallSubstitution.Apply(hoisted, refs,
                localTopLevelFns, attributeTopLevelOwner, isValueFqn, localPropertyDeclarations);
            // Reified-nullability witnesses were prepared while declaration identities and Kotlin type arguments were
            // still authoritative. Materialize them only after semantic calls (enum/array/collection intrinsics) have
            // either been replaced or deliberately retained, so a physical hidden ABI argument cannot interfere with
            // recognition of the Kotlin-visible argument vector.
            ReifiedNullabilityWitnessLowering.MaterializeCallWitnesses(substituted);
            // Cross-module half of UncheckedGenericCastReturnErasure.  MemberCallSubstitution has now attributed a
            // referenced top-level call to its real file-class owner; bind the trusted physical-Object/logical-T
            // metadata boundary to an explicit CIR return conversion before `sty` is consumed by type lowering.
            if (!_options.RefBuild) UncheckedGenericCastReturnErasure.ApplyReferenced(substituted, refs);
            // Cross-module half of the nullable-Tv use realignment (#86 D1). The pass above already re-derived every use
            // whose callee it could name; a REFERENCED top-level fun reaches it as `callStatic owner=null` — kotc has no
            // file class for it — and only MemberCallSubstitution has now attributed it to the owner the reference index
            // is keyed by. Same formula, same code, run once more so those calls' returns and arguments are derived from
            // the producing assembly's declaration too. Idempotent on everything the earlier run already corrected.
            if (!_options.RefBuild)
                NullableTvErasureCallRealign.ApplyAfterReferencedOwnerBinding(
                    substituted, nullableTvDeclRets, isValueFqn, refs);
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
            // synthesized in the rt assembly exactly once (dedup), implementing the RT's canonical `dotkt$CharSequence`,
            // so an app that then routes a String op to a real stdlib body works. Skipped only for the ref build (its
            // bodies are squashed to `throw` anyway). Purely additive: only positively-String values are wrapped.
            // CharSeqStringLowering already selected the pure-app System.String representation while calls still
            // carried explicit property identity. Materialize any remaining synthetic/StringBuilder adapters after
            // call substitution; a user CharSequence implementation keeps the synthetic representation verbatim.
            IReadOnlyList<JsonNode> materializedDelegateAdapters = Array.Empty<JsonNode>();
            if (!_options.RefBuild)
                substituted = StringCharSequenceBridge.Apply(
                    substituted, refs, charSeqRetLambdas, out materializedDelegateAdapters);
            // The String/CharSequence call-boundary bridge may materialize a closure that captures a non-literal
            // `(P...) -> String` delegate and exposes `(P...) -> CharSequence` (#190). Assemble those late closure
            // ingredients now; all earlier source/inline/event closures were already consumed by the main passes.
            var materializedDelegateAdapterTypes = ClosureSynthesis.ApplyMaterialized(
                substituted, materializedDelegateAdapters, refs);
            foreach (var materializedType in materializedDelegateAdapterTypes)
                SharedSyntheticSynthesis.DropSyntheticTypeArgs(materializedType);
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
            if (!_options.RefBuild) TryValueOperandHoist.Apply(substituted, isValueFqn);
            staged.Add((substituted, outputName));
        }

        // Event raises and subscriptions can target a Kotlin-declared event in a sibling source file. Every
        // implementation now has its concrete delegate-derived declaration, so bind all call sites to those exact
        // module-wide facts before type lowering.
        if (!_options.RefBuild)
        {
            ClrEventImplBinding.BindRaisesAll(staged.Select(s => s.Root), refs);
            ClrEventImplBinding.BindLocalSubscriptionsAll(staged.Select(s => s.Root));
        }

        // Type-level #29 source truth must be captured at the last all-Kotlin boundary. Every source/inline type now
        // exists, while inner applications, star projections, reference nullability, and captured type-parameter
        // indexes still retain their Kotlin meaning. Those facts have no separate supertype-edge metadata channel.
        if (attributeTopLevelOwner)
            foreach (var stagedFile in staged) CollectionIdentityRecord.RecordTypeEdges(stagedFile.Root);

        // All source and inline-spliced Kotlin type applications now exist. Project Kotlin inner argument order to
        // CLR flattened nested order before the first CLR-oriented generic/slot pass consumes those applications.
        TypeOwnershipLowering.ProjectInnerApplications(staged.Select(s => s.Root).ToList(), refs);

        // F-BOUND STAR PROJECTION: CLR has no legal/reified `Node<*>` TypeSpec for `Node<N : Node<N>>`.
        // Materialize a deterministic non-generic existential view in bir2cir and make every closed Node<N> implement
        // it. Runs before interface-slot normalization and suspend lowering, in ref and runtime builds alike.
        var localExistentialOwners =
            FBoundStarProjectionErasure.ApplyAll(staged.Select(s => s.Root).ToList(), refs);
        var existentialReceiverMembers =
            ExistentialReceiverBinding.Collect(staged.Select(s => s.Root));

        // KOTLIN FAKE-OVERRIDE -> CLR INTERFACE SLOT: if a concrete class implements an interface using a public
        // NON-VIRTUAL method inherited from its base class, Kotlin considers the member implemented but CLR implicit
        // interface binding does not. Materialize the exact forwarding member in CIR/BIR-space before suspend lowering
        // (so a synthesized suspend member is transformed normally). Exact signature/return only; ambiguity is skipped.
        InheritedClassInterfaceBridge.ApplyAll(staged.Select(s => s.Root).ToList());

        // KOTLIN COVARIANT OVERRIDE -> EXACT CLR METHODIMPL: preserve the Kotlin declaration's narrow return and add a
        // private forwarding bridge with the interface slot's exact return. The bridge carries a resolved
        // `clrInterfaceImpls` instruction; ilemit only consumes that instruction and does not infer covariance.
        var covariantBridgedSlots = CovariantInterfaceReturnBridge.ApplyAll(
            staged.Select(s => s.Root).ToList(), refs, isValueFqn);

        // KOTLIN-ONLY COLLECTION SLOTS -> EXACT CLR METHODIMPL: the BCL operational faces carry neither Kotlin's
        // remove-capable `MutableIterable.iterator()` return nor `MutableCollection.removeAll`/`retainAll`/
        // `addAll(elements)` / `MutableList.addAll(index, elements)`. A Kotlin class overriding one of them would
        // therefore be unreachable through that face. Give each participating class the matching compiler-owned slot
        // interface plus a private forwarding bridge carrying a resolved `clrInterfaceImpls` instruction; the
        // `ClrCollectionDefaults` dispatchers test for those interfaces and otherwise run the BCL default. HERE, in
        // the Kotlin-vocabulary phase, because the pass keys on the frontend `overrides` identity — which does not
        // survive to CIR — and on Kotlin's own supertype graph. Non-ref builds only.
        if (!_options.RefBuild)
            KotlinCollectionSlotSynthesis.ApplyAll(staged.Select(s => s.Root).ToList());

        // NOMINAL COLLECTION CLASSIFIER IDENTITIES: the operational aliases intentionally share BCL faces
        // (Collection/Set -> IReadOnlyCollection, MutableCollection/MutableSet -> ICollection), which otherwise makes
        // the Kotlin classifiers indistinguishable for emitted user implementations. Add the compiler-owned identity
        // edge while the Kotlin supertype graph is still present. BCL-backed values are handled by their real generic
        // collection faces in StarProjectionLowering's runtime classifier.
        if (!_options.RefBuild)
            KotlinCollectionIdentitySynthesis.ApplyAll(staged.Select(s => s.Root).ToList());

        // CONSTRUCTED MEMBER RESULT SUBSTITUTION (early): suspend lowering copies a call's result type into
        // state-machine fields/locals. Close every already-constructed receiver-relative return BEFORE that copy
        // happens (`Deferred<Int>.await(): type-TV0` -> `Int`), otherwise a non-generic SM permanently captures an
        // out-of-scope TV and ilemit can only realize it as object. Later exact declaring-owner transitions discharge
        // this same obligation locally when they change a call's owner; no module-wide repair sweep follows.
        ConstructedMemberReturnSubstitution.ApplyAll(staged.Select(s => s.Root).ToList());

        // PHASE 1.5 — SUSPEND COLD LOWERING (R1 classifier): rewrite EVERY declared `suspend fun` (top-level statics,
        // extensions, instance members, static/companion members, abstract/interface members) into the cold
        // Continuation shape (SM class + `f$dotkt_suspend` cold entry + Task bridge + suspend-main drain), and rewrite
        // member/cross-file/cross-assembly suspend CALLS to the callee's cold shape. Runs GLOBALLY across all files (a
        // same-assembly cross-file suspend call keeps `owner:null`, so the registry spans every input file). After call
        // substitution (its synthesized calls are already-final sibling/BCL shapes) and before type lowering (its
        // kotlin.* type tokens flow through BirTypeLowering). Declaration is UNCONDITIONAL: a non-segmentable v1 shape
        // gets a call-time-throw cold entry, not a drop — no `suspend:true` survives to ilemit in app builds.
        //
        // Runs in app, runtime-stdlib, AND reference builds. Reference emission replaces bodies with throw stubs, but
        // must retain every declaration signature and trusted identity carrier, including the independently allocated
        // `id|cold` entry. Otherwise a consuming module can preserve the frontend-selected suspend overload only until
        // cold lowering, then has no authoritative physical binding and falls back to the erased overload set. The
        // runtime and reference builds therefore execute the same declaration transform and physical allocation.
        var suspendCalleeRet = SuspendColdLowering.ApplyAll(staged.Select(s => s.Root).ToList(), refs,
            localTypeFqns, attributeTopLevelOwner, localExistentialOwners, isValueFqn);

        // PHASE 1.6 — SUSPEND LAMBDA LOWERING (bundle-6 P3 wave-2b, LIVE): replace each `newSuspendLambda`
        // node with `new <mangled>_lambdaN$sm(captures..., null)` + synthesize its SuspendLambda state machine
        // (SuspendColdLowering.BuildLambdaSm, the shared FunGen machinery). Runs after the cold lowering (so a
        // suspend-lambda relocated into a synthesized SM invokeSuspend body is still caught — this pass walks
        // the newly-added SM types too) and before type lowering. kotc emits `newSuspendLambda` for every
        // `suspend` lambda literal (exercised by cases/il-lam1, il-lam2); same (non-ref) gate as the cold lowering.
        if (!_options.RefBuild)
            SuspendLambdaLowering.ApplyAll(staged.Select(s => s.Root).ToList(), localTypeFqns, suspendCalleeRet, refs);

        // PHASE 1.7a — RESIDUAL SUSPEND DECLARATIONS. The two phases above leave the `suspend` modifier on exactly the
        // declarations the stdlib self-build retains without a state machine (the Kotlin surface kept beside its cold
        // entry, and the inline coroutine primitives whose call sites are reconstructed inline). Those have no
        // state-machine body, so bir2cir states their physical one — an explicit call-time throw — instead of letting a
        // later layer invent a body for a modifier it cannot interpret. In an app build every suspend declaration is
        // lowered, so a survivor is refused here. Runs after BOTH suspend phases and before type lowering. The
        // REFERENCE build is exempt for the same reason it needs no `newSuspendLambda` phase: RefBodySquash replaces
        // EVERY body there with the metadata-only throw, which is already the physical body of a declaration that
        // cannot be executed — and its kotlin.* type tokens are kept verbatim, so an authored `NotSupportedException
        // (kotlin.String)` would have no CLR ctor to bind.
        if (!_options.RefBuild)
            SuspendResidueLowering.ApplyAll(
                staged.Select(s => s.Root).ToList(), _options.StdlibMode == BuildStdlibMode.App);

        // Every source declaration was frozen before representation erasure, and every synthesized suspend declaration
        // must have copied that fact from its semantic source. Refuse an incomplete current BIR here; do not reconstruct
        // Kotlin meaning from the now-physical `suspendRet`.
        RoundtripMetadata.RequireSuspendResults(staged.Select(s => s.Root));

        // KOTLIN ERASURE-NARROWED OVERRIDE -> FINAL CLR METHODIMPL (#344 / #86 D3). The declaration-move half ran
        // early, but the bridge half must see the FINAL declarations: one logical suspend override becomes a public
        // Task member AND a continuation cold entry, and each is a distinct CLR slot. SuspendColdLowering carries the
        // source `overrides` proof onto both generated declarations (renaming it for the cold member), so this single
        // rule emits one exact bridge/MethodImpl per physical obligation without teaching ilemit suspend semantics.
        // Ref builds skip suspend lowering and normalize their logical declaration here; app and rt builds normalize
        // the final Task/cold shapes. Star views already exist, and all types are still in the Kotlin vocabulary.
        KotlinOverrideSlotBridge.ApplyAll(
            staged.Select(s => s.Root).ToList(), isValueFqn, refs, localTypeFqns,
            covariantBridgedSlots);

        // The final override bridge deliberately runs after the main F-bound/star rewrite because suspend lowering
        // can create additional physical slots. Project any Kotlin star types copied into those late declarations
        // through the already-allocated existential ABI; no new carrier or semantic/member lookup occurs here.
        FBoundStarProjectionErasure.RewriteLateTypes(
            staged.Select(s => s.Root).ToList(), localExistentialOwners, refs);

        // Non-generic `System.IComparable` bridge. Synthesize it at this final Kotlin-vocabulary boundary so its
        // forwarding call carries the target declaration's semantic return stamp. In particular, a legal
        // `compareTo(...): Nothing` is normalized at that bridge's construction boundary, before the physical Int32
        // slot could receive Nothing's later CLR object erasure.
        if (!_options.RefBuild)
            foreach (var stagedFile in staged) ComparableBridgeSynthesis.Apply(stagedFile.Root);

        // @PublishedApi is a Kotlin semantic annotation carried alongside the source `internal` visibility in BIR.
        // Resolve its effective CLR accessibility here, before ownership selects top-level versus Nested* metadata.
        PublishedApiTypeVisibilityLowering.ApplyAll(staged.Select(s => s.Root));

        // Materialize every remaining Kotlin lexical owner as explicit CLR TypeDef nesting only after all source,
        // inline, event-adapter, closure, and coroutine synthesis has finished adding declarations. This is the sole
        // BIR semanticOwner -> CIR nestedIn boundary.
        TypeOwnershipLowering.ApplyAll(staged.Select(s => s.Root).ToList());
        // Opaque raw-BIR carriers cross the assembly boundary after the representation decision above. Bind any local
        // nested type tokens to this producer's exact metadata identity now; readers never reconstruct that relation.
        OpaqueCarrierTypeBinding.ApplyAll(staged.Select(s => s.Root).ToList(), refs);
        // A synthesized closure/SAM class holds each capture in an INSTANCE FIELD, which the CLR refuses for a
        // byref-like (`ref struct`) type. ClosureSynthesis recorded those refusals rather than throwing, because the
        // cold suspend lowering above reconstructs a `suspendCoroutine { … }` block inline and PRUNES the class it
        // came from — its captures become ordinary locals of the enclosing frame, judged by the suspend storage
        // gate instead. Report now, over the classes that actually survived.
        ClosureSynthesis.AssertSurvivingCapturesLegal(staged.Select(s => s.Root));

        // PHASE 1.7 — GENERIC SELF INSTANTIATION (bundle-6 P5 BUG A part-2): a lifted GENERIC anon-object emits
        // its self instance accesses with the BARE type name (`dotkt_obj144`, no type args) -> ".NET method/type
        // not fully instantiated" at runtime. Derive the constructed self `dotkt_obj144[gp:T]` for those
        // executable instance accesses (kotc emits the FQN identity; bir2cir derives the CLR instantiation).
        if (!_options.RefBuild)
            GenericSelfInstantiation.ApplyAll(staged.Select(s => s.Root).ToList());

        // PHASE 1.9 — GENERIC DELEGATE INSTANTIATION (#191): a `var x by D<T>(…)` delegated property whose user
        // delegate is GENERIC gets its getValue/setValue dispatch emitted with the BARE delegate owner (`"D"`, no
        // type args) while the `$delegate` field/local carries the constructed `D<String>` — the open owner
        // mismatches the constructed receiver (BadImageFormatException / ilverify `found 'string' expected '!0'`).
        // Recover the receiver's instantiation (StaticType.Surface) and stamp the constructed owner. The
        // delegated-property analog of GenericSelfInstantiation; kotc names the type, bir2cir derives the CLR
        // instantiation. BEFORE BirTypeLowering (so `D<kotlin.String>` lowers consistently with the receiver).
        if (!_options.RefBuild)
            GenericDelegateInstantiation.ApplyAll(staged.Select(s => s.Root).ToList(), refs);

        // GENERIC STATIC OWNER BINDING: bind the semantic G owner to the explicit non-generic carrier selected above
        // (or validated from a referenced DotKt assembly). No representative type argument is chosen.
        GenericStaticOwnerBinding.ApplyAll(staged.Select(s => s.Root).ToList(), refs);

        // Carry the exact IsVolatile representation of local and referenced fields into each CIR access. In
        // particular, an access through a constructed local generic owner cannot rely on FieldInfo identity at emit
        // time: anchoring the declaration builder produces a different object. CIR therefore states the prefix itself.
        VolatileFieldLowering.ApplyAll(staged.Select(s => s.Root).ToList(), refs);

        // Kotlin const fields become CLR literal fields. In the reference build the physical scalar/string slot also
        // carries its exact Kotlin declaration type, because a locally-declared kotlin.String/Int TypeDef is not a
        // legal Constant-table type. ilemit still receives only final physical facts in either mode.
        ConstFieldLowering.ApplyAll(staged.Select(s => s.Root).ToList(), refs, _options.RefBuild);

        // CONSTRAINED TYPE-PARAMETER RECEIVER, phase 1 of 2: kotc names a type-parameter receiver's classifier with
        // a BARE token (`fun <N : Node<N>> N.close()` -> `Node`). Close it from N's own lexical bound, which source
        // writes constructed, so the inherited-owner walk immediately below has a constructed type to substitute the
        // exact DECLARING owner into. The node stays a callInstance; only the token changes.
        if (!_options.RefBuild)
            ConstrainedTypeParameterReceiverBinding.CloseOpenOwners(staged.Select(s => s.Root).ToList());

        // INHERITED GENERIC MEMBER OWNER BINDING: BIR keeps the Kotlin receiver owner (`Derived<T>.m`), while a CLR
        // MemberRef must name the exact CONSTRUCTED declaring owner (`Base<T>.m`). Resolve that hierarchy substitution
        // here, from local declarations + kotc's override facts, before type lowering. This removes a semantic inference
        // from ilemit and prevents open-generic member operands ("containing type is not fully instantiated"). Exact
        // signature/arity matches only; ambiguous overloads are never guessed. No library/member names are special.
        if (!_options.RefBuild)
            InheritedMemberOwnerBinding.ApplyAll(staged.Select(s => s.Root).ToList(), refs);

        // CONSTRAINED TYPE-PARAMETER RECEIVER, phase 2 of 2: a member called on a receiver whose static type is a
        // type PARAMETER (`fun <T : Tagged> f(t: T) = t.tag()`) cannot be a plain `callvirt` — the stack holds a
        // `!!T`, not an interface reference, so ECMA-335 requires an address plus `constrained. !!T ; callvirt`.
        // Author that dispatch now that the walk above has named the exact constructed DECLARING owner: naming the
        // receiver's bound instead would emit a MemberRef on a type that merely inherits the member, which binds
        // locally only through a fake override and has nothing to bind to across an assembly boundary.
        if (!_options.RefBuild)
            ConstrainedTypeParameterReceiverBinding.ApplyAll(
                staged.Select(s => s.Root).ToList(), isValueFqn, refs);

        // dll2klib restores G<*> for Kotlin source analysis while the referenced DLL physically exposes
        // its compiler-generated existential carrier. Re-apply that exact referenced ABI to call signatures and directly initialized locals before
        // CLR type lowering; ilemit must never infer the hidden physical signature.
        if (!_options.RefBuild)
            foreach (var stagedFile in staged)
                ReferenceExistentialAbiBinding.Apply(stagedFile.Root, refs);

        // A spliced inline payload may retain raw identity `T === null` over a generic local. ECMA ceq cannot consume
        // a generic-parameter stack value and null directly; author the boxed object-null comparison in CIR.
        if (!_options.RefBuild)
            foreach (var stagedFile in staged) GenericParameterNullComparison.Apply(stagedFile.Root);

        // Every structural synthesizer has now run. Allocate the complete declaration/property set before any pass
        // consumes physical Property descriptors: BackingFieldRename distinguishes receiverless storage ownership by
        // the exact getSig/setSig written here, including on late String/CharSequence adapters. This allocator is the
        // sole forward Kotlin-property -> CLR-name projection; no consumer reconstructs identity from that name.
        KotlinPropertyAccessors.AllocateDeclarationsAndProperties(staged.Select(s => s.Root));

        // AUTO-PROPERTY BACKING-FIELD RENAME (#228): kotc names an accessor-routed property's storage with the KOTLIN
        // identity, so the emitted type carried a property AND a field of the same name (reflection consumers cannot
        // resolve the pair). Mint the CLR metadata name `<Name>k__BackingField` here — the layer that owns the
        // Kotlin-to-CLR representation — and rewrite every field read/write that addresses it. GLOBAL (a
        // `byref(obj.prop)` addresses a sibling file's backing field) and unconditional (ref/rt/app emit one shape);
        // last in the structural phase, so every synthesized body exists and owner tokens are still Kotlin FQNs.
        BackingFieldRename.ApplyAll(staged.Select(s => s.Root).ToList());

        // Kotlin lexical visibility remains unchanged even when its declaration and use land in different CLR
        // TypeDefs. Project each otherwise-illegal physical edge to a private caller-side [UnsafeAccessor] extern;
        // target members are never widened to internal/protectedInternal.
        if (_options.RefBuild)
            UnsafeAccessorLowering.DropFacts(staged.Select(s => s.Root).ToList());
        else
            UnsafeAccessorLowering.ApplyAll(staged.Select(s => s.Root).ToList());

        // Resolve the Kotlin `lateinitGet` failure path to an ordinary UPAE construction before constructor binding.
        // This is deliberately after UnsafeAccessorLowering, which can rebuild a private-field lateinitGet around a
        // byref load, so every surviving node carries one complete physical exception expression into CIR.
        LateinitGetLowering.ApplyAll(staged.Select(s => s.Root));

        // Normalize every local call/delegate descriptor before the module-wide local member binding below. Generic
        // delegate targets may carry a closed function shape while their declaration remains open; bir2cir owns the
        // declaration lookup and records that open signature explicitly for one-to-one emission.
        foreach (var stagedFile in staged)
            ClrMemberResolution.EnsurePlainCallDescriptors(stagedFile.Root);
        ClrMemberResolution.ResolveLocalDelegateTargets(staged.Select(s => s.Root), refs);

        // Suspend/state-machine and bridge synthesis above can add classes after the initial source-declaration
        // inventory was collected. The final CLR-member stamping pass must see the complete emission-unit set so a
        // local `new` is never mistaken for a referenced type and reflected against the compile universe.
        var emittedLocalTypes = AnySlotRebind.CollectLocalTypes(staged.Select(s => s.Root))
            .Keys.ToHashSet(StringComparer.Ordinal);
        // Some generated declarations are representation templates for canonical TypeDefs emitted once by the
        // runtime stdlib.  The shared catalog is the single ownership registry used by bir2cir and ilemit; the exact
        // shipped type must also exist before an app can treat its template as external.
        var externalCanonicalTypes = (_options.StdlibMode == BuildStdlibMode.App
            ? staged.SelectMany(s => s.Root is JsonObject root && root["types"] is JsonArray types
                ? types.OfType<JsonObject>()
                : Enumerable.Empty<JsonObject>())
            .Where(type => (type["generated"] as JsonValue)?.TryGetValue<bool>(out var generated) == true && generated)
            .Select(type => (type["name"] as JsonValue)?.GetValue<string>())
            .Where(name => !string.IsNullOrEmpty(name)
                && ManagedReferenceCatalog.IsCanonicalRuntimeSyntheticType(name)
                && refs.PhysicalTypeNamed(name) != null)
            : Enumerable.Empty<string>())
            .ToHashSet(StringComparer.Ordinal);

        // STAR-PROJECTION BOUND index (#2): the in-assembly generic type-param BOUNDS (`interface Key<E : Element>`
        // -> {Key: [Element]}), collected across ALL staged roots (a `Key<*>` use may live in a sibling file from Key's
        // declaration). Feeds StarProjectionBoundLowering so a `Key<object>` (kotc's star-projection erasure) is
        // repointed to `Key<Element>` for the stdlib's OWN Key; a REFERENCED Key resolves via refs.TvBound instead.
        var starProjBounds = StarProjectionBoundLowering.CollectTypeParamBounds(staged.Select(s => s.Root));
        // The validator's local declaration index and referenced generic-declaration cache are module invariants.
        // Prepare them once before per-file physical finishing; each root is checked only after its last BIR-space
        // type-argument rewrite below.
        var externalGenericConstraintValidation = ExternalGenericConstraintValidation.Prepare(
            staged.Select(file => file.Root).ToList(), refs, isValueFqn, localBasicEnums);
        // The early semantic index only prevents a local accessor call from being captured by an external ancestor.
        // Capture the actual post-allocation MethodDef names now, once, for exact call allocation below.
        var localPropertyAccessors = MemberCallSubstitution.CollectLocalPropertyAccessors(
            staged.Select(s => s.Root));
        // PHASE 2 — per-file type lowering onwards.
        var files = new List<CirFile>();
        // The fully-lowered roots, kept so the implementing-position half of the crossing refusal can be asked of the
        // WHOLE compilation at once: a Kotlin interface declared in one file may be the only path from a class in
        // another to the .NET declaration whose slot it cannot fill.
        var loweredRoots = new List<(JsonNode Root, string File)>();
        // Reference assemblies retain Kotlin type vocabulary, but their MethodDef names must match the runtime twin.
        // Derive the collision set from an isolated runtime-physical projection in every build mode.
        var declarationCollisionProjection = new List<JsonNode>();
        var finalLocalDeclarationIds = DeclarationIdentityBinding.CollectDeclarationIds(staged.Select(s => s.Root));
        var declarationCollisionAliases = DeclarationIdentityBinding.CollisionAliases(
            staged.Select(s => s.Root), refs.Aliases);
        foreach (var (substituted, outputName) in staged)
        {
            // §11 CONTINUATION-ERASURE (bundle-6 bug #5 ROOT): make the coroutine ABI monomorphic on
            // kotlin.coroutines.Continuation<object>. Every Continuation[X] type token -> Continuation[kotlin.Any]
            // (all positions), and the resumeWith(Result<X>) protocol boundary -> Result<object> (Option A: the
            // resumeWith method + its Result-construction call args). ALL builds (ref/rt agree), BEFORE type lowering
            // (kotlin.Any then lowers to object in rt/app, verbatim in ref). Un-blocks BlockOnSink/startCoroutine/
            // resumeWith dispatch (CLR interface variance does not lift value types; uniform erasure is the fix).
            ContinuationErasure.Apply(substituted, refs);
            GenericDowncastRealignment.Apply(substituted, genericDowncastHierarchy);
            // GenericDowncastRealignment aligns a local's declared type with an erased
            // existential-carrier cast. Bind calls through that local to the exact synthesized
            // existential slot before CLR type lowering.
            ExistentialReceiverBinding.Apply(substituted, existentialReceiverMembers, refs);
            // SEQUENCE for-in dispatch (#37 m1 wave-2, cases/il-seqforin): a `for (x in seq)` over a Kotlin Sequence
            // lowers to `forEachInline` with a typed `IEnumerable<elem>::GetEnumerator` dispatch, but the anon Sequence
            // `sequence { .. }` returns is erased to `IEnumerable<object>` at runtime (its lifted class carries no type
            // param yet declares `IEnumerable<T>` over the enclosing method's T) -> the typed slot is absent
            // (EntryPointNotFound). Re-point such a forEachInline onto the variance-immune non-generic
            // `System.Collections.IEnumerable`/`IEnumerator` + an element cast. Non-ref; before type lowering (the src's
            // `kotlin.sequences.Sequence` FQN is still in the source vocabulary).
            if (!_options.RefBuild) SequenceForEachLowering.Apply(substituted);
            // Resolve an attributed callStatic to the referenced declaration while its Kotlin descriptor still
            // distinguishes shapes that share one CLR erasure (`T?` vs object, function-return `T?` vs object).
            // The selected declaration then follows the ordinary nullable/alias/type transform into physical CIR.
            // App-only: stdlib metadata/runtime builds own their kotlin.* facades in this assembly.
            ClrMemberResolution.EnsurePlainCallDescriptors(substituted);
            // Signature-shaping passes above operate on Kotlin-visible parameters and may rebuild `sig` from the
            // semantic declaration carrier. Re-append the already-materialized hidden nullable witnesses now, at the
            // final physical-binding boundary, so exact reference resolution sees the actual MethodDef signature.
            ReifiedNullabilityWitnessLowering.FinalizeCallSignatures(substituted);
            // Fail closed if a late materialization introduced an identity-bearing external call after the early
            // binding boundary; the same authoritative ID lookup applies and no erased overload search is permitted.
            DeclarationIdentityBinding.BindReferenced(substituted, refs, finalLocalDeclarationIds,
                preserveForScalarResolution: attributeTopLevelOwner);
            if (attributeTopLevelOwner)
                ClrMemberResolution.ResolveReferencedStaticCalls(
                    substituted, refs, emittedLocalTypes, externalCanonicalTypes,
                    finalLocalDeclarationIds, outputName);
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
            // #66 — RUNTIME stdlib build only: remove the `kotlin.Comparable` upper bound that substituted CLR
            // primitives cannot satisfy, plus `in` declaration-site variance that the CLR rejects for the realized
            // signature. kotc emits pure-Kotlin type params in every build (ref==rt BIR); this pass removes only the
            // constraints/variance incompatible with the runtime stdlib's substituted CLR shape. Runs
            // BEFORE BirTypeLowering (the constraint is still the pure `kotlin.Comparable` token here).
            if (_options.SubstituteStdlibBuild) StdlibSubstituteTypeParams.Apply(substituted);
            // STAR-PROJECTION BOUND LOWERING (#2): a `T<*>` on a self-ref-bounded generic (`Key<E : Element>`) that kotc
            // erased to `Key<object>` violates `E : Element` (illegal reified CLR instantiation). Repoint the objectish
            // arg to the type-param BOUND (`Key<Element>`), reading the constraint from the in-assembly declaration (its
            // self-build) or refs.TvBound (a referenced owner). ALL builds, BEFORE BirTypeLowering (still kotlin.Any /
            // dotted Kotlin FQNs here), so ref.dll + rt.dll + app agree on the corrected signature.
            StarProjectionBoundLowering.Apply(substituted, starProjBounds, refs);
            // A reference KLIB can express ordinary nominal generic bounds, but not ECMA's class/struct/new() flags;
            // projecting the implicit ValueType/Enum rows as Kotlin bounds is worse, because no Kotlin value inhabits
            // those CLR root classifiers. Validate that physical half against the authoritative reference metadata
            // after every BIR-space splice/retyping/star-bound rewrite has settled the constructed arguments and before
            // type lowering erases their Kotlin/value identities. Foreign declaration descriptors are deliberately not
            // use-site TypeSpecs and retain their own generic frames. The frontend remains the owner of nominal bounds.
            externalGenericConstraintValidation.Apply(substituted);
            // #29 ROUND-TRIP RECORD: before the type transform collapses a nested read-only `kotlin.collections.List/
            // Set/Collection` (Root V) to its invariant sibling `IList`/`ICollection` — colliding with the mutable
            // sibling's own alias and losing the Kotlin read-only-vs-mutable identity — stash the PRE-collapse Kotlin
            // type of each affected declaration position as an opaque string. Member slots use
            // [KotlinCollectionIdentity]; type edges and bounds merge into [KotlinSupertypes]. dll2klib therefore
            // restores `List` vs `MutableList` cross-module in both channels. APP builds only (the collapse is
            // non-ref; only an app-emitted library is dll2klib-re-consumed). Runs on kotlin.* names before lowering.
            if (attributeTopLevelOwner) CollectionIdentityRecord.RecordMemberSlots(substituted);
            // The type transform: lower the Kotlin type vocabulary into ilemit's CLR-codegen vocabulary, emitting a
            // BIR-SHAPED CIR (same node shape; only type strings change). No verbatim/envelope track. The ref.dll
            // @ClrTypeAlias index lowers EVERY CLR-bound type (collections/StringBuilder/Regex/... not just the
            // hardcoded primitives) wherever it appears as a type token. The struct-ness oracle drives the reference
            // `{t:nullable}` strip (a value `T?` stays `Nullable<T>`; a reference `T?` -> bare + the NRT byte above).
            // #305 §2.7 CHOKEPOINT — every pass that can retype a node has now run, and BirTypeLowering below STRIPS
            // `sty`, so this is the last point at which the stamp exists to be checked. A stale stamp surviving here
            // is a pass that changed a node's result type without carrying `sty` with it.
            CheckStySanity(outputName, substituted);
            KotlinPropertyAccessors.AllocateAll(substituted, refs, localPropertyAccessors);
            var collisionProjection = substituted.DeepClone();
            declarationCollisionProjection.Add(BirTypeLowering.Lower(
                collisionProjection, refBuild: false, declarationCollisionAliases, isValueFqn, outputName,
                refs.PhysicalTypeNames, emittedLocalTypes));
            var lowered = BirTypeLowering.Lower(substituted, _options.RefBuild, refs.Aliases, isValueFqn, outputName,
                refs.PhysicalTypeNames, emittedLocalTypes);
            // The erasure can collapse two Kotlin declarations onto ONE CLR signature, where only one of them can
            // ever be called and the other is unreachable. Checked HERE, on the lowered tree, because that is where
            // the physical signature is final: `T?` reaches `object` through this pass and `Any?` reaches it through
            // the reference-nullable strip, so nothing earlier sees the two meet. Refuses loudly, naming both source
            // signatures — a silent wrong binding is the one outcome a program with no valid lowering must not get.
            NullableGenericOverloadCollision.Check(lowered, outputName);
            loweredRoots.Add((lowered, outputName));
        }

        // #395: Kotlin declarations that were distinct before type lowering may now occupy one CLI signature.
        // Allocate their MethodDef names from the common runtime-physical projection and rewrite all local uses from
        // the frontend-selected declaration identity before any module-wide member binding consults declarations.
        var declarationPhysicalNames = DeclarationIdentityBinding.AllocatePhysicalNames(
            declarationCollisionProjection, out var declarationSemanticCarrierIds);
        DeclarationIdentityBinding.ApplyLocal(
            loweredRoots.Select(s => s.Root), declarationPhysicalNames,
            declarationSemanticCarrierIds, declarationSemanticSignatures, refs);
        KotlinPropertyAccessors.FinalizePhysicalProperties(loweredRoots.Select(s => s.Root));

        // SAME-UNIT CONSTRUCTOR BINDING: resolve `new`, `this(...)`, and local `super(...)` to declaration indices
        // while every source file is visible together. This deliberately follows physical type lowering and its
        // collision refusal: an invalid pair of Kotlin constructors that erases to one CLR signature must receive the
        // existing actionable collision diagnostic, rather than being intercepted as an ambiguous local lookup.
        // ilemit receives only the winning declaration index and never selects by name/arity/assignability.
        ClrMemberResolution.ResolveLocalConstructors(loweredRoots.Select(s => s.Root));
        // A call may retain a local receiver owner while its declaration lives on an inherited referenced
        // interface.  Resolve that local/external boundary only after every physically-lowered local declaration
        // is visible, so a stale shipped copy of a local owner can never win.
        ClrMemberResolution.ResolveInheritedExternalCalls(loweredRoots.Select(s => s.Root), refs);
        // Some lowering stages synthesize or physically reshape calls after the early semantic-signature pass.
        // Re-run the same exact external binding on the final physical descriptors; the explicit local-type set
        // keeps every declaration emitted by this compilation on the local axis.
        foreach (var (lowered, outputName) in loweredRoots)
            ClrMemberResolution.ResolveReferencedStaticCalls(
                lowered, refs, emittedLocalTypes, externalCanonicalTypes,
                finalLocalDeclarationIds, outputName);

        // PHASE 3A — complete every representation synthesis module-wide before any pass snapshots the final type
        // graph. A derived type can precede its base in another file; resolving interface manifests inside this loop
        // would otherwise make their contents depend on source-file order.
        foreach (var (lowered, _) in loweredRoots)
        {
            // `.size` (Count) on a STAR-PROJECTED / `Any`-erased collection receiver: StarProjectionLowering already
            // re-pointed the receiver `cast` at a non-generic BCL collection interface, but MemberCallSubstitution bound
            // Count to the GENERIC `IReadOnly*<object>.Count`, absent on a value-type-arg collection (`List<int>`)
            // -> EntryPointNotFound. Re-point such Count reads at the VARIANCE-IMMUNE non-generic
            // `System.Collections.ICollection.Count`. App build only; runs AFTER MemberCallSubstitution so Count is bound.
            if (attributeTopLevelOwner) StarProjectionCountLowering.Apply(lowered);
            // BCL-only collection slots (non-ref builds): a CONCRETE Kotlin class implementing @ClrTypeAlias'd
            // `MutableCollection`/`MutableList` (ICollection<E>/IList<E>) is missing the BCL members Kotlin's collection
            // interfaces lack — `Contains`/`CopyTo`/`get_IsReadOnly` (ICollection) and `IndexOf` (IList) — so the concrete
            // type (kotlin.collections.ArrayDeque, the AbstractMutable* bases, a MutableMap keys/values view) fails to LOAD
            // ("... does not have an implementation"), surfacing at the referencing app as "cannot resolve .NET type". Fill
            // each missing slot with an ordinary public forwarding member. Return-DROPPING slots
            // (Add/set_Item/RemoveAt) are allocated by the common KotlinOverrideSlotBridge pass below, which carries
            // their exact MethodImpl descriptors to ilemit.
            if (!_options.RefBuild) CollectionBclSlotSynthesis.Apply(lowered);
            // The READ-ONLY sibling of every mutable collection face this unit's types name. Kotlin's `MutableList<E>`
            // IS-A `List<E>`, but their lowered CLR faces (`IList<T>` / `IReadOnlyList<T>`) are unrelated interfaces,
            // so the read-only view is real only when the emitted type declares it. Runs AFTER the mutable faces are
            // final (CollectionBclSlotSynthesis adds the ICollection<E> face of a list implementer) and BEFORE the
            // interface-slot manifests below, so the sibling is an ordinary stated interface everywhere downstream.
            // All builds: the rule is keyed on the lowered BCL face, which the reference build (whose surface stays
            // Kotlin-faced) simply never has.
            ReadOnlyCollectionViewInterfaces.Apply(lowered);
        }

        // THE REVERSE ENUMERATOR BRIDGE (#139/#400): a class whose supertype graph reaches a BCL enumerable face owes
        // `IEnumerator<E> GetEnumerator()` and has only Kotlin's `iterator(): Iterator<E>`. Author the compiler-owned
        // adapter TypeDef and both GetEnumerator halves — declarations, bodies and exact MethodImpl descriptors — so
        // ilemit emits them one-to-one. Module-wide (the face can be inherited through a base declared in a sibling
        // file) and after the loop above, whose last passes can still state such a face. Non-ref: the reference
        // surface keeps the Kotlin collection faces, so nothing in it implements a BCL enumerable interface.
        if (!_options.RefBuild
            && ReverseEnumeratorBridgeSynthesis.ApplyAll(loweredRoots.Select(s => s.Root).ToList(), refs))
            // The adapter is declared by THIS emission unit, later than the initial inventory, so record it before
            // any member binding below can mistake a local construction for a reference to another assembly.
            emittedLocalTypes.Add(ReverseEnumeratorBridgeSynthesis.AdapterName);

        // Late synthesis above authors ordinary calls as part of new bridge bodies. Bind those calls only after every
        // root has reached its final representation, then resolve local-owner calls inherited from external bases.
        ClrMemberResolution.ResolveInheritedExternalCalls(loweredRoots.Select(s => s.Root), refs);
        foreach (var (lowered, outputName) in loweredRoots)
            ClrMemberResolution.ResolveReferencedStaticCalls(
                lowered, refs, emittedLocalTypes, externalCanonicalTypes,
                finalLocalDeclarationIds, outputName);

        // Declaration inheritance and MethodImpl ownership are physical CLR edges. Put their final TypeSpecs on the
        // same exact reflected identity so ilemit cannot visit one slot twice through an arity-free alias and an exact
        // owner. This runs after every pass that can add an interface or descriptor (including read-only collection
        // views and reverse-enumerator synthesis), but before ResolveInterfaceSlots snapshots those final edges into
        // ilemit's exact slot manifests. The reference build keeps Kotlin vocabulary and deliberately skips this
        // CIR-only normalization.
        if (!_options.RefBuild)
            foreach (var (lowered, _) in loweredRoots)
                ExactExternalDeclarationIdentity.Apply(lowered, refs);

        // PHASE 3B — metadata, exact external identities, and validation over the now-stable module graph.
        foreach (var (lowered, outputName) in loweredRoots)
        {
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
            // DotKt.Stdlib.dll is the shipping runtime assembly and is never metadata-read, so only runtime-relevant
            // annotations survive this boundary.
            else RoundtripMetadata.StripRuntimeAttrs(lowered);
            // The Kotlin `suspend` modifier has no CLR meaning and every consumer of it is a bir2cir pass: the cold
            // lowering, and the [KotlinFunction(Suspend)] stamp just written (which is how a referenced module still
            // sees a suspend surface). CIR describes the physical graph, so the flag stops here — ilemit has no
            // coroutine semantics to apply to it, and IrSanity refuses one that survives.
            SuspendResidueLowering.DropModifier(lowered);
            // #48/#146: mint the ilemit-facing `attrExternal` bool from an applied-attribute's `{t:fqn}` type node —
            // consume kotc's `attrClr` origin flag for an imported .NET attr (all builds), and mark the cross-module
            // @KotlinDefault external in APP/user-library builds (it only REFERENCES the stdlib-defined type; the ref/rt
            // self-build defines it locally in `_types` and stays a bare-FQN local stamp).
            AttrExternalNormalize.Apply(lowered, _options.StdlibMode == BuildStdlibMode.App);
            // W1-S2 (#46): resolve every clrStatic/clrInstance/newClr against the ref.dll MLC. Internal
            // `resolvedMemberParams` carries matching inputs between bir2cir passes; Apply consumes it into the scalar
            // memberRef (+ `dispatch` on clrInstance). ilemit is a pure linker. Runs LAST — on
            // the fully-lowered tree — so owner/argTypes speak the CLR vocabulary the MLC resolves; unconditional so
            // RefBodySquash's `newClr NotImplementedException` is stamped too (its owner resolves off the BCL compile-refs).
            ClrMemberResolution.EnsurePlainCallDescriptors(lowered);
            ClrMemberResolution.Apply(lowered, refs, emittedLocalTypes);
            // Exact member resolution above selected the authoritative MethodDef and temporarily carried its CLR-only
            // generic parameter facts. Validate the call's actual method arguments in the caller's lexical frame, then
            // consume that internal carrier before CIR serialization. Generic Kotlin properties are physical accessor
            // MethodDefs here and therefore follow the same rule as functions and extension methods.
            externalGenericConstraintValidation.ApplyResolvedMembers(lowered);
            // Property nodes reveal whether they denote an accessor or a public field only after CLR member
            // resolution. Convert the accessor subset on type-variable receivers to constrained calls now, retaining
            // the exact memberRef just resolved; fields keep their distinct load/store representation.
            ConstrainedTypeParameterReceiverBinding.ApplyResolvedProperties(new[] { lowered });
            // An applied attribute is a call into the assembly that declares it, so its constructor is resolved
            // like any other external member. After Apply, whose statics it shares.
            ClrMemberResolution.ResolveAttributeCtors(lowered, refs);
            // DllImportAttribute is a pseudo-custom attribute. Consume the Kotlin external fact plus the now-resolved
            // exact attribute application into one physical CIR MethodDef/ImplMap descriptor before ilemit sees it.
            PInvokeLowering.Apply(lowered, refs);
            ClrMemberResolution.ResolveWellKnown(lowered, refs);
            ClrMemberResolution.ResolveInterfaceSlots(lowered, loweredRoots.Select(file => file.Root), refs);
            // Every delegate slot in this file now names its selected member, so each literal lambda filling one
            // can be pointed at the delegate it physically constructs — and the void-to-value adapter that needs
            // can be authored as ordinary CIR. Runs after the last resolution pass, because the rule compares the
            // construction's FINAL function type with the slot's.
            ClrMemberResolution.MaterializeDelegateSlots(lowered, refs, emittedLocalTypes);
            // THE STAMPING CHOKEPOINT: every node resolved against a .NET member carries that member's declared
            // return. Two omissions of exactly that shape — a generic method and a public field — each removed a
            // whole family from the crossing refusal below without any gate noticing.
            ClrMemberResolution.CheckStamped(lowered, outputName);
            // The other side of the erasure: a .NET member may DECLARE a `List<int?>`, which no Kotlin type inhabits
            // once `X?` in a reified argument is `System.Object`. Unrelated invariant reified generics have no
            // conversion between them and an adapter would change the argument's identity, so the crossing is
            // refused rather than silently mis-typed. Checked HERE, immediately after resolution has established the
            // member's declared physical signature: before it, most `clr*` nodes still carry caller-side `argTypes` and the .NET declaration
            // this refusal is about has not been read yet.
            ForeignNullableGenericCrossing.Check(lowered, outputName);
        }

        // The IMPLEMENTING-POSITION half of the same refusal: a .NET supertype declaring a slot no Kotlin body can
        // fill. It is asked of every lowered root together, because the supertype graph that reaches such a slot runs
        // through this compilation's own declarations as freely as through referenced ones, and a per-file view stops
        // at the first Kotlin interface declared next door. Nothing has been written yet, so a refusal here is as
        // clean as one inside the loop.
        ForeignNullableGenericCrossing.CheckImplementedSlots(loweredRoots, refs);

        // BirTypeLowering's mutable/read-only collection faces are sibling CLR interfaces. Materialize every cast
        // required by the final value-flow graph only now, after every synthetic declaration and exact memberRef is
        // stable. ilemit then emits those ordinary CIR casts without recognizing the collection ABI. A metadata/ref
        // build keeps kotlin.collections.* verbatim and never creates these physical sibling faces.
        if (!_options.RefBuild)
            CollectionViewCoercion.ApplyAll(loweredRoots.Select(file => file.Root).ToList());

        // Every representation synthesis is now complete. Validate the exact MethodDef table that CIR will describe;
        // do not defer a generated/user collision to ilemit and do not invent a late name after calls are bound.
        DeclarationIdentityBinding.ValidateFinalPhysicalNames(loweredRoots.Select(file => file.Root));

        // Canonical runtime synthetics were useful as module-wide representation templates, but an application does
        // not emit their TypeDefs. Remove those declarations from CIR so its local/external ownership is literal:
        // every surviving reference to the type names the runtime assembly and no duplicate local declaration exists.
        foreach (var (root, _) in loweredRoots)
            if (root is JsonObject document && document["types"] is JsonArray types)
                for (var i = types.Count - 1; i >= 0; i--)
                    if (types[i] is JsonObject type
                        && (type["name"] as JsonValue)?.TryGetValue<string>(out var name) == true
                        && externalCanonicalTypes.Contains(name))
                        types.RemoveAt(i);

        // SERIALIZATION IS THE LAST THING, and not the tail of the loop above. The check that just ran asks each
        // Kotlin body which slot it fills, and the answer is a pass-to-pass record on the declaration that must not
        // reach CIR — so the check consumes it, and it can only do that once every file has been lowered and read.
        // Writing a file's JSON inside the loop froze that record into the CIR of every file but the last.
        // Preserve the assembly-wide fixed-member contract independently of source-root elision below. A valid
        // alias-only compilation can have every lowered source root removed by IsEmptyCir; its synthetic CIR root is
        // then the only surviving carrier, and ilemit must still receive the complete fail-closed role table.
        var fixedMemberTable = loweredRoots.Select(file => (file.Root as JsonObject)?["wellKnownRefs"])
            .FirstOrDefault(table => table != null)?.DeepClone();
        foreach (var (lowered, outputName) in loweredRoots)
        {
            // A file whose ENTIRE content was @ClrTypeAlias types (e.g. Primitives.kt, Comparable.kt) is now empty after
            // AliasHelperHoist dropped them — emit no CIR file for it (an empty file-class would be a pointless empty
            // static type in the assembly). Skips only when types AND methods AND fields are all empty; never in ref.
            if (!_options.RefBuild && IsEmptyCir(lowered)) continue;
            files.Add(new CirFile(outputName, lowered.ToJsonString(JsonOptions.Indented)));
        }

        // #220: both stdlib twins define the canonical wide-delegate family. This is a physical CLR declaration,
        // authored directly as CIR after all source-semantic passes have completed; it is not a Kotlin source type
        // and must not be fed back through those passes. The fixed synthetic file also makes the ownership visible in
        // captured CIR instead of letting ilemit manufacture an undeclared ABI from a build-mode switch.
        if (_options.StdlibMode != BuildStdlibMode.App)
        {
            if (files.Any(f => f.OutputName == CanonicalDelegateSynthesis.OutputName))
                throw new InvalidOperationException(
                    $"bir2cir: reserved synthetic CIR name '{CanonicalDelegateSynthesis.OutputName}' collides with an input file");
            var delegateDefinitions = CanonicalDelegateSynthesis.SynthDefsFile();
            if (fixedMemberTable != null) delegateDefinitions["wellKnownRefs"] = fixedMemberTable.DeepClone();
            files.Insert(0, new CirFile(
                CanonicalDelegateSynthesis.OutputName,
                delegateDefinitions.ToJsonString(JsonOptions.Indented)));
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
            var attributeDefinitions = RoundtripMetadata.SynthDefsFile(refs);
            if (fixedMemberTable != null) attributeDefinitions["wellKnownRefs"] = fixedMemberTable.DeepClone();
            files.Insert(0, new CirFile(synthName, attributeDefinitions.ToJsonString(JsonOptions.Indented)));
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
        static bool Empty(JsonNode n) => n is not JsonArray a || a.Count == 0;
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
                    // interfaces are structured `{t:fqn,name:…}` nodes. Read via OwnerName so a user
                    // `class S : CharSequence` is detected. Reading it as JsonValue left hasUserCharSeqImpl false, so
                    // CharSeqStringLowering ran on an assembly
                    // with a real polymorphic implementer, lowering its `subSequence(): CharSequence` override return to
                    // System.String (+ toString coercion) while it overrides a `dotkt$CharSequence` slot -> TypeLoad
                    // "signature of the body and declaration do not match" (il-charseq/charseqx).
                    if (TypeJson.OwnerName(i) == "dotkt$CharSequence")
                        return true;
        return false;
    }

    // #305: the spec §2.7 `sty` chokepoint, run on the FULLY-PASSED BIR of one file. `sty` is bir2cir-internal and
    // BirTypeLowering strips it, so the CIR gate below can never see one — the stamp has to be checked here, while it
    // still exists, or the invariant has no mechanical witness at all. Same bir-common checker, restricted to the one
    // check that is meaningful pre-lowering (IrSanityChecks.StyStampsOnly); the JsonDocument round-trip is what gives
    // the shared JsonElement-based checker a view of the JsonNode tree the passes work on.
    static void CheckStySanity(string outputName, JsonNode root)
    {
        // BOTH ends of the round trip carry the #147 depth bound: `BirJson.Writer` to write and `BirJson.DocOptions`
        // to read back. System.Text.Json defaults MaxDepth to 64 on each independently, and one Kotlin function with
        // deeply-nested inlined lambdas nests a method body past that — so a plain `ToJsonString()` here would crash
        // the sanity CHECKPOINT on exactly the input a checkpoint exists to survive.
        using var doc = JsonDocument.Parse(root.ToJsonString(BirJson.Writer), BirJson.DocOptions);
        try { IrSanity.Check(new[] { doc.RootElement }, IrSanityChecks.StyStampsOnly); }
        catch (IrSanityException ex) { throw new InvalidOperationException($"{outputName}: {ex.Decl}: sanity: {ex.Message}"); }
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

sealed record DriverOptions(string OutDir, IReadOnlyList<string> CompileReferences, IReadOnlyList<string> Inputs,
    BuildStdlibMode StdlibMode, bool ReflectionRestricted, string StdlibBindings)
{
    // The pure-Kotlin REFERENCE stdlib surface (`--build-stdlib=metadata` -> DotKt.Private.Stdlib.dll) keeps kotlin.*
    // type tokens verbatim and squashes bodies to a throw; EVERY other invocation — the runtime stdlib build and all
    // app builds — lowers kotlin.* to the CLR vocabulary.
    public bool RefBuild => StdlibMode == BuildStdlibMode.Metadata;

    // The RUNTIME stdlib build (`--build-stdlib=runtime` -> DotKt.Stdlib.dll) — NOT an app build. Since #66 kotc emits
    // one substitute-independent BIR, the rt-only type-param drops (kotlin.Comparable bound / `in` variance) that kotc
    // used to do live here (StdlibSubstituteTypeParams). App builds keep those, substituting the Comparable bound to
    // System.IComparable — so this is the stdlib-runtime build ONLY. Runtime attribute stripping and every other
    // Kotlin-to-CLR representation decision are likewise completed in bir2cir before ilemit sees the CIR.
    public bool SubstituteStdlibBuild => StdlibMode == BuildStdlibMode.Runtime;

    public static DriverOptions Parse(string[] args)
    {
        if (args.Length < 2)
            throw new UsageException("bir2cir: missing output directory or input files");

        var outDir = args[0];
        var refs = new List<string>();
        var inputs = new List<string>();
        var mode = BuildStdlibMode.App;
        var reflectionRestricted = false;
        string stdlibBindings = null;

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
                case "--stdlib-bindings" when i + 1 < args.Length:
                    stdlibBindings = args[++i];
                    break;
                case "--stdlib-bindings":
                    throw new UsageException("bir2cir: --stdlib-bindings requires a path");
                case "--reflection-restricted":
                    reflectionRestricted = true;
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
        if (stdlibBindings != null && mode == BuildStdlibMode.App)
            throw new UsageException("bir2cir: --stdlib-bindings is valid only with --build-stdlib");
        if (stdlibBindings == null && mode != BuildStdlibMode.App)
            throw new UsageException("bir2cir: --build-stdlib requires --stdlib-bindings <path>");

        return new DriverOptions(outDir, refs, inputs, mode, reflectionRestricted, stdlibBindings);
    }

    public static void SelfTest()
    {
        try
        {
            Parse(new[] { "out", "--build-stdlib=metadata", "input.bir.json" });
            throw new InvalidOperationException("DriverOptions self-test accepted a stdlib build without bindings");
        }
        catch (UsageException ex) when (ex.Message.Contains("requires --stdlib-bindings", StringComparison.Ordinal))
        {
        }
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
