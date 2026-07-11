// bir2cir — SuspendColdLowering (bundle-6 P2 straight-line + P3 control-flow/generics/try + P3 wave-2a
// instance-members/member-calls): the cold-core suspend -> state-machine transform.
//
// Per docs/design-coroutine-cold-core-task-bridge.md §11 (the LOCKED contract) + the approved plan
// (functional-nibbling-pearl.md "The bir2cir transform"). This pass lowers a Kotlin `suspend fun` into
// the COLD Continuation shape:
//
//   suspend fun f(a): R           (top-level file-class static; extension = leading `__self` param)
//     -- SM class:   <Owner>_f$sm[<tp>] : kotlin.coroutines.clr.internal.ContinuationImpl
//                      fields: int label; [$this for an instance member]; <spilled params/locals/temps>
//                      object invokeSuspend(object result)   // label dispatch + segmented body
//     -- cold entry: object f$dotkt_suspend[<tp>](a, completion: Continuation<Any?>)
//                      { val sm = new <Owner>_f$sm[<tp>]([this,] a, completion); return sm.invokeSuspend(null) }
//     -- suspend main additionally gets a synthesized PLAIN `fun main()` that drains the cold body.
//
// The blueprint is kotc's LIVE CPS engine (BirEmitter.kt:1412-1744 collectCpsVars/spillExpr/emitWhenCps/
// emitWhileCps/emitTryCps), re-implemented over BIR JSON targeting the cold shape. CRITICAL OBSERVATION:
// kotc already FLATTENS `while`/`for`/`do-while` into structured `block`/`label`/`brIf`/`goto` BIR, so
// loops need no re-segmentation here — only `if`/`when` survive as `cond` (ternary) EXPRESSIONS, which
// this pass lowers to label/brIf/goto control flow when they contain a suspension (mirroring emitWhenCps).
//
// The SM resume protocol matches Kotlin/JVM's ContinuationImpl lowering: a single `result` carrier (the
// invokeSuspend parameter), label dispatch that jumps to each post-suspend merge point, a
// `COROUTINE_SUSPENDED` check after each cold call, and a `throwOnFailure(result)` prologue at each merge
// point (the SM-prologue rethrow that surfaces a failed async resume — the CLR analog of the JVM SM's
// `ResultKt.throwOnFailure($result)`).
//
// SUPPORTED: straight-line + control flow across suspension (if/when via cond-lowering, while/for/do-while
// already flat), try/catch where the suspension is in the TRY BODY (two-level dispatch), generic suspend
// funs (`suspend fun <T> f(x): T` -> a generic SM `f$sm<T>`), extension suspend funs (kotc lowers the
// receiver to a `__self` param), INSTANCE suspend MEMBERS (`class C { suspend fun m() }` — the SM carries a
// `$this` field of type C; `this`/implicit-receiver reads become `SM.$this`; the cold entry is an INSTANCE
// method on C so a member direct/no-suspension body keeps `this` verbatim), and MEMBER + cross-file/
// cross-assembly suspend CALLS (`x.g()` callInstance / an owner'd top-level callStatic — rewritten to the
// callee's `<name>$dotkt_suspend` cold shape on the correct receiver; cross-assembly resolved via the
// ref.dll MemberBinding.Suspend flag + the naming convention).
//
// The whole analysis is GLOBAL across the compilation's files (ApplyAll) because a same-assembly cross-file
// suspend call keeps `owner:null` (kotc emits it identically to a same-file call) and a cold entry it names
// may live in another file — so the transformability fixpoint spans every input file.
//
// LEFT UNTOUCHED (rides the existing ilemit throw-stub, zero regression): suspension inside a
// catch/finally block, a nested suspending try, suspend lambdas / closures, a suspending member of a GENERIC
// class (the generic-class SM needs the enclosing class type params threaded — deferred), a static member
// suspend fun inside a class, and any suspend call whose callee cold shape can't be resolved (same-assembly
// non-transformable or a cross-assembly member without a ref.dll Suspend flag). Those keep `"suspend":true`.
//
// Runs AFTER MemberCallSubstitution and BEFORE BirTypeLowering, in app builds only (gated in Pipeline via
// attributeTopLevelOwner; skipped in the ref AND rt-stdlib builds). Its synthesized nodes are emitted in
// the SUBSTITUTED call form but in the kotlin.* TYPE vocabulary, so they flow through BirTypeLowering.

using System.Text.Json.Nodes;
using DotKt.Bir;

static class SuspendColdLowering
{
    // Structured type-node helpers for the SM synthesis (all SM type slots are structured TypeNode). `Tn` = a bare-FQN
    // slot; `Gen` = a constructed generic slot; `ContAny`/`ContUnit` = Continuation<Any?>/<Unit>. Each returns a FRESH
    // JsonNode (a JSON node has a single parent, so a shared instance cannot be reused across slots).
    static readonly TypeNode AnyTn = new TypeNode.Fqn("kotlin.Any");
    static readonly TypeNode IntTn = new TypeNode.Fqn("kotlin.Int");
    static readonly TypeNode BoolTn = new TypeNode.Fqn("kotlin.Boolean");
    static readonly TypeNode VoidTn = new TypeNode.Fqn("void");
    static readonly TypeNode UnitTn = new TypeNode.Fqn("kotlin.Unit");
    static readonly TypeNode ContAnyTn = new TypeNode.Fqn("kotlin.coroutines.Continuation", new TypeNode[] { new TypeNode.Fqn("kotlin.Any") });
    static readonly TypeNode ContUnitTn = new TypeNode.Fqn("kotlin.coroutines.Continuation", new TypeNode[] { new TypeNode.Fqn("kotlin.Unit") });
    static JsonNode Tn(string fqn) => TypeJson.Fqn(fqn);
    static JsonNode Tw(TypeNode t) => TypeJson.Write(t);
    static JsonNode ContAny() => TypeJson.Write(ContAnyTn);
    static JsonNode ContUnit() => TypeJson.Write(ContUnitTn);
    static bool IsUnitTn(TypeNode t) => t is TypeNode.Fqn { Args: null, Name: "void" or "kotlin.Unit" };
    static bool IsAnyTn(TypeNode t) => t is TypeNode.Fqn { Args: null, Name: "kotlin.Any" };

    const string ContinuationImplFqn = "kotlin.coroutines.clr.internal.ContinuationImpl";
    const string SuspendLambdaFqn = "kotlin.coroutines.clr.internal.SuspendLambda";
    // A @RestrictsSuspension-scope (e.g. SequenceScope) suspend lambda's SM base. Same 2-arg (arity, completion)
    // ctor + create() protocol as SuspendLambda; RestrictedContinuationImpl pins EmptyCoroutineContext.
    const string RestrictedSuspendLambdaFqn = "kotlin.coroutines.clr.internal.RestrictedSuspendLambda";
    const string BaseContinuationImplFqn = "kotlin.coroutines.clr.internal.BaseContinuationImpl";
    // BaseContinuationImpl.create returns Continuation<Unit> (ContinuationImpl.kt:82/87); a CLR virtual override needs
    // an EXACT return-type match (no covariance), so the SM's create() must return Continuation<Unit>, NOT <Any>.
    const string IntrinsicsKtFqn = "kotlin.coroutines.intrinsics.IntrinsicsKt";
    // The CLR-interop coroutine bridge file-class (kotlin.clr.await). Its suspend fn is NOT a genuine cold
    // coroutine body: `await` is a facadegen call-site MARKER (lowered by EmitAwaitPoint at CALL sites, its
    // DEFINITION a plain suspend declaration kept for ref/rt signature symmetry). When the cold transform runs in
    // the rt-stdlib build (bundle-6 P5), it must be EXCLUDED — transforming its definition into a cold entry / Task
    // bridge would manufacture the wrong ABI (Codex-confirmed rt-gate decision). NOTE: this skips the whole
    // file-class coarsely; the ideal is to narrow to the `await` marker. (`delay`/`blockOn` were DROPPED from
    // the stdlib — the old `delay` here is gone.)
    const string InteropBridgeFileClass = "kotlin.clr.CoroutinesKt";
    // Top-level `throwOnFailure(result)` helper (ContinuationImpl.kt, package kotlin.coroutines.clr.internal).
    const string ThrowOnFailureOwner = "kotlin.coroutines.clr.internal.ContinuationImplKt";

    // Node kinds whose PRESENCE around a suspension disqualifies the fun (leave untouched for the ilemit
    // throw-stub): suspend lambdas / closures / inline collection loops.
    static readonly HashSet<string> LambdaKinds = new(StringComparer.Ordinal)
    {
        "newClosure", "newDelegate", "lambda", "forEachInline", "repeatInline",
        // A suspend-lambda VALUE built inside a suspend fun. Kept here so the SUBTREE-SKIPPING analyses
        // (CollectVarFields / DisambiguateShadowedVars) do not descend into the lambda's own body (its vars are
        // the lambda SM's, not the enclosing SM's). SuspensionsSupported and Rewrite SPECIAL-CASE it (GAP 2): the
        // enclosing fun IS cold-transformed, the lambda copied opaquely with SM-vocabulary `capValues`.
        "newSuspendLambda",
    };

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();
    static bool Bool(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    // Structured declaration modifier (spec §2.1): `decl.mods.<key> == true` (absent object/key = false).
    static bool Mod(JsonObject decl, string key) => decl["mods"] is JsonObject m && Bool(m[key]);

    // F2 — the SOLE `suspendCoroutine`/`suspendCoroutineUninterceptedOrReturn` recognizer, FQN-based on the plain
    // call. Our compiler does NOT inline these @InlineOnly intrinsics at the call site (cross-module never; same-module
    // no longer either — kotc's same-module source-splice + the `suspendIntrinsic` valueBlock stamp were retired in
    // #75 S4b), so in EVERY build (app cross-module AND stdlib self-build same-module) kotc emits a plain
    // `callStatic <name>(<newClosure|newDelegate>) suspendCall:true`, owner:null (top-level intrinsic) OR the resolved
    // stdlib file-class, the block materialized as a closure class (capturing) or a top-level `__lambdaN`
    // (non-capturing). This IS a suspension point — recognized here, lowered by EmitSuspendCoroutineCall (which
    // reconstructs the wrapper's SafeContinuation body / the unintercepted block, since the un-inlined wrapper body is
    // unavailable). The recognizer is purely STRUCTURAL (k/suspendCall/method/owner/arg-shape) — no module-boundary
    // gate — so it fires identically same-module and cross-module.
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
        return (o["args"] as JsonArray)?.FirstOrDefault() is JsonObject a
            && Str(a["k"]) is "newClosure" or "newDelegate";
    }

    // GAP 1 (P3 wave-2b) — a call to a suspend functional VALUE: `b()` where `b: suspend (...) -> T` is a
    // param/local/field. kotc emits it as `recv.invoke()` (suspendCall:true) whose receiver TYPE is a
    // `kotlin.coroutines.SuspendFunctionN[...]` (SuspendFunction0[R], SuspendFunction1[P,R], ...). Unlike a
    // NAMED suspend call it has no `<name>$dotkt_suspend` cold entry — the value at runtime IS a SuspendLambda
    // state machine (a BaseContinuationImpl), so the suspension is driven through the stdlib cold-invoke helper
    // `startSuspendUninterceptedOrReturn(fn, [receiver,] completion)` (= `create(completion).invokeSuspend(Unit)`),
    // NOT a virtual invoke (the SM implements no SuspendFunctionN interface / carries no `invoke` bridge).
    const string SuspendFunctionPrefix = "kotlin.coroutines.SuspendFunction";
    const string StartSuspendOwner = "kotlin.coroutines.clr.internal.ContinuationImplKt";
    static bool IsSuspendValueCall(JsonObject o) =>
        Str(o["k"]) == "callInstance" && Bool(o["suspendCall"]) && Str(o["method"]) == "invoke"
        && (BareOwner(TypeJson.OwnerName(o["ownerType"]))?.StartsWith(SuspendFunctionPrefix, StringComparison.Ordinal) ?? false);

    // A suspend fun's identity: Owner=null for a top-level file-class static, else the enclosing class FQN. Sig
    // is the joined param-type list — it discriminates OVERLOADS that share (Owner, Name) (e.g. SequenceScope
    // has three `yieldAll` overloads differing only by param type: Iterator/Iterable/Sequence). Without it the
    // registry would collapse the overloads to one, dropping the others (see SigOf).
    readonly record struct FunKey(string Owner, string Name, string Sig);

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

    // A shape-eligible suspend fun + where it lives (for cold-entry/SM splicing).
    sealed record Entry(JsonObject Method, JsonObject Root, JsonObject TypeNode, string Owner, string FileClass);

    // A suspend CALL site descriptor (for the resolvability fixpoint).
    readonly record struct CallRef(bool Instance, string Owner, string Name);

