// bir2cir — lower Backend IR (BIR) JSON into CLR IR (CIR) JSON.
//
// bir2cir owns the Kotlin -> CLR type substitution. Its SINGLE, sole transform rewrites the Kotlin type
// vocabulary in the BIR into the CLR-codegen vocabulary ilemit consumes, emitting a BIR-SHAPED CIR (same node
// shape; only type strings change). There is no verbatim-copy / envelope alternative — that dual track is retired.
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;

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
        // Fail-loud: a ref.dll scan swallows load/type failures into Diagnostics (so ONE malformed type never aborts the
        // whole scan). Surface them here — a silent ref-scan miss otherwise surfaces as a distant EntryPointNotFound/NRE
        // with no "ref scan failed" signal. An empty Diagnostics stays silent (the happy path prints nothing).
        var diagnostics = refs.Diagnostics.ToList();
        foreach (var d in diagnostics) Console.Error.WriteLine($"bir2cir: WARNING ref-scan diagnostic: {d}");
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

        // The callee generic-param ORDER index (funName|arity -> ordered type-param names), aggregated across ALL input
        // BIR files: a same-assembly cross-file `callStatic owner=null` may target a fun defined in another input, and
        // MapVarianceRealign needs the callee's declared type-param order to map a sig's `gp:NAME` to its typeArg index.
        var calleeTypeParams = MapVarianceRealign.CollectCalleeTypeParams(birFiles.Select(f => f.Root));

        // PHASE 1: per-file transforms up through the CharSequence bridge. Collect the staged roots so the
        // suspend cold lowering can run GLOBALLY (a same-assembly cross-file suspend call keeps `owner:null`,
        // so its cold-entry callee may live in another file — the transformability fixpoint spans all files).
        var staged = new List<(JsonNode Root, string OutputName)>();
        foreach (var bir in birFiles)
        {
            var outputName = OutputNameFor(bir.Path);
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
            if (!_options.RefBuild) EnumMemberBinding.Apply(bir.Root);
            // NULLABLE-GENERIC-RETURN erasure (ALL builds, so ref.dll + rt.dll signatures agree): a Kotlin method
            // declaring a nullable generic-parameter return (`fun <T> …(): T?`) has its nullability erased by kotc to
            // a bare `gp:T` return (Nullable<T> is inexpressible for an unconstrained T). That is CORRECT for a
            // reference T (`ldnull` is a real null) but for a VALUE T `ldnull; ret !!T` collapses to default(T)=0 —
            // null-ness is LOST (firstOrNull on a value-type list returns 0, not the element / not null-for-empty).
            // The CLR-faithful representation of a generic `T?` is `System.Object` (the boxed/erased nullable form).
            // Rewrite the return to `object`; ilemit boxes value returns and the CALL boundary converts object ->
            // the caller's Nullable<V> / reference type. Runs BEFORE the rest so type-lowering/substitution see it.
            NullableGenericReturnErasure.Apply(bir.Root);
            // FUNC-SLOT nullable-return erasure (ALL builds — the transform-side twin of the pass above): a function
            // TYPE with a nullable return (`(T) -> R?`) is kotc-tokenized `func:nullable:<ret>:<args>` (the open
            // generic view `nullable:gp:R`; a value instantiation `nullable:int`). Its only CLR rep that agrees
            // across open/value/reference instantiations is Func<…, object> (reference instantiations stay bare-typed
            // and ride Func's `out TResult` covariance into the object slot). Rewrites every such token (param slots,
            // sig strings, delegateNew/delegateInvoke funcTypes), erases the backing lambda methods' returns to
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
            if (!_options.RefBuild) MapVarianceRealign.Apply(bir.Root, calleeTypeParams);
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
            var substituted = _options.RefBuild ? hoisted : MemberCallSubstitution.Apply(hoisted, refs, localTopLevelFns, attributeTopLevelOwner);
            // Gap A — the for-loop iterator protocol over a referenced collection: re-point the desugared `<iterator>`
            // var + its synthetic hasNext/next owner at the REAL referenced kotlin.collections.Iterator<E> (app build
            // only; the stdlib self-build emits Iterator itself, so it is left synthetic there).
            if (attributeTopLevelOwner) IteratorConsumerNormalization.Apply(substituted);
            // PRODUCER-side twin: a user `class C : Iterator<T>` / `object : Iterator<T>` implements kotc's
            // monomorphized synthetic `<>dotkt_KIterator_<elem>`, but bir2cir lowers the matching TYPE tokens to the
            // referenced generic `kotlin.collections.Iterator`1<elem>` — two CLR identities for one Kotlin iterator
            // (ilverify StackUnexpected). Collapse the synthetic onto the generic (implementers, tokens, dispatch)
            // and drop the synthetic defs. App build only; before type lowering. (bundle-6 iter/iterable.)
            if (attributeTopLevelOwner) SyntheticIteratorUnification.Apply(substituted);
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
            suspendCalleeRet = SuspendColdLowering.ApplyAll(staged.Select(s => s.Root).ToList(), refs, localTypeFqns);

        // PHASE 1.6 — SUSPEND LAMBDA LOWERING (bundle-6 P3 wave-2b, LIVE): replace each `suspendLambdaNew`
        // node with `new <mangled>_lambdaN$sm(captures..., null)` + synthesize its SuspendLambda state machine
        // (SuspendColdLowering.BuildLambdaSm, the shared FunGen machinery). Runs after the cold lowering (so a
        // suspend-lambda relocated into a synthesized SM invokeSuspend body is still caught — this pass walks
        // the newly-added SM types too) and before type lowering. kotc emits `suspendLambdaNew` for every
        // `suspend` lambda literal (exercised by cases/il-lam1, il-lam2); same (non-ref) gate as the cold lowering.
        if (!_options.RefBuild)
            SuspendLambdaLowering.ApplyAll(staged.Select(s => s.Root).ToList(), localTypeFqns, suspendCalleeRet, refs);

        // PHASE 1.7 — CROSS-CLASS PRIVATE WIDENING (bundle-6 P5 BUG A): a LIFTED anon-object / closure class
        // (`<>dotkt_obj*`) is a SEPARATE top-level CLR class that reads its enclosing class's PRIVATE members
        // via its captured `__outer` — legal on the JVM (nested class), a System.MethodAccessException on the
        // CLR. Widen exactly the private members reached CROSS-CLASS to `internal` (assembly-visible). Runs
        // GLOBALLY, in non-ref builds, AFTER the suspend passes (so synthesized SM types are covered too) and
        // BEFORE type lowering (owner tokens are still the kotlin.* FQN that match local type names).
        if (!_options.RefBuild)
            CrossClassPrivateWidening.ApplyAll(staged.Select(s => s.Root).ToList());

        // PHASE 1.8 — GENERIC SELF INSTANTIATION (bundle-6 P5 BUG A part-2): a lifted GENERIC anon-object emits
        // its self instance accesses with the BARE type name (`<>dotkt_obj144`, no type args) -> ".NET method/type
        // not fully instantiated" at runtime. Derive the constructed self `<>dotkt_obj144[gp:T]` for those
        // executable instance accesses (kotc emits the FQN identity; bir2cir derives the CLR instantiation).
        if (!_options.RefBuild)
            GenericSelfInstantiation.ApplyAll(staged.Select(s => s.Root).ToList());

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
            // The type transform: lower the Kotlin type vocabulary into ilemit's CLR-codegen vocabulary, emitting a
            // BIR-SHAPED CIR (same node shape; only type strings change). No verbatim/envelope track. The ref.dll
            // @ClrTypeAlias index lowers EVERY CLR-bound type (collections/StringBuilder/Regex/... not just the
            // hardcoded primitives) wherever it appears as a type token.
            var lowered = BirTypeLowering.Lower(substituted, _options.RefBuild, refs.Aliases);
            // `.size` on a collection-OF-collections (groupBy's `Map<K, List<T>>`, whose runtime value is the MUTABLE
            // `IList<T>` while its STATIC value is `IReadOnlyList<T>`): Count comes via the INVARIANT `ICollection<KVP<K,V>>`,
            // so the reified generic slot the app dispatches (`...<KVP<int,IReadOnlyList<int>>>`) is absent on the runtime
            // `Dictionary<int,IList<int>>` -> EntryPointNotFound. Re-point such Count reads at the VARIANCE-IMMUNE
            // non-generic `System.Collections.ICollection.Count` (every BCL-backed map/list implements it). App build only;
            // runs AFTER BirTypeLowering so the collection tokens are already the `clrg:System.Collections.*` CLR forms.
            if (attributeTopLevelOwner) NestedCollectionCountLowering.Apply(lowered);
            // Non-generic `System.IComparable` bridge (non-ref builds): a Kotlin `class C : Comparable<C>` lowers to
            // `C : System.IComparable<C>` ONLY, but the CLR dispatch spine for natural ordering goes through the
            // NON-generic `System.IComparable` (compareValues' `as IComparable` + ilemit's constrained-compareTo
            // value-type-safe fallback — boxed primitives implement IComparable but not a reified IComparable<object>).
            // Every comparable BCL type (Int32/String/DateTime) implements BOTH; a user Kotlin type must too, or a
            // stdlib body sorting it hits EntryPointNotFound/InvalidCast on `IComparable.CompareTo(object)`. Add the
            // missing interface + a `CompareTo(object)` bridge that casts and forwards to the generic CompareTo.
            if (!_options.RefBuild) ComparableBridgeSynthesis.Apply(lowered);
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
                    // interfaces are STRUCTURED `{t:fqn,name:…}` nodes after the m1 TYPE FLIP (was a legacy string);
                    // read via OwnerName so a user `class S : CharSequence` is still detected. A stale `as JsonValue`
                    // read missed it -> hasUserCharSeqImpl wrongly false -> CharSeqStringLowering ran on an assembly
                    // with a real polymorphic implementer, lowering its `subSequence(): CharSequence` override return to
                    // System.String (+ toString coercion) while it overrides a `<>dotkt_CharSequence` slot -> TypeLoad
                    // "signature of the body and declaration do not match" (il-charseq/charseqx).
                    if (TypeJson.OwnerName(i)?.TrimStart('@') == "<>dotkt_CharSequence")
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
    // The build scripts export these env vars.
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
    const string RestrictsSuspensionAttr = "kotlin.coroutines.RestrictsSuspension";
    // [KotlinFunction(flags)] flag word (mirrors ilemit Program.cs pass 4 / facadegen): Infix=1, Operator=2, Suspend=4.
    const int KotlinFunctionSuspendFlag = 4;

    readonly List<ReferenceAssembly> _assemblies;

    // Aggregate CALL-SUBSTITUTION index across all reference assemblies.
    readonly Dictionary<string, string> _ownerAlias = new(StringComparer.Ordinal);   // Kotlin FQN -> BCL alias
    readonly Dictionary<string, string> _ownerKind = new(StringComparer.Ordinal);    // Kotlin FQN -> class/struct/...
    readonly Dictionary<string, int> _ownerArity = new(StringComparer.Ordinal);      // Kotlin FQN -> generic arity
    readonly Dictionary<string, string[]> _ownerTypeParams = new(StringComparer.Ordinal); // Kotlin FQN -> generic param names
    readonly HashSet<string> _helperTypes = new(StringComparer.Ordinal);             // emitted "<>dotkt_ClrH_*"
    readonly HashSet<string> _restrictsSuspension = new(StringComparer.Ordinal);     // @RestrictsSuspension owners
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
            foreach (var s in asm.DotKt.RestrictsSuspensionTypes) _restrictsSuspension.Add(s);
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

    // Cross-assembly suspend-call resolution (bundle-6 P3 wave 2a): does the referenced owner declare a suspend
    // member of this name? The cold entry is the naming-convention linkage (`<name>$dotkt_suspend` on the same
    // owner type), keyed off the [KotlinFunction(Suspend)] flag scanned into MemberBinding.Suspend. Used by
    // SuspendColdLowering to rewrite a cross-assembly `x.g()` suspend call to `x.g$dotkt_suspend(…, completion)`.
    public bool HasSuspendMember(string owner, string name) =>
        owner != null && _membersByOwner.TryGetValue(owner, out var list)
        && list.Any(m => m.Suspend && string.Equals(m.Name, name, StringComparison.Ordinal));

    // Does this owner type carry @kotlin.coroutines.RestrictsSuspension (a restricted-suspension scope, e.g.
    // SequenceScope)? A suspend lambda with such a receiver gets the RestrictedSuspendLambda SM base (bundle-6 P5).
    public bool HasRestrictsSuspension(string ownerToken) =>
        ownerToken != null && _restrictsSuspension.Contains(BareOwnerFqn(ownerToken));

    public int Count => _assemblies.Count;
    public IReadOnlyList<ReferenceAssembly> Assemblies => _assemblies;

    // Every ref.dll scan diagnostic (a swallowed MetadataLoadContext load failure / partial-type-load / per-type skip).
    // Surfaced to stderr in the driver so a silent ref-scan miss (which becomes a DISTANT EntryPointNotFound/NRE at
    // ilemit or run time) is visible at the layer that produced it. See the driver's `Run` for the fail-loud print.
    public IEnumerable<string> Diagnostics => _assemblies.SelectMany(a => a.DotKt.Diagnostics);

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
        var pick = cands.FirstOrDefault(m => m.ParamCount == argCount);
        if (pick == null)
        {
            // Failure posture (LOUD): no exact-arity match. A single candidate is unambiguous (use it); MULTIPLE
            // candidates that DISAGREE on the bound property are a genuine routing ambiguity — refuse rather than
            // pick an arbitrary overload (which would bind the wrong .NET property).
            if (cands.Select(c => (c.PropertyAccess, c.PropertyName)).Distinct().Count() > 1)
                throw new InvalidOperationException(
                    $"ambiguous @ClrProperty overload for {ownerFqn}.{memberName} (argCount={argCount}): candidate arities " +
                    $"[{string.Join(",", cands.Select(c => c.ParamCount))}] bind different properties — no exact-arity match");
            pick = cands[0];
        }
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
        var pick = cands.FirstOrDefault(m => m.ParamCount == argCount);
        if (pick == null)
        {
            // Failure posture (LOUD): no exact-arity match. A single candidate is unambiguous; MULTIPLE candidates
            // binding DIFFERENT BCL members are a genuine ambiguity — refuse rather than pick an arbitrary overload.
            if (cands.Select(c => c.Intrinsic).Distinct(StringComparer.Ordinal).Count() > 1)
                throw new InvalidOperationException(
                    $"ambiguous @ClrIntrinsic overload for {ownerFqn}.{memberName} (argCount={argCount}): candidate arities " +
                    $"[{string.Join(",", cands.Select(c => c.ParamCount))}] bind different BCL members — no exact-arity match");
            pick = cands[0];
        }
        intrinsic = pick.Intrinsic;
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
        // `sfunc:` (suspend fn TYPE) erases to `object`: a suspend-lambda VALUE is a SuspendLambda state-machine
        // OBJECT (a Continuation-based object), NOT a Func delegate — so it keys as `obj`, matching an intrinsic's
        // object-erased suspend param/receiver. A plain `func:` still keys as the delegate bucket.
        if (t.StartsWith("sfunc:", StringComparison.Ordinal)) return "obj";
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

    // ParamKey over a STRUCTURED Type node (a birType-emitted param slot) — walks the TypeNode natively (never
    // re-renders a legacy token), matching the string ParamKey's top-level-identity canonicalization exactly:
    // byref/array/nullable unwrap-with-marker, a fn -> obj (suspend) / func, a type-var -> gp, an Fqn leaf folded via
    // the shared primitive switch (delegating to ParamKey(f.Name) — a bare FQN the switch already handles).
    public static string ParamKey(TypeNode t) => t switch
    {
        TypeNode.ByRef b => "byref:" + ParamKey(b.Of),
        TypeNode.Array a => "array:" + ParamKey(a.Elem),
        TypeNode.Nullable n => "nullable:" + ParamKey(n.Of),
        TypeNode.Fn fn => fn.Suspend ? "obj" : "func",
        TypeNode.Tv => "gp",
        TypeNode.Fqn f => ParamKey(f.Name),
        _ => "obj",
    };

    // ParamKey off a JSON type slot: a structured `{t:…}` node walks natively; a legacy string slot (sig-side token)
    // keeps the string path.
    public static string ParamKey(JsonNode typeSlot)
    {
        if (TypeJson.Read(typeSlot) is TypeNode tn) return ParamKey(tn);
        if (typeSlot is JsonValue v && v.TryGetValue<string>(out var s)) return ParamKey(s);
        return ParamKey("");
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

    // Whether the ref.dll ALSO has a NON-intrinsic (real-Kotlin-body) top-level fun of this name. Such a name is
    // unsafe for the NAME-ONLY intrinsic fallback even when every intrinsic overload agrees on one BCL static:
    // `sort` binds all 8 primitive-array overloads to "System.Array.Sort" (so it is NOT "ambiguous"), but
    // `MutableList<T>.sort()` / `Array<out T>.sort()` are real bodies — the name fallback rewrote the real-bodied
    // call inside the compiled `sorted()` to an open-generic `Array.Sort` ("not fully instantiated" at runtime).
    // With a real-bodied sibling present, only the sig-EXACT intrinsic match may substitute.
    public bool HasNonIntrinsicTopLevel(string funName) => _topLevelStatics.ContainsKey(funName);

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
        var pick = cands.FirstOrDefault(m => m.ParamCount == argCount);
        if (pick == null)
        {
            // Failure posture (LOUD): no exact-arity match. A single candidate is unambiguous; MULTIPLE candidates
            // with DIFFERENT byref positions are a genuine ambiguity — refuse rather than pick an arbitrary overload.
            if (cands.Select(c => string.Join(",", c.ByrefPositions)).Distinct(StringComparer.Ordinal).Count() > 1)
                throw new InvalidOperationException(
                    $"ambiguous @ClrRefArgument byref overload for {ownerFqn}.{memberName} (argCount={argCount}): candidate " +
                    $"arities [{string.Join(",", cands.Select(c => c.ParamCount))}] disagree on byref positions — no exact-arity match");
            pick = cands[0];
        }
        return pick.ByrefPositions;
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
        // The substitution index via MetadataLoadContext (a metadata-only reflection read) is the SOLE scan. A former
        // runtime `Assembly.LoadFrom` scan (populating Members/Types/Functions/FileClasses) was REMOVED: it always
        // threw TypeLoadException on the metadata-only ref stdlib (throw-stub bodies + kotlin.* signatures) — logging a
        // spurious "metadata scan failed: TypeLoadException Type: 'kotlin.String'" on every build — and aborted early,
        // and its output fed ONLY dead resolution paths (the unreferenced Resolve(CallSite)/Resolve(TypeSite)/
        // ResolveClrProperty). The live @ClrTypeAlias/@ClrIntrinsic/rule-3 substitution reads exclusively from here.
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
                    if (HasAttribute(type.GetCustomAttributesData(), RestrictsSuspensionAttr)) metadata.RestrictsSuspensionTypes.Add(ownerFqn);
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
                        // The `suspend` bit from the DotKt round-trip [KotlinFunction(flags)] attribute (Suspend = 4,
                        // the flag word ilemit stamps; the dead Assembly.LoadFrom scan read it, this live scan didn't).
                        // Channelled into MemberBinding.Suspend for the coroutine bundle (bundle 6) — no consumer yet.
                        var suspend = (KotlinFunctionFlags(method.GetCustomAttributesData()) & KotlinFunctionSuspendFlag) != 0;
                        if (suspend && Environment.GetEnvironmentVariable("DOTKT_BIR2CIR_DEBUG_SUSPEND") == "1")
                            Console.Error.WriteLine($"bir2cir: ref-scan suspend member {ownerFqn}.{method.Name}/{method.GetParameters().Length} (Suspend=true)");
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
                            byrefPositions,
                            suspend));
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
    // to "[]", generic params to "gp", a generic type to its open def's stripped FQN. A NESTED type's reflection name
    // ("kotlin.collections.Map`2+Map$Entry`2") is normalized to the BIR token convention the call side uses
    // ("kotlin.collections.Map$Entry" = namespace + innermost simple name) — e.g. the Map.Entry.component1/2 extensions.
    static string RecvKey(Type t)
    {
        if (t.IsByRef && t.GetElementType() is Type e) t = e;
        if (t.IsArray) return "[]";
        if (t.IsGenericParameter) return "gp";
        var def = t.IsGenericType ? t.GetGenericTypeDefinition() : t;
        var full = def.IsNested
            ? (string.IsNullOrEmpty(def.Namespace) ? "" : def.Namespace + ".") + def.Name
            : def.FullName ?? def.Name;
        return StripGenericArity(full);
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

    static string StripGenericArity(string value)
    {
        var idx = value.IndexOf('`');
        return idx >= 0 ? value[..idx] : value;
    }
}

sealed record ReferenceAssembly(string Path, string Name, string Version, ReferenceDotKtMetadata DotKt);

sealed class ReferenceDotKtMetadata
{
    public readonly List<string> Diagnostics = new();

    // CALL-SUBSTITUTION metadata (sourced from the ref.dll, consumed by MemberCallSubstitution; NOT serialized).
    // ownerFqn (the Kotlin FQN, e.g. "kotlin.String") -> the BCL alias it binds to ("System.String"), from a
    // class-level @ClrTypeAlias (the type-identity binding) or, for a not-yet-renamed bound class, a class-level @ClrIntrinsic.
    public readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> TypeKinds = new(StringComparer.Ordinal);   // ownerFqn -> class/struct/interface/enum
    public readonly Dictionary<string, int> TypeArity = new(StringComparer.Ordinal);       // ownerFqn -> generic arity
    public readonly Dictionary<string, string[]> TypeParamNames = new(StringComparer.Ordinal); // ownerFqn -> generic param names
    public readonly HashSet<string> HelperTypes = new(StringComparer.Ordinal);            // emitted "<>dotkt_ClrH_*" rule-3 helpers
    // Types carrying @kotlin.coroutines.RestrictsSuspension (BINARY-retained, so present on the ref.dll). A suspend
    // lambda whose RECEIVER is such a scope (e.g. SequenceScope) gets the RestrictedSuspendLambda SM base (bundle-6 P5).
    public readonly HashSet<string> RestrictsSuspensionTypes = new(StringComparer.Ordinal);
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
}

