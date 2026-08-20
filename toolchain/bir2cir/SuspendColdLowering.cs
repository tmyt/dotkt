// bir2cir — SuspendColdLowering: the cold-core suspend -> state-machine transform.
//
// Per docs/design-coroutine-cold-core-task-bridge.md §11 (the LOCKED contract) + §14 (the R1 classifier
// addendum). This pass lowers a Kotlin `suspend fun` into the COLD Continuation shape:
//
//   suspend fun f(a): R           (top-level file-class static; extension = leading `__self` param)
//     -- SM class:   <Owner>_f$sm[<tp>] : kotlin.coroutines.clr.internal.ContinuationImpl
//                      fields: int label; [$this for an instance member]; <spilled params/locals/temps>
//                      object invokeSuspend(object result)   // label dispatch + segmented body
//     -- cold entry: object f$dotkt_suspend[<tp>](a, completion: Continuation<Any?>)
//                      { val sm = new <Owner>_f$sm[<tp>]([this,] a, completion); return sm.invokeSuspend(null) }
//     -- suspend main additionally gets a synthesized PLAIN `fun main()` that drains the cold body.
//
// The blueprint is kotc's LIVE CPS engine (BirEmitter.kt collectCpsVars/spillExpr/emitWhenCps/emitWhileCps/
// emitTryCps), re-implemented over BIR JSON targeting the cold shape. CRITICAL OBSERVATION: kotc already
// FLATTENS `while`/`for`/`do-while` into structured `block`/`label`/`brIf`/`goto` BIR, so loops need no
// re-segmentation here — only `if`/`when` survive as `cond` (ternary) EXPRESSIONS, which this pass lowers to
// label/brIf/goto control flow when they contain a suspension (mirroring emitWhenCps).
//
// The SM resume protocol matches Kotlin/JVM's ContinuationImpl lowering: a single `result` carrier (the
// invokeSuspend parameter), label dispatch that jumps to each post-suspend merge point, a
// `COROUTINE_SUSPENDED` check after each cold call, and a `throwOnFailure(result)` prologue at each merge
// point (the SM-prologue rethrow that surfaces a failed async resume — the CLR analog of the JVM SM's
// `ResultKt.throwOnFailure($result)`).
//
// R1 — DECLARATION IS UNCONDITIONAL (the cold-entry ABI is an INVARIANT, not a property to be proven).
// EVERY suspend member declared in the compilation gets a cold-entry slot `<name>$dotkt_suspend` + a public
// Task bridge, no exceptions. There is NO resolvability fixpoint and NO eligibility FILTER — a CLASSIFIER
// (see the collection loop + FunGen.Build) assigns each declared suspend member one of three shapes:
//   * explicitly abstract declaration    -> abstract cold entry + abstract bridge (no SM).
//   * concrete + segmentable             -> SM class + cold entry + bridge (the full transform).
//   * concrete + NOT segmentable (v1)    -> a call-time-throw cold entry (`throw NotSupportedException(reason)`)
//                                           + bridge, and a bir2cir WARNING naming the fun + the refusal site.
// The v1-non-segmentable set is: a suspension in a catch/finally, a nested suspending try, a suspend lambda
// body reached in a disallowed position, and the M4 own-generic-on-a-generic-class combination. Because the
// cold entry ALWAYS exists (concrete, callable), same-assembly call-site rewrite is UNCONDITIONAL for
// callStatic/callInstance/clr* alike (resolvability holds by construction), and virtual dispatch through the
// virtual/override-lockstep cold slot resolves inherited/overridden members natively — no hierarchy walk.
//
// SUPPORTED segmentable shapes: straight-line + control flow across suspension (if/when via cond-lowering,
// while/for/do-while already flat), try/catch where the suspension is in the TRY BODY (two-level dispatch),
// generic suspend funs (`suspend fun <T> f(x): T` -> a generic SM `f$sm<T>`), extension suspend funs (kotc
// lowers the receiver to a `__self` param), INSTANCE suspend MEMBERS (`class C { suspend fun m() }` — the SM
// carries a `$this` field of type C), STATIC members (a companion suspend fun kotc promotes to a `static`
// method on the outer class — cold entry/bridge stay static, no `$this`), and MEMBER + cross-file/
// cross-assembly suspend CALLS (`x.g()` callInstance / an owner'd top-level callStatic / a `clr*` referenced
// call — rewritten to the callee's `<name>$dotkt_suspend` cold shape; cross-assembly resolved via the ref.dll
// MemberBinding.Suspend flag + the naming convention, with the R1b existence guard in ColdCall).
//
// The whole analysis is GLOBAL across the compilation's files (ApplyAll): a same-assembly cross-file suspend
// call keeps `owner:null` (kotc emits it identically to a same-file call) and the cold entry it names may
// live in another file. #199 Design B: kotc's `calleeOwner` file-class DISPATCH hint rides through the cold
// rewrite (DeepClone), and synthesized owner-null cold-entry calls (the `main` kickoff, the Task bridge) stamp
// `calleeOwner = Tn(FileClass)` so ilemit dispatches `<name>$dotkt_suspend` in the correct same-simple-name
// package's file class. It is advisory: owner stays null, so the owner-null recognition machinery is untouched.
//
// Runs AFTER MemberCallSubstitution and BEFORE BirTypeLowering, in app, rt-stdlib AND reference builds alike: a
// consuming module needs the ref.dll to declare the same cold entry the runtime twin defines, so the reference build
// executes the same declaration transform and only its BODIES are squashed away afterwards (RefBodySquash). Its
// synthesized nodes are emitted in the SUBSTITUTED call form but in the kotlin.* TYPE vocabulary, so they flow
// through BirTypeLowering.