    // Returns the callee-return-type map (cold-entry name -> Kotlin resultType), so the SEPARATE
    // SuspendLambdaLowering phase can type a suspend-lambda's awaited value the SAME way (else a
    // lambda's `h()` await falls back to kotlin.Any and the value is never unboxed -> `object + int`).
    public static IReadOnlyDictionary<string, TypeNode> ApplyAll(IReadOnlyList<JsonNode> roots, ReferenceMetadataIndex refs, IReadOnlySet<string> localTypeFqns)
    {
        // 1. Global registry of shape-eligible suspend funs across every input file.
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
            // Skip the CLR-interop bridge (kotlin.clr.await/delay): its suspend fns are call-site markers /
            // interop declarations, not cold coroutine bodies (see InteropBridgeFileClass). Relevant only in the
            // rt-stdlib build, where this file-class is present; a no-op in app builds.
            var isInteropBridge = fileClass == InteropBridgeFileClass;
            if (file["methods"] is JsonArray methods && !isInteropBridge)
                foreach (var m in methods)
                    if (m is JsonObject mo && Str(mo["name"]) is string name && IsShapeEligible(mo))
                        entries[new FunKey(null, name, SigOf(mo))] = new Entry(mo, file, null, null, fileClass);
            if (file["types"] is JsonArray types)
                foreach (var t in types)
                    if (t is JsonObject to && Str(to["name"]) is string owner && to["methods"] is JsonArray tms)
                        foreach (var m in tms)
                            if (m is JsonObject mo && Str(mo["name"]) is string name && IsMemberShapeEligible(mo, to))
                                entries[new FunKey(owner, name, SigOf(mo))] = new Entry(mo, file, to, owner, fileClass);
        }
        // callee-return-type fallback for await-temp field typing when a call node carries no instantiated
        // ret (a bare `one()` has `sig:""`): the callee's declared resultType, keyed by cold-entry name.
        // Built here (before the early returns) so it is ALWAYS returned for the lambda phase's use.
        var calleeRet = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        foreach (var (k, e) in entries)
            calleeRet[k.Name] = TypeJson.Read(e.Method["suspendRet"]) ?? AnyTn;