// A single ref.dll member's call-substitution shape. Owner is the Kotlin FQN ("kotlin.String"); Intrinsic is the
// @ClrIntrinsic BCL name or null (null + no @ClrProperty + !IsAbstract = a rule-3 hoist candidate). PropertyName (+ the
// READ/WRITE access flags) is set when the member carries @ClrProperty — an EXPLICIT .NET property accessor binding.
// Suspend = the Kotlin `suspend` modifier, read from the DotKt round-trip [KotlinFunction(flags)] attribute
// (Suspend bit = 4) in the LIVE MetadataLoadContext scan. Populated for the Task-based coroutine bundle (bundle 6):
// a cross-module call site must know "is this referenced callee suspend?" (its CLR shape is the Task<T> kickoff).
// NO consumer reads it yet — bundle 6 wires it.
sealed record MemberBinding(string Owner, string Name, int ParamCount, string Intrinsic, bool IsAbstract, bool IsStatic, string[] ParamTypes = null, int PropertyAccess = 0, string PropertyName = null, int[] ByrefPositions = null, bool Suspend = false);

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
        if (normalized.StartsWith("sfunc:", StringComparison.Ordinal)) return "already-clr";   // suspend fn type -> object (wave-2b)
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

    static string StringProp(JsonObject obj, string name) => (obj[name] as JsonValue)?.GetValue<string>();
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

    static string StringProp(JsonObject obj, string name) => (obj[name] as JsonValue)?.GetValue<string>();

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

    static string StringProp(JsonObject obj, string name) => (obj[name] as JsonValue)?.GetValue<string>();
    static bool BoolProp(JsonObject obj, string name) => (obj[name] as JsonValue)?.GetValue<bool>() == true;
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

    // === STRUCTURED TypeNode lowering (#37 m1) =================================================
    // The freeze put every kotc type field as a structured `{t:…}` Type node (DotKt.Bir.TypeNode). The
    // string helpers below (LowerTypeString/…) survive ONLY for the still-string legacy fields (`sig`
    // comma-list, a literal `accessOwner`/clr* owner FQN); every OBJECT-valued type slot flows through
    // LowerType(TypeNode). Output vocabulary mirrors the old strings exactly, wrapped in TypeNode:
    //   primitive -> Fqn("int"/…) (the CLR shorthand, so ilemit's opcode switch is unchanged),
    //   Unit-in-return / const|try Unit value -> Fqn("void"), a suspend fn VALUE slot -> Fqn("object"),
    //   @ClrTypeAlias owner -> Fqn(bcl[,args]) (no clr:/clrg: marker — ilemit derives from the name),
    //   an in-assembly/user/stdlib FQN -> unchanged, a `tv` -> unchanged (ilemit maps scope+i to !i/!!i).
    static readonly TypeNode VoidType = new TypeNode.Fqn("void");
    static readonly TypeNode ObjectType = new TypeNode.Fqn("object");

    static bool IsObjectish(TypeNode t) =>
        t is TypeNode.Fqn f && f.Args == null && (f.Name == "object" || f.Name == "kotlin.Any" || f.Name == "kotlin.Nothing");

    // typeArg = "this type sits in a generic type-ARGUMENT position": a primitive there stays BOXED
    // (kotlin.Int / the JVM-boxing dual-representation — Comparable<kotlin.Int>, IReadOnlyList<kotlin.Int>);
    // a bare/value primitive lowers to the CLR shorthand. Only Fqn.args propagate typeArg=true; array/byref/
    // nullable/fn element+param+return positions are value positions (typeArg=false).
    public static TypeNode LowerType(TypeNode t, bool refBuild, bool force, bool typeArg)
    {
        switch (t)
        {
            case TypeNode.Fqn f:
            {
                // The reference build keeps the pure-Kotlin surface verbatim (no recursion) unless an attribute
                // blob forces a concrete System.* type.
                if (!force && refBuild) return f;
                // `kotlin.Enum<E>` -> the NON-generic `System.Enum` (a Kotlin enum is a real CLR System.Enum, not
                // the generic stdlib class); drop the self-referential arg (`where T : Enum`).
                if (f.Name == "kotlin.Enum" && f.Args != null) return new TypeNode.Fqn("System.Enum");
                var loweredArgs = f.Args?.Select(a => LowerType(a, refBuild, force, typeArg: true)).ToArray();
                if (loweredArgs == null)
                {
                    // A leaf: a foundational primitive (numeric/bool/char + String/Any/Nothing + the unsigned set)
                    // lowers to the CLR shorthand in EVERY position — a type-arg primitive reifies as the CLR value type
                    // (`List<Int>` -> IReadOnlyList<int>), the CLR-idiomatic form (the boxed `kotlin.*` isn't an emitted
                    // type in the substitute/app build; the ref build keeps kotlin.* via the refBuild passthrough above).
                    var map = force ? KotlinAllToClr : KotlinToClr;
                    if (map.TryGetValue(f.Name, out var clr)) return new TypeNode.Fqn(clr);
                    // A non-generic @ClrTypeAlias type (StringBuilder/Regex/IComparable/…) -> the BCL FQN.
                    if (AliasBcl(f.Name) is string bclNonGen) return new TypeNode.Fqn(bclNonGen);
                    return f;   // user / stdlib / in-assembly FQN — identity preserved
                }
                // A generic application: a @ClrTypeAlias GENERIC owner -> the BCL generic (ilemit arity-constructs).
                if (AliasBcl(f.Name) is string bcl)
                {
                    // `Comparable<*>` / `Comparable<Any?>` -> the NON-generic `System.IComparable` (contravariant;
                    // no value type is IComparable<object>). A concrete arg keeps the generic form.
                    if (bcl == "System.IComparable" && loweredArgs.Length == 1 && IsObjectish(loweredArgs[0]))
                        return new TypeNode.Fqn("System.IComparable");
                    return new TypeNode.Fqn(bcl, loweredArgs);
                }
                return new TypeNode.Fqn(f.Name, loweredArgs);
            }
            case TypeNode.Tv:
                return t;   // scope+i preserved; ilemit maps scope:"type"->!i / scope:"method"->!!i
            case TypeNode.Fn fn:
                // A suspend-fn VALUE in a general TYPE slot is a Continuation state-machine OBJECT (not a delegate)
                // -> erase to object; a plain fn is a delegate (Func/Action) with lowered ret/params.
                return fn.Suspend ? ObjectType : LowerFnDelegate(fn, refBuild, force);
            case TypeNode.Nullable n:
                return new TypeNode.Nullable(LowerType(n.Of, refBuild, force, typeArg: false));
            case TypeNode.Array a:
                return new TypeNode.Array(LowerType(a.Elem, refBuild, force, typeArg: false));
            case TypeNode.ByRef b:
                return new TypeNode.ByRef(LowerType(b.Of, refBuild, force, typeArg: false));
            default:
                return t;
        }
    }

    // A function type kept as a DELEGATE (a `funcType` slot, or a plain fn in a type slot): lower ret (a Unit
    // ret -> void, Action vs Func) + params + receiver; the suspend flag is folded to false (the delegate shape
    // is preserved — the sequence/iterator closure path needs a real Func/Action, not an object-erased SM value).
    static TypeNode LowerFnDelegate(TypeNode.Fn fn, bool refBuild, bool force)
    {
        var ret = (fn.Ret is TypeNode.Fqn rf && rf.Args == null && rf.Name == "kotlin.Unit")
            ? VoidType : LowerType(fn.Ret, refBuild, force, typeArg: false);
        var ps = fn.Params.Select(p => LowerType(p, refBuild, force, typeArg: false)).ToArray();
        var recv = fn.Recv == null ? null : LowerType(fn.Recv, refBuild, force, typeArg: false);
        return new TypeNode.Fn(false, ret, ps, recv);
    }

    // Read a structured Type node out of the BIR JSON, lower it, and write it back.
    static JsonNode LowerTypeObject(JsonNode node, bool refBuild, bool force, bool typeArg)
    {
        var tn = TypeNode.Parse(node.ToJsonString());
        return TypeNode.Write(LowerType(tn, refBuild, force, typeArg));
    }

    // True iff a JSON value is a structured Type node (has a `t` discriminator) rather than a legacy type STRING
    // or a k-tagged sub-node.
    static bool IsTypeObject(JsonNode n) =>
        n is JsonObject o && o["t"] is JsonValue tv && tv.TryGetValue<string>(out var s) && s != null;

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
                else if (kv.Key == "funcType")
                    copy[kv.Key] = LowerFuncTypeValued(kv.Value, refBuild, here);  // delegate slot -> keep sfunc as func:
                else if (TypeKeys.Contains(kv.Key))
                    copy[kv.Key] = LowerTypeValued(kv.Value, refBuild, here);
                else
                    copy[kv.Key] = LowerNode(kv.Value, refBuild, here);
            }
            // H2 — SUSPEND FUNCTION-TYPE POSITION metadata. LowerTypeString erases an `sfunc:` token (a `suspend (…)->T`
            // type) to `object` at a param/field/property `type` or a method `ret` (a suspend-lambda VALUE is a
            // Continuation-based state-machine object, not a Func delegate). That fold destroys the suspend ORIGIN and
            // its arg/return SHAPE in the CLR signature, so a re-consuming DotKt assembly can no longer tell
            // `fun run(block: suspend () -> T)` from a plain function-typed one. Record the RAW pre-erasure `sfunc:`
            // token alongside — mirroring the `nullable`/`retNullable` positional-fact model — so ilemit can stamp
            // [KotlinSuspendFunctionType(raw)] and facadegen restore the suspend function type on re-consumption. This
            // carries the SHAPE STRING (not a bare flag): the erased CLR type is `object`, from which the arg/return
            // types are otherwise unrecoverable. Additive — ilemit reads it only on param/return/field/property builders;
            // harmless on any other node that happens to carry an sfunc-typed `type`/`ret`.
            if (SuspendFnSlot(obj["type"]) is JsonNode h2t) copy["suspendFnType"] = h2t;
            if (SuspendFnSlot(obj["ret"]) is JsonNode h2r) copy["retSuspendFnType"] = h2r;
            // ANNOTATION-BASE DERIVATION (annotation-base-lowering-to-bir2cir, USER 2026-07-02): kotc emits a user
            // `annotation class` as a plain class carrying `"annotation":true` (base:null) — the Kotlin fact. bir2cir
            // is the Kotlin<->CLR layer that DERIVES the CLR base: an annotation class extends System.Attribute. Set
            // the base here (the `clr:` form ilemit resolves to the referenced .NET type) and drop the Kotlin-only
            // flag so it never reaches the CIR/ilemit. The `here`/force path above already lowered its field/ctor
            // types with the full map (IsAttributeClass recognizes the flag), so the attribute is emittable.
            if (obj["annotation"] is JsonValue av && av.TryGetValue<bool>(out var annFlag) && annFlag)
            {
                copy["base"] = TypeNode.Write(new TypeNode.Fqn("System.Attribute"));
                copy.Remove("annotation");
            }
            // UNIT -> void DERIVATION (unit-fold-in-bir2cir, USER 2026-07-05): kotc emits the pure Kotlin `kotlin.Unit`
            // FQN identity for a "no value" position — the Unit-literal `const` type and a Unit-valued `try` expression
            // `type` — instead of naming the CLR `void` shorthand. bir2cir DERIVES `void` HERE, node-kind-scoped, so
            // ONLY these two value-slot positions fold. A Unit as a genuine VALUE elsewhere (a `var`/field/param type, a
            // generic TYPE-ARG like `Continuation[kotlin.Unit]`) stays `kotlin.Unit` — a `void` field/param/arg is
            // invalid metadata. (Return slots fold via the ReturnKeys/LowerReturnSlot path; this covers the rest.)
            if (copy["k"] is JsonValue kv2 && kv2.TryGetValue<string>(out var kind2) && (kind2 == "const" || kind2 == "try")
                && copy["type"] is JsonObject tobj && tobj["t"] is JsonValue tvt && tvt.TryGetValue<string>(out var tvts)
                && tvts == "fqn" && tobj["name"] is JsonValue tnm && tnm.TryGetValue<string>(out var tnms) && tnms == "kotlin.Unit")
                copy["type"] = TypeNode.Write(VoidType);
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

    // A type declaration is an attribute class iff kotc flagged it `"annotation":true` (the pure-Kotlin "this is an
    // annotation" fact) — bir2cir DERIVES the `: System.Attribute` base from that flag (annotation-base-lowering-to-
    // bir2cir, USER 2026-07-02; kotc no longer names the CLR base). Also true once the base has already been derived
    // (an already-`System.Attribute`-based class, e.g. a .NET attribute surfaced with a real base). Its ctor params /
    // fields / property accessors must carry concrete CLR types so the attribute is emittable — hence the force path.
    static bool IsAttributeClass(JsonObject obj) =>
        (obj["annotation"] is JsonValue a && a.TryGetValue<bool>(out var isAnn) && isAnn) ||
        (obj["base"] is JsonObject b && b["t"] is JsonValue bt && bt.TryGetValue<string>(out var bts) && bts == "fqn" &&
         b["name"] is JsonValue bn && bn.TryGetValue<string>(out var s) && s != null &&
         s.EndsWith("System.Attribute", StringComparison.Ordinal));

    // If a `type`/`ret` slot holds a suspend function type (Fn{suspend:true}), return the STRUCTURED fn node for the
    // H2 suspendFnType/retSuspendFnType metadata stamping (the slot's type itself is erased to `object` by LowerType,
    // so its arg/return SHAPE would otherwise be unrecoverable). Spec §0/§1: the metadata IS the structured Type node
    // (the old `sfunc:` string folds into it) — ilemit/facadegen consume the Fn directly, never a re-rendered string.
    static JsonNode SuspendFnSlot(JsonNode slot)
    {
        if (slot is JsonObject o && o["t"] is JsonValue tv && tv.TryGetValue<string>(out var s) && s == "fn"
            && o["suspend"] is JsonValue sv && sv.TryGetValue<bool>(out var susp) && susp)
            return o.DeepClone();
        return null;
    }

    // A `sig` value is a top-level comma-joined list of parameter type tokens (the overload key ilemit matches by
    // STRING EQUALITY against a method def's lowered `params[].type`). Lower each element so the call-side sig and
    // the def-side params stay in the SAME vocabulary, else overload resolution misses.
    static JsonNode LowerSigValue(JsonNode val, bool refBuild, bool force)
    {
        if (val is JsonValue scalar && scalar.TryGetValue<string>(out var s))
            return JsonValue.Create(string.Join(",", SplitTopLevel(s).Select(p => LowerTypeString(p, refBuild, force))));
        return LowerNode(val, refBuild, force);
    }

    // The `funcType` key names the DELEGATE type constructed by a closureNew/delegateNew/delegateInvoke. A suspend-fn
    // delegate (`sfunc:`) here is a genuine CLR delegate — the pre-P3 sequence/iterator closure path (`iterator {}`
    // yields a `closureNew` whose funcType is `sfunc:void:SequenceScope[..]`) — NOT an object-erased SM value slot.
    // So fold `sfunc:`->`func:` for THIS key only (delegate shape preserved), then lower normally; every OTHER type
    // slot (param/field/return/receiver) erases `sfunc:`->`object`. The APP suspend-lambda SM path never reaches
    // here: its `suspendLambdaNew` is replaced by a `new <SM>` node (SuspendLambdaLowering) before type lowering.
    static JsonNode LowerFuncTypeValued(JsonNode val, bool refBuild, bool force)
    {
        if (IsTypeObject(val))
        {
            var tn = TypeNode.Parse(val.ToJsonString());
            // A suspend fn in a funcType slot is a genuine delegate here (the sequence/iterator closure path) — keep
            // the shape, folding suspend->false; a plain fn likewise. Any non-fn type lowers normally.
            return TypeNode.Write(tn is TypeNode.Fn fn ? LowerFnDelegate(fn, refBuild, force) : LowerType(tn, refBuild, force, false));
        }
        if (val is JsonValue scalar && scalar.TryGetValue<string>(out var s))
            return JsonValue.Create(LowerTypeString(FoldSFuncToFunc(s), refBuild, force));
        return LowerTypeValued(val, refBuild, force);
    }

    static string FoldSFuncToFunc(string t) =>
        t.Contains("sfunc:", StringComparison.Ordinal) ? t.Replace("sfunc:", "func:", StringComparison.Ordinal) : t;

    // A return-slot value: a bare top-level `kotlin.Unit` -> `void` (UNIFORM, both modes); otherwise the normal type
    // lowering (so a return like clrg:List[kotlin.Int] still lowers its inner Int).
    static JsonNode LowerReturnValued(JsonNode val, bool refBuild, bool force)
    {
        if (IsTypeObject(val))
        {
            var tn = TypeNode.Parse(val.ToJsonString());
            // A Unit RETURN is the CLR `void` convention (uniform across ref AND substitute/app).
            if (tn is TypeNode.Fqn f && f.Args == null && f.Name == "kotlin.Unit")
                return TypeNode.Write(VoidType);
            return TypeNode.Write(LowerType(tn, refBuild, force, typeArg: false));
        }
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
        if (IsTypeObject(val))
            return LowerTypeObject(val, refBuild, force, typeArg: false);

        if (val is JsonValue scalar && scalar.TryGetValue<string>(out var s))
            return JsonValue.Create(LowerTypeString(s, refBuild, force));

        if (val is JsonArray arr)
        {
            var copy = new JsonArray();
            foreach (var item in arr)
            {
                if (item != null && IsTypeObject(item))
                    copy.Add(LowerTypeObject(item, refBuild, force, typeArg: false));
                else if (item is JsonValue iv && iv.TryGetValue<string>(out var its))
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
        // bundle-6 P3 wave-2b (final): `sfunc:` (the suspend-fn-type token) erases to `object` in EVERY build
        // (ref/rt/app), consistently. A suspend-lambda VALUE is a SuspendLambda state machine — a Continuation-
        // based OBJECT that extends BaseContinuationImpl — NOT a Func delegate; typing it as `object` lets the SM
        // pass anywhere a `suspend () -> T` is expected and satisfy the intrinsics' `as? BaseContinuationImpl`.
        // A TOP-LEVEL sfunc token is erased here before the refBuild/no-kotlin guards (an `sfunc:gp:T:` token has
        // no "kotlin." substring and would otherwise slip through with `sfunc:` intact — ilemit has no opcode for
        // it). A NESTED sfunc (a `func:`/generic arg) is reached by the structural recursion below, so the guard
        // must also descend when any `sfunc:` is present. ilemit NEVER receives `sfunc:`.
        var t = raw.Trim();
        if (t.StartsWith("sfunc:", StringComparison.Ordinal)) return "object";
        // The reference build keeps kotlin.* primitives verbatim (general path); the attribute force path lowers
        // unconditionally. A token with neither "kotlin." nor a nested "sfunc:" carries nothing to rewrite.
        if (!raw.Contains("sfunc:", StringComparison.Ordinal)
            && ((!force && refBuild) || !raw.Contains("kotlin.", StringComparison.Ordinal))) return raw;

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
            // `kotlin.Enum<E>` -> the NON-generic `System.Enum` (C2): a Kotlin `enum class` is emitted as a real CLR
            // `System.Enum`-backed enum (ilemit `DefineEnum`), which does NOT extend the stdlib's generic `kotlin.Enum<E>`
            // class. So a `fun <T : Enum<T>> …` self-referential bound (`@kotlin.Enum[gp:T]`) must lower to `System.Enum`
            // (the CLR `where T : Enum` idiom) or a real enum type argument violates the constraint (VerificationException).
            // Drop the self-referential type arg — System.Enum is non-generic.
            if (bareHead == "kotlin.Enum") return "clr:System.Enum";
            if (!head.StartsWith("clr", StringComparison.Ordinal) && AliasBcl(bareHead) is string genericBcl)
            {
                // `Comparable<*>` / `Comparable<Any?>` (the star / Any-projected comparable — kotc token
                // `kotlin.Comparable[object]`) -> the NON-generic `System.IComparable`, NOT `IComparable<object>` (C2).
                // `System.IComparable<in T>` is contravariant, so no VALUE type is `IComparable<object>` (a boxed Int is
                // `IComparable<int>` / non-generic `IComparable` only). The `compareBy`/`compareValuesBy` selector
                // `(T) -> Comparable<*>?` and its boxed selector value must ride the non-generic dispatch spine
                // (clrRawCompareTo's `as IComparable`); a reified `IComparable<object>` castclass fails on every primitive.
                // A CONCRETE arg (`Comparable<C>` / `Comparable<gp:T>`) keeps the generic form (`sorted`'s element cast).
                if (genericBcl == "System.IComparable" && args == "object") return "clr:System.IComparable";
                return "clrg:" + genericBcl + "[" + args + "]";
            }
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

    // Index of the ':' separating RET from ARGS in a `func:` BODY (leading "func:" already stripped). When the RET is a
    // NESTED func — `(Int)->(()->Int)` = body `func:kotlin.Int::kotlin.Int` — its own depth-0 ret/args colon must NOT
    // be taken as the outer separator (the old first-colon split produced the malformed `func:func:int:::kotlin.Int`,
    // leaving `:kotlin.Int` un-lowered). Recursively skip the whole inner func in that case; every OTHER ret shape keeps
    // the prior single-prefix scan (scoped narrowly so only the genuine nested-func-ret changes vs. the prior lowering).
    static int FuncRetEnd(string value)
    {
        if (value.StartsWith("func:", StringComparison.Ordinal) || value.StartsWith("sfunc:", StringComparison.Ordinal))
            return SkipTypeToken(value, 0);
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

    // Advance past exactly ONE type token at `i`; return the index just after it (a top-level ':' / ',' / ']' / end).
    // A func:/sfunc: token recurses through its ret + comma-list args; a modifier prefix recurses into its element;
    // a leaf/clrg:/clr:/gp: token scans to the next top-level delimiter with [] nesting protecting inner ':'/','.
    static int SkipTypeToken(string value, int i)
    {
        static bool At(string s, int i, string pre) => i + pre.Length <= s.Length && s.AsSpan(i, pre.Length).SequenceEqual(pre);
        foreach (var pre in new[] { "array:", "nullable:", "byref:" })
            if (At(value, i, pre)) return SkipTypeToken(value, i + pre.Length);
        foreach (var fp in new[] { "func:", "sfunc:" })
            if (At(value, i, fp))
            {
                i = SkipTypeToken(value, i + fp.Length);                              // ret
                if (i < value.Length && value[i] == ':') i++;                         // ret/args separator
                if (i < value.Length && value[i] != ':' && value[i] != ',' && value[i] != ']')
                {
                    i = SkipTypeToken(value, i);
                    while (i < value.Length && value[i] == ',') i = SkipTypeToken(value, i + 1);
                }
                return i;
            }
        foreach (var pre in new[] { "clrg:", "clr:", "gp:" })
            if (At(value, i, pre)) { i += pre.Length; break; }
        var depth = 0;
        for (; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '[') depth++;
            else if (c == ']') { if (depth == 0) break; depth--; }
            else if (depth == 0 && (c == ':' || c == ',')) break;
        }
        return i;
    }

    static int PrefixLength(string value)
    {
        foreach (var prefix in new[] { "clrg:", "clr:", "array:", "nullable:", "sfunc:", "func:", "gp:", "byref:" })
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

    public static void Apply(JsonNode root) => Process(root, new Dictionary<string, TypeNode>(StringComparer.Ordinal));

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    // The referenced-generic Iterator<elem> type node (the canonical CLR consumer target).
    static JsonNode IterType(TypeNode elem) => TypeJson.Write(new TypeNode.Fqn("kotlin.collections.Iterator", new[] { elem }));

    // A SINGLE document-order walk: a `var <name>` initialized by the bridge records name->elem (and retypes the var)
    // BEFORE its for-loop body is reached, so a hasNext/next on that local is rewritten with the elem current at that
    // point. This is order-sensitive on purpose: sibling/nested for-loops reuse the synthetic `<iterator>` name with
    // DIFFERENT element types, and each loop's body sits AFTER its own var-decl and BEFORE the next one — so the
    // forward walk always rewrites a call with its own loop's element (a two-pass collect-then-rewrite conflated them).
    static void Process(JsonNode node, Dictionary<string, TypeNode> map)
    {
        if (node is JsonObject obj)
        {
            var k = Str(obj["k"]);
            if (k == "var" && Str(obj["name"]) is string vn && obj["init"] is JsonObject init &&
                Str(init["k"]) == "callStatic" && TypeJson.OwnerName(init["owner"]) == Bridge &&
                Str(init["method"]) == "iteratorOverEnumerable" &&
                init["typeArgs"] is JsonArray ta && ta.Count == 1 && TypeJson.Read(ta[0]) is TypeNode elem)
            {
                map[vn] = elem;
                obj["type"] = IterType(elem);
            }
            // An `Iterator[elem]`-typed var initialized by a call INTO THE RT STDLIB (a `kotlin.*` owner — an unaliased
            // kotlin.collections interface like Set.iterator(), or an attributed top-level like MapsKt.iterator(map)
            // — or the ALREADY-SUBSTITUTED rule-3 helper `<>dotkt_ClrH_kotlin_*`: MemberCallSubstitution runs BEFORE
            // this pass, so a concrete alias receiver's `ArrayList<Int>().iterator()` arrives as a callStatic on the
            // rt helper owner, and its ArrayListIterator likewise implements the REAL Iterator, not the synthetic):
            // the runtime iterator is an rt-dll type implementing the REAL kotlin.collections.Iterator, never the app's
            // monomorphized `<>dotkt_KIterator_<elem>` synthetic — so its hasNext/next consumers must be re-pointed
            // exactly like the bridge case above. A USER-owned init (Countdown.iterator() returning an app-emitted
            // `object : Iterator<Int>` that implements the synthetic) is deliberately NOT registered — app-internal
            // producer/consumer stay consistent on the synthetic.
            else if (k == "var" && Str(obj["name"]) is string vn2 && obj["init"] is JsonObject init2 &&
                IteratorVarElem(TypeJson.Read(obj["type"])) is (string head, TypeNode elem2) &&
                (TypeJson.OwnerName(init2["owner"]) ?? TypeJson.OwnerName(init2["ownerType"]) ?? "") is string initOwner &&
                (initOwner.StartsWith("kotlin.", StringComparison.Ordinal)
                    || initOwner.StartsWith("<>dotkt_ClrH_kotlin_", StringComparison.Ordinal)))
            {
                // MUTABLE-MAP for-in REROUTE (bundle-6 BUG-2): `for ((k,v) in mm)` desugars to
                // `MutableMap.iterator(): MutableIterator<MutableEntry>`, which lowers to the SAME signature
                // `MapsKt.iterator(IDictionary<K,V>)` as the immutable `Map.iterator(): Iterator<Map.Entry>` — a genuine
                // COLLISION. ilemit binds the app's `iterator` call by name to the IMMUTABLE overload (the mutable one
                // is emitted as `iterator$dup2`), whose runtime iterator is `Iterator<Map.Entry>` — so hasNext/next
                // (typed MutableEntry from kotc) dispatch on a generic instantiation the object doesn't implement ->
                // EntryPointNotFound. Sidestep the collision: reroute the init to the SAME entries-based iterator that
                // `for (e in mm.entries)` already uses successfully — `iteratorOverEnumerable(clrMapMutableEntries(mm))`
                // — which yields a genuine `Iterator<MutableEntry>` (KotlinIteratorOverEnumerator over the live
                // ClrMutableMapEntry snapshot). Everything then stays consistently typed on MutableEntry (ilverify-clean),
                // and the read Iterator matches the wrapper's implemented interface. Only the MUTABLE entry element is
                // rerouted; the immutable `Map.iterator()` path already works and is left untouched.
                if (elem2 is TypeNode.Fqn { Name: "kotlin.collections.MutableMap$MutableEntry", Args: { } } mutEntry
                    && TypeJson.OwnerName(init2["owner"]) == "kotlin.collections.MapsKt" && Str(init2["method"]) == "iterator"
                    && init2["args"] is JsonArray iargs && iargs.Count == 1 && iargs[0] is JsonNode recv0)
                {
                    var (ek, ev) = EntryKvArgs(mutEntry);
                    obj["init"] = new JsonObject
                    {
                        ["k"] = "callStatic",
                        ["owner"] = TypeJson.Fqn(Bridge),
                        ["method"] = "iteratorOverEnumerable",
                        ["args"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["k"] = "callStatic",
                                ["owner"] = TypeJson.Fqn("kotlin.collections.ClrMapDefaultsKt"),
                                ["method"] = "clrMapMutableEntries",
                                ["args"] = new JsonArray { recv0.DeepClone() },
                                ["typeArgs"] = new JsonArray { TypeJson.Write(ek), TypeJson.Write(ev) },
                            },
                        },
                        ["typeArgs"] = new JsonArray { TypeJson.Write(elem2) },
                    };
                    map[vn2] = elem2;
                    obj["type"] = IterType(elem2);
                }
                else
                {
                    map[vn2] = elem2;
                    obj["type"] = TypeJson.Write(new TypeNode.Fqn(head, new[] { elem2 }));
                }
            }
            // `iterator()` dispatched through the monomorphized `<>dotkt_KIterable_<elem>` synthetic on a value that
            // came FROM THE RT STDLIB (`for ((i,v) in xs.withIndex())`: withIndex returns an rt IndexingIterable,
            // which implements IEnumerable<elem> — Iterable is @ClrTypeAlias — never the app's synthetic ->
            // EntryPointNotFound). Re-route the init through the ClrIteratorBridge (the same target kotc's aliased-
            // receiver path uses) and register the var. Receiver-gated: an APP-emitted `class R : Iterable<Int>`
            // (implements the synthetic; il-iterable) has a non-kotlin.* receiver owner and is left untouched.
            else if (k == "var" && Str(obj["name"]) is string vn3 && obj["init"] is JsonObject init3 &&
                IteratorVarElem(TypeJson.Read(obj["type"])) is (string, TypeNode elem3) &&
                Str(init3["k"]) == "callInstance" && Str(init3["method"]) == "iterator" &&
                (TypeJson.OwnerName(init3["ownerType"]) ?? "").StartsWith("<>dotkt_KIterable_", StringComparison.Ordinal) &&
                init3["recv"] is JsonObject krecv &&
                (TypeJson.OwnerName(krecv["owner"]) ?? TypeJson.OwnerName(krecv["ownerType"]) ?? "").StartsWith("kotlin.", StringComparison.Ordinal))
            {
                obj["init"] = new JsonObject
                {
                    ["k"] = "callStatic",
                    ["owner"] = TypeJson.Fqn(Bridge),
                    ["method"] = "iteratorOverEnumerable",
                    ["args"] = new JsonArray { krecv.DeepClone() },
                    ["typeArgs"] = new JsonArray { TypeJson.Write(elem3) },
                };
                map[vn3] = elem3;
                obj["type"] = IterType(elem3);
            }
            // A hasNext/next `callInstance` whose synthetic owner addresses one of those iterator locals -> a
            // `clrInstance` on the real referenced generic interface. callInstance routes through ResolveMethod/
            // ParseOwner (an EMITTED-type `_types` lookup that KeyNotFounds on a clrg: owner); the CLR-bound member path
            // is `clrInstance` (EmitClrCall), exactly how the substituted IReadOnlyList's get_Item/get_Count resolve.
            // next() returns the element, hasNext() returns Boolean; argTypes are empty. `type`/`ret` stay in the source
            // vocabulary — the later type-lowering pass lowers them.
            else if (k == "callInstance" && TypeJson.OwnerName(obj["ownerType"]) is string owner &&
                owner.StartsWith(SynthPrefix, StringComparison.Ordinal) && obj["recv"] is JsonObject recv &&
                Str(recv["k"]) == "local" && Str(recv["name"]) is string rn && map.TryGetValue(rn, out var e))
            {
                var method = Str(obj["method"]);
                obj["k"] = "clrInstance";
                obj.Remove("ownerType");
                obj.Remove("virtual");
                obj["type"] = IterType(e);
                obj["method"] = method;
                obj["argTypes"] = new JsonArray();
                obj["ret"] = method == "next" ? TypeJson.Write(e) : TypeJson.Fqn("kotlin.Boolean");
            }
            foreach (var kv in obj) if (kv.Value != null) Process(kv.Value, map);
        }
        else if (node is JsonArray arr)
            foreach (var it in arr) if (it != null) Process(it, map);
    }

    // The (K, V) type args of a Map.Entry / MutableEntry element token (`@kotlin.collections.MutableMap$MutableEntry[
    // string,int]` -> ("string","int")); ("object","object") when erased/unparseable. Used to instantiate the
    // clrMapMutableEntries<K,V> reroute target.
    static readonly TypeNode ObjT = new TypeNode.Fqn("object");
    static (TypeNode, TypeNode) EntryKvArgs(TypeNode.Fqn elem)
    {
        var a = elem.Args;
        return (a is { Length: >= 1 } && a[0] != null ? a[0] : ObjT,
                a is { Length: >= 2 } && a[1] != null ? a[1] : ObjT);
    }

    // `kotlin.collections.Iterator<elem>` / `kotlin.collections.MutableIterator<elem>` -> (head name, elem); null
    // otherwise. The elem may itself be a constructed type (`kotlin.collections.Map$Entry<K,V>`).
    static (string, TypeNode)? IteratorVarElem(TypeNode vt)
    {
        if (vt is TypeNode.Fqn { Args: { Length: 1 } args } f
            && f.Name is "kotlin.collections.Iterator" or "kotlin.collections.MutableIterator")
            return (f.Name, args[0]);
        return null;
    }
}

// PRODUCER-side unification of the monomorphized synthetic Iterator interface (bundle-6 iter/iterable).
// kotc emits a Kotlin `class C : Iterator<T>` (or an `object : Iterator<T>`) by synthesizing a per-element
// MONOMORPHIZED interface `<>dotkt_KIterator_<elem>` and having C implement THAT — the legacy "IL can't define a
// generic interface" A8 workaround. But bir2cir's type lowering already lowers every `Iterator<T>` TYPE token (a
// `C.iterator()` return, the desugared for-loop `<iterator>` var) to the REAL referenced generic
// `kotlin.collections.Iterator`1<elem>` (the rt stdlib DOES define it). So a single Kotlin `Iterator<Int>` ends up
// with TWO CLR identities: the app-local synthetic (what C implements + what hasNext/next dispatch on) and the rt
// generic (what the var / return are typed) — ilverify flags the store/return as StackUnexpected (it runs correct
// only because both are object-refs at runtime). This pass collapses the synthetic onto the generic, the ONE
// canonical identity: every implementer's `interfaces` entry, every type/return/param/cast token, and the
// synthetic-owner hasNext/next DISPATCH (retargeted to a `clrInstance` on the referenced generic, exactly as
// IteratorConsumerNormalization does for the kotlin.*-receiver case) are re-pointed, and the now-unreferenced
// synthetic interface DEFINITION is dropped. The element type is read straight off the synthetic definition's
// `next()` return — no name-demangling. Producer classes then implement the referenced generic interface directly
// (the proven `class C : Comparable<C>` -> IComparable`1<C> shape works for a leaf interface like Iterator).
//
// SCOPE — Iterator ONLY, never the `<>dotkt_KIterable_<elem>` synthetic. The rt `kotlin.collections.Iterable`1`
// is @ClrTypeAlias-projected onto IEnumerable<T>, so retargeting a `class C : Iterable<T>` onto it would demand a
// GetEnumerator implementation C does not carry (TypeLoadException). The KIterable synthetic stays app-local and
// self-consistent (its producer emits the IEnumerable shape correctly at runtime); crucially, every ilverify
// StackUnexpected in the iter/iterable samples is an ITERATOR-identity mismatch — the KIterator half — so unifying
// only that is both sufficient and safe. (The KIterable interface's own `iterator()` return, an `@<>dotkt_KIterator`
// token, IS rewritten to the generic here, keeping it consistent with its implementers' generic-returning override.)
//
// App build only (the rt stdlib emits Iterator itself); runs AFTER IteratorConsumerNormalization (which already
// handled the kotlin.*-receiver consumer vars) and BEFORE type lowering (so the element token lowers with the rest).
static class SyntheticIteratorUnification
{
    const string IterPrefix = "<>dotkt_KIterator_";

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    public static void Apply(JsonNode root)
    {
        if (root is not JsonObject o || o["types"] is not JsonArray types) return;

        // 1) Resolve the element type of each synthetic KIterator definition (its next() return type).
        var iterElem = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        foreach (var t in types)
        {
            if (t is not JsonObject to || Str(to["name"]) is not string nm) continue;
            if (nm.StartsWith(IterPrefix, StringComparison.Ordinal) && MethodRet(to, "next") is TypeNode el)
                iterElem[nm] = el;
        }
        if (iterElem.Count == 0) return;

        // 2) The canonical referenced-generic replacement type for each synthetic name (element still in the source
        //    vocabulary — kotlin.Int — so the subsequent BirTypeLowering lowers it uniformly).
        var repl = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        foreach (var (nm, el) in iterElem)
            repl[nm] = new TypeNode.Fqn("kotlin.collections.Iterator", new[] { el });

        // 3) Drop the synthetic interface DEFINITIONS (nothing references them once step 4 completes).
        var keep = new JsonArray();
        foreach (var t in types.ToList())
        {
            if (t is JsonObject to && Str(to["name"]) is string nm && repl.ContainsKey(nm)) continue;
            types.Remove(t);
            keep.Add(t);
        }
        o["types"] = keep;

        // 4) Re-point every remaining occurrence: synthetic-owner dispatch -> clrInstance on the generic; every
        //    other synthetic type token (interfaces / ret / param / var-type / cast) -> the generic type node.
        Walk(o, repl);
    }

    // The `ret` type of the first method named `mname` on a type, or null.
    static TypeNode MethodRet(JsonObject type, string mname)
    {
        if (type["methods"] is not JsonArray ms) return null;
        foreach (var m in ms)
            if (m is JsonObject mo && Str(mo["name"]) == mname)
                return TypeJson.Read(mo["ret"]);
        return null;
    }

    static void Walk(JsonNode node, Dictionary<string, TypeNode> repl)
    {
        if (node is JsonObject obj)
        {
            // A hasNext/next/iterator dispatch whose synthetic owner is being eliminated: retarget it to a
            // `clrInstance` on the referenced generic interface (a callInstance on a synthetic owner KeyNotFounds in
            // ilemit's emitted-`_types` lookup; the CLR-bound member path is clrInstance/EmitClrCall).
            if (Str(obj["k"]) == "callInstance" && TypeJson.OwnerName(obj["ownerType"]) is string owner &&
                repl.TryGetValue(owner, out var genOwner) && Str(obj["method"]) is string method)
            {
                var el = ((TypeNode.Fqn)genOwner).Args[0];
                obj["k"] = "clrInstance";
                obj.Remove("ownerType");
                obj.Remove("virtual");
                obj["type"] = TypeJson.Write(genOwner);
                obj["method"] = method;
                obj["argTypes"] = new JsonArray();
                obj["ret"] = method switch
                {
                    "next" => TypeJson.Write(el),
                    "hasNext" => TypeJson.Fqn("kotlin.Boolean"),
                    "iterator" => TypeJson.Write(genOwner),
                    _ => obj["ret"]?.DeepClone(),
                };
                if (obj["recv"] is JsonNode recv) Walk(recv, repl);
                if (obj["args"] is JsonArray a) foreach (var x in a) if (x != null) Walk(x, repl);
                return;
            }
            foreach (var key in obj.Select(kv => kv.Key).ToList())
            {
                var val = obj[key];
                if (val == null) continue;
                if (TypeJson.Read(val) is TypeNode tn) obj[key] = TypeJson.Write(ReplaceSynthetic(tn, repl));
                else Walk(val, repl);
            }
        }
        else if (node is JsonArray arr)
            for (var i = 0; i < arr.Count; i++)
            {
                var val = arr[i];
                if (val == null) continue;
                if (TypeJson.Read(val) is TypeNode tn) arr[i] = TypeJson.Write(ReplaceSynthetic(tn, repl));
                else Walk(val, repl);
            }
    }

    // Replace a synthetic-KIterator-named Fqn with its referenced-generic replacement, recursively through args.
    static TypeNode ReplaceSynthetic(TypeNode t, Dictionary<string, TypeNode> repl) => t switch
    {
        TypeNode.Fqn f when repl.TryGetValue(f.Name, out var gen) => gen,
        TypeNode.Fqn { Args: null } f => f,
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args.Select(a => ReplaceSynthetic(a, repl)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(ReplaceSynthetic(n.Of, repl)),
        TypeNode.Array a => new TypeNode.Array(ReplaceSynthetic(a.Elem, repl)),
        TypeNode.ByRef b => new TypeNode.ByRef(ReplaceSynthetic(b.Of, repl)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, ReplaceSynthetic(fn.Ret, repl),
            fn.Params.Select(p => ReplaceSynthetic(p, repl)).ToArray(), fn.Recv == null ? null : ReplaceSynthetic(fn.Recv, repl)),
        _ => t,
    };
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
        var hasPlaceholder = false;
        for (var j = 0; j < args.Count; j++) if (IsPlaceholder(args[j])) { hasPlaceholder = true; break; }
        if (!hasPlaceholder && args.Count >= sigCount) return;           // no omitted arg to fill
        var owner = TypeJson.OwnerName(node["owner"]) ?? TypeJson.OwnerName(node["ownerType"]);
        var method = Str(node["method"]);
        if (owner == null || method == null) return;
        var defaults = refs.KotlinDefaultsFor(owner, method, sigCount);
        if (defaults == null) return;
        // An extension receiver rides args[0] (the `__self` first arg of an emitted extension fun). A `= this` default
        // (substringAfter's missingDelimiterValue, a data-class copy) references it — bind the callee's `this` to it.
        var receiver = args.Count > 0 ? args[0] : null;
        // 1) Replace POSITIONAL `defaultArg` placeholders in place (kotc keeps a later provided arg's slot). Fill by array
        //    index — which equals the @KotlinDefault index (extension receiver counted first, matching kotc's stamp).
        //    A default reading an EARLIER param (`b = a * 10`) rides a `{param N}` token → this call's already-filled args[N]
        //    (Kotlin defaults reference only earlier params, and the loop fills lower indices first, so args[N] is resolved).
        for (var j = 0; j < args.Count; j++)
        {
            if (!IsPlaceholder(args[j])) continue;
            if (!defaults.TryGetValue(j, out var bir)) continue;         // no @KotlinDefault at this slot -> leave it (loud downstream)
            if (SpliceOne(bir, receiver, args) is JsonNode fill) args[j] = fill;
        }
        // 2) Append any purely-TRAILING omitted args (callee carries @KotlinDefault but kotc dropped the tail).
        for (var pos = args.Count; pos < sigCount; pos++)
        {
            if (!defaults.TryGetValue(pos, out var bir)) return;         // gap -> bail (leave the call unchanged)
            if (SpliceOne(bir, receiver, args) is JsonNode fill) args.Add(fill); else return;
        }
    }

    static bool IsPlaceholder(JsonNode n) => n is JsonObject o && Str(o["k"]) == "defaultArg";

    // Parse a @KotlinDefault BIR-json string and bind the callee's default-expression tokens to THIS call's args: `{this}`
    // (an extension receiver) -> the call's receiver, and `{param N}` (a read of another value param) -> the call's arg at
    // index N. A fresh deep clone per occurrence, so each filled value is a self-contained subtree.
    static JsonNode SpliceOne(string bir, JsonNode receiver, JsonArray args)
    {
        JsonNode parsed; try { parsed = JsonNode.Parse(bir); } catch { return null; }
        return SubstituteTokens(parsed, receiver, args);
    }

    // Rebuild `node`, replacing every `{"k":"this"}` with a deep clone of `receiver` and every
    // `{"k":"defaultArgParam","idx":N}` with a deep clone of `args[N]` (the callee's default-scope reads, resolved to this
    // call's values). Rebuilds fresh so no node is attached to two parents.
    static JsonNode SubstituteTokens(JsonNode node, JsonNode receiver, JsonArray args)
    {
        switch (node)
        {
            case JsonObject obj when Str(obj["k"]) == "this":
                return receiver == null ? obj.DeepClone() : receiver.DeepClone();
            case JsonObject obj when Str(obj["k"]) == "defaultArgParam":
            {
                var idx = (obj["idx"] as JsonValue)?.GetValue<int>() ?? -1;
                return idx >= 0 && idx < args.Count && args[idx] is JsonNode a ? a.DeepClone() : obj.DeepClone();
            }
            case JsonObject obj:
            {
                var res = new JsonObject();
                foreach (var kv in obj) res[kv.Key] = kv.Value == null ? null : SubstituteTokens(kv.Value, receiver, args);
                return res;
            }
            case JsonArray arr:
            {
                var res = new JsonArray();
                foreach (var it in arr) res.Add(it == null ? null : SubstituteTokens(it, receiver, args));
                return res;
            }
            default: return node.DeepClone();
        }
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
    // Monotonic counter for unique subSequence receiver/start spill-temp names (BUG-4 single-eval rewrite).
    static int _subSeqTmp;
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

    // --- structured Type versions (for the object-valued type slots; the string ones above stay for the m3 sig) ---
    static readonly TypeNode StringTn = new TypeNode.Fqn("kotlin.String");
    static bool IsCharSeqT(TypeNode t) => t switch
    {
        TypeNode.Fqn f => f.Name == CharSeq,
        TypeNode.Nullable n => IsCharSeqT(n.Of),
        TypeNode.Array a => IsCharSeqT(a.Elem),
        _ => false,
    };
    static bool IsCharSeqSlot(JsonNode n) => TypeJson.Read(n) is TypeNode t && IsCharSeqT(t);
    // A CharSequence Fqn (under nullable/array) -> kotlin.String, preserving the wrappers.
    static TypeNode LowerTokT(TypeNode t) => t switch
    {
        TypeNode.Nullable n => new TypeNode.Nullable(LowerTokT(n.Of)),
        TypeNode.Array a => new TypeNode.Array(LowerTokT(a.Elem)),
        _ => StringTn,
    };
    static JsonNode LowerSlot(JsonNode n) => TypeJson.Read(n) is TypeNode t ? TypeJson.Write(LowerTokT(t)) : n;

    // Lexical name -> declared type (params + local vars, with CharSequence already mapped to kotlin.String), plus
    // whether the enclosing method's return type was CharSequence. Copy-on-extend (mirrors StringCharSequenceBridge.Env).
    sealed class Env
    {
        public readonly Dictionary<string, TypeNode> Vars;
        public readonly bool RetWasCharSeq;
        public Env() { Vars = new(StringComparer.Ordinal); RetWasCharSeq = false; }
        Env(Dictionary<string, TypeNode> vars, bool ret) { Vars = vars; RetWasCharSeq = ret; }

        public Env WithDecl(JsonObject decl)
        {
            if (decl["params"] is not JsonArray ps) return this;
            var vars = new Dictionary<string, TypeNode>(Vars, StringComparer.Ordinal);
            foreach (var p in ps)
                if (p is JsonObject po && Str(po["name"]) is string pn && TypeJson.Read(po["type"]) is TypeNode pt)
                    vars[pn] = IsCharSeqT(pt) ? StringTn : pt;
            var ret = TypeJson.Read(decl["ret"]) is TypeNode rt ? IsCharSeqT(rt) : RetWasCharSeq;
            return new Env(vars, ret);
        }

        public Env WithVar(string name, TypeNode type)
        {
            var vars = new Dictionary<string, TypeNode>(Vars, StringComparer.Ordinal) { [name] = type };
            return new Env(vars, RetWasCharSeq);
        }
    }

    static HashSet<string> _localFns = new(StringComparer.Ordinal);
    // Lambda/method names used as a `delegateNew` target whose funcType carries a `<>dotkt_CharSequence` PARAM position.
    // Such a method is a delegate body invoked by a (stdlib or app-local) higher-order caller, which passes a GENUINE
    // `<>dotkt_CharSequence` value into that slot — e.g. `CharSequence.windowed(size){…}` calls `transform(subSequence(…))`
    // and `subSequence` returns a real `<>dotkt_StringCharSequence`, NOT a `System.String`. CharSeqStringLowering never
    // lowers a `funcType` token (it must keep matching the stdlib's `Func<CharSequence,R>` generic sig), so if we ALSO
    // collapsed the target lambda's own CharSequence param to `string` its member reads would be emitted as
    // `System.String.get_Length/get_Chars` and run against a non-String object -> garbage (a value-type `R` transform
    // reads pointer bits as an int; a reference-type `R` masked it because `toString()` is a virtual objMethod). So the
    // delegate contract requires the target's param to stay the (un-lowered) synthetic — exempt the whole subtree.
    static HashSet<string> _delegateTargets = new(StringComparer.Ordinal);

    public static JsonNode Apply(JsonNode root, HashSet<string> localTopLevelFns)
    {
        _localFns = localTopLevelFns ?? new HashSet<string>(StringComparer.Ordinal);
        _delegateTargets = CollectCharSeqDelegateTargets(root);
        return Walk(root, new Env());
    }

    // Collect the `delegateNew`/`delegateInvoke` target method names whose funcType names `<>dotkt_CharSequence` in a
    // PARAM position (i.e. an argument slot the caller supplies — `func:<ret>:<arg0>,<arg1>,…`). The funcType's leading
    // segment is the RETURN (a CharSequence return is handled by the return-coercion path, not this exemption).
    static HashSet<string> CollectCharSeqDelegateTargets(JsonNode root)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        void Scan(JsonNode n)
        {
            if (n is JsonObject o)
            {
                var k = Str(o["k"]);
                if (k is "delegateNew" or "delegateInvoke"
                    && Str(o["method"]) is string mn
                    && FuncTypeHasCharSeqParam(o["funcType"]))
                    set.Add(mn);
                foreach (var kv in o) if (kv.Value != null) Scan(kv.Value);
            }
            else if (n is JsonArray a) foreach (var it in a) if (it != null) Scan(it);
        }
        Scan(root);
        return set;
    }

    // A function type any of whose PARAMS is CharSequence (the delegate-target exemption). funcType is a structured Fn
    // (delegateNew) or, on a closureNew, a legacy `func:<ret>:<args>` string.
    static bool FuncTypeHasCharSeqParam(JsonNode ftNode)
    {
        if (TypeJson.Read(ftNode) is TypeNode.Fn fn) return fn.Params.Any(IsCharSeqT);
        if (Str(ftNode) is not string ft || !ft.StartsWith("func:", StringComparison.Ordinal)) return false;
        var rest = ft["func:".Length..];
        var ci = TopLevelColon(rest);
        if (ci < 0) return false;
        return SplitTopLevel(rest[(ci + 1)..]).Any(IsCharSeq);
    }

    // Index of the first `:` not nested inside `[`/`<`/`(` brackets, or -1.
    static int TopLevelColon(string s)
    {
        int depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '[' or '<' or '(') depth++;
            else if (c is ']' or '>' or ')') depth--;
            else if (c == ':' && depth == 0) return i;
        }
        return -1;
    }

    static JsonNode Walk(JsonNode node, Env env)
    {
        if (node is JsonObject obj)
        {
            // A delegate-target lambda keeps its signature matching the (un-lowered) funcType — do not collapse its
            // CharSequence params/reads to String. Leave the whole subtree verbatim (its member reads stay virtual
            // interface calls that resolve on the real <>dotkt_CharSequence the caller passes in).
            if (obj["k"] == null && Str(obj["name"]) is string dn && _delegateTargets.Contains(dn))
                return obj.DeepClone();
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
                && Str(wo["name"]) is string vn && TypeJson.Read(wo["type"]) is TypeNode vt)
                cur = cur.WithVar(vn, IsCharSeqT(vt) ? StringTn : vt);
        }
        return copy;
    }

    static JsonNode Transform(JsonObject node, Env env)
    {
        var k = Str(node["k"]);

        // A member READ on a CharSequence value (kotc: callInstance whose ownerType is the synthetic). A stdlib
        // CharSequence-EXTENSION is a callStatic (receiver as arg[0]), never this shape, so this only ever hits the
        // synthetic interface's own length/get/subSequence.
        if (k == "callInstance" && IsCharSeqSlot(node["ownerType"]))
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
                if (IsCharSeqSlot(node["type"]))
                {
                    node["type"] = LowerSlot(node["type"]);
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
                if (IsCharSeqSlot(node["type"]) && node["e"] is JsonNode ce)
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
                if (p is JsonObject po && IsCharSeqSlot(po["type"])) po["type"] = LowerSlot(po["type"]);
        if (IsCharSeqSlot(node["ret"])) node["ret"] = LowerSlot(node["ret"]);
        if (node["k"] == null && IsCharSeqSlot(node["type"]) && node["name"] != null)
            node["type"] = LowerSlot(node["type"]);   // a field {name,type}
    }

    // A LOCAL top-level call (owner null, method in this assembly): lower each CharSequence `sig` slot to kotlin.String
    // and coerce the matching arg (a non-String value -> implicit .toString()). An EXTERNAL stdlib call (attributed
    // owner, or a name absent from localTopLevelFns) is left untouched -> the StringCharSequenceBridge handles it.
    static void LowerLocalCall(JsonObject node, Env env)
    {
        if (TypeJson.OwnerName(node["owner"]) != null) return;   // attributed -> external
        if (Str(node["method"]) is not string method || !_localFns.Contains(method)) return;
        if (Str(node["sig"]) is not string sig) return;
        var parts = SplitTopLevel(sig).ToList();   // sig stays the m3 legacy string
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
        if (IsCharSeqSlot(node["dynRet"])) node["dynRet"] = LowerSlot(node["dynRet"]);
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
                    ["k"] = "clrPropGet", ["type"] = TypeJson.Fqn("System.String"), ["name"] = "Length",
                    ["retType"] = TypeJson.Fqn("System.Int32"), ["static"] = false, ["recv"] = recv?.DeepClone(),
                };
            case "get":
                return new JsonObject
                {
                    ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.String"), ["method"] = "get_Chars",
                    ["argTypes"] = new JsonArray { TypeJson.Fqn("System.Int32") }, ["ret"] = TypeJson.Fqn("System.Char"),
                    ["recv"] = recv?.DeepClone(),
                    ["args"] = new JsonArray { args != null && args.Count > 0 ? args[0].DeepClone() : null },
                };
            case "subSequence":
                if (args == null || args.Count < 2) return null;
                // `cs.subSequence(a, b)` -> `cs.Substring(a, b - a)`. `a` (start) is needed BOTH as Substring's
                // start arg AND inside the length `b - a`, so a naive rewrite evaluates `a` twice — a side-effecting
                // start index runs twice (bundle-6 BUG-4). Spill the receiver and start to temps (a `valueBlock`) so
                // each subexpression evaluates exactly once, in Kotlin order (receiver, then start, then end).
                var id = System.Threading.Interlocked.Increment(ref _subSeqTmp);
                var recvTmp = "$subSeqRecv$" + id;
                var startTmp = "$subSeqStart$" + id;
                return new JsonObject
                {
                    ["k"] = "valueBlock",
                    ["stmts"] = new JsonArray
                    {
                        new JsonObject { ["k"] = "var", ["name"] = recvTmp, ["type"] = TypeJson.Fqn("System.String"), ["init"] = recv?.DeepClone() },
                        new JsonObject { ["k"] = "var", ["name"] = startTmp, ["type"] = TypeJson.Fqn("System.Int32"), ["init"] = args[0].DeepClone() },
                    },
                    ["result"] = new JsonObject
                    {
                        ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.String"), ["method"] = "Substring",
                        ["argTypes"] = new JsonArray { TypeJson.Fqn("System.Int32"), TypeJson.Fqn("System.Int32") }, ["ret"] = TypeJson.Fqn("System.String"),
                        ["recv"] = new JsonObject { ["k"] = "local", ["name"] = recvTmp },
                        ["args"] = new JsonArray
                        {
                            new JsonObject { ["k"] = "local", ["name"] = startTmp },
                            new JsonObject { ["k"] = "bin", ["op"] = "-", ["l"] = args[1].DeepClone(), ["r"] = new JsonObject { ["k"] = "local", ["name"] = startTmp } },
                        },
                    },
                };
            default:
                return null;
        }
    }

    // A value flowing into a now-`string` slot: a provably-String value needs NO coercion (return null); anything else
    // (a StringBuilder, an Any) is snapshot via `.toString()` (the returned wrapper is a fresh, detached node). Callers
    // assign the wrapper only when non-null, avoiding a JsonNode reparenting error.
    //
    // NULL-SAFE (bundle-6 BUG-3): a bare `objMethod ToString` (callvirt object::ToString) NREs when `value` is null —
    // Kotlin's `x.toString()` on a null yields "null". Route through the `Any?.toString()` stdlib extension
    // (`kotlin.LibraryKt.toString` == `this?.toString() ?: "null"`), which is null-safe AND preserves the virtual
    // dispatch for a StringBuilder/Any (its `this?.toString()` calls the member override). `value` here is always a
    // CharSequence/StringBuilder/Any REFERENCE (it flows into a string slot), so no value->object boxing is needed.
    static JsonNode CoerceOrNull(JsonNode value, Env env)
    {
        if (IsStaticString(value, env)) return null;
        return new JsonObject
        {
            ["k"] = "callStatic", ["owner"] = TypeJson.Fqn("kotlin.LibraryKt"), ["method"] = "toString",
            ["sig"] = "object", ["args"] = new JsonArray { value.DeepClone() },
        };
    }

    // POSITIVE static-String detection (mirrors StringCharSequenceBridge.IsStaticString, extended with dynRet and the
    // already-rewritten clr* String result nodes).
    static bool IsStaticString(JsonNode n, Env env)
    {
        if (n is not JsonObject o) return false;
        switch (Str(o["k"]))
        {
            case "const": return IsStringTokT(TypeJson.Read(o["type"]));
            case "local": return Str(o["name"]) is string nm && env.Vars.TryGetValue(nm, out var t) && IsStringTokT(t);
            case "cast": return IsStringTokT(TypeJson.Read(o["type"]));
            case "concat": return true;   // string concatenation
            case "this": return false;
            default:
                return IsStringTokT(TypeJson.Read(o["ret"]) ?? TypeJson.Read(o["retType"]) ?? TypeJson.Read(o["dynRet"]));
        }
    }

    static bool IsStringTokT(TypeNode t) => t is TypeNode.Fqn { Args: null } f && StringTokens.Contains(f.Name);

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
        public readonly Dictionary<string, TypeNode> Vars;
        public readonly TypeNode RetType;
        public Env() { Vars = new(StringComparer.Ordinal); RetType = null; }
        Env(Dictionary<string, TypeNode> vars, TypeNode retType) { Vars = vars; RetType = retType; }

        // A declaration node (has a `params` array — methods/lambdas always emit one, even empty) opens a child scope
        // seeded with its params and return type. A non-decl node (call/expr — no `params`) returns `this` unchanged.
        public Env WithDecl(JsonObject decl)
        {
            if (decl["params"] is not JsonArray ps) return this;
            var vars = new Dictionary<string, TypeNode>(Vars, StringComparer.Ordinal);
            foreach (var p in ps)
                if (p is JsonObject po && Str(po["name"]) is string pn && TypeJson.Read(po["type"]) is TypeNode pt)
                    vars[pn] = pt;
            return new Env(vars, TypeJson.Read(decl["ret"]) ?? RetType);
        }

        public Env WithVar(string name, TypeNode type)
        {
            var vars = new Dictionary<string, TypeNode>(Vars, StringComparer.Ordinal) { [name] = type };
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
                && Str(wo["name"]) is string vn && TypeJson.Read(wo["type"]) is TypeNode vt)
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
        if (IsCharSeqT(TypeJson.Read(node["type"])) && node["init"] is JsonNode init && IsStaticString(init, env))
            node["init"] = WrapAdapter(init);
    }

    // (c): a return of a static String into a CharSequence return type.
    static void WrapReturn(JsonObject node, Env env)
    {
        if (IsCharSeqT(env.RetType) && node["value"] is JsonNode v && IsStaticString(v, env))
            node["value"] = WrapAdapter(v);
    }

    // A structured Type is (nullable/array of) the CharSequence synthetic.
    static bool IsCharSeqT(TypeNode t) => t switch
    {
        TypeNode.Fqn f => f.Name == CharSeq,
        TypeNode.Nullable n => IsCharSeqT(n.Of),
        TypeNode.Array a => IsCharSeqT(a.Elem),
        _ => false,
    };
    static bool IsStringTokT(TypeNode t) => t is TypeNode.Fqn { Args: null } f && StringTokens.Contains(f.Name);

    // (e): `as CharSequence` on a static String -> REPLACE the (would-be InvalidCast) `castclass <>dotkt_CharSequence`
    // with the materializing adapter. A non-statically-String cast (an `Any?`->CharSequence runtime check) is left as
    // the plain cast — a runtime-type-check adapter helper for that is a follow-up (see docs 【4-A】).
    static JsonNode WrapCast(JsonObject node, Env env)
    {
        if (IsCharSeqT(TypeJson.Read(node["type"])) && node["e"] is JsonNode e && IsStaticString(e, env))
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
            case "const": return IsStringTokT(TypeJson.Read(o["type"]));
            case "local": return Str(o["name"]) is string nm && env.Vars.TryGetValue(nm, out var t) && IsStringTokT(t);
            case "cast": return IsStringTokT(TypeJson.Read(o["type"]));
            case "this": return false;
            default:
                // A CLR/Kotlin call node carrying an explicit result type (`ret`/`retType` = System.String).
                return IsStringTokT(TypeJson.Read(o["ret"]) ?? TypeJson.Read(o["retType"]));
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

// Non-generic `System.IComparable` bridge. A Kotlin `class C : Comparable<C>` lowers (via the stdlib's
// `@ClrTypeAlias("System.IComparable")` on `kotlin.Comparable`) to `C : System.IComparable<C>` — the GENERIC
// interface only. But the CLR-side natural-ordering dispatch spine is the NON-generic `System.IComparable`:
// the stdlib's `compareValues` casts `a as IComparable` and ilemit's constrained-compareTo emits the value-type-safe
// `IComparable.CompareTo(object)` fallback (a boxed primitive implements IComparable but NOT a reified
// `IComparable<object>`). Every comparable BCL type (Int32/String/DateTime/...) therefore implements BOTH faces;
// a user Kotlin type that implements only the generic face hits `EntryPointNotFoundException` (SAM-shim
// `a.compareTo(b)` inside the rt's `sortWith`) or `InvalidCastException` (`compareValues`) the moment a compiled
// stdlib body sorts it. Mirror the BCL convention: for every emitted CLASS whose lowered interfaces include
// `clrg:System.IComparable[X]`, add `clr:System.IComparable` + a `CompareTo(object)` bridge that casts the arg
// to X and forwards to the generic CompareTo. Non-ref builds only (the ref surface stays pure Kotlin).
static class ComparableBridgeSynthesis
{
    public static void Apply(JsonNode root)
    {
        if (root is not JsonObject o || o["types"] is not JsonArray types) return;
        foreach (var t in types)
        {
            if (t is not JsonObject to) continue;
            if ((to["kind"] as JsonValue)?.GetValue<string>() != "class") continue;   // interfaces carry no bodies
            if (to["interfaces"] is not JsonArray ifaces) continue;
            // Post-lowering the interfaces are structured Fqn: `System.IComparable` (non-generic) / `System.IComparable<X>`.
            TypeNode selfArg = null; var hasNonGeneric = false;
            foreach (var i in ifaces)
            {
                if (TypeJson.Read(i) is not TypeNode.Fqn f || f.Name != "System.IComparable") continue;
                if (f.Args == null) hasNonGeneric = true;
                else if (f.Args.Length == 1) selfArg = f.Args[0];
            }
            if (selfArg == null || hasNonGeneric) continue;   // 1-arg IComparable<X> only
            if (to["methods"] is not JsonArray methods) { methods = new JsonArray(); to["methods"] = methods; }
            // Idempotence: skip when a 1-arg CompareTo(object) is already declared (user-written or a prior pass).
            var exists = methods.OfType<JsonObject>().Any(m =>
                (m["name"] as JsonValue)?.GetValue<string>() == "CompareTo"
                && m["params"] is JsonArray ps && ps.Count == 1
                && TypeJson.Read((ps[0] as JsonObject)?["type"]) is TypeNode.Fqn { Args: null, Name: "object" or "kotlin.Any" });
            if (exists) continue;
            var owner = (to["name"] as JsonValue)?.GetValue<string>();
            if (string.IsNullOrEmpty(owner)) continue;
            // Forward target: the generic-face method as DECLARED on this type (normally renamed `CompareTo` by
            // DeclarationRename; tolerate an un-renamed `compareTo`). Virtual dispatch covers a base-declared slot.
            var target = methods.OfType<JsonObject>().FirstOrDefault(m =>
                (m["name"] as JsonValue)?.GetValue<string>() is "CompareTo" or "compareTo"
                && m["params"] is JsonArray ps1 && ps1.Count == 1);
            var targetName = target != null ? (target["name"] as JsonValue)?.GetValue<string>() : "CompareTo";
            ifaces.Add(TypeJson.Fqn("System.IComparable"));
            methods.Add(new JsonObject
            {
                ["name"] = "CompareTo",
                ["static"] = false,
                ["override"] = false,
                ["virtual"] = true,
                ["abstract"] = false,
                ["objectOverride"] = false,
                ["vis"] = "public",
                ["params"] = new JsonArray(new JsonObject { ["name"] = "obj", ["type"] = TypeJson.Fqn("object") }),
                ["ret"] = TypeJson.Fqn("int"),
                ["body"] = new JsonArray(new JsonObject
                {
                    ["k"] = "return",
                    ["value"] = new JsonObject
                    {
                        ["k"] = "callInstance",
                        ["ownerType"] = TypeJson.Fqn(owner),
                        ["virtual"] = true,
                        ["recv"] = new JsonObject { ["k"] = "this" },
                        ["method"] = targetName,
                        ["sig"] = MemberCallSubstitution.SigToken(selfArg),
                        ["args"] = new JsonArray(new JsonObject
                        {
                            ["k"] = "cast",
                            ["type"] = TypeJson.Write(selfArg),
                            ["e"] = new JsonObject { ["k"] = "local", ["name"] = "obj" },
                        }),
                    },
                }),
            });
        }
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
        ApplyRec(o);
        // NESTED / STANDALONE nullable-generic TYPE-ARG erasure (FIX 1 part-2). A `T?` that kotc left as the
        // inline token `nullable:gp:T` — nested in a `clrg:Owner[...]` arg list (e.g.
        // `clrg:System.Collections.Generic.IEnumerable[nullable:gp:T]`) or standalone as a param/field type —
        // has the SAME value-type-null fault as the return case: `nullable:gp:T` lowers to `Nullable<T>`, invalid
        // for an unconstrained (reference-allowed) T. Erase every such token to `object` (the boxed/erased nullable
        // rep that carries a real null), everywhere a type token appears (params, returns, fields, `sig`). ilemit
        // must NEVER see `nullable:gp:` — this fully consumes it, exactly as NullableFuncReturnErasure consumes the
        // `func:nullable:` returns (which this pass deliberately leaves for that twin — see EraseNullableGpToken).
        EraseNullableGpAllStrings(o);
    }

    static void ApplyRec(JsonObject o)
    {
        if (o["methods"] is JsonArray methods)
            foreach (var m in methods) ApplyToMethod(m);
        // FIELD / PROPERTY nullable-generic erasure (FIX 1 part-1). kotc marks a nullable-generic field/property
        // slot with a SEPARATE `"nullable":true` boolean next to `"type":"gp:T"` (a bare `gp:T` slot silently drops
        // the `?`, so a value-type instantiation stores default(T)=0 instead of a real null). Rewrite the `type` to
        // `object` so the slot becomes a reference slot holding a genuine null; ilemit boxes the value store and the
        // read boundary re-narrows (unbox.any / castclass), mirroring the return-erasure boundary handling.
        //
        // ACCESSOR + READER consistency (bundle-6 BUG-1: value-type `asSequence().filter{}` InvalidProgram). The
        // erased-to-`object` property must ALSO drag its ACCESSOR methods to `object` — otherwise `get_nextItem():gp:T`
        // reads the object field and returns an unboxed gp:T (invalid), and `set_nextItem(null)` pushes ldnull into a
        // value-type gp:T param slot (invalid). ilemit boxes a value arg into an object param and unbox.any's an
        // `as T` cast, but it does NOT unbox object->gp:T on a bare store/return. So we (a) retype the getter return
        // and setter param to `object`, and (b) retype any local `var` initialized from that getter to `object`, so
        // the trailing `result as T` (already present: `return result as T`) performs the single unbox.any. The
        // property METADATA row was already erased to object above, keeping row/getter/setter coherent (ilverify-clean).
        var getters = new HashSet<string>(StringComparer.Ordinal);
        var setters = new HashSet<string>(StringComparer.Ordinal);
        CollectNullableAccessors(o["properties"], getters, setters);
        EraseNullableGpDecls(o["fields"]);
        EraseNullableGpDecls(o["properties"]);
        // GENERAL body-local nullable-generic erasure (bundle-6 value-type-nullable LOCAL, the twin of the field/property
        // pass above). kotc marks a `var single: T? = null` value-type-nullable accumulator LOCAL with a sibling
        // `"nullable":true` next to `"type":"gp:T"`. Left as-is, the value-type `T` slot holds a null → the trailing
        // `single as T` unbox.any NREs (Sequence.single{}'s terminal). RetypeNullableGpVars erases the slot to `object`
        // (a real null survives; value stores box; the `as T` read re-narrows) — see there for why it gates on a
        // null-const init (to skip kotc's synthetic safe-call temps, whose implicit reads would corrupt).
        if (o["methods"] is JsonArray msLocals)
            foreach (var m in msLocals)
                if (m is JsonObject mo) RetypeNullableGpVars(mo["body"]);
        // FOREACH-OVER-NULLABLE-GENERIC-SOURCE erasure (bundle-6 BUG-1, value-type filterNotNull). A stdlib method
        // whose extension receiver is `Iterable<T?>` (kotc token `@kotlin.collections.Iterable[nullable:gp:T]`, erased
        // by the EraseNullableGpAllStrings sweep below to `IEnumerable<object>`) iterates it with a `forEachInline`
        // whose loop-var `elem` is the bare `gp:T`. When T is instantiated with a VALUE type, storing the object
        // `Current` (the typed enumerator is unavailable — ilemit falls back to the non-generic enumerator + Unbox_Any
        // for a `gp:T` elem) into the value slot unbox.any's a null element -> NRE (filterNotNullTo). Erase the loop-var
        // to `object` (the object enumerator yields object; a null survives), and re-narrow the loop var where it flows
        // into a value-typed call arg (clrCollAdd's `gp:T` param) via a `cast`->`gp:T` (unbox.any for value, castclass
        // for ref). The RECEIVER-side boxing (a value-type collection is NOT covariantly IEnumerable<object> on the CLR)
        // is the call-site's job (ValueTypeNullableCollectionArg). This is the loop-var twin of EraseNullableGpDecls.
        EraseForEachOverNullableGpSource(o);
        if ((getters.Count > 0 || setters.Count > 0) && o["methods"] is JsonArray ms2)
        {
            foreach (var m in ms2)
                if (m is JsonObject mo && (mo["name"] as JsonValue)?.GetValue<string>() is string nm)
                {
                    if (getters.Contains(nm)) mo["ret"] = TypeJson.Fqn("object");
                    if (setters.Contains(nm) && mo["params"] is JsonArray ps)
                        foreach (var p in ps)
                            if (p is JsonObject po && TypeJson.Read(po["type"]) is TypeNode.Tv)
                                po["type"] = TypeJson.Fqn("object");
                }
            if (getters.Count > 0)
                foreach (var m in ms2)
                    if (m is JsonObject mo) RetypeGetterReaderVars(mo["body"], getters);
            // Re-narrow the CALL-NODE `retType` of every read of an erased getter to `object`. kotc stamped the
            // call node with the property's declared (nullable-generic) return `gp:T`; the getter now RETURNS
            // `object`, so a stale `gp:T` retType makes ilemit insert a coercion unbox.any right after the call —
            // and when the read is ALSO wrapped in an explicit `as T` cast (`nextValue as T`, the common
            // `T?`-property reader), the cast unbox.any's AGAIN → a DOUBLE `unbox.any !T` that NREs on the
            // second (the first already produced a bare value, not a boxed reference). This is the reader twin of
            // the `mo["ret"]="object"` accessor erasure above: the call node's retType must agree with the
            // callee's (now-object) return so exactly ONE narrow (the source `as T`) survives. (SequenceBuilder
            // `next()`'s `nextValue as T` on a VALUE element was the symptom — a cold-sequence NRE.)
            if (getters.Count > 0)
                foreach (var m in ms2)
                    if (m is JsonObject mo) RetypeErasedGetterCalls(mo["body"], getters);
            // Force the value->object box at each CALL to an erased setter. ilemit cannot read the param types off a
            // TypeBuilder-re-anchored generic self-call (`set_nextItem` on `<>dotkt_obj146[gp:T]`), so its arg-coercion
            // silently skips the box: a `gp:T` value arg lands on the stack unboxed where the now-`object` param wants a
            // reference -> InvalidProgram in calcNext. Wrapping the arg in an explicit `cast`->object boxes it from the
            // SOURCE type (ilemit's cast emitter boxes a value/generic-param source), independent of param-type lookup.
            if (setters.Count > 0)
                foreach (var m in ms2)
                    if (m is JsonObject mo) WrapErasedSetterArgs(mo["body"], setters);
        }
        // Nested types (a generic class' member methods / fields) carry their own declaration lists.
        if (o["types"] is JsonArray types)
            foreach (var t in types) if (t is JsonObject to) ApplyRec(to);
    }

    // Wrap each argument of a `callInstance` to an erased setter (`set_X` in `setters`) in a `cast`->`object`, so ilemit
    // boxes a value/generic-param arg into the erased `object` param even when it can't resolve the re-anchored generic
    // method's param types. A `null`/already-reference arg becomes a redundant `castclass object` (valid, no box).
    static void WrapErasedSetterArgs(JsonNode node, HashSet<string> setters)
    {
        switch (node)
        {
            case JsonObject obj:
                if ((obj["k"] as JsonValue)?.TryGetValue<string>(out var k) == true && k == "callInstance"
                    && (obj["method"] as JsonValue)?.TryGetValue<string>(out var mn) == true && setters.Contains(mn)
                    && obj["args"] is JsonArray a)
                    for (var i = 0; i < a.Count; i++)
                        if (a[i] is JsonObject arg && (arg["k"] as JsonValue)?.GetValue<string>() != "cast")
                            a[i] = new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Fqn("object"), ["e"] = arg.DeepClone() };
                foreach (var kv in obj) WrapErasedSetterArgs(kv.Value, setters);
                break;
            case JsonArray arr:
                foreach (var it in arr) WrapErasedSetterArgs(it, setters);
                break;
        }
    }

    // Record the get_/set_ accessor names of every nullable generic-parameter PROPERTY (`type:"gp:T"` + `nullable:true`)
    // — captured BEFORE EraseNullableGpDecls rewrites the property type to `object` (the `gp:` test would then miss).
    static void CollectNullableAccessors(JsonNode arr, HashSet<string> getters, HashSet<string> setters)
    {
        if (arr is not JsonArray a) return;
        foreach (var d in a)
            if (d is JsonObject po
                && (po["nullable"] as JsonValue)?.TryGetValue<bool>(out var nb) == true && nb
                && TypeJson.Read(po["type"]) is TypeNode.Tv)
            {
                if ((po["get"] as JsonValue)?.TryGetValue<string>(out var g) == true && g != null) getters.Add(g);
                if ((po["set"] as JsonValue)?.TryGetValue<string>(out var s) == true && s != null) setters.Add(s);
            }
    }

    // Retype a local `var x: gp:T = <call to an erased getter>()` slot to `object`, so the object value read from the
    // now-`object` getter is held in a reference local until an explicit `as T` re-narrows it. Only the direct
    // reader-local pattern (init is a callInstance to a getter in `getters`); other uses re-narrow via their own cast.
    static void RetypeGetterReaderVars(JsonNode node, HashSet<string> getters)
    {
        switch (node)
        {
            case JsonObject obj:
                if ((obj["k"] as JsonValue)?.TryGetValue<string>(out var k) == true && k == "var"
                    && TypeJson.Read(obj["type"]) is TypeNode.Tv
                    && obj["init"] is JsonObject init
                    && (init["k"] as JsonValue)?.TryGetValue<string>(out var ik) == true && ik == "callInstance"
                    && (init["method"] as JsonValue)?.TryGetValue<string>(out var im) == true && getters.Contains(im))
                    obj["type"] = TypeJson.Fqn("object");
                foreach (var kv in obj) RetypeGetterReaderVars(kv.Value, getters);
                break;
            case JsonArray arr:
                foreach (var it in arr) RetypeGetterReaderVars(it, getters);
                break;
        }
    }

    // Retype the `retType` of every `callInstance` reading an erased getter (`get_X` in `getters`) to `object`, so
    // the CIR call node agrees with the getter's now-`object` return. Without this, a stale `retType:"gp:T"` makes
    // ilemit coerce (unbox.any) the object result to the value type at the call — and a wrapping `as T` cast then
    // unbox.any's the already-unboxed value AGAIN, NREing. Retyping to `object` leaves a single narrow (the `as T`).
    static void RetypeErasedGetterCalls(JsonNode node, HashSet<string> getters)
    {
        switch (node)
        {
            case JsonObject obj:
                if ((obj["k"] as JsonValue)?.TryGetValue<string>(out var k) == true && k == "callInstance"
                    && (obj["method"] as JsonValue)?.TryGetValue<string>(out var mn) == true && getters.Contains(mn)
                    && TypeJson.Read(obj["retType"]) is TypeNode.Tv or TypeNode.Nullable { Of: TypeNode.Tv })
                    obj["retType"] = TypeJson.Fqn("object");
                foreach (var kv in obj) RetypeErasedGetterCalls(kv.Value, getters);
                break;
            case JsonArray arr:
                foreach (var it in arr) RetypeErasedGetterCalls(it, getters);
                break;
        }
    }

    // GENERAL body-local twin of EraseNullableGpDecls: retype a NULL-INITIALIZED `k=="var"` local marked `type:"gp:T"`
    // + sibling `nullable:true` to `object`. The local counterpart of the field/property erasure — a value-type-nullable
    // accumulator local (`var single: T? = null` in Sequence.single{}) must hold a genuine null in a reference slot,
    // with value stores boxing and the read boundary (`single as T`) re-narrowing (unbox.any/castclass).
    //
    // WHY GATE ON A NULL-CONST INIT (not the bare marker). kotc stamps `nullable:true` on EVERY value-type-nullable `gp:`
    // local, INCLUDING compiler-synthesized safe-call receiver temps (`tmp0_safe_receiver` for `transform(x)?.let{…}` in
    // mapNotNullTo). Those temps init from an object-returning call and are read IMPLICITLY (`?.`/`.let`) with no explicit
    // `as T`, so erasing them to `object` corrupts the unbox (mapNotNull -> garbage; collmore NEW-FAIL). The `var x: T? =
    // null` accumulator idiom — the case that genuinely needs a surviving null — always inits to a null const and is read
    // through an explicit `as T`; keying on the null-const init selects exactly that idiom and excludes the synthetic
    // temps. (The `forEachInline` loop var over a nullable-generic SOURCE — filterNotNullTo's `for (element in
    // this: Iterable<T?>)` — is a DISTINCT axis needing a value-type-nullable COLLECTION receiver conversion the call
    // sites lack; broad erasure there corrupts hashCode/collectionToArray iterations. Left to the collection
    // dual-representation track — NOT erased here.)
    static void RetypeNullableGpVars(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                if ((obj["k"] as JsonValue)?.TryGetValue<string>(out var k) == true && k == "var"
                    && (obj["nullable"] as JsonValue)?.TryGetValue<bool>(out var nb) == true && nb
                    && TypeJson.Read(obj["type"]) is TypeNode.Tv
                    && (IsNullConstInit(obj["init"]) || IsNullableGenericMapGet(obj["init"])))
                    obj["type"] = TypeJson.Fqn("object");
                foreach (var kv in obj) RetypeNullableGpVars(kv.Value);
                break;
            case JsonArray arr:
                foreach (var it in arr) RetypeNullableGpVars(it);
                break;
        }
    }

    // True when a var initializer is a `Map`/`MutableMap` `.get(key)` call — its Kotlin result is `V?`, which
    // MemberCallSubstitution rewrites to the erased nullable-generic `clrMapGet<K,V>: object` (a present value boxes,
    // a missing key is a genuine `null`). A `var value: gp:V nullable:true = get(key)` slot (getOrPut's explicit
    // `val value = get(key)`, unlike getOrElse's `?:`-synthesized `object` subject) must therefore be an `object`
    // slot — else the object init is stored raw into a `!!V` slot and the `value == null` check never sees the null
    // (getOrPut on `MutableMap<K,primitive>` silently returned 0 and never inserted). The read boundary re-narrows:
    // `objEq(value, null)` reads it as `object`; the `else value` branch (cond typed `gp:V`) unbox.any's it back
    // (EmitNullableCoerced). Gated on the `overrides` marker (owner Map/MutableMap, member `get`), so it never hits
    // the safe-call receiver temps RetypeNullableGpVars deliberately excludes (those init from a `transform(x)` invoke).
    static bool IsNullableGenericMapGet(JsonNode init)
    {
        if (init is not JsonObject io) return false;
        if ((io["k"] as JsonValue)?.TryGetValue<string>(out var ik) != true || ik != "callInstance") return false;
        if (io["overrides"] is not JsonArray ovs) return false;
        foreach (var ov in ovs)
            if (ov is JsonObject oo
                && (oo["member"] as JsonValue)?.TryGetValue<string>(out var mem) == true && mem == "get"
                && TypeJson.OwnerName(oo["owner"]) is string own
                && (own == "kotlin.collections.Map" || own == "kotlin.collections.MutableMap"))
                return true;
        return false;
    }

    // True when a var initializer is the null literal (`{k:"const", value:null}`) — the `T? = null` accumulator idiom.
    // A JSON null property surfaces as a C# null JsonNode, so a `const` whose `value` node is null IS the null literal.
    static bool IsNullConstInit(JsonNode init) =>
        init is JsonObject io
        && (io["k"] as JsonValue)?.TryGetValue<string>(out var ik) == true && ik == "const"
        && io.ContainsKey("value") && io["value"] is null;

    // BUG-1 Part B: for each method, find a `forEachInline` whose SOURCE is a param typed as a nullable-generic
    // collection (`...[nullable:gp:X]`) and whose loop-var `elem` is the bare `gp:X`; erase the loop-var to `object`
    // (so the iteration yields boxed/null objects, not an unbox.any that NREs on a null value element) and re-narrow
    // the loop var wherever it flows into a call arg back to the original `gp:X` (unbox.any at the value consumer).
    static void EraseForEachOverNullableGpSource(JsonObject o)
    {
        if (o["methods"] is not JsonArray methods) return;
        foreach (var m in methods)
        {
            if (m is not JsonObject mo || mo["params"] is not JsonArray ps) continue;
            // param name -> the element type-var Tv of a `…<T?>` (Nullable(Tv)) collection param.
            var nullableSrc = new Dictionary<string, TypeNode.Tv>(StringComparer.Ordinal);
            foreach (var p in ps)
                if (p is JsonObject po
                    && (po["name"] as JsonValue)?.TryGetValue<string>(out var pn) == true
                    && TypeJson.Read(po["type"]) is TypeNode pt && ExtractNullableTv(pt) is TypeNode.Tv tp)
                    nullableSrc[pn] = tp;
            if (nullableSrc.Count > 0) ErodeForEach(mo["body"], nullableSrc);
        }
    }

    static void ErodeForEach(JsonNode node, Dictionary<string, TypeNode.Tv> nullableSrc)
    {
        switch (node)
        {
            case JsonObject obj:
                if ((obj["k"] as JsonValue)?.TryGetValue<string>(out var k) == true && k == "forEachInline"
                    && obj["src"] is JsonObject src
                    && (src["k"] as JsonValue)?.TryGetValue<string>(out var sk) == true && sk == "local"
                    && (src["name"] as JsonValue)?.TryGetValue<string>(out var sn) == true
                    && nullableSrc.TryGetValue(sn, out var tp)
                    && TypeJson.Read(obj["elem"]) is TypeNode.Tv el && el == tp
                    && (obj["var"] as JsonValue)?.TryGetValue<string>(out var lv) == true)
                {
                    obj["elem"] = TypeJson.Fqn("object");
                    RenarrowLoopVarArgs(obj["body"], lv, el);
                }
                foreach (var kv in obj) ErodeForEach(kv.Value, nullableSrc);
                break;
            case JsonArray arr:
                foreach (var it in arr) ErodeForEach(it, nullableSrc);
                break;
        }
    }

    // Wrap every reference to the (now-`object`) loop var `lv` that appears as a CALL argument in a `cast`->`origElem`
    // (the Tv), so a value-type consumer unbox.any's the boxed element. The null-check use (`objEq(element, null)`) is
    // NOT a call arg and is correctly left as `object`.
    static void RenarrowLoopVarArgs(JsonNode node, string lv, TypeNode.Tv origElem)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["args"] is JsonArray a)
                    for (var i = 0; i < a.Count; i++)
                        if (a[i] is JsonObject ai
                            && (ai["k"] as JsonValue)?.TryGetValue<string>(out var ak) == true && ak == "local"
                            && (ai["name"] as JsonValue)?.TryGetValue<string>(out var an) == true && an == lv)
                            a[i] = new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Write(origElem), ["e"] = ai.DeepClone() };
                foreach (var kv in obj) RenarrowLoopVarArgs(kv.Value, lv, origElem);
                break;
            case JsonArray arr:
                foreach (var it in arr) RenarrowLoopVarArgs(it, lv, origElem);
                break;
        }
    }

    // A field/property whose slot is a nullable generic parameter (`type:"gp:T"` + sibling `nullable:true`) -> the
    // reference `object` slot. Only the boolean-marked `gp:` form; the inline `nullable:gp:T` form (should it appear
    // on a decl `type`) is caught by the blanket EraseNullableGpAllStrings sweep.
    static void EraseNullableGpDecls(JsonNode arr)
    {
        if (arr is not JsonArray a) return;
        foreach (var d in a)
            if (d is JsonObject fo
                && (fo["nullable"] as JsonValue)?.TryGetValue<bool>(out var nb) == true && nb
                && TypeJson.Read(fo["type"]) is TypeNode.Tv)
                fo["type"] = TypeJson.Fqn("object");
    }

    // Blanket type-slot sweep applying EraseNullableTv to every structured Type in the tree — a `Nullable(Tv)` (a
    // value-type-nullable type variable `T?`) erases to `object` wherever it sits (a clrg-nested type-arg / field /
    // standalone-param), the same value-type-null fault as the return case. Mirrors NullableFuncReturnErasure.
    static void EraseNullableGpAllStrings(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var child = obj[key];
                    if (child == null) continue;
                    // A call's `sig` is a LEGACY m3 STRING token (`clrg:System.Collections.Generic.IEnumerable[nullable:gp:T]`),
                    // not a structured TypeNode — TypeJson.Read misses it, so the structured EraseNullableTv above never
                    // touches it, and the CALL keeps `nullable:gp:T` while the callee DEF's `Iterable<T?>` param erases to
                    // `IEnumerable<object>`. That DEF/CALL sig mismatch drops ilemit off the exact-sig overload (two
                    // `filterNotNull`/`filterNotNullTo` overloads share a name) onto the ARRAY overload — an IEnumerable where
                    // `T[]` is expected -> segfault/empty result (il-chunk / il-collmore). Erase the `nullable:gp:X` tokens
                    // inside the sig string too so DEF and CALL agree, mirroring EraseNullableTv's structured rewrite.
                    if (key == "sig" && child is JsonValue sv && sv.TryGetValue<string>(out var sigStr)
                        && sigStr.Contains("nullable:gp:", StringComparison.Ordinal))
                    {
                        obj[key] = EraseNullableGpSigString(sigStr);
                        continue;
                    }
                    if (TypeJson.Read(child) is TypeNode tn) obj[key] = TypeJson.Write(EraseNullableTv(tn));
                    else EraseNullableGpAllStrings(child);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var child = arr[i];
                    if (child == null) continue;
                    if (TypeJson.Read(child) is TypeNode tn) arr[i] = TypeJson.Write(EraseNullableTv(tn));
                    else EraseNullableGpAllStrings(child);
                }
                break;
        }
    }

    // Replace every `nullable:gp:<ident>` token inside a legacy sig STRING with `object` (the boxed/erased nullable
    // rep that carries a real null), preserving all bracket/comma structure — the string twin of EraseNullableTv.
    // A token nested in a `clrg:Owner[...]` arg list (`IEnumerable[nullable:gp:T]` -> `IEnumerable[object]`), an
    // `array:nullable:gp:T` (-> `array:object`), a `func:` return/param, or a standalone comma-separated token all
    // collapse to `object`, so the erased CALL sig agrees with the callee DEF param (erased structurally to `object`).
    static string EraseNullableGpSigString(string sig)
    {
        const string probe = "nullable:gp:";
        var sb = new System.Text.StringBuilder(sig.Length);
        var i = 0;
        while (i < sig.Length)
        {
            var at = sig.IndexOf(probe, i, StringComparison.Ordinal);
            if (at < 0) { sb.Append(sig, i, sig.Length - i); break; }
            sb.Append(sig, i, at - i);
            sb.Append("object");
            // Skip PAST the `nullable:gp:` prefix and its type-param identifier (ends at `]`, `,`, or EOS).
            var j = at + probe.Length;
            while (j < sig.Length && (char.IsLetterOrDigit(sig[j]) || sig[j] == '_')) j++;
            i = j;
        }
        return sb.ToString();
    }

    // Replace every `Nullable(Tv)` (a value-type-nullable type variable) with `object`, recursively. LEAVES a func
    // RETURN nullable-tv (`Fn.Ret`) for NullableFuncReturnErasure (erasing it here would blind that pass); a func
    // param/receiver nullable-tv is erased.
    internal static TypeNode EraseNullableTv(TypeNode t) => t switch
    {
        TypeNode.Nullable { Of: TypeNode.Tv } => new TypeNode.Fqn("object"),
        TypeNode.Nullable n => new TypeNode.Nullable(EraseNullableTv(n.Of)),
        TypeNode.Fqn { Args: null } f => f,
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args.Select(EraseNullableTv).ToArray()),
        TypeNode.Array a => new TypeNode.Array(EraseNullableTv(a.Elem)),
        TypeNode.ByRef b => new TypeNode.ByRef(EraseNullableTv(b.Of)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, fn.Ret, fn.Params.Select(EraseNullableTv).ToArray(),
            fn.Recv == null ? null : EraseNullableTv(fn.Recv)),
        _ => t,
    };

    // The Tv of a Nullable(Tv) somewhere in a type (a nullable-generic collection element `…<T?>`), else null.
    static TypeNode.Tv ExtractNullableTv(TypeNode t) => t switch
    {
        TypeNode.Nullable { Of: TypeNode.Tv tv } => tv,
        TypeNode.Nullable n => ExtractNullableTv(n.Of),
        TypeNode.Fqn { Args: { } args } => args.Select(ExtractNullableTv).FirstOrDefault(x => x != null),
        TypeNode.Array a => ExtractNullableTv(a.Elem),
        TypeNode.ByRef b => ExtractNullableTv(b.Of),
        _ => null,
    };

    static void ApplyToMethod(JsonNode m)
    {
        if (m is not JsonObject mo) return;
        if (TypeJson.Read(mo["ret"]) is not TypeNode.Tv gp) return;
        if ((mo["retNullable"] as JsonValue)?.TryGetValue<bool>(out var rn) != true || !rn) return;
        mo["ret"] = TypeJson.Fqn("object");
        // A return-value expression whose STATIC type is the (now-erased) `gp:X` must also flow as object so its
        // null/value coercion targets object: a `return (cond typed gp:X)` (if-empty-null-else-elem) and a
        // `return (delegating call retType=gp:X)` (find -> firstOrNull) both become object end-to-end.
        RetypeReturns(mo["body"], gp);
    }

    static void RetypeReturns(JsonNode node, TypeNode.Tv gp)
    {
        switch (node)
        {
            case JsonObject obj:
                if ((obj["k"] as JsonValue)?.TryGetValue<string>(out var k) == true && k == "return"
                    && obj["value"] is JsonObject v)
                {
                    if (TypeJson.Read(v["type"]) is TypeNode.Tv vt && vt == gp) v["type"] = TypeJson.Fqn("object");
                    if (TypeJson.Read(v["retType"]) is TypeNode.Tv vr && vr == gp) v["retType"] = TypeJson.Fqn("object");
                }
                foreach (var kv in obj) RetypeReturns(kv.Value, gp);
                break;
            case JsonArray arr:
                foreach (var it in arr) RetypeReturns(it, gp);
                break;
        }
    }
}