using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class SuspendColdLowering
{
    // The ref.dll + .NET-refs metadata index — read by EmitAwaitPoint to resolve the .NET AWAITABLE PATTERN (#10:
    // GetAwaiter/awaiter shape) for whatever awaitable a `.await()` targets. Set by ApplyAll / BuildLambdaSm (the two
    // entry points); FunGen (nested) reads it. ApplyAll runs before the lambda phase, so it is always populated by then.
    static ReferenceMetadataIndex _refs;

    // APP-build gate for cold-lowering an `inline suspend fun`'s STANDALONE body. In an app build an inline suspend fun
    // is a user/kotlinx WRAPPER (e.g. `suspendCancellableCoroutine`, or the issue-#22 `mySuspend`) whose standalone body
    // is a real coroutine that must be cold-lowered (InlineSplice splices its call sites, but the emitted standalone
    // method still reaches ilemit). In a STDLIB build the ONLY inline suspend funs are the coroutine PRIMITIVES
    // (`suspendCoroutine`/`suspendCoroutineUninterceptedOrReturn`) — intrinsics whose call sites are reconstructed inline
    // (EmitSuspendCoroutineCall) and whose standalone bodies have no state-machine form at all. So the inline shape gate
    // is lifted ONLY in app builds; stdlib builds keep excluding inline funs, and SuspendResidueLowering then states the
    // excluded declaration's physical body as an explicit call-time throw.
    static bool _appBuild;

    // #22 — names of the top-level intrinsic-block CLOSURE classes consumed (reconstructed INLINE) by
    // EmitSuspendCoroutineCall. A `suspendCoroutineUninterceptedOrReturn`/`suspendCoroutine` block that CAPTURES an
    // enclosing local (e.g. the crossinline `block` param of the issue-#22 `mySuspend`/`suspendCancellableCoroutine`)
    // is materialized by kotc/ClosureSynthesis as a `newClosure` top-level class; the cold lowering splices that
    // class's invoke body into the SM, leaving the class DEAD. It is not merely wasteful: its verbatim invoke body
    // carries a `COROUTINE_SUSPENDED` read whose owner MemberCallSubstitution mis-resolves to the enclosing file class
    // (the SM's own reconstructed `Suspended()` uses the correct IntrinsicsKt owner). So the dead class is pruned after
    // the transform loop. (The non-capturing `newDelegate`/generated-method block form is left untouched — it emits cleanly.)
    static HashSet<string> _consumedIntrinsicClosures;

    // Structured type-node helpers for the SM synthesis (all SM type slots are structured TypeNode). `Tn` = a bare-FQN
    // slot; `Gen` = a constructed generic slot; `ContAny`/`ContUnit` = Continuation<Any?>/<Unit>. Each returns a FRESH
    // JsonNode (a JSON node has a single parent, so a shared instance cannot be reused across slots).
    static readonly TypeNode AnyTn = new TypeNode.Fqn("kotlin.Any");
    static readonly TypeNode IntTn = new TypeNode.Fqn("kotlin.Int");
    static readonly TypeNode BoolTn = new TypeNode.Fqn("kotlin.Boolean");
    static readonly TypeNode VoidTn = new TypeNode.Fqn("void");
    static readonly TypeNode UnitTn = new TypeNode.Fqn("kotlin.Unit");
    static readonly TypeNode ContAnyTn = new TypeNode.Fqn("kotlin.coroutines.Continuation", new TypeNode[] { new TypeNode.Fqn("kotlin.Any") });
    static readonly TypeNode NullableContAnyTn = new TypeNode.Nullable(new TypeNode.Fqn(
        "kotlin.coroutines.Continuation", new TypeNode[] { new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")) }));
    static readonly TypeNode ContUnitTn = new TypeNode.Fqn("kotlin.coroutines.Continuation", new TypeNode[] { new TypeNode.Fqn("kotlin.Unit") });
    // Physical existential view synthesized by FBoundStarProjectionErasure for Continuation<*>. BaseContinuationImpl's
    // create overloads use this CLR slot; their Kotlin surface remains Continuation<*> via [KotlinType].
    static TypeNode ContStarTn;
    static JsonNode Tn(string fqn) => TypeJson.Fqn(fqn);
    static JsonNode Tw(TypeNode t) => TypeJson.Write(t);
    static JsonNode ContAny() => TypeJson.Write(ContAnyTn);
    static JsonNode NullableContAny() => TypeJson.Write(NullableContAnyTn);
    static JsonNode ContUnit() => TypeJson.Write(ContUnitTn);
    static JsonNode ContStar() => TypeJson.Write(ContStarTn);
    // This pass runs after NetInteropBinding and can still synthesize calls to companion factories. Shape such a call
    // from the same validated association oracle used for referenced source calls: a singleton carrier gets its exact
    // INSTANCE receiver; a projected CLR-static association keeps its authored owner-static ABI. The semantic owner never implies
    // a carrier name, so an absent trusted mapping cannot manufacture one from a suffix convention.
    static JsonObject CompanionFactoryCall(
        string semanticOwner,
        string method,
        JsonArray typeArgs,
        JsonArray sig,
        JsonArray args,
        JsonNode ret)
    {
        var call = new JsonObject
        {
            ["method"] = method,
            ["typeArgs"] = typeArgs,
            ["sig"] = sig,
            ["args"] = args,
            ["ret"] = ret,
        };
        if (_refs?.TrySingletonCompanionCarrier(semanticOwner, out var carrier) == true)
        {
            // A companion carrier is always a non-generic TypeDef, whether it is nested in its owner or hoisted
            // beside it, so its singleton is named with no type arguments.
            var carrierType = Tw(new TypeNode.Fqn(carrier));
            call["k"] = "callInstance";
            call["ownerType"] = carrierType.DeepClone();
            call["virtual"] = false;
            call["recv"] = new JsonObject
            {
                ["k"] = "staticField",
                ["ownerType"] = carrierType.DeepClone(),
                ["name"] = "$INSTANCE",
                // This pass owns the late companion-factory call shape, so state the singleton field's self-typed
                // representation here just as the earlier companion-representation producer does. A consumer must
                // never have to infer a static field's value type from its declaring owner.
                ["sty"] = carrierType,
            };
        }
        else
        {
            call["k"] = "callStatic";
            call["owner"] = Tn(semanticOwner);
        }
        return call;
    }
    static bool IsUnitTn(TypeNode t) => t is TypeNode.Fqn { Args: null, Name: "void" or "kotlin.Unit" };
    static bool IsAnyTn(TypeNode t) => t is TypeNode.Fqn { Args: null, Name: "kotlin.Any" };
    // #151 — the suspend RESULT type is `kotlin.Nothing` (`suspend fun f(): Nothing`, incl. `Nothing?` — the outer `?`
    // is peeled onto _resultNullable, leaving a bare `kotlin.Nothing`). The Task<Nothing> bridge return must carry the
    // pre-erasure Nothing fact (retNothing) so RoundtripMetadata stamps [KotlinNothing] and dll2klib restores it (#135).
    static bool IsNothingTn(TypeNode t) => t is TypeNode.Fqn { Args: null, Name: "kotlin.Nothing" };

    const string ContinuationImplFqn = "kotlin.coroutines.clr.internal.ContinuationImpl";
    const string SuspendLambdaFqn = "kotlin.coroutines.clr.internal.SuspendLambda";
    // The CLR Task ABI is a physical lowering decision owned here. These identities are resolved and validated
    // against the exact compile-reference universe; no kotlin.clr alias declaration participates in the bridge.
    const string TaskBclFqn = "System.Threading.Tasks.Task";
    const string TaskCompletionSourceBclFqn = "System.Threading.Tasks.TaskCompletionSource";
    // One module-private sink is synthesized into each app assembly that needs a Task bridge or suspend-main drain.
    // It implements the Kotlin Continuation protocol but completes a CLR TaskCompletionSource<T>.
    const string RootContinuationFqn = "kotlin.coroutines.clr.internal.RootContinuation";
    // A @RestrictsSuspension-scope (e.g. SequenceScope) suspend lambda's SM base. Same 2-arg (arity, completion)
    // ctor + create() protocol as SuspendLambda; RestrictedContinuationImpl pins EmptyCoroutineContext.
    const string RestrictedSuspendLambdaFqn = "kotlin.coroutines.clr.internal.RestrictedSuspendLambda";
    const string BaseContinuationImplFqn = "kotlin.coroutines.clr.internal.BaseContinuationImpl";
    // BaseContinuationImpl.create returns Continuation<Unit> (ContinuationImpl.kt:82/87); a CLR virtual override needs
    // an EXACT return-type match (no covariance), so the SM's create() must return Continuation<Unit>, NOT <Any>.
    const string IntrinsicsKtFqn = "kotlin.coroutines.intrinsics.IntrinsicsKt";
    // Top-level `throwOnFailure(result)` helper (ContinuationImpl.kt, package kotlin.coroutines.clr.internal).
    const string ThrowOnFailureOwner = "kotlin.coroutines.clr.internal.ContinuationImplKt";

    // The two DISJOINT halves of "a subtree the enclosing state machine does not own". LambdaKinds below is their
    // UNION, never the other way round: a new kind is placed in exactly one half and every consumer's view of it
    // follows, so there is no "added to the union but missing from the half" state for a reader to get wrong.
    // (SuspendLiveness needs the halves apart — it skips one and descends into the other — and derives both from
    // here rather than restating them.)
    //
    // Half 1 — a lambda/closure VALUE: its body is ANOTHER frame (its own state machine or closure class), so the
    // enclosing analyses never descend into it. `newSuspendLambda` is a suspend-lambda VALUE built inside a suspend
    // fun: SuspensionRefusalReason and Rewrite SPECIAL-CASE it (GAP 2) — the enclosing fun IS cold-transformed and
    // the lambda copied opaquely with SM-vocabulary `capValues`.
    internal static readonly HashSet<string> OtherFrameBaseKinds = new(StringComparer.Ordinal)
        { "newClosure", "newDelegate", "lambda", "newSuspendLambda" };

    // Half 2 — an INLINE loop: its body runs in THIS frame, so the storage analysis walks it, but the emitter's
    // own-suspension question treats it as a separate scope (a suspending one is flattened away before it is
    // asked). `newSam` is deliberately in NEITHER half: the emitter descends into it and SuspensionRefusalReason
    // admits it, so it is transparent to every consumer.
    internal static readonly HashSet<string> InlineLoopKinds = new(StringComparer.Ordinal)
        { "forEachInline", "repeatInline" };

    // Node kinds whose PRESENCE around a suspension disqualifies the fun (its cold entry becomes a call-time
    // throw — ColdEntryStub): suspend lambdas / closures / inline collection loops.
    internal static readonly HashSet<string> LambdaKinds =
        new(OtherFrameBaseKinds.Concat(InlineLoopKinds), StringComparer.Ordinal);

    static SuspendColdLowering()
    {
        if (OtherFrameBaseKinds.Overlaps(InlineLoopKinds)
            || LambdaKinds.Count != OtherFrameBaseKinds.Count + InlineLoopKinds.Count)
            throw new InvalidOperationException(
                "bir2cir: suspend-lowering: the two halves of LambdaKinds are no longer disjoint. A kind in both "
                + "(or in neither, having been added straight to the union) leaves the storage analysis and this "
                + "pass disagreeing about whose frame its body belongs to, which silently demotes a local the "
                + "emitter reads after a resume.");
    }

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();
    static bool Bool(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    // Structured declaration modifier (spec §2.1): `decl.mods.<key> == true` (absent object/key = false).
    static bool Mod(JsonObject decl, string key) => decl["mods"] is JsonObject m && Bool(m[key]);

    // F2 — the SOLE `suspendCoroutine`/`suspendCoroutineUninterceptedOrReturn` recognizer, FQN-based on the plain
    // call. Our compiler does NOT inline these @InlineOnly intrinsics at the call site (cross-module never; same-module
    // no longer either — kotc's same-module source-splice + the `suspendIntrinsic` valueBlock stamp were retired in
    // #75 S4b), so in EVERY build (app cross-module AND stdlib self-build same-module) kotc emits a plain
    // `callStatic <name>(<block>) suspendCall:true`, owner:null (top-level intrinsic) OR the resolved stdlib file-class.
    // A literal block is materialized as a closure class (capturing) or a top-level generated method (non-capturing),
    // but Kotlin also permits an already-materialized function value (a local/parameter/field/call result) here. This
    // IS a suspension point — recognized here, lowered by EmitSuspendCoroutineCall (which reconstructs the wrapper's
    // SafeContinuation body / the unintercepted block, since the un-inlined wrapper body is unavailable). The
    // recognizer is purely STRUCTURAL (k/suspendCall/method/owner/one block arg) — no module-boundary gate — so it fires
    // identically same-module and cross-module.
    // The two suspendCoroutine intrinsics and their stdlib file-class owners. kotc emits `owner:null`; an earlier
    // bir2cir pass (call-owner resolution) fills in the file class — so a match is `owner == null` (unresolved) OR the
    // exact stdlib owner (a user-defined same-name function in a DIFFERENT owner is thus never mistaken for these).
    static readonly Dictionary<string, string> SuspendCoroutineIntrinsicOwners = new(StringComparer.Ordinal)
    {
        ["suspendCoroutine"] = "kotlin.coroutines.ContinuationKt",
        ["suspendCoroutineUninterceptedOrReturn"] = "kotlin.coroutines.intrinsics.IntrinsicsKt",
    };
    static bool IsSuspendCoroutineCall(JsonObject o)
    {
        if (Str(o["k"]) != "callStatic" || !Bool(o["suspendCall"])) return false;
        if (Str(o["method"]) is not string m || !SuspendCoroutineIntrinsicOwners.TryGetValue(m, out var expectOwner)) return false;
        var owner = TypeJson.OwnerName(o["owner"]);
        if (owner != null && owner != expectOwner) return false;
        return o["args"] is JsonArray args && args.Count == 1 && args[0] is JsonObject;
    }

    // GAP 1 (P3 wave-2b) — a call to a suspend functional VALUE: `b()` where `b: suspend (...) -> T` is a
    // param/local/field. kotc emits it as `recv.invoke()` (suspendCall:true) and stamps the receiver's resolved
    // Kotlin static type as a structured suspend `fn`. That fact covers both a declared `suspend (...) -> T` value
    // (`kotlin.coroutines.SuspendFunctionN`) and an inferred callable-reference value (`kotlin.reflect.KSuspendFunctionN`)
    // without teaching this lowering about either frontend implementation class name. Unlike a NAMED suspend call it
    // has no `<name>$dotkt_suspend` cold entry — the value at runtime IS a SuspendLambda state machine (a
    // BaseContinuationImpl), so the suspension is driven through the stdlib cold-invoke helper
    // `startSuspendUninterceptedOrReturn(fn, [receiver,] completion)` (= `create(completion).invokeSuspend(Unit)`),
    // NOT a virtual invoke (the SM implements no SuspendFunctionN interface / carries no `invoke` bridge).
    const string StartSuspendOwner = "kotlin.coroutines.clr.internal.ContinuationImplKt";
    // The receiver's static type comes from the SHARED node-local deriver, so this asks the question in the one stamp
    // order the toolchain has (`sty`, then `ret`, then `dynRet`, then the kind's own slot — bir-common/NodeType.cs).
    // Inline materialization can replace a local receiver with a typed field/call expression; its equivalent fact
    // rides that node's ordinary result slot, which the deriver reads for it.
    static bool IsSuspendFunctionValue(JsonNode n)
        => CallEvalLowering.StaticTypeOf(n) is TypeNode.Fn { Suspend: true };
    // #79 — the top-level `suspend inline val coroutineContext` read (Continuation.kt:157). Its getter is
    // intentionally `throw NotImplementedError("Implemented as intrinsic")`, so RESOLUTION can never make it work — a
    // real binding is required, and it lives HERE (the only layer that knows the current-continuation identity). kotc
    // emits the read as a top-level `callStatic method=coroutineContext prop=get` (NOT stamped suspendCall — a context
    // read is not a suspension point); MemberCallSubstitution mis-owns it to the enclosing file class and reconstructs
    // the accessor to `get_coroutineContext` before this pass. Recognized by k=callStatic + the (get_)coroutineContext
    // method name (both spellings) + no args. EXCLUDES kotlinx's own `CoroutineScope.coroutineContext` (a callInstance
    // member property), which stays untouched. Lowered to `<current continuation>.get_context` (JVM's <cont>.getContext).
    internal static bool IsCoroutineContextRead(JsonObject o) =>
        Str(o["k"]) == "callStatic"
        && KotlinPropertyAccessors.IsCall(o, "coroutineContext", "get")
        && (o["args"] is not JsonArray ar || ar.Count == 0);

    const string CoroutineContextFqn = "kotlin.coroutines.CoroutineContext";
    const string ContinuationFqn = "kotlin.coroutines.Continuation";

    // A suspend fun's identity: Owner=null for a top-level file-class static, else the enclosing class FQN. Sig
    // is the joined param-type list — it discriminates OVERLOADS that share (Owner, Name) (e.g. SequenceScope
    // has three `yieldAll` overloads differing only by param type: Iterator/Iterable/Sequence). Without it the
    // registry would collapse the overloads to one, dropping the others (see SigOf).
    //
    // #199 — FileClass is part of the identity so two TOP-LEVEL suspend funs with the SAME simple name in
    // DIFFERENT packages (Owner=null for both — kotc emits top-level method names as bare simple names) do NOT
    // collide. Without it `a.foo` and `b.foo` share FunKey(null,"foo",sig): the loser is dropped from `entries`
    // and left un-lowered -> a bir2cir refusal (SuspendResidueLowering) / runtime EntryPointNotFound. A MEMBER's Owner is
    // already the class FQN (`a.Box` vs `b.Box`) so it needs no FileClass to disambiguate, but including it is
    // harmless (a class lives in exactly one file). The Container (`Owner ?? FileClass`) is the FQN identity used
    // to key the return-type maps below.
    readonly record struct FunKey(string Owner, string FileClass, string Name, string Sig, string TypeParamSig);

    // The param-type signature discriminating overloaded suspend members (see FunKey.Sig).
    // Overload-key signature: the CANONICAL JSON of each param's structured TypeNode. Post-#37-flip `p["type"]` is a
    // TypeNode OBJECT, not a bare string — the old `Str(p["type"])` returned null for EVERY param (→ sig ""), collapsing
    // all same-arity overloads onto one FunKey (only the last survived). That lost the abstract `yieldAll(Iterator)`
    // cold entry to the sibling `yieldAll(IEnumerable)` overloads (seqyieldall: an inner `yieldAll$dotkt_suspend(Iterator,
    // Continuation)` call then fell back to the IEnumerable slot → a Kotlin Iterator cast to IEnumerable → InvalidCast).
    // The two Iterable/Sequence overloads genuinely alias to the SAME `IEnumerable` sig and still coalesce (correct — one
    // CLR method), but the distinct Iterator overload now keeps its own entry.
    static string SigOf(JsonObject m) =>
        m["params"] is JsonArray ps
            ? string.Join(",", ps.OfType<JsonObject>().Select(p =>
                TypeJson.Read(p["type"]) is TypeNode t ? TypeNode.ToJson(t) : (Str(p["type"]) ?? "")))
            : "";

    // Kotlin may distinguish overloads solely by generic bounds. Those bounds are absent from the CLR method
    // signature, but they remain part of the Kotlin declaration identity and therefore must keep the declarations
    // distinct through suspend lowering. ilemit's general duplicate-signature handling owns the eventual physical
    // CLR-name mangling; this registry must not discard either declaration before that layer sees it.
    static string TypeParamSigOf(JsonObject m) =>
        m["typeParams"] is JsonArray tps ? tps.ToJsonString() : "";

    // A declared suspend fun + where it lives (for cold-entry/SM splicing).
    sealed record Entry(JsonObject Method, JsonObject Root, JsonObject TypeNode, string Owner, string FileClass);

    // Returns the callee-return-type map (cold-entry name -> Kotlin resultType), so the SEPARATE
    // SuspendLambdaLowering phase can type a suspend-lambda's awaited value the SAME way (else a
    // lambda's `h()` await falls back to kotlin.Any and the value is never unboxed -> `object + int`).
    public static IReadOnlyDictionary<string, TypeNode> ApplyAll(IReadOnlyList<JsonNode> roots,
        ReferenceMetadataIndex refs, IReadOnlySet<string> localTypeFqns, bool appBuild,
        IReadOnlyDictionary<string, string> localExistentialOwners)
    {
        _refs = refs;   // #10: EmitAwaitPoint reads it to resolve the .NET awaitable pattern for each `.await()`.
        _appBuild = appBuild;
        const string continuation = "kotlin.coroutines.Continuation";
        var continuationCarrier = localExistentialOwners.GetValueOrDefault(continuation);
        if (continuationCarrier == null)
            refs.TryExistentialPhysicalOwner(continuation, out continuationCarrier);
        if (continuationCarrier == null)
            throw new InvalidOperationException(
                "suspend lowering requires the trusted Continuation<*> existential ABI");
        ContStarTn = new TypeNode.Fqn(continuationCarrier);
        _consumedIntrinsicClosures = new HashSet<string>(StringComparer.Ordinal);
        // 1. R1 — the UNCONDITIONAL registry of EVERY declared suspend fun across every input file. This is a
        //    CLASSIFIER, not a filter: nothing is dropped for shape or resolvability. Each admitted member is classified
        //    into abstract / segmentable / call-time-throw by FunGen.
        var entries = new Dictionary<FunKey, Entry>();
        // Closure-class registry (for the suspendCoroutine intrinsic inliner): kotc materializes the intrinsic's
        // `{ c -> … }` block as a top-level closure class; the inliner resolves its `invoke` body by name here.
        var closures = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var r in roots)
            if (r is JsonObject f && f["types"] is JsonArray fts)
                foreach (var t in fts)
                    if (t is JsonObject to && Str(to["name"]) is string tn) closures[tn] = to;
        foreach (var r in roots)
        {
            if (r is not JsonObject file) continue;
            var fileClass = Str(file["fileClass"]) ?? "Kt";
            if (file["methods"] is JsonArray methods)
                foreach (var m in methods)
                    if (m is JsonObject mo && Str(mo["name"]) is string name && IsColdCandidate(mo))
                        entries[new FunKey(null, fileClass, name, SigOf(mo), TypeParamSigOf(mo))] = new Entry(mo, file, null, null, fileClass);
            if (file["types"] is JsonArray types)
                foreach (var t in types)
                    if (t is JsonObject to && Str(to["name"]) is string owner && to["methods"] is JsonArray tms)
                        foreach (var m in tms)
                            if (m is JsonObject mo && Str(mo["name"]) is string name && IsMemberColdCandidate(mo))
                                entries[new FunKey(owner, fileClass, name, SigOf(mo), TypeParamSigOf(mo))] = new Entry(mo, file, to, owner, fileClass);
        }
        // callee-return-type fallback for await-temp field typing when a call node carries neither an instantiated
        // `ret`/`dynRet` NOR a static-type `sty` (see EmitSuspensionPoint — `sty`, the frontend-resolved static
        // type kotc stamps on every call node, is the PRIMARY source and needs no owner disambiguation): the
        // callee's declared resultType, keyed by cold-entry name. Built here (before the early returns) so it is
        // ALWAYS returned for the lambda phase's use. #199 note: this is a bare-name key, so it cannot distinguish
        // two same-simple-name suspend funs across packages — but `sty` (read first) already types every
        // kotc-origin suspension point precisely per call, so this fallback only serves rare synthesized nodes.
        var calleeRet = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        foreach (var (k, e) in entries)
            calleeRet[k.Name] = TypeJson.Read(e.Method["suspendRet"])
                ?? throw new InvalidOperationException(
                    $"bir2cir: suspend-lowering: the suspend declaration `{k.Owner ?? k.FileClass}.{k.Name}` carries no "
                    + "`suspendRet` slot. kotc stamps it on EVERY `isSuspend` declaration (BirEmitterDeclarations "
                    + "`resultTypeJson`) and admission to this registry requires the same `suspend` modifier, so the two "
                    + "cannot disagree on valid input: the slot was dropped by a pass that rewrote the declaration.");

        // A global (owner#name -> resultType) index of EVERY method (not just suspend). Stage 0's expression typer
        // (SuspendOperandPlan.ExprTyper) uses it to type the local an operand plan materialises when the operand's
        // own node carries no return type. It is a SECONDARY fallback: the typer reads the node's `sty` (the
        // frontend static type kotc stamps on every call/field node) FIRST, so this index is consulted only for the
        // rare node without one. Top-level -> "#name"; member -> "owner#name".
        var methodRets = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        // A global (owner#name -> declared type) index of EVERY member/static FIELD, a SECONDARY fallback to `sty`
        // (as methodRets): a raw `field`/`staticField`/`lateinitGet` read carries no result type of its own, and one
        // bound left of a suspension needs a typed local like any other.
        var fieldTypes = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        foreach (var r in roots)
        {
            if (r is not JsonObject file) continue;
            if (file["methods"] is JsonArray fms)
                foreach (var m in fms)
                    if (m is JsonObject mo && Str(mo["name"]) is string mn)
                        methodRets["#" + mn] = MethodDeclRet(mo, null, mn);
            if (file["fields"] is JsonArray ffs)
                foreach (var f in ffs)
                    if (f is JsonObject fo && Str(fo["name"]) is string fn && TypeJson.Read(fo["type"]) is TypeNode ft0)
                        fieldTypes["#" + fn] = ft0;
            if (file["types"] is JsonArray fts2)
                foreach (var t in fts2)
                    if (t is JsonObject to && Str(to["name"]) is string ow)
                    {
                        if (to["methods"] is JsonArray tms)
                            foreach (var m in tms)
                                if (m is JsonObject mo && Str(mo["name"]) is string mn)
                                    methodRets[ow + "#" + mn] = MethodDeclRet(mo, ow, mn);
                        if (to["fields"] is JsonArray tfs)
                            foreach (var f in tfs)
                                if (f is JsonObject fo && Str(fo["name"]) is string fn && TypeJson.Read(fo["type"]) is TypeNode ft1)
                                    fieldTypes[ow + "#" + fn] = ft1;
                    }
        }

        // STAGE 0 — OPERAND PLANS (SuspendOperandPlan.cs). Runs on EVERY input file, before any state machine is
        // built, so that no node reaches the per-fun transform still holding a suspension where the segmented body
        // cannot express one: a suspension inside a suspend call's own operand list, or an operand evaluated left
        // of a suspension while sitting in a slot that is assembled after it. Ahead of the `entries` early return
        // below, because a suspend LAMBDA in a file that declares no suspend fun is lowered by the separate
        // SuspendLambdaLowering phase and needs the same normalization.
        PlanSuspensionBearingOperands(roots, new ExprTyper(methodRets, fieldTypes));

        if (entries.Count == 0) return calleeRet;

        // R1 — NO fixpoint. Every declared suspend member is transformed unconditionally (its cold entry always
        // exists — concrete/abstract, but always callable), so a suspend call it makes always resolves to a cold
        // entry by construction: same-assembly by declaration, cross-assembly by the ref.dll Suspend flag + naming
        // convention (guarded in ColdCall / R1b). The old resolvability fixpoint + its L3 drop warning are deleted;
        // the diagnostic quality moved into the classifier (FunGen names the ROOT non-segmentable shape).

        var baseIsLocal = localTypeFqns.Contains(ContinuationImplFqn);

        // The public Task<R> bridge is physical CLR representation, so resolve it from the compile references that
        // bir2cir already owns. The former stdlib @ClrTypeAlias lookup inverted ownership: a Kotlin declaration was
        // acting as a registry for a representation selected entirely after BIR.
        var tcsBcl = TaskCompletionSourceBclFqn;
        var taskBcl = TaskBclFqn;
        if (appBuild && !baseIsLocal
            && (refs.ResolveNetType(TaskBclFqn, 0) == null
                || refs.ResolveNetType(TaskBclFqn + "`1", 1) == null
                || refs.ResolveNetType(TaskCompletionSourceBclFqn + "`1", 1) == null))
            throw new NotSupportedException(
                "bir2cir: suspend-lowering: the compile-reference set does not provide "
                + "System.Threading.Tasks.Task, Task<T>, and TaskCompletionSource<T>; "
                + "the public suspend ABI cannot be emitted.");

        // SM-type-name disambiguation for OVERLOADED members (same Owner+Name, differing only by param type):
        // each overload needs a UNIQUE SM class name (the cold-entry NAME stays `<name>$dotkt_suspend` — they are
        // IL overloads resolved by param type). A group of one keeps the bare `<owner>_<name>$sm` name (existing
        // output unchanged); a group of >1 appends a suffix from the param types' simple names
        // (`_Iterator`/`_Iterable`/`_Sequence`), falling back to a positional index on a residual collision.
        var smSuffix = new Dictionary<FunKey, string>();
        foreach (var g in entries.Keys.GroupBy(k => (k.Owner, k.FileClass, k.Name)))
        {
            var members = g.ToList();
            if (members.Count == 1) { smSuffix[members[0]] = ""; continue; }
            var cands = members.Select(k => ParamSimpleNames(entries[k].Method)).ToList();
            var unique = cands.All(c => c.Length > 0) && cands.Distinct(StringComparer.Ordinal).Count() == cands.Count;
            for (var i = 0; i < members.Count; i++)
                smSuffix[members[i]] = "_" + (unique ? cands[i] : "ov" + i);
        }

        // 2. Transform each declared suspend fun, splicing the cold entry (into its declaring container) and the
        //    SM type (into its file's top-level types).
        JsonObject rootContinuationHost = null;
        foreach (var key in entries.Keys)
        {
            var e = entries[key];
            // M3 — a `static` member (a companion suspend fun kotc promotes to a static method on the OUTER class):
            // no `$this`, the cold entry/bridge stay static in the class (like a top-level fun, but the container is
            // the class). An instance member is `owner != null && !static`.
            var staticMember = e.TypeNode != null && Bool(e.Method["static"]);
            // The ENCLOSING class's type-param names. An ordinary instance member needs them for its constructed
            // `$this`. A lexical local function physically materialized as a static member also sees that frame and
            // carries `lexicalOwnerTypeParamCount`; its state machine is nested under the same generic TypeDef and must
            // re-declare the complete prefix. Companion/static source members carry no such fact and remain owner-free.
            var ownerTpDecls = new JsonArray();
            var lexicalOwnerCount = !staticMember
                ? (e.TypeNode?["typeParams"] as JsonArray)?.Count ?? 0
                : e.Method["lexicalOwnerTypeParamCount"] is JsonValue ownerCountValue
                    && ownerCountValue.TryGetValue<int>(out var ownerCount) ? ownerCount : 0;
            if (lexicalOwnerCount > 0 && e.TypeNode?["typeParams"] is JsonArray otps)
            {
                if (lexicalOwnerCount > otps.Count)
                    throw new InvalidOperationException(
                        $"suspend member '{e.Owner}.{key.Name}' claims {lexicalOwnerCount} lexical owner slots " +
                        $"but its owner declares only {otps.Count}");
                foreach (var t in otps.Take(lexicalOwnerCount))
                    ownerTpDecls.Add(t?.DeepClone());
            }
            e.Method.Remove("lexicalOwnerTypeParamCount");
            // Per-file registry of top-level generated methods (the non-capturing `newDelegate` block bodies of a
            // cross-module suspendCoroutine — F2). Keyed within the declaring file (kotc names lambdas per-file, so a
            // global map would collide across files).
            var fileLambdas = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            if (e.Root["methods"] is JsonArray flm)
                foreach (var lm in flm)
                    if (lm is JsonObject lmo && Str(lmo["name"]) is string lmn) fileLambdas[lmn] = lmo;
            // An interface member (kotc emits interface `suspend fun`s with `virtual:true` but WITHOUT the
            // `abstract` flag — unlike an abstract-CLASS member) with no body is abstract by definition (an
            // interface method with no default). Treat it exactly like the abstract-class case so its cold entry
            // AND Task bridge are emitted ABSTRACT (no body), rather than a concrete bridge whose non-virtual
            // `call` to the (interface-abstract) cold entry is unverifiable (ilverify CallAbstract). Concrete
            // implementations in classes fill both slots — ilemit's interface-impl pass binds them by name/sig.
            var gen = new FunGen(e.Method, key.Name, e.FileClass, e.Owner, calleeRet, baseIsLocal, tcsBcl, taskBcl,
                ownerTpDecls, closures, smSuffix[key], fileLambdas,
                e.TypeNode != null && Str(e.TypeNode["kind"]) == "interface", staticMember);
            var newMethods = new List<JsonNode>();
            var newTypes = new List<JsonNode>();
            gen.Build(newMethods, newTypes);
            if (gen.NeedsRootContinuation)
                rootContinuationHost ??= e.Root;

            var container = e.TypeNode != null
                ? (e.TypeNode["methods"] as JsonArray) ?? EnsureArray(e.TypeNode, "methods")
                : (e.Root["methods"] as JsonArray) ?? EnsureArray(e.Root, "methods");
            // In an APP build the original suspend method is REPLACED (public Task bridge + cold entry + SM). In the
            // rt-STDLIB build (baseIsLocal) it is RETAINED alongside the cold entry: kotc's pre-ignition
            // @RestrictsSuspension builder path (`sequence{}`/`iterator{}`, e.g. SlidingWindow.windowedIterator)
            // still calls the Task-shaped SequenceScope.yield/yieldAll BY NAME (BirEmitter.kt:2169-2173 routes those
            // restricted builders through the old closure path) — removing the original would break the stdlib
            // build. The kotc-ignition handoff (delete BirEmitter.kt :3329 + isYield/isYieldAll :1640-1657) retires
            // that path; the original then goes dead and a follow-up drops it. ref/rt stay symmetric (both keep the
            // Task `yield`); rt ADDITIONALLY carries the cold entry an app resolves via the ref.dll Suspend flag.
            if (!baseIsLocal)
                for (var i = container.Count - 1; i >= 0; i--)
                    if (ReferenceEquals(container[i], e.Method)) container.RemoveAt(i);
            foreach (var nm in newMethods) container.Add(nm);

            if (newTypes.Count > 0)
            {
                var ts = (e.Root["types"] as JsonArray) ?? EnsureArray(e.Root, "types");
                foreach (var nt in newTypes) ts.Add(nt);
            }

            // The state machine carries its semantic owner and is materialized as that owner's CLR nested type by
            // TypeOwnershipLowering, so its `$this.<private>` accesses retain lexical accessibility without widening.
        }

        // The root Task sink is concrete CLR adapter machinery, not standard-library vocabulary. Emit one internal
        // helper into the app assembly that actually constructs it; abstract-only suspend surfaces need no helper.
        if (rootContinuationHost != null)
            ((rootContinuationHost["types"] as JsonArray) ?? EnsureArray(rootContinuationHost, "types"))
                .Add(BuildRootContinuationType(tcsBcl));

        // #22 — prune the intrinsic-block closure classes the cold lowering reconstructed inline (now dead; see
        // _consumedIntrinsicClosures). Only the SM-consumed closure NAMES are removed, so a `newClosure` used
        // anywhere else survives. GATED on `!baseIsLocal`: an rt-stdlib build (baseIsLocal) RETAINS the original
        // suspend method (above) alongside the cold entry, and that retained body still holds the raw `newClosure`
        // intrinsic-block arg — pruning its class there would dangle the ref. In an app build the original is
        // replaced by the SM, so the closure is genuinely dead. (Vacuous for the current stdlib — its intrinsic
        // blocks are non-capturing — but defensive against a future capturing stdlib primitive.)
        if (!baseIsLocal && _consumedIntrinsicClosures.Count > 0)
            foreach (var r in roots)
                if (r is JsonObject file && file["types"] is JsonArray ts)
                    for (var i = ts.Count - 1; i >= 0; i--)
                        if (ts[i] is JsonObject to && Str(to["name"]) is string tn && _consumedIntrinsicClosures.Contains(tn))
                            ts.RemoveAt(i);
        return calleeRet;
    }

    static JsonObject BuildRootContinuationType(string tcsBcl)
    {
        var tv = new TypeNode.Tv("type", 0);
        var rootType = new TypeNode.Fqn(RootContinuationFqn, new TypeNode[] { tv });
        var tcsType = new TypeNode.Fqn(tcsBcl, new TypeNode[] { tv });
        var resultType = new TypeNode.Fqn("kotlin.Result", new TypeNode[] { tv });
        var nullableThrowable = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Throwable"));
        const string fieldName = "$tcs";
        const string exceptionName = "$exception";
        const int failureLabel = 9101;
        const int kotlinCancellationLabel = 9102;
        const int faultLabel = 9103;
        const int doneLabel = 9104;

        JsonObject This() => new() { ["k"] = "this" };
        JsonObject LocalValue(string name) => new() { ["k"] = "local", ["name"] = name };
        JsonObject TcsField() => new()
        {
            ["k"] = "field",
            ["ownerType"] = Tw(rootType),
            ["recv"] = This(),
            ["name"] = fieldName,
        };
        JsonObject TcsCall(string method, TypeNode[] resolvedMemberParams, params JsonNode[] args) => new()
        {
            ["k"] = "clrInstance",
            ["type"] = Tw(tcsType),
            ["method"] = method,
            ["recv"] = TcsField(),
            ["argTypes"] = new JsonArray(resolvedMemberParams.Select(Tw).ToArray()),
            ["args"] = new JsonArray(args),
            ["ret"] = Tw(BoolTn),
        };
        JsonObject Expr(JsonNode value) => new() { ["k"] = "exprStmt", ["expr"] = value };

        var resultValue = new JsonObject
        {
            ["k"] = "callInstance",
            ["ownerType"] = Tw(resultType),
            ["virtual"] = false,
            ["recv"] = LocalValue("result"),
            ["method"] = "value",
            ["prop"] = "get",
            ["args"] = new JsonArray(),
            ["ret"] = Tw(AnyTn),
        };
        var exceptionValue = new JsonObject
        {
            ["k"] = "callInstance",
            ["ownerType"] = Tw(resultType),
            ["virtual"] = false,
            ["recv"] = LocalValue("result"),
            ["method"] = "exceptionOrNull",
            ["sig"] = new JsonArray(),
            ["args"] = new JsonArray(),
            ["ret"] = Tw(nullableThrowable),
        };
        var resumeBody = new JsonArray
        {
            new JsonObject
            {
                ["k"] = "var",
                ["name"] = exceptionName,
                ["type"] = Tw(nullableThrowable),
                ["init"] = exceptionValue,
            },
            new JsonObject
            {
                ["k"] = "block",
                ["body"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["k"] = "brIf", ["id"] = failureLabel, ["on"] = false,
                        ["cond"] = new JsonObject
                        {
                            ["k"] = "objEq",
                            ["lhs"] = LocalValue(exceptionName),
                            ["rhs"] = new JsonObject
                            {
                                ["k"] = "const",
                                ["type"] = Tw(nullableThrowable),
                                ["value"] = null,
                            },
                        },
                    },
                    Expr(TcsCall(
                        "TrySetResult",
                        new TypeNode[] { tv },
                        new JsonObject { ["k"] = "cast", ["type"] = Tw(tv), ["e"] = resultValue })),
                    new JsonObject { ["k"] = "goto", ["id"] = doneLabel },
                    new JsonObject { ["k"] = "label", ["id"] = failureLabel },
                    new JsonObject
                    {
                        ["k"] = "brIf", ["id"] = kotlinCancellationLabel, ["on"] = false,
                        ["cond"] = new JsonObject
                        {
                            ["k"] = "isInst",
                            ["type"] = Tn("System.OperationCanceledException"),
                            ["e"] = LocalValue(exceptionName),
                        },
                    },
                    Expr(TcsCall(
                        "TrySetCanceled",
                        new TypeNode[] { new TypeNode.Fqn("System.Threading.CancellationToken") },
                        new JsonObject
                        {
                            ["k"] = "clrPropGet",
                            ["type"] = Tn("System.OperationCanceledException"),
                            ["name"] = "CancellationToken",
                            ["static"] = false,
                            ["recv"] = new JsonObject
                            {
                                ["k"] = "cast",
                                ["type"] = Tn("System.OperationCanceledException"),
                                ["e"] = LocalValue(exceptionName),
                            },
                            ["ret"] = Tn("System.Threading.CancellationToken"),
                        })),
                    new JsonObject { ["k"] = "goto", ["id"] = doneLabel },
                    new JsonObject { ["k"] = "label", ["id"] = kotlinCancellationLabel },
                    new JsonObject
                    {
                        ["k"] = "brIf", ["id"] = faultLabel, ["on"] = false,
                        ["cond"] = new JsonObject
                        {
                            ["k"] = "isInst",
                            ["type"] = Tn("kotlin.coroutines.cancellation.CancellationException"),
                            ["e"] = LocalValue(exceptionName),
                        },
                    },
                    Expr(TcsCall("TrySetCanceled", System.Array.Empty<TypeNode>())),
                    new JsonObject { ["k"] = "goto", ["id"] = doneLabel },
                    new JsonObject { ["k"] = "label", ["id"] = faultLabel },
                    Expr(TcsCall(
                        "TrySetException",
                        new TypeNode[] { new TypeNode.Fqn("kotlin.Throwable") },
                        new JsonObject
                        {
                            ["k"] = "cast",
                            ["type"] = Tn("kotlin.Throwable"),
                            ["e"] = LocalValue(exceptionName),
                        })),
                    new JsonObject { ["k"] = "label", ["id"] = doneLabel },
                },
            },
        };

        return new JsonObject
        {
            ["name"] = RootContinuationFqn,
            ["kind"] = "class",
            ["abstract"] = false,
            ["vis"] = "internal",
            ["typeParams"] = new JsonArray("T"),
            ["base"] = null,
            ["interfaces"] = new JsonArray(Tw(new TypeNode.Fqn(
                "kotlin.coroutines.Continuation", new TypeNode[] { tv }))),
            ["fields"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = fieldName,
                    ["type"] = Tw(tcsType),
                    ["vis"] = "private",
                    ["attrs"] = new JsonArray(),
                },
            },
            ["ctors"] = new JsonArray
            {
                new JsonObject
                {
                    ["params"] = new JsonArray
                    {
                        new JsonObject { ["name"] = "tcs", ["type"] = Tw(tcsType) },
                    },
                    ["baseArgs"] = null,
                    ["thisArgs"] = null,
                    ["vis"] = "internal",
                    ["body"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["k"] = "setField",
                            ["ownerType"] = Tw(rootType),
                            ["recv"] = This(),
                            ["name"] = fieldName,
                            ["value"] = LocalValue("tcs"),
                        },
                    },
                },
            },
            ["methods"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "resumeWith",
                    ["static"] = false,
                    ["override"] = false,
                    ["virtual"] = true,
                    ["abstract"] = false,
                    ["objectOverride"] = false,
                    ["vis"] = "public",
                    ["params"] = new JsonArray
                    {
                        new JsonObject { ["name"] = "result", ["type"] = Tw(resultType) },
                    },
                    ["ret"] = Tw(VoidTn),
                    ["body"] = resumeBody,
                    ["attrs"] = new JsonArray(),
                },
                new JsonObject
                {
                    ["name"] = "context",
                    [KotlinPropertyAccessors.SourceNameKey] = "context",
                    [KotlinPropertyAccessors.KindKey] = "get",
                    [KotlinPropertyAccessors.AssociationKey] = "continuation-context",
                    ["static"] = false,
                    ["override"] = false,
                    ["virtual"] = true,
                    ["abstract"] = false,
                    ["objectOverride"] = false,
                    ["vis"] = "public",
                    ["params"] = new JsonArray(),
                    ["ret"] = Tn("kotlin.coroutines.CoroutineContext"),
                    ["body"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["k"] = "return",
                            ["value"] = new JsonObject
                            {
                                ["k"] = "staticField",
                                ["ownerType"] = Tn("kotlin.coroutines.EmptyCoroutineContext"),
                                ["name"] = "INSTANCE",
                            },
                        },
                    },
                    ["attrs"] = new JsonArray(),
                },
            },
            ["properties"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "context",
                    ["type"] = Tn("kotlin.coroutines.CoroutineContext"),
                    [KotlinPropertyAccessors.PropertyRolesKey] = new JsonArray("get"),
                    [KotlinPropertyAccessors.AssociationKey] = "continuation-context",
                },
            },
            ["attrs"] = new JsonArray(),
        };
    }

    static JsonArray EnsureArray(JsonObject o, string key)
    {
        var a = new JsonArray();
        o[key] = a;
        return a;
    }

    // The concatenated simple names of a member's param types (`Iterator<T>` -> "Iterator"), used to build a
    // stable, readable SM-name suffix that disambiguates overloaded suspend members.
    static string ParamSimpleNames(JsonObject m)
    {
        if (m["params"] is not JsonArray ps) return "";
        var parts = new List<string>();
        foreach (var p in ps.OfType<JsonObject>())
        {
            var t = TypeJson.Read(p["type"]) is TypeNode.Fqn f ? f.Name : "";   // the type's simple-name basis
            var dot = t.LastIndexOf('.'); if (dot >= 0) t = t.Substring(dot + 1); // simple name
            t = new string(t.Where(char.IsLetterOrDigit).ToArray());             // sanitize to an identifier
            if (t.Length > 0) parts.Add(t);
        }
        return string.Join("", parts);
    }

    // Part B entry: build a suspend-LAMBDA state-machine TYPE from a newSuspendLambda node's parts (used by
    // SuspendLambdaLowering). Handles arbitrary arity N: arities 0/1 override the fixed create() slots, arity
    // >= 2 overrides the general create(args, completion) slot (CreateMethods unpacks the boxed args).
    public static JsonObject BuildLambdaSm(string smName, int arity,
        List<(string name, TypeNode type)> captures, List<JsonObject> lambdaParams, JsonArray body,
        TypeNode resultType, List<string> typeParams, JsonArray typeParamDecls, int ownerTypeParamCount,
        bool baseIsLocal,
        IReadOnlyDictionary<string, TypeNode> calleeRet = null, bool restricted = false,
        ReferenceMetadataIndex refs = null)
    {
        if (arity < 0) return null;
        // #10: an `.await()` inside a suspend LAMBDA lowers through this path — keep the awaitable-pattern resolver set
        // (SuspendLambdaLowering runs after ApplyAll, but pass it explicitly rather than rely on the prior static write).
        if (refs != null) _refs = refs;
        var gen = new FunGen(smName, arity, captures ?? new List<(string, TypeNode)>(), lambdaParams, body,
            resultType, typeParams, typeParamDecls, ownerTypeParamCount,
            calleeRet as Dictionary<string, TypeNode> ??
                (calleeRet != null ? new Dictionary<string, TypeNode>(calleeRet, StringComparer.Ordinal)
                                   : new Dictionary<string, TypeNode>(StringComparer.Ordinal)),
            baseIsLocal, restricted);
        var types = new List<JsonNode>();
        gen.Build(new List<JsonNode>(), types);
        return types.Count > 0 ? (JsonObject)types[0] : null;
    }

    // --- R1 classifier: ADMIT gate --------------------------------------------------------------------
    // Nothing is refused here for shape — the collection admits EVERY declared suspend member (top-level or
    // member, abstract or concrete, static or instance, generic or not). The only exclusions are the stdlib
    // carve-outs. FunGen then classifies each admitted member into abstract / segmentable / call-time-throw.

    // A TOP-LEVEL suspend fun (kotc emits top-level funs + extension funs as `static`).
    // The declared result type of a method DECLARATION, for the secondary `owner#name` index that stage 0's typer and
    // the suspension points consult when a node itself carries no stamp. A suspend declaration answers with
    // `suspendRet` (the `T` of its `Continuation<T>`); every other with `ret`.
    //
    // NO `kotlin.Any` fallback. Every kotc method emitter writes `ret` UNCONDITIONALLY (BirEmitterDeclarations'
    // member/accessor/enum/event emitters, BirEmitterLifts' lifted lambdas and SAM slots — surveyed: 0 of 7308 stdlib
    // declarations lack it), and a bir2cir-synthesized method that omits it is malformed for ilemit as well. So a miss
    // here is a DROPPED slot, not an untyped-but-valid declaration, and `kotlin.Any` merely boxed the value and moved
    // the failure to a runtime unbox at whichever spill later consulted this index.
    static TypeNode MethodDeclRet(JsonObject m, string owner, string name)
        => TypeJson.Read(m["suspendRet"])
           ?? TypeJson.Read(m["ret"])
           ?? throw new InvalidOperationException(
               $"bir2cir: suspend-lowering: the declaration `{(owner is null ? "" : owner + ".")}{name}` carries neither "
               + "a `suspendRet` nor a `ret` slot, so its result type cannot be indexed for the operand typing that "
               + "spills values across suspensions. kotc stamps `ret` on every method declaration, so this is a slot "
               + "dropped by a pass that synthesized or rewrote the declaration.");

    static bool IsColdCandidate(JsonObject m)
    {
        if (!Mod(m, "suspend")) return false;
        if (!Bool(m["static"])) return false;                       // top-level statics + extensions (kotc: __self param)
        if (Mod(m, "inline") && !_appBuild) return false;           // #22: cold-lower an inline suspend WRAPPER's standalone body in app builds; stdlib intrinsics (suspendCoroutine*) stay stubbed
        if (m.ContainsKey("steps") || m.ContainsKey("coClass")) return false;  // old CPS / sequence path (kotc-owned)
        return true;
    }

    // A suspend member of a class (instance, static/companion, abstract, interface, or generic). All shapes are
    // admitted — a non-segmentable one (the M4 own-generic-on-generic-class combination, a suspension in a
    // catch/finally, …) still gets a call-time-throw cold entry from FunGen, never a drop.
    static bool IsMemberColdCandidate(JsonObject m)
    {
        if (!Mod(m, "suspend")) return false;
        if (Mod(m, "inline") && !_appBuild) return false;           // #22: as IsColdCandidate — inline suspend member wrapper lowers in app builds
        if (m.ContainsKey("steps") || m.ContainsKey("coClass")) return false;
        return true;
    }

    // --- segmentability ------------------------------------------------------------------------------
    // Is every suspension point in a position the straight-line SM can lower? Returns null when YES (segmentable),
    // else a short human-readable REFUSAL REASON naming the first unsupported shape (the classifier stubs the fun
    // with a call-time throw carrying this reason + warns). Refuses a suspension buried in a disallowed
    // lambda/closure position. Catch/finally handlers and nested protected regions are normalized into resumable
    // flat routes before segmentation. Member/cross-assembly suspend CALLS are always
    // allowed — their cold-shape resolvability holds by construction (R1), not by a check here.
    static string SuspensionRefusalReason(JsonNode node, bool inHandler, int tryDepth)
    {
        switch (node)
        {
            case JsonObject o:
                {
                    var k = Str(o["k"]);
                    // F2 — a suspendCoroutine/suspendCoroutineUninterceptedOrReturn call IS a supported cold suspension
                    // point; do NOT descend into its embedded newClosure/newDelegate block arg (which would trip the
                    // LambdaKinds refusal below).
                    if (IsSuspendCoroutineCall(o)) return null;
                    // GAP 2 — a `newSuspendLambda` VALUE built inside a suspend fun (e.g. a member `suspend fun go() =
                    // run1 { … }` that constructs a `this`-capturing suspend lambda and drives it via a suspend-value
                    // call) is SUPPORTED: the lambda is an opaque value whose OWN suspensions become a SEPARATE SM
                    // (SuspendLambdaLowering), and its captures resolve in the enclosing cold SM (a spilled local -> an
                    // SM field, `__outer` -> the member SM's `$this`). Do NOT descend into its body (that is the
                    // lambda's own scope, validated by its own FunGen build) — descending would trip the refusal below.
                    if (k == "newSuspendLambda") return null;
                    // #22 — a PLAIN closure/delegate VALUE (no suspension inside) is a spillable local, not a suspend
                    // lambda: the cold SM holds it in a field across the suspension. This arises when InlineSplice
                    // MATERIALIZES a crossinline lambda param (`§4.4ii`) as a `newClosure` bound to a temp that a nested
                    // suspend-intrinsic closure captures (the issue-#22 `suspendCancellableCoroutine`/`mySuspend` shape).
                    // Admit it (do NOT descend — its body is its own scope, and HasSuspension already proved it holds no
                    // suspension). A genuine SUSPEND lambda is `newSuspendLambda` (above); a `newClosure` that DOES wrap a
                    // suspension stays refused (SuspendLambdaLowering territory).
                    if ((k == "newClosure" || k == "newDelegate") && !HasSuspension(o)) return null;
                    // App-build ranges have already become counted `for` and are flattened below. A stdlib self-build
                    // retains `forRange`; reject its suspending body explicitly instead of letting Rewrite hoist the
                    // suspension outside the structured loop.
                    if (k == "forRange" && o["body"] is JsonNode frBody && HasOwnSuspension(frBody))
                        return "suspension buried in an unsupported 'forRange' loop position";
                    // #82/#98 — a `forEachInline` (GetEnumerator collection loop) or counted `for` whose BODY spans a
                    // suspension is FLATTENED to flat CFG by FlattenSuspendingLoops (like the always-admitted `forArray`).
                    // Only forEachInline needs an exemption
                    // from the LambdaKinds refusal below (falling through to the generic child recursion, which validates its
                    // body). A SUSPENSION-FREE forEachInline stays REFUSED — conservatively, not by necessity: the original
                    // reason was that its LambdaKinds subtree was invisible to the promote-all field collection while
                    // Rewrite descended into it, so a loop-interior name colliding with a spilled outer var miscompiled
                    // silently. The liveness analysis that replaced that collection DOES walk the loop body, so the
                    // collision is gone and lifting this refusal is a separate, gate-backed change.
                    // `repeatInline`/`forRange` with a body suspension stay refused (not flattened); app-build ranges have
                    // already become counted `for` nodes and are handled here.
                    // ANY OTHER lambda/closure/sequence node -> unsupported (genuine suspend lambdas, which emit a
                    // `newClosure` and are NOT flagged `suspendCall`, are handled separately by SuspendLambdaLowering).
                    if (k != null && LambdaKinds.Contains(k)
                        && !(k == "forEachInline" && o["body"] is JsonNode feBody && HasOwnSuspension(feBody)))
                        return $"suspension buried in an unsupported '{k}' lambda/closure position";
                    if (k == "try")
                    {
                        // Loop-aware: HasOwnSuspension is forEachInline-BLIND (LambdaKinds), so a try whose body's only
                        // suspension lives inside a (flattenable) forEachInline would read as non-suspending here — the
                        // nested-suspending-try refusal would then be bypassed and Build would emit a branch INTO the outer
                        // protected region (InvalidProgramException). HasLoopBorneSuspension sees through forEachInline.
                        var bodyHasSusp = o["body"] != null && HasLoopBorneSuspension(o["body"]);
                        if (SuspensionRefusalReason(o["body"] ?? JsonValue.Create(0), inHandler, bodyHasSusp ? tryDepth + 1 : tryDepth) is string tbr)
                            return tbr;
                        // #78 — HoistSuspendingCatches moves suspending catch/finally handlers out of CLR handler
                        // clauses before segmentation. Recurse here only to retain the other structural refusals.
                        if (o["catches"] is JsonArray cs)
                            foreach (var c in cs)
                                if (c is JsonObject co && SuspensionRefusalReason(co["body"] ?? JsonValue.Create(0), inHandler, tryDepth) is string cbr)
                                    return cbr;
                        if (o["finally"] != null && SuspensionRefusalReason(o["finally"], inHandler, tryDepth) is string fbr)
                            return fbr;
                        return null;
                    }
                    foreach (var kv in o)
                        if (kv.Value != null && SuspensionRefusalReason(kv.Value, inHandler, tryDepth) is string cr) return cr;
                    return null;
                }
            case JsonArray a:
                foreach (var it in a) if (it != null && SuspensionRefusalReason(it, inHandler, tryDepth) is string ar) return ar;
                return null;
            default:
                return null;
        }
    }

    static bool HasSuspension(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                // A suspendCoroutine/suspendCoroutineUninterceptedOrReturn call (F2) is a plain `callStatic
                // suspendCall:true`, already caught by the check above — no separate marker needed.
                if (o.ContainsKey("suspendCall") && Bool(o["suspendCall"])) return true;
                foreach (var kv in o) if (kv.Value != null && HasSuspension(kv.Value)) return true;
                return false;
            case JsonArray a:
                foreach (var it in a) if (it != null && HasSuspension(it)) return true;
                return false;
            default:
                return false;
        }
    }

    // #78 Step 3a — like HasSuspension, but does NOT descend into a nested lambda/closure/suspend-lambda subtree
    // (LambdaKinds): a suspension inside such a value belongs to THAT value's own SM, not the enclosing fun. Used by
    // the try/eval-order gates so a try/when whose only suspensions live inside a nested suspend-lambda VALUE reads as
    // NON-suspending here (it needs no SM segmentation) — killing the false-positive "suspending try" refusals and the
    // needless splitting of plain trys. The DEEP HasSuspension is kept ONLY where the closure interior must be
    // inspected (the newClosure/newDelegate classification at SuspensionRefusalReason).
    static bool HasOwnSuspension(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                if (Str(o["k"]) is string k && LambdaKinds.Contains(k)) return false;   // nested lambda -> its own SM
                if (o.ContainsKey("suspendCall") && Bool(o["suspendCall"])) return true;
                foreach (var kv in o) if (kv.Value != null && HasOwnSuspension(kv.Value)) return true;
                return false;
            case JsonArray a:
                foreach (var it in a) if (it != null && HasOwnSuspension(it)) return true;
                return false;
            default:
                return false;
        }
    }

    // #82 — like HasOwnSuspension, but ALSO descends into a `forEachInline` body. FlattenSuspendingLoops lifts a
    // forEachInline-body suspension into the ENCLOSING SM's CFG (a loop is not a separate SM scope), so for the try/
    // eval-order gate the suspension IS the enclosing fun's — HasOwnSuspension's LambdaKinds skip would hide it and
    // mis-account the try nesting (a nested-suspending-try bypass → branch-into-try → InvalidProgramException). Every
    // OTHER LambdaKinds node (genuine lambda/closure, repeatInline) stays skipped (its suspension is its own SM's).
    static bool HasLoopBorneSuspension(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                if (Str(o["k"]) is string k && k != "forEachInline" && LambdaKinds.Contains(k)) return false;
                if (o.ContainsKey("suspendCall") && Bool(o["suspendCall"])) return true;
                foreach (var kv in o) if (kv.Value != null && HasLoopBorneSuspension(kv.Value)) return true;
                return false;
            case JsonArray a:
                foreach (var it in a) if (it != null && HasLoopBorneSuspension(it)) return true;
                return false;
            default:
                return false;
        }
    }

    // The method NAME of the first suspending call in a subtree — the "…lives across the suspending call to `f`"
    // half of a storage diagnostic. Null when the subtree carries no suspension. "First" means first in
    // EVALUATION order (operands before the call that consumes them, receiver before arguments), not in JSON key
    // order, so the name the diagnostic prints is the one the reader reaches first in the source.
    static string SuspendedCalleeIn(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                if (Str(o["k"]) is string k && LambdaKinds.Contains(k)) return null;
                if (EvalOrderOf(o) is { } order)
                    foreach (var kid in order.Operands)
                        if (kid != null && SuspendedCalleeIn(kid) is string ec) return ec;
                if (Bool(o["suspendCall"])) return Str(o["method"]) ?? "a suspending call";
                foreach (var kv in o) if (kv.Value != null && SuspendedCalleeIn(kv.Value) is string c) return c;
                return null;
            case JsonArray a:
                foreach (var it in a) if (it != null && SuspendedCalleeIn(it) is string ac) return ac;
                return null;
            default:
                return null;
        }
    }

    // The node kinds that TRANSFER CONTROL out of the expression they sit in — read by EscapesExpression, which
    // flattens such a value out of a CLR operand slot while bir2cir still owns the control-flow decision.
    static readonly HashSet<string> ControlTransferKinds = new(StringComparer.Ordinal)
    {
        "goto", "brIf", "return", "returnExpr", "throw", "throwExpr",
    };

    // A call to dll2klib's metadata-only @ClrAwaitBridge declaration. The marker, rather than a
    // source name or owner, keeps user-authored suspend functions named `await` on the ordinary cold-call path.
    static bool IsAwaitMarkerCall(JsonObject o) =>
        Bool(o["suspendCall"]) && Bool(o["clrAwaitBridge"])
        && Str(o["k"]) is "callStatic" or "callInstance";

    // A literal `true` — the ONE argument shape `await(captureContext = …)` lowers as the plain capturing awaiter,
    // because that is what the awaitable's own `GetAwaiter()` already does. Everything else, constant or not, goes
    // through `ConfigureAwait(<the argument>)` (EmitAwaitPoint).
    static bool IsConstTrue(JsonObject o) =>
        Str(o["k"]) == "const" && o["value"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    // BUG 1: does the subtree contain a `try` whose finally is non-empty AND whose body spans a suspension?
    // Such a finally needs the $suspending gate (it would otherwise run on the suspend-return leave and again at
    // exit). SuspensionRefusalReason guarantees at most one such level (nested suspending try is left untransformed).
    static bool HasSuspendingFinally(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                if (Str(o["k"]) == "try" && o["finally"] is JsonArray fin && fin.Count > 0
                    && o["body"] != null && HasOwnSuspension(o["body"]))
                    return true;
                foreach (var kv in o) if (kv.Value != null && HasSuspendingFinally(kv.Value)) return true;
                return false;
            case JsonArray a:
                foreach (var it in a) if (it != null && HasSuspendingFinally(it)) return true;
                return false;
            default:
                return false;
        }
    }

    // --- per-fun code generation -----------------------------------------------------------------------

    sealed partial class FunGen
    {
        const string ThisField = "$this";
        // BUG 1 (try/finally across a suspension): a boolean SM field gating a suspending-try's finally. Set true
        // right before every `return COROUTINE_SUSPENDED`, reset false at the top of each invokeSuspend; the
        // finally runs its real body only when it is false — so it is SKIPPED on the suspend-return unwind (when
        // the CLR runs the finally on the `leave`) and RUNS EXACTLY ONCE on the post-resume normal/exception exit.
        // This mirrors the C#/JVM state-gated finally (a per-label finally-route table collapsed to one flag,
        // valid because SuspensionRefusalReason admits only a SINGLE level of suspending try).
        const string SuspendingField = "$suspending";
        // #10 — dll2klib's metadata-only @ClrAwaitBridge declaration. EmitAwaitPoint
        // resolves the .NET AWAITABLE PATTERN (GetAwaiter/awaiter/IsCompleted/GetResult/OnCompleted) for the marker's
        // receiver type from ref metadata (ReferenceMetadataIndex.ResolveAwaitable) — Task / ValueTask / a WinRT
        // IAsyncOperation<T> (extension GetAwaiter) / any custom awaitable — with ZERO per-type hardcode. The awaiter
        // FQNs (TaskAwaiter / ConfiguredTaskAwaitable+ConfiguredTaskAwaiter / ValueTaskAwaiter / …) come from the plan.
        const string ActionFqn = "System.Action";

        readonly JsonObject _m;
        readonly string _name;
        readonly string _fileClass;
        readonly string _ownerClass;             // enclosing class FQN for a member (instance OR static), else null
        readonly bool _isMember;                 // an INSTANCE member (owner != null && !static): carries a `$this` field
        readonly bool _staticMember;             // M3 — a `static` member (companion suspend fun): cold entry/bridge stay static in the class, no `$this`
        readonly bool _generated;                // implementation declaration (e.g. a materialized local suspend fun)
        // R1 classifier — non-null when this concrete member cannot be segmented (a v1-unsupported suspension
        // position, or M4 own-generic-on-generic-class): the cold entry becomes a call-time `throw NotSupportedException`
        // carrying this reason (design §11 policy). null for a segmentable or an abstract member.
        readonly string _stubReason;
        readonly Dictionary<string, TypeNode> _calleeRet;
        readonly bool _baseIsLocal;
        // The public Task<R> bridge BCL owners (from the ref.dll @ClrTypeAlias index); null -> no bridge (see ApplyAll).
        readonly string _tcsBcl;
        readonly string _taskBcl;
        readonly List<string> _ownerTypeParams;   // enclosing class type-param names (instance member on a generic class)
        readonly JsonArray _ownerTypeParamDecls;  // the same declarations with their real Kotlin constraints
        readonly List<string> _smAllTps;           // owner + method type-param names (the SM's own generic params)
        readonly TypeNode _selfType;               // constructed self `Box<T>` (instance member), else _ownerClass/null
        readonly bool _memberAbstract;             // source member is `abstract` -> abstract cold entry, no SM
        readonly bool _memberOverride;             // source member is `override` -> override cold entry (fills base slot)
        readonly bool _memberVirtual;              // source member is `open` -> virtual cold entry (new vtable slot)
        // Closure-class registry (name -> type node) for the suspendCoroutine intrinsic inliner. Empty in lambda mode.
        readonly IReadOnlyDictionary<string, JsonObject> _closures;
        // Top-level generated-method registry (name -> method) for a cross-module suspendCoroutine's non-capturing
        // `newDelegate` block body (F2). Empty in lambda mode.
        readonly IReadOnlyDictionary<string, JsonObject> _lambdaMethods;
        // Lambda mode (bundle-6 P3 wave-2b Part B): a suspend LAMBDA SM (extends SuspendLambda, no cold
        // entry/main-drain, adds the create() override protocol). Left at defaults for the named-fun path.
        readonly bool _isLambda;
        readonly bool _restrictedBase;           // lambda mode: receiver is @RestrictsSuspension -> RestrictedSuspendLambda base
        readonly int _arity;                     // the lambda's own param count (v1: 0 or 1)
        readonly List<(string name, TypeNode type)> _captures;   // captured vars -> ctor params + fields
        readonly JsonArray _lambdaBody;          // the lambda's structured body (no `_m` in lambda mode)
        readonly string _smType;                 // bare SM type name
        readonly TypeNode _smTypeInst;           // instantiated (`f$sm<T>`) or bare when non-generic
        readonly string _coldName;
        readonly TypeNode _resultType;           // Kotlin resultType, OUTER `?` stripped (VoidTn for Unit)
        readonly TypeNode _taskResultType;       // explicitly selected physical Task<T> result, else _resultType
        readonly bool _resultNullable;           // the suspend fn's result had an outer `?` (#37/#48: read off the type node)
        readonly string _resultNullableGeneric;  // #86: the PRE-erasure `T?` result, for the bridge's carrier (else null)
        readonly List<JsonObject> _params;       // original params (extension: leading __self)
        readonly List<string> _typeParams;       // generic type-param names ([] when non-generic)
        readonly JsonArray _methodTypeParamDecls; // original names + constraints for emitted bridge/cold signatures
        // Kotlin override ownership is the proof used by the late CLR slot normalizer. Suspend lowering replaces one
        // logical declaration with TWO physical declarations, so both retain that proof under their final names.
        readonly JsonArray _overrideMarkers;
        // Some suspend declarations are already compiler-authored physical MethodImpl bodies (for example the
        // forwarding method from G<T> to its existential G<*> interface). Suspend lowering replaces one MethodDef
        // with a cold entry and a Task bridge, so the physical role and its exact declaration descriptors must be
        // transformed onto both outputs. Dropping them forces ilemit to rediscover the relation from names/hierarchy.
        readonly bool _physicalSlotBridge;
        readonly bool _clrInterfaceSlotBridge;
        readonly string _physicalSlotVisibility;
        readonly JsonArray _clrInterfaceImpls;
        readonly JsonArray _clrBaseImpls;
        // A companion suspend extension retains both its receiver association and Kotlin source name after the
        // original method is replaced by the public Task bridge. RoundtripMetadata consumes both facts there.
        readonly string _companionReceiver;
        readonly string _companionSourceName;
        readonly string _companionMemberKind;
        readonly string _declarationId;
        readonly string _declarationSourceName;
        readonly string _explicitClrName;
        readonly HashSet<string> _fields = new(StringComparer.Ordinal);
        readonly List<(string name, TypeNode type)> _fieldDecls = new();
        // Declared type of every `{k:var}` of this body, INCLUDING the ones the storage gate leaves as MoveNext
        // locals — so a lexical type lookup does not depend on a name having been promoted to a field.
        readonly Dictionary<string, TypeNode> _localTypes = new(StringComparer.Ordinal);
        // Synthesized SM methods for each `task.await()` suspension point (the OnCompleted Action callback
        // that re-drives THIS SM via resumeWith). Populated during body emission; spliced into the SM type.
        readonly List<JsonObject> _awaitResumeMethods = new();

        int _state;                              // resume-state counter (>=1)
        int _label;                              // label id allocator (above kotc's low ids)
        int _condCounter;
        int _loopCounter;                        // #82 — fresh splice-loop temp counter (FlattenSuspendingLoops)
        int _excCounter;                         // #78 — fresh catch-hoist capture-var counter (HoistSuspendingCatches)
        int _awaitTarget;                        // #64 — fresh `__awaitable$n` field counter (EmitAwaitPoint's receiver bind)
        bool _needSuspendGuard;                  // fun has a suspending try/finally -> emit the $suspending gate (BUG 1)
        readonly List<(int state, int label)> _dispatch = new();
        readonly Stack<(List<(int state, int label)> inner, int tryEntry)> _tryStack = new();

        public FunGen(JsonObject m, string name, string fileClass, string ownerClass,
            Dictionary<string, TypeNode> calleeRet, bool baseIsLocal, string tcsBcl = null, string taskBcl = null,
            JsonArray ownerTypeParamDecls = null, IReadOnlyDictionary<string, JsonObject> closures = null,
            string smNameSuffix = "",
            IReadOnlyDictionary<string, JsonObject> lambdaMethods = null,
            bool ownerIsInterface = false, bool staticMember = false)
        {
            _m = m; _name = name; _fileClass = fileClass; _ownerClass = ownerClass;
            _declarationId = Str(m[DeclarationIdentityBinding.Key]);
            _declarationSourceName = Str(m["declarationSourceName"]);
            _explicitClrName = Str(m[DeclarationIdentityBinding.ExplicitNameKey]);
            _staticMember = staticMember;
            _generated = Bool(m["generated"]);
            _isMember = ownerClass != null && !staticMember;   // an INSTANCE member (static/companion members are top-level-shaped)
            _calleeRet = calleeRet; _baseIsLocal = baseIsLocal;
            _tcsBcl = tcsBcl; _taskBcl = taskBcl;
            _closures = closures ?? new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            _lambdaMethods = lambdaMethods ?? new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            _ownerTypeParamDecls = ownerTypeParamDecls?.DeepClone() as JsonArray ?? new JsonArray();
            _ownerTypeParams = ReadTypeParamNames(_ownerTypeParamDecls);
            // Virtuality of the source member (kept in lockstep on the cold entry). An interface declaration with no
            // body is abstract even when kotc omitted the explicit abstract bit; a concrete DIM remains concrete.
            var interfaceAbstract = ownerIsInterface
                && (m["body"] is not JsonArray interfaceBody || interfaceBody.Count == 0);
            _memberAbstract = _isMember && (Bool(m["abstract"]) || interfaceAbstract);
            _memberOverride = _isMember && Bool(m["override"]);
            _memberVirtual = _isMember && Bool(m["virtual"]);
            _smType = (ownerClass ?? fileClass) + "_" + name + smNameSuffix + "$sm";
            // Exact-signature cold collisions are allocated later from the frontend declaration identity, just like
            // their hot Task bridges. Pre-mangling by traversal order would partition the collision set before the
            // common allocator sees it and make the cold ABI source-order dependent.
            // The cold entry is a generated projection of the public declaration, so its role suffix is applied to
            // the explicitly selected physical base name. This is not collision repair: two unresolved cold shapes
            // still fail the final MethodDef check instead of receiving a hash or traversal-order suffix.
            _coldName = (_explicitClrName ?? name) + "$dotkt_suspend";
            // #37/#48: the result nullability now rides the `suspendRet` TYPE NODE (`{t:nullable,of:R}`), not a retired
            // scalar `retNullable` flag. Strip the outer `?` so `_resultType` is the bare R (as it always was for the
            // reference case) and record it in `_resultNullable` for the Task-bridge NRT walk.
            var suspendRetRaw = TypeJson.Read(m["suspendRet"]);
            // #86: a `suspend fun <T> f(): T?` had its result object-erased before this pass ran, so `suspendRet`
            // reads a bare `object` and the outer `?` is no longer visible on it. The pre-erasure result was stashed
            // by NullableGenericErasure; it restores BOTH channels the bridge return needs — the NRT byte (below,
            // via `_resultNullable`) and the Kotlin type itself (the carrier). Without them the bridge's `Task<object>`
            // return re-imports as a non-null `Any` and a cross-module consumer stops compiling.
            _resultNullableGeneric = (m["nullableGenericSuspendRet"] as JsonValue)?.GetValue<string>();
            _resultNullable = suspendRetRaw is TypeNode.Nullable || _resultNullableGeneric != null;
            _resultType = (suspendRetRaw is TypeNode.Nullable srn ? srn.Of : suspendRetRaw) ?? VoidTn;
            _taskResultType = TypeJson.Read(m[KotlinPropertyAccessors.SuspendTaskResultKey]) ?? _resultType;
            _params = (m["params"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            _typeParams = ReadTypeParamNames(m["typeParams"]);
            _methodTypeParamDecls = (m["typeParams"] as JsonArray)?.DeepClone() as JsonArray ?? new JsonArray();
            _overrideMarkers = (m["overrides"] as JsonArray)?.DeepClone() as JsonArray ?? new JsonArray();
            _physicalSlotBridge = Bool(m[KotlinPropertyAccessors.PhysicalSlotBridgeKey]);
            _clrInterfaceSlotBridge = Bool(m[KotlinPropertyAccessors.ClrInterfaceSlotBridgeKey]);
            _physicalSlotVisibility = Str(m["vis"]);
            _clrInterfaceImpls = (m["clrInterfaceImpls"] as JsonArray)?.DeepClone() as JsonArray;
            _clrBaseImpls = (m["clrBaseImpls"] as JsonArray)?.DeepClone() as JsonArray;
            _companionReceiver = (m["companionReceiver"] as JsonValue)?.GetValue<string>();
            _companionSourceName = (m["companionSourceName"] as JsonValue)?.GetValue<string>();
            _companionMemberKind = (m["companionMemberKind"] as JsonValue)?.GetValue<string>();
            // The SM is generic over the ENCLOSING class's type params (an instance member on a generic class) PLUS
            // the member's own — its fields / `$this` / label reference them (type-scope tv by flattened position).
            _smAllTps = new List<string>(_ownerTypeParams);
            // Generic frames concatenate by declaration slot, never by source name. Kotlin permits a method parameter
            // to shadow an owner's parameter name; the CLR TypeDef must still carry both physical slots.
            _smAllTps.AddRange(_typeParams);
            _selfType = _ownerClass == null ? null
                : _ownerTypeParams.Count == 0 ? new TypeNode.Fqn(_ownerClass)
                : new TypeNode.Fqn(_ownerClass, TypeTvs(_ownerTypeParams.Count));
            _smTypeInst = _smAllTps.Count == 0 ? new TypeNode.Fqn(_smType) : new TypeNode.Fqn(_smType, TypeTvs(_smAllTps.Count));
            // R1 classifier — decide the segmentable-vs-call-time-throw shape for a CONCRETE member (an abstract
            // member has no body and is handled by the `_memberAbstract` branch). M4: a member generic on its OWN
            // type params AND on a generic class needs the SM to thread the union of both param lists (deferred v1).
            // Otherwise the body must have every suspension in a segmentable position (SuspensionRefusalReason).
            if (!_memberAbstract)
            {
                if (_typeParams.Count > 0 && _ownerTypeParams.Count > 0 && !_staticMember)
                    _stubReason = "a generic suspend method on a generic class (v1)";
                else if ((m["body"] as JsonArray) is JsonArray b0)
                    _stubReason = SuspensionRefusalReason(b0, inHandler: false, tryDepth: 0);
            }
        }

        // The first `n` type-scope generic params by flattened index (Tv{type,0..n-1}).
        static TypeNode[] TypeTvs(int n) => Enumerable.Range(0, n).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray();

        // `_smTypeInst` is the SM as seen from inside the synthesized type, where all of its generic parameters are
        // type-scope (`!i`).  The cold entry is still the original generic method, however, so its own parameters
        // remain method-scope (`!!i`) while any enclosing-class parameters remain type-scope.  Keeping those two
        // lexical views distinct is essential now that local constructor linkage is an exact descriptor match:
        // spelling the cold entry's `new SM<T>` with `!i` made its owner disagree with its `!!i` argument signature
        // and previously survived only because ilemit selected a constructor by arity.
        TypeNode ColdEntrySmTypeInst()
        {
            if (_smAllTps.Count == 0) return new TypeNode.Fqn(_smType);
            var args = new List<TypeNode>();
            for (var i = 0; i < _ownerTypeParams.Count; i++) args.Add(new TypeNode.Tv("type", i));
            for (var i = 0; i < _typeParams.Count; i++) args.Add(new TypeNode.Tv("method", i));
            return new TypeNode.Fqn(_smType, args.ToArray());
        }

        // A named suspend method's executable body moves from a generic METHOD into a synthesized generic TYPE.
        // Every lexical `!!i` in that moved declaration (including nested newSam/newClosure type arguments) must
        // therefore become the SM's `!ownerArity+i`. The same move applies to the method type-parameter constraints:
        // without carrying `C : MutableCollection<T>` onto the SM, constructing a nested `Sam<T,C>` is rejected by
        // the CLR even when the eventual closed C satisfies the Kotlin constraint.
        static void RebindMethodTypeVariablesToSm(JsonNode node, int ownerArity)
        {
            switch (node)
            {
                case JsonObject o:
                    if (Str(o["t"]) == "tv" && Str(o["scope"]) == "method"
                        && o["i"] is JsonValue iv && iv.TryGetValue<int>(out var index))
                    {
                        o["scope"] = "type";
                        o["i"] = ownerArity + index;
                        return;
                    }
                    var kind = Str(o["k"]);
                    foreach (var kv in o)
                    {
                        // These arrays describe the CALLEE's generic signature (`!!i` belongs to the called method),
                        // not a lexical type used by the moved caller body. Re-scoping them would make overload tokens
                        // refer to an unrelated SM type parameter and also defeat later type-argument substitution.
                        // A constructor cannot declare method generic parameters, however, so a `new` node's
                        // `argTypes` method variables necessarily belong to the caller's lexical scope and move with
                        // the body. This matters for a nested generic suspend lambda: its construction is inserted
                        // into the outer SM before this walk rebases the outer method variables.
                        if (kv.Key is "sig" or "resolvedMemberParams"
                            || (kv.Key == "argTypes" && kind != "new")) continue;
                        if (kv.Value != null) RebindMethodTypeVariablesToSm(kv.Value, ownerArity);
                    }
                    break;
                case JsonArray a:
                    foreach (var item in a)
                        if (item != null) RebindMethodTypeVariablesToSm(item, ownerArity);
                    break;
            }
        }

        // Lambda-mode ctor (Part B). Builds a `<smName> : SuspendLambda` SM from a newSuspendLambda node's
        // parts. Captures become ctor params + fields; the lambda's own params become fields set by create().
        public FunGen(string smName, int arity, List<(string name, TypeNode type)> captures,
            List<JsonObject> lambdaParams, JsonArray body, TypeNode resultType, List<string> typeParams,
            JsonArray typeParamDecls, int ownerTypeParamCount, Dictionary<string, TypeNode> calleeRet,
            bool baseIsLocal, bool restricted = false)
        {
            _isLambda = true;
            _restrictedBase = restricted;
            var allTypeParamDecls = typeParamDecls?.DeepClone() as JsonArray
                ?? new JsonArray((typeParams ?? new List<string>())
                    .Select(n => (JsonNode)JsonValue.Create(n)).ToArray());
            if (ownerTypeParamCount < 0 || ownerTypeParamCount > allTypeParamDecls.Count)
                throw new InvalidOperationException(
                    $"suspend lambda '{smName}' declares {allTypeParamDecls.Count} generic slot(s) but " +
                    $"claims {ownerTypeParamCount} owner capture slot(s)");
            _ownerTypeParamDecls = new JsonArray(allTypeParamDecls.Take(ownerTypeParamCount)
                .Select(p => p?.DeepClone()).ToArray());
            _ownerTypeParams = ReadTypeParamNames(_ownerTypeParamDecls);
            _closures = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            _lambdaMethods = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            _m = null;
            _arity = arity;
            _captures = captures;
            _lambdaBody = body;
            _name = smName;
            _fileClass = null;
            _ownerClass = null;
            _isMember = false;
            _calleeRet = calleeRet;
            _baseIsLocal = baseIsLocal;
            _smType = smName;
            _coldName = null;
            _resultNullable = resultType is TypeNode.Nullable;
            _resultType = (resultType is TypeNode.Nullable lrn ? lrn.Of : resultType) ?? VoidTn;
            _params = lambdaParams ?? new List<JsonObject>();
            _methodTypeParamDecls = new JsonArray(allTypeParamDecls.Skip(ownerTypeParamCount)
                .Select(p => p?.DeepClone()).ToArray());
            _typeParams = ReadTypeParamNames(_methodTypeParamDecls);
            _overrideMarkers = new JsonArray();
            _companionReceiver = null;
            _companionSourceName = null;
            _companionMemberKind = null;
            _smAllTps = new List<string>(_ownerTypeParams);
            _smAllTps.AddRange(_typeParams);
            _smTypeInst = _smAllTps.Count == 0 ? new TypeNode.Fqn(_smType) : new TypeNode.Fqn(_smType, TypeTvs(_smAllTps.Count));
            // #125 — lambda SMs share the named-fun segmentability classifier. A refused lambda still gets a valid
            // SuspendLambda type, but invokeSuspend becomes a call-time NotSupportedException stub (Build), never a
            // body containing an unsegmented suspendCall / invalid IL.
            _stubReason = SuspensionRefusalReason(body, inHandler: false, tryDepth: 0);
        }

        static List<string> ReadTypeParamNames(JsonNode tps)
        {
            var names = new List<string>();
            if (tps is JsonArray a)
                foreach (var t in a)
                    if (t is JsonValue v && v.TryGetValue<string>(out var s)) names.Add(s);
                    else if (t is JsonObject o && Str(o["name"]) is string n) names.Add(n);
            return names;
        }

        int NextLabel() => ++_label;

        public void Build(List<JsonNode> newMethods, List<JsonNode> newTypes)
        {
            CheckSuspendAbi();
            if (_memberAbstract)
            {
                // An abstract suspend member -> the abstract cold-entry DECLARATION (no SM, no drain). Concrete
                // overrides fill the slot; a Kotlin virtual `scope.yield(x)` dispatches through it.
                newMethods.Add(ColdEntryAbstract());
                // BUG 3 (interface/abstract suspend round-trip): also emit the ABSTRACT Task<T> bridge SIGNATURE so the
                // member carries the [KotlinFunction(Suspend)] trigger (via suspendBridge) — else dll2klib sees only the
                // object-returning `$dotkt_suspend` cold entry and cannot restore `suspend fun` on a re-consuming Kotlin.
                // Concrete overrides emit the matching override bridge (below), filling this abstract slot.
                if (WantsBridge) newMethods.Add(BuildBridge());
                return;
            }

            // R1 — a concrete but NOT-segmentable member (a v1-unsupported suspension position, or M4
            // own-generic-on-generic-class) still gets its cold-entry + bridge slot UNCONDITIONALLY, with a
            // CALL-TIME throw body (design §11: "call-time NotSupportedException, never an emit crash"). Both call
            // paths observe the throw: a Kotlin->Kotlin cold call propagates it synchronously; the public Task
            // bridge catches it (its try/catch) and faults the Task. Warn once here, naming the fun + the root
            // refusal (this REPLACES the deleted fixpoint's L3 drop warning).
            if (_stubReason is string reason)
            {
                if (_isLambda)
                {
                    Console.Error.WriteLine(
                        $"bir2cir: WARNING suspend-lowering: suspend lambda '{_smType}' is not segmentable "
                        + $"({reason}) — emitting a call-time-throw SuspendLambda state machine (v1 limitation). "
                        + "The lambda COMPILES; invoking it throws NotSupportedException.");
                    // SmTypeLambda's ctor/create protocol still needs capture + lambda-param fields even though the
                    // stub invokeSuspend never reads them — the fields are REAL on the emitted class, so they face
                    // the same storage gate as a segmentable lambda's.
                    foreach (var (n, t) in _captures) FieldStorage(n, t, RoleCapture, lives: true, across: null);
                    foreach (var p in _params)
                        FieldStorage(Str(p["name"]), TypeJson.Read(p["type"]), RoleParam, lives: true, across: null);
                    var msg = $"{_smType}: {reason} — this suspend lambda is not supported by bir2cir's v1 "
                        + "cold-lowering (docs/design-coroutine-cold-core-task-bridge.md §11/§14).";
                    newTypes.Add(SmTypeLambda(UnsupportedThrowBody(msg)));
                    return;
                }
                Console.Error.WriteLine(
                    $"bir2cir: WARNING suspend-lowering: '{(_ownerClass ?? _fileClass)}.{_name}' is not segmentable "
                    + $"({reason}) — emitting a call-time-throw cold entry (v1 limitation). The suspend fun COMPILES; "
                    + "invoking it throws NotSupportedException.");
                newMethods.Add(ColdEntryStub(reason));
                if (_name == "main" && _ownerClass == null) newMethods.Add(DrainMain());
                if (WantsBridge) newMethods.Add(BuildBridge());
                return;
            }

            var body = _isLambda ? _lambdaBody : ((_m["body"] as JsonArray) ?? new JsonArray());
            var hasSuspension = HasSuspension(body);
            // #78/#82/#98 — normalize a suspending body BEFORE segmentation so a suspension in a POSITION the straight-line
            // SM cannot segment is lifted into one it can: a structured loop whose body spans a suspension is
            // flattened to label/brIf/goto CFG (FlattenSuspendingLoops — its implicit loop vars become real `{k:var}` so
            // the storage gate spills the ones that survive a resume), and a suspending catch handler is hoisted OUT
            // of the CLR catch clause (a
            // `leave`-in / resume-into a catch is illegal IL) into gated straight-line code (HoistSuspendingCatches).
            // Both clone-on-change (never mutate the shared/retained rt-stdlib original) and skip nested lambda scopes.
            // They allocate fresh label ids via NextLabel(), so seed the allocator first.
            if (hasSuspension)
            {
                _label = MaxLabelId(body) + 1000;
                body = FlattenSuspendingLoops(body);
                body = HoistSuspendingCatches(body);
            }
            // BUG 1: does the body contain a try whose finally spans a suspension? If so, the finally must be
            // gated (see SuspendingField) so it does not run on the suspend-return unwind and re-run at exit.
            // Computed POST-flatten so a suspension that was inside a (now-flattened) loop is visible here.
            _needSuspendGuard = hasSuspension && HasSuspendingFinally(body);

            if (!_isLambda && !hasSuspension)
            {
                // No suspension point: the cold entry IS the body directly (extra unused completion param,
                // Any? return so a value return boxes). No SM needed. For an instance member the cold entry
                // stays an INSTANCE method on the class, so a `this`/receiver in the body remains valid.
                // (A suspend LAMBDA always becomes an SM even without suspension — its VALUE is the SM instance.)
                newMethods.Add(ColdEntryDirect(body));
                if (_name == "main" && _ownerClass == null) newMethods.Add(DrainMain());
                if (WantsBridge) newMethods.Add(BuildBridge());
                return;
            }

            // A no-suspension LAMBDA still becomes an SM (its VALUE is the SM instance) but skipped the flatten/hoist
            // block above — seed the label allocator here.
            if (!hasSuspension) _label = MaxLabelId(body) + 1000;

            // Which of this body's locals actually survive a suspension. Computed on the NORMALIZED body (the
            // flatten/hoist above already ran) and BEFORE any storage decision, because it IS the storage
            // decision for every `{k:var}`: a local that is dead across every suspension point stays a MoveNext
            // local, which is both cheaper and the only way a byref-like value can live in a suspend function.
            var live = SuspendLiveness.Analyze(body);

            FieldStorage("label", IntTn, RoleMachinery, lives: true, across: null);
            if (_needSuspendGuard) FieldStorage(SuspendingField, BoolTn, RoleMachinery, true, null);   // BUG 1: the finally gate flag
            if (_isMember) FieldStorage(ThisField, _selfType, RoleMachinery, true, null);   // holds the enclosing (constructed) instance
            if (_isLambda)
                foreach (var (n, t) in _captures)
                    FieldStorage(n, t, RoleCapture, lives: true, across: null);             // captured vars -> ctor-set fields
            foreach (var p in _params)
                FieldStorage(Str(p["name"]), RequiredParamType(p), RoleParam, true, null);  // lambda: create()-set param field(s)
            foreach (var (vn, vt, role) in live.DeclaredVars)
            {
                var declared = TypeJson.Read(vt);
                if (vn != null) _localTypes[vn] = declared;
                // A call-evaluation plan binding names itself ("the receiver of `copy`", "the default of parameter
                // `b`"); an ordinary local is just a local. Either way the refusal below reads the SAME sentence.
                FieldStorage(vn, declared, role ?? RoleLocal,
                    live.LivesAcrossSuspension(vn), live.FirstSuspensionAcross(vn), roleNamesIt: role != null);
            }

            var bodyOut = new List<JsonNode>();
            foreach (var s in body) EmitStmt(s, bodyOut);
            if (IsUnitTn(_resultType))
                bodyOut.Add(Ret(NullConst(AnyTn)));

            var invoke = new JsonArray();
            // BUG 1: reset the finally gate at every entry (first call + each resume) BEFORE the label dispatch,
            // so a finally reached on the normal/exception path runs its real body; the suspend-return path sets
            // it true just before returning SUSPENDED (see the EmitSuspensionPoint/EmitAwaitPoint sites).
            if (_needSuspendGuard) invoke.Add(SetField(SuspendingField, BoolConst(false)));
            foreach (var (state, label) in _dispatch)
                invoke.Add(BrIf(BinEq(FieldOf("label", IntTn), IntConst(state)), true, label));
            foreach (var st in bodyOut) invoke.Add(st);

            // #82 — tripwire: every `{k:local}`/`{k:setLocal}` in the emitted invokeSuspend must resolve to the `result`
            // param, an SM `{k:var}`, a catch/loop var, or (as a `{k:field}`) an SM field. A residual bare local that was
            // NOT spilled (a splice-generated local crossing a resume, the #82 root) would reach ilemit as `load unknown
            // var` — surface it HERE as a bir2cir build error naming the SM/fun/local instead.
            AssertLocalsResolved(invoke);

            if (_isLambda)
            {
                newTypes.Add(SmTypeLambda(invoke));
                return;
            }

            newTypes.Add(SmType(invoke));
            newMethods.Add(ColdEntrySm());
            if (_name == "main" && _ownerClass == null) newMethods.Add(DrainMain());
            if (WantsBridge) newMethods.Add(BuildBridge());
        }

        // A named (non-lambda), non-`main` suspend fun gets a public Task<R> bridge. `main` is excluded (it is the
        // entry point, drained by the synthesized plain `main`).
        // EXCLUDED too:
        //  - `_baseIsLocal` (the rt-STDLIB build): the stdlib's own suspend members
        //    (yield/yieldAll/callRecursive) are internal machinery, not C#-facing Task APIs; the bridge is an
        //    APP-build concern (consumers of the dll).
        // A virtual/abstract/override member DOES get a bridge (BUG 3): the bridge's virtuality rides in lockstep with
        // the cold entry (abstract -> an abstract signature; open -> virtual; override -> override), so an interface
        // `suspend fun` round-trips (its [KotlinFunction(Suspend)] trigger lives on the bridge) and its concrete
        // overrides fill both the bridge and the cold-entry slots.
        // The compile-reference validation in ApplyAll makes the Task ABI mandatory: every non-main non-lambda cold
        // entry in an app build gets the public Task bridge. rt-stdlib self-builds keep no bridge.
        bool WantsBridge => !_isLambda && _name != "main" && !_baseIsLocal;
        public bool NeedsRootContinuation =>
            !_memberAbstract
            && ((_name == "main" && _ownerClass == null) || WantsBridge);

        static int MaxLabelId(JsonNode node)
        {
            int max = 0;
            void Walk(JsonNode n)
            {
                if (n is JsonObject o)
                {
                    var k = Str(o["k"]);
                    if ((k == "label" || k == "goto" || k == "brIf") && o["id"] is JsonValue v && v.TryGetValue<int>(out var id))
                        max = Math.Max(max, id);
                    foreach (var kv in o) if (kv.Value != null) Walk(kv.Value);
                }
                else if (n is JsonArray a) foreach (var it in a) if (it != null) Walk(it);
            }
            Walk(node);
            return max;
        }

        // ---- the storage gate ------------------------------------------------------------------------------
        //
        // The SINGLE decision "does this value get an SM instance field, a MoveNext local, or a compile error"
        // (docs/dotkt-semantics.md §7.1). Every field this state machine mints goes through here — spilled
        // locals, parameters, captures, `$this`/`label`/`$suspending`, and the synthesized `__aw$`/`__cond$`/
        // `__awaiter$` temporaries alike:
        //
        //   lives == false                        -> stays a MoveNext LOCAL (a byref-like value is legal there)
        //   lives == true,  field-legal type      -> an SM instance field
        //   lives == true,  byref-like / `ref T`  -> a compile-time diagnostic (the CS4007/CS4012 mirrors)
        //
        // A parameter, a capture and the machinery slots are unconditional storage (the SM ctor/create writes
        // them), so they pass lives:true with a null `across`; a local's verdict comes from SuspendLiveness,
        // which also names the first suspending callee it lives across for the diagnostic.
        // Roles are the source-level words the diagnostic uses.
        const string RoleLocal = "local variable";
        const string RoleParam = "parameter";
        const string RoleCapture = "captured variable";
        const string RoleMachinery = "state-machine slot";
        const string RoleAwaited = "awaited value";
        const string RoleCondResult = "conditional-result temporary";
        const string RoleReturn = "result";

        // `roleNamesIt` = the role already identifies the value in source terms (a call-evaluation plan binding says
        // "the argument 'x' of `f`"), so the diagnostic drops the emitted name rather than printing a minted
        // `cir$b1` the author never wrote. An ordinary local's role is a category, and the name is what tells two of
        // them apart, so it is kept.
        void FieldStorage(string name, TypeNode type, string role, bool lives, string across, bool roleNamesIt = false)
        {
            if (name == null || !lives) return;                  // dead across every suspension -> stays a local
            var why = FieldLegality.Classify(type, IsByRefLikeFqn, out var offending);
            if (why != FieldRejection.None)
                throw new NotSupportedException(FieldLegality.SuspendMessage(
                    FieldLegality.PosPrefix(_m), DiagOwner, role, roleNamesIt ? null : name, type, offending, why, across));
            if (_fields.Add(name)) _fieldDecls.Add((name, type ?? throw new NotSupportedException(
                $"bir2cir: {FieldLegality.PosPrefix(_m)}suspend-lowering: in `{DiagOwner}`, the {role} "
                + $"`{name}` lives across a suspension but carries no static type, so its state-machine field would "
                + "be untyped — an earlier lowering dropped the type.")));
        }

        // The suspend ABI itself, checked for EVERY suspend declaration — abstract, stubbed, suspension-free or
        // fully segmented alike. A parameter and a capture are written by the state machine's constructor, and the
        // result crosses the cold entry's `Any?` slot and the public `Task<R>` bridge, so none of them can carry a
        // byref-like value whatever the body does. (Only LOCALS get the liveness question; the ABI has no "dead
        // across" escape.) Unconditional, exactly like C#'s CS4012 — and it is what turns a suspension-free
        // `suspend fun f(s: Span<Int>)` from an InvalidProgramException at call time into a compile-time message.
        void CheckSuspendAbi()
        {
            void Check(string role, string name, TypeNode t)
            {
                var why = FieldLegality.Classify(t, IsByRefLikeFqn, out var offending);
                if (why != FieldRejection.None)
                    throw new NotSupportedException(FieldLegality.SuspendAbiMessage(
                        FieldLegality.PosPrefix(_m), DiagOwner, role, name, t, offending, why));
            }
            foreach (var p in _params) Check(RoleParam, Str(p["name"]), RequiredParamType(p));
            if (_captures != null) foreach (var (n, t) in _captures) Check(RoleCapture, n, t);
            Check(RoleReturn, "<result>", _resultType);
        }

        // The referenced-metadata `ref struct` oracle. Null refs (a unit-test/lambda path with no reference set)
        // answers "not byref-like": a byref-like value cannot exist without a reference assembly declaring it.
        static bool IsByRefLikeFqn(string fqn) => _refs != null && _refs.IsByRefLikeFqn(fqn);

        // How a diagnostic names the thing being compiled.
        string DiagOwner => _isLambda ? _smType : (_ownerClass ?? _fileClass) + "." + _name;

        // THE DECLARED TYPE of a `{k:var}` this lowering re-emits — as a MoveNext local or as an SM field. Mandatory,
        // and an ERROR when absent: `kotlin.Any` is not a lesser slot, it BOXES a value type and hides a type the CLR
        // would refuse as a field, so a silently-`Any` local is a miscompile waiting for a value type. Every lowering
        // that mints a `var` stamps its type — the call-evaluation plan from the binding, the inline splice from the
        // callee's closed return type and parameter types — so a var without one means an earlier layer DROPPED it,
        // which is the drop to fix rather than to compensate for (docs/dotkt-semantics.md §7.1).
        TypeNode VarType(JsonObject v) =>
            TypeJson.Read(v["type"]) ?? throw new NotSupportedException(
                $"bir2cir: {FieldLegality.PosPrefix(_m)}suspend-lowering: in `{DiagOwner}`, the local "
                + $"`{Str(v["name"])}` is declared with no type, so the slot that holds it across a suspension would "
                + "be untyped — an earlier lowering dropped the type.");

        TypeNode RequiredParamType(JsonObject p) =>
            TypeJson.Read(p["type"]) ?? throw new NotSupportedException(
                $"bir2cir: {FieldLegality.PosPrefix(_m)}suspend-lowering: in `{DiagOwner}`, parameter "
                + $"`{Str(p["name"])}` carries no static type — an earlier lowering dropped it.");

        // A `{k:var}` that the storage gate left as a MoveNext LOCAL.
        JsonObject LocalVar(JsonObject v, JsonNode init) => new()
        {
            ["k"] = "var",
            ["name"] = Str(v["name"]),
            ["type"] = Tw(VarType(v)),
            ["init"] = init,
        };

        // ---- statement lowering ----

        void EmitStmt(JsonNode stmt, List<JsonNode> outp)
        {
            if (stmt is not JsonObject o) return;
            switch (Str(o["k"]))
            {
                case "var":
                    {
                        var nm = Str(o["name"]);
                        var init = o["init"];
                        // An init-less `var` (e.g. kotc's tryExpr `var dotkt_tryvalN`, assigned in the try/catch) default-inits:
                        // `{k:default}` is the zero value for BOTH a reference (ldnull) and a VALUE type (initobj) — a bare
                        // NullConst(valueType) would emit a null Int32 (ilemit: "requires Number, target is Null").
                        var declared = VarType(o);
                        var val = init == null ? DefaultOf(declared) : Rewrite(init, outp, declared);
                        if (_fields.Contains(nm)) outp.Add(SetField(nm, val));
                        else outp.Add(LocalVar(o, val));
                        break;
                    }
                case "setLocal":
                    {
                        var nm = Str(o["name"]);
                        var val = Rewrite(o["value"], outp, RequiredSlotType(nm));
                        if (_fields.Contains(nm)) outp.Add(SetField(nm, val));
                        else outp.Add(new JsonObject { ["k"] = "setLocal", ["name"] = nm, ["value"] = val });
                        break;
                    }
                case "return":
                    {
                        var v = o["value"];
                        outp.Add(v == null ? Ret(NullConst(AnyTn))
                            : Ret(Rewrite(v, outp, IsUnitTn(_resultType) ? UnitTn : _resultType)));
                        break;
                    }
                case "exprStmt":
                    outp.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = Rewrite(o["expr"], outp, UnitTn) });
                    break;
                case "block":
                    if (o["body"] is JsonArray bb) foreach (var s in bb) EmitStmt(s, outp);
                    break;
                case "label":
                case "goto":
                    outp.Add(o.DeepClone());
                    break;
                case "brIf":
                    outp.Add(new JsonObject
                    {
                        ["k"] = "brIf",
                        ["id"] = o["id"]?.DeepClone(),
                        ["on"] = o["on"]?.DeepClone(),
                        ["cond"] = Rewrite(o["cond"], outp, BoolTn),
                    });
                    break;
                case "try":
                    EmitTry(o, outp);
                    break;
                default:
                    outp.Add(Rewrite(o, outp));
                    break;
            }
        }

        void EmitTry(JsonObject o, List<JsonNode> outp)
        {
            var bodyHasSusp = o["body"] != null && HasOwnSuspension(o["body"]);
            if (!bodyHasSusp)
            {
                outp.Add(RewriteTryPlain(o));
                return;
            }
            var tryEntry = NextLabel();
            outp.Add(Label(tryEntry));
            // A label does not occupy an IL offset. If the next CIR statement is the try itself, Reflection.Emit
            // gives the label and the protected region the same offset; a resume dispatch from outside then becomes
            // an illegal branch INTO the try (the CLR JIT can crash before producing InvalidProgramException).
            // Materialize a semantically inert instruction at the bir2cir-authored boundary so every dispatch lands
            // outside the EH region and falls through normally. ilemit still emits the CIR one-to-one.
            outp.Add(new JsonObject
            {
                ["k"] = "exprStmt",
                ["expr"] = NullConst(AnyTn),
            });

            var inner = new List<(int state, int label)>();
            _tryStack.Push((inner, tryEntry));
            var tryBody = new List<JsonNode>();
            if (o["body"] is JsonArray tb) foreach (var s in tb) EmitStmt(s, tryBody);
            _tryStack.Pop();

            var body2 = new JsonArray();
            foreach (var (state, label) in inner)
                body2.Add(BrIf(BinEq(FieldOf("label", IntTn), IntConst(state)), true, label));
            foreach (var st in tryBody) body2.Add(st);

            var catches = new JsonArray();
            if (o["catches"] is JsonArray cs)
                foreach (var c in cs)
                    if (c is JsonObject co)
                    {
                        var cbody = new List<JsonNode>();
                        if (co["body"] is JsonArray cb) foreach (var s in cb) EmitStmt(s, cbody);
                        var cbodyArr = new JsonArray();
                        foreach (var st in cbody) cbodyArr.Add(st);
                        catches.Add(new JsonObject
                        {
                            ["excType"] = co["excType"]?.DeepClone(),
                            ["var"] = Str(co["var"]),
                            ["body"] = cbodyArr,
                        });
                    }

            var tryNode = new JsonObject
            {
                ["k"] = "try",
                ["type"] = o["type"]?.DeepClone(),
                ["body"] = body2,
                ["catches"] = catches,
            };
            if (o["finally"] is JsonArray fin)
            {
                var finOut = new List<JsonNode>();
                foreach (var s in fin) EmitStmt(s, finOut);
                var finArr = new JsonArray();
                // BUG 1: this try body spans a suspension (bodyHasSusp is true here), so its finally would be run
                // by the CLR on the suspend-return `leave` AND again at the post-resume exit. Gate it on the
                // $suspending flag: `if ($suspending) goto skip; <finally>; skip:` — skipped on the suspend-return
                // unwind, run exactly once on the real normal/exception exit.
                if (_needSuspendGuard)
                {
                    var skipL = NextLabel();
                    finArr.Add(BrIf(FieldOf(SuspendingField, BoolTn), true, skipL));
                    foreach (var st in finOut) finArr.Add(st);
                    finArr.Add(Label(skipL));
                }
                else foreach (var st in finOut) finArr.Add(st);
                tryNode["finally"] = finArr;
            }
            outp.Add(tryNode);
        }

        JsonObject RewriteTryPlain(JsonObject o)
        {
            var copy = new JsonObject();
            foreach (var kv in o) copy[kv.Key] = kv.Value == null ? null : RewriteNoSpill(kv.Value);
            return copy;
        }

        // Rewrite a suspension-free subtree: redirect field reads + `this`, no suspension segments to append.
        JsonNode RewriteNoSpill(JsonNode node)
        {
            if (node is JsonObject o)
            {
                if (Str(o["k"]) == "smSelf") return new JsonObject { ["k"] = "this" };
                // #80 — canonicalize ANY `COROUTINE_SUSPENDED`/`get_COROUTINE_SUSPENDED` top-level-val read to the SM's
                // own `Suspended()` marker, regardless of the owner MemberCallSubstitution resolved it to (it mis-owns a
                // top-level val to the ENCLOSING file class, so a bare `<FileClass>.get_COROUTINE_SUSPENDED` would reach
                // ilemit unresolved). Lifted out of the F2-only SubstBlock so EVERY SM-body path (incl. this suspension-
                // free subtree) is covered. Argless-guarded so a hypothetical user `COROUTINE_SUSPENDED(x)` fn isn't
                // swallowed. A correctly-owned IntrinsicsKt read normalizes to the identical node.
                if (Str(o["k"]) == "callStatic"
                    && KotlinPropertyAccessors.IsCall(o, "COROUTINE_SUSPENDED", "get")
                    && (o["args"] is not JsonArray csa0 || csa0.Count == 0))
                    return Suspended();
                if (Str(o["k"]) == "local" && Str(o["name"]) is string ln && _fields.Contains(ln))
                    return FieldOf(ln, RequiredFieldType(ln));
                if (Str(o["k"]) == "local" && Str(o["name"]) == "__self" && CapturedSelfField() is JsonNode sf0)
                    return sf0;
                if (Str(o["k"]) == "setLocal" && Str(o["name"]) is string sln && _fields.Contains(sln))
                    return SetField(sln, RewriteNoSpill(o["value"]));
                if (Str(o["k"]) == "var" && Str(o["name"]) is string vln)
                    return _fields.Contains(vln)
                        ? SetField(vln, o["init"] == null
                            ? DefaultOf(VarType(o)) : RewriteNoSpill(o["init"]))
                        : LocalVar(o, o["init"] == null
                            ? DefaultOf(VarType(o)) : RewriteNoSpill(o["init"]));
                if (_isMember && Str(o["k"]) == "this")
                    return FieldOf(ThisField, new TypeNode.Fqn(_ownerClass));
                if (Str(o["k"]) == "this" && CapturedOuterField() is JsonNode of0)
                    return of0;
                if (IsCoroutineContextRead(o))          // #79 — <cont>.get_context (the SM itself, in an SM subtree)
                    return CoroutineContextValue(smPath: true);
                var copy = new JsonObject();
                foreach (var kv in o) copy[kv.Key] = kv.Value == null ? null : RewriteNoSpill(kv.Value);
                return copy;
            }
            if (node is JsonArray a)
            {
                var copy = new JsonArray();
                foreach (var it in a) copy.Add(it == null ? null : RewriteNoSpill(it));
                return copy;
            }
            return node?.DeepClone();
        }

        // Rewrite an expression: lower a suspending `cond` to control flow, spill each suspend call (post-order)
        // into a suspension segment + await field, redirect param/local reads to SM field reads, and (for an
        // instance member) redirect `this`/implicit-receiver to the SM's `$this` field. Appends to `outp`.
        //
        // WHICH arms append to `outp` is a fact one caller has to predict before calling: `LowersToStatements`
        // (below) mirrors them, so an operand can be bound before a neighbour that emits statements. Add an
        // appending arm here and add it there.
        JsonNode Rewrite(JsonNode node, List<JsonNode> outp, TypeNode expectedType = null)
        {
            if (node is JsonObject o)
            {
                var k = Str(o["k"]);
                // `smSelf`: the state-machine's OWN identity (`this` in invokeSuspend). Introduced by the F2
                // suspendCoroutine inliner (EmitSuspendCoroutineCall) for the `c`/continuation binding — it must survive
                // the `this`->`$this` member rewrite (a captured `this` means the ENCLOSING receiver -> $this; `c` means
                // the SM itself).
                if (k == "smSelf") return new JsonObject { ["k"] = "this" };
                // #80 — canonicalize ANY `COROUTINE_SUSPENDED`/`get_COROUTINE_SUSPENDED` top-level-val read to the SM's
                // own `Suspended()` marker, regardless of the owner MemberCallSubstitution resolved it to (it mis-owns a
                // top-level val to the ENCLOSING file class → a bare `<FileClass>.get_COROUTINE_SUSPENDED` reaching ilemit
                // unresolved). Lifted out of the F2-only SubstBlock so EVERY suspending SM-body path is covered — incl. a
                // direct user `suspendCoroutineUninterceptedOrReturn { … ; COROUTINE_SUSPENDED }` tail that flows through
                // here, not SubstBlock. Argless-guarded so a hypothetical user `COROUTINE_SUSPENDED(x)` fn isn't swallowed.
                // A correctly-owned IntrinsicsKt read normalizes to the identical node.
                if (k == "callStatic"
                    && KotlinPropertyAccessors.IsCall(o, "COROUTINE_SUSPENDED", "get")
                    && (o["args"] is not JsonArray csa1 || csa1.Count == 0))
                    return Suspended();
                if (k == "local" && Str(o["name"]) is string ln && _fields.Contains(ln))
                    return FieldOf(ln, RequiredFieldType(ln));
                if (k == "local" && Str(o["name"]) == "__self" && CapturedSelfField() is JsonNode sf1)
                    return sf1;
                // A `setLocal`/`var` that assigns a SPILLED variable but sits INSIDE an expression subtree (e.g. the
                // `index++` post-increment lowered to `valueBlock { var <unary> = index; index = index+1; <unary> }`)
                // is reached via Rewrite, not the statement-level EmitStmt, so its field-assignment must be redirected
                // here too — else a bare `setLocal index` to an SM FIELD reaches ilemit as `store unknown var index`.
                if (k == "setLocal" && Str(o["name"]) is string sln && _fields.Contains(sln))
                    return SetField(sln, Rewrite(o["value"], outp, RequiredFieldType(sln)));
                if (k == "var" && Str(o["name"]) is string vln)
                    return _fields.Contains(vln)
                        ? SetField(vln, o["init"] == null
                            ? DefaultOf(VarType(o)) : Rewrite(o["init"], outp, VarType(o)))
                        : LocalVar(o, o["init"] == null
                            ? DefaultOf(VarType(o)) : Rewrite(o["init"], outp, VarType(o)));
                if (_isMember && k == "this")
                    return FieldOf(ThisField, new TypeNode.Fqn(_ownerClass));
                if (k == "this" && CapturedOuterField() is JsonNode of1)
                    return of1;
                // GAP 2 — a `newSuspendLambda` VALUE inside the cold SM is OPAQUE: its own body is the lambda's
                // scope (a separate SM built by SuspendLambdaLowering, which runs after this pass), so we must NOT
                // rewrite it here (a `this`/`local` in the lambda body means the LAMBDA's receiver/param, not this
                // SM's `$this`/field). Copy it verbatim, only resolving each CAPTURE's construction value into THIS
                // SM's vocabulary (`$this` for the enclosing instance, a spilled-local field, else a still-live
                // local) as `capValues` — SuspendLambdaLowering consumes that instead of re-synthesizing `this`,
                // which would wrongly denote the SM here rather than the captured enclosing instance.
                if (k == "newSuspendLambda")
                    return RewriteSuspendLambdaNew(o);
                if (IsCoroutineContextRead(o))          // #79 — <cont>.get_context; here the SM (invokeSuspend's `this`)
                    return CoroutineContextValue(smPath: true);
                if (IsSuspendCoroutineCall(o))
                    return EmitSuspendCoroutineCall(o, outp);
                // #11 — a `valueBlock` whose stmts/result span a suspension (e.g. an INLINE scope function
                // `with(lib){ b.fetch() }` used as an expression body: kotc inlines it to
                // `valueBlock { stmts:[var __scope0=lib], result: __scope0.fetch(b) }`, its result a suspend call).
                // A valueBlock's stmts run IN PLACE, so flatten it here: emit the stmts to `outp` as ordinary
                // statements (their `var`s are ordinary locals of this body, so the storage gate has already given
                // each one an SM field iff it is read after the resume), then
                // rewrite the result expression — a suspend call in the result becomes a normal suspension point
                // owned by this pass. A suspension-FREE valueBlock (e.g. an `index++` post-increment) is left
                // intact (the default copy below) for ilemit's inline emission — output stays byte-identical.
                if (k == "valueBlock" && (HasOwnSuspension(o) || EscapesExpression(o)))
                {
                    if (o["stmts"] is JsonArray vbStmts) foreach (var s in vbStmts) EmitStmt(s, outp);
                    if (o["body"] is JsonArray vbBody) foreach (var s in vbBody) EmitStmt(s, outp);
                    return o["result"] != null
                        ? Rewrite(o["result"], outp, expectedType)
                        : MissingValuePlaceholder(expectedType);
                }
                // #10 REVERSE bridge — a call to dll2klib's metadata-only
                // @ClrAwaitBridge declaration. The exact declaration marker,
                // not its source name or owner, distinguishes it from an
                // ordinary user-authored suspend function.
                if (IsAwaitMarkerCall(o))
                    return EmitAwaitPoint(o, outp);
                if ((k == "callStatic" || k == "callInstance") && Bool(o["suspendCall"]))
                    return EmitSuspensionPoint(o, outp);
                // BUG 1 (cross-module suspend consume): kotc emits a suspend call to a CROSS-ASSEMBLY (referenced)
                // suspend fun in the `clr*` vocabulary — `clrStatic`/`clrInstance` on the referenced file-class/owner,
                // `clrGenericStatic`/`clrGenericInstance` for a generic one — NOT `callStatic`/`callInstance`. Such a
                // call still carries `suspendCall:true`; without this it fell through to a plain BCL call resolving to
                // the callee's public Task<T> BRIDGE, so `blockOn { lib.crossFn() }` read a Task<Int> where an Int was
                // expected (InvalidCastException Task`1[Int32] -> Int32). Route it to the cold entry
                // `crossFn$dotkt_suspend` on the SAME referenced owner (the ColdCall clr-form path), exactly like a
                // same-assembly suspend call. (The await marker above is caught first, so it is excluded here.)
                if ((k == "clrStatic" || k == "clrInstance" || k == "clrGenericStatic" || k == "clrGenericInstance")
                    && Bool(o["suspendCall"]))
                    return EmitSuspensionPoint(o, outp);
                if (k == "cond" && (HasOwnSuspension(o) || EscapesExpression(o)))
                    return EmitCondValue(o, outp);
                // The generic copy — and it is ORDERED. A rewritten operand may append statements to `outp` (a
                // suspension's segments, an escaping `cond`'s control flow), so the operands must be rewritten in
                // EVALUATION order, not in JSON key order: `susp().f(escaping())` would otherwise emit the argument's
                // control flow ahead of the receiver's suspension. The rank table is the toolchain's one statement of
                // which operand of a node runs first, so this holds for every kind rather than for a listed few —
                // which is why the suspension-bearing operands stage 0 has already lifted out (SuspendOperandPlan.cs)
                // need no second arm here. The COPY is still built in the node's own key order, so the emitted CIR is
                // byte-identical to what an unordered copy produced.
                var rewritten = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
                foreach (var kv in o.OrderBy(kv => SuspendLiveness.OperandRank(kv.Key)).ToList())
                    rewritten[kv.Key] = kv.Value == null ? null : Rewrite(kv.Value, outp);
                var copy = new JsonObject();
                foreach (var kv in o) copy[kv.Key] = rewritten[kv.Key];
                return copy;
            }
            if (node is JsonArray a)
            {
                var copy = new JsonArray();
                foreach (var it in a) copy.Add(it == null ? null : Rewrite(it, outp));
                return copy;
            }
            return node?.DeepClone();
        }

        // Lower a `cond` (ternary) EXPRESSION to control flow, carrying its value in a temporary. The temporary
        // goes through the same storage gate as everything else: it only has to survive a suspension when the
        // conditional itself contains one. A conditional lowered purely because it ESCAPES (a `goto`/`return` in a
        // branch, with no suspension anywhere in it) keeps its value in a MoveNext local, so a byref-like value can
        // still flow through an escaping `if` inside a suspend function.
        JsonNode EmitCondValue(JsonObject c, List<JsonNode> outp)
        {
            var ty = FrameType(c)
                ?? throw new NotSupportedException(
                    $"bir2cir: suspend-lowering: a conditional expression in "
                    + $"`{(_ownerClass ?? _fileClass)}.{_name}` carries no result type of its own and neither branch "
                    + $"yields one (`then` = {Kind(c["then"])}, `else` = {Kind(c["else"])}), so the temporary that "
                    + "carries its value through the lowering would be untyped. An earlier lowering dropped a "
                    + "branch's type, or a branch's node kind needs an arm in bir-common/NodeType.cs.");
            var spans = HasOwnSuspension(c);
            var resultSlot = "__cond$" + (++_condCounter);
            if (spans) FieldStorage(resultSlot, ty, RoleCondResult, lives: true, across: SuspendedCalleeIn(c));
            else outp.Add(new JsonObject { ["k"] = "var", ["name"] = resultSlot, ["type"] = Tw(ty), ["init"] = DefaultOf(ty) });
            var elseL = NextLabel();
            var endL = NextLabel();

            var condExpr = Rewrite(c["cond"], outp, BoolTn);
            outp.Add(BrIf(condExpr, false, elseL));
            EmitCondBranch(c["then"], outp, resultSlot, ty);
            outp.Add(Goto(endL));
            outp.Add(Label(elseL));
            EmitCondBranch(c["else"], outp, resultSlot, ty);
            outp.Add(Label(endL));
            return spans ? FieldOf(resultSlot, ty) : new JsonObject { ["k"] = "local", ["name"] = resultSlot };
        }

        // A condition branch can be a suspension-free valueBlock whose statements perform a non-local control transfer
        // (`goto` back to an enclosing inline loop) and whose formal result is an unreachable throw expression. Leaving
        // that valueBlock as a setField RHS makes ilemit push the field receiver before entering the block; the goto then
        // reaches its target with that receiver still on the CLR stack. Flatten the block while bir2cir still owns the
        // control-flow decision, then assign only its fallthrough result. Suspension-bearing valueBlocks are already
        // flattened by Rewrite itself.
        void EmitCondBranch(JsonNode branch, List<JsonNode> outp, string resultSlot, TypeNode resultType)
        {
            JsonNode value;
            if (branch is JsonObject b && Str(b["k"]) == "valueBlock")
            {
                if (b["stmts"] is JsonArray stmts)
                    foreach (var stmt in stmts) EmitStmt(stmt, outp);
                if (b["body"] is JsonArray body)
                    foreach (var stmt in body) EmitStmt(stmt, outp);
                value = b["result"] != null
                    ? Rewrite(b["result"], outp, resultType)
                    : NullConst(resultType);
            }
            else
            {
                value = Rewrite(branch, outp, resultType);
            }
            // A terminal expression has no fallthrough value to assign. Emitting it as a
            // standalone CIR statement also guarantees that no setField receiver has been
            // pushed before `throw`/`return`/`goto`.
            if (value is JsonObject terminal
                && Str(terminal["k"]) is "throwExpr" or "returnExpr" or "throw" or "return" or "goto")
            {
                var terminalKind = Str(terminal["k"]);
                outp.Add(terminalKind is "throwExpr" or "returnExpr"
                    ? new JsonObject
                    {
                        ["k"] = terminalKind == "throwExpr" ? "throw" : "return",
                        ["value"] = terminalKind == "returnExpr" && terminal["value"] == null
                            ? NullConst(AnyTn)
                            : terminal["value"]?.DeepClone(),
                    }
                    : value);
                return;
            }
            outp.Add(_fields.Contains(resultSlot)
                ? SetField(resultSlot, value)
                : new JsonObject { ["k"] = "setLocal", ["name"] = resultSlot, ["value"] = value });
        }

        // A value expression containing a control transfer cannot remain nested under a CLR
        // operand (setField receiver, call receiver/argument, binary lhs, ...). ilemit emits
        // CIR mechanically and would necessarily push the parent operand before entering the
        // nested block; a jump/return/throw then escapes with that pending value on the stack.
        // Flatten such expressions while bir2cir still owns CFG construction.
        //
        // Stops at `LambdaKinds` on purpose — a transfer inside a lambda body belongs to that
        // lambda's own frame, so flattening the enclosing expression on account of it would be
        // a regression.
        static bool EscapesExpression(JsonNode node)
        {
            bool Walk(JsonNode n)
            {
                if (n is JsonObject o)
                {
                    var k = Str(o["k"]);
                    if (k != null && LambdaKinds.Contains(k)) return false;
                    if (k != null && ControlTransferKinds.Contains(k)) return true;
                    foreach (var kv in o)
                        if (kv.Value != null && Walk(kv.Value)) return true;
                }
                else if (n is JsonArray a)
                {
                    foreach (var item in a)
                        if (item != null && Walk(item)) return true;
                }
                return false;
            }
            return Walk(node);
        }

        // Does rewriting this operand APPEND STATEMENTS to `outp` — a suspension's segments, or the control flow an
        // escaping value is flattened into? `EmitAwaitPoint` asks it of the captureContext argument: an operand that
        // emits statements is what can leave an earlier operand's expression stranded behind them.
        //
        // NOT `HasOwnSuspension || EscapesExpression`. Those two answer "whose frame does this belong to" and stop at
        // every `LambdaKinds` node; `Rewrite` treats only `newSuspendLambda` as opaque and descends into everything
        // else — including a `newClosure`'s CAPTURES, which are ordinary expressions evaluated where the closure is
        // constructed. A bound callable reference `(<expr>)::f` puts an ARBITRARY expression there (kotc's
        // `boundExtRef`), so an escaping value can sit under a lambda kind and still be flattened into this frame.
        // The `synthClass` is not an operand — it is the closure's own body — and is skipped for the same reason the
        // call-evaluation plan's eager-spine scan skips it.
        //
        // Over-answering is safe (one bound receiver more than strictly needed); under-answering reorders operands,
        // so an unfamiliar shape must land on `true`.
        //
        // MAINTENANCE: its arms are `Rewrite`'s statement-appending dispatch, one for one — each is marked below with
        // the emitter it stands for. A new arm there that appends to `outp` needs one here, or the operand left of it
        // stops being ordered against it.
        static bool LowersToStatements(JsonNode node) => OperandLowering(node, suspensionOnly: false);

        /// Of those statements, is any of them a SUSPENSION — the question that decides whether the value bound
        /// before this operand needs a state-machine field rather than a local. Asked over the SAME subtree, by the
        /// same walk, so it cannot miss a suspension the statement question above saw: the two answers are about one
        /// operand, and a disagreement between them is a value stored where the resume cannot read it.
        static bool LowersToSuspension(JsonNode node) => OperandLowering(node, suspensionOnly: true);

        static bool OperandLowering(JsonNode node, bool suspensionOnly)
        {
            switch (node)
            {
                case JsonObject o:
                    var k = Str(o["k"]);
                    if (k == "newSuspendLambda") return false;
                    if (Bool(o["suspendCall"])) return true;               // -> EmitSuspensionPoint / EmitAwaitPoint
                    if (IsSuspendCoroutineCall(o)) return true;            // -> EmitSuspendCoroutineCall
                    if (!suspensionOnly && k != null && ControlTransferKinds.Contains(k))
                        return true;                                       // -> the escaping-value flatten
                    foreach (var kv in o)
                        if (kv.Key != "synthClass" && kv.Value != null && OperandLowering(kv.Value, suspensionOnly))
                            return true;
                    return false;
                case JsonArray a:
                    foreach (var it in a)
                        if (it != null && OperandLowering(it, suspensionOnly)) return true;
                    return false;
                default:
                    return false;
            }
        }

        // A suspension point (mirrors kotc emitSuspend): set label, start the cold call passing `this` (the SM,
        // a Continuation) as the callee's completion; if it returns COROUTINE_SUSPENDED, return SUSPENDED
        // (inline); else fall through to the merge label, rethrow a failed resume, store the awaited value.
        JsonNode EmitSuspensionPoint(JsonObject callNode, List<JsonNode> outp)
        {
            // The awaited value's Kotlin type, in the toolchain's ONE stamp order — `sty`, then `ret`, then `dynRet`
            // (bir-common/NodeType.cs PRECEDENCE, #199). `sty` is the frontend-resolved INSTANTIATED type kotc stamps
            // per call site, so it types the awaited value correctly even for two same-simple-name suspend funs in
            // different packages, and for a generic-owner call whose `ret` names the un-instantiated `T`. A
            // CROSS-ASSEMBLY suspend call arrives in the `clr*` vocabulary carrying `ret`, which the second read
            // covers. The name-keyed same-assembly fallback below remains for a rare synthesized node carrying no
            // stamp at all.
            var retTok = CallEvalLowering.StaticTypeOf(callNode)
                ?? (_calleeRet.TryGetValue(Str(callNode["method"]) ?? "", out var d) ? d : null)
                ?? AnyTn;
            if (IsUnitTn(retTok)) retTok = AnyTn;
            var state = ++_state;
            var resumeLabel = NextLabel();
            RegisterResume(state, resumeLabel);
            var field = "__aw$" + state;
            // The awaited value is written at the RESUME point and read after it, so it is heap storage by
            // construction — a byref-like result type is refused here (a suspend callee cannot return one anyway:
            // the cold entry's return slot is `Any?`).
            FieldStorage(field, retTok, RoleAwaited, lives: true, across: Str(callNode["method"]));

            outp.Add(SetField("label", IntConst(state)));
            outp.Add(new JsonObject { ["k"] = "setLocal", ["name"] = "result", ["value"] = ColdCall(callNode, outp) });
            outp.Add(BrIf(new JsonObject
            {
                ["k"] = "objEq",
                ["lhs"] = new JsonObject { ["k"] = "local", ["name"] = "result" },
                ["rhs"] = Suspended(),
            }, false, resumeLabel));
            if (_needSuspendGuard) outp.Add(SetField(SuspendingField, BoolConst(true)));   // BUG 1: mark the suspend-return
            outp.Add(Ret(Suspended()));
            outp.Add(Label(resumeLabel));
            outp.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = ThrowOnFailure() });
            outp.Add(SetField(field, IsAnyTn(retTok)
                ? new JsonObject { ["k"] = "local", ["name"] = "result" }
                : new JsonObject { ["k"] = "cast", ["type"] = Tw(retTok), ["e"] = new JsonObject { ["k"] = "local", ["name"] = "result" } }));
            return FieldOf(field, retTok);
        }

        // F2 — a `suspendCoroutine { … }` / `suspendCoroutineUninterceptedOrReturn { … }` call, lowered to a real cold
        // suspension point. Our compiler does NOT inline these @InlineOnly intrinsics at the call site (cross-module
        // never; same-module no longer either since #75 S4b), so in EVERY build kotc carries a plain
        // `callStatic <name>(<block>)`, its wrapper body NOT inlined. We reconstruct it here:
        //   suspendCoroutine (block returns Unit, wraps a SafeContinuation):
        //     this.__safe = newSafeContinuation((Continuation)this)   // buffers a synchronous resume
        //     <inlined block, `c` -> this.__safe>                      // e.g. resume(this.__safe, 42)
        //     result = safeGetOrThrow(this.__safe)                     // sync value, or COROUTINE_SUSPENDED
        //   suspendCoroutineUninterceptedOrReturn (block returns Any?, `c` is the SM directly):
        //     <inlined block pre-stmts, `c` -> this(SM)>;  result = <block tail>
        //   then the standard fast-path: if result !== COROUTINE_SUSPENDED goto resume; return SUSPENDED;
        //   resume: throwOnFailure(result); __aw = (T)result.
        JsonNode EmitSuspendCoroutineCall(JsonObject callNode, List<JsonNode> outp)
        {
            var method = Str(callNode["method"]);
            var wrapper = method == "suspendCoroutine";
            var arg = (callNode["args"] as JsonArray)?.FirstOrDefault() as JsonObject;
            var (invBody, cParam, capMap, closureType) = ResolveBlockLambda(arg);
            var blockFn = CallEvalLowering.StaticTypeOf(arg) as TypeNode.Fn
                ?? TypeJson.Read(arg?["funcType"]) as TypeNode.Fn
                ?? TypeJson.Read((callNode["sig"] as JsonArray)?.FirstOrDefault()) as TypeNode.Fn;
            if (invBody == null && (blockFn == null || blockFn.Suspend || blockFn.Params.Length != 1))
                throw new InvalidOperationException(
                    $"malformed {method} block in '{(_ownerClass ?? _fileClass)}.{_name}': expected one non-suspend " +
                    $"Continuation function argument");

            // The intrinsic's Kotlin result type, in the toolchain's ONE stamp order (bir-common/NodeType.cs
            // PRECEDENCE): `suspendCoroutine<T>` is generic, so `ret` here is the DECLARED `T` while `sty` is the
            // instantiated one at this call site. AnyTn survives as the LEGITIMATE cold-ABI type for the un-stamped
            // and the Unit case — the resume slot is `Any?` either way.
            var resultT = CallEvalLowering.StaticTypeOf(callNode) ?? AnyTn;
            var retTok = IsUnitTn(resultT) ? AnyTn : resultT;

            var state = ++_state;
            var resumeLabel = NextLabel();
            RegisterResume(state, resumeLabel);
            var awField = "__aw$" + state;
            FieldStorage(awField, retTok, RoleAwaited, lives: true, across: method);

            JsonNode tail;
            if (wrapper)
            {
                var safeField = "__safe$" + state;
                FieldStorage(safeField, ContAnyTn, RoleMachinery, lives: true, across: null);
                // this.__safe = newSafeContinuation((Continuation<Any?>) this)   — the SM is its own delegate.
                outp.Add(SetField(safeField, new JsonObject
                {
                    ["k"] = "callStatic",
                    ["owner"] = Tn(ThrowOnFailureOwner),
                    ["method"] = "newSafeContinuation",
                    ["sig"] = new JsonArray { ContAny() },
                    ["args"] = new JsonArray
                    {
                        new JsonObject { ["k"] = "cast", ["type"] = ContAny(), ["e"] = new JsonObject { ["k"] = "this" } },
                    },
                    ["ret"] = ContAny(),
                }));
                var cBinding = SmSelfField(safeField, ContAnyTn);   // smSelf recv survives the this->$this member rewrite
                if (invBody != null)
                    foreach (var s in invBody) EmitStmt(SubstBlock(s, capMap, cParam, cBinding, closureType), outp);
                else
                    EmitStmt(new JsonObject
                    {
                        ["k"] = "exprStmt",
                        ["expr"] = InvokeStoredBlock(arg, blockFn, cBinding),
                    }, outp);
                tail = new JsonObject
                {
                    ["k"] = "callStatic",
                    ["owner"] = Tn(ThrowOnFailureOwner),
                    ["method"] = "safeGetOrThrow",
                    ["sig"] = new JsonArray { ContAny() },
                    ["args"] = new JsonArray { SmSelfField(safeField, ContAnyTn) },
                    ["ret"] = Tw(AnyTn),
                };
            }
            else
            {
                var cBinding = new JsonObject { ["k"] = "smSelf" };
                // #22 — the UNINTERCEPTED form passes the SM ITSELF (smSelf) as the raw continuation, so a block that
                // SYNCHRONOUSLY `cont.resume(v)`s re-enters this same invokeSuspend before it returns. The state label
                // must therefore be armed to `state` BEFORE the block runs (mirroring the JVM CPS, which sets
                // `this.label = N` before the suspend call) — otherwise the re-entry finds label 0 and restarts the
                // block from the top (unbounded recursion). The wrapper form buffers through SafeContinuation, so its
                // label-set stays AFTER the block (below), keeping that path byte-identical.
                // INVARIANT: the intrinsic block is a PLAIN (non-suspend) lambda, so its pre-stmts carry no nested
                // suspension that would re-arm the label to a later state — arming `state` here is the sole write in
                // this segment.
                outp.Add(SetField("label", IntConst(state)));
                if (invBody != null)
                {
                    JsonNode t = Suspended();
                    var pre = new List<JsonNode>();
                    for (var i = 0; i < invBody.Count; i++)
                        if (i == invBody.Count - 1 && invBody[i] is JsonObject last && Str(last["k"]) == "return")
                            t = last["value"] ?? NullConst(AnyTn);
                        else
                            pre.Add(invBody[i]);
                    foreach (var s in pre) EmitStmt(SubstBlock(s, capMap, cParam, cBinding, closureType), outp);
                    tail = SubstBlock(t, capMap, cParam, cBinding, closureType);
                }
                else tail = InvokeStoredBlock(arg, blockFn, cBinding);
            }

            if (wrapper) outp.Add(SetField("label", IntConst(state)));
            outp.Add(new JsonObject { ["k"] = "setLocal", ["name"] = "result", ["value"] = Rewrite(tail, outp, AnyTn) });
            outp.Add(BrIf(new JsonObject
            {
                ["k"] = "objEq",
                ["lhs"] = new JsonObject { ["k"] = "local", ["name"] = "result" },
                ["rhs"] = Suspended(),
            }, false, resumeLabel));
            if (_needSuspendGuard) outp.Add(SetField(SuspendingField, BoolConst(true)));
            outp.Add(Ret(Suspended()));
            outp.Add(Label(resumeLabel));
            outp.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = ThrowOnFailure() });
            outp.Add(SetField(awField, IsAnyTn(retTok)
                ? new JsonObject { ["k"] = "local", ["name"] = "result" }
                : new JsonObject { ["k"] = "cast", ["type"] = Tw(retTok), ["e"] = new JsonObject { ["k"] = "local", ["name"] = "result" } }));
            return FieldOf(awField, retTok);
        }

        // A non-literal intrinsic argument is already a real function value. Invoke that value once instead of trying
        // to reconstruct (and potentially re-evaluate) the expression that originally produced it. The function type
        // is still Kotlin semantic vocabulary here; ClrMemberResolution later binds the one concrete delegate Invoke.
        static JsonObject InvokeStoredBlock(JsonObject block, TypeNode.Fn blockFn, JsonNode continuation) => new()
        {
            ["k"] = "delegateInvoke",
            ["funcType"] = TypeJson.Write(blockFn),
            ["recv"] = block.DeepClone(),
            ["args"] = new JsonArray
            {
                new JsonObject
                {
                    ["k"] = "cast",
                    ["type"] = TypeJson.Write(blockFn.Params[0]),
                    ["e"] = continuation.DeepClone(),
                },
            },
        };

        // Resolve a suspendCoroutine block arg (newClosure -> a top-level closure class in _closures; newDelegate -> a
        // top-level generated method in _lambdaMethods) to its invoke body, continuation-param name, and capture map (empty
        // for newDelegate). Returns (null, …) when unresolvable.
        (JsonArray body, string cParam, Dictionary<string, JsonNode> capMap, string closureType)
        ResolveBlockLambda(JsonObject arg)
        {
            var capMap = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
            JsonObject invoke;
            string closureType = null;
            if (arg != null && Str(arg["k"]) == "newClosure")
            {
                closureType = TypeJson.OwnerName(arg["closureType"]);
                if (closureType == null || !_closures.TryGetValue(closureType, out var cls))
                    return (null, null, null, null);
                _consumedIntrinsicClosures?.Add(closureType);   // #22: this class is reconstructed INLINE below -> now dead; pruned by ApplyAll
                invoke = (cls["methods"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(m => Str(m["name"]) == "invoke");
                if (cls["fields"] is JsonArray flds && arg["captures"] is JsonArray caps)
                    for (var i = 0; i < flds.Count && i < caps.Count; i++)
                        if (flds[i] is JsonObject fo && Str(fo["name"]) is string fn) capMap[fn] = caps[i];
            }
            else if (arg != null && Str(arg["k"]) == "newDelegate")
            {
                if (Str(arg["method"]) is not string mname || !_lambdaMethods.TryGetValue(mname, out invoke))
                    return (null, null, null, null);
            }
            else return (null, null, null, null);
            if (invoke == null) return (null, null, null, null);
            var body = invoke["body"] as JsonArray ?? new JsonArray();
            var cParam = (invoke["params"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() is JsonObject cp ? Str(cp["name"]) : null;
            return (body, cParam, capMap, closureType);
        }

        // Substitute an F2 block-lambda body for inlining: a field read of the closure's own captured field -> its
        // capture expression; the `c`/continuation param -> `cBinding` (the SafeContinuation field for the wrapper,
        // `smSelf` for the unintercepted form). Produces a tree still in the ENCLOSING method's vocabulary, which
        // Rewrite/EmitStmt then lower (smSelf->this, captured this->$this).
        JsonNode SubstBlock(JsonNode node, Dictionary<string, JsonNode> capMap, string cParam, JsonNode cBinding, string closureType)
        {
            if (node is JsonObject o)
            {
                var k = Str(o["k"]);
                // (#80) — the `COROUTINE_SUSPENDED`->`Suspended()` canonicalization was LIFTED out of here into
                // Rewrite/RewriteNoSpill (which every SubstBlock output flows through), so it now covers every SM-body
                // path, not just this F2 block. No canonicalization is needed here.
                if (k == "field" && closureType != null && TypeJson.OwnerName(o["ownerType"]) == closureType && Str(o["name"]) is string fn
                    && capMap.TryGetValue(fn, out var cap))
                    return cap.DeepClone();
                if (k == "local" && cParam != null && Str(o["name"]) == cParam)
                    return cBinding.DeepClone();
                var copy = new JsonObject();
                foreach (var kv in o) copy[kv.Key] = kv.Value == null ? null : SubstBlock(kv.Value, capMap, cParam, cBinding, closureType);
                return copy;
            }
            if (node is JsonArray a)
            {
                var copy = new JsonArray();
                foreach (var it in a) copy.Add(it == null ? null : SubstBlock(it, capMap, cParam, cBinding, closureType));
                return copy;
            }
            return node?.DeepClone();
        }

        // An SM field access whose receiver is `smSelf` (the SM itself) — survives the this->$this member rewrite when
        // the access is inlined into a body that Rewrite later processes (cf. FieldOf, which uses a bare `this` recv).
        JsonObject SmSelfField(string name, TypeNode type) => new()
        {
            ["k"] = "field",
            ["ownerType"] = Tw(_smTypeInst),
            ["recv"] = new JsonObject { ["k"] = "smSelf" },
            ["name"] = name,
            ["ret"] = Tw(type),
        };

        void RegisterResume(int state, int resumeLabel)
        {
            if (_tryStack.Count == 0)
                _dispatch.Add((state, resumeLabel));
            else
            {
                // A resume nested N protected regions must re-enter the OUTERMOST try first, then each nested
                // try's local state dispatch, before reaching the actual resume label. Branching directly to the
                // innermost try entry bypasses its enclosing protected region and produces invalid IL.
                var target = resumeLabel;
                foreach (var frame in _tryStack) // Stack enumerates innermost -> outermost.
                {
                    frame.inner.Add((state, target));
                    target = frame.tryEntry;
                }
                _dispatch.Add((state, target));
            }
        }

        // An `x.await()` suspension point (#10 REVERSE bridge, design §4/§5) — the .NET-awaitable ⇒ Kotlin suspend
        // boundary. The dll2klib-projected @ClrAwaitBridge marker becomes the cold-core
        // awaiter dance, structurally IDENTICAL to EmitSuspensionPoint but obtaining the resume value from a .NET AWAITER
        // instead of a cold `$dotkt_suspend` return. The awaiter shape is discovered from the awaitable's GetAwaiter via
        // ref metadata (ReferenceMetadataIndex.ResolveAwaitable) — Task/ValueTask/WinRT/custom, NO per-type hardcode:
        //
        //   this.<aw> = <awaitable>.GetAwaiter()             // the awaiter struct spilled into an SM field
        //   if (this.<aw>.IsCompleted) goto L_state          // sync fast path — no suspension
        //   this.label = state
        //   this.<aw>.OnCompleted(<Action bound to this.$awaitOnDone$state>)   // INotifyCompletion; flows ExecutionContext
        //   return COROUTINE_SUSPENDED
        //   L_state: <value> = this.<aw>.GetResult()          // throws on a faulted/canceled awaitable
        //
        // We bind INotifyCompletion.OnCompleted (the pattern's MANDATORY member) rather than ICriticalNotifyCompletion.
        // UnsafeOnCompleted: our cold core carries no ExecutionContext-flowing state-machine box, so OnCompleted (which
        // DOES flow EC) is the correct choice — UnsafeOnCompleted would silently drop AsyncLocal flow across every await.
        //
        // The Action callback (a synthesized SM instance method) re-drives THIS SM through the INTERCEPTED continuation:
        // `this.intercepted().resumeWith(Result(null))` (#7 Part B). When the coroutine context carries a
        // ContinuationInterceptor, `intercepted()` returns the interceptor-wrapped continuation whose resumeWith
        // dispatches on the interceptor's chosen thread — so the interceptor takes PRECEDENCE over the captured
        // SynchronizationContext. Absent an interceptor `intercepted()` is the identity continuation (cached) →
        // `this.resumeWith(...)`, so the #3 captured-SyncContext / inline fallback is unchanged. The resumed `result` is
        // only a WAKE TOKEN (Option B): the real value / fault comes from GetResult() at L_state — a fault THROWS there,
        // propagating up through invokeSuspend into BaseContinuationImpl.resumeWith's catch → the completion. So NO
        // throwOnFailure and NO try/catch in the callback are needed. The awaiter is a readonly struct, so the field
        // spill/copy is safe.
        JsonNode EmitAwaitPoint(JsonObject awaitNode, List<JsonNode> outp)
        {
            var isMember = Str(awaitNode["k"]) == "callInstance";
            var methodTypeArgs = (awaitNode["typeArgs"] as JsonArray)?
                .Select(TypeJson.Read)
                .Where(x => x != null)
                .ToArray() ?? System.Array.Empty<TypeNode>();
            // #3/#64: the SynchronizationContext-capture control. dll2klib publishes TWO await bridges for an
            // awaitable that has `ConfigureAwait(bool)` — `await()` and `await(captureContext: Boolean)` — so the
            // argument is present or absent, never defaulted. TWO arms, chosen by the argument's SHAPE and never by
            // its VALUE:
            //
            //   omitted, or a constant `true`  ->  awaitable.GetAwaiter()                     (the capturing default)
            //   anything else                  ->  awaitable.ConfigureAwait(<arg>).GetAwaiter()
            //
            // A constant `false` is not a case of its own: it flows through the second arm as the expression `false`.
            // `ConfigureAwait(b)` returns the SAME configured awaitable — hence the same awaiter type — for either
            // value, so the runtime Boolean changes no CLR type and needs no branch between two state-machine field
            // types. The first arm exists because `GetAwaiter()` already IS the capturing behavior, so only the
            // capture-controlling call pays for the configured awaitable.
            var callArgs = awaitNode["args"] as JsonArray ?? new JsonArray();
            var captureIndex = isMember ? 0 : 1;
            JsonObject ccArgNode = callArgs.Count > captureIndex
                ? callArgs[captureIndex] as JsonObject : null;
            var configured = ccArgNode != null && !IsConstTrue(ccArgNode);

            // A member bridge uses its constructed owner type. An extension
            // bridge uses its declared receiver type, substituting the
            // call-site method arguments into the open signature.
            var receiverType = isMember
                ? TypeJson.Read(awaitNode["ownerType"])
                : ReceiverParamType(awaitNode);
            receiverType = SubstituteMethodTypeParameters(
                receiverType, methodTypeArgs);
            var recvParam = receiverType as TypeNode.Fqn;
            var awaitableName = recvParam?.Name;
            var awaitableArity = recvParam?.Args?.Length ?? 0;
            var plan = awaitableName != null ? _refs?.ResolveAwaitable(awaitableName, awaitableArity) : null;
            // Fail LOUD on an unresolvable/non-conforming awaitable — an `.await()` that did not type-check as a .NET
            // awaitable should never have reached here; never guess a Task shape.
            if (plan == null)
                throw new NotSupportedException(
                    $"await: '{awaitableName ?? "<unknown>"}' is not a resolvable .NET awaitable "
                    + "(no conforming GetAwaiter found in the referenced metadata)");
            if (configured && !plan.SupportsConfigureAwait)
                throw new NotSupportedException(
                    $"await(captureContext = …) is unsupported for '{awaitableName}': {plan.ConfigureAwaitGap}");

            // An extension bridge declares `awaitResult` in its own method-type-parameter space; a member bridge on a
            // generic awaitable (`Task<T>`, `ValueTask<T>`) declares it in the owner's type-parameter space. Close both
            // axes from the frontend-selected call before the result becomes a state-machine field / GetResult CIR
            // signature. Leaving either `method.tv0` or `type.tv0` open produces invalid object arithmetic after resume.
            var resultTok = SubstituteMethodTypeParameters(
                TypeJson.Read(awaitNode["awaitResult"]) ?? UnitTn,
                methodTypeArgs);
            resultTok = SubstituteTypeParameters(
                resultTok,
                recvParam?.Args ?? System.Array.Empty<TypeNode>());
            var hasResult = resultTok is not TypeNode.Fqn { Name: "kotlin.Unit" };
            var awaitableType = recvParam;
            // The awaiter type, one per arm: the awaitable's own awaiter, or the one its configured awaitable returns
            // — statically known in BOTH arms, because the Boolean the configured arm passes cannot change it. Each
            // comes from the plan as a TEMPLATE of the DECLARED type (ReferenceMetadataIndex.Awaitable.cs), closed
            // here from this call site: the awaitable's type arguments, and an extension GetAwaiter's method
            // arguments. It is not rebuilt from the receiver's arguments — a declaration is free to permute, drop or
            // fix them, and copying them positionally named a type the awaiter's members do not live on.
            var awaiterType = CloseAwaitTemplate(
                configured ? plan.ConfiguredAwaiterTemplate : plan.AwaiterTemplate, awaitableType, methodTypeArgs);

            // KOTLIN ORDER, exactly once each: the awaitable receiver, then the captureContext argument. This emitter
            // rewrites its own operands (the marker is excluded from the stage-0 operand plan, SuspendOperandPlan.cs),
            // so the ordering rule that plan states is stated here for the two operands a marker has.
            var awaitable = Rewrite(
                isMember ? awaitNode["recv"] : callArgs.FirstOrDefault(),
                outp, awaitableType);
            JsonNode captureValue = null;
            if (configured)
            {
                // When the argument's own lowering APPENDS STATEMENTS, the receiver's value is still an expression
                // sitting in a slot — so it would be evaluated after them. Bind it first. Nothing is bound when the
                // argument emits no statements: the receiver is then evaluated by the ConfigureAwait call itself,
                // ahead of its argument, which is the order Kotlin asks for.
                //
                // WHICH STORAGE is a separate question, and only a SUSPENSION between the binding and its use makes it
                // a state-machine field: invokeSuspend is re-entered at the resume label, so a MoveNext local written
                // before the suspension no longer holds. Suspension-free statements (an escaping `throw`/`return`
                // flattened into control flow) run inside ONE invocation, so the local is enough — and a local is what
                // a byref-like awaitable can be. A field for those would refuse `Span`-like awaitables that the CLR
                // only forbids as FIELDS (docs/dotkt-semantics.md §4d), which is a refusal on valid IR.
                //
                // BOTH questions are asked by the SAME walk over the SAME subtree. Asking the second one with the
                // frame-ownership predicates instead would reintroduce the disagreement the first one exists to
                // avoid: they stop at every lambda kind, so a suspension inside a `newClosure` capture would be
                // invisible to the storage question and visible to the statement question — a local written before a
                // suspension the resume then reads. `SuspensionRefusalReason` refuses such a function outright today
                // (a `newClosure` holding any suspension is not segmentable), so the disagreement had no witness;
                // agreement by construction does not depend on that refusal staying where it is.
                if (LowersToStatements(ccArgNode))
                {
                    var bound = "__awaitable$" + (++_awaitTarget);
                    if (LowersToSuspension(ccArgNode))
                    {
                        FieldStorage(bound, awaitableType, RoleMachinery, lives: true, across: "await");
                        outp.Add(SetField(bound, awaitable));
                        awaitable = FieldOf(bound, awaitableType);
                    }
                    else
                    {
                        outp.Add(new JsonObject
                        {
                            ["k"] = "var",
                            ["name"] = bound,
                            ["type"] = Tw(awaitableType),
                            ["init"] = awaitable,
                        });
                        awaitable = new JsonObject
                        {
                            ["k"] = "local",
                            ["name"] = bound,
                            ["sty"] = Tw(awaitableType),
                        };
                    }
                }
                captureValue = Rewrite(ccArgNode, outp, BoolTn);
            }

            var state = ++_state;
            var afterLabel = NextLabel();
            RegisterResume(state, afterLabel);

            var awField = "__awaiter$" + state;
            FieldStorage(awField, awaiterType, RoleMachinery, lives: true, across: "await");

            // this.<aw> = <getAwaiter over the awaitable / its ConfigureAwait(<captureContext>) / a referenced extension GetAwaiter>
            outp.Add(SetField(
                awField,
                BuildGetAwaiter(
                    plan,
                    awaitableType,
                    awaiterType,
                    awaitable,
                    captureValue,
                    resultTok,
                    methodTypeArgs)));
            // if (this.<aw>.IsCompleted) goto L_state;   (sync fast path — no suspension)
            outp.Add(BrIf(new JsonObject
            {
                ["k"] = "clrPropGet",
                ["type"] = Tw(awaiterType),
                ["name"] = "IsCompleted",
                ["static"] = false,
                ["recv"] = FieldOf(awField, awaiterType),
                ["ret"] = Tw(BoolTn),
            }, true, afterLabel));
            // this.label = state; this.<aw>.OnCompleted(<callback Action>); return COROUTINE_SUSPENDED;
            outp.Add(SetField("label", IntConst(state)));
            var cbName = "$awaitOnDone$" + state;
            _awaitResumeMethods.Add(AwaitResumeMethod(cbName));
            outp.Add(new JsonObject
            {
                ["k"] = "exprStmt",
                ["expr"] = new JsonObject
                {
                    ["k"] = "clrInstance",
                    ["type"] = Tw(awaiterType),
                    ["method"] = "OnCompleted",
                    ["recv"] = FieldOf(awField, awaiterType),
                    ["argTypes"] = new JsonArray { Tn(ActionFqn) },
                    ["args"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["k"] = "newBoundDelegate", ["funcType"] = Tw(new TypeNode.Fn(false, VoidTn, System.Array.Empty<TypeNode>())),
                            ["ownerType"] = Tw(_smTypeInst), ["calleeOwner"] = Tw(_smTypeInst),
                            ["method"] = cbName, ["virtual"] = false,
                            ["recv"] = new JsonObject { ["k"] = "this" },
                        },
                    },
                    ["ret"] = Tw(VoidTn),
                },
            });
            if (_needSuspendGuard) outp.Add(SetField(SuspendingField, BoolConst(true)));   // BUG 1: mark the suspend-return
            outp.Add(Ret(Suspended()));
            outp.Add(Label(afterLabel));

            // L_state: <value> = this.<aw>.GetResult();   (throws on a faulted/canceled task)
            var getResult = new JsonObject
            {
                ["k"] = "clrInstance",
                ["type"] = Tw(awaiterType),
                ["method"] = "GetResult",
                ["recv"] = FieldOf(awField, awaiterType),
                ["argTypes"] = new JsonArray(),
                ["args"] = new JsonArray(),
                ["ret"] = Tw(hasResult ? resultTok : VoidTn),
            };
            if (hasResult)
            {
                var valField = "__awval$" + state;
                FieldStorage(valField, resultTok, RoleAwaited, lives: true, across: "await");
                outp.Add(SetField(valField, getResult));
                return FieldOf(valField, resultTok);
            }
            // Non-generic await(): Unit — GetResult is `void` (side-effecting), the value is Unit.
            outp.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = getResult });
            return NullConst(UnitTn);
        }

        // The awaitable type constructor at the marker: the extension's DECLARED receiver-param type. The generic marker
        // carries it as `shapeTypes[0]` (an open `X<method.tv0>`); the non-generic marker as `sig[0]`/`argTypes[0]`.
        static TypeNode ReceiverParamType(JsonObject awaitNode)
        {
            JsonNode slot = (awaitNode["shapeTypes"] as JsonArray)?.FirstOrDefault()
                ?? (awaitNode["argTypes"] as JsonArray)?.FirstOrDefault()
                ?? (awaitNode["sig"] as JsonArray)?.FirstOrDefault();
            return slot != null ? TypeJson.Read(slot) : null;
        }

        // Close an AwaitPlan template at this call site: the extension-method arguments the marker carries, then the
        // awaitable's own. Both scopes in one place, because a plan template can mention either — an extension
        // GetAwaiter's awaiter is written in the method's parameters, a member's in the awaitable's.
        static TypeNode CloseAwaitTemplate(TypeNode template, TypeNode.Fqn awaitableType, TypeNode[] methodTypeArgs) =>
            SubstituteTypeParameters(
                SubstituteMethodTypeParameters(template, methodTypeArgs),
                awaitableType?.Args ?? System.Array.Empty<TypeNode>());

        static TypeNode SubstituteMethodTypeParameters(
            TypeNode type,
            TypeNode[] arguments) =>
            SubstituteParameters(type, "method", arguments);

        static TypeNode SubstituteTypeParameters(
            TypeNode type,
            TypeNode[] arguments) =>
            SubstituteParameters(type, "type", arguments);

        static TypeNode SubstituteParameters(
            TypeNode type,
            string scope,
            TypeNode[] arguments) => type switch
            {
                TypeNode.Tv tv when tv.Scope == scope
                    && tv.I >= 0 && tv.I < arguments.Length => arguments[tv.I],
                TypeNode.Fqn f => new TypeNode.Fqn(
                    f.Name,
                    f.Args?.Select(x => SubstituteParameters(x, scope, arguments)).ToArray()),
                TypeNode.Nullable n => new TypeNode.Nullable(
                    SubstituteParameters(n.Of, scope, arguments)),
                TypeNode.Oblivious o => new TypeNode.Oblivious(
                    SubstituteParameters(o.Of, scope, arguments)),
                TypeNode.Array a => new TypeNode.Array(
                    SubstituteParameters(a.Elem, scope, arguments)),
                TypeNode.ByRef b => new TypeNode.ByRef(
                    SubstituteParameters(b.Of, scope, arguments)),
                TypeNode.Fn f => new TypeNode.Fn(
                    f.Suspend,
                    SubstituteParameters(f.Ret, scope, arguments),
                    f.Params.Select(x => SubstituteParameters(x, scope, arguments)).ToArray(),
                    f.Recv is null
                        ? null
                        : SubstituteParameters(f.Recv, scope, arguments),
                    f.Clr,
                    f.Ctx?.Select(x => SubstituteParameters(x, scope, arguments)).ToArray()),
                _ => type,
            };

        // `this.<aw> = <getAwaiter>()` for the resolved awaitable pattern. The awaitable is entered in one of two
        // ways, and a captureContext argument inserts one hop before it:
        //   member GetAwaiter (Task/ValueTask)        -> clrInstance on the awaitable
        //   referenced extension GetAwaiter (WinRT)   -> clrStatic (non-generic) / clrGenericStatic (generic, over the
        //                                                result type) on the [Extension] static class, awaitable as arg0
        //   a captureContext argument (#3/#64)        -> the same two shapes, on `awaitable.ConfigureAwait(<value>)`
        //                                                instead of on the awaitable — the configured awaitable is an
        //                                                awaitable like any other, and its GetAwaiter may equally be a
        //                                                member or a referenced extension. The value is whatever the
        //                                                call passed, constant or not.
        JsonNode BuildGetAwaiter(
            AwaitPlan plan,
            TypeNode awaitableType,
            TypeNode awaiterType,
            JsonNode task,
            JsonNode captureValue,
            TypeNode resultTok,
            TypeNode[] methodTypeArgs)
        {
            if (captureValue != null)
            {
                // configured = awaitable.ConfigureAwait(<captureContext>); this.<aw> = <getAwaiter on configured>.
                // The configured type is the DECLARED return type closed at this call site, never the receiver's
                // arguments reused: `Awaitable<A,B>.ConfigureAwait(bool): Configured<B,A>` is `Configured<B,A>`.
                var configuredType = CloseAwaitTemplate(
                    plan.ConfiguredAwaitableTemplate, awaitableType as TypeNode.Fqn, methodTypeArgs);
                var configured = new JsonObject
                {
                    ["k"] = "clrInstance",
                    ["type"] = Tw(awaitableType),
                    ["method"] = "ConfigureAwait",
                    ["recv"] = task,
                    ["argTypes"] = new JsonArray { Tw(BoolTn) },
                    ["args"] = new JsonArray { captureValue },
                    ["ret"] = Tw(configuredType),
                };
                if (!plan.ConfiguredGetAwaiterExtension)
                    return new JsonObject
                    {
                        ["k"] = "clrInstance",
                        ["type"] = Tw(configuredType),
                        ["method"] = "GetAwaiter",
                        ["recv"] = configured,
                        ["argTypes"] = new JsonArray(),
                        ["args"] = new JsonArray(),
                        ["ret"] = Tw(awaiterType),
                    };
                var cfgExtOwner = new TypeNode.Fqn(plan.ConfiguredGetAwaiterExtOwner);
                if (!plan.ConfiguredGetAwaiterExtGeneric)
                    return new JsonObject
                    {
                        ["k"] = "clrStatic",
                        ["type"] = Tw(cfgExtOwner),
                        ["method"] = "GetAwaiter",
                        ["argTypes"] = new JsonArray { Tw(configuredType) },
                        ["args"] = new JsonArray { configured },
                        ["ret"] = Tw(awaiterType),
                    };
                // A GENERIC extension: the plan resolved its type arguments by unifying the declared receiver against
                // the configured type, and carries that receiver open over `method.tvN` as bir2cir's internal
                // `resolvedMemberParams` input for exact memberRef resolution.
                return new JsonObject
                {
                    ["k"] = "clrGenericStatic",
                    ["type"] = Tw(cfgExtOwner),
                    ["method"] = "GetAwaiter",
                    ["typeArgs"] = new JsonArray(plan.ConfiguredGetAwaiterExtTypeArgs
                        .Select(t => TypeJson.Write(CloseAwaitTemplate(t, awaitableType as TypeNode.Fqn, methodTypeArgs)))
                        .ToArray()),
                    ["resolvedMemberParams"] = new JsonArray { TypeJson.Write(plan.ConfiguredGetAwaiterExtOpenRecv) },
                    ["args"] = new JsonArray { configured },
                    ["ret"] = Tw(awaiterType),
                };
            }
            if (plan.GetAwaiterExtension)
            {
                var extOwner = new TypeNode.Fqn(plan.GetAwaiterExtOwner);
                if (plan.GetAwaiterExtGeneric)
                {
                    // clrGenericStatic ExtClass.GetAwaiter<resultTok>(awaitable). W1-S1 (#46): carry the resolved
                    // member descriptor `resolvedMemberParams` = the OPEN declared receiver param `Awaitable<T>` (the awaitable
                    // constructor over the method type-var), so ilemit exact-matches the `GetAwaiter<T>` extension def.
                    var openAwaitable = awaitableType is TypeNode.Fqn af
                        ? new TypeNode.Fqn(
                            af.Name,
                            af.Args?.Select((_, i) =>
                                (TypeNode)new TypeNode.Tv("method", i)).ToArray())
                        : awaitableType;
                    return new JsonObject
                    {
                        ["k"] = "clrGenericStatic",
                        ["type"] = Tw(extOwner),
                        ["method"] = "GetAwaiter",
                        ["typeArgs"] = new JsonArray(
                            methodTypeArgs.Select(TypeJson.Write).ToArray()),
                        ["resolvedMemberParams"] = new JsonArray { TypeJson.Write(openAwaitable) },
                        ["args"] = new JsonArray { task },
                        ["ret"] = Tw(awaiterType),
                    };
                }
                return new JsonObject
                {
                    ["k"] = "clrStatic",
                    ["type"] = Tw(extOwner),
                    ["method"] = "GetAwaiter",
                    ["argTypes"] = new JsonArray { Tw(awaitableType) },
                    ["args"] = new JsonArray { task },
                    ["ret"] = Tw(awaiterType),
                };
            }
            // member GetAwaiter — clrInstance on the awaitable.
            return new JsonObject
            {
                ["k"] = "clrInstance",
                ["type"] = Tw(awaitableType),
                ["method"] = "GetAwaiter",
                ["recv"] = task,
                ["argTypes"] = new JsonArray(),
                ["args"] = new JsonArray(),
                ["ret"] = Tw(awaiterType),
            };
        }

        // void $awaitOnDone$state() { this.intercepted().resumeWith(Result(null)); } — the OnCompleted Action target
        // that re-drives THIS SM through the interceptor-aware continuation (#7 Part B; Option B WAKE TOKEN, the
        // value/fault flows from GetResult at the resume label). `this.intercepted()` (ContinuationImpl.intercepted)
        // yields the ContinuationInterceptor-wrapped continuation when the context has one — that wrapper's resumeWith
        // owns the resume dispatch (interceptor > captured SyncContext) — else the identity `this`. The resumeWith
        // call mirrors the rt-stdlib BaseContinuationImpl form (Continuation<object>.resumeWith, Result(object)
        // construction); ContinuationErasure then normalizes both to the Result<object> slot.
        JsonObject AwaitResumeMethod(string name)
        {
            var resumeCall = new JsonObject
            {
                ["k"] = "callInstance",
                ["ownerType"] = ContAny(),
                ["virtual"] = true,
                // this.intercepted(): the interceptor decides the resume thread/context (precedence over SyncContext).
                ["recv"] = new JsonObject
                {
                    ["k"] = "callInstance",
                    ["ownerType"] = Tn(ContinuationImplFqn),
                    ["virtual"] = true,
                    ["recv"] = new JsonObject { ["k"] = "this" },
                    ["method"] = "intercepted",
                    ["sig"] = new JsonArray(),
                    ["ret"] = ContAny(),
                    ["args"] = new JsonArray(),
                },
                ["method"] = "resumeWith",
                ["sig"] = new JsonArray { Tw(new TypeNode.Fqn("kotlin.Result", new TypeNode[] { AnyTn })) },
                ["ret"] = Tw(VoidTn),
                ["args"] = new JsonArray
                {
                    // Result.success(null): the PUBLIC static companion factory (the internal `Result(value)` ctor is
                    // inaccessible cross-assembly, so an app SM cannot `new kotlin.Result`). typeArgs erased to Any by
                    // ContinuationErasure to hit the Result<object> resumeWith slot.
                    CompanionFactoryCall(
                        "kotlin.Result",
                        "success",
                        new JsonArray { Tn("kotlin.Any") },
                        new JsonArray { Tw(new TypeNode.Tv("method", 0)) },
                        new JsonArray { NullConst(AnyTn) },
                        Tw(new TypeNode.Fqn("kotlin.Result", new TypeNode[] { AnyTn }))),
                },
            };
            return new JsonObject
            {
                ["name"] = name,
                ["static"] = false,
                ["override"] = false,
                ["virtual"] = false,
                ["abstract"] = false,
                ["objectOverride"] = false,
                ["vis"] = "public",
                ["params"] = new JsonArray(),
                ["ret"] = Tw(VoidTn),
                ["body"] = new JsonArray { new JsonObject { ["k"] = "exprStmt", ["expr"] = resumeCall } },
                ["attrs"] = new JsonArray(),
            };
        }

        // The static type of a value node in THIS frame's vocabulary. It is the SHARED node-local deriver
        // (bir-common/NodeType.cs — which owns the stamp precedence `sty`/`ret`/`dynRet` and every kind's own result
        // slot, including a stamp-less `cond` typed by its LIVE branch) plus the ONE arm only the frame can answer: a
        // `local` read, whose type lives on the `var`/parameter that DECLARES it rather than on the read. Same shape
        // as SuspendOperandPlan.ExprTyper's scope arm; the declaration wins over a read's stamp because what this
        // types is a DECLARED slot, which must hold the local's full declared type and not a smart-cast narrowing.
        //
        // Structural array spelling on purpose (NOT StaticType.Surface's name-keyed `kotlin.Array<E>`, which its
        // classifiers match): a declared slot needs the structural form (spec §2.7 *One deriver, two layers*).
        //
        // NULL is a real answer and callers must report it — `kotlin.Any` is not a fallback for a slot type, it boxes
        // a value type and hides a type the CLR would refuse.
        TypeNode FrameType(JsonNode n)
        {
            if (n is JsonObject o && Str(o["k"]) == "local" && Str(o["name"]) is string ln
                && _localTypes.TryGetValue(ln, out var lt) && lt != null)
                return lt;
            return NodeType.Of(n, FrameType,
                               name => BirTypeLowering.PrimArrayElem.TryGetValue(name, out var e) ? e : null);
        }

        // A node's kind for a diagnostic.
        static string Kind(JsonNode n) => Str((n as JsonObject)?["k"]) ?? "<none>";

        // The declared type of an SM slot by name. A slot is either an SM FIELD or — since the storage gate demotes
        // every local that is dead across each suspension — a MoveNext LOCAL, and a lexical type lookup
        // (IsSuspendValueCallInScope resolving a bare inline-materialized receiver's `fn` type) must find either.
        TypeNode FieldType(string name)
        {
            foreach (var (n, t) in _fieldDecls) if (n == name) return t;
            return _localTypes.TryGetValue(name ?? "", out var lt) ? lt : null;
        }

        TypeNode RequiredFieldType(string name) => FieldType(name) ?? throw new NotSupportedException(
            $"bir2cir: suspend-lowering: state-machine slot `{name}` in `{DiagOwner}` carries no static type.");

        TypeNode RequiredSlotType(string name) => FieldType(name) ?? throw new NotSupportedException(
            $"bir2cir: suspend-lowering: local slot `{name}` in `{DiagOwner}` carries no static type.");

        // kotc names a suspend lambda's captured ENCLOSING extension receiver `__outer` (the `<this>` capture-field
        // convention, BirEmitter.kt:2929) yet, INSIDE the lambda body, references that receiver as `local __self`
        // (the enclosing static extension's receiver-param name, via selfSubst — BirEmitter.kt:1308). The two names
        // are the SAME captured value, so a body `__self` read maps to the `__outer` capture FIELD (`this.__outer`).
        // Guarded on `!__self` field: a NAMED-fun cold entry spills its real `__self` PARAM into a `__self` field,
        // which the generic local->field rule already redirects — this alias is only for the lambda-capture mismatch.
        JsonNode CapturedSelfField() =>
            (!_fields.Contains("__self") && _fields.Contains("__outer"))
                ? FieldOf("__outer", RequiredFieldType("__outer")) : null;

        // #34a — a suspend LAMBDA that closes over its enclosing INSTANCE captures it as the `__outer`
        // field (SuspendLambdaLowering seeds the ctor arg from the enclosing `this`/`__self`). kotc emits references
        // to that instance's members as a bare `this.member` (recv `{k:"this"}`) inside the lambda body, but inside the
        // SM `this` is the SM itself — so the body `this` must read the captured `__outer` field (`this.__outer`).
        // An extension receiver is never inferred from this token: kotc/InlineSplice name its create()-set parameter
        // explicitly as a local, and the ordinary local-to-field rewrite handles it. Only synthesized SM-self nodes
        // use the `smSelf` marker, so they are unaffected. Absent an `__outer` capture there is nothing to redirect.
        JsonNode CapturedOuterField() =>
            (_isLambda && _fields.Contains("__outer"))
                ? FieldOf("__outer", RequiredFieldType("__outer")) : null;

        // GAP 2 — copy a `newSuspendLambda` verbatim (its body is the lambda's own scope, left for
        // SuspendLambdaLowering) and attach `capValues`: each capture's construction value resolved into THIS cold
        // SM's vocabulary. SuspendLambdaLowering builds `new <lambdaSM>(capValues..., null)` at this exact site.
        JsonObject RewriteSuspendLambdaNew(JsonObject o)
        {
            var copy = (JsonObject)o.DeepClone();
            // A pre-stamped `capValues` override (kotc E1 "one value channel", or InlineSplice 2B — a spliced payload
            // lambda's `__outer` rebound to the splice's receiver TEMP) is the capture's construction VALUE in the
            // ENCLOSING frame's vocabulary: a `{k:local,name:X}` naming an enclosing local (a selfSubst inline-receiver
            // rename `__recv40`, a spilled temp), a `{k:field,recv:{k:this},…}` naming an enclosing SAM/closure field
            // (`second`), or `{k:this}`. GAP 2 must RESOLVE it into THIS cold SM's vocabulary — a spilled local -> its SM
            // field, `this`/`__self` -> the member SM's `$this`/`__outer` — which is EXACTLY RewriteNoSpill's suspension-free
            // subtree rewrite. Do NOT clobber it with the descriptor-name synthesis (which would re-derive the caller SM's
            // own receiver -> a silent __outer mis-bind / InvalidProgram). Merge per-slot: override present -> RewriteNoSpill;
            // absent (null slot) -> the name-derived synthesis.
            var overrides = o["capValues"] as JsonArray;
            var capValues = new JsonArray();
            if (o["captures"] is JsonArray caps)
            {
                int i = 0;
                foreach (var c in caps.OfType<JsonObject>())
                {
                    if (overrides != null && i < overrides.Count && overrides[i] is JsonNode ov)
                        capValues.Add(RewriteNoSpill(ov.DeepClone()));
                    else
                        capValues.Add(CaptureValueInSm(Str(c["name"]), Str(c["type"])));
                    i++;
                }
            }
            copy["capValues"] = capValues;
            return copy;
        }

        // The value of a captured name AS SEEN from inside this cold SM's invokeSuspend: the enclosing instance
        // (`__outer`) is the member SM's `$this` field (or a spilled `__self`/`__outer` for an extension/lambda SM);
        // a captured plain local that was spilled is the matching SM field; anything else is a still-live local.
        JsonNode CaptureValueInSm(string name, string type)
        {
            if (name == "__outer")
            {
                if (_isMember) return FieldOf(ThisField, _selfType);
                if (_fields.Contains("__self")) return FieldOf("__self", RequiredFieldType("__self"));
                if (_isLambda && _fields.Contains("__outer")) return FieldOf("__outer", RequiredFieldType("__outer"));
                return new JsonObject { ["k"] = "this" };
            }
            if (_fields.Contains(name)) return FieldOf(name, RequiredFieldType(name));
            return new JsonObject { ["k"] = "local", ["name"] = name };
        }

        // The cold call. Shapes (same-assembly callStatic/callInstance, and — BUG 1 — the CROSS-ASSEMBLY
        // clr forms kotc emits for a referenced suspend callee):
        //   callStatic         -> <method>$dotkt_suspend(<args>, cast(this))                     (owner preserved)
        //   callInstance       -> recv.<method>$dotkt_suspend(<args>, cast(this))
        //   clrStatic          -> LibKt.<method>$dotkt_suspend(<args>, cast(this))               (referenced owner)
        //   clrInstance        -> recv.<method>$dotkt_suspend(<args>, cast(this))                (referenced owner)
        //   clrGenericStatic   -> LibKt.<method>$dotkt_suspend<T>(<args>, cast(this))
        //   clrGenericInstance -> recv.<method>$dotkt_suspend<T>(<args>, cast(this))
        // `this` (the caller SM, a Continuation) is the callee's completion. typeArgs are preserved. Args/receiver
        // are rewritten (redirecting locals/`this`).
        JsonObject ColdCall(JsonObject callNode, List<JsonNode> outp)
        {
            // GAP 1: a call to a suspend functional VALUE has no named cold entry — drive it through the stdlib
            // cold-invoke helper `startSuspendUninterceptedOrReturn(fn, [receiver,] completion)`.
            if (IsSuspendValueCallInScope(callNode)) return SuspendValueColdCall(callNode, outp);

            var k = Str(callNode["k"]);
            var method = Str(callNode["method"]) + "$dotkt_suspend";
            var coldDeclarationId = Str(callNode[DeclarationIdentityBinding.Key]) is string sourceDeclarationId
                ? sourceDeclarationId + "|cold" : null;
            var isClr = k is "clrStatic" or "clrInstance" or "clrGenericStatic" or "clrGenericInstance";
            var isGeneric = k is "clrGenericStatic" or "clrGenericInstance";
            // Evaluate recv (instance) then args LEFT-TO-RIGHT, over the shared operand description. Stage 0 has
            // already lifted every suspension-bearing operand of a SUSPEND call into a `var` ahead of it
            // (SuspendOperandPlan.cs), so what is left here appends no statements of its own and the rewrite is a
            // plain ordered walk.
            var order = EvalOrderOf(callNode).Value;
            var isInstance = order.HasReceiver;
            // A same-assembly super-qualified suspend call already names its exact base declaration and non-virtual
            // dispatch. The cold rewrite mints a fresh call node, so carry that frontend fact just as NetInteropBinding
            // does for an ordinary call reshape. Otherwise the later inherited-owner binding treats the cold call as
            // an ordinary receiver call and restores the declaration's virtual bit, redispatching into an override.
            // UnsafeAccessorLowering may then move the call from the nested state machine into a private forwarder on
            // the derived owner. Preserve the declaration's method-generic frame for that synthesis, but not memberSignature
            // or memberReturnType: those describe the hot entry and omit the cold ABI's continuation slot.
            void CarryLocalSuper(JsonObject target)
            {
                if (!isInstance || callNode["super"] is not JsonNode superNode) return;
                target["super"] = superNode.DeepClone();
                if (callNode["memberMethodTypeParams"] is JsonNode methodTypeParams)
                    target["memberMethodTypeParams"] = methodTypeParams.DeepClone();
            }
            var rw = order.Operands.Select(x => x == null ? null : Rewrite(x, outp)).ToList();
            var ri = order.ArgumentStart;
            var recvRw = isInstance ? rw[0] : null;
            var args = new JsonArray();
            for (; ri < rw.Count; ri++) args.Add(rw[ri]);
            var completion = new JsonObject
            {
                ["k"] = "cast",
                ["type"] = ContAny(),
                ["e"] = new JsonObject { ["k"] = "this" },
            };
            args.Add(completion);

            if (isClr)
            {
                // R1b (#100) — the clr* rewrite assumes the referenced owner exposes a `<name>$dotkt_suspend` cold
                // entry. Under R1's unconditional-declaration invariant that holds for any DotKt assembly built with
                // the cold ABI, advertised on the ref.dll via the [KotlinFunction(Suspend)] flag (MemberBinding.Suspend)
                // — the metadata linkage. The provisional cold-entry name is replaced through the selected declaration
                // identity with the independently allocated MethodDef carried by both reference and runtime twins.
                // Consult the suspend flag through the reflected hierarchy (a member declared on a super is inherited).
                // ABSENT => the callee is not a cold-ABI Kotlin suspend member (a pre-cold-ABI DotKt lib, or a
                // hand-written assembly): a HARD actionable error, not a silent rewrite to a nonexistent method (the
                // #100 emit-time resolution failure this guard replaces). The CLR await bridge marker + suspendCoroutine
                // intrinsics are intercepted upstream in Rewrite and a suspend functional VALUE is diverted at the top
                // of ColdCall, so a flag-absent owner HERE is a genuine named cross-assembly suspend callee.
                var refOwner = TypeJson.OwnerName(callNode["type"]);
                var calleeName = Str(callNode["method"]);
                if (refOwner != null && calleeName != null && !_refs.HasSuspendMemberInHierarchy(refOwner, calleeName))
                    throw new NotSupportedException(
                        $"bir2cir: suspend-lowering: the referenced owner '{refOwner}' exposes a suspend call target "
                        + $"'{calleeName}' without the cold-entry ABI ([KotlinFunction(Suspend)] absent on the ref.dll). "
                        + "The referenced assembly predates the cold-entry ABI (or is a hand-written .NET assembly that "
                        + "is not a Kotlin suspend member). Rebuild it with a cold-ABI DotKt toolchain — there is no "
                        + "dual-track fallback.");
                // A referenced suspend callee: keep the `clr*` node kind + referenced `type` owner, retarget the method
                // to the cold entry, append the completion. ilemit's EmitClrCall/ResolveGenericMethod resolves the cold
                // entry on the referenced assembly by name (uniquely named) + arg/shape — no fileClass sig lookup.
                var clr = new JsonObject
                {
                    ["k"] = k,
                    ["type"] = callNode["type"]?.DeepClone(),
                    ["method"] = method,
                    ["args"] = args,
                    ["ret"] = Tw(AnyTn),
                };
                if (isInstance) clr["recv"] = recvRw;
                if (coldDeclarationId != null) clr[DeclarationIdentityBinding.Key] = coldDeclarationId;
                if (isGeneric)
                {
                    // clrGeneric* resolves by the structured `resolvedMemberParams` descriptor (W1-S1 #46). Preserve typeArgs; append
                    // the cold entry's trailing completion param `Continuation<Any>` to the hot callee's declared params so
                    // ilemit exact-matches the cold entry (which has one extra trailing param), not the hot signature.
                    if (callNode["typeArgs"] is JsonArray gta) clr["typeArgs"] = gta.DeepClone();
                    var ms = (callNode["resolvedMemberParams"] as JsonArray)?.DeepClone() as JsonArray ?? new JsonArray();
                    ms.Add(ContAny());
                    clr["resolvedMemberParams"] = ms;
                }
                else
                {
                    // clrStatic/clrInstance resolves by argTypes. Append the completion's type (clrg: so ilemit's ClrRef
                    // constructs Continuation<object>, the exact cold-entry `completion` param type after lowering).
                    var argTypes = (callNode["argTypes"] as JsonArray)?.DeepClone() as JsonArray ?? new JsonArray();
                    argTypes.Add(ContAny());
                    clr["argTypes"] = argTypes;
                }
                return clr;
            }

            JsonObject call;
            if (isInstance)
            {
                call = new JsonObject
                {
                    ["k"] = "callInstance",
                    ["ownerType"] = callNode["ownerType"]?.DeepClone(),
                    ["virtual"] = Bool(callNode["virtual"]),
                    ["recv"] = recvRw,
                    ["method"] = method,
                    ["args"] = args,
                    ["ret"] = Tw(AnyTn),
                };
                CarryLocalSuper(call);
            }
            else
            {
                call = new JsonObject
                {
                    ["k"] = "callStatic",
                    ["owner"] = callNode["owner"]?.DeepClone(),
                    // #199 Design B: carry the callee-file-class DISPATCH hint on the rewritten owner-null cold call, so
                    // ilemit dispatches `<method>$dotkt_suspend` to the correct same-simple-name package's file class.
                    ["calleeOwner"] = callNode["calleeOwner"]?.DeepClone(),
                    ["method"] = method,
                    ["args"] = args,
                    ["ret"] = Tw(AnyTn),
                };
            }
            if (callNode["typeArgs"] is JsonArray ta) call["typeArgs"] = ta.DeepClone();
            if (coldDeclarationId != null) call[DeclarationIdentityBinding.Key] = coldDeclarationId;
            if (!isInstance)
                ClrMemberResolution.CarryReferencedStaticCallSignatureSnapshot(callNode, call);
            // BUG Y — overload disambiguation. `<method>$dotkt_suspend` may be one of several same-named IL
            // overloads (SequenceScope.yieldAll has 3: Iterator/Iterable/Sequence), which ilemit resolves via
            // MethodsBySig on the param-type signature. Synthesize the call `sig` = the ORIGINAL call's param
            // types + the appended completion param type (ContinuationOfAny — the exact `completion` slot type
            // ColdMethod/ColdEntryAbstract give the cold entry). This runs in PHASE 1.5, BEFORE type lowering,
            // so the sig's kotlin.* tokens are lowered together with the rest and string-match the def's lowered
            // `params[].type`. Without it, FindMethod falls to an ARBITRARY `ti.Methods[name]` -> wrong overload
            // -> arg/param mismatch -> BadImageFormatException. (yield works today only because it has ONE overload.)
            // Structured sig (#37 m3b): the ORIGINAL call's param TypeNodes + the appended `completion` slot
            // (Continuation<Any>). ilemit resolves the `<method>$dotkt_suspend` overload by this structured signature.
            // BIR spells the resolved descriptor of an instance call as `argTypes`, a static call's as `sig`, and a
            // GENERIC member's as `shapeTypes` — the declared parameter vector with the callee's own type variables
            // still open (`f<T>(a: T, b: Int)` -> `[tv method 0, kotlin.Int]`). All three are the same fact under
            // three names, so preserve whichever the frontend supplied before appending the cold ABI's completion
            // slot. Falling back to an empty signature here used to be masked by ilemit's name/arity lookup and made
            // every referenced non-zero-argument suspend member ambiguous or unlinkable once CIR signature
            // consumption became exact — and `shapeTypes` was the one spelling still missing, so a cross-module
            // GENERIC suspend member call resolved to `<name>$dotkt_suspend(Continuation)` and did not link at all.
            var sigArr = callNode["sig"] is JsonArray os
                ? (JsonArray)os.DeepClone()
                : callNode["argTypes"] is JsonArray oat
                    ? (JsonArray)oat.DeepClone()
                    : callNode["shapeTypes"] is JsonArray osh
                        ? (JsonArray)osh.DeepClone()
                        : new JsonArray();
            sigArr.Add(ContAny());
            call["sig"] = sigArr;
            return call;
        }

        bool IsSuspendValueCallInScope(JsonObject call)
        {
            if (Str(call["k"]) != "callInstance" || !Bool(call["suspendCall"]) || Str(call["method"]) != "invoke")
                return false;
            if (IsSuspendFunctionValue(call["recv"])) return true;

            // InlineSplice may replace a typed lambda parameter with a fresh bare local (`__inlmatN`). The local's
            // structured `fn` type lives on its `var` declaration and has already been collected into this SM's field
            // table; resolve it lexically instead of falling back to the call owner's implementation-class name.
            return call["recv"] is JsonObject recv
                && Str(recv["k"]) == "local"
                && Str(recv["name"]) is string local
                && FieldType(local) is TypeNode.Fn { Suspend: true };
        }

        // GAP 1 — the cold-invoke of a suspend functional VALUE. The value `fn` (`b()`'s receiver) is a cold,
        // unstarted SuspendLambda state machine (a BaseContinuationImpl). Driving it as a suspension point is the
        // SAME machinery as a named cold call — set label, start it passing THIS SM (a Continuation) as its
        // completion, check COROUTINE_SUSPENDED — except the "start" is the stdlib helper
        // `startSuspendUninterceptedOrReturn(fn, [receiver,] completion)` (= `create(completion).invokeSuspend(Unit)`)
        // rather than a `<name>$dotkt_suspend` call. EmitSuspensionPoint owns the label/suspend/resume dance and
        // the result typing (from the invoke's `retType`); this only builds the start call.
        //   arity-0 (`b()`):    startSuspendUninterceptedOrReturn<Any>(fn, completion)
        //   arity-1 (`b(x)`):    startSuspendUninterceptedOrReturn<Any,Any>(fn, x, completion)   // x -> receiver R
        //   arity-N (`b(x,y)`):  startSuspendUninterceptedOrReturnN<Any>(fn, arrayOf<Any?>(x,y,…), completion)
        //     — the fixed create()/2-3-arg helpers only cover 0/1, so N>=2 boxes the args into an Array<Any?> and
        //     drives the value through the general create(args, completion) slot (the SM overrides it, unpacking
        //     args[i] into its param fields). The invoke args are the receiver + params in SuspendFunctionN order.
        JsonObject SuspendValueColdCall(JsonObject callNode, List<JsonNode> outp)
        {
            // Evaluate the value receiver then any invoke arg LEFT-TO-RIGHT, over the shared operand description. A
            // suspend-VALUE invoke is a `callInstance`, so that description always carries its receiver first; stage 0
            // has already lifted any suspension-bearing operand out of it (SuspendOperandPlan.cs).
            var order = EvalOrderOf(callNode).Value;
            var rw = order.Operands.Select(x => x == null ? null : Rewrite(x, outp)).ToList();
            var recvRw = rw[0];
            var invokeArgs = rw.Skip(order.ArgumentStart).ToList();   // 0 / 1 / N (SuspendFunction0 / 1 / N)

            var completion = new JsonObject
            {
                ["k"] = "cast",
                ["type"] = ContAny(),
                ["e"] = new JsonObject { ["k"] = "this" },
            };

            if (invokeArgs.Count >= 2)
            {
                // arity >= 2: box the N invoke args into an Array<Any?> and call the general N-arg start helper.
                var elems = new JsonArray();
                foreach (var a in invokeArgs) elems.Add(a);
                var argArray = new JsonObject { ["k"] = "newArray", ["elem"] = Tw(AnyTn), ["elems"] = elems };
                return new JsonObject
                {
                    ["k"] = "callStatic",
                    ["owner"] = Tn(StartSuspendOwner),
                    ["method"] = "startSuspendUninterceptedOrReturnN",
                    ["typeArgs"] = new JsonArray { Tw(AnyTn) },        // T (result) — erased
                    // fn:Any, args:Array<Any?>, completion:Continuation<Any> — discriminates the N-arg overload.
                    ["sig"] = new JsonArray { Tw(AnyTn), Tw(new TypeNode.Array(new TypeNode.Nullable(AnyTn))), ContAny() },
                    ["args"] = new JsonArray { recvRw, argArray, completion },
                    ["ret"] = Tw(AnyTn),
                };
            }

            // args = fn, [receiver], completion ; the helper is generic (<T> arity-0 / <R,T> arity-1), erased to Any.
            var args = new JsonArray { recvRw };
            foreach (var a in invokeArgs) args.Add(a);
            args.Add(completion);
            var typeArgs = new JsonArray { Tw(AnyTn) };            // T (result) — erased
            if (invokeArgs.Count == 1) typeArgs.Add(Tw(AnyTn));    // R (receiver) — erased
            // sig discriminates the fixed-arity overloads (2/3 params): fn:Any, [receiver:Any], completion:Continuation.
            // Structured TypeNode array (#37 m3b).
            var sigArr = new JsonArray { Tw(AnyTn) };
            for (var i = 0; i < invokeArgs.Count; i++) sigArr.Add(Tw(AnyTn));
            sigArr.Add(ContAny());

            return new JsonObject
            {
                ["k"] = "callStatic",
                ["owner"] = Tn(StartSuspendOwner),
                ["method"] = "startSuspendUninterceptedOrReturn",
                ["typeArgs"] = typeArgs,
                ["sig"] = sigArr,
                ["args"] = args,
                ["ret"] = Tw(AnyTn),
            };
        }

        // ---- declaration synthesis ----

        JsonObject SmType(JsonArray invokeBody)
        {
            var fields = new JsonArray();
            foreach (var (n, t) in _fieldDecls)
                fields.Add(new JsonObject { ["name"] = n, ["type"] = Tw(t), ["vis"] = "internal" });

            var ctorParams = new JsonArray();
            var ctorBody = new JsonArray();
            if (_isMember)
            {
                ctorParams.Add(new JsonObject { ["name"] = ThisField, ["type"] = Tw(_selfType) });
                ctorBody.Add(SetField(ThisField, new JsonObject { ["k"] = "local", ["name"] = ThisField }));
            }
            foreach (var p in _params)
            {
                var pn = Str(p["name"]);
                ctorParams.Add(new JsonObject { ["name"] = pn, ["type"] = p["type"]?.DeepClone() });
                ctorBody.Add(SetField(pn, new JsonObject { ["k"] = "local", ["name"] = pn }));
            }
            ctorParams.Add(new JsonObject { ["name"] = "completion", ["type"] = ContAny() });

            var invoke = new JsonObject
            {
                ["name"] = "invokeSuspend",
                ["static"] = false,
                ["override"] = _baseIsLocal,
                ["virtual"] = false,
                ["abstract"] = false,
                ["objectOverride"] = false,
                ["vis"] = "public",
                ["params"] = new JsonArray { new JsonObject { ["name"] = "result", ["type"] = Tw(AnyTn) } },
                ["ret"] = Tw(AnyTn),
                ["body"] = invokeBody,
                ["attrs"] = new JsonArray(),
            };
            if (!_baseIsLocal)
            {
                invoke["pendingOverrideOwner"] = Tn(BaseContinuationImplFqn);
                invoke["pendingOverrideReturn"] = Tw(AnyTn);
            }

            var methods = new JsonArray { invoke };
            foreach (var rm in _awaitResumeMethods) methods.Add(rm);

            var type = new JsonObject
            {
                ["name"] = _smType,
                ["kind"] = "class",
                ["generated"] = true,
                ["semanticOwner"] = _ownerClass ?? _fileClass,
                ["abstract"] = false,
                ["vis"] = "public",
                ["base"] = Tn(ContinuationImplFqn),
                ["interfaces"] = new JsonArray(),
                ["fields"] = fields,
                ["ctors"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["params"] = ctorParams,
                        // Call the 1-arg ContinuationImpl(completion) base ctor, which threads _context =
                        // completion?.context (#7 Part B). The old 2-arg form pinned _context = null (context =
                        // EmptyCoroutineContext), so a named-fun cold-entry SM never inherited its completion's
                        // context — context[ContinuationInterceptor] would always miss and the interceptor could
                        // not take precedence at a nested-fun await. Threading the completion context propagates
                        // the interceptor (and any other context element) down the cold-entry call chain.
                        ["baseArgs"] = new JsonArray
                        {
                            new JsonObject { ["k"] = "local", ["name"] = "completion" },
                        },
                        ["delegationSig"] = new JsonArray { NullableContAny() },
                        ["thisArgs"] = null,
                        ["vis"] = "public",
                        ["body"] = ctorBody,
                    },
                },
                ["methods"] = methods,
                ["properties"] = new JsonArray(),
                ["attrs"] = new JsonArray(),
            };
            // The SM is generic over the owner's type params (instance member on a generic class) PLUS the
            // member's own — `$this`/fields/label reference them (see _smAllTps / _smTypeInst).
            if (_smAllTps.Count > 0)
            {
                var tp = new JsonArray();
                foreach (var declaration in _ownerTypeParamDecls)
                    tp.Add(declaration?.DeepClone());
                foreach (var declaration in _methodTypeParamDecls)
                    tp.Add(declaration?.DeepClone());
                type["typeParams"] = tp;
            }
            if (_ownerTypeParams.Count > 0)
                type["outerTypeParamCount"] = _ownerTypeParams.Count;
            RebindMethodTypeVariablesToSm(type, _ownerTypeParams.Count);
            return type;
        }

        // The suspend-LAMBDA SM (Part B): `<smName>[<tp>] : SuspendLambda`. Mirrors SmType but (1) the base is
        // SuspendLambda (ctor `base(arity, completion)`), (2) the ctor params are the CAPTURES (+ completion),
        // NOT the lambda's own params, and (3) it adds the create() override(s) — the cold-lambda VALUE protocol
        // (createCoroutineUnintercepted / startCoroutineUninterceptedOrReturn: IntrinsicsClr.kt:42-56). The
        // lambda's own params are fields set by create() on the fresh instance, not by the ctor.
        JsonObject SmTypeLambda(JsonArray invokeBody)
        {
            var lambdaBaseFqn = _restrictedBase ? RestrictedSuspendLambdaFqn : SuspendLambdaFqn;
            var fields = new JsonArray();
            foreach (var (n, t) in _fieldDecls)
                fields.Add(new JsonObject { ["name"] = n, ["type"] = Tw(t), ["vis"] = "internal" });

            var ctorParams = new JsonArray();
            var ctorBody = new JsonArray();
            foreach (var (n, t) in _captures)
            {
                ctorParams.Add(new JsonObject { ["name"] = n, ["type"] = Tw(t) });
                ctorBody.Add(SetField(n, new JsonObject { ["k"] = "local", ["name"] = n }));
            }
            ctorParams.Add(new JsonObject { ["name"] = "completion", ["type"] = ContAny() });

            var invoke = new JsonObject
            {
                ["name"] = "invokeSuspend",
                ["static"] = false,
                ["override"] = _baseIsLocal,
                ["virtual"] = false,
                ["abstract"] = false,
                ["objectOverride"] = false,
                ["vis"] = "public",
                ["params"] = new JsonArray { new JsonObject { ["name"] = "result", ["type"] = Tw(AnyTn) } },
                ["ret"] = Tw(AnyTn),
                ["body"] = invokeBody,
                ["attrs"] = new JsonArray(),
            };
            if (!_baseIsLocal)
            {
                invoke["pendingOverrideOwner"] = Tn(BaseContinuationImplFqn);
                invoke["pendingOverrideReturn"] = Tw(AnyTn);
            }

            var methods = new JsonArray { invoke };
            foreach (var cm in CreateMethods()) methods.Add(cm);
            foreach (var rm in _awaitResumeMethods) methods.Add(rm);

            var type = new JsonObject
            {
                ["name"] = _smType,
                ["kind"] = "class",
                ["generated"] = true,
                ["abstract"] = false,
                ["vis"] = "public",
                ["base"] = Tn(lambdaBaseFqn),
                ["interfaces"] = new JsonArray(),
                ["fields"] = fields,
                ["ctors"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["params"] = ctorParams,
                        // (Restricted)SuspendLambda(arity: Int, completion: Continuation<Any?>?) — the 2-arg base ctor.
                        ["baseArgs"] = new JsonArray
                        {
                            IntConst(_arity),
                            new JsonObject { ["k"] = "local", ["name"] = "completion" },
                        },
                        ["delegationSig"] = new JsonArray { Tn("kotlin.Int"), NullableContAny() },
                        ["thisArgs"] = null,
                        ["vis"] = "public",
                        ["body"] = ctorBody,
                    },
                },
                ["methods"] = methods,
                ["properties"] = new JsonArray(),
                ["attrs"] = new JsonArray(),
            };
            if (_smAllTps.Count > 0)
            {
                var tp = new JsonArray();
                foreach (var declaration in _ownerTypeParamDecls)
                    tp.Add(declaration?.DeepClone());
                foreach (var declaration in _methodTypeParamDecls)
                    tp.Add(declaration?.DeepClone());
                type["typeParams"] = tp;
            }
            if (_ownerTypeParams.Count > 0)
                type["outerTypeParamCount"] = _ownerTypeParams.Count;
            RebindMethodTypeVariablesToSm(type, _ownerTypeParams.Count);
            return type;
        }

        // The create() override(s) — a fresh SM bound to a new completion, carrying THIS SM's captures.
        //   arity-0:  create(completion): Continuation            -> new SM(captures..., completion)
        //   arity-1:  create(value, completion): Continuation     -> sm = new SM(captures..., completion);
        //                                                             sm.<param> = value; return sm
        //   arity-N:  create(args: Array<Any?>, completion): Continuation
        //                 -> sm = new SM(captures..., completion); sm.<p0> = args[0]; …; sm.<pN-1> = args[N-1]; return sm
        // Matches BaseContinuationImpl.create's CLR ABI: params (Continuation<*> existential) / (object,
        // Continuation<*> existential) / (object[], Continuation<*> existential), return Continuation<Unit>.
        // bir2cir resolves these physical params to one clrOverrideRef before ilemit sees the declaration.
        IEnumerable<JsonObject> CreateMethods()
        {
            if (_arity == 0)
            {
                yield return CreateMethod(
                    new JsonArray { new JsonObject { ["name"] = "completion", ["type"] = ContStar() } },
                    new JsonArray { Ret(NewSm()) });
            }
            else if (_arity >= 2)
            {
                // arity >= 2: override create(args: Array<Any?>, completion). Allocate the SM, unpack each boxed
                // arg into its param field (the same object -> param cast the arity-1 path uses), return it.
                var body = new JsonArray
                {
                    new JsonObject { ["k"] = "var", ["name"] = "__sm", ["type"] = Tw(_smTypeInst), ["init"] = NewSm() },
                };
                for (var i = 0; i < _params.Count; i++)
                {
                    var paramName = Str(_params[i]["name"]);
                    var paramType = RequiredParamType(_params[i]);
                    JsonNode elem = new JsonObject
                    {
                        ["k"] = "arrayGet",
                        ["elem"] = Tw(AnyTn),
                        ["array"] = new JsonObject { ["k"] = "local", ["name"] = "args" },
                        ["index"] = IntConst(i),
                    };
                    JsonNode storedValue = IsAnyTn(paramType)
                        ? elem
                        : new JsonObject { ["k"] = "cast", ["type"] = Tw(paramType), ["e"] = elem };
                    body.Add(new JsonObject
                    {
                        ["k"] = "setField",
                        ["ownerType"] = Tw(_smTypeInst),
                        ["recv"] = new JsonObject { ["k"] = "local", ["name"] = "__sm" },
                        ["name"] = paramName,
                        ["value"] = storedValue,
                    });
                }
                body.Add(Ret(new JsonObject { ["k"] = "local", ["name"] = "__sm" }));
                yield return CreateMethod(
                    new JsonArray
                    {
                        new JsonObject { ["name"] = "args", ["type"] = Tw(new TypeNode.Array(new TypeNode.Nullable(AnyTn))) },
                        new JsonObject { ["name"] = "completion", ["type"] = ContStar() },
                    },
                    body);
            }
            else
            {
                // arity-1: the lambda's single own param (extension receiver OR value) -> a field set here.
                // The `value` param is erased `object`; storing it into the (possibly value-typed) param field
                // needs an explicit unbox/castclass (ilemit's setField does not auto-coerce object -> value) —
                // the same `cast` wrap FunGen uses for await fields. A kotlin.Any field takes the value verbatim.
                var paramName = Str(_params[0]["name"]);
                var paramType = RequiredParamType(_params[0]);
                JsonNode storedValue = IsAnyTn(paramType)
                    ? new JsonObject { ["k"] = "local", ["name"] = "value" }
                    : new JsonObject { ["k"] = "cast", ["type"] = Tw(paramType), ["e"] = new JsonObject { ["k"] = "local", ["name"] = "value" } };
                yield return CreateMethod(
                    new JsonArray
                    {
                        new JsonObject { ["name"] = "value", ["type"] = Tw(AnyTn) },
                        new JsonObject { ["name"] = "completion", ["type"] = ContStar() },
                    },
                    new JsonArray
                    {
                        new JsonObject
                        {
                            ["k"] = "var",
                            ["name"] = "__sm",
                            ["type"] = Tw(_smTypeInst),
                            ["init"] = NewSm(),
                        },
                        new JsonObject
                        {
                            ["k"] = "setField",
                            ["ownerType"] = Tw(_smTypeInst),
                            ["recv"] = new JsonObject { ["k"] = "local", ["name"] = "__sm" },
                            ["name"] = paramName,
                            ["value"] = storedValue,
                        },
                        Ret(new JsonObject { ["k"] = "local", ["name"] = "__sm" }),
                    });
            }
        }

        // `new SM(this.cap1, ..., this.capN, completion)` — captures read from THIS SM's fields (create runs on
        // the template SM); the `completion` local is the create() parameter.
        JsonObject NewSm()
        {
            var args = new JsonArray();
            var argTypes = new JsonArray();
            // `Tw(t)`, not the bare record: a document slot holds a WIRE type node. Adding the `TypeNode` itself
            // made the JsonArray hold a `JsonValueCustomized<TypeNode>` — a live CLR object in the tree, which no
            // reader can parse and which makes any full-document write of this BIR throw. It went unnoticed because
            // these entries are dropped again before the CIR is written; the #305 chokepoint, which serializes the
            // post-pass BIR, is the first thing that had to write one.
            foreach (var (n, t) in _captures)
            {
                args.Add(FieldOf(n, t));
                // SmTypeLambda performs one whole-type lexical-method -> physical-type rebind after all create
                // methods have been assembled. Rebinding here as well would turn `!!0` into `!0` too early; for a
                // lambda nested in `Owner<T>.method<M>` the final pass would then mistake M for owner slot T.
                argTypes.Add(Tw(t));
            }
            // The create ABI accepts the existential Continuation<*> view, while every generated SM/base ctor stores
            // the uniform erased Continuation<Any>. Compiled coroutine completions are BaseContinuationImpl/root
            // continuations and implement that physical slot; make the representation narrowing explicit in CIR.
            args.Add(new JsonObject
            {
                ["k"] = "cast",
                ["type"] = ContAny(),
                ["e"] = new JsonObject { ["k"] = "local", ["name"] = "completion" },
            });
            argTypes.Add(ContAny());
            return new JsonObject { ["k"] = "new", ["type"] = Tw(_smTypeInst), ["argTypes"] = argTypes, ["args"] = args };
        }

        JsonObject CreateMethod(JsonArray createParams, JsonArray body)
        {
            var m = new JsonObject
            {
                ["name"] = "create",
                ["static"] = false,
                ["override"] = _baseIsLocal,
                ["virtual"] = false,
                ["abstract"] = false,
                ["objectOverride"] = false,
                ["vis"] = "public",
                ["params"] = createParams,
                ["ret"] = ContUnit(),
                ["body"] = body,
                ["attrs"] = new JsonArray(),
            };
            if (!_baseIsLocal)
            {
                m["pendingOverrideOwner"] = Tn(BaseContinuationImplFqn);
                m["pendingOverrideReturn"] = ContUnit();
            }
            return m;
        }

        // object f$dotkt_suspend[<tp>](params..., completion) {
        //   val sm = new SM[<tp>]([this,] params..., completion); return sm.invokeSuspend(null) }
        JsonObject ColdEntrySm()
        {
            var coldSmType = ColdEntrySmTypeInst();
            var ctorArgs = new JsonArray();
            if (_isMember) ctorArgs.Add(new JsonObject { ["k"] = "this" });
            foreach (var p in _params) ctorArgs.Add(new JsonObject { ["k"] = "local", ["name"] = Str(p["name"]) });
            ctorArgs.Add(new JsonObject { ["k"] = "local", ["name"] = "completion" });
            var argTypes = new JsonArray();
            if (_isMember) argTypes.Add(Tw(_selfType));
            foreach (var p in _params) argTypes.Add(p["type"]?.DeepClone());
            argTypes.Add(ContAny());

            var body = new JsonArray
            {
                new JsonObject
                {
                    ["k"] = "var",
                    ["name"] = "__sm",
                    ["type"] = Tw(coldSmType),
                    ["init"] = new JsonObject { ["k"] = "new", ["type"] = Tw(coldSmType), ["argTypes"] = argTypes, ["args"] = ctorArgs },
                },
                Ret(new JsonObject
                {
                    ["k"] = "callInstance",
                    ["ownerType"] = Tw(coldSmType),
                    ["virtual"] = true,
                    ["recv"] = new JsonObject { ["k"] = "local", ["name"] = "__sm" },
                    ["method"] = "invokeSuspend",
                    ["sig"] = new JsonArray { Tw(AnyTn) },
                    ["args"] = new JsonArray { NullConst(AnyTn) },
                    ["ret"] = Tw(AnyTn),
                }),
            };
            return ColdMethod(body);
        }

        JsonObject ColdEntryDirect(JsonArray body)
        {
            // For an instance member the cold entry stays an instance method — `this` in the cloned body remains
            // valid. For a top-level fun the body has no `this`. Either way no rewrite is needed (no suspension) —
            // EXCEPT #79: a `coroutineContext` read must bind to the `completion` param's context here (no SM to hold it).
            var cloned = (JsonArray)ReplaceCoroutineContextDirect(body.DeepClone());
            // The cold entry returns kotlin.Any (so a value return boxes). A void/Unit-returning suspend fn body
            // may fall off the end with no explicit `return` (e.g. `{ items.add(v) }`) — the SM (suspension) branch
            // appends the trailing `return Unit` at Build() (bodyOut); the direct (no-suspension) branch must do the
            // SAME, else the cold entry falls through with no value on the stack (ilverify ReturnMissing / a runtime
            // InvalidProgramException — surfaced by a user-authored createCoroutineUnintercepted/startCoroutine driver
            // over a restricted-scope suspend member, e.g. cases/il-corestrict).
            if (IsUnitTn(_resultType)
                && !(cloned.Count > 0 && cloned[^1] is JsonObject last && Str(last["k"]) == "return"))
                cloned.Add(Ret(NullConst(AnyTn)));
            return ColdMethod(cloned);
        }

        JsonObject ColdMethod(JsonArray body)
        {
            var ps = new JsonArray();
            foreach (var p in _params) ps.Add(p.DeepClone());
            ps.Add(new JsonObject { ["name"] = "completion", ["type"] = ContAny() });
            var method = new JsonObject
            {
                ["name"] = _coldName,
                ["static"] = !_isMember,
                // Lockstep virtuality: an override cold entry fills the base slot (Virtual, no NewSlot); an `open`
                // member's cold entry opens a new virtual slot; a final member stays non-virtual (unchanged).
                ["override"] = _memberOverride,
                ["virtual"] = _memberVirtual,
                ["abstract"] = false,
                ["objectOverride"] = false,
                ["vis"] = _physicalSlotBridge ? _physicalSlotVisibility ?? "private" : "public",
                ["params"] = ps,
                ["ret"] = Tw(AnyTn),
                ["body"] = body,
                ["attrs"] = new JsonArray(),
                // Physical coroutine implementation entry, never a second Kotlin source declaration. In particular,
                // the reference DLL now retains this MethodDef so consumers can bind `id|cold` exactly; dll2klib must
                // still omit it from the projected KLIB surface.
                ["generated"] = true,
            };
            if (_typeParams.Count > 0)
                method["typeParams"] = _methodTypeParamDecls.DeepClone();
            if (_generated) method["generated"] = true;
            CarrySourceDeclaration(method);
            CarryOverrideMarkers(method);
            CarryPhysicalSlotFacts(method, coldEntry: true);
            if (_declarationId != null)
            {
                method[DeclarationIdentityBinding.Key] = _declarationId + "|cold";
                method["declarationSourceName"] = _declarationSourceName
                    ?? throw new InvalidOperationException("suspend declaration identity has no frontend source name");
            }
            return method;
        }

        // Override markers are frontend-selected Kotlin declaration identities. Suspend lowering creates two CLR
        // MethodDefs but must not rewrite that semantic identity into either physical spelling. The late slot pass
        // matches cold/Task MethodDefs independently by their exact physical name, signature, and constraints, then
        // uses this unchanged marker only to prove which Kotlin declaration the frontend selected.
        void CarryOverrideMarkers(JsonObject method)
        {
            if (_overrideMarkers.Count == 0) return;
            method["overrides"] = _overrideMarkers.DeepClone();
        }

        // Keep the frontend declaration identity beside every physical suspend projection. A later CLR slot pass
        // may need to bind a frontend-selected DIM after this lowering has replaced the one Kotlin signature with
        // two unrelated CLR signatures. The facts are consumed before CIR emission; neither names nor bodies are
        // inspected to recover the original declaration.
        void CarrySourceDeclaration(JsonObject method)
        {
            method[DeclarationRename.SourceMemberKey] =
                _m[DeclarationRename.SourceMemberKey]?.DeepClone() ?? JsonValue.Create(_name);
            var parameters = new JsonArray();
            foreach (var parameter in _params)
                parameters.Add(parameter["type"]?.DeepClone());
            method[KotlinPropertyAccessors.SuspendSourceParamsKey] = parameters;
            method[KotlinPropertyAccessors.SuspendSourceRetKey] = _m["ret"]?.DeepClone();
        }

        // A resolved MethodImpl descriptor names the declaration signature, not merely its logical source method.
        // Transform it in lockstep with the suspend declaration: the cold slot appends the continuation and returns
        // object; the public bridge retains the member name and exposes Task/Task<R>. Owner and generic arity are
        // already exact and remain untouched. This is a one-to-one physical rewrite, not interface-slot selection.
        void CarryPhysicalSlotFacts(JsonObject method, bool coldEntry)
        {
            if (!_physicalSlotBridge) return;
            method[KotlinPropertyAccessors.PhysicalSlotBridgeKey] = true;
            if (_clrInterfaceSlotBridge)
                method[KotlinPropertyAccessors.ClrInterfaceSlotBridgeKey] = true;

            void Carry(string key, JsonArray source)
            {
                if (source == null || source.Count == 0) return;
                var descriptors = new JsonArray();
                foreach (var item in source.OfType<JsonObject>())
                {
                    var descriptor = (JsonObject)item.DeepClone();
                    if (coldEntry)
                    {
                        descriptor["member"] = (Str(descriptor["member"]) ?? _name) + "$dotkt_suspend";
                        var parameters = (descriptor["params"] as JsonArray)?.DeepClone() as JsonArray
                            ?? new JsonArray();
                        parameters.Add(ContAny());
                        descriptor["params"] = parameters;
                        descriptor["ret"] = Tw(AnyTn);
                    }
                    else if (TypeJson.Read(descriptor["ret"]) is TypeNode result)
                    {
                        var slot = BirTypeLowering.AsReadonlyResultSlot(result);
                        descriptor["ret"] = Tw(IsUnitTn(result)
                            ? new TypeNode.Fqn(_taskBcl)
                            : new TypeNode.Fqn(_taskBcl, new[] { slot }));
                    }
                    descriptors.Add(descriptor);
                }
                method[key] = descriptors;
            }

            Carry("clrInterfaceImpls", _clrInterfaceImpls);
            Carry("clrBaseImpls", _clrBaseImpls);
        }

        // R1 — the call-time-throw cold entry for a concrete-but-NOT-segmentable member (design §11 v1 policy). An
        // ORDINARY ColdMethod whose body is a single `throw new System.NotSupportedException(reason)` — NOT routed
        // through ColdEntryDirect (whose Unit-return check would append an unreachable trailing return). Goes through
        // ColdMethod so the virtuality lockstep (_memberOverride/_memberVirtual/static) is preserved: a stubbed
        // concrete base whose override IS segmentable keeps virtual dispatch — only the stubbed slot throws.
        JsonObject ColdEntryStub(string reason)
        {
            var msg = $"{(_ownerClass ?? _fileClass)}.{_name}: {reason} — this suspend fun is not supported by "
                + "bir2cir's v1 cold-lowering (docs/design-coroutine-cold-core-task-bridge.md §11/§14).";
            return ColdMethod(UnsupportedThrowBody(msg));
        }

        JsonArray UnsupportedThrowBody(string msg) => new()
        {
            new JsonObject
            {
                ["k"] = "throw",
                ["value"] = new JsonObject
                {
                    ["k"] = "newClr",
                    ["type"] = TypeJson.Fqn("System.NotSupportedException"),
                    ["argTypes"] = new JsonArray { Tw(new TypeNode.Fqn("kotlin.String")) },
                    ["args"] = new JsonArray { new JsonObject { ["k"] = "const", ["type"] = Tw(new TypeNode.Fqn("kotlin.String")), ["value"] = msg } },
                },
            },
        };

        // An abstract member's cold entry: `<name>$dotkt_suspend(params..., completion): Any?`, Virtual|Abstract,
        // no body/SM. Concrete overrides emit an `override:true` ColdMethod filling this slot. On a generic class
        // the params keep the class type params verbatim (the cold entry is an instance method of that class).
        JsonObject ColdEntryAbstract()
        {
            var ps = new JsonArray();
            foreach (var p in _params) ps.Add(p.DeepClone());
            ps.Add(new JsonObject { ["name"] = "completion", ["type"] = ContAny() });
            var method = new JsonObject
            {
                ["name"] = _coldName,
                ["static"] = false,
                ["override"] = _memberOverride,
                ["virtual"] = true,
                ["abstract"] = true,
                ["objectOverride"] = false,
                ["vis"] = _physicalSlotBridge ? _physicalSlotVisibility ?? "private" : "public",
                ["params"] = ps,
                ["ret"] = Tw(AnyTn),
                ["body"] = new JsonArray(),
                ["attrs"] = new JsonArray(),
                ["generated"] = true,
            };
            if (_typeParams.Count > 0)
                method["typeParams"] = _methodTypeParamDecls.DeepClone();
            if (_generated) method["generated"] = true;
            CarrySourceDeclaration(method);
            CarryOverrideMarkers(method);
            CarryPhysicalSlotFacts(method, coldEntry: true);
            if (_declarationId != null)
            {
                method[DeclarationIdentityBinding.Key] = _declarationId + "|cold";
                method["declarationSourceName"] = _declarationSourceName
                    ?? throw new InvalidOperationException("suspend declaration identity has no frontend source name");
            }
            return method;
        }

        // A synthesized PLAIN `fun main(...)` (no `suspend`) that drains the cold body.
        //
        // BUG 4 (main-drain): drive the cold body under a REAL root continuation (a RootContinuation<Unit> over a
        // TaskCompletionSource<Unit>) — NOT a null completion. A fully-synchronous suspend main returns a non-SUSPENDED
        // value and needs no wait (the root is unused, byte-for-byte the old behaviour, and a raw synchronous throw
        // still propagates because there is no try/catch on the sync path). A GENUINELY-suspending main (e.g. it awaits
        // an incomplete Task) returns COROUTINE_SUSPENDED; the eventual resume lands in RootContinuation.resumeWith on a
        // threadpool thread and completes the TCS, so main must BLOCK on `tcs.Task` until then. Drain it through
        // `GetAwaiter().GetResult()` (not `Task.Wait()`) so an asynchronous fault/cancellation is rethrown with normal
        // .NET await semantics instead of being wrapped in AggregateException (#140). With a
        // null completion the resume dereferenced null (NRE / lost result). The required BCL Task types are validated
        // against the compile references in ApplyAll, so there is no null-completion fallback.
        JsonObject DrainMain()
        {
            var ps = new JsonArray();
            foreach (var p in _params) ps.Add(p.DeepClone());

            // main returns Unit, so the root sink is typed over Unit.
            var tcsType = new TypeNode.Fqn(_tcsBcl, new[] { UnitTn });
            var taskType = new TypeNode.Fqn(_taskBcl, new[] { UnitTn });
            var rootType = new TypeNode.Fqn(RootContinuationFqn, new[] { UnitTn });
            var taskPlan = _refs?.ResolveAwaitable(_taskBcl, 1)
                ?? throw new InvalidOperationException(
                    $"suspend main drain: '{_taskBcl}<Unit>' has no conforming GetAwaiter in referenced metadata");
            // `Task<Unit>`'s own awaiter, from the plan template closed over that one type argument.
            var awaiterType = CloseAwaitTemplate(
                taskPlan.AwaiterTemplate, taskType as TypeNode.Fqn, System.Array.Empty<TypeNode>());

            var body = new JsonArray
            {
                new JsonObject
                {
                    ["k"] = "var", ["name"] = "__tcs", ["type"] = Tw(tcsType),
                    ["init"] = new JsonObject { ["k"] = "newClr", ["type"] = Tw(tcsType), ["argTypes"] = new JsonArray(), ["args"] = new JsonArray() },
                },
                new JsonObject
                {
                    ["k"] = "var", ["name"] = "__root", ["type"] = Tw(rootType),
                    ["init"] = new JsonObject
                    {
                        ["k"] = "new", ["type"] = Tw(rootType),
                        ["argTypes"] = new JsonArray { Tw(tcsType) }, ["args"] = new JsonArray { Local("__tcs") },
                    },
                },
                // r = main$dotkt_suspend(args..., (Continuation)root)   — a synchronous throw propagates RAW.
                new JsonObject
                {
                    ["k"] = "var", ["name"] = "__r", ["type"] = Tw(AnyTn),
                    // Use the same resolved cold-entry descriptor as the public Task bridge. Hand-authoring this call
                    // used to omit `sig`, leaving ilemit to rediscover the local overload for suspend-main alone.
                    ["init"] = BridgeColdCall(),
                },
            };
            // if (r !== COROUTINE_SUSPENDED) return;
            // else tcs.Task.GetAwaiter().GetResult();   (block, preserving raw await exception semantics)
            var skipL = NextLabel();
            body.Add(BrIf(new JsonObject { ["k"] = "objEq", ["lhs"] = Local("__r"), ["rhs"] = Suspended() }, false, skipL));
            var task = new JsonObject
            {
                ["k"] = "clrPropGet",
                ["type"] = Tw(tcsType),
                ["name"] = "Task",
                ["static"] = false,
                ["recv"] = Local("__tcs"),
                ["ret"] = Tw(taskType),
            };
            var getAwaiter = BuildGetAwaiter(taskPlan, taskType, awaiterType, task,
                captureValue: null,
                resultTok: UnitTn,
                methodTypeArgs: System.Array.Empty<TypeNode>());
            body.Add(new JsonObject
            {
                ["k"] = "exprStmt",
                ["expr"] = new JsonObject
                {
                    ["k"] = "clrInstance",
                    ["type"] = Tw(awaiterType),
                    ["method"] = "GetResult",
                    ["recv"] = getAwaiter,
                    ["argTypes"] = new JsonArray(),
                    ["args"] = new JsonArray(),
                    ["ret"] = Tw(UnitTn),
                },
            });
            body.Add(Label(skipL));

            return new JsonObject
            {
                ["name"] = "main",
                ["static"] = true,
                ["override"] = false,
                ["virtual"] = false,
                ["abstract"] = false,
                ["objectOverride"] = false,
                ["vis"] = "public",
                ["params"] = ps,
                ["ret"] = Tw(UnitTn),
                ["body"] = body,
                ["attrs"] = new JsonArray(),
            };
        }

        // ---- the public Task<R> bridge (bundle-6 P4, design §11) ----
        //
        //   public Task<R> f(args...) {
        //     var __tcs  = new TaskCompletionSource<R>();
        //     var __root = new RootContinuation<R>(__tcs);   // : Continuation<Any> (post ContinuationErasure)
        //     var __r    = COROUTINE_SUSPENDED;               // object
        //     try { __r = f$dotkt_suspend(args..., (Continuation<Any>)__root); }
        //     catch (e: Throwable) { ((Continuation<Any>)__root).resumeWith(Result.failure(e)); __r = COROUTINE_SUSPENDED; }  // #109: OCE->Cancel via RootContinuation
        //     if (__r !== COROUTINE_SUSPENDED) __tcs.TrySetResult((R)__r);   // sync-completion fast path
        //     return __tcs.Task;
        //   }
        //
        // Sync/async completions are mutually exclusive by the coroutine contract: a non-SUSPENDED cold return means the
        // body completed inline (complete the TCS here); a SUSPENDED return means the eventual resume lands in
        // RootContinuation.resumeWith, which completes the TCS. A synchronous throw is caught and routed through the
        // SAME RootContinuation.resumeWith choke point (via RootResumeFailure, #109) so an OCE Cancels — not Faults — the Task.
        // R = Unit/void is treated uniformly as kotlin.Unit (the cold entry returns null for a Unit body; `(Unit)null`
        // is null, matching what RootContinuation.resumeWith stores for the async Unit path — the two agree). The bridge
        // carries `suspendBridge:true` so ilemit stamps [KotlinFunction(Suspend)] (a re-consuming Kotlin sees `suspend fun`).
        JsonObject BuildBridge()
        {
            var isUnit = IsUnitTn(_resultType);
            var rKotlin = isUnit ? UnitTn : _taskResultType;
            // Name the readonly public result representation now and use it for every producer/consumer of the slot.
            // Otherwise Root-V independently makes the nested TCS/Task owner invariant while the head-position
            // TrySetResult value and Kotlin call sites remain readonly, producing either a resolver or IL mismatch.
            var rTaskSlot = BirTypeLowering.AsReadonlyResultSlot(rKotlin);
            // coroutine-abi.md §1: `suspend fun f(): Unit` -> a NON-generic public `Task` (the C#-idiomatic
            // async-void-returning-Task shape); `suspend fun f(): R` -> `Task<R>`. The internal drive stays generic
            // over Unit (TaskCompletionSource<Unit> / RootContinuation<Unit>); the returned `__tcs.Task` (a Task<Unit>)
            // upcasts to the non-generic Task on return (Task<T> : Task). So ONLY the PUBLIC return type differs for Unit.
            var taskType = new TypeNode.Fqn(_taskBcl, new[] { rTaskSlot }); // TaskCompletionSource<R>.Task runtime type
            var taskRetType = isUnit ? new TypeNode.Fqn(_taskBcl) : taskType;   // the public bridge return type

            // BUG 3: an ABSTRACT interface/member suspend fun -> an abstract Task<T> bridge SIGNATURE (no body, no
            // TCS drive). It carries the [KotlinFunction(Suspend)] trigger for round-trip; concrete overrides supply the
            // real driving body (the non-abstract path below, emitted override:true).
            if (_memberAbstract)
            {
                var aps = new JsonArray();
                foreach (var p in _params) aps.Add(p.DeepClone());
                var am = new JsonObject
                {
                    ["name"] = _name,
                    ["static"] = false,
                    ["override"] = _memberOverride,
                    ["virtual"] = true,
                    ["abstract"] = true,
                    ["objectOverride"] = false,
                    ["suspendBridge"] = true,
                    ["vis"] = _physicalSlotBridge ? _physicalSlotVisibility ?? "private" : "public",
                    ["params"] = aps,
                    ["ret"] = Tw(taskRetType),
                    ["body"] = new JsonArray(),
                    ["attrs"] = new JsonArray(),
                };
                if (_typeParams.Count > 0)
                    am["typeParams"] = _methodTypeParamDecls.DeepClone();
                if (_generated) am["generated"] = true;
                CarrySourceDeclaration(am);
                CarryOverrideMarkers(am);
                CarryPhysicalSlotFacts(am, coldEntry: false);
                if (TaskReturnNullableFlags() is JsonArray arnf) am["retNullableFlags"] = arnf;
                if (_resultNullableGeneric != null) am["nullableGenericRet"] = _resultNullableGeneric;
                if (_companionReceiver != null) am["companionReceiver"] = _companionReceiver;
                if (_companionSourceName != null) am["companionSourceName"] = _companionSourceName;
                if (_companionMemberKind != null) am["companionMemberKind"] = _companionMemberKind;
                if (_declarationId != null)
                {
                    am[DeclarationIdentityBinding.Key] = _declarationId;
                    am["declarationSourceName"] = _declarationSourceName
                        ?? throw new InvalidOperationException("suspend declaration identity has no frontend source name");
                }
                if (_explicitClrName != null)
                    am[DeclarationIdentityBinding.ExplicitNameKey] = _explicitClrName;
                // #151 — a `suspend fun f(): Nothing` bridge (Task<Nothing>): carry the pre-erasure Nothing fact so
                // RoundtripMetadata stamps [KotlinNothing] on the return (BirTypeLowering erases the inner Nothing to
                // object, so its own bare-Fqn IsNothingRet check can't see it on the Task<...> return — set it here).
                if (IsNothingTn(_resultType)) am["retNothing"] = true;
                return am;
            }

            var tcsType = new TypeNode.Fqn(_tcsBcl, new[] { rTaskSlot });
            var rootType = new TypeNode.Fqn(RootContinuationFqn, new[] { rTaskSlot });

            var body = new JsonArray
            {
                new JsonObject
                {
                    ["k"] = "var", ["name"] = "__tcs", ["type"] = Tw(tcsType),
                    ["init"] = new JsonObject { ["k"] = "newClr", ["type"] = Tw(tcsType), ["argTypes"] = new JsonArray(), ["args"] = new JsonArray() },
                },
                new JsonObject
                {
                    ["k"] = "var", ["name"] = "__root", ["type"] = Tw(rootType),
                    ["init"] = new JsonObject
                    {
                        ["k"] = "new", ["type"] = Tw(rootType),
                        ["argTypes"] = new JsonArray { Tw(tcsType) },
                        ["args"] = new JsonArray { Local("__tcs") },
                    },
                },
                new JsonObject { ["k"] = "var", ["name"] = "__r", ["type"] = Tw(AnyTn), ["init"] = Suspended() },
                new JsonObject
                {
                    ["k"] = "try",
                    ["type"] = Tw(VoidTn),
                    ["body"] = new JsonArray
                    {
                        new JsonObject { ["k"] = "setLocal", ["name"] = "__r", ["value"] = BridgeColdCall() },
                    },
                    ["catches"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["excType"] = Tn("kotlin.Throwable"),
                            ["var"] = "__e",
                            ["body"] = new JsonArray
                            {
                                // #109: funnel the sync throw through RootContinuation.resumeWith (OCE->TrySetCanceled
                                // else TrySetException) — the SAME choke point the async resume path reaches — rather
                                // than faulting the TCS directly. __r stays SUSPENDED so the sync-completion fast path below is skipped.
                                new JsonObject { ["k"] = "exprStmt", ["expr"] = RootResumeFailure() },
                                new JsonObject { ["k"] = "setLocal", ["name"] = "__r", ["value"] = Suspended() },
                            },
                        },
                    },
                },
            };

            var skipL = NextLabel();
            body.Add(BrIf(new JsonObject { ["k"] = "objEq", ["lhs"] = Local("__r"), ["rhs"] = Suspended() }, true, skipL));
            JsonNode resultVal = IsAnyTn(rTaskSlot)
                ? Local("__r")
                : new JsonObject { ["k"] = "cast", ["type"] = Tw(rTaskSlot), ["e"] = Local("__r") };
            body.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = TcsCall(tcsType, "TrySetResult", rTaskSlot, resultVal) });
            body.Add(Label(skipL));
            JsonNode tcsTask = new JsonObject
            {
                ["k"] = "clrPropGet",
                ["type"] = Tw(tcsType),
                ["name"] = "Task",
                ["static"] = false,
                ["recv"] = Local("__tcs"),
                ["ret"] = Tw(taskType),
            };
            // Unit: upcast the Task<Unit> (TCS<Unit>.Task) to the non-generic public `Task` return (Task<T> : Task).
            if (isUnit) tcsTask = new JsonObject { ["k"] = "cast", ["type"] = Tw(taskRetType), ["e"] = tcsTask };
            body.Add(Ret(tcsTask));

            var ps = new JsonArray();
            foreach (var p in _params) ps.Add(p.DeepClone());
            var method = new JsonObject
            {
                ["name"] = _name,
                ["static"] = !_isMember,
                // BUG 3: the bridge's virtuality rides in lockstep with the cold entry — an `override` member's bridge
                // fills the base (interface/open) bridge slot; an `open` member's bridge opens a new virtual slot; a
                // plain (final) member's bridge stays non-virtual (unchanged from the original single-shape path).
                ["override"] = _memberOverride,
                ["virtual"] = _memberVirtual,
                ["abstract"] = false,
                ["objectOverride"] = false,
                ["suspendBridge"] = true,
                ["vis"] = _physicalSlotBridge ? _physicalSlotVisibility ?? "private" : "public",
                ["params"] = ps,
                ["ret"] = Tw(taskRetType),
                ["body"] = body,
                ["attrs"] = new JsonArray(),
            };
            if (_typeParams.Count > 0)
                method["typeParams"] = _methodTypeParamDecls.DeepClone();
            if (_generated) method["generated"] = true;
            CarrySourceDeclaration(method);
            CarryOverrideMarkers(method);
            CarryPhysicalSlotFacts(method, coldEntry: false);
            // BUG 2 (nested return nullability): a `suspend fun f(): String?`'s bridge return `Task<string?>` needs the
            // inner `?` — the scalar retNullable can't express a nullability that rides an INNER type arg. Emit the
            // flattened NullableAttribute byte walk (RoundtripMetadata folds it into the return's `retAttrs` for ilemit
            // to stamp; dll2klib reads it back).
            if (TaskReturnNullableFlags() is JsonArray rnf) method["retNullableFlags"] = rnf;
            // #86: the Kotlin type of an object-erased `T?` result. dll2klib unwraps the bridge's `Task<R>` to `R`
            // FIRST and only then reads the slot's carrier, so the carrier holds the UNWRAPPED Kotlin result — and the
            // NRT byte above (offset past the Task node) is what puts its `?` back.
            if (_resultNullableGeneric != null) method["nullableGenericRet"] = _resultNullableGeneric;
            if (_companionReceiver != null) method["companionReceiver"] = _companionReceiver;
            if (_companionSourceName != null) method["companionSourceName"] = _companionSourceName;
            if (_companionMemberKind != null) method["companionMemberKind"] = _companionMemberKind;
            // This Task bridge is the physical hot form of the frontend-selected suspend declaration. Keep the
            // source identity on it so the common post-erasure allocator can distinguish overloads whose Kotlin
            // signatures collapse to one CLR signature. The original BIR declaration is replaced by this bridge;
            // dropping the fact here would force ilemit to rediscover (or arbitrarily repair) the overload.
            if (_declarationId != null)
            {
                method[DeclarationIdentityBinding.Key] = _declarationId;
                method["declarationSourceName"] = _declarationSourceName
                    ?? throw new InvalidOperationException("suspend declaration identity has no frontend source name");
            }
            if (_explicitClrName != null)
                method[DeclarationIdentityBinding.ExplicitNameKey] = _explicitClrName;
            // #151 — a `suspend fun f(): Nothing` bridge (Task<Nothing>): carry the pre-erasure Nothing fact so
            // RoundtripMetadata stamps [KotlinNothing] on the return (BirTypeLowering erases the inner Nothing to
            // object, so its own bare-Fqn IsNothingRet check can't see it on the Task<...> return — set it here).
            if (IsNothingTn(_resultType)) method["retNothing"] = true;
            return method;
        }

        // Value-type Kotlin FQNs — NRT [Nullable] never annotates these (a nullable value type is `Nullable<T>`, a
        // DISTINCT type, not an attribute), so they contribute NO byte to the pre-order NullableAttribute walk.
        static readonly HashSet<string> ValueTypeFqns = new(StringComparer.Ordinal)
        {
            "kotlin.Int", "kotlin.Long", "kotlin.Short", "kotlin.Byte", "kotlin.Char", "kotlin.Boolean",
            "kotlin.Double", "kotlin.Float", "kotlin.UInt", "kotlin.ULong", "kotlin.UShort", "kotlin.UByte",
        };

        // BUG 2: the pre-order NullableAttribute byte walk for the bridge return `Task<R>`, or null when it carries no
        // nullable position (then the type-level [NullableContext(1)] non-null default suffices). Reference nodes get 1
        // (non-null) or 2 (nullable); value-type / Unit nodes are skipped (no byte). kotc conveys only R's OUTER
        // nullability (`retNullable` on the suspend method), so inner reference args stay non-null (1) — the common
        // `suspend fun f(): String?` -> {1,2}; `List<String>?` -> {1,2,1}.
        JsonArray TaskReturnNullableFlags()
        {
            if (!_resultNullable) return null;   // no outer `?` (off the type node now) -> nothing nullable to encode
            var rKotlin = IsUnitTn(_resultType) ? UnitTn : _resultType;
            var flags = new List<int> { 1 };             // the Task<...> outer node is a non-null reference
            if (!WalkNullable(rKotlin, outerNullable: true, flags)) return null;   // R was a value type -> Nullable<T>
            var arr = new JsonArray();
            foreach (var b in flags) arr.Add(b);
            return arr;
        }

        // Append the pre-order NRT bytes for `token` (a Kotlin type FQN, possibly `Owner[arg,...]`). Returns whether any
        // nullable (2) byte was emitted. `outerNullable` marks this node's own `?`; inner args are non-null.
        static bool WalkNullable(TypeNode t, bool outerNullable, List<int> flags)
        {
            if (t == null) return false;
            // A value type / Unit / void carries no nullability byte; every other head (a reference type or a
            // generic application) contributes a flag (2 if this position is nullable, else 1), then recurses its args.
            var head = t is TypeNode.Fqn f ? f.Name : null;
            if (head != null && (ValueTypeFqns.Contains(head) || head is "kotlin.Unit" or "void")) return false;
            flags.Add(outerNullable ? 2 : 1);
            var any = outerNullable;
            if (t is TypeNode.Fqn { Args: { } args })
                foreach (var arg in args) any |= WalkNullable(arg, outerNullable: false, flags);
            return any;
        }

        // The bridge's cold-entry call: forward the bridge params + the RootContinuation (cast to the erased
        // Continuation<Any> completion). typeArgs thread the bridge's own generic params to the generic cold entry.
        JsonObject BridgeColdCall()
        {
            var args = new JsonArray();
            foreach (var p in _params) args.Add(Local(Str(p["name"])));
            args.Add(new JsonObject { ["k"] = "cast", ["type"] = ContAny(), ["e"] = Local("__root") });
            var sig = new JsonArray();
            foreach (var p in _params) sig.Add(p["type"]?.DeepClone());
            sig.Add(ContAny());

            // On a GENERIC enclosing class the callee's declaring type is the CONSTRUCTED self `Box[!0..]` (matching
            // `this`), never the open `Box` — else verification rejects the recv type. `_selfType` is exactly that
            // constructed owner (Fqn(owner, Tv{type,i}...)); it is non-null in the member branch (member <=> owner != null).

            JsonObject call;
            if (_isMember)
                call = new JsonObject
                {
                    ["k"] = "callInstance",
                    ["ownerType"] = Tw(_selfType),
                    ["virtual"] = false,
                    ["recv"] = new JsonObject { ["k"] = "this" },
                    ["method"] = _coldName,
                    ["sig"] = sig.DeepClone(),
                    ["args"] = args,
                    ["ret"] = Tw(AnyTn),
                };
            else
                // A top-level fun's cold entry lives in the file class (owner:null); a STATIC member's cold entry is a
                // static method on the enclosing class, so target THAT owner (owner:null would resolve to the file class).
                call = new JsonObject
                {
                    ["k"] = "callStatic",
                    // A materialized local suspend function is static only as a CLR representation detail: its
                    // declaring owner is still the current Owner<!0,...> closure. Keep that exact constructed owner
                    // on the generated bridge call. A source static/companion member has no lexical owner slots, so
                    // its bare owner continues through GenericStaticOwnerBinding's canonical-static rule.
                    ["ownerType"] = _staticMember && _ownerClass != null ? Tw(_selfType) : null,
                    // #199 Design B: a top-level cold entry keeps owner:null and carries the file-class dispatch hint (a
                    // materialized static member instead carries its exact constructed declaring owner. callStatic's
                    // CIR contract requires calleeOwner for every owner-less dispatch; ownerType is its type-checking
                    // surface, not an alternative dispatch channel.
                    ["calleeOwner"] = _staticMember && _ownerClass != null ? Tw(_selfType) : Tn(_fileClass),
                    ["method"] = _coldName,
                    ["sig"] = sig.DeepClone(),
                    ["args"] = args,
                    ["ret"] = Tw(AnyTn),
                };
            if (_typeParams.Count > 0)
            {
                // Thread the bridge method's OWN method type-params (scope:"method" -> ilemit !!i) to the generic
                // cold entry, which declares the same method type-params in the same order.
                var ta = new JsonArray();
                for (var i = 0; i < _typeParams.Count; i++) ta.Add(Tw(new TypeNode.Tv("method", i)));
                call["typeArgs"] = ta;
            }
            // The generated bridge is also a use of the exact cold implementation derived from the same Kotlin
            // declaration. Bind it by the derived identity after cold signatures have been projected, rather than
            // selecting an erased overload by the provisional `$dotkt_suspend` spelling.
            if (_declarationId != null)
                call[DeclarationIdentityBinding.Key] = _declarationId + "|cold";
            return call;
        }

        // #109 — route a SYNCHRONOUS bridge throw through the RootContinuation choke point:
        //   ((Continuation<Any>)__root).resumeWith(Result.failure<Any>(__e))
        // The cold entry calls `sm.invokeSuspend(null)` DIRECTLY (ColdEntrySm), so a suspension point that throws on
        // the FIRST pass (e.g. `await` of an already-cancelled Task — IsCompleted true -> GetResult() throws OCE
        // synchronously) escapes out of the cold entry into the bridge's catch, NOT through BaseContinuationImpl/
        // RootContinuation. Completing the TCS directly with TrySetException here would FAULT a cancellation (bypassing
        // #86's OCE->TrySetCanceled). Instead hand the failure to RootContinuation.resumeWith — the SINGLE site that
        // discriminates `is OperationCanceledException -> trySetCanceled` else `trySetException` — so the SYNC throw
        // and the ASYNC resume (which reaches RootContinuation via the SM's completion chain) funnel through ONE test.
        // `Result.failure` is the PUBLIC companion factory (mirrors the `Result.success` in AwaitResumeMethod; the
        // internal `Result(value)` ctor is cross-assembly-inaccessible). Erased to Result<Any> by ContinuationErasure.
        JsonObject RootResumeFailure() => new()
        {
            ["k"] = "callInstance",
            ["ownerType"] = ContAny(),
            ["virtual"] = true,
            ["recv"] = new JsonObject { ["k"] = "cast", ["type"] = ContAny(), ["e"] = Local("__root") },
            ["method"] = "resumeWith",
            ["sig"] = new JsonArray { Tw(new TypeNode.Fqn("kotlin.Result", new TypeNode[] { AnyTn })) },
            ["ret"] = Tw(VoidTn),
            ["args"] = new JsonArray
            {
                CompanionFactoryCall(
                    "kotlin.Result",
                    "failure",
                    new JsonArray { Tn("kotlin.Any") },
                    new JsonArray { Tn("kotlin.Throwable") },
                    new JsonArray { Local("__e") },
                    Tw(new TypeNode.Fqn("kotlin.Result", new TypeNode[] { AnyTn }))),
            },
        };

        // A substituted @ClrIntrinsic instance call on the TaskCompletionSource<R> sink (TrySetResult/TrySetException).
        // Emitted post-MemberCallSubstitution, so already in the BCL-owner clrInstance form (result discarded via exprStmt).
        JsonObject TcsCall(TypeNode tcsType, string method, TypeNode argType, JsonNode arg) => new()
        {
            ["k"] = "clrInstance",
            ["type"] = Tw(tcsType),
            ["method"] = method,
            ["recv"] = Local("__tcs"),
            ["argTypes"] = new JsonArray { Tw(argType) },
            ["args"] = new JsonArray { arg },
            ["ret"] = Tw(BoolTn),
        };

        static JsonObject Local(string name) => new() { ["k"] = "local", ["name"] = name };

        // #79 — build the `coroutineContext` value as `<current continuation>.get_context()`. In an SM the current
        // continuation is the SM ITSELF (invokeSuspend's `this`, a ContinuationImpl subtype whose `override val context`
        // is `get_context`); in the no-SM cold entry it is the `completion` param (a Continuation<Any?>). The receiver
        // node is returned final (not re-Rewritten), so a raw `this` denotes the SM and survives the this->$this member
        // rewrite by construction. ret = kotlin.coroutines.CoroutineContext.
        JsonObject CoroutineContextValue(bool smPath) => new()
        {
            ["k"] = "callInstance",
            ["ownerType"] = Tw(new TypeNode.Fqn(smPath ? ContinuationImplFqn : ContinuationFqn)),
            ["method"] = "context",
            ["prop"] = "get",
            // `context` is a VIRTUAL Kotlin property (override on ContinuationImpl; declared on the Continuation
            // interface for the completion path). A callInstance defaults to a plain `call`, which is INVALID IL for
            // an interface method (Continuation) — so mark it virtual (a callvirt also dispatches to the SM's override).
            ["virtual"] = true,
            ["recv"] = smPath ? new JsonObject { ["k"] = "this" } : Local("completion"),
            ["argTypes"] = new JsonArray(),
            ["args"] = new JsonArray(),
            ["ret"] = Tw(new TypeNode.Fqn(CoroutineContextFqn)),
        };

        // #79 — recursively replace every `coroutineContext` read in a NO-SM (body-direct) cold entry with
        // `completion.get_context()`. ColdEntryDirect emits the body verbatim (no Rewrite pass), so the read would
        // otherwise reach ilemit as the bogus `<fileclass>.get_coroutineContext`. Descends into nested lambdas too:
        // a `coroutineContext` read there is still resolved against THIS cold entry's completion (the lambda captures
        // the enclosing continuation) — matching kotc's inline-val semantics.
        JsonNode ReplaceCoroutineContextDirect(JsonNode node)
        {
            if (node is JsonObject o)
            {
                if (IsCoroutineContextRead(o)) return CoroutineContextValue(smPath: false);
                var copy = new JsonObject();
                foreach (var kv in o) copy[kv.Key] = kv.Value == null ? null : ReplaceCoroutineContextDirect(kv.Value);
                return copy;
            }
            if (node is JsonArray a)
            {
                var copy = new JsonArray();
                foreach (var it in a) copy.Add(it == null ? null : ReplaceCoroutineContextDirect(it));
                return copy;
            }
            return node?.DeepClone();
        }

        // ---- small node builders ----

        JsonObject SetField(string name, JsonNode value) => new()
        {
            ["k"] = "setField",
            ["ownerType"] = Tw(_smTypeInst),
            ["recv"] = new JsonObject { ["k"] = "this" },
            ["name"] = name,
            ["value"] = value,
        };

        JsonObject FieldOf(string name, TypeNode type) => new()
        {
            ["k"] = "field",
            ["ownerType"] = Tw(_smTypeInst),
            ["recv"] = new JsonObject { ["k"] = "this" },
            ["name"] = name,
            ["ret"] = Tw(type),
        };

        static JsonObject Suspended() => new()
        {
            ["k"] = "callStatic",
            ["owner"] = Tn(IntrinsicsKtFqn),
            ["method"] = "COROUTINE_SUSPENDED",
            ["prop"] = "get",
            ["args"] = new JsonArray(),
            ["ret"] = Tw(AnyTn),
        };

        static JsonObject ThrowOnFailure() => new()
        {
            ["k"] = "callStatic",
            ["owner"] = Tn(ThrowOnFailureOwner),
            ["method"] = "throwOnFailure",
            ["sig"] = new JsonArray { Tw(new TypeNode.Nullable(AnyTn)) },
            ["args"] = new JsonArray { new JsonObject { ["k"] = "local", ["name"] = "result" } },
            ["ret"] = Tw(VoidTn),
        };

        static JsonObject Ret(JsonNode value) => new() { ["k"] = "return", ["value"] = value };
        static JsonObject IntConst(int v) => new() { ["k"] = "const", ["type"] = TypeJson.Write(IntTn), ["value"] = v };
        static JsonObject BoolConst(bool v) => new() { ["k"] = "const", ["type"] = TypeJson.Write(BoolTn), ["value"] = v };
        static JsonObject NullConst(TypeNode type) => new() { ["k"] = "const", ["type"] = TypeJson.Write(type), ["value"] = null };
        JsonObject MissingValuePlaceholder(TypeNode expectedType)
        {
            var type = expectedType ?? throw new NotSupportedException(
                $"bir2cir: suspend-lowering: a result-less suspension-bearing valueBlock in "
                + $"`{DiagOwner}` has no enclosing expected type.");
            return IsUnitTn(type) ? NullConst(type) : DefaultOf(type);
        }
        // The zero value of a type (ldnull for a ref, initobj for a value type) — the correct init-less-`var` default,
        // unlike NullConst which emits a null literal that ilemit rejects for a value type.
        static JsonObject DefaultOf(TypeNode type) => new() { ["k"] = "default", ["type"] = TypeJson.Write(type) };
        static JsonObject Label(int id) => new() { ["k"] = "label", ["id"] = id };
        static JsonObject Goto(int id) => new() { ["k"] = "goto", ["id"] = id };
        static JsonObject BrIf(JsonNode cond, bool on, int id) => new()
        { ["k"] = "brIf", ["cond"] = cond, ["on"] = on, ["id"] = id };
        static JsonObject BinEq(JsonNode l, JsonNode r) => new()
        { ["k"] = "binOp", ["op"] = "==", ["lhs"] = l, ["rhs"] = r };
    }
}