        // A global (owner#name -> resultType) index of EVERY method (not just suspend). The eval-order spill
        // (BUG 2 fix, Rewrite/RewriteEvalOrder) uses it to type a temp SM field holding a left-of-suspension
        // operand whose own node carries no return type (a same-assembly `callStatic side()` has no `ret`).
        // Top-level -> "#name"; member -> "owner#name".
        var methodRets = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        // A global (owner#name -> declared type) index of EVERY member/static FIELD. The eval-order spill (BUG 2 fix,
        // N4) needs it to type the temp SM field that holds a raw `field`/`staticField`/`lateinitGet` read spilled to
        // the LEFT of a suspension — a raw field read node carries no `retType` (kotc emits only ownerType+name), so
        // without this the temp would fall back to kotlin.Any and box a value-type field, breaking the enclosing bin.
        var fieldTypes = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        foreach (var r in roots)
        {
            if (r is not JsonObject file) continue;
            if (file["methods"] is JsonArray fms)
                foreach (var m in fms)
                    if (m is JsonObject mo && Str(mo["name"]) is string mn)
                        methodRets["#" + mn] = TypeJson.Read(mo["suspendRet"]) ?? TypeJson.Read(mo["ret"]) ?? AnyTn;
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
                                    methodRets[ow + "#" + mn] = TypeJson.Read(mo["suspendRet"]) ?? TypeJson.Read(mo["ret"]) ?? AnyTn;
                        if (to["fields"] is JsonArray tfs)
                            foreach (var f in tfs)
                                if (f is JsonObject fo && Str(fo["name"]) is string fn && TypeJson.Read(fo["type"]) is TypeNode ft1)
                                    fieldTypes[ow + "#" + fn] = ft1;
                    }
        }

        if (entries.Count == 0) return calleeRet;

        // 2. Fixpoint: a fun stays transformable only if EVERY suspend call it makes is RESOLVABLE — a
        //    same-assembly transformable callee (its cold entry will be synthesized) OR a cross-assembly
        //    callee whose ref.dll MemberBinding.Suspend flag + the naming convention give the cold entry.
        var transformable = new HashSet<FunKey>(entries.Keys);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var key in transformable.ToList())
                foreach (var call in SuspendCalls(entries[key].Method))
                    if (!IsResolvable(call, transformable, refs))
                    {
                        // L3 (make-it-loud): a shape-eligible suspend fun is dropped from the cold-transform set
                        // because ONE of its suspend calls can't be resolved to a cold entry (no same-assembly
                        // transformable callee AND no ref.dll Suspend-flagged member). Left silent, the fun stays
                        // `suspend:true` and only trips the DISTANT "suspend method reached codegen un-lowered"
                        // NotSupportedException at the ilemit boundary — pointing at the surviving method, not the
                        // root call. Emit the ROOT here: which fun, which unresolvable call (kind/owner/name).
                        var callKind = call.Instance ? "callInstance" : "callStatic";
                        var callOwner = call.Owner ?? "<top-level>";
                        var funDesc = (key.Owner ?? "<top-level>") + "." + key.Name + (string.IsNullOrEmpty(key.Sig) ? "" : "(" + key.Sig + ")");
                        Console.Error.WriteLine(
                            $"bir2cir: WARNING suspend-lowering: dropped '{funDesc}' from the cold-transform set — "
                            + $"unresolvable suspend {callKind} '{callOwner}.{call.Name}' (no same-assembly cold entry "
                            + "and no ref.dll Suspend-flagged member). This fun will reach ilemit un-lowered.");
                        transformable.Remove(key);
                        changed = true;
                        break;
                    }
        }
        if (transformable.Count == 0) return calleeRet;

        var baseIsLocal = localTypeFqns.Contains(ContinuationImplFqn);

        // The public Task<R> bridge (bundle-6 P4, design §11): resolve the Task-family BCL owners from the ref.dll
        // @ClrTypeAlias index (the SAME ref.dll-sourced substitution the member-call pass reads). `TaskCompletionSource`
        // is the sink the RootContinuation completes; `Task` is the hot public return. When either alias is absent from
        // the ref.dll (a build whose stdlib predates the taskinterop set), the bridge is skipped — the cold entry still
        // emits (Kotlin->Kotlin suspend calls are unaffected), only the cross-language Task ABI is dropped.
        var tcsBcl = refs.TryResolveClrOwner("kotlin.clr.TaskCompletionSource", out var tb, out _) ? tb : null;
        var taskBcl = refs.TryResolveClrOwner("kotlin.clr.Task", out var kb, out _) ? kb : null;

        // SM-type-name disambiguation for OVERLOADED members (same Owner+Name, differing only by param type):
        // each overload needs a UNIQUE SM class name (the cold-entry NAME stays `<name>$dotkt_suspend` — they are
        // IL overloads resolved by param type). A group of one keeps the bare `<owner>_<name>$sm` name (existing
        // output unchanged); a group of >1 appends a suffix from the param types' simple names
        // (`_Iterator`/`_Iterable`/`_Sequence`), falling back to a positional index on a residual collision.
        var smSuffix = new Dictionary<FunKey, string>();
        foreach (var g in entries.Keys.GroupBy(k => (k.Owner, k.Name)))
        {
            var members = g.ToList();
            if (members.Count == 1) { smSuffix[members[0]] = ""; continue; }
            var cands = members.Select(k => ParamSimpleNames(entries[k].Method)).ToList();
            var unique = cands.All(c => c.Length > 0) && cands.Distinct(StringComparer.Ordinal).Count() == cands.Count;
            for (var i = 0; i < members.Count; i++)
                smSuffix[members[i]] = "_" + (unique ? cands[i] : "ov" + i);
        }

        // 3. Transform each transformable fun, splicing the cold entry (into its declaring container) and the
        //    SM type (into its file's top-level types).
        foreach (var key in transformable)
        {
            var e = entries[key];
            // The ENCLOSING class's type-param names (for an instance member on a generic class): the SM is made
            // generic over them, `$this` is typed as the CONSTRUCTED self `Box[gp:T]`, and the bridge's self
            // cold-call targets that constructed self (not the open `Box`), or `this` (Box<T>) mismatches the
            // callee's declaring type at verification (StackUnexpected).
            var ownerTps = new List<string>();
            if (e.TypeNode?["typeParams"] is JsonArray otps)
                foreach (var t in otps)
                    if (t is JsonValue tv2 && tv2.TryGetValue<string>(out var s2)) ownerTps.Add(s2);
                    else if (t is JsonObject to2 && Str(to2["name"]) is string n2) ownerTps.Add(n2);
            // Per-file registry of top-level `__lambdaN` methods (the non-capturing `newDelegate` block bodies of a
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
            var ownerIsInterface = e.TypeNode != null && Str(e.TypeNode["kind"]) == "interface";
            var gen = new FunGen(e.Method, key.Name, e.FileClass, e.Owner, calleeRet, baseIsLocal, tcsBcl, taskBcl, ownerTps, closures, smSuffix[key], methodRets, fieldTypes, fileLambdas, ownerIsInterface);
            var newMethods = new List<JsonNode>();
            var newTypes = new List<JsonNode>();
            gen.Build(newMethods, newTypes);

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

            // A synthesized instance-member SM is a SEPARATE top-level class, so its `$this.<member>` accesses to
            // the enclosing class's PRIVATE members (e.g. SequenceBuilderIterator.yield's SM writing the private
            // `set_nextValue`/`set_state`) fail CLR visibility (MethodAccessException) — on the JVM the SM is a
            // nested class with private access; on CLR our SM is top-level. Widen exactly the private members the
            // SM touches on `$this` to `internal` (assembly-visible) so the same-assembly SM can reach them. The
            // enclosing class is itself internal machinery (SequenceBuilderIterator is a `private class`), so this
            // leaks nothing to the public surface.
            if (e.TypeNode != null) WidenPrivatesAccessedBySm(e.TypeNode, newTypes);
        }
        return calleeRet;
    }

    // Collect every member name a synthesized SM reads/writes through its `$this` field, then relax any matching
    // PRIVATE member on the enclosing type to `internal` so the separate SM class can access it.
    static void WidenPrivatesAccessedBySm(JsonObject typeNode, List<JsonNode> smTypes)
    {
        var accessed = new HashSet<string>(StringComparer.Ordinal);
        void Collect(JsonNode n)
        {
            if (n is JsonObject o)
            {
                // A node whose receiver is the SM's `$this` field: a callInstance's `method`, or a field read's `name`.
                if (o["recv"] is JsonObject r && Str(r["k"]) == "field" && Str(r["name"]) == "$this")
                {
                    if (Str(o["method"]) is string mn) accessed.Add(mn);
                    if (Str(o["k"]) is "field" or "setField" && Str(o["name"]) is string fn) accessed.Add(fn);
                }
                foreach (var kv in o) if (kv.Value != null) Collect(kv.Value);
            }
            else if (n is JsonArray a) foreach (var it in a) if (it != null) Collect(it);
        }
        foreach (var t in smTypes) Collect(t);
        if (accessed.Count == 0) return;

        void Relax(JsonObject member)
        {
            if (member != null && Str(member["vis"]) == "private") member["vis"] = "internal";
        }
        if (typeNode["methods"] is JsonArray ms)
            foreach (var m in ms)
                if (m is JsonObject mo && Str(mo["name"]) is string n && accessed.Contains(n)) Relax(mo);
        if (typeNode["fields"] is JsonArray fs)
            foreach (var f in fs)
                if (f is JsonObject fo && Str(fo["name"]) is string n && accessed.Contains(n)) Relax(fo);
        if (typeNode["properties"] is JsonArray ps)
            foreach (var p in ps)
                if (p is JsonObject po)
                {
                    // The SM calls property accessors by their get_/set_ method name; relax the accessor whose
                    // synthesized method name was accessed (and the property itself if it is directly named).
                    if (Str(po["name"]) is string pn)
                    {
                        if (accessed.Contains(pn)) Relax(po);
                        if (accessed.Contains("get_" + pn)) Relax(po["getter"] as JsonObject);
                        if (accessed.Contains("set_" + pn)) Relax(po["setter"] as JsonObject);
                    }
                }
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
        TypeNode resultType, List<string> typeParams, bool baseIsLocal,
        IReadOnlyDictionary<string, TypeNode> calleeRet = null, bool restricted = false)
    {
        if (arity < 0) return null;
        var gen = new FunGen(smName, arity, captures ?? new List<(string, TypeNode)>(), lambdaParams, body,
            resultType, typeParams,
            calleeRet as Dictionary<string, TypeNode> ??
                (calleeRet != null ? new Dictionary<string, TypeNode>(calleeRet, StringComparer.Ordinal)
                                   : new Dictionary<string, TypeNode>(StringComparer.Ordinal)),
            baseIsLocal, restricted);
        var types = new List<JsonNode>();
        gen.Build(new List<JsonNode>(), types);
        return types.Count > 0 ? (JsonObject)types[0] : null;
    }

    // Can a suspend call site be rewritten to a cold entry? Same-assembly: the callee is in `transformable`
    // (its cold entry gets synthesized here). Cross-assembly: the ref.dll flags the member `suspend`, so the
    // `<name>$dotkt_suspend` convention names the cold entry.
    static bool IsResolvable(CallRef call, HashSet<FunKey> transformable, ReferenceMetadataIndex refs)
    {
        // Match by (owner, name) — the call site carries no resolved overload signature, so a same-assembly
        // callee is resolvable if ANY overload with that owner+name is transformable (its cold entry gets
        // synthesized). Cross-assembly: the ref.dll Suspend flag + the `<name>$dotkt_suspend` convention.
        bool LocalMatch(string owner) => transformable.Any(k => k.Owner == owner && k.Name == call.Name);
        if (call.Instance)
            return LocalMatch(call.Owner) || refs.HasSuspendMember(call.Owner, call.Name);
        // callStatic: owner==null -> same-assembly top-level (possibly cross-file, keyed by name);
        // owner set -> a cross-assembly file-class static (ref.dll flag).
        if (call.Owner == null) return LocalMatch(null);
        return LocalMatch(call.Owner) || refs.HasSuspendMember(call.Owner, call.Name);
    }

    // --- shape gate ------------------------------------------------------------------------------------

    static bool IsShapeEligible(JsonObject m)
    {
        if (!Mod(m, "suspend")) return false;
        if (!Bool(m["static"])) return false;                       // top-level statics + extensions (kotc: __self param)
        if (Mod(m, "inline") || Bool(m["abstract"])) return false;
        if (m.ContainsKey("steps") || m.ContainsKey("coClass")) return false;  // old CPS / sequence path
        if (m["body"] is not JsonArray body) return false;
        return SuspensionsSupported(body, inHandler: false, tryDepth: 0);
    }

    // An INSTANCE suspend member (static==false, lives inside a class). Same structural gate as a top-level
    // fun, minus the static requirement. Now ADMITS (bundle-6 P5 A1b): generic-class members (the SM is threaded
    // the enclosing class type params — `$this` typed as the constructed self, the SM generic over them), and
    // abstract / open / override / virtual members (the cold entry is emitted abstract / virtual / override in
    // lockstep with the original so a virtual `x.g()` resolves to the right override at runtime).
    static bool IsMemberShapeEligible(JsonObject m, JsonObject typeNode)
    {
        if (!Mod(m, "suspend")) return false;
        if (Bool(m["static"])) return false;                        // a static member fun -> deferred
        if (Mod(m, "inline")) return false;
        if (m.ContainsKey("steps") || m.ContainsKey("coClass")) return false;
        // A member that is BOTH generic on its own (its own type params) AND on a generic class is deferred v1:
        // the SM would need to thread the union of both param lists (e.g. DeepRecursiveScope<T,R>'s
        // `<U,S> ...callRecursive`), an untested combination not needed for the sequence path. yield/yieldAll
        // (no own type params, only the class's) transform. Kept for abstract members too so a deferred
        // abstract slot and its (equally deferred) overrides stay consistent.
        var ownGeneric = m["typeParams"] is JsonArray mtp && mtp.Count > 0;
        var genericClass = typeNode["typeParams"] is JsonArray tps && tps.Count > 0;
        if (ownGeneric && genericClass) return false;
        // An ABSTRACT suspend member (no body) -> an abstract cold entry `<name>$dotkt_suspend` (Virtual|Abstract,
        // no SM); its concrete overrides fill the slot. Admitted here (no body to structurally validate).
        if (Bool(m["abstract"])) return true;
        if (m["body"] is not JsonArray body) return false;
        return SuspensionsSupported(body, inHandler: false, tryDepth: 0);
    }

    // Validate that every suspension point is in a position this pass can lower. Rejects: suspension in a
    // catch/finally handler, inside a lambda/closure, and a suspending try nested inside another suspending
    // try (the two-level dispatch is single-level v1). Member/cross-assembly suspend CALLS are now allowed —
    // their cold-shape resolvability is decided by the fixpoint, not here.
    static bool SuspensionsSupported(JsonNode node, bool inHandler, int tryDepth)
    {
        switch (node)
        {
            case JsonObject o:
            {
                var k = Str(o["k"]);
                // F2 — a suspendCoroutine/suspendCoroutineUninterceptedOrReturn call IS a supported cold suspension
                // point; do NOT descend into its embedded newClosure/newDelegate block arg (which would trip the
                // LambdaKinds refusal below).
                if (IsSuspendCoroutineCall(o)) return true;
                // GAP 2 — a `newSuspendLambda` VALUE built inside a suspend fun (e.g. a member `suspend fun go() =
                // run1 { … }` that constructs a `this`-capturing suspend lambda and drives it via a suspend-value
                // call) is SUPPORTED: the lambda is an opaque value whose OWN suspensions become a SEPARATE SM
                // (SuspendLambdaLowering), and its captures resolve in the enclosing cold SM (a spilled local -> an
                // SM field, `__outer` -> the member SM's `$this`). Do NOT descend into its body (that is the
                // lambda's own scope, validated by its own FunGen build) — descending would trip the refusal below.
                if (k == "newSuspendLambda") return true;
                // ANY OTHER lambda/closure/sequence node -> unsupported (genuine suspend lambdas, which emit a
                // `newClosure` and are NOT flagged `suspendCall`, are handled separately by SuspendLambdaLowering).
                // Left untouched.
                if (k != null && LambdaKinds.Contains(k))
                    return false;
                if (o.ContainsKey("suspendCall") && Bool(o["suspendCall"]))
                {
                    if (inHandler) return false;                        // suspension in catch/finally -> unsupported
                }
                if (k == "try")
                {
                    var bodyHasSusp = o["body"] != null && HasSuspension(o["body"]);
                    if (bodyHasSusp && tryDepth > 0) return false;      // nested suspending try -> unsupported (v1)
                    if (!SuspensionsSupported(o["body"] ?? JsonValue.Create(0), inHandler, bodyHasSusp ? tryDepth + 1 : tryDepth))
                        return false;
                    if (o["catches"] is JsonArray cs)
                        foreach (var c in cs)
                            if (c is JsonObject co && !SuspensionsSupported(co["body"] ?? JsonValue.Create(0), inHandler: true, tryDepth))
                                return false;
                    if (o["finally"] != null && !SuspensionsSupported(o["finally"], inHandler: true, tryDepth))
                        return false;
                    return true;
                }
                foreach (var kv in o)
                    if (kv.Value != null && !SuspensionsSupported(kv.Value, inHandler, tryDepth)) return false;
                return true;
            }
            case JsonArray a:
                foreach (var it in a) if (it != null && !SuspensionsSupported(it, inHandler, tryDepth)) return false;
                return true;
            default:
                return true;
        }
    }

    // Every suspend call this method makes, as a CallRef (kind + owner + callee name).
    static IEnumerable<CallRef> SuspendCalls(JsonObject method)
    {
        var seen = new HashSet<CallRef>();
        void Walk(JsonNode n)
        {
            if (n is JsonObject o)
            {
                // F2: a cross-module suspendCoroutine call is lowered INLINE (its block reconstructed in the SM),
                // not routed to a cold entry — so it is NOT a resolvability constraint. Skip it (and its block arg).
                if (IsSuspendCoroutineCall(o)) return;
                // GAP 1: a suspend-VALUE invoke is lowered inline to the cold-invoke helper (no named cold entry to
                // resolve), so it must NOT add a resolvability constraint — but DO descend (a nested suspension may
                // sit in the receiver/args). Guard only the CallRef add below.
                if (Bool(o["suspendCall"]) && !IsSuspendValueCall(o) && Str(o["method"]) is string mn)
                {
                    var k = Str(o["k"]);
                    if (k == "callInstance")
                        seen.Add(new CallRef(true, BareOwner(TypeJson.OwnerName(o["ownerType"])), mn));
                    else if (k == "callStatic")
                        seen.Add(new CallRef(false, BareOwner(TypeJson.OwnerName(o["owner"])), mn));
                }
                foreach (var kv in o) if (kv.Value != null) Walk(kv.Value);
            }
            else if (n is JsonArray a)
                foreach (var it in a) if (it != null) Walk(it);
        }
        if (method["body"] is JsonArray body) Walk(body);
        return seen;
    }

    // Strip a generic instantiation suffix from an owner token so a call site's instantiated ownerType
    // (`Box[kotlin.Int]`) matches the registry's bare class key (`Box`) / a ref.dll owner FQN.
    static string BareOwner(string s)
    {
        if (s == null) return null;
        var i = s.IndexOf('[');
        return i >= 0 ? s.Substring(0, i) : s;
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

    // BUG 1: does the subtree contain a `try` whose finally is non-empty AND whose body spans a suspension?
    // Such a finally needs the $suspending gate (it would otherwise run on the suspend-return leave and again at
    // exit). SuspensionsSupported guarantees at most one such level (nested suspending try is left untransformed).
    static bool HasSuspendingFinally(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                if (Str(o["k"]) == "try" && o["finally"] is JsonArray fin && fin.Count > 0
                    && o["body"] != null && HasSuspension(o["body"]))
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

    sealed class FunGen
    {
        const string ThisField = "$this";
        // BUG 1 (try/finally across a suspension): a boolean SM field gating a suspending-try's finally. Set true
        // right before every `return COROUTINE_SUSPENDED`, reset false at the top of each invokeSuspend; the
        // finally runs its real body only when it is false — so it is SKIPPED on the suspend-return unwind (when
        // the CLR runs the finally on the `leave`) and RUNS EXACTLY ONCE on the post-resume normal/exception exit.
        // This mirrors the C#/JVM state-gated finally (a per-label finally-route table collapsed to one flag,
        // valid because SuspensionsSupported admits only a SINGLE level of suspending try).
        const string SuspendingField = "$suspending";
        // The Task-bridge root sink (a real emitted stdlib class, referenced cross-assembly like ContinuationImpl).
        const string RootContinuationFqn = "kotlin.coroutines.clr.internal.RootContinuation";
        // bundle-6 P4 REVERSE bridge — the facadegen-injected Task.await marker + the BCL awaiter family.
        const string AwaitMarkerOwner = "kotlin.clr.CoroutinesKt";
        const string TaskFqn = "System.Threading.Tasks.Task";
        const string TaskAwaiterFqn = "System.Runtime.CompilerServices.TaskAwaiter";
        const string ActionFqn = "System.Action";

        readonly JsonObject _m;
        readonly string _name;
        readonly string _fileClass;
        readonly string _ownerClass;             // enclosing class FQN for an instance member, else null
        readonly bool _isMember;
        readonly Dictionary<string, TypeNode> _calleeRet;
        readonly bool _baseIsLocal;
        // The public Task<R> bridge BCL owners (from the ref.dll @ClrTypeAlias index); null -> no bridge (see ApplyAll).
        readonly string _tcsBcl;
        readonly string _taskBcl;
        readonly List<string> _ownerTypeParams;   // enclosing class type-param names (instance member on a generic class)
        readonly List<string> _smAllTps;           // owner + method type-param names (the SM's own generic params)
        readonly TypeNode _selfType;               // constructed self `Box<T>` (instance member), else _ownerClass/null
        readonly bool _memberAbstract;             // source member is `abstract` -> abstract cold entry, no SM
        readonly bool _memberOverride;             // source member is `override` -> override cold entry (fills base slot)
        readonly bool _memberVirtual;              // source member is `open` -> virtual cold entry (new vtable slot)
        // Closure-class registry (name -> type node) for the suspendCoroutine intrinsic inliner. Empty in lambda mode.
        readonly IReadOnlyDictionary<string, JsonObject> _closures;
        // Top-level `__lambdaN` method registry (name -> method) for a cross-module suspendCoroutine's non-capturing
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
        readonly bool _resultNullable;           // the suspend fn's result had an outer `?` (#37/#48: read off the type node)
        readonly List<JsonObject> _params;       // original params (extension: leading __self)
        readonly List<string> _typeParams;       // generic type-param names ([] when non-generic)
        readonly HashSet<string> _fields = new(StringComparer.Ordinal);
        readonly List<(string name, TypeNode type)> _fieldDecls = new();
        // Synthesized SM methods for each `task.await()` suspension point (the OnCompleted Action callback
        // that re-drives THIS SM via resumeWith). Populated during body emission; spliced into the SM type.
        readonly List<JsonObject> _awaitResumeMethods = new();

        // Global (owner#name -> resultType) index for typing eval-order spill fields (BUG 2). Empty in lambda mode.
        readonly Dictionary<string, TypeNode> _methodRets;
        readonly Dictionary<string, TypeNode> _fieldTypes;   // (owner#field / #field) -> declared type, for eval-order spill typing (N4)

        int _state;                              // resume-state counter (>=1)
        int _label;                              // label id allocator (above kotc's low ids)
        int _condCounter;
        int _ordCounter;                         // eval-order spill temp-field counter (BUG 2)
        bool _needSuspendGuard;                  // fun has a suspending try/finally -> emit the $suspending gate (BUG 1)
        readonly List<(int state, int label)> _dispatch = new();
        readonly Stack<(List<(int state, int label)> inner, int tryEntry)> _tryStack = new();

        public FunGen(JsonObject m, string name, string fileClass, string ownerClass,
            Dictionary<string, TypeNode> calleeRet, bool baseIsLocal, string tcsBcl = null, string taskBcl = null,
            List<string> ownerTypeParams = null, IReadOnlyDictionary<string, JsonObject> closures = null,
            string smNameSuffix = "", Dictionary<string, TypeNode> methodRets = null,
            Dictionary<string, TypeNode> fieldTypes = null,
            IReadOnlyDictionary<string, JsonObject> lambdaMethods = null,
            bool ownerIsInterface = false)
        {
            _m = m; _name = name; _fileClass = fileClass; _ownerClass = ownerClass;
            _isMember = ownerClass != null;
            _calleeRet = calleeRet; _baseIsLocal = baseIsLocal;
            _methodRets = methodRets ?? new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            _fieldTypes = fieldTypes ?? new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            _tcsBcl = tcsBcl; _taskBcl = taskBcl;
            _closures = closures ?? new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            _lambdaMethods = lambdaMethods ?? new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            _ownerTypeParams = ownerTypeParams ?? new List<string>();
            // Virtuality of the source member (kept in lockstep on the cold entry): an abstract member -> an abstract
            // cold entry (no SM); an override/open member -> an override/virtual cold entry (fills/opens the slot).
            // An interface member with no body is abstract (see the ownerIsInterface note at the call site): its
            // cold entry + Task bridge are emitted abstract, mirroring an abstract-class member.
            var interfaceAbstract = ownerIsInterface
                && (m["body"] is not JsonArray ib || ib.Count == 0);
            _memberAbstract = _isMember && (Bool(m["abstract"]) || interfaceAbstract);
            _memberOverride = _isMember && Bool(m["override"]);
            _memberVirtual = _isMember && Bool(m["virtual"]);
            _smType = (ownerClass ?? fileClass) + "_" + name + smNameSuffix + "$sm";
            _coldName = name + "$dotkt_suspend";
            // #37/#48: the result nullability now rides the `suspendRet` TYPE NODE (`{t:nullable,of:R}`), not a retired
            // scalar `retNullable` flag. Strip the outer `?` so `_resultType` is the bare R (as it always was for the
            // reference case) and record it in `_resultNullable` for the Task-bridge NRT walk.
            var suspendRetRaw = TypeJson.Read(m["suspendRet"]);
            _resultNullable = suspendRetRaw is TypeNode.Nullable;
            _resultType = (suspendRetRaw is TypeNode.Nullable srn ? srn.Of : suspendRetRaw) ?? VoidTn;
            _params = (m["params"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            _typeParams = ReadTypeParamNames(m["typeParams"]);
            // The SM is generic over the ENCLOSING class's type params (an instance member on a generic class) PLUS
            // the member's own — its fields / `$this` / label reference them (type-scope tv by flattened position).
            _smAllTps = new List<string>(_ownerTypeParams);
            foreach (var t in _typeParams) if (!_smAllTps.Contains(t)) _smAllTps.Add(t);
            _selfType = _ownerClass == null ? null
                : _ownerTypeParams.Count == 0 ? new TypeNode.Fqn(_ownerClass)
                : new TypeNode.Fqn(_ownerClass, TypeTvs(_ownerTypeParams.Count));
            _smTypeInst = _smAllTps.Count == 0 ? new TypeNode.Fqn(_smType) : new TypeNode.Fqn(_smType, TypeTvs(_smAllTps.Count));
        }

        // The first `n` type-scope generic params by flattened index (Tv{type,0..n-1}).
        static TypeNode[] TypeTvs(int n) => Enumerable.Range(0, n).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray();

        // Lambda-mode ctor (Part B). Builds a `<smName> : SuspendLambda` SM from a newSuspendLambda node's
        // parts. Captures become ctor params + fields; the lambda's own params become fields set by create().
        public FunGen(string smName, int arity, List<(string name, TypeNode type)> captures,
            List<JsonObject> lambdaParams, JsonArray body, TypeNode resultType, List<string> typeParams,
            Dictionary<string, TypeNode> calleeRet, bool baseIsLocal, bool restricted = false)
        {
            _isLambda = true;
            _restrictedBase = restricted;
            _methodRets = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            _fieldTypes = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            _ownerTypeParams = new List<string>();
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
            _typeParams = typeParams ?? new List<string>();
            _smAllTps = _typeParams;   // a lambda SM has no enclosing-class type params
            _smTypeInst = _typeParams.Count == 0 ? new TypeNode.Fqn(_smType) : new TypeNode.Fqn(_smType, TypeTvs(_typeParams.Count));
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
            if (_memberAbstract)
            {
                // An abstract suspend member -> the abstract cold-entry DECLARATION (no SM, no drain). Concrete
                // overrides fill the slot; a Kotlin virtual `scope.yield(x)` dispatches through it.
                newMethods.Add(ColdEntryAbstract());
                // BUG 3 (interface/abstract suspend round-trip): also emit the ABSTRACT Task<T> bridge SIGNATURE so the
                // member carries the [KotlinFunction(Suspend)] trigger (via suspendBridge) — else facadegen sees only the
                // object-returning `$dotkt_suspend` cold entry and cannot restore `suspend fun` on a re-consuming Kotlin.
                // Concrete overrides emit the matching override bridge (below), filling this abstract slot.
                if (WantsBridge) newMethods.Add(BuildBridge());
                return;
            }

            var body = _isLambda ? _lambdaBody : ((_m["body"] as JsonArray) ?? new JsonArray());
            // Pre-pass: alpha-rename shadowed same-name locals of DIFFERENT types (see DisambiguateShadowedVars)
            // so each distinct declaration gets its OWN correctly-typed SM field. Returns the input unchanged
            // when there is no type clash; a renamed CLONE otherwise (never mutates the shared/retained body).
            body = DisambiguateShadowedVars(body);
            var hasSuspension = HasSuspension(body);
            // BUG 1: does the body contain a try whose finally spans a suspension? If so, the finally must be
            // gated (see SuspendingField) so it does not run on the suspend-return unwind and re-run at exit.
            _needSuspendGuard = hasSuspension && HasSuspendingFinally(body);

            if (!_isLambda && !hasSuspension)
            {
                // No suspension point: the cold entry IS the body directly (extra unused completion param,
                // Any? return so a value return boxes). No SM needed. For an instance member the cold entry
                // stays an INSTANCE method on the class, so a `this`/receiver in the body remains valid.
                // (A suspend LAMBDA always becomes an SM even without suspension — its VALUE is the SM instance.)
                newMethods.Add(ColdEntryDirect(body));
                if (_name == "main" && !_isMember) newMethods.Add(DrainMain());
                if (WantsBridge) newMethods.Add(BuildBridge());
                return;
            }

            _label = MaxLabelId(body) + 1000;

            AddField("label", IntTn);
            if (_needSuspendGuard) AddField(SuspendingField, BoolTn);   // BUG 1: the finally gate flag
            if (_isMember) AddField(ThisField, _selfType);          // holds the enclosing (constructed) instance
            if (_isLambda)
                foreach (var (n, t) in _captures) AddField(n, t);   // captured vars -> ctor-set fields
            foreach (var p in _params)
                AddField(Str(p["name"]), TypeJson.Read(p["type"]));           // lambda: create()-set param field(s)
            CollectVarFields(body, inHandler: false);

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

            if (_isLambda)
            {
                newTypes.Add(SmTypeLambda(invoke));
                return;
            }

            newTypes.Add(SmType(invoke));
            newMethods.Add(ColdEntrySm());
            if (_name == "main" && !_isMember) newMethods.Add(DrainMain());
            if (WantsBridge) newMethods.Add(BuildBridge());
        }

        // A named (non-lambda), non-`main` suspend fun whose Task-family aliases resolved gets a public Task<R>
        // bridge. `main` is excluded (it is the entry point, drained by the synthesized plain `main`).
        // EXCLUDED too:
        //  - `_baseIsLocal` (the rt-STDLIB build): the bridge's RootContinuation/TCS/Task sinks are the coroutine
        //    primitives being DEFINED here, not external .NET refs — a bridge would `newClr` a local type as if it
        //    were referenced (NotSupported). The stdlib's own suspend members (yield/yieldAll/callRecursive) are
        //    internal machinery, not C#-facing Task APIs; the bridge is an APP-build concern (consumers of the dll).
        // A virtual/abstract/override member DOES get a bridge (BUG 3): the bridge's virtuality rides in lockstep with
        // the cold entry (abstract -> an abstract signature; open -> virtual; override -> override), so an interface
        // `suspend fun` round-trips (its [KotlinFunction(Suspend)] trigger lives on the bridge) and its concrete
        // overrides fill both the bridge and the cold-entry slots.
        bool WantsBridge => !_isLambda && _name != "main" && _tcsBcl != null && _taskBcl != null && !_baseIsLocal;

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

        void AddField(string name, TypeNode type)
        {
            if (name == null || !_fields.Add(name)) return;
            _fieldDecls.Add((name, type ?? AnyTn));
        }

        void AddFieldTyped(string name, TypeNode type)
        {
            if (_fields.Add(name)) _fieldDecls.Add((name, type ?? AnyTn));
        }

        // Make SM-field allocation SCOPE-AWARE for shadowed same-name locals of DIFFERENT types. A coroutine
        // body may declare the same `var` name in DISJOINT scopes with DIFFERENT types (e.g.
        // SlidingWindow.windowedIterator's `var buffer = ArrayList<T>()` in one if-branch vs
        // `var buffer = RingBuffer<T>(...)` in the other). CollectVarFields keys SM fields by NAME, so it would
        // collapse the two to a single field of ONE type -> the other branch's `buffer.expanded()/isFull()` then
        // can't resolve on the wrong-typed field (ilverify StackUnexpected / runtime "Iterator has failed").
        //
        // We alpha-rename the shadowing declarations (`buffer` / `buffer$2`) so each distinct-typed declaration
        // gets its OWN correctly-typed SM field, binding every `local`/`setLocal` reference to the declaration
        // lexically IN SCOPE (a scope-frame stack, resolved innermost-first, one frame per block/valueBlock/try
        // body/catch/finally). This is the general "shadowed same-name locals of different types" fix, common in
        // generated/inlined stdlib code. Only names whose declarations DISAGREE on type are renamed (the common
        // case — including same-type disjoint reuse that harmlessly shares one field — is left byte-identical),
        // and we return the INPUT array untouched when there is no clash. On a clash we operate on a DeepClone,
        // so the shared/retained original body node (kept in the rt-stdlib build) is never mutated.
        static JsonArray DisambiguateShadowedVars(JsonArray body)
        {
            // 1. Which `var` names are declared with more than one distinct type? (Skip nested lambda/closure and
            //    the suspendCoroutine intrinsic subtrees — they own their own scope, handled separately.)
            var declTypes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            void Scan(JsonNode n)
            {
                if (n is JsonObject o)
                {
                    var k = Str(o["k"]);
                    // Skip nested lambda/closure subtrees (incl. the F2 suspendCoroutine call's newClosure/newDelegate
                    // block arg) — they own their own scope, handled separately.
                    if (k != null && LambdaKinds.Contains(k)) return;
                    if (k == "var" && Str(o["name"]) is string vn)
                    {
                        if (!declTypes.TryGetValue(vn, out var set))
                            declTypes[vn] = set = new HashSet<string>(StringComparer.Ordinal);
                        set.Add(Str(o["type"]) ?? "kotlin.Any");
                    }
                    foreach (var kv in o) if (kv.Value != null) Scan(kv.Value);
                }
                else if (n is JsonArray a) foreach (var it in a) if (it != null) Scan(it);
            }
            Scan(body);
            var conflicts = new HashSet<string>(
                declTypes.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key), StringComparer.Ordinal);
            if (conflicts.Count == 0) return body;

            // 2. Rename the conflicting declarations + their in-scope references on a private clone.
            var clone = (JsonArray)body.DeepClone();
            // Per conflicting name: type -> assigned emitted name. First type keeps the bare name; each further
            // type gets a `$N` suffix. A same (name,type) reused across disjoint scopes maps to the SAME field.
            var assigned = new Dictionary<string, Dictionary<TypeNode, string>>(StringComparer.Ordinal);
            string Assign(string name, TypeNode type)
            {
                if (!assigned.TryGetValue(name, out var byType))
                    assigned[name] = byType = new Dictionary<TypeNode, string>();
                if (byType.TryGetValue(type, out var nn)) return nn;
                var newName = byType.Count == 0 ? name : name + "$" + (byType.Count + 1);
                byType[type] = newName;
                return newName;
            }

            var scopes = new List<Dictionary<string, string>>();
            string Resolve(string name)
            {
                for (var i = scopes.Count - 1; i >= 0; i--)
                    if (scopes[i].TryGetValue(name, out var r)) return r;
                return null;
            }
            void PushList(JsonArray stmts)
            {
                scopes.Add(new Dictionary<string, string>(StringComparer.Ordinal));
                foreach (var s in stmts) if (s != null) Visit(s);
                scopes.RemoveAt(scopes.Count - 1);
            }
            void Visit(JsonNode n)
            {
                if (n is JsonArray a) { foreach (var it in a) if (it != null) Visit(it); return; }
                if (n is not JsonObject o) return;
                var k = Str(o["k"]);
                if (k != null && LambdaKinds.Contains(k)) return;   // nested lambda/closure (incl. F2 block arg) -> separate SM
                switch (k)
                {
                    case "var":
                        if (o["init"] != null) Visit(o["init"]);    // init binds in the OUTER scope (before decl)
                        if (Str(o["name"]) is string vn && conflicts.Contains(vn))
                        {
                            var nn = Assign(vn, TypeJson.Read(o["type"]) ?? AnyTn);
                            o["name"] = nn;
                            scopes[^1][vn] = nn;
                        }
                        return;
                    case "local":
                        if (Str(o["name"]) is string ln && Resolve(ln) is string lr) o["name"] = lr;
                        return;
                    case "setLocal":
                        if (o["value"] != null) Visit(o["value"]);
                        if (Str(o["name"]) is string sn && Resolve(sn) is string sr) o["name"] = sr;
                        return;
                    case "block":
                    case "valueBlock":
                        scopes.Add(new Dictionary<string, string>(StringComparer.Ordinal));
                        if (o["body"] is JsonArray bb) foreach (var s in bb) if (s != null) Visit(s);
                        if (o["stmts"] is JsonArray ss) foreach (var s in ss) if (s != null) Visit(s);
                        if (o["result"] != null) Visit(o["result"]);   // evaluated in the block's scope
                        scopes.RemoveAt(scopes.Count - 1);
                        return;
                    case "try":
                        if (o["body"] is JsonArray tb) PushList(tb);
                        if (o["catches"] is JsonArray cs)
                            foreach (var c in cs)
                                if (c is JsonObject co && co["body"] is JsonArray cb) PushList(cb);
                        if (o["finally"] is JsonArray fb) PushList(fb);
                        return;
                    default:
                        foreach (var kv in o) if (kv.Value != null) Visit(kv.Value);
                        return;
                }
            }
            PushList(clone);
            return clone;
        }

        void CollectVarFields(JsonNode node, bool inHandler)
        {
            switch (node)
            {
                case JsonObject o:
                    var k = Str(o["k"]);
                    // Nested lambda/closure subtrees (incl. the F2 suspendCoroutine block arg) own their own scope —
                    // their captures resolve via capMap, not as SM fields.
                    if (k != null && LambdaKinds.Contains(k)) return;
                    if (k == "var" && !inHandler)
                        AddField(Str(o["name"]), TypeJson.Read(o["type"]));
                    if (k == "try")
                    {
                        CollectVarFields(o["body"] ?? JsonValue.Create(0), inHandler);
                        if (o["catches"] is JsonArray cs)
                            foreach (var c in cs)
                                if (c is JsonObject co) CollectVarFields(co["body"] ?? JsonValue.Create(0), inHandler: true);
                        if (o["finally"] != null) CollectVarFields(o["finally"], inHandler: true);
                        return;
                    }
                    foreach (var kv in o) if (kv.Value != null) CollectVarFields(kv.Value, inHandler);
                    return;
                case JsonArray a:
                    foreach (var it in a) if (it != null) CollectVarFields(it, inHandler);
                    return;
            }
        }

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
                    var val = init == null ? NullConst(TypeJson.Read(o["type"]) ?? AnyTn) : Rewrite(init, outp);
                    if (_fields.Contains(nm)) outp.Add(SetField(nm, val));
                    else outp.Add(new JsonObject { ["k"] = "var", ["name"] = nm, ["type"] = o["type"]?.DeepClone(), ["init"] = val });
                    break;
                }
                case "setLocal":
                {
                    var nm = Str(o["name"]);
                    var val = Rewrite(o["value"], outp);
                    if (_fields.Contains(nm)) outp.Add(SetField(nm, val));
                    else outp.Add(new JsonObject { ["k"] = "setLocal", ["name"] = nm, ["value"] = val });
                    break;
                }
                case "return":
                {
                    var v = o["value"];
                    outp.Add(v == null ? Ret(NullConst(AnyTn)) : Ret(Rewrite(v, outp)));
                    break;
                }
                case "exprStmt":
                    outp.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = Rewrite(o["expr"], outp) });
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
                        ["cond"] = Rewrite(o["cond"], outp),
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
            var bodyHasSusp = o["body"] != null && HasSuspension(o["body"]);
            if (!bodyHasSusp)
            {
                outp.Add(RewriteTryPlain(o));
                return;
            }
            var tryEntry = NextLabel();
            outp.Add(Label(tryEntry));

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
                if (Str(o["k"]) == "local" && Str(o["name"]) is string ln && _fields.Contains(ln))
                    return FieldOf(ln, FieldType(ln));
                if (Str(o["k"]) == "local" && Str(o["name"]) == "__self" && CapturedSelfField() is JsonNode sf0)
                    return sf0;
                if (Str(o["k"]) == "setLocal" && Str(o["name"]) is string sln && _fields.Contains(sln))
                    return SetField(sln, RewriteNoSpill(o["value"]));
                if (Str(o["k"]) == "var" && Str(o["name"]) is string vln && _fields.Contains(vln))
                    return SetField(vln, o["init"] == null
                        ? NullConst(TypeJson.Read(o["type"]) ?? AnyTn) : RewriteNoSpill(o["init"]));
                if (_isMember && Str(o["k"]) == "this")
                    return FieldOf(ThisField, new TypeNode.Fqn(_ownerClass));
                if (Str(o["k"]) == "this" && CapturedOuterField() is JsonNode of0)
                    return of0;
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
        JsonNode Rewrite(JsonNode node, List<JsonNode> outp)
        {
            if (node is JsonObject o)
            {
                var k = Str(o["k"]);
                // `smSelf`: the state-machine's OWN identity (`this` in invokeSuspend). Introduced by the F2
                // suspendCoroutine inliner (EmitSuspendCoroutineCall) for the `c`/continuation binding — it must survive
                // the `this`->`$this` member rewrite (a captured `this` means the ENCLOSING receiver -> $this; `c` means
                // the SM itself).
                if (k == "smSelf") return new JsonObject { ["k"] = "this" };
                if (k == "local" && Str(o["name"]) is string ln && _fields.Contains(ln))
                    return FieldOf(ln, FieldType(ln));
                if (k == "local" && Str(o["name"]) == "__self" && CapturedSelfField() is JsonNode sf1)
                    return sf1;
                // A `setLocal`/`var` that assigns a SPILLED variable but sits INSIDE an expression subtree (e.g. the
                // `index++` post-increment lowered to `valueBlock { var <unary> = index; index = index+1; <unary> }`)
                // is reached via Rewrite, not the statement-level EmitStmt, so its field-assignment must be redirected
                // here too — else a bare `setLocal index` to an SM FIELD reaches ilemit as `store unknown var index`.
                if (k == "setLocal" && Str(o["name"]) is string sln && _fields.Contains(sln))
                    return SetField(sln, Rewrite(o["value"], outp));
                if (k == "var" && Str(o["name"]) is string vln && _fields.Contains(vln))
                    return SetField(vln, o["init"] == null
                        ? NullConst(TypeJson.Read(o["type"]) ?? AnyTn) : Rewrite(o["init"], outp));
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
                if (IsSuspendCoroutineCall(o))
                    return EmitSuspendCoroutineCall(o, outp);
                // #11 — a `valueBlock` whose stmts/result span a suspension (e.g. an INLINE scope function
                // `with(lib){ b.fetch() }` used as an expression body: kotc inlines it to
                // `valueBlock { stmts:[var __scope0=lib], result: __scope0.fetch(b) }`, its result a suspend call).
                // A valueBlock's stmts run IN PLACE, so flatten it here: emit the stmts to `outp` as ordinary
                // statements (their `var`s were collected as SM fields, so they survive the suspension), then
                // rewrite the result expression — a suspend call in the result becomes a normal suspension point
                // owned by this pass. A suspension-FREE valueBlock (e.g. an `index++` post-increment) is left
                // intact (the default copy below) for ilemit's inline emission — output stays byte-identical.
                if (k == "valueBlock" && HasSuspension(o))
                {
                    if (o["stmts"] is JsonArray vbStmts) foreach (var s in vbStmts) EmitStmt(s, outp);
                    if (o["body"] is JsonArray vbBody) foreach (var s in vbBody) EmitStmt(s, outp);
                    return o["result"] != null ? Rewrite(o["result"], outp) : NullConst(TypeJson.Read(o["type"]) ?? AnyTn);
                }
                // bundle-6 P4 REVERSE bridge — the Task.await() marker (kotlin.clr.CoroutinesKt.await, suspendCall).
                // A2 (#61): kotc emits it as a PLAIN callStatic/callInstance by identity; its kotlin.* owner is SKIPPED by
                // NetInteropBinding (a stdlib owner), so it reaches here unshaped, the owner in `ownerType`. It MUST be
                // caught BEFORE the generic callStatic/callInstance suspendCall path below — else it routes to a bogus
                // same-assembly cold entry `await$dotkt_suspend` (unresolved). The generic-vs-non-generic split is the
                // `typeArgs` presence (was the `clrGenericStatic` k). (`type` covers the pre-A2 clr* form for safety.)
                if (Bool(o["suspendCall"]) && Str(o["method"]) == "await"
                    && (Str(o["type"]) ?? TypeJson.OwnerName(o["ownerType"])) == AwaitMarkerOwner
                    && k is "callStatic" or "callInstance" or "clrStatic" or "clrGenericStatic")
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
                if (k == "cond" && HasSuspension(o))
                    return EmitCondValue(o, outp);
                // BUG 2 (left-to-right evaluation order across a suspension): when an ordered-eval node contains a
                // suspension in a LATER operand, any impure earlier operand must be evaluated + SPILLED into a temp
                // SM field BEFORE the suspension's segments are appended (else its side effects run after the
                // suspension resumes). Applies to `bin` (l,r) and call/new arg lists (recv,args...).
                if (HasSuspension(o))
                {
                    if (k == "binOp")
                    {
                        var rw = RewriteEvalOrder(new List<JsonNode> { o["lhs"], o["rhs"] }, outp);
                        var binCopy = new JsonObject();
                        foreach (var kv in o) binCopy[kv.Key] = kv.Value?.DeepClone();
                        binCopy["lhs"] = rw[0];
                        binCopy["rhs"] = rw[1];
                        return binCopy;
                    }
                    if (k is "callStatic" or "callInstance" or "clrStatic" or "clrInstance"
                        or "clrGenericStatic" or "new" or "newClr")
                        return RewriteCallOrdered(o, outp);
                }
                var copy = new JsonObject();
                foreach (var kv in o) copy[kv.Key] = kv.Value == null ? null : Rewrite(kv.Value, outp);
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

        // BUG 2: rewrite a left-to-right eval sequence, SPILLING each impure operand that precedes a later
        // suspension into a temp SM field NOW (so its side effects happen before the suspension's segments are
        // appended by a subsequent Rewrite). An operand that itself carries the suspension is already spilled to
        // its own `__aw` field by EmitSuspensionPoint, so it needs no extra spill; a pure operand (no call/new/
        // assignment anywhere in it) is stable across the suspension and stays inline (keeps output byte-identical
        // for the common `acc + one()` case where the left operand is a plain field/local read).
        List<JsonNode> RewriteEvalOrder(List<JsonNode> kids, List<JsonNode> outp)
        {
            var lastSusp = -1;
            for (var i = 0; i < kids.Count; i++)
                if (kids[i] != null && HasSuspension(kids[i])) lastSusp = i;
            var res = new List<JsonNode>(kids.Count);
            for (var i = 0; i < kids.Count; i++)
            {
                var child = kids[i];
                if (child == null) { res.Add(null); continue; }
                var rw = Rewrite(child, outp);
                if (i < lastSusp && !HasSuspension(child) && !IsPureExpr(child))
                {
                    var ty = TypeOfExpr(child);
                    var tmp = "__ord$" + (++_ordCounter);
                    AddFieldTyped(tmp, ty);
                    outp.Add(SetField(tmp, rw));
                    res.Add(FieldOf(tmp, ty));
                }
                else res.Add(rw);
            }
            return res;
        }

        // BUG 2 for call/new nodes that CONTAIN (but are not themselves) a suspension: rewrite recv then args in
        // eval order, spilling impure operands to the left of a suspending operand.
        JsonNode RewriteCallOrdered(JsonObject o, List<JsonNode> outp)
        {
            var recv = o["recv"];
            var argsArr = o["args"] as JsonArray;
            var kids = new List<JsonNode>();
            if (recv != null) kids.Add(recv);
            if (argsArr != null) foreach (var arg in argsArr) kids.Add(arg);
            var rw = RewriteEvalOrder(kids, outp);
            var idx = 0;
            var recvRw = recv != null ? rw[idx++] : null;
            var argsRw = new JsonArray();
            if (argsArr != null) foreach (var _ in argsArr) argsRw.Add(rw[idx++]);
            var copy = new JsonObject();
            foreach (var kv in o)
            {
                if (kv.Key == "recv") copy["recv"] = recvRw;
                else if (kv.Key == "args") copy["args"] = argsRw;
                else copy[kv.Key] = kv.Value?.DeepClone();
            }
            return copy;
        }

        // Impure kinds whose PRESENCE anywhere in a subtree makes it unsafe to defer past a suspension (a call,
        // an allocation, or an assignment has an observable effect). A subtree free of them is "pure" — stable to
        // read after the suspension resumes, so it stays inline. (A suspend fun's plain locals are its OWN private
        // SM fields, unreachable by the callee it suspends into, so a `local` read is stable — treated pure.)
        //
        // N4: a raw member/static FIELD read (`field`/`staticField`/`lateinitGet`) is NOT pure w.r.t. a later
        // suspension — the suspend callee can reach and MUTATE that field (a source property read goes through a
        // getter = `callInstance`/`clrInstance`, already impure; only the direct backing-field / `@ClrField` read
        // slipped through). So `this.x + mutatingSuspendCall()` must SPILL `this.x` into an SM temp BEFORE the
        // suspension, else the read happens after resume and observes the post-mutation value. Position still gates
        // it: RewriteEvalOrder only spills an operand LEFT of a suspension (`i < lastSusp`), so a field read with no
        // suspension to its right stays inline. (Captured locals are `local` in pre-Rewrite BIR, so unaffected.)
        //
        // N4-sibling: an ARRAY-ELEMENT read (`arrayGet`/`clr.ldelem`) has the SAME reorder hazard — the array is a
        // shared reference the suspend callee can reach, so `arr[i] + mutatingSuspendCall()` must read the PRE-call
        // element. It stayed scoped out over an element-type/over-spill worry, but the node carries its element type
        // verbatim on `elem`, so the spill temp is typed precisely (no kotlin.Any box fallback — see TypeOfExpr). The
        // position guard keeps a same-node `arr[i]` with no suspension to its right inline. (`arrayLen`/`clr.ldlen`
        // stays pure: a .NET array's length is immutable, so a later suspension cannot change it.)
        static readonly HashSet<string> ImpureKinds = new(StringComparer.Ordinal)
        {
            "callStatic", "callInstance", "clrStatic", "clrInstance", "clrGenericStatic",
            "new", "newClr", "setLocal", "setField", "throwExpr", "dynCall",
            "field", "staticField", "lateinitGet",
            "arrayGet",
        };
        static bool IsPureExpr(JsonNode n)
        {
            var impure = false;
            void Walk(JsonNode x)
            {
                if (impure || x == null) return;
                if (x is JsonObject o)
                {
                    if (Str(o["k"]) is string k && ImpureKinds.Contains(k)) { impure = true; return; }
                    foreach (var kv in o) if (kv.Value != null) Walk(kv.Value);
                }
                else if (x is JsonArray a) foreach (var it in a) if (it != null) Walk(it);
            }
            Walk(n);
            return !impure;
        }

        // The static type token of an expression, for typing an eval-order spill field. Reads an explicit type key
        // when present; else resolves a same-assembly call's declared return type from the global method index; a
        // `bin`'s result is its left-operand type (comparisons -> Boolean). Falls back to kotlin.Any.
        TypeNode TypeOfExpr(JsonNode n)
        {
            if (n is not JsonObject o) return AnyTn;
            if (TypeJson.Read(o["ret"]) is TypeNode t0) return t0;
            if (TypeJson.Read(o["ret"]) is TypeNode t1) return t1;
            if (TypeJson.Read(o["dynRet"]) is TypeNode t2) return t2;
            var k = Str(o["k"]);
            switch (k)
            {
                case "const": case "cast": case "new": case "newClr": case "valueBlock": case "var":
                    if (TypeJson.Read(o["type"]) is TypeNode t) return t;
                    break;
                case "callStatic":
                {
                    var name = Str(o["method"]);
                    var owner = TypeJson.OwnerName(o["owner"]);
                    var key = (owner == null ? "#" : BareOwner(owner) + "#") + name;
                    if (_methodRets.TryGetValue(key, out var rt)) return rt;
                    if (_methodRets.TryGetValue("#" + name, out var rt2)) return rt2;
                    break;
                }
                case "callInstance":
                {
                    var ot = BareOwner(TypeJson.OwnerName(o["ownerType"]));
                    if (ot != null && _methodRets.TryGetValue(ot + "#" + Str(o["method"]), out var rt)) return rt;
                    break;
                }
                case "field": case "staticField": case "lateinitGet":
                {
                    // N4 eval-order spill: type the temp SM field from the field's declared type (a raw field read
                    // carries no `retType`). Owner-qualified first (`owner#name`), then top-level (`#name`).
                    var fname = Str(o["name"]);
                    var fowner = BareOwner(TypeJson.OwnerName(o["ownerType"]));
                    if (fowner != null && _fieldTypes.TryGetValue(fowner + "#" + fname, out var fft)) return fft;
                    if (_fieldTypes.TryGetValue("#" + fname, out var fft2)) return fft2;
                    break;
                }
                case "arrayGet":
                    // N4-sibling eval-order spill: an array-element read carries its element type verbatim on `elem`,
                    // so the temp SM field is typed precisely (avoids a kotlin.Any box of a value-type element).
                    if (TypeJson.Read(o["elem"]) is TypeNode et) return et;
                    break;
                case "binOp":
                    return Str(o["op"]) is "==" or "!=" or "<" or ">" or "<=" or ">=" ? BoolTn : TypeOfExpr(o["lhs"]);
            }
            return AnyTn;
        }

        JsonNode EmitCondValue(JsonObject c, List<JsonNode> outp)
        {
            var ty = TypeJson.Read(c["type"]) ?? AnyTn;
            var resultField = "__cond$" + (++_condCounter);
            AddFieldTyped(resultField, ty);
            var elseL = NextLabel();
            var endL = NextLabel();

            var condExpr = Rewrite(c["cond"], outp);
            outp.Add(BrIf(condExpr, false, elseL));
            outp.Add(SetField(resultField, Rewrite(c["then"], outp)));
            outp.Add(Goto(endL));
            outp.Add(Label(elseL));
            outp.Add(SetField(resultField, Rewrite(c["else"], outp)));
            outp.Add(Label(endL));
            return FieldOf(resultField, ty);
        }

        // A suspension point (mirrors kotc emitSuspend): set label, start the cold call passing `this` (the SM,
        // a Continuation) as the callee's completion; if it returns COROUTINE_SUSPENDED, return SUSPENDED
        // (inline); else fall through to the merge label, rethrow a failed resume, store the awaited value.
        JsonNode EmitSuspensionPoint(JsonObject callNode, List<JsonNode> outp)
        {
            var retTok = TypeJson.Read(callNode["ret"])
                ?? TypeJson.Read(callNode["dynRet"])
                // A CROSS-ASSEMBLY suspend call arrives in the `clr*` vocabulary, whose declared return type rides `ret`
                // (not `retType`/`sig`) and is absent from _calleeRet (a same-assembly-only map). Read it so the awaited
                // value gets its real type (+ unbox/castclass) instead of falling to kotlin.Any.
                ?? TypeJson.Read(callNode["ret"])
                ?? (_calleeRet.TryGetValue(Str(callNode["method"]) ?? "", out var d) ? d : null)
                ?? AnyTn;
            if (IsUnitTn(retTok)) retTok = AnyTn;
            var state = ++_state;
            var resumeLabel = NextLabel();
            RegisterResume(state, resumeLabel);
            var field = "__aw$" + state;
            AddFieldTyped(field, retTok);

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
        // `callStatic <name>(<newClosure|newDelegate>)`, its wrapper body NOT inlined. We reconstruct it here:
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
            if (invBody == null)
                throw new InvalidOperationException(
                    $"unresolved {method} block in '{(_ownerClass ?? _fileClass)}.{_name}': the `{{ c -> … }}` block " +
                    $"(newClosure/newDelegate) could not be resolved in the compilation — refusing to emit a broken coroutine");

            var resultT = TypeJson.Read(callNode["ret"]) ?? AnyTn;
            var retTok = IsUnitTn(resultT) ? AnyTn : resultT;

            var state = ++_state;
            var resumeLabel = NextLabel();
            RegisterResume(state, resumeLabel);
            var awField = "__aw$" + state;
            AddFieldTyped(awField, retTok);

            JsonNode tail;
            if (wrapper)
            {
                var safeField = "__safe$" + state;
                AddFieldTyped(safeField, ContAnyTn);
                // this.__safe = newSafeContinuation((Continuation<Any?>) this)   — the SM is its own delegate.
                outp.Add(SetField(safeField, new JsonObject
                {
                    ["k"] = "callStatic", ["owner"] = ThrowOnFailureOwner, ["method"] = "newSafeContinuation",
                    ["args"] = new JsonArray
                    {
                        new JsonObject { ["k"] = "cast", ["type"] = ContAny(), ["e"] = new JsonObject { ["k"] = "this" } },
                    },
                    ["ret"] = ContAny(),
                }));
                var cBinding = SmSelfField(safeField, ContAnyTn);   // smSelf recv survives the this->$this member rewrite
                foreach (var s in invBody) EmitStmt(SubstBlock(s, capMap, cParam, cBinding, closureType), outp);
                tail = new JsonObject
                {
                    ["k"] = "callStatic", ["owner"] = ThrowOnFailureOwner, ["method"] = "safeGetOrThrow",
                    ["args"] = new JsonArray { SmSelfField(safeField, ContAnyTn) }, ["ret"] = Tw(AnyTn),
                };
            }
            else
            {
                var cBinding = new JsonObject { ["k"] = "smSelf" };
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

            outp.Add(SetField("label", IntConst(state)));
            outp.Add(new JsonObject { ["k"] = "setLocal", ["name"] = "result", ["value"] = Rewrite(tail, outp) });
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

        // Resolve a suspendCoroutine block arg (newClosure -> a top-level closure class in _closures; newDelegate -> a
        // top-level __lambdaN in _lambdaMethods) to its invoke body, continuation-param name, and capture map (empty
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
                var top = _tryStack.Peek();
                top.inner.Add((state, resumeLabel));
                _dispatch.Add((state, top.tryEntry));
            }
        }

        // A `task.await()` suspension point (bundle-6 P4 REVERSE bridge, design §4/§5) — the .NET-Task ⇒ Kotlin
        // suspend boundary. The facadegen-injected marker (kotlin.clr.CoroutinesKt.await, suspendCall) becomes the
        // cold-core awaiter dance, structurally IDENTICAL to EmitSuspensionPoint but obtaining the resume value from
        // a TaskAwaiter instead of a cold `$dotkt_suspend` return:
        //
        //   this.<aw> = ((Task<T>)task).GetAwaiter()        // TaskAwaiter<T> (a struct) spilled into an SM field
        //   if (this.<aw>.IsCompleted) goto L_state          // sync fast path — no suspension
        //   this.label = state
        //   this.<aw>.OnCompleted(<Action bound to this.$awaitOnDone$state>)   // flows ExecutionContext
        //   return COROUTINE_SUSPENDED
        //   L_state: <value> = this.<aw>.GetResult()          // throws on a faulted/canceled task
        //
        // The Action callback (a synthesized SM instance method) re-drives THIS SM: `this.resumeWith(Result(null))`.
        // The resumed `result` is only a WAKE TOKEN (Codex-verified Option B): the real value / fault comes from
        // GetResult() at L_state — a fault THROWS there, propagating up through invokeSuspend into
        // BaseContinuationImpl.resumeWith's catch → the completion (exactly the JVM Task.await semantics). So NO
        // throwOnFailure and NO try/catch in the callback are needed. TaskAwaiter is a readonly struct over a task
        // reference, so the field spill/copy is safe (Codex-confirmed).
        JsonNode EmitAwaitPoint(JsonObject awaitNode, List<JsonNode> outp)
        {
            // A2 (#61): generic await is now signaled by `typeArgs` presence (kotc emits a plain callStatic carrying the
            // type-arg fact), not the pre-A2 `clrGenericStatic` k-tag.
            var generic = awaitNode["typeArgs"] is JsonArray ga && ga.Count > 0;
            TypeNode resultTok = UnitTn, taskType, awaiterType;
            if (generic)
            {
                resultTok = ((awaitNode["typeArgs"] as JsonArray)?.FirstOrDefault() is JsonNode ta0
                    ? TypeJson.Read(ta0) : null) ?? AnyTn;
                taskType = new TypeNode.Fqn(TaskFqn, new[] { resultTok });
                awaiterType = new TypeNode.Fqn(TaskAwaiterFqn, new[] { resultTok });
            }
            else
            {
                taskType = new TypeNode.Fqn(TaskFqn);
                awaiterType = new TypeNode.Fqn(TaskAwaiterFqn);
            }

            var task = Rewrite((awaitNode["args"] as JsonArray)?[0], outp);

            var state = ++_state;
            var afterLabel = NextLabel();
            RegisterResume(state, afterLabel);

            var awField = "__awaiter$" + state;
            AddFieldTyped(awField, awaiterType);

            // this.<aw> = ((Task<T>)task).GetAwaiter();
            outp.Add(SetField(awField, new JsonObject
            {
                ["k"] = "clrInstance", ["type"] = Tw(taskType), ["method"] = "GetAwaiter",
                ["recv"] = task, ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
                ["ret"] = Tw(awaiterType),
            }));
            // if (this.<aw>.IsCompleted) goto L_state;   (sync fast path — no suspension)
            outp.Add(BrIf(new JsonObject
            {
                ["k"] = "clrPropGet", ["type"] = Tw(awaiterType), ["name"] = "IsCompleted",
                ["static"] = false, ["recv"] = FieldOf(awField, awaiterType), ["ret"] = Tw(BoolTn),
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
                    ["k"] = "clrInstance", ["type"] = Tw(awaiterType), ["method"] = "OnCompleted",
                    ["recv"] = FieldOf(awField, awaiterType),
                    ["argTypes"] = new JsonArray { Tn(ActionFqn) },
                    ["args"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["k"] = "newBoundDelegate", ["funcType"] = Tw(new TypeNode.Fn(false, VoidTn, System.Array.Empty<TypeNode>())),
                            ["ownerType"] = Tw(_smTypeInst), ["method"] = cbName, ["virtual"] = false,
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
                ["k"] = "clrInstance", ["type"] = Tw(awaiterType), ["method"] = "GetResult",
                ["recv"] = FieldOf(awField, awaiterType), ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
                ["ret"] = Tw(generic ? resultTok : VoidTn),
            };
            if (generic)
            {
                var valField = "__awval$" + state;
                AddFieldTyped(valField, resultTok);
                outp.Add(SetField(valField, getResult));
                return FieldOf(valField, resultTok);
            }
            // Non-generic Task.await(): Unit — GetResult is `void` (side-effecting), the value is Unit.
            outp.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = getResult });
            return NullConst(UnitTn);
        }

        // void $awaitOnDone$state() { this.resumeWith(Result(null)); } — the OnCompleted Action target that
        // re-drives THIS SM (Option B WAKE TOKEN; the value/fault flows from GetResult at the resume label). The
        // resumeWith call mirrors the rt-stdlib BaseContinuationImpl form (Continuation<object>.resumeWith,
        // Result(object) construction); ContinuationErasure then normalizes both to the Result<object> slot.
        JsonObject AwaitResumeMethod(string name)
        {
            var resumeCall = new JsonObject
            {
                ["k"] = "callInstance",
                ["ownerType"] = ContAny(),
                ["virtual"] = true,
                ["recv"] = new JsonObject { ["k"] = "this" },
                ["method"] = "resumeWith",
                ["sig"] = new JsonArray { Tw(new TypeNode.Fqn("kotlin.Result", new TypeNode[] { AnyTn })) },
                ["ret"] = Tw(VoidTn),
                ["args"] = new JsonArray
                {
                    // Result.success(null): the PUBLIC static companion factory (the internal `Result(value)` ctor is
                    // inaccessible cross-assembly, so an app SM cannot `new kotlin.Result`). typeArgs erased to Any by
                    // ContinuationErasure to hit the Result<object> resumeWith slot.
                    new JsonObject
                    {
                        ["k"] = "callStatic", ["owner"] = Tn("kotlin.Result"), ["method"] = "success",
                        ["typeArgs"] = new JsonArray { Tn("kotlin.Any") },
                        ["args"] = new JsonArray { NullConst(AnyTn) },
                        ["ret"] = Tw(new TypeNode.Fqn("kotlin.Result", new TypeNode[] { AnyTn })),
                    },
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

        TypeNode FieldType(string name)
        {
            foreach (var (n, t) in _fieldDecls) if (n == name) return t;
            return AnyTn;
        }

        // kotc names a suspend lambda's captured ENCLOSING extension receiver `__outer` (the `<this>` capture-field
        // convention, BirEmitter.kt:2929) yet, INSIDE the lambda body, references that receiver as `local __self`
        // (the enclosing static extension's receiver-param name, via selfSubst — BirEmitter.kt:1308). The two names
        // are the SAME captured value, so a body `__self` read maps to the `__outer` capture FIELD (`this.__outer`).
        // Guarded on `!__self` field: a NAMED-fun cold entry spills its real `__self` PARAM into a `__self` field,
        // which the generic local->field rule already redirects — this alias is only for the lambda-capture mismatch.
        JsonNode CapturedSelfField() =>
            (!_fields.Contains("__self") && _fields.Contains("__outer"))
                ? FieldOf("__outer", FieldType("__outer")) : null;

        // #34a — a suspend LAMBDA that closes over its enclosing INSTANCE captures it as the `__outer` field
        // (SuspendLambdaLowering seeds the ctor arg from the enclosing `this`/`__self`). kotc emits references to
        // that instance's members as a bare `this.member` (recv `{k:"this"}`) inside the lambda body, but inside the
        // SM `this` is the SM itself — so a body `this` must read the captured `__outer` field (`this.__outer`). A
        // lambda has no `this` of its own (its receiver, if any, rides a create()-set param field), so EVERY bare
        // `this` in the lambda body denotes the captured enclosing instance. Only synthesized SM-self nodes use the
        // `smSelf` marker, so they are unaffected. Absent an `__outer` capture there is nothing to redirect.
        JsonNode CapturedOuterField() =>
            (_isLambda && _fields.Contains("__outer"))
                ? FieldOf("__outer", FieldType("__outer")) : null;

        // GAP 2 — copy a `newSuspendLambda` verbatim (its body is the lambda's own scope, left for
        // SuspendLambdaLowering) and attach `capValues`: each capture's construction value resolved into THIS cold
        // SM's vocabulary. SuspendLambdaLowering builds `new <lambdaSM>(capValues..., null)` at this exact site.
        JsonObject RewriteSuspendLambdaNew(JsonObject o)
        {
            var copy = (JsonObject)o.DeepClone();
            var capValues = new JsonArray();
            if (o["captures"] is JsonArray caps)
                foreach (var c in caps.OfType<JsonObject>())
                    capValues.Add(CaptureValueInSm(Str(c["name"]), Str(c["type"])));
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
                if (_fields.Contains("__self")) return FieldOf("__self", FieldType("__self"));
                if (_isLambda && _fields.Contains("__outer")) return FieldOf("__outer", FieldType("__outer"));
                return new JsonObject { ["k"] = "this" };
            }
            if (_fields.Contains(name)) return FieldOf(name, FieldType(name));
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
        // are rewritten (spilling nested suspensions, redirecting locals/`this`).
        JsonObject ColdCall(JsonObject callNode, List<JsonNode> outp)
        {
            // GAP 1: a call to a suspend functional VALUE has no named cold entry — drive it through the stdlib
            // cold-invoke helper `startSuspendUninterceptedOrReturn(fn, [receiver,] completion)`.
            if (IsSuspendValueCall(callNode)) return SuspendValueColdCall(callNode, outp);

            var k = Str(callNode["k"]);
            var method = Str(callNode["method"]) + "$dotkt_suspend";
            var isInstance = k is "callInstance" or "clrInstance" or "clrGenericInstance";
            var isClr = k is "clrStatic" or "clrInstance" or "clrGenericStatic" or "clrGenericInstance";
            var isGeneric = k is "clrGenericStatic" or "clrGenericInstance";
            // Evaluate recv (instance) then args LEFT-TO-RIGHT, spilling any impure operand that precedes a later
            // suspending operand (BUG 2 — a nested suspension inside a suspend call's own argument list).
            var kids = new List<JsonNode>();
            if (isInstance) kids.Add(callNode["recv"]);
            if (callNode["args"] is JsonArray oa) foreach (var arg in oa) kids.Add(arg);
            var rw = RewriteEvalOrder(kids, outp);
            var ri = 0;
            var recvRw = isInstance ? rw[ri++] : null;
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
                if (isGeneric)
                {
                    // clrGeneric* resolves by (typeArgs, param SHAPES). Preserve typeArgs; append the completion's shape
                    // ("generic" — Continuation<Any> is a constructed generic type) so the cold entry's trailing
                    // completion param is matched instead of required to be optional.
                    if (callNode["typeArgs"] is JsonArray gta) clr["typeArgs"] = gta.DeepClone();
                    var shapes = (callNode["shapes"] as JsonArray)?.DeepClone() as JsonArray ?? new JsonArray();
                    shapes.Add("generic");
                    clr["shapes"] = shapes;
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
            }
            else
            {
                call = new JsonObject
                {
                    ["k"] = "callStatic",
                    ["owner"] = callNode["owner"]?.DeepClone(),
                    ["method"] = method,
                    ["args"] = args,
                    ["ret"] = Tw(AnyTn),
                };
            }
            if (callNode["typeArgs"] is JsonArray ta) call["typeArgs"] = ta.DeepClone();
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
            var sigArr = callNode["sig"] is JsonArray os ? (JsonArray)os.DeepClone() : new JsonArray();
            sigArr.Add(ContAny());
            call["sig"] = sigArr;
            return call;
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
            // Evaluate the value receiver then any invoke arg LEFT-TO-RIGHT (BUG 2 — a nested suspension in an arg).
            var kids = new List<JsonNode> { callNode["recv"] };
            if (callNode["args"] is JsonArray oa) foreach (var arg in oa) kids.Add(arg);
            var rw = RewriteEvalOrder(kids, outp);
            var recvRw = rw[0];
            var invokeArgs = rw.Skip(1).ToList();   // 0 / 1 / N (SuspendFunction0 / 1 / N)

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
                    ["owner"] = StartSuspendOwner,
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
                ["owner"] = StartSuspendOwner,
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
            if (!_baseIsLocal) invoke["clrOverride"] = BaseContinuationImplFqn;

            var methods = new JsonArray { invoke };
            foreach (var rm in _awaitResumeMethods) methods.Add(rm);

            var type = new JsonObject
            {
                ["name"] = _smType,
                ["kind"] = "class",
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
                        ["baseArgs"] = new JsonArray
                        {
                            new JsonObject { ["k"] = "local", ["name"] = "completion" },
                            NullConst(new TypeNode.Fqn("kotlin.coroutines.CoroutineContext")),
                        },
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
                foreach (var n in _smAllTps) tp.Add(n);
                type["typeParams"] = tp;
            }
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
            if (!_baseIsLocal) invoke["clrOverride"] = BaseContinuationImplFqn;

            var methods = new JsonArray { invoke };
            foreach (var cm in CreateMethods()) methods.Add(cm);
            foreach (var rm in _awaitResumeMethods) methods.Add(rm);

            var type = new JsonObject
            {
                ["name"] = _smType,
                ["kind"] = "class",
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
                        ["thisArgs"] = null,
                        ["vis"] = "public",
                        ["body"] = ctorBody,
                    },
                },
                ["methods"] = methods,
                ["properties"] = new JsonArray(),
                ["attrs"] = new JsonArray(),
            };
            if (_typeParams.Count > 0)
            {
                var tp = new JsonArray();
                foreach (var n in _typeParams) tp.Add(n);
                type["typeParams"] = tp;
            }
            return type;
        }

        // The create() override(s) — a fresh SM bound to a new completion, carrying THIS SM's captures.
        //   arity-0:  create(completion): Continuation            -> new SM(captures..., completion)
        //   arity-1:  create(value, completion): Continuation     -> sm = new SM(captures..., completion);
        //                                                             sm.<param> = value; return sm
        //   arity-N:  create(args: Array<Any?>, completion): Continuation
        //                 -> sm = new SM(captures..., completion); sm.<p0> = args[0]; …; sm.<pN-1> = args[N-1]; return sm
        // Matches BaseContinuationImpl.create's erased CLR ABI: params (Continuation<object>) / (object,
        // Continuation<object>) / (object[], Continuation<object>), return Continuation<object> (Unit-as-typearg
        // erases to object). ilemit binds the base slot by name + param types (clrOverride), so the param types MUST
        // match exactly.
        IEnumerable<JsonObject> CreateMethods()
        {
            if (_arity == 0)
            {
                yield return CreateMethod(
                    new JsonArray { new JsonObject { ["name"] = "completion", ["type"] = ContAny() } },
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
                    var paramType = TypeJson.Read(_params[i]["type"]) ?? AnyTn;
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
                        new JsonObject { ["name"] = "completion", ["type"] = ContAny() },
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
                var paramType = TypeJson.Read(_params[0]["type"]) ?? AnyTn;
                JsonNode storedValue = IsAnyTn(paramType)
                    ? new JsonObject { ["k"] = "local", ["name"] = "value" }
                    : new JsonObject { ["k"] = "cast", ["type"] = Tw(paramType), ["e"] = new JsonObject { ["k"] = "local", ["name"] = "value" } };
                yield return CreateMethod(
                    new JsonArray
                    {
                        new JsonObject { ["name"] = "value", ["type"] = Tw(AnyTn) },
                        new JsonObject { ["name"] = "completion", ["type"] = ContAny() },
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
            foreach (var (n, t) in _captures) { args.Add(FieldOf(n, t)); argTypes.Add(t); }
            args.Add(new JsonObject { ["k"] = "local", ["name"] = "completion" });
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
            if (!_baseIsLocal) m["clrOverride"] = BaseContinuationImplFqn;
            return m;
        }

        // object f$dotkt_suspend[<tp>](params..., completion) {
        //   val sm = new SM[<tp>]([this,] params..., completion); return sm.invokeSuspend(null) }
        JsonObject ColdEntrySm()
        {
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
                    ["type"] = Tw(_smTypeInst),
                    ["init"] = new JsonObject { ["k"] = "new", ["type"] = Tw(_smTypeInst), ["argTypes"] = argTypes, ["args"] = ctorArgs },
                },
                Ret(new JsonObject
                {
                    ["k"] = "callInstance",
                    ["ownerType"] = Tw(_smTypeInst),
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
            // valid. For a top-level fun the body has no `this`. Either way no rewrite is needed (no suspension).
            var cloned = (JsonArray)body.DeepClone();
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
                ["vis"] = "public",
                ["params"] = ps,
                ["ret"] = Tw(AnyTn),
                ["body"] = body,
                ["attrs"] = new JsonArray(),
            };
            if (_typeParams.Count > 0)
            {
                var tp = new JsonArray();
                foreach (var n in _typeParams) tp.Add(n);
                method["typeParams"] = tp;
            }
            return method;
        }

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
                ["vis"] = "public",
                ["params"] = ps,
                ["ret"] = Tw(AnyTn),
                ["body"] = new JsonArray(),
                ["attrs"] = new JsonArray(),
            };
            if (_typeParams.Count > 0)
            {
                var tp = new JsonArray();
                foreach (var n in _typeParams) tp.Add(n);
                method["typeParams"] = tp;
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
        // threadpool thread and completes the TCS, so main must BLOCK on `tcs.Task` until then (`Task.Wait()`) — with a
        // null completion the resume dereferenced null (NRE / lost result). When the Task-family aliases are absent (a
        // stdlib predating taskinterop) fall back to the old null-completion drive (correct for the synchronous case).
        JsonObject DrainMain()
        {
            var ps = new JsonArray();
            foreach (var p in _params) ps.Add(p.DeepClone());

            JsonArray body;
            if (_tcsBcl == null || _taskBcl == null)
            {
                // No Task aliases: the legacy null-completion drive (a synchronous main completes inline).
                var fwd = new JsonArray();
                foreach (var p in _params) fwd.Add(new JsonObject { ["k"] = "local", ["name"] = Str(p["name"]) });
                fwd.Add(NullConst(ContAnyTn));
                body = new JsonArray
                {
                    new JsonObject
                    {
                        ["k"] = "exprStmt",
                        ["expr"] = new JsonObject
                        {
                            ["k"] = "callStatic", ["owner"] = null, ["method"] = _coldName,
                            ["args"] = fwd, ["ret"] = Tw(AnyTn),
                        },
                    },
                };
            }
            else
            {
                // main returns Unit, so the root sink is typed over Unit.
                var tcsType = new TypeNode.Fqn(_tcsBcl, new[] { UnitTn });
                var taskType = new TypeNode.Fqn(_taskBcl, new[] { UnitTn });
                var rootType = new TypeNode.Fqn(RootContinuationFqn, new[] { UnitTn });

                var coldArgs = new JsonArray();
                foreach (var p in _params) coldArgs.Add(new JsonObject { ["k"] = "local", ["name"] = Str(p["name"]) });
                coldArgs.Add(new JsonObject { ["k"] = "cast", ["type"] = ContAny(), ["e"] = Local("__root") });

                body = new JsonArray
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
                            ["k"] = "newClr", ["type"] = Tw(rootType),
                            ["argTypes"] = new JsonArray { Tw(tcsType) }, ["args"] = new JsonArray { Local("__tcs") },
                        },
                    },
                    // r = main$dotkt_suspend(args..., (Continuation)root)   — a synchronous throw propagates RAW.
                    new JsonObject
                    {
                        ["k"] = "var", ["name"] = "__r", ["type"] = Tw(AnyTn),
                        ["init"] = new JsonObject
                        {
                            ["k"] = "callStatic", ["owner"] = null, ["method"] = _coldName,
                            ["args"] = coldArgs, ["ret"] = Tw(AnyTn),
                        },
                    },
                };
                // if (r !== COROUTINE_SUSPENDED) return;   else  tcs.Task.Wait();   (block for the async resume)
                var skipL = NextLabel();
                body.Add(BrIf(new JsonObject { ["k"] = "objEq", ["lhs"] = Local("__r"), ["rhs"] = Suspended() }, false, skipL));
                body.Add(new JsonObject
                {
                    ["k"] = "exprStmt",
                    ["expr"] = new JsonObject
                    {
                        ["k"] = "clrInstance", ["type"] = Tw(taskType), ["method"] = "Wait",
                        ["recv"] = new JsonObject
                        {
                            ["k"] = "clrPropGet", ["type"] = Tw(tcsType), ["name"] = "Task", ["static"] = false,
                            ["recv"] = Local("__tcs"), ["ret"] = Tw(taskType),
                        },
                        ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(), ["ret"] = Tw(VoidTn),
                    },
                });
                body.Add(Label(skipL));
            }

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
        //     catch (e: Throwable) { __tcs.TrySetException(e); __r = COROUTINE_SUSPENDED; }
        //     if (__r !== COROUTINE_SUSPENDED) __tcs.TrySetResult((R)__r);   // sync-completion fast path
        //     return __tcs.Task;
        //   }
        //
        // Sync/async completions are mutually exclusive by the coroutine contract: a non-SUSPENDED cold return means the
        // body completed inline (complete the TCS here); a SUSPENDED return means the eventual resume lands in
        // RootContinuation.resumeWith, which completes the TCS. A synchronous throw is caught and faults the TCS.
        // R = Unit/void is treated uniformly as kotlin.Unit (the cold entry returns null for a Unit body; `(Unit)null`
        // is null, matching what RootContinuation.resumeWith stores for the async Unit path — the two agree). The bridge
        // carries `suspendBridge:true` so ilemit stamps [KotlinFunction(Suspend)] (a re-consuming Kotlin sees `suspend fun`).
        JsonObject BuildBridge()
        {
            var isUnit = IsUnitTn(_resultType);
            var rKotlin = isUnit ? UnitTn : _resultType;
            // coroutine-abi.md §1: `suspend fun f(): Unit` -> a NON-generic public `Task` (the C#-idiomatic
            // async-void-returning-Task shape); `suspend fun f(): R` -> `Task<R>`. The internal drive stays generic
            // over Unit (TaskCompletionSource<Unit> / RootContinuation<Unit>); the returned `__tcs.Task` (a Task<Unit>)
            // upcasts to the non-generic Task on return (Task<T> : Task). So ONLY the PUBLIC return type differs for Unit.
            var taskType = new TypeNode.Fqn(_taskBcl, new[] { rKotlin });    // TaskCompletionSource<R>.Task runtime type
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
                    ["vis"] = "public",
                    ["params"] = aps,
                    ["ret"] = Tw(taskRetType),
                    ["body"] = new JsonArray(),
                    ["attrs"] = new JsonArray(),
                };
                if (_typeParams.Count > 0)
                {
                    var tp = new JsonArray();
                    foreach (var n in _typeParams) tp.Add(n);
                    am["typeParams"] = tp;
                }
                if (TaskReturnNullableFlags() is JsonArray arnf) am["retNullableFlags"] = arnf;
                return am;
            }

            var tcsType = new TypeNode.Fqn(_tcsBcl, new[] { rKotlin });
            var rootType = new TypeNode.Fqn(RootContinuationFqn, new[] { rKotlin });

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
                        ["k"] = "newClr", ["type"] = Tw(rootType),
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
                                new JsonObject { ["k"] = "exprStmt", ["expr"] = TcsCall(tcsType, "TrySetException", new TypeNode.Fqn("kotlin.Throwable"), Local("__e")) },
                                new JsonObject { ["k"] = "setLocal", ["name"] = "__r", ["value"] = Suspended() },
                            },
                        },
                    },
                },
            };

            var skipL = NextLabel();
            body.Add(BrIf(new JsonObject { ["k"] = "objEq", ["lhs"] = Local("__r"), ["rhs"] = Suspended() }, true, skipL));
            JsonNode resultVal = IsAnyTn(rKotlin)
                ? Local("__r")
                : new JsonObject { ["k"] = "cast", ["type"] = Tw(rKotlin), ["e"] = Local("__r") };
            body.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = TcsCall(tcsType, "TrySetResult", rKotlin, resultVal) });
            body.Add(Label(skipL));
            JsonNode tcsTask = new JsonObject
            {
                ["k"] = "clrPropGet", ["type"] = Tw(tcsType), ["name"] = "Task", ["static"] = false,
                ["recv"] = Local("__tcs"), ["ret"] = Tw(taskType),
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
                ["vis"] = "public",
                ["params"] = ps,
                ["ret"] = Tw(taskRetType),
                ["body"] = body,
                ["attrs"] = new JsonArray(),
            };
            if (_typeParams.Count > 0)
            {
                var tp = new JsonArray();
                foreach (var n in _typeParams) tp.Add(n);
                method["typeParams"] = tp;
            }
            // BUG 2 (nested return nullability): a `suspend fun f(): String?`'s bridge return `Task<string?>` needs the
            // inner `?` — the scalar retNullable can't express a nullability that rides an INNER type arg. Emit the
            // flattened NullableAttribute byte walk (ilemit stamps it verbatim on the return; facadegen reads it back).
            if (TaskReturnNullableFlags() is JsonArray rnf) method["retNullableFlags"] = rnf;
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
                    ["args"] = args,
                    ["ret"] = Tw(AnyTn),
                };
            else
                call = new JsonObject
                {
                    ["k"] = "callStatic",
                    ["owner"] = null,
                    ["method"] = _coldName,
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
            return call;
        }

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
            ["owner"] = IntrinsicsKtFqn,
            ["method"] = "get_COROUTINE_SUSPENDED",
            ["args"] = new JsonArray(),
            ["ret"] = Tw(AnyTn),
        };

        static JsonObject ThrowOnFailure() => new()
        {
            ["k"] = "callStatic",
            ["owner"] = ThrowOnFailureOwner,
            ["method"] = "throwOnFailure",
            ["args"] = new JsonArray { new JsonObject { ["k"] = "local", ["name"] = "result" } },
            ["ret"] = Tw(VoidTn),
        };

        static JsonObject Ret(JsonNode value) => new() { ["k"] = "return", ["value"] = value };
        static JsonObject IntConst(int v) => new() { ["k"] = "const", ["type"] = TypeJson.Write(IntTn), ["value"] = v };
        static JsonObject BoolConst(bool v) => new() { ["k"] = "const", ["type"] = TypeJson.Write(BoolTn), ["value"] = v };
        static JsonObject NullConst(TypeNode type) => new() { ["k"] = "const", ["type"] = TypeJson.Write(type), ["value"] = null };
        static JsonObject Label(int id) => new() { ["k"] = "label", ["id"] = id };
        static JsonObject Goto(int id) => new() { ["k"] = "goto", ["id"] = id };
        static JsonObject BrIf(JsonNode cond, bool on, int id) => new()
            { ["k"] = "brIf", ["cond"] = cond, ["on"] = on, ["id"] = id };
        static JsonObject BinEq(JsonNode l, JsonNode r) => new()
            { ["k"] = "binOp", ["op"] = "==", ["lhs"] = l, ["rhs"] = r };
    }
}