// The TRANSFORM-SIDE twin of NullableGenericReturnErasure: erase a nullable FUNCTION-TYPE return
// (`(T) -> R?`, kotc-tokenized `func:nullable:<ret>:<args>`) to a `Func<…, object>` slot. Rationale: the open
// stdlib view (`nullable:gp:R`) and a caller's value instantiation (`nullable:int`) must lower to the SAME
// delegate type or the passed delegate is reinterpreted through a foreign Invoke signature (Func<int,int> read
// as Func<int,object> — the il-collmore mapNotNull InvalidProgram / il-sort sortedBy AccessViolation). `object`
// is the one rep every instantiation agrees on: value/generic returns box (null stays a real null); a REFERENCE
// instantiation is never nullable-marked by kotc and keeps its bare Func<…, T>, which flows into the object slot
// via Func's `out TResult` covariance. Three coordinated rewrites:
//   1. every `func:` TOKEN whose return segment is `nullable:`-marked (param slots, call sig strings,
//      delegateNew/closureNew/delegateInvoke funcTypes, nested occurrences) — ret segment -> `object`;
//   2. the backing lambda method of an erased delegateNew/closureNew — its `ret` -> `object` (+ the return-value
//      expression types, mirroring NullableGenericReturnErasure.RetypeReturns);
//   3. local dataflow repair where an erased delegateInvoke result lands in a typed var: a `gp:X` var is retyped
//      to `object` (it must still hold the null); a `nullable:V`/reference var keeps its type and the init is
//      wrapped in a `cast` (ilemit's universal unbox.any/castclass); a later var re-narrowing an object-retyped
//      local into a typed slot (the post-null-check `gp:R` copy) gets the same cast wrap.
// CATCH-CLAUSE WIDENING (bundle-6 ④): a Kotlin `catch (e: IndexOutOfBoundsException)` @ClrTypeAlias-es to a SINGLE .NET
// type, but .NET raises TWO unrelated out-of-range exceptions — `System.ArgumentOutOfRangeException` (List<T>.get_Item /
// most BCL collection indexers) and `System.IndexOutOfRangeException` (raw array access). Neither is a subtype of the
// other, so a single-type catch misses half the cases. Kotlin's semantics are "one IndexOutOfBoundsException catches
// any out-of-range access", so widen each such clause into TWO consecutive clauses (same body + var) covering both .NET
// types. Emits `clr:` tokens that pass through type-lowering unchanged. Keyed on the pure-Kotlin type name (runs before
// type lowering), so it is independent of whichever single .NET type the alias picks.
// STAR-PROJECTION IS-TEST (bundle-6 ④): `x is Collection<*>` / `is Map<*,*>` lowers (via the @ClrTypeAlias type map)
// to a REIFIED generic isinst — `isinst IReadOnlyCollection<object>` / `IDictionary<object,object>`. On .NET, reified
// generics have NO covariance on VALUE-type args (and IDictionary is invariant), so `List<int> is IReadOnlyCollection<object>`
// is FALSE — the check silently fails for every value-type collection. Kotlin's `is` on a star-projected type is a pure
// runtime shape test (the args are erased), so lower it to the NON-generic BCL interface, which a `List<int>`/`Dictionary<int,int>`
// DOES implement regardless of element type. A concrete-arg generic is-check is a Kotlin compile error, so every
// `is Collection<...>` here is necessarily `<*>` — keying on the alias FQN alone is sufficient. Only the isinst node's
// type token is rewritten (a Collection-typed VARIABLE keeps its generic form for member access). Runs before type
// lowering; emits `clr:` tokens that pass through unchanged. Non-ref only. (Set/MutableSet are intentionally absent:
// .NET HashSet<T> implements no non-generic collection interface beyond IEnumerable, so no faithful single token exists.)
// The COMPLETE star-projection lowering (bundle-6 `iscoll`). Lowering the isinst alone (Fix #6) made the is-test true
// for a value-type collection, but the guarded SMART-CAST member access (`(x as Collection<*>).size` in
// collectionSizeOrDefault) still castclassed the REIFIED `IReadOnlyCollection<object>` -> InvalidCast, regressing
// map/filter. The fix routes the WHOLE chain to the non-generic BCL interface: the `isinst`, the smart-cast `cast`,
// AND the member access on that star-cast (`.size` -> ICollection.Count, `.iterator()` -> IEnumerable.GetEnumerator,
// `[i]` -> IList.get_Item, `.contains` -> IList.Contains, `.isEmpty()` -> Count == 0). Runs BEFORE MemberCallSubstitution
// (so it sees the raw `callInstance get_size` on the kotlin.collections.* alias, not the already-substituted reified
// clrPropGet) and is gated on the APP build (attributeTopLevelOwner) — the ref/rt stdlib self-build keeps the reified
// form (its collectionSizeOrDefault is-test stays false -> the harmless capacity-hint default), which is exactly why
// this does NOT reintroduce the Fix #6 map/filter regression. A concrete-arg generic `is`-check is a Kotlin compile
// error, so every `is Collection<...>` is necessarily `<*>`; keying on the alias FQN is sufficient for the isinst,
// and the smart-cast + member rewrite is gated to all-`object` (star / erased) type args to leave a genuine
// `as List<String>` unchecked cast alone. Emits final CLR/`clr:` tokens that pass through type-lowering unchanged.
static class StarProjectionLowering
{
    // Kotlin generic collection alias -> the non-generic BCL interface a `List<int>`/`Dictionary<int,int>` implements
    // regardless of element type. (Set/MutableSet map to ICollection for the Count/is-test path; HashSet<T> implements
    // the non-generic ICollection.)
    static readonly Dictionary<string, string> NonGenericIface = new(StringComparer.Ordinal)
    {
        ["kotlin.collections.Collection"] = "System.Collections.ICollection",
        ["kotlin.collections.MutableCollection"] = "System.Collections.ICollection",
        ["kotlin.collections.List"] = "System.Collections.IList",
        ["kotlin.collections.MutableList"] = "System.Collections.IList",
        ["kotlin.collections.Set"] = "System.Collections.ICollection",
        ["kotlin.collections.MutableSet"] = "System.Collections.ICollection",
        ["kotlin.collections.Iterable"] = "System.Collections.IEnumerable",
        ["kotlin.collections.MutableIterable"] = "System.Collections.IEnumerable",
        ["kotlin.collections.Map"] = "System.Collections.IDictionary",
        ["kotlin.collections.MutableMap"] = "System.Collections.IDictionary",
    };

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    // True for a star-projected (or `object`-erased) generic collection type: owner is a known collection alias and
    // every type arg is `object`/`Any` (Kotlin allows only `<*>` in an is/as of these, so the args are always erased).
    static bool IsStarCollection(JsonNode slot, out string iface)
    {
        iface = null;
        if (TypeJson.Read(slot) is not TypeNode.Fqn f) return false;
        if (!NonGenericIface.TryGetValue(f.Name, out iface)) return false;
        if (f.Args == null) return true;                            // raw / bare collection alias
        return f.Args.All(a => a is TypeNode.Fqn { Args: null, Name: "object" or "kotlin.Any" });
    }

    public static void Apply(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            // Smart-cast member access: `callInstance` on a star-collection alias whose receiver is a `cast` to that
            // same star-collection -> a non-generic BCL member. Rewrite in place so the cast recv is lowered too.
            if (Str(obj["k"]) == "callInstance"
                && IsStarCollection(obj["ownerType"], out _)
                && obj["recv"] is JsonObject recv && Str(recv["k"]) == "cast"
                && IsStarCollection(recv["type"], out var recvIface)
                && LowerMember(obj, recv, recvIface) is JsonObject rewritten)
            {
                foreach (var kv in rewritten) obj[kv.Key] = kv.Value?.DeepClone();
                foreach (var stale in obj.Select(kv => kv.Key).Where(k => !rewritten.ContainsKey(k)).ToList())
                    obj.Remove(stale);
                // The rewritten node's recv/args are already final; recurse only into them (not the stale members).
                if (obj["recv"] != null) Apply(obj["recv"]);
                if (obj["args"] is JsonArray ra) foreach (var a in ra) if (a != null) Apply(a);
                return;
            }
            // Standalone star-projection `is`-test -> the non-generic interface (always safe: a boolean shape test).
            if (Str(obj["k"]) == "isinst" && IsStarCollection(obj["type"], out var ng))
                obj["type"] = TypeJson.Fqn(ng);
            foreach (var kv in obj) if (kv.Value != null) Apply(kv.Value);
        }
        else if (node is JsonArray arr)
            foreach (var it in arr) if (it != null) Apply(it);
    }

    // Build the non-generic replacement for a star-cast member call. `iface` is the non-generic interface the receiver
    // is cast to. Returns null for an unmapped member (leave it reified — the guarding isinst stays whatever it is).
    static JsonObject LowerMember(JsonObject call, JsonObject cast, string iface)
    {
        var recvInner = cast["e"];
        JsonObject CastTo(string toIface) => new() { ["k"] = "cast", ["type"] = TypeJson.Fqn(toIface), ["e"] = recvInner.DeepClone() };
        var args = call["args"] as JsonArray;
        switch (Str(call["method"]))
        {
            case "get_size":
            case "size":
                // `.size` -> ICollection/IList/IDictionary.Count.
                return new JsonObject { ["k"] = "clrPropGet", ["type"] = TypeJson.Fqn(iface), ["name"] = "Count", ["retType"] = TypeJson.Fqn("System.Int32"), ["static"] = false, ["recv"] = CastTo(iface) };
            case "isEmpty":
                // `.isEmpty()` -> Count == 0 (non-generic interfaces expose no IsEmpty).
                return new JsonObject
                {
                    ["k"] = "bin", ["op"] = "==", ["type"] = TypeJson.Fqn("System.Boolean"),
                    ["l"] = new JsonObject { ["k"] = "clrPropGet", ["type"] = TypeJson.Fqn(iface), ["name"] = "Count", ["retType"] = TypeJson.Fqn("System.Int32"), ["static"] = false, ["recv"] = CastTo(iface) },
                    ["r"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("System.Int32"), ["value"] = 0 },
                };
            case "iterator":
                // `.iterator()` -> IEnumerable.GetEnumerator (non-generic IEnumerator; Current is object == Any).
                return new JsonObject { ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.Collections.IEnumerable"), ["method"] = "GetEnumerator", ["argTypes"] = new JsonArray(), ["ret"] = TypeJson.Fqn("System.Collections.IEnumerator"), ["recv"] = CastTo("System.Collections.IEnumerable") };
            case "get":
            case "get_Item":
                // `list[i]` -> IList.get_Item(int) (returns object == Any).
                if (args == null || args.Count < 1) return null;
                return new JsonObject { ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.Collections.IList"), ["method"] = "get_Item", ["argTypes"] = new JsonArray { TypeJson.Fqn("System.Int32") }, ["ret"] = TypeJson.Fqn("System.Object"), ["recv"] = CastTo("System.Collections.IList"), ["args"] = new JsonArray { args[0].DeepClone() } };
            case "contains":
                // `list.contains(e)` -> IList.Contains(object) (only IList carries a non-generic Contains).
                if (args == null || args.Count < 1 || iface != "System.Collections.IList") return null;
                return new JsonObject { ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.Collections.IList"), ["method"] = "Contains", ["argTypes"] = new JsonArray { TypeJson.Fqn("System.Object") }, ["ret"] = TypeJson.Fqn("System.Boolean"), ["recv"] = CastTo("System.Collections.IList"), ["args"] = new JsonArray { args[0].DeepClone() } };
            default:
                return null;
        }
    }
}

static class CatchClauseWidening
{
    static readonly string[] IndexOobNet = { "System.ArgumentOutOfRangeException", "System.IndexOutOfRangeException" };

    public static void Apply(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["catches"] is JsonArray catches) WidenCatches(catches);
            foreach (var kv in obj) if (kv.Value != null) Apply(kv.Value);
        }
        else if (node is JsonArray arr)
            foreach (var it in arr) if (it != null) Apply(it);
    }

    static void WidenCatches(JsonArray catches)
    {
        for (var i = catches.Count - 1; i >= 0; i--)
        {
            if (catches[i] is not JsonObject c) continue;
            if (TypeJson.Read(c["excType"]) is not TypeNode.Fqn et
                || ReferenceMetadataIndex.BareOwnerFqn(et.Name) != "kotlin.IndexOutOfBoundsException") continue;
            catches.RemoveAt(i);
            for (var j = IndexOobNet.Length - 1; j >= 0; j--)   // insert in reverse -> keeps [ArgumentOOR, IndexOOR] order
            {
                var clone = (JsonObject)c.DeepClone();
                clone["excType"] = TypeJson.Fqn(IndexOobNet[j]);
                catches.Insert(i, clone);
            }
        }
    }
}

static class NullableFuncReturnErasure
{
    public static void Apply(JsonNode root)
    {
        if (root is not JsonObject o) return;
        var erasedDelegateMethods = new HashSet<string>(StringComparer.Ordinal);
        var erasedClosureInvokes = new HashSet<string>(StringComparer.Ordinal);   // closure TYPE names
        // Structural sweep first (records delegate targets + repairs var dataflow off the PRE-rewrite tokens),
        // then the token rewrite.
        StructuralSweep(o, erasedDelegateMethods, erasedClosureInvokes);
        RewriteAllStrings(o);
        if (o["methods"] is JsonArray methods)
            foreach (var m in methods)
                if (m is JsonObject mo && (mo["name"] as JsonValue)?.GetValue<string>() is string mn
                    && erasedDelegateMethods.Contains(mn))
                    EraseMethodRet(mo);
        if (o["types"] is JsonArray types)
            foreach (var t in types)
                if (t is JsonObject to && (to["name"] as JsonValue)?.GetValue<string>() is string tn
                    && erasedClosureInvokes.Contains(tn) && to["methods"] is JsonArray tms)
                    foreach (var tm in tms)
                        if (tm is JsonObject tmo && (tmo["name"] as JsonValue)?.GetValue<string>() == "invoke")
                            EraseMethodRet(tmo);
    }

    static readonly TypeNode ObjFqn = new TypeNode.Fqn("object");

    static void EraseMethodRet(JsonObject mo)
    {
        if (TypeJson.Read(mo["ret"]) is not TypeNode ret) return;
        if (ret is TypeNode.Fqn { Args: null, Name: "object" or "void" }) return;
        mo["ret"] = TypeJson.Write(ObjFqn);
        RetypeReturnValues(mo["body"], ret);
    }

    static void RetypeReturnValues(JsonNode node, TypeNode oldRet)
    {
        switch (node)
        {
            case JsonObject obj:
                if ((obj["k"] as JsonValue)?.TryGetValue<string>(out var k) == true && k == "return"
                    && obj["value"] is JsonObject v)
                {
                    if (TypeJson.Read(v["type"]) is TypeNode vt && vt == oldRet) v["type"] = TypeJson.Write(ObjFqn);
                    if (TypeJson.Read(v["retType"]) is TypeNode vr && vr == oldRet) v["retType"] = TypeJson.Write(ObjFqn);
                }
                foreach (var kv in obj) RetypeReturnValues(kv.Value, oldRet);
                break;
            case JsonArray arr:
                foreach (var it in arr) RetypeReturnValues(it, oldRet);
                break;
        }
    }

    // Walks the tree recording (a) delegateNew/closureNew whose funcType RETURN is nullable-marked and
    // (b) `var` nodes needing dataflow repair. Carries the per-walk set of var names retyped to object so a
    // downstream `var y: gp:R = local(x_object)` re-narrowing gets a cast wrap.
    static void StructuralSweep(JsonNode node, HashSet<string> delegateMethods, HashSet<string> closureTypes)
        => Sweep(node, delegateMethods, closureTypes, new HashSet<string>(StringComparer.Ordinal));

    static void Sweep(JsonNode node, HashSet<string> delegateMethods, HashSet<string> closureTypes, HashSet<string> objectVars)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var k = (obj["k"] as JsonValue)?.TryGetValue<string>(out var ks) == true ? ks : null;
                if (k == "delegateNew" && HasErasedRet(obj) && (obj["method"] as JsonValue)?.GetValue<string>() is string dm)
                    delegateMethods.Add(dm);
                else if (k == "closureNew" && HasErasedRet(obj) && (obj["closureType"] as JsonValue)?.GetValue<string>() is string ct)
                    closureTypes.Add(ct);
                else if (k == "var" && TypeJson.Read(obj["type"]) is TypeNode vt && obj["init"] is JsonObject init)
                {
                    var ik = (init["k"] as JsonValue)?.TryGetValue<string>(out var iks) == true ? iks : null;
                    var vn = (obj["name"] as JsonValue)?.GetValue<string>();
                    var isObj = vt is TypeNode.Fqn { Args: null, Name: "object" };
                    if (ik == "delegateInvoke" && HasErasedRet(init))
                    {
                        if (vt is TypeNode.Tv)
                        {
                            obj["type"] = TypeJson.Write(ObjFqn);
                            if (vn != null) objectVars.Add(vn);
                        }
                        else if (!isObj)
                            obj["init"] = new JsonObject { ["k"] = "cast", ["type"] = obj["type"].DeepClone(), ["e"] = init.DeepClone() };
                    }
                    else if (ik == "local" && !isObj && vt is not TypeNode.Nullable
                        && (init["name"] as JsonValue)?.GetValue<string>() is string src && objectVars.Contains(src))
                        // Post-null-check narrowing of an object-retyped local back into its typed slot:
                        // unbox.any/castclass via the universal `cast`.
                        obj["init"] = new JsonObject { ["k"] = "cast", ["type"] = obj["type"].DeepClone(), ["e"] = init.DeepClone() };
                }
                foreach (var kv in obj) Sweep(kv.Value, delegateMethods, closureTypes, objectVars);
                break;
            }
            case JsonArray arr:
                foreach (var it in arr) Sweep(it, delegateMethods, closureTypes, objectVars);
                break;
        }
    }

    static bool HasErasedRet(JsonObject node)
        => TypeJson.Read(node["funcType"]) is TypeNode.Fn { Suspend: false, Ret: TypeNode.Nullable };

    // Type-slot sweep: a NON-suspend function type whose RETURN is a Nullable (`(…) -> R?`) has its return erased to
    // `object` — the only CLR delegate return that carries a real null for a value-type R. Recurses nested funcs/args.
    static void RewriteAllStrings(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var child = obj[key];
                    if (child == null) continue;
                    if (TypeJson.Read(child) is TypeNode tn) obj[key] = TypeJson.Write(RewriteFnRet(tn));
                    else RewriteAllStrings(child);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var child = arr[i];
                    if (child == null) continue;
                    if (TypeJson.Read(child) is TypeNode tn) arr[i] = TypeJson.Write(RewriteFnRet(tn));
                    else RewriteAllStrings(child);
                }
                break;
        }
    }

    internal static TypeNode RewriteFnRet(TypeNode t) => t switch
    {
        TypeNode.Fn { Suspend: false } fn => new TypeNode.Fn(false,
            fn.Ret is TypeNode.Nullable ? new TypeNode.Fqn("object") : RewriteFnRet(fn.Ret),
            fn.Params.Select(RewriteFnRet).ToArray(), fn.Recv == null ? null : RewriteFnRet(fn.Recv)),
        TypeNode.Fn fn => new TypeNode.Fn(true, fn.Ret, fn.Params.Select(RewriteFnRet).ToArray(),
            fn.Recv == null ? null : RewriteFnRet(fn.Recv)),
        TypeNode.Nullable n => new TypeNode.Nullable(RewriteFnRet(n.Of)),
        TypeNode.Fqn { Args: null } f => f,
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args.Select(RewriteFnRet).ToArray()),
        TypeNode.Array a => new TypeNode.Array(RewriteFnRet(a.Elem)),
        TypeNode.ByRef b => new TypeNode.ByRef(RewriteFnRet(b.Of)),
        _ => t,
    };
}

// .NET EVENT `+=`/`-=` binding (the idiomatic ClrEvent<T> redesign). A .NET event is surfaced by facadegen/kotc as a
// read-only `kotlin.clr.ClrEvent<T>` property (a compile-time fiction — a .NET event is NOT a first-class value), and
// `w.Changed += handler` resolves through NORMAL Kotlin operator resolution to `w.Changed.plusAssign(handler)`. kotc
// emits that as the PLAIN operator call `callInstance(ownerType = kotlin.clr.ClrEvent, method = plusAssign/minusAssign,
// recv = <clrPropGet w Changed>, args = [handler])` — no `add_`/`remove_` naming, no CLR binding. This pass BINDS it:
// it reads the owner .NET type + event name straight off the clrPropGet member-access node and emits the EXISTING
// clrEventAdd/clrEventRemove node (ilemit's EmitClrEvent, unchanged) — so the emitted add/remove accessor IL is
// identical to the old `add_<E>`/`remove_<E>` model. The ClrEvent<T> value + the clrPropGet are consumed here, never
// emitted (a .NET event isn't materializable). This is the Kotlin<->CLR event relation, which is bir2cir's to own.
static class ClrEventOperatorBinding
{
    public static JsonNode Apply(JsonNode root) => Walk(root);

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    static JsonNode Walk(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var copy = new JsonObject();
            foreach (var kv in obj) copy[kv.Key] = kv.Value == null ? null : Walk(kv.Value);   // children first (bottom-up)
            return Transform(copy) ?? copy;
        }
        if (node is JsonArray arr)
        {
            var copy = new JsonArray();
            foreach (var item in arr) copy.Add(item == null ? null : Walk(item));
            return copy;
        }
        return node.DeepClone();
    }

    // A `callInstance` on kotlin.clr.ClrEvent whose method is plusAssign/minusAssign -> the add/remove accessor node.
    static JsonNode Transform(JsonObject node)
    {
        if (Str(node["k"]) != "callInstance") return null;
        if (TypeJson.OwnerName(node["ownerType"]) != "kotlin.clr.ClrEvent") return null;
        var method = Str(node["method"]);
        if (method != "plusAssign" && method != "minusAssign") return null;
        // The receiver is the event member-access `w.Changed`, emitted as a clrPropGet carrying the .NET owner type
        // (`type`), the event name (`name`), and the actual owner value (`recv`). Anything else is not an event op.
        if (node["recv"] is not JsonObject propGet || Str(propGet["k"]) != "clrPropGet") return null;
        if (node["args"] is not JsonArray args || args.Count != 1) return null;
        var isStatic = (propGet["static"] as JsonValue)?.GetValue<bool>() ?? false;
        return new JsonObject
        {
            ["k"] = method == "plusAssign" ? "clrEventAdd" : "clrEventRemove",
            ["type"] = propGet["type"]?.DeepClone(),
            ["event"] = propGet["name"]?.DeepClone(),
            ["static"] = isStatic,
            ["recv"] = isStatic ? null : propGet["recv"]?.DeepClone(),
            ["handler"] = args[0]?.DeepClone(),
        };
    }
}

// A `kotlin.reflect.KClass` member read (`T::class.simpleName` / `.qualifiedName`) -> its System.Type BCL member.
// kotc emits the pure-Kotlin property read `callInstance(kotlin.reflect.KClass[..].get_simpleName, recv = <::class>)`;
// the `::class` receiver is already a System.Type token (a `getType`/`classRef` node), and KClass is @ClrTypeAlias-ed
// onto System.Type, so the member binds to Type.Name / Type.FullName. This is the Kotlin<->CLR relation, so the
// System.Type / BCL-member knowledge lives here (not in kotc). Mirrors ClrEventOperatorBinding's bottom-up rewrite.
static class KClassMemberBinding
{
    public static JsonNode Apply(JsonNode root) => Walk(root);

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    static JsonNode Walk(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var copy = new JsonObject();
            foreach (var kv in obj) copy[kv.Key] = kv.Value == null ? null : Walk(kv.Value);   // children first (bottom-up)
            return Transform(copy) ?? copy;
        }
        if (node is JsonArray arr)
        {
            var copy = new JsonArray();
            foreach (var item in arr) copy.Add(item == null ? null : Walk(item));
            return copy;
        }
        return node.DeepClone();
    }

    static JsonNode Transform(JsonObject node)
    {
        if (Str(node["k"]) != "callInstance") return null;
        // ownerType is `kotlin.reflect.KClass` (its type-arg, if any, is dropped by OwnerName — we key on the identity).
        if (TypeJson.OwnerName(node["ownerType"]) != "kotlin.reflect.KClass") return null;
        var bcl = Str(node["method"]) switch
        {
            "get_simpleName" => "Name",
            "get_qualifiedName" => "FullName",
            _ => null,
        };
        if (bcl == null) return null;
        if (node["recv"] is not JsonObject recv) return null;   // the ::class receiver (a System.Type value)
        return new JsonObject
        {
            ["k"] = "clrPropGet",
            ["type"] = TypeJson.Fqn("System.Type"),
            ["name"] = bcl,
            ["retType"] = TypeJson.Fqn("System.String"),
            ["static"] = false,
            ["recv"] = recv.DeepClone(),
        };
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
        // VarTypes/TpConstraints hold STRUCTURED types (a param/local's slot Type, a type-param's constraint Types) —
        // walked natively by Constrainify/CollElemArg/MapKvArgs (a receiver's static type / a collection element).
        public readonly Dictionary<string, TypeNode> VarTypes;
        public readonly Dictionary<string, List<TypeNode>> TpConstraints;
        public SubstCtx()
        {
            VarTypes = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            TpConstraints = new Dictionary<string, List<TypeNode>>(StringComparer.Ordinal);
        }
        SubstCtx(SubstCtx parent)
        {
            VarTypes = new Dictionary<string, TypeNode>(parent.VarTypes, StringComparer.Ordinal);
            TpConstraints = new Dictionary<string, List<TypeNode>>(parent.TpConstraints, StringComparer.Ordinal);
        }
        // A child scope extended with this declaration's params + generic-parameter constraints. Returns `this`
        // unchanged when the node introduces no bindings (so plain nodes don't allocate a scope).
        //
        // SHADOWED-LOCAL disambiguation (bundle-6 BUG-2): a method/lambda's own local `var` decls must ALSO enter
        // VarTypes, so a `var x` that SHADOWS a same-named param `x` of a different type wins (its own type is what a
        // receiver read resolves to). Without this, a shadowing local was skipped and a call whose receiver is the
        // local kept the PARAM's (possibly `gp:`) type — mis-routing Constrainify to a constrained dispatch on a
        // concrete-typed local. Recorded AFTER params so the local shadows; scoped to this decl's own body (the walk
        // stops at a nested param-bearing decl, so an inner lambda's locals don't leak up). Mirrors the SM's
        // DisambiguateShadowedVars intent (a same-name local of a different type is a distinct binding).
        public SubstCtx Extend(JsonObject decl)
        {
            var ps = decl["params"] as JsonArray;
            var tps = decl["typeParams"] as JsonArray;
            // A method/accessor DECLARATION (a `params`+`body` node with no expression `k`) needs its local `var` types
            // recorded even when it has ZERO params — a param-less getter (get_groupValues) otherwise left VarTypes empty,
            // so a receiver read of a materialized local (mapTo's concrete `destination: ArrayList<String>`) could not
            // recover its element type and CollElemArg fell back to the `object` variance-approximation.
            var isDecl = ps != null && decl["body"] != null && decl["k"] == null;
            if ((ps == null || ps.Count == 0) && (tps == null || tps.Count == 0) && !isDecl) return this;
            var child = new SubstCtx(this);
            if (ps != null)
                foreach (var p in ps)
                    if (p is JsonObject po && (po["name"] as JsonValue)?.GetValue<string>() is string pn
                        && TypeJson.Read(po["type"]) is TypeNode pt)
                        child.VarTypes[pn] = pt;
            // TpConstraints is keyed POSITIONALLY, matching a receiver's `tv` (scope+index) — a class decl's params are
            // the TYPE scope, a method/fun's are the METHOD scope (the common constrained-build/compareTo receiver).
            if (tps != null)
            {
                var scope = (decl["kind"] as JsonValue)?.GetValue<string>() is "class" or "interface" ? "type" : "method";
                for (var i = 0; i < tps.Count; i++)
                    if (tps[i] is JsonObject to && to["constraints"] is JsonArray cs)
                        child.TpConstraints[scope + ":" + i] =
                            cs.Select(c => TypeJson.Read(c)).Where(c => c != null).ToList();
            }
            // Walk a DECLARATION's body once to record its local vars (a local shadows a same-name param; and a
            // materialized collection local is the receiver whose element type CollElemArg/Constrainify recover).
            if (isDecl && decl["body"] is JsonNode body) RecordLocalVars(body, child.VarTypes);
            return child;
        }

        // Record the `var name/type` of every local declaration in this decl's own body, so a local shadows a
        // same-named param. Stops at a nested param-bearing declaration (an inner lambda/fun scopes its own locals).
        static void RecordLocalVars(JsonNode node, Dictionary<string, TypeNode> vars)
        {
            switch (node)
            {
                case JsonObject o:
                    if ((o["k"] as JsonValue)?.GetValue<string>() == "var"
                        && (o["name"] as JsonValue)?.GetValue<string>() is string vn
                        && TypeJson.Read(o["type"]) is TypeNode vt)
                        vars[vn] = vt;
                    if (o["params"] is JsonArray ip && ip.Count > 0) return;   // nested decl: its locals are its own
                    foreach (var kv in o) if (kv.Value != null) RecordLocalVars(kv.Value, vars);
                    break;
                case JsonArray a:
                    foreach (var it in a) if (it != null) RecordLocalVars(it, vars);
                    break;
            }
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
            "staticField" => TransformStaticField(node, refs) ?? node,
            _ => node,
        };
    }

    // A companion INSTANCE load on a CLR-bound owner (`String.Companion` as a value — e.g. the receiver arg of a
    // companion-extension call like `String.format(...)`): the pure-Kotlin type the ref build emits carries the
    // companion INSTANCE field, but the substituted BCL type (System.String) has none — the substitution erases the
    // companion's runtime representation. kotc flattens a plain companion, so the companion-extension `__self`
    // param is a plain `object` whose value is never used: lower the load to a null object const.
    static JsonNode TransformStaticField(JsonObject node, ReferenceMetadataIndex refs)
    {
        if ((node["name"] as JsonValue)?.GetValue<string>() != "INSTANCE") return null;
        var owner = TypeJson.OwnerName(node["ownerType"]);
        if (string.IsNullOrEmpty(owner) || !refs.TryResolveClrOwner(owner, out _, out _)) return null;
        return new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("object"), ["value"] = null };
    }

    // `new T(..)` on a CLR-bound REFERENCE owner -> clrNew. A value-type (struct) owner is left untouched: a value
    // primitive keeps its identity (the inline-value-class / unsigned representation is a primitive concern handled
    // by type lowering + kotc, not a member-call substitution).
    static JsonNode TransformNew(JsonObject node, ReferenceMetadataIndex refs)
    {
        if (TypeJson.Read(node["type"]) is not TypeNode.Fqn ownerFqn) return null;
        if (!refs.TryResolveClrOwner(ownerFqn.Name, out var bcl, out var kind)) return null;

        // Inline-class CONSTRUCTION erasure (the BOX, mirror of the `.data` unbox collapse): an @JvmInline value class
        // erases to its single backing field's primitive CLR form, so `new UByte(arg)` IS `arg` (no System.Byte(byte)
        // ctor exists). Collapse to the lone arg UNCHANGED — never a conv: the int32 stack bits are already the value,
        // and a signed conv (Conv_I1) would sign-extend and corrupt an unsigned high bit (UByte 200 -> -56). Width is
        // truncated/masked at the byte-typed store/use sites. (Codex-confirmed: identity, not conv.)
        if (refs.IsInlineValueClass(ownerFqn.Name) &&
            node["args"] is JsonArray ctorArgs && ctorArgs.Count == 1)
            return ctorArgs[0].DeepClone();

        if (kind is "struct" or "enum") return null;

        // A GENERIC @ClrTypeAlias owner (`new HashSet<E>()`) must carry its element args so ilemit reconstructs the
        // instantiation: the structured `Fqn(bcl, sourceArgs)` (the SAME generic-alias form BirTypeLowering produces
        // for type positions — the clrNew `type` is a TypeKey, so the subsequent type-lowering pass lowers the args). A
        // non-generic owner is the bare BCL Fqn.
        var typeNode = ownerFqn.Args != null ? new TypeNode.Fqn(bcl, ownerFqn.Args) : new TypeNode.Fqn(bcl);

        var args = node["args"] as JsonArray ?? new JsonArray();

        // JVM (initialCapacity: Int, loadFactor: Float) collection ctor -> the capacity-only (int) BCL ctor. .NET's
        // HashSet/Dictionary have NO (int, float) constructor (loadFactor is a JVM hashtable concept), so a
        // `HashSet<Int>(16, 0.75f)` call would mis-resolve to the `(IEnumerable, IEqualityComparer)` overload and throw
        // at run. Drop the trailing loadFactor arg (and its declared argType) so the overload key becomes a bare (int).
        // Gated on a @ClrTypeAlias owner whose declared 2nd ctor param is a Float — the loadFactor idiom is unique to
        // the stdlib collection aliases (no BCL type reaching here has a genuine (int, float) ctor).
        if (args.Count == 2 && refs.Aliases.ContainsKey(ownerFqn.Name)
            && node["argTypes"] is JsonArray dat && dat.Count == 2 && IsFloatArg(dat[1]))
        {
            args = new JsonArray { args[0].DeepClone() };
            node["argTypes"] = new JsonArray { dat[0].DeepClone() };
        }

        return new JsonObject
        {
            ["k"] = "clrNew",
            ["type"] = TypeJson.Write(typeNode),
            ["argTypes"] = CtorArgTypes(node, args, refs, ownerFqn.Name),
            ["args"] = args.DeepClone(),
        };
    }

    // The clrNew's ctor-overload key. kotc emits the ctor's DECLARED param types on the `new` node's `argTypes`, but they
    // reference the class's OWN type parameters (`ArrayList<E>`'s copy ctor -> `Collection[gp:E]`). Substitute those with
    // the instantiation's type args (`ArrayList[kotlin.Int]` => E:=kotlin.Int) so the lowered argType is a RESOLVABLE,
    // precise overload key (`IReadOnlyCollection[int]`) — this disambiguates List's `IEnumerable<T>` ctor from its `int`
    // capacity ctor (a bare `object`/unbound-`gp:E` argType matches neither, so ilemit mis-picked `List(int)` ->
    // InvalidProgramException). Falls back to InferArgTypes when the node has no declared argTypes (older shape).
    // The 2nd ctor arg is a Float (the JVM loadFactor idiom) — read the structured argType (with a legacy-string fallback).
    static bool IsFloatArg(JsonNode n)
    {
        if (TypeJson.Read(n) is TypeNode.Fqn { Args: null } f) return f.Name is "kotlin.Float" or "float";
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return s is "kotlin.Float" or "float";
        return false;
    }

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
        var ownerFqnNode = TypeJson.Read(node[instance ? "ownerType" : "owner"]) as TypeNode.Fqn;
        var ownerToken = ownerFqnNode?.Name;
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
            // UNAMBIGUOUS names (isNaN, clrTimestamp) — never for a name whose overloads split across Math/MathF,
            // and never for a name that ALSO has a real-bodied (non-intrinsic) top-level overload: `sort`'s 8
            // primitive-array intrinsics all agree on "System.Array.Sort" (not "ambiguous"), yet the name fallback
            // captured the real-bodied `MutableList<T>.sort()` call inside the compiled `sorted()` body.
            var sigKey0 = string.Join(",", sigParts0.Select(ReferenceMetadataIndex.ParamKey));
            if ((refs.TryTopLevelIntrinsicBySig(fn, sigKey0, out var fq)
                    || (!refs.IsAmbiguousTopLevelIntrinsic(fn) && !refs.HasNonIntrinsicTopLevel(fn)
                        && refs.TryTopLevelIntrinsic(fn, out fq)))
                && fq.LastIndexOf('.') is var dot && dot > 0)
                return ClrCallNode(node, new TypeNode.Fqn(fq[..dot]), fq[(dot + 1)..], fq[(dot + 1)..], args0, instance: false, refs.TopLevelByrefPositions(fn));
            // bare-intrinsic extension: resolve by name + the first-arg's receiver key + full param count (disambiguates
            // `set`, and keeps `substring(String,Int)`@ClrIntrinsic from capturing the 3-arg `substring(String,Int,Int)`).
            if (sigParts0.Count >= 1 && refs.TryExtMemberIntrinsic(fn, RecvKeyOf(sigParts0[0]), sigParts0.Count, out var extMember))
                return TopLevelExtensionInstance(node, refs, extMember, args0, sigParts0, ctx);
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
                    node["owner"] = TypeJson.Fqn(fileClassOwner);   // owner is a birType-emitted (structured Fqn) slot
                    return node;
                }
            }
            return null;
        }

        // Rule 2p-inherited (property-accessor override chain): a `.message`/`.cause` read dispatches through a subclass
        // receiver whose STATIC owner is either a USER class (`AppErr : Exception`) — not CLR-bound at all — or a
        // non-redeclaring @ClrTypeAlias subclass (`kotlin.Exception` inherits `message` from `kotlin.Throwable`) — so
        // neither carries the @ClrProperty binding on its OWN members. The binding lives on the CLR-bound ANCESTOR that
        // DECLARES the property (`kotlin.Throwable.message` -> @ClrProperty "Message"). Walk the `overrides` marker (kotc
        // stamps it on every accessor call) to that ancestor and route the read to clrPropGet/clrPropSet on ITS BCL
        // owner. Mirrors Rule 3-inherited (printStackTrace). The DIRECT-owner @ClrProperty (a self-declared member such
        // as StringBuilder.capacity()) takes priority — handled by Rule 2p below — so this fires only when the direct
        // owner has no binding of its own. Runs BEFORE the CLR-owner gate so a NON-CLR-bound direct owner still resolves.
        {
            var pmember = (node["method"] as JsonValue)?.GetValue<string>();
            var pargs = node["args"] as JsonArray ?? new JsonArray();
            var directHasProp = instance && !string.IsNullOrEmpty(pmember)
                && refs.TryResolveClrOwner(ownerToken, out _, out _)
                && refs.TryMemberProperty(ReferenceMetadataIndex.BareOwnerFqn(ownerToken), pmember, pargs.Count, out _, out _);
            if (instance && !directHasProp && !string.IsNullOrEmpty(pmember) && node["overrides"] is JsonArray povChain)
                foreach (var o in povChain)
                    if (o is JsonObject oo && TypeJson.OwnerName(oo["owner"]) is string ovOwner
                        && refs.TryResolveClrOwner(ovOwner, out var ovBcl, out _)
                        && refs.TryMemberProperty(ovOwner, pmember, pargs.Count, out var povAccess, out var povName))
                        return ClrPropNode(node, ClrOwnerType(refs, new TypeNode.Fqn(ovOwner)) ?? new TypeNode.Fqn(ovBcl), povName, povAccess, pmember, pargs);
        }

        // A Kotlin-collection `iterator()` on an EMITTED (non-@ClrTypeAlias) collection type — a `kotlin.collections.
        // AbstractMutable*` self-call: its abstract iterator() slot vanished when its collection supertype substituted
        // to the BCL IEnumerable face, so `this.iterator()` finds no slot. Route it to the ClrIteratorBridge over the
        // receiver (the exact target the @ClrTypeAlias-interface path — Rule 5 — uses; here the owner is a CLASS not in
        // the alias table, so that rule never reaches it). Element type = the owner's first type-arg.
        if (instance && ownerToken.StartsWith("kotlin.collections.", StringComparison.Ordinal)
            && (node["method"] as JsonValue)?.GetValue<string>() == "iterator"
            && node["args"] is JsonArray itArgs && itArgs.Count == 0
            && ownerFqnNode != null && !refs.TryResolveClrOwner(ownerToken, out _, out _))
            return CollDefaultCall(node, "kotlin.collections.ClrIteratorBridgeKt", "iteratorOverEnumerable",
                OwnerElemArg(ownerFqnNode), itArgs);

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
            return new JsonObject { ["k"] = "conv", ["to"] = TypeJson.Fqn(inlineConv), ["e"] = node["recv"]?.DeepClone() };

        // The CLR owner TYPE the call addresses (a ClrRef-resolvable BCL token; see ClrOwnerType).
        TypeNode clrOwner = ClrOwnerType(refs, ownerFqnNode) ?? new TypeNode.Fqn(bcl);

        // Rule 2p (explicit PROPERTY accessor): the member carries @ClrProperty(access, name) -> route EXPLICITLY to
        // clrPropGet(name) [READ] / clrPropSet(name) [WRITE] on the BCL owner, from the stated access role — NOT the old
        // get_/set_ intrinsic-string-prefix sniff. Handled before Rule 2/3 so a @ClrProperty stub (setLength/capacity/
        // ticks) is neither routed as a plain method nor hoisted as a rule-3 body.
        if (instance && refs.TryMemberProperty(ownerFqn, member, args.Count, out var pAccess, out var pName))
            return ClrPropNode(node, clrOwner, pName, pAccess, member, args);

        // PRE-Rule-2 semantic override: MutableCollection.add is @ClrIntrinsic("Add") (the binding drives the
        // implementor-side DeclarationRename), but the CALL semantics diverge — Kotlin `add` returns the
        // changed-Boolean while `ICollection<T>.Add` is VOID (a brIf on the phantom result was a stack underflow),
        // and 1-arg `addAll` has no ICollection slot at all. Route these calls to the ClrCollectionDefaults
        // helpers BEFORE the intrinsic rule; the 2-arg add(index, e)/addAll(index, c) Insert forms fall through.
        if (instance && kind == "interface" && ownerFqn.StartsWith("kotlin.collections.", StringComparison.Ordinal)
            && args.Count == 1 && member is "add" or "Add" or "addAll")
            return CollDefaultCall(node, "kotlin.collections.ClrCollectionDefaultsKt",
                member == "addAll" ? "clrCollAddAll" : "clrCollAdd", CollElemArg(node, refs, ctx, ownerFqnNode), args);

        // PRE-Rule-2 semantic override: MutableList.set(i,e) / removeAt(i) @ClrIntrinsic(set_Item/RemoveAt), but the
        // BCL slots are VOID while Kotlin RETURNS the previous/removed element — binding the intrinsic directly
        // underflows the stack when the result is consumed (`val old = list.set(i,e)` -> InvalidProgramException).
        // Route to the ClrCollectionDefaults wrappers (clrListSet/clrListRemoveAt) that read the old element, perform
        // the void mutation, and return it. `retType` carries the concrete element type for the boxing/convert at the
        // call site (the helper's own `!!0` is out of scope). The void-returning 2-arg add(i,e) Insert form is left
        // on the intrinsic path.
        if (instance && kind == "interface" && ownerFqn == "kotlin.collections.MutableList"
            && (((member is "set" or "set_Item") && args.Count == 2) || ((member is "removeAt" or "RemoveAt") && args.Count == 1)))
        {
            var listHelper = member is "set" or "set_Item" ? "clrListSet" : "clrListRemoveAt";
            var listCall = (JsonObject)CollDefaultCall(node, "kotlin.collections.ClrCollectionDefaultsKt", listHelper, OwnerElemArg(ownerFqnNode), args);
            if (RetToken(node) is JsonNode lret && !IsTvType(lret)) listCall["retType"] = lret;
            return listCall;
        }

        // Rule 1c (PRIMITIVE compareTo): `x.compareTo(y)` on a boxed kotlin.<Prim> -> `System.<Prim>.CompareTo`
        // (IComparable<T>). The boxed kotlin.* primitive is NOT emitted in the runtime (it is substituted to the BCL
        // value type), so a member call on the omitted class must route to the BCL value type's CompareTo. This is the
        // bir2cir home of the former kotc primitive-compareTo lowering (layer purity): kotc emits the plain
        // `callInstance kotlin.Int.compareTo`; the primitive->BCL knowledge lives here. Placed BEFORE Rule 3 because a
        // primitive that carries a rule-3 body (Char) would otherwise route to its `<>dotkt_ClrH_kotlin_Char` helper —
        // WRONG (and self-recursive inside that helper's own body). The 8 signed/bool/char primitives only.
        if (instance && member == "compareTo" && args.Count == 1 && PrimitiveCompareToBcl(ownerFqn) is string primBcl)
            return new JsonObject
            {
                ["k"] = "clrInstance", ["type"] = TypeJson.Fqn(primBcl), ["method"] = "CompareTo",
                ["argTypes"] = new JsonArray { TypeJson.Fqn(primBcl) }, ["ret"] = TypeJson.Fqn("System.Int32"),
                ["recv"] = node["recv"]?.DeepClone(), ["args"] = args.DeepClone(),
            };

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
            return Rule3HelperCall(node, refs, ownerFqnNode, member, args, instance);

        // Rule 3-inherited: the concrete rule-3 body lives on an ANCESTOR, not the static call owner. `printStackTrace`
        // has its real body on kotlin.Throwable but is called through a kotlin.Exception/RuntimeException subclass
        // receiver — IsRule3Member keys on the static owner (Exception) and misses it, so the call would fall through to
        // Rule 4 as a bogus `System.Exception.printStackTrace` (NRE). Walk the `overrides` marker to the CLR-bound
        // non-interface ancestor that actually declares the concrete intrinsic-less body and route to ITS helper; the
        // subclass receiver is assignable to the ancestor-typed __self. Only when the direct owner had no rule-3 match.
        if (kind != "interface" && instance && node["overrides"] is JsonArray ovChain)
            foreach (var o in ovChain)
                if (o is JsonObject oo
                    && TypeJson.OwnerName(oo["owner"]) is string ovOwner
                    && (oo["member"] as JsonValue)?.GetValue<string>() is string ovMember
                    && refs.TryResolveClrOwner(ovOwner, out _, out var ovKind) && ovKind != "interface"
                    && refs.IsRule3Member(ovOwner, ovMember))
                    return Rule3HelperCall(node, refs, new TypeNode.Fqn(ovOwner), ovMember, args, instance);

        // Rule 5m (MAP-interface defaults): Map/MutableMap both alias IDictionary<K,V> (see the stdlib rationale), but
        // most Kotlin map members have no 1:1 IDictionary equivalent — `get` is null-on-missing while get_Item THROWS,
        // put/remove return the previous value, and the keys/values/entries views are Kotlin-typed. Route them to the
        // rt's ClrMapDefaults statics, generic over BOTH type args (the 2-type-arg mirror of CollDefaultCall). Members
        // that DO bind 1:1 (@ClrIntrinsic size/containsKey/clear + MutableMap keys/values) were already renamed to
        // their BCL slot by DeclarationRename and fall through to Rule 4; the defensive get_keys/get_values entries
        // below catch an un-renamed MutableMap accessor call (no overrides metadata) as a direct property read.
        if (instance && kind == "interface" &&
            (ownerFqn == "kotlin.collections.Map" || ownerFqn == "kotlin.collections.MutableMap"))
        {
            var mutable = ownerFqn == "kotlin.collections.MutableMap";
            var helper = (member, args.Count, mutable) switch
            {
                ("get", 1, _) => "clrMapGet",
                // size / containsKey are UNBOUND (no @ClrIntrinsic) — a direct Count/ContainsKey reads through the
                // INVARIANT generic IDictionary<K,V> and throws EntryPointNotFound on a value-type-mismatched map (a
                // groupBy result). Route to the covariance-safe non-generic helpers (ICollection.Count / IDictionary
                // .Contains). This also makes mapValues' transitive `mapCapacity(this.size)` covariance-safe.
                ("get_size", 0, _) => "clrMapSize",
                ("containsKey", 1, _) => "clrMapContainsKey",
                ("isEmpty", 0, _) => "clrMapIsEmpty",
                ("containsValue", 1, _) => "clrMapContainsValue",
                ("getOrDefault", 2, _) => "clrMapGetOrDefault",
                ("get_keys", 0, false) => "clrMapKeys",
                ("get_values", 0, false) => "clrMapValues",
                ("get_entries", 0, false) => "clrMapEntries",
                ("get_entries", 0, true) => "clrMapMutableEntries",
                ("put", 2, true) => "clrMapPut",
                ("remove", 1, true) => "clrMapRemove",
                ("remove", 2, true) => "clrMapRemoveKV",
                ("putAll", 1, true) => "clrMapPutAll",
                ("putIfAbsent", 2, true) => "clrMapPutIfAbsent",
                ("replace", 2, true) => "clrMapReplace",
                ("replace", 3, true) => "clrMapReplaceKVV",
                ("merge", 3, true) => "clrMapMerge",
                _ => null,
            };
            if (helper != null)
                return MapDefaultCall(node, helper, ownerFqnNode, args, refs, ctx);
            if (mutable && args.Count == 0 && member is "get_keys" or "get_values")
                return ClrPropNode(node, clrOwner, member == "get_keys" ? "Keys" : "Values", ClrPropRead, member, args);
            // else fall through to Rule 4: an already-BCL member name on the aliased IDictionary owner.
        }

        // Rule 5 (collection-interface defaults): the substituted BCL IReadOnly*/I* interfaces lack isEmpty/contains/
        // containsAll/indexOf/lastIndexOf/subList/listIterator/iterator, so an @ClrTypeAlias collection-interface call
        // routes to the rt's ClrCollectionDefaults / ClrIteratorBridge helpers — the SAME targets kotc's collDefault
        // path uses (its `clrName(declaringClass) != null` gate is now null for the @ClrTypeAlias collection interfaces,
        // so it no longer fires; this is the bir2cir home of that Kotlin<->CLR relation). The element type is the
        // owner token's first type arg; the helper is generic over it.
        else if (instance && kind == "interface" && ownerFqn.StartsWith("kotlin.collections.", StringComparison.Ordinal))
        {
            var elem = OwnerElemArg(ownerFqnNode);
            if (member == "iterator" && args.Count == 0)
                return CollDefaultCall(node, "kotlin.collections.ClrIteratorBridgeKt", "iteratorOverEnumerable", elem, args);
            if (member == "listIterator")
            {
                var idx = args.Count >= 1 ? args : new JsonArray { new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("int"), ["value"] = 0 } };
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
        //
        // MAKE-IT-LOUD gate (H1): the "falls to dynamic dispatch" escape is ONLY legitimate for an INTERFACE owner —
        // the intended `MutableCollection.addAll/removeAll/retainAll` on `ICollection<T>`, where the runtime value
        // implements the interface under a concrete type so reflection finds the slot. A lowercase-camelCase member on a
        // CLR-bound NON-interface owner (a concrete BCL class) is an UNBOUND Kotlin member with no BCL equivalent by that
        // name AND no @ClrIntrinsic/@ClrProperty/rule-3 binding: it is a genuine routing MISS. Left unrefused it would
        // emit a clrInstance that ilemit can neither resolve statically nor (post-gate) dispatch dynamically → an opaque
        // runtime NRE. Refuse it here, at compile time, naming `owner.member`. Allow only a BCL-shaped name (PascalCase
        // or a get_/set_ accessor) or an interface owner (the legit dynamic-dispatch case). Instance-only: a static
        // lowercase miss already throws loudly at ilemit (no dynamic-dispatch path is instance-gated there).
        if (instance && kind != "interface" && !string.IsNullOrEmpty(member)
            && !char.IsUpper(member[0])
            && !member.StartsWith("get_", StringComparison.Ordinal)
            && !member.StartsWith("set_", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"bir2cir: unresolved CLR member '{ownerFqn}.{member}' — a lowercase-camelCase member on the CLR-bound "
                + $"{kind} owner '{ownerToken}' has no @ClrIntrinsic/@ClrProperty/rule-3 binding and is not a BCL member "
                + "name (BCL members are PascalCase). This is a routing MISS: fix the stdlib binding or the owner alias, "
                + "do not let it fall to a silent runtime dynamic-dispatch NRE.");
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
        if (node["recv"] is not JsonObject recv) return built;
        // The receiver's STATIC type. A local/param -> VarTypes. For a CompareTo call ONLY (the constrained-compareTo
        // case), also recover it from a callInstance receiver's declared return (`retType`/`ret`) or an arrayGet's
        // `elem`: a `gp:X` receiver reached via a member call (`ClosedRange.start.compareTo`) or an array read
        // (`a[i].compareTo`) still needs `constrained.` for value-type-safe dispatch. The collection.add path stays
        // LOCAL-only (unchanged) so broadening the receiver shapes cannot re-route a non-compareTo interface call.
        var isCompareTo = (call["method"] as JsonValue)?.GetValue<string>() == "CompareTo";
        var vt = RecvStaticType(recv, ctx, isCompareTo);
        if (vt is not TypeNode.Tv tvRecv) return built;   // only a generic-parameter receiver needs constrained dispatch
        // The call's declaring owner must itself be a CLR-bound INTERFACE (concrete-class members dispatch fine already).
        if (!refs.TryResolveClrOwner(ownerToken, out var ownerBcl, out var ownerKind) || ownerKind != "interface")
            return built;
        TypeNode[] cargs;
        if (isCompareTo)
        {
            // COMPARETO (System.IComparable): `T : Comparable<T>` means the interface is `IComparable<recvType>` — the
            // arg IS the receiver's own static type.
            cargs = new TypeNode[] { vt };
        }
        else
        {
            // Collection-BUILD (mapTo/filterTo `destination.add`): the element args come from the receiver
            // type-parameter's own collection-interface constraint (`MutableCollection<R>` -> [R]). Requires the
            // constraint to be present on THIS declaration (a local/param receiver of a generic method); local-only.
            if (!ctx.TpConstraints.TryGetValue(tvRecv.Scope + ":" + tvRecv.I, out var cons)) return built;
            cargs = null;
            foreach (var c in cons)
                if (c is TypeNode.Fqn cf && cf.Args != null && refs.TryResolveClrOwner(cf.Name, out _, out var ck)
                    && ck == "interface") { cargs = cf.Args; break; }
            if (cargs == null) return built;
        }

        var cc = new JsonObject
        {
            ["k"] = "constrainedCall",
            ["recvType"] = TypeJson.Write(vt),
            ["iface"] = TypeJson.Write(new TypeNode.Fqn(ownerBcl, cargs)),
            ["method"] = (call["method"] as JsonValue)?.GetValue<string>(),
            ["recv"] = call["recv"]?.DeepClone(),
            ["args"] = (call["args"] as JsonArray)?.DeepClone() ?? new JsonArray(),
        };
        if (call["argTypes"] is JsonArray at) cc["argTypes"] = at.DeepClone();
        if (call["ret"] is JsonNode rv) cc["ret"] = rv.DeepClone();
        return cc;
    }

    // The receiver expression's static type token, for constrained-dispatch recovery. A local/param resolves via
    // VarTypes; for the constrained-COMPARETO case a callInstance receiver's declared return (`retType`/`ret`) and an
    // arrayGet's element (`elem`) also carry it (`ClosedRange.get_start(): T` -> compareTo; `a[i]: T` -> compareTo).
    // null when the shape carries no recoverable static type.
    static TypeNode RecvStaticType(JsonObject recv, SubstCtx ctx, bool allowExprShapes)
    {
        var rk = (recv["k"] as JsonValue)?.GetValue<string>();
        if (rk == "local")
            return (recv["name"] as JsonValue)?.GetValue<string>() is string vn
                && ctx.VarTypes.TryGetValue(vn, out var vt) ? vt : null;
        if (!allowExprShapes) return null;
        if (rk == "callInstance")
            return TypeJson.Read(recv["retType"]) ?? TypeJson.Read(recv["ret"]);
        if (rk == "arrayGet")
            return TypeJson.Read(recv["elem"]);
        return null;
    }

    // The BCL value type whose `CompareTo` a boxed kotlin.<Prim> primitive's `compareTo` routes to (mirrors the former
    // kotc primitive-compareTo lowering). null for a non-primitive owner.
    static string PrimitiveCompareToBcl(string ownerFqn) => ownerFqn switch
    {
        "kotlin.Int" => "System.Int32",
        "kotlin.Long" => "System.Int64",
        "kotlin.Byte" => "System.SByte",
        "kotlin.Short" => "System.Int16",
        "kotlin.Float" => "System.Single",
        "kotlin.Double" => "System.Double",
        "kotlin.Char" => "System.Char",
        "kotlin.Boolean" => "System.Boolean",
        _ => null,
    };

    // The collection ELEMENT type arg for a defaults-helper call: the owner token's own arg
    // (`MutableCollection[gp:R]` -> gp:R), or — when the owner is BARE because the receiver is a generic
    // parameter (`destination: C where C : MutableCollection<R>`) — the receiver's collection-interface
    // constraint's arg (the same recovery Constrainify performs). Falls back to `object`.
    static readonly TypeNode ObjType = new TypeNode.Fqn("object");
    static bool IsObjType(TypeNode t) => t is TypeNode.Fqn { Args: null } f && (f.Name == "object" || f.Name == "kotlin.Any");

    static TypeNode CollElemArg(JsonObject node, ReferenceMetadataIndex refs, SubstCtx ctx, TypeNode.Fqn ownerFqn)
    {
        var own = OwnerElemArg(ownerFqn);
        if (!IsObjType(own) || ownerFqn.Args != null) return own;
        // The owner is BARE (`kotlin.collections.MutableCollection`, no type args): the frontend dropped the element
        // when inlining `mapTo`/`filterTo`'s `destination.add(...)`. Recover it from the RECEIVER's declared type.
        if (ctx != null && node["recv"] is JsonObject recv && (recv["k"] as JsonValue)?.GetValue<string>() == "local"
            && (recv["name"] as JsonValue)?.GetValue<string>() is string vn
            && ctx.VarTypes.TryGetValue(vn, out var vt))
        {
            // (a) The receiver is a type-PARAMETER (`destination: C where C : MutableCollection<R>`): its element comes
            // from the collection-interface constraint's arg (the same recovery Constrainify performs).
            if (vt is TypeNode.Tv tvR)
            {
                if (ctx.TpConstraints.TryGetValue(tvR.Scope + ":" + tvR.I, out var cons))
                    foreach (var c in cons)
                        if (c is TypeNode.Fqn cf && cf.Args is { Length: >= 1 } && refs.TryResolveClrOwner(cf.Name, out _, out var ck)
                            && ck == "interface" && cf.Args[0] != null) return cf.Args[0];
            }
            // (b) The receiver is a CONCRETE generic collection local (`__inlN : ArrayList<String>`, mapTo's
            // materialized destination): its OWN first type-arg is the element. Without this the helper's typeArg stays
            // the frontend's `object` over-approximation. Mirrors MapKvArgs' bare-owner recovery.
            else if (vt is TypeNode.Fqn)
            {
                var elem = OwnerElemArg((TypeNode.Fqn)vt);
                if (!IsObjType(elem)) return elem;
            }
        }
        return ObjType;
    }

    // The (K, V) type args for a map-defaults helper call — the two-arg twin of CollElemArg. The owner token's own args
    // (`Map[gp:K,gp:V]`) when present and concrete; otherwise — when the owner is BARE or an OVER-APPROXIMATED position
    // (`MutableMap` bare / `MutableMap[kotlin.Any,V]`, because the receiver is a `gp:M` whose `in K` projection erased the
    // key to Any) — the receiver type-parameter's INVARIANT map-interface constraint (`M : MutableMap[gp:K,gp:V]`). This
    // undoes the variance approximation so `associateWith`/`associateBy`'s `destination.put(..)` emits clrMapPut<K,V>, not
    // <object,object> whose `IDictionary<object,..>::ContainsKey` finds no slot on the runtime dict -> EntryPointNotFound.
    static (TypeNode, TypeNode) MapKvArgs(JsonObject node, ReferenceMetadataIndex refs, SubstCtx ctx, TypeNode.Fqn ownerFqn)
    {
        var (k, v) = OwnerKvArgs(ownerFqn);
        if (!IsObjType(k) && !IsObjType(v)) return (k, v);
        if (ctx != null && refs != null && node["recv"] is JsonObject recv && (recv["k"] as JsonValue)?.GetValue<string>() == "local"
            && (recv["name"] as JsonValue)?.GetValue<string>() is string vn && ctx.VarTypes.TryGetValue(vn, out var vt) && vt is TypeNode.Tv tvR
            && ctx.TpConstraints.TryGetValue(tvR.Scope + ":" + tvR.I, out var cons))
        {
            foreach (var c in cons)
            {
                if (c is not TypeNode.Fqn cf || cf.Args is not { Length: >= 2 } || !refs.TryResolveClrOwner(cf.Name, out _, out var ck) || ck != "interface") continue;
                // Only OVERRIDE an over-approximated (object/kotlin.Any) position; a genuinely-concrete owner arg wins.
                if (IsObjType(k) && cf.Args[0] != null) k = cf.Args[0];
                if (IsObjType(v) && cf.Args[1] != null) v = cf.Args[1];
                break;
            }
        }
        return (k, v);
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
    static JsonNode CollDefaultCall(JsonObject node, string helperOwner, string helperMethod, TypeNode elem, JsonArray args)
    {
        var hargs = new JsonArray();
        if (node["recv"] != null) hargs.Add(node["recv"].DeepClone());
        foreach (var a in args) hargs.Add(a?.DeepClone());
        return new JsonObject
        {
            ["k"] = "callStatic",
            ["owner"] = TypeJson.Fqn(helperOwner),
            ["method"] = helperMethod,
            ["args"] = hargs,
            ["typeArgs"] = new JsonArray { TypeJson.Write(elem) },
        };
    }

    // The 2-type-arg map mirror of CollDefaultCall: `callStatic ClrMapDefaultsKt.<helper>(recv, args...)` typed over
    // the map owner token's [K,V] instantiation args.
    static JsonNode MapDefaultCall(JsonObject node, string helperMethod, TypeNode.Fqn ownerFqn, JsonArray args, ReferenceMetadataIndex refs, SubstCtx ctx)
    {
        var hargs = new JsonArray();
        if (node["recv"] != null) hargs.Add(node["recv"].DeepClone());
        foreach (var a in args) hargs.Add(a?.DeepClone());
        var (k, v) = MapKvArgs(node, refs, ctx, ownerFqn);
        var call = new JsonObject
        {
            ["k"] = "callStatic",
            ["owner"] = TypeJson.Fqn("kotlin.collections.ClrMapDefaultsKt"),
            ["method"] = helperMethod,
            ["args"] = hargs,
            ["typeArgs"] = new JsonArray { TypeJson.Write(k), TypeJson.Write(v) },
        };
        // Carry the call's statically-known return (same rationale + `gp:` guard as Rule3HelperCall): a helper
        // returning the BARE map value param (`getOrDefault` -> V) reflects as the callee's own `!!1` at the call
        // site — boxing that out-of-scope token is invalid metadata -> BadImageFormatException at run (both the
        // Map- and MutableMap-typed receivers). `retType` lets ilemit box/convert the concrete instantiation.
        if (RetToken(node) is JsonNode ret && !IsTvType(ret)) call["retType"] = ret;
        return call;
    }

    // The first TWO top-level type arguments of a map owner token (`kotlin.collections.Map[gp:K,gp:V]`); `object` when
    // erased/unbound.
    static (TypeNode, TypeNode) OwnerKvArgs(TypeNode.Fqn ownerFqn)
    {
        var args = ownerFqn.Args;
        return (args is { Length: >= 1 } && args[0] != null ? args[0] : ObjType,
                args is { Length: >= 2 } && args[1] != null ? args[1] : ObjType);
    }

    // The first top-level type argument of an owner Fqn (`kotlin.collections.List<E>` -> E); `object` if none.
    static TypeNode OwnerElemArg(TypeNode.Fqn ownerFqn) =>
        ownerFqn.Args is { Length: >= 1 } args && args[0] != null ? args[0] : ObjType;

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
    static TypeNode ClrOwnerType(ReferenceMetadataIndex refs, TypeNode.Fqn ownerFqn)
    {
        if (!refs.TryResolveClrOwner(ownerFqn.Name, out var bcl, out _)) return null;
        var arity = refs.OwnerArity(ownerFqn.Name);
        if (ownerFqn.Args != null || arity > 0)
        {
            // Pad a PARTIALLY-erased arg list to the alias's declared arity (a star-projection `Map<K, *>` reaches here
            // as `kotlin.collections.Map<K>` — 1 of IDictionary's 2 args; ilemit's GenericType would fail to resolve
            // `IDictionary`1`). The trailing/all erased args become `object`.
            var kept = (ownerFqn.Args ?? Array.Empty<TypeNode>()).Where(a => a != null).ToList();
            for (var i = kept.Count; i < arity; i++) kept.Add(ObjType);
            if (kept.Count > 0) return new TypeNode.Fqn(bcl, kept.ToArray());
        }
        return new TypeNode.Fqn(bcl);
    }

    static JsonNode TopLevelExtensionInstance(JsonObject node, ReferenceMetadataIndex refs, string intrinsic, JsonArray args, List<string> sigParts, SubstCtx ctx)
    {
        if (args.Count == 0) return null;   // no receiver -> not an extension shape; leave for FindStatic to report
        // The extension receiver's CLR owner type. PREFER the receiver EXPRESSION's STRUCTURED static type (from ctx):
        // a param/local typed `MutableCollection<T>` carries the CONCRETE tv element arg (`[tv method 0]`). The legacy
        // sig0 string's `BareOwnerFqn` STRIPS the receiver's type-args, so a generic-collection receiver would resolve
        // to the INVARIANT `ICollection<object>` and mis-dispatch at run (`ICollection<object>::Add` on a runtime
        // `List<string>` -> EntryPointNotFoundException — the stdlib `clrCollNativeAdd`@ClrIntrinsic("Add") crash). The
        // structured receiver keeps `ICollection<gp:T>`. Fall back to the sig0 bare owner when no structured Fqn is
        // recoverable; the receiver `type` slot must be the ClrRef-resolvable BCL Fqn, not the "string" shorthand.
        var sig0 = sigParts.Count > 0 ? sigParts[0] : null;
        TypeNode recvClr = null;
        if (ctx != null && args[0] is JsonObject recv0 && RecvStaticType(recv0, ctx, allowExprShapes: false) is TypeNode.Fqn structRecv
            && ClrOwnerType(refs, structRecv) is TypeNode roStruct)
            recvClr = roStruct;
        else if (sig0 != null && ClrOwnerType(refs, new TypeNode.Fqn(ReferenceMetadataIndex.BareOwnerFqn(sig0))) is TypeNode roBare)
            recvClr = roBare;
        JsonNode recvType = recvClr != null
            ? TypeJson.Write(recvClr)
            : (sig0 != null ? JsonValue.Create(sig0) : InferArgType(args[0]));

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
        if (RetToken(node) is JsonNode ret) call["ret"] = ret;
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
    static JsonNode ClrPropNode(JsonObject node, TypeNode clrOwner, string prop, int access, string member, JsonArray args)
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
            ["type"] = TypeJson.Write(clrOwner),
            ["name"] = prop,
            ["static"] = false,
            ["recv"] = node["recv"]?.DeepClone(),
        };
        if (!write && RetToken(node) is JsonNode ret) pg["retType"] = ret;
        if (write && args.Count >= 1) pg["value"] = args[0].DeepClone();
        return pg;
    }

    // A clrInstance / clrStatic node. A property-accessor call whose MEMBER carries the `get_`/`set_` prefix (kotc's
    // property convention: a `val length` -> the accessor call `get_length`, intrinsic bare "Length") emits clrPropGet/
    // clrPropSet on the bare intrinsic; otherwise a plain method call. A standalone accessor FUN bound to a property is
    // routed EXPLICITLY by @ClrProperty (Rule 2p) BEFORE this node is built, so there is no intrinsic-prefix sniff here.
    // Prefix `byref:` onto the argTypes at each @ClrRefArgument position (idempotent), so ilemit resolves the `ref`/`out`
    // BCL overload and emits the address-load for that arg (the byref shape a `ref`/`out` parameter needs).
    static void WrapByref(JsonArray argTypes, int[] byrefPositions)
    {
        if (byrefPositions == null) return;
        foreach (var i in byrefPositions)
        {
            if (i < 0 || i >= argTypes.Count) continue;
            // A structured arg type -> ByRef(inner); a legacy sig-string token -> "byref:"+s. Idempotent either way.
            if (TypeJson.Read(argTypes[i]) is TypeNode tn)
            {
                if (tn is not TypeNode.ByRef) argTypes[i] = TypeJson.Write(new TypeNode.ByRef(tn));
            }
            else if (argTypes[i] is JsonValue v && v.TryGetValue<string>(out var s) && !s.StartsWith("byref:", StringComparison.Ordinal))
                argTypes[i] = "byref:" + s;
        }
    }

    static JsonNode ClrCallNode(JsonObject node, TypeNode clrOwner, string intrinsic, string member, JsonArray args, bool instance, int[] byrefPositions = null)
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
            return ClrPropNode(node, clrOwner, intrinsic, isSet ? ClrPropWrite : ClrPropRead, member, args);

        var call = new JsonObject
        {
            ["k"] = instance ? "clrInstance" : "clrStatic",
            ["type"] = TypeJson.Write(clrOwner),
            ["method"] = intrinsic,
            ["argTypes"] = argTypes,
        };
        if (ret != null) call["ret"] = ret;
        if (instance) call["recv"] = node["recv"]?.DeepClone();
        call["args"] = args.DeepClone();
        // Thread the source call's generic type arguments onto the substituted clr call. A generic Kotlin
        // @ClrIntrinsic method (`fun <T> Array<T>.nativeFill(...)`) binds to a generic BCL method
        // (`System.Array.Fill<T>(T[],T,int,int)`); ilemit needs the type args to MakeGenericMethod the resolved
        // definition (else it emits an OPEN generic MethodSpec -> "method/type not fully instantiated" at run,
        // the windowed/RingBuffer.removeFirst -> _ArraysKt.fill -> Array.Fill NRE). ilemit instantiates ONLY when
        // the resolved BCL method is itself a generic DEFINITION, so threading these onto a call whose target is
        // non-generic (nativeClone -> Array.Clone) is a harmless no-op there.
        if (node["typeArgs"] is JsonArray callTypeArgs && callTypeArgs.Count > 0)
            call["typeArgs"] = callTypeArgs.DeepClone();
        CoerceCharSeqArgsToString(argTypes, call["args"] as JsonArray);
        return call;
    }

    // A synthetic-CharSequence (`<>dotkt_CharSequence`) value flowing as an ARGUMENT into a substituted BCL call has NO
    // BCL overload: `Appendable.append(CharSequence)` binds to `System.Text.StringBuilder.Append`, and ilemit — finding
    // no `Append(<>dotkt_CharSequence)` slot — mis-selects `Append(String)` and marshals the interface reference as a raw
    // string pointer, corrupting memory ("Destination is too short" / AccessViolationException inside joinTo/
    // joinToString). The CLR has no representation for kotc's monomorphic CharSequence interface at a BCL boundary, so any
    // CharSequence reaching one must be snapshot to System.String (its `.toString()` content). Convert the arg to a
    // null-safe `Any?.toString()` (kotlin.LibraryKt.toString) and pin the argType to `kotlin.String` (BirTypeLowering ->
    // System.String) so the overload binds cleanly. Runs in EVERY non-ref build: the rt-stdlib's OWN joinTo/joinToString
    // bodies keep the synthetic CharSequence params (CharSeqStringLowering is app-only), so this is the sole marshaling
    // point for their `buffer.append(separator/prefix/postfix/truncated)` calls.
    static void CoerceCharSeqArgsToString(JsonArray argTypes, JsonArray args)
    {
        if (argTypes == null || args == null) return;
        for (var i = 0; i < argTypes.Count && i < args.Count; i++)
            if (IsSyntheticCharSeqToken(argTypes[i]) && args[i] is JsonNode a)
            {
                args[i] = new JsonObject
                {
                    ["k"] = "callStatic", ["owner"] = TypeJson.Fqn("kotlin.LibraryKt"), ["method"] = "toString",
                    ["sig"] = "object", ["args"] = new JsonArray { a.DeepClone() },
                };
                argTypes[i] = JsonValue.Create("kotlin.String");
            }
    }

    // True iff an argType slot (a legacy sig STRING or a structured Fqn) denotes kotc's synthetic monomorphic
    // `<>dotkt_CharSequence` interface (tolerating a `nullable:`/`@` decoration). The `<>dotkt_StringCharSequence`
    // adapter deliberately does NOT match — its token has no `<>dotkt_CharSequence` substring.
    static bool IsSyntheticCharSeqToken(JsonNode slot)
    {
        var name = slot switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => (TypeJson.Read(slot) as TypeNode.Fqn)?.Name,
        };
        return name != null && name.Contains("<>dotkt_CharSequence", StringComparison.Ordinal);
    }

    // Rule-3: route to `<>dotkt_ClrH_<owner>.<member>(recv?, args..)`. The receiver is threaded as the helper's
    // first argument (the hoisted static's `__self`); type args are carried through when present.
    //
    // GENERIC alias owner: the hoisted helper declares the alias CLASS's type params FIRST, then the method's own
    // (HoistMethod -> MergeTypeParams order), so the call must instantiate the helper with the receiver's static-type
    // args (from the `ownerType` token, padded with `object` when erased) AHEAD of the method's own typeArgs.
    // Copying only node["typeArgs"] left the helper OPEN for a concrete generic receiver
    // (`HashMap<String,Int>().put(..)` -> an open-generic callStatic -> InvalidProgramException at run), and the bare
    // ownerFqn sig slot lowered to the degenerate NON-generic BCL type (`clr:System...Dictionary`) — carry the
    // instantiated token so the `__self` slot and the helper type args agree.
    static JsonNode Rule3HelperCall(JsonObject node, ReferenceMetadataIndex refs, TypeNode.Fqn ownerFqn, string member, JsonArray args, bool instance)
    {
        var ownerName = ownerFqn.Name;
        var hargs = new JsonArray();
        if (instance && node["recv"] != null) hargs.Add(node["recv"].DeepClone());
        foreach (var a in args) hargs.Add(a?.DeepClone());

        // The alias class's instantiation args, padded to its declared arity (a bare/partially-erased owner — a raw or
        // star-projected receiver — degrades to `object`, same as ClrOwnerType). Empty for a non-generic alias.
        var classArgs = (ownerFqn.Args ?? Array.Empty<TypeNode>()).Where(a => a != null).ToList();
        var arity = refs.OwnerArity(ownerName);
        for (var i = classArgs.Count; i < arity; i++) classArgs.Add(ObjType);

        var call = new JsonObject
        {
            ["k"] = "callStatic",
            ["owner"] = TypeJson.Fqn(ReferenceMetadataIndex.HelperTypeName(ownerName)),
            ["method"] = member,
            ["args"] = hargs,
        };
        // The helper is instantiated with the alias class's args FIRST, then the method's own typeArgs (structured).
        var typeArgs = new JsonArray();
        foreach (var ca in classArgs) typeArgs.Add(TypeJson.Write(ca));
        if (node["typeArgs"] is JsonArray ta) foreach (var t in ta) typeArgs.Add(t?.DeepClone());
        if (typeArgs.Count > 0) call["typeArgs"] = typeArgs;
        // Carry the callee's param-type list (receiver-first, mirroring the hoisted helper's __self) so the
        // String->CharSequence bridge sees the synthetic-CharSequence slots (il-regex). `sig` is the m3 legacy
        // comma-string: the receiver token is rendered via the sig-only SigToken; the orig sig tokens pass verbatim
        // (kotc collapses every type var to `gp:T`, so the old class-param NAME substitution is a no-op now).
        var sigParts = new List<string>();
        if (instance && node["recv"] != null)
            sigParts.Add(SigToken(classArgs.Count > 0 ? new TypeNode.Fqn(ownerName, classArgs.ToArray()) : ownerFqn));
        var origSig = (node["sig"] as JsonValue)?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(origSig))
            foreach (var p in SplitTopLevel(origSig)) sigParts.Add(p);
        // `sig` may be LONGER than args (omitted defaulted params, filled downstream) — the bridge matches
        // positionally from the left; only a SHORTER sig would misalign.
        if (sigParts.Count >= hargs.Count) call["sig"] = string.Join(",", sigParts);
        // Carry the call's statically-known return: a helper returning the alias class's BARE type param
        // (`ArrayList<Int>.removeAt` -> E) reflects as the callee's own `!!n` at the call site, and boxing that
        // out-of-scope token is invalid IL (BadImageFormat); ilemit's RetOr/CoerceReturn recover the concrete type
        // from `retType` (same channel the erased nullable-generic return conversion reads). NEVER a bare `gp:`
        // token (an open call site inside another generic body): it buys no conversion there, and when the callee's
        // return is the ERASED nullable-generic `object`, CoerceReturn would `unbox.any !!X` a possibly-null —
        // NullReferenceException for a value instantiation. The open representation of such a value stays `object`.
        if (RetToken(node) is JsonNode ret && !IsTvType(ret)) call["retType"] = ret;
        return call;
    }

    // The legacy sig-token spelling of a SOURCE-vocabulary type, used ONLY to build the m3 `sig` comma-string (the one
    // documented legacy-string exception). Mirrors kotc.bir.TypeNode.legacyToken (a type var collapses to `gp:T`).
    internal static string SigToken(TypeNode t) => t switch
    {
        TypeNode.Fqn f => f.Args == null ? f.Name : f.Name + "[" + string.Join(",", f.Args.Select(SigToken)) + "]",
        TypeNode.Tv => "gp:T",
        TypeNode.Fn fn => (fn.Suspend ? "sfunc:" : "func:") + SigToken(fn.Ret) + ":" + string.Join(",", fn.Params.Select(SigToken)),
        TypeNode.Nullable n => "nullable:" + SigToken(n.Of),
        TypeNode.Array a => "array:" + SigToken(a.Elem),
        TypeNode.ByRef b => "byref:" + SigToken(b.Of),
        _ => "object",
    };

    // The call's parameter types, used as the clr* argTypes overload key. Prefer kotc's `sig` (a comma-joined
    // param-type list); else infer each arg's own type token; else empty. Left in the kotlin.* vocabulary —
    // BirTypeLowering lowers `argTypes` afterwards.
    static JsonArray InferArgTypes(JsonObject node, JsonArray args)
    {
        // Prefer kotc's `sig` (the m3 legacy comma-string of param-type tokens) — its elements stay STRINGS this
        // milestone; else infer each arg's own STRUCTURED type. Either form is a valid clr* argTypes overload-key entry.
        var sig = (node["sig"] as JsonValue)?.GetValue<string>();
        var result = new JsonArray();
        if (!string.IsNullOrWhiteSpace(sig))
        {
            foreach (var p in SplitTopLevel(sig)) result.Add(p);
            if (result.Count == args.Count) return result;
            result = new JsonArray();
        }
        foreach (var a in args) result.Add(InferArgType(a));
        return result;
    }

    // The structured return-type slot of a call node (dynRet/retType/ret), cloned; null when absent.
    static JsonNode RetToken(JsonObject node)
    {
        foreach (var key in new[] { "dynRet", "retType", "ret" })
            if (node[key] is JsonNode n && TypeJson.Read(n) is TypeNode) return n.DeepClone();
        return null;
    }

    // A ret slot is an UNBOUND type parameter (`Tv`) — the guard on carrying a `retType` hint (an open `gp:` token
    // buys no conversion at the call site and, when the callee return is object-erased, would unbox.any a null).
    static bool IsTvType(JsonNode slot) => TypeJson.Read(slot) is TypeNode.Tv;

    // An expression's own STRUCTURED type (its type/ret slot), cloned; Fqn("object") when none is recoverable.
    static JsonNode InferArgType(JsonNode node)
    {
        if (node is JsonObject obj)
            foreach (var key in new[] { "type", "retType", "resultType", "ret", "dynRet" })
                if (obj[key] is JsonNode n && TypeJson.Read(n) is TypeNode) return n.DeepClone();
        return TypeJson.Fqn("object");
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
                ["type"] = TypeJson.Fqn("System.NotImplementedException"),
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
// name is left untouched — those stay kotc's concern.)
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
                    if ((obj["k"] as JsonValue)?.GetValue<string>() == "callInstance")
                    {
                        // SKIP the BCL-slot rename when the call targets a rule-3 member on a @ClrTypeAlias CLASS
                        // owner (an intrinsic-less concrete override carrying a real body that AliasHelperHoist lifts
                        // into a <>dotkt_ClrH_* helper — String.compareTo's ordinal body must NOT resolve to the
                        // culture-sensitive System.String.CompareTo slot). Leaving it the Kotlin name lets
                        // MemberCallSubstitution's Rule 3 route it to that helper. Mirrors Rule 3's own gate exactly:
                        // a CLR-bound NON-interface owner whose member is rule-3. (An INTERFACE owner is excluded —
                        // the ref.dll mis-reports its abstract members as non-abstract, so IsRule3Member false-positives
                        // there; and a REAL non-alias class like ArrayDeque.size -> the emitted Count slot still renames.)
                        // ownerType is a STRUCTURED `{t:fqn,name:…}` node after the m1 TYPE FLIP (was a legacy string) —
                        // read it via OwnerName so `ot` is non-null; a stale `as JsonValue` read left it null, so the
                        // rule-3 guard below never fired and String.compareTo was WRONGLY renamed to the culture-sensitive
                        // System.String.CompareTo slot (il-cmpord: ordinal comparison must win).
                        var ot = TypeJson.OwnerName(obj["ownerType"]);
                        var mn = (obj["method"] as JsonValue)?.GetValue<string>();
                        var otFqn = ot != null ? ReferenceMetadataIndex.BareOwnerFqn(ot) : null;
                        var isRule3Alias = otFqn != null && mn != null
                            && refs.TryResolveClrOwner(ot, out _, out var otKind) && otKind != "interface"
                            && refs.IsRule3Member(otFqn, mn);
                        if (!isRule3Alias) obj["method"] = slot;
                    }
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
            if (TypeJson.OwnerName(oo["owner"]) is not string owner) continue;
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
            if (TypeJson.OwnerName(oo["owner"]) is not string owner) continue;
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
                .Select(p => ReferenceMetadataIndex.ParamKey((p as JsonObject)?["type"])).ToList();
            // An alias-class member that overrides a @ClrIntrinsic ancestor is normally a bound stub (its call
            // substitutes to the BCL), so it is dropped. But a GENUINE rule-3 member — concrete + intrinsic-less in
            // the ref.dll (String.compareTo's ordinal body overriding the culture-sensitive Comparable.compareTo@ClrIntrinsic)
            // — carries a REAL Kotlin body that must be PRESERVED and hoisted (else the call would resolve to the
            // semantically-wrong BCL slot). IsRule3Member is exactly that ref.dll signal, so exempt it from the override-drop.
            var drop = refs.IsBoundStub(owner, name, keys)
                || (alias && mo["overrides"] is JsonArray ovs && DeclarationRename.ResolveSlot(ovs, refs) != null
                    && !refs.IsRule3Member(owner, name));
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
// of rule-3 helper synthesis. Runs only in substitute/app builds (never ref).
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
            if ((mo["static"] as JsonValue)?.GetValue<bool>() == true) continue;   // a top-level/companion static, not a member
            if (mo["body"] is not JsonArray mbody) continue;                        // abstract / no body
            // A property accessor (`get_`/`set_`) is normally a `clrPropGet`/`clrPropSet` on the BCL type, NOT a hoisted
            // helper — so blanket-skip it. EXCEPTION: a rule-3 accessor whose body binds to a BCL *method* (e.g. Regex's
            // `val pattern get() = toString()` — the BCL Regex has no `Pattern` property, only `ToString()`). Such an
            // accessor MUST be hoisted so `re.pattern` routes to `<>dotkt_ClrH_Regex.get_pattern(recv)`. But hoist it ONLY
            // when the body reads NO backing field: a rule-3 accessor that reads `{"k":"field"}` (another alias's real
            // backing field) would NRE ilemit's ResolveField (no such field on the BCL type) — those stay clrPropGet/Set.
            if ((mn.StartsWith("get_", StringComparison.Ordinal) || mn.StartsWith("set_", StringComparison.Ordinal))
                && BodyReadsBackingField(mbody)) continue;
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
        // The class type params are declared on the static helper as its OWN (method-scope) params AHEAD of the
        // method's own (MergeTypeParams), so `__self`'s generic args are METHOD-scope tv by flattened position.
        TypeNode selfType = classTps is { Count: > 0 }
            ? new TypeNode.Fqn(aliasToken, Enumerable.Range(0, classTps.Count).Select(i => (TypeNode)new TypeNode.Tv("method", i)).ToArray())
            : new TypeNode.Fqn(aliasToken);
        var ps = new JsonArray { new JsonObject { ["name"] = "__self", ["type"] = TypeJson.Write(selfType) } };
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

    // True if the accessor body reads (or writes) a raw backing field — a `{"k":"field"}` / `{"k":"setFieldExpr"}` node.
    // Such an accessor cannot be hoisted onto the BCL alias type (ilemit's ResolveField NREs — the BCL type has no such
    // field), so it stays a clrPropGet/Set. A rule-3 accessor with NO field node (e.g. `get() = toString()`) is safe.
    static bool BodyReadsBackingField(JsonNode n)
    {
        if (n is JsonObject o)
        {
            if ((o["k"] as JsonValue)?.GetValue<string>() is string k
                && (k == "field" || k == "setFieldExpr" || k == "staticField" || k == "staticFieldSet")) return true;
            foreach (var kv in o) if (kv.Value != null && BodyReadsBackingField(kv.Value)) return true;
            return false;
        }
        if (n is JsonArray a)
        {
            foreach (var i in a) if (i != null && BodyReadsBackingField(i)) return true;
            return false;
        }
        return false;
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
