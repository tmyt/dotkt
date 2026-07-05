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

static class SuspendColdLowering
{
    const string ContinuationImplFqn = "kotlin.coroutines.clr.internal.ContinuationImpl";
    const string SuspendLambdaFqn = "kotlin.coroutines.clr.internal.SuspendLambda";
    // A @RestrictsSuspension-scope (e.g. SequenceScope) suspend lambda's SM base. Same 2-arg (arity, completion)
    // ctor + create() protocol as SuspendLambda; RestrictedContinuationImpl pins EmptyCoroutineContext.
    const string RestrictedSuspendLambdaFqn = "kotlin.coroutines.clr.internal.RestrictedSuspendLambda";
    const string BaseContinuationImplFqn = "kotlin.coroutines.clr.internal.BaseContinuationImpl";
    const string ContinuationOfAny = "kotlin.coroutines.Continuation[kotlin.Any]";
    // BaseContinuationImpl.create returns Continuation<Unit> (ContinuationImpl.kt:82/87); a CLR virtual
    // override needs an EXACT return-type match (no covariance), so the SM's create() must return this,
    // NOT Continuation<Any>. The returned SM value converts by Continuation's `in T` contravariance.
    const string ContinuationOfUnit = "kotlin.coroutines.Continuation[kotlin.Unit]";
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
        "closureNew", "delegateNew", "lambda", "forEachInline", "repeatInline",
        // Part B: a suspend-lambda VALUE inside a suspend fun disqualifies the enclosing fun from cold
        // transform (its own SM is built separately by SuspendLambdaLowering, which runs after).
        "suspendLambdaNew",
    };

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();
    static string NonEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
    static bool Bool(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    // The inline `suspendCoroutineUninterceptedOrReturn { c -> … }` intrinsic marker. kotc's IR inliner has
    // already run when this reaches bir2cir: the `@InlineOnly inline` intrinsic's fake body (a bare
    // `throw NotImplementedError("Implementation of suspendCoroutineUninterceptedOrReturn is intrinsic")`)
    // survives as the `valueBlock` RESULT, and its `{ c -> … }` block is materialized as a separate closure
    // class captured into a dead `var __inlN` in the block's stmts. We recognize such a block here and lower it
    // to a real cold suspension point — inlining the closure's invoke body, binding `c` to the SM. This is kotc's
    // live emitSuspendIntrinsic/coSelfCont (BirEmitter.kt:1669-1688) re-expressed over the cold SM.
    //
    // DETECTION (see IsSuspendIntrinsicBlock — the SINGLE recognizer; every call routes through it):
    //  (1) PREFERRED — a stable `suspendIntrinsic:true` flag on the valueBlock. kotc does NOT emit this tag today;
    //      it SHOULD (a one-line marker on the lowered intrinsic block would retire the fragile string sniff). The
    //      recognizer already reads it, so the day kotc stamps it the string path below becomes dead weight.
    //  (2) FALLBACK — the intrinsic's fake `throw <exc>(<SuspendIntrinsicMarker>)` result string. Fragile (couples to
    //      a stdlib message), but the ONLY marker available until kotc emits (1): the frontend never invokes `block`,
    //      so no `suspendCall` tag exists on the intrinsic call itself. The fake body is the inliner's residue of
    //      `libraries/stdlib/src/kotlin/coroutines/intrinsics/Intrinsics.kt:43`
    //      (`throw NotImplementedError("Implementation of suspendCoroutineUninterceptedOrReturn is intrinsic")`).
    //      HARDENED: we no longer couple to the exact thrown TYPE NAME (`kotlin.NotImplementedError`) — an earlier
    //      exception-alias/substitution pass (kotc's exception map, or a future @ClrTypeAlias) could rewrite it to
    //      `System.NotImplementedException` and silently break the match. The marker STRING is globally unique
    //      (no user code throws it), so the `throwExpr`+`new`+marker-const shape alone is a safe discriminator.
    const string SuspendIntrinsicMarker = "suspendCoroutineUninterceptedOrReturn is intrinsic";
    const string SuspendIntrinsicFlag = "suspendIntrinsic";

    // Is this a `valueBlock` that is the lowered `suspendCoroutineUninterceptedOrReturn` intrinsic? Such a block
    // IS a suspension point (its embedded closure is the suspension body), NOT an ordinary lambda value. The
    // SINGLE, centralized recognizer — prefers the stable flag (1), falls back to the (type-name-independent)
    // message string (2).
    static bool IsSuspendIntrinsicBlock(JsonNode node)
    {
        if (node is not JsonObject o || Str(o["k"]) != "valueBlock") return false;
        if (Bool(o[SuspendIntrinsicFlag])) return true;                                 // (1) stable tag
        if (o["result"] is not JsonObject res || Str(res["k"]) != "throwExpr") return false;
        if (res["value"] is not JsonObject nw || Str(nw["k"]) != "new") return false;   // any exc type (alias-safe)
        return nw["args"] is JsonArray a && a.Count >= 1 && a[0] is JsonObject c0     // (2) message-string fallback
            && Str(c0["k"]) == "const" && (Str(c0["value"])?.Contains(SuspendIntrinsicMarker) ?? false);
    }

    // F2 — the CROSS-MODULE suspendCoroutine shape. Our compiler does NOT inline @InlineOnly cross-module,
    // so an APP calling `suspendCoroutine { … }` / `suspendCoroutineUninterceptedOrReturn { … }` does NOT get
    // the same-module `valueBlock`+`NotImplementedError` intrinsic block above — instead kotc emits a plain
    // `callStatic <name>(<closureNew|delegateNew>) suspendCall:true`, owner:null (top-level intrinsic), the
    // block materialized as a closure class (capturing) or a top-level `__lambdaN` (non-capturing). This IS a
    // suspension point — recognized here, lowered by EmitSuspendCoroutineCall (which reconstructs the wrapper's
    // SafeContinuation body / the unintercepted block, since the un-inlined wrapper body is unavailable).
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
        var owner = Str(o["owner"]);
        if (owner != null && owner != expectOwner) return false;
        return (o["args"] as JsonArray)?.FirstOrDefault() is JsonObject a
            && Str(a["k"]) is "closureNew" or "delegateNew";
    }

    // The `closureNew` (the `{ c -> … }` block) buried in an intrinsic block's stmts (a `var __inlN` init).
    static JsonObject IntrinsicClosureNew(JsonObject block)
    {
        if (block["stmts"] is JsonArray a)
            foreach (var s in a)
                if (s is JsonObject so && so["init"] is JsonObject init && Str(init["k"]) == "closureNew")
                    return init;
        return null;
    }

    // A suspend fun's identity: Owner=null for a top-level file-class static, else the enclosing class FQN. Sig
    // is the joined param-type list — it discriminates OVERLOADS that share (Owner, Name) (e.g. SequenceScope
    // has three `yieldAll` overloads differing only by param type: Iterator/Iterable/Sequence). Without it the
    // registry would collapse the overloads to one, dropping the others (see SigOf).
    readonly record struct FunKey(string Owner, string Name, string Sig);

    // The param-type signature discriminating overloaded suspend members (see FunKey.Sig).
    static string SigOf(JsonObject m) =>
        m["params"] is JsonArray ps ? string.Join(",", ps.OfType<JsonObject>().Select(p => Str(p["type"]) ?? "")) : "";

    // A shape-eligible suspend fun + where it lives (for cold-entry/SM splicing).
    sealed record Entry(JsonObject Method, JsonObject Root, JsonObject TypeNode, string Owner, string FileClass);

    // A suspend CALL site descriptor (for the resolvability fixpoint).
    readonly record struct CallRef(bool Instance, string Owner, string Name);

    // Returns the callee-return-type map (cold-entry name -> Kotlin resultType), so the SEPARATE
    // SuspendLambdaLowering phase can type a suspend-lambda's awaited value the SAME way (else a
    // lambda's `h()` await falls back to kotlin.Any and the value is never unboxed -> `object + int`).
    public static IReadOnlyDictionary<string, string> ApplyAll(IReadOnlyList<JsonNode> roots, ReferenceMetadataIndex refs, IReadOnlySet<string> localTypeFqns)
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
        var calleeRet = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, e) in entries)
            calleeRet[k.Name] = Str(e.Method["resultType"]) ?? "kotlin.Any";

        // A global (owner#name -> resultType) index of EVERY method (not just suspend). The eval-order spill
        // (BUG 2 fix, Rewrite/RewriteEvalOrder) uses it to type a temp SM field holding a left-of-suspension
        // operand whose own node carries no return type (a same-assembly `callStatic side()` has no `ret`).
        // Top-level -> "#name"; member -> "owner#name".
        var methodRets = new Dictionary<string, string>(StringComparer.Ordinal);
        // A global (owner#name -> declared type) index of EVERY member/static FIELD. The eval-order spill (BUG 2 fix,
        // N4) needs it to type the temp SM field that holds a raw `field`/`staticField`/`lateinitGet` read spilled to
        // the LEFT of a suspension — a raw field read node carries no `retType` (kotc emits only ownerType+name), so
        // without this the temp would fall back to kotlin.Any and box a value-type field, breaking the enclosing bin.
        var fieldTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in roots)
        {
            if (r is not JsonObject file) continue;
            if (file["methods"] is JsonArray fms)
                foreach (var m in fms)
                    if (m is JsonObject mo && Str(mo["name"]) is string mn)
                        methodRets["#" + mn] = Str(mo["resultType"]) ?? Str(mo["ret"]) ?? "kotlin.Any";
            if (file["fields"] is JsonArray ffs)
                foreach (var f in ffs)
                    if (f is JsonObject fo && Str(fo["name"]) is string fn && Str(fo["type"]) is string ft0)
                        fieldTypes["#" + fn] = ft0;
            if (file["types"] is JsonArray fts2)
                foreach (var t in fts2)
                    if (t is JsonObject to && Str(to["name"]) is string ow)
                    {
                        if (to["methods"] is JsonArray tms)
                            foreach (var m in tms)
                                if (m is JsonObject mo && Str(mo["name"]) is string mn)
                                    methodRets[ow + "#" + mn] = Str(mo["resultType"]) ?? Str(mo["ret"]) ?? "kotlin.Any";
                        if (to["fields"] is JsonArray tfs)
                            foreach (var f in tfs)
                                if (f is JsonObject fo && Str(fo["name"]) is string fn && Str(fo["type"]) is string ft1)
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
            // Per-file registry of top-level `__lambdaN` methods (the non-capturing `delegateNew` block bodies of a
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
            var t = Str(p["type"]) ?? "";
            var br = t.IndexOf('['); if (br >= 0) t = t.Substring(0, br);         // strip generic args
            var dot = t.LastIndexOf('.'); if (dot >= 0) t = t.Substring(dot + 1); // simple name
            t = new string(t.Where(char.IsLetterOrDigit).ToArray());             // sanitize to an identifier
            if (t.Length > 0) parts.Add(t);
        }
        return string.Join("", parts);
    }

    // Part B entry: build a suspend-LAMBDA state-machine TYPE from a suspendLambdaNew node's parts (used by
    // SuspendLambdaLowering). Returns null for arity >= 2 — the create()/invoke protocol covers arities 0/1
    // only (JVM parity), so a wider lambda is not expressible v1 and the caller keeps the node (reports it).
    public static JsonObject BuildLambdaSm(string smName, int arity,
        List<(string name, string type)> captures, List<JsonObject> lambdaParams, JsonArray body,
        string resultType, List<string> typeParams, bool baseIsLocal,
        IReadOnlyDictionary<string, string> calleeRet = null, bool restricted = false)
    {
        if (arity is < 0 or > 1) return null;
        var gen = new FunGen(smName, arity, captures ?? new List<(string, string)>(), lambdaParams, body,
            resultType, typeParams,
            calleeRet as Dictionary<string, string> ??
                (calleeRet != null ? new Dictionary<string, string>(calleeRet, StringComparer.Ordinal)
                                   : new Dictionary<string, string>(StringComparer.Ordinal)),
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
        if (!Bool(m["suspend"])) return false;
        if (!Bool(m["static"])) return false;                       // top-level statics + extensions (kotc: __self param)
        if (Bool(m["inline"]) || Bool(m["abstract"])) return false;
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
        if (!Bool(m["suspend"])) return false;
        if (Bool(m["static"])) return false;                        // a static member fun -> deferred
        if (Bool(m["inline"])) return false;
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
                // The inline `suspendCoroutineUninterceptedOrReturn { c -> … }` intrinsic (a valueBlock whose
                // result is the fake NotImplementedError throw) IS a supported cold suspension point — do NOT
                // descend into its embedded closureNew (which would trip the LambdaKinds refusal below).
                if (IsSuspendIntrinsicBlock(o)) return true;
                // F2 — a cross-module suspendCoroutine call IS a supported cold suspension point; do NOT descend
                // into its embedded closureNew/delegateNew block arg (which would trip the LambdaKinds refusal).
                if (IsSuspendCoroutineCall(o)) return true;
                // ANY OTHER lambda/closure/sequence node -> unsupported (genuine suspend lambdas, which emit a
                // `closureNew` and are NOT flagged `suspendCall`, are handled separately by SuspendLambdaLowering).
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
                if (Bool(o["suspendCall"]) && Str(o["method"]) is string mn)
                {
                    var k = Str(o["k"]);
                    if (k == "callInstance")
                        seen.Add(new CallRef(true, BareOwner(Str(o["ownerType"])), mn));
                    else if (k == "callStatic")
                        seen.Add(new CallRef(false, BareOwner(Str(o["owner"])), mn));
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
                if (o.ContainsKey("suspendCall") && Bool(o["suspendCall"])) return true;
                if (IsSuspendIntrinsicBlock(o)) return true;   // the inline suspendCoroutine intrinsic IS a suspension
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
        readonly Dictionary<string, string> _calleeRet;
        readonly bool _baseIsLocal;
        // The public Task<R> bridge BCL owners (from the ref.dll @ClrTypeAlias index); null -> no bridge (see ApplyAll).
        readonly string _tcsBcl;
        readonly string _taskBcl;
        readonly List<string> _ownerTypeParams;   // enclosing class type-param names (instance member on a generic class)
        readonly List<string> _smAllTps;           // owner + method type-param names (the SM's own generic params)
        readonly string _selfType;                 // constructed self `Box[gp:T]` (instance member), else _ownerClass/null
        readonly bool _memberAbstract;             // source member is `abstract` -> abstract cold entry, no SM
        readonly bool _memberOverride;             // source member is `override` -> override cold entry (fills base slot)
        readonly bool _memberVirtual;              // source member is `open` -> virtual cold entry (new vtable slot)
        // Closure-class registry (name -> type node) for the suspendCoroutine intrinsic inliner. Empty in lambda mode.
        readonly IReadOnlyDictionary<string, JsonObject> _closures;
        // Top-level `__lambdaN` method registry (name -> method) for a cross-module suspendCoroutine's non-capturing
        // `delegateNew` block body (F2). Empty in lambda mode.
        readonly IReadOnlyDictionary<string, JsonObject> _lambdaMethods;
        // Lambda mode (bundle-6 P3 wave-2b Part B): a suspend LAMBDA SM (extends SuspendLambda, no cold
        // entry/main-drain, adds the create() override protocol). Left at defaults for the named-fun path.
        readonly bool _isLambda;
        readonly bool _restrictedBase;           // lambda mode: receiver is @RestrictsSuspension -> RestrictedSuspendLambda base
        readonly int _arity;                     // the lambda's own param count (v1: 0 or 1)
        readonly List<(string name, string type)> _captures;   // captured vars -> ctor params + fields
        readonly JsonArray _lambdaBody;          // the lambda's structured body (no `_m` in lambda mode)
        readonly string _smType;                 // bare SM type name
        readonly string _smTypeInst;             // instantiated (`f$sm[gp:T]`) or bare when non-generic
        readonly string _coldName;
        readonly string _resultType;             // Kotlin resultType token ("void" for Unit)
        readonly List<JsonObject> _params;       // original params (extension: leading __self)
        readonly List<string> _typeParams;       // generic type-param names ([] when non-generic)
        readonly HashSet<string> _fields = new(StringComparer.Ordinal);
        readonly List<(string name, string type)> _fieldDecls = new();
        // Synthesized SM methods for each `task.await()` suspension point (the OnCompleted Action callback
        // that re-drives THIS SM via resumeWith). Populated during body emission; spliced into the SM type.
        readonly List<JsonObject> _awaitResumeMethods = new();

        // Global (owner#name -> resultType) index for typing eval-order spill fields (BUG 2). Empty in lambda mode.
        readonly Dictionary<string, string> _methodRets;
        readonly Dictionary<string, string> _fieldTypes;   // (owner#field / #field) -> declared type, for eval-order spill typing (N4)

        int _state;                              // resume-state counter (>=1)
        int _label;                              // label id allocator (above kotc's low ids)
        int _condCounter;
        int _ordCounter;                         // eval-order spill temp-field counter (BUG 2)
        bool _needSuspendGuard;                  // fun has a suspending try/finally -> emit the $suspending gate (BUG 1)
        readonly List<(int state, int label)> _dispatch = new();
        readonly Stack<(List<(int state, int label)> inner, int tryEntry)> _tryStack = new();

        public FunGen(JsonObject m, string name, string fileClass, string ownerClass,
            Dictionary<string, string> calleeRet, bool baseIsLocal, string tcsBcl = null, string taskBcl = null,
            List<string> ownerTypeParams = null, IReadOnlyDictionary<string, JsonObject> closures = null,
            string smNameSuffix = "", Dictionary<string, string> methodRets = null,
            Dictionary<string, string> fieldTypes = null,
            IReadOnlyDictionary<string, JsonObject> lambdaMethods = null,
            bool ownerIsInterface = false)
        {
            _m = m; _name = name; _fileClass = fileClass; _ownerClass = ownerClass;
            _isMember = ownerClass != null;
            _calleeRet = calleeRet; _baseIsLocal = baseIsLocal;
            _methodRets = methodRets ?? new Dictionary<string, string>(StringComparer.Ordinal);
            _fieldTypes = fieldTypes ?? new Dictionary<string, string>(StringComparer.Ordinal);
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
            _resultType = Str(m["resultType"]) ?? "void";
            _params = (m["params"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            _typeParams = ReadTypeParamNames(m["typeParams"]);
            // The SM is generic over the ENCLOSING class's type params (an instance member on a generic class) PLUS
            // the member's own — its fields / `$this` / label reference them. `_selfType` is the constructed self.
            _smAllTps = new List<string>(_ownerTypeParams);
            foreach (var t in _typeParams) if (!_smAllTps.Contains(t)) _smAllTps.Add(t);
            _selfType = _ownerTypeParams.Count == 0 ? _ownerClass
                : _ownerClass + "[" + string.Join(",", _ownerTypeParams.Select(t => "gp:" + t)) + "]";
            _smTypeInst = _smAllTps.Count == 0
                ? _smType
                : _smType + "[" + string.Join(",", _smAllTps.Select(t => "gp:" + t)) + "]";
        }

        // Lambda-mode ctor (Part B). Builds a `<smName> : SuspendLambda` SM from a suspendLambdaNew node's
        // parts. Captures become ctor params + fields; the lambda's own params become fields set by create().
        public FunGen(string smName, int arity, List<(string name, string type)> captures,
            List<JsonObject> lambdaParams, JsonArray body, string resultType, List<string> typeParams,
            Dictionary<string, string> calleeRet, bool baseIsLocal, bool restricted = false)
        {
            _isLambda = true;
            _restrictedBase = restricted;
            _methodRets = new Dictionary<string, string>(StringComparer.Ordinal);
            _fieldTypes = new Dictionary<string, string>(StringComparer.Ordinal);
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
            _resultType = string.IsNullOrEmpty(resultType) ? "void" : resultType;
            _params = lambdaParams ?? new List<JsonObject>();
            _typeParams = typeParams ?? new List<string>();
            _smAllTps = _typeParams;   // a lambda SM has no enclosing-class type params
            _smTypeInst = _typeParams.Count == 0
                ? _smType
                : _smType + "[" + string.Join(",", _typeParams.Select(t => "gp:" + t)) + "]";
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

            AddField("label", "kotlin.Int");
            if (_needSuspendGuard) AddField(SuspendingField, "kotlin.Boolean");   // BUG 1: the finally gate flag
            if (_isMember) AddField(ThisField, _selfType);          // holds the enclosing (constructed) instance
            if (_isLambda)
                foreach (var (n, t) in _captures) AddField(n, t);   // captured vars -> ctor-set fields
            foreach (var p in _params)
                AddField(Str(p["name"]), Str(p["type"]));           // lambda: create()-set param field(s)
            CollectVarFields(body, inHandler: false);

            var bodyOut = new List<JsonNode>();
            foreach (var s in body) EmitStmt(s, bodyOut);
            if (_resultType is "void" or "kotlin.Unit")
                bodyOut.Add(Ret(NullConst("kotlin.Any")));

            var invoke = new JsonArray();
            // BUG 1: reset the finally gate at every entry (first call + each resume) BEFORE the label dispatch,
            // so a finally reached on the normal/exception path runs its real body; the suspend-return path sets
            // it true just before returning SUSPENDED (see the EmitSuspensionPoint/EmitAwaitPoint sites).
            if (_needSuspendGuard) invoke.Add(SetField(SuspendingField, BoolConst(false)));
            foreach (var (state, label) in _dispatch)
                invoke.Add(BrIf(BinEq(FieldOf("label", "kotlin.Int"), IntConst(state)), true, label));
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
        //    primitives being DEFINED here, not external .NET refs — a bridge would `clrNew` a local type as if it
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

        void AddField(string name, string type)
        {
            if (name == null || !_fields.Add(name)) return;
            _fieldDecls.Add((name, type ?? "kotlin.Any"));
        }

        void AddFieldTyped(string name, string type)
        {
            if (_fields.Add(name)) _fieldDecls.Add((name, type));
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
                    if (IsSuspendIntrinsicBlock(o)) return;
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
            var assigned = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            string Assign(string name, string type)
            {
                if (!assigned.TryGetValue(name, out var byType))
                    assigned[name] = byType = new Dictionary<string, string>(StringComparer.Ordinal);
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
                if (IsSuspendIntrinsicBlock(o)) return;             // nested suspension owns its own scope
                if (k != null && LambdaKinds.Contains(k)) return;   // nested lambda/closure -> separate SM
                switch (k)
                {
                    case "var":
                        if (o["init"] != null) Visit(o["init"]);    // init binds in the OUTER scope (before decl)
                        if (Str(o["name"]) is string vn && conflicts.Contains(vn))
                        {
                            var nn = Assign(vn, Str(o["type"]) ?? "kotlin.Any");
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
                    // The intrinsic block's `var __inlN` (the dead materialized closure) is inlined away, not
                    // spilled — do NOT collect it as an SM field.
                    if (IsSuspendIntrinsicBlock(o)) return;
                    if (k != null && LambdaKinds.Contains(k)) return;
                    if (k == "var" && !inHandler)
                        AddField(Str(o["name"]), Str(o["type"]));
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
                    var val = init == null ? NullConst(Str(o["type"]) ?? "kotlin.Any") : Rewrite(init, outp);
                    if (_fields.Contains(nm)) outp.Add(SetField(nm, val));
                    else outp.Add(new JsonObject { ["k"] = "var", ["name"] = nm, ["type"] = Str(o["type"]), ["init"] = val });
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
                    outp.Add(v == null ? Ret(NullConst("kotlin.Any")) : Ret(Rewrite(v, outp)));
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
                body2.Add(BrIf(BinEq(FieldOf("label", "kotlin.Int"), IntConst(state)), true, label));
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
                            ["excType"] = Str(co["excType"]),
                            ["var"] = Str(co["var"]),
                            ["body"] = cbodyArr,
                        });
                    }

            var tryNode = new JsonObject
            {
                ["k"] = "try",
                ["type"] = Str(o["type"]),
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
                    finArr.Add(BrIf(FieldOf(SuspendingField, "kotlin.Boolean"), true, skipL));
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
                        ? NullConst(Str(o["type"]) ?? "kotlin.Any") : RewriteNoSpill(o["init"]));
                if (_isMember && Str(o["k"]) == "this")
                    return FieldOf(ThisField, _ownerClass);
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
                // `smSelf`: the state-machine's OWN identity (`this` in invokeSuspend). Introduced by the intrinsic
                // inliner for the `c`/continuation binding — it must survive the `this`->`$this` member rewrite
                // (a captured `this` means the ENCLOSING receiver -> $this; `c` means the SM itself).
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
                        ? NullConst(Str(o["type"]) ?? "kotlin.Any") : Rewrite(o["init"], outp));
                if (_isMember && k == "this")
                    return FieldOf(ThisField, _ownerClass);
                if (IsSuspendIntrinsicBlock(o))
                    return EmitIntrinsicSuspension(o, outp);
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
                    return o["result"] != null ? Rewrite(o["result"], outp) : NullConst(Str(o["type"]) ?? "kotlin.Any");
                }
                if ((k == "callStatic" || k == "callInstance") && Bool(o["suspendCall"]))
                    return EmitSuspensionPoint(o, outp);
                if ((k == "clrStatic" || k == "clrGenericStatic") && Bool(o["suspendCall"])
                    && Str(o["type"]) == AwaitMarkerOwner && Str(o["method"]) == "await")
                    return EmitAwaitPoint(o, outp);
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
                    if (k == "bin")
                    {
                        var rw = RewriteEvalOrder(new List<JsonNode> { o["l"], o["r"] }, outp);
                        var binCopy = new JsonObject();
                        foreach (var kv in o) binCopy[kv.Key] = kv.Value?.DeepClone();
                        binCopy["l"] = rw[0];
                        binCopy["r"] = rw[1];
                        return binCopy;
                    }
                    if (k is "callStatic" or "callInstance" or "clrStatic" or "clrInstance"
                        or "clrGenericStatic" or "new" or "clrNew")
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
            "new", "clrNew", "setLocal", "setField", "throwExpr", "dynCall",
            "field", "staticField", "lateinitGet",
            "arrayGet", "clr.ldelem",
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
        string TypeOfExpr(JsonNode n)
        {
            if (n is not JsonObject o) return "kotlin.Any";
            if (NonEmpty(Str(o["retType"])) is string t0) return t0;
            if (NonEmpty(Str(o["ret"])) is string t1) return t1;
            if (NonEmpty(Str(o["dynRet"])) is string t2) return t2;
            var k = Str(o["k"]);
            switch (k)
            {
                case "const": case "cast": case "new": case "clrNew": case "valueBlock": case "var":
                    if (NonEmpty(Str(o["type"])) is string t) return t;
                    break;
                case "callStatic":
                {
                    var name = Str(o["method"]);
                    var owner = Str(o["owner"]);
                    var key = (owner == null ? "#" : BareOwner(owner) + "#") + name;
                    if (_methodRets.TryGetValue(key, out var rt)) return rt;
                    if (_methodRets.TryGetValue("#" + name, out var rt2)) return rt2;
                    break;
                }
                case "callInstance":
                {
                    var ot = BareOwner(Str(o["ownerType"]));
                    if (ot != null && _methodRets.TryGetValue(ot + "#" + Str(o["method"]), out var rt)) return rt;
                    break;
                }
                case "field": case "staticField": case "lateinitGet":
                {
                    // N4 eval-order spill: type the temp SM field from the field's declared type (a raw field read
                    // carries no `retType`). Owner-qualified first (`owner#name`), then top-level (`#name`).
                    var fname = Str(o["name"]);
                    var fowner = BareOwner(Str(o["ownerType"]));
                    if (fowner != null && _fieldTypes.TryGetValue(fowner + "#" + fname, out var fft)) return fft;
                    if (_fieldTypes.TryGetValue("#" + fname, out var fft2)) return fft2;
                    break;
                }
                case "arrayGet": case "clr.ldelem":
                    // N4-sibling eval-order spill: an array-element read carries its element type verbatim on `elem`,
                    // so the temp SM field is typed precisely (avoids a kotlin.Any box of a value-type element).
                    if (NonEmpty(Str(o["elem"])) is string et) return et;
                    break;
                case "bin":
                    return Str(o["op"]) is "==" or "!=" or "<" or ">" or "<=" or ">=" ? "kotlin.Boolean" : TypeOfExpr(o["l"]);
            }
            return "kotlin.Any";
        }

        JsonNode EmitCondValue(JsonObject c, List<JsonNode> outp)
        {
            var ty = Str(c["type"]) ?? "kotlin.Any";
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
            var retTok = NonEmpty(Str(callNode["retType"]))
                ?? NonEmpty(Str(callNode["dynRet"]))
                // A CROSS-ASSEMBLY suspend call arrives in the `clr*` vocabulary, whose declared return type rides `ret`
                // (not `retType`/`sig`) and is absent from _calleeRet (a same-assembly-only map). Read it so the awaited
                // value gets its real type (+ unbox/castclass) instead of falling to kotlin.Any.
                ?? NonEmpty(Str(callNode["ret"]))
                ?? (_calleeRet.TryGetValue(Str(callNode["method"]) ?? "", out var d) ? d : null)
                ?? NonEmpty(Str(callNode["sig"]))
                ?? "kotlin.Any";
            if (retTok is "void" or "kotlin.Unit") retTok = "kotlin.Any";
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
                ["l"] = new JsonObject { ["k"] = "local", ["name"] = "result" },
                ["r"] = Suspended(),
            }, false, resumeLabel));
            if (_needSuspendGuard) outp.Add(SetField(SuspendingField, BoolConst(true)));   // BUG 1: mark the suspend-return
            outp.Add(Ret(Suspended()));
            outp.Add(Label(resumeLabel));
            outp.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = ThrowOnFailure() });
            outp.Add(SetField(field, retTok == "kotlin.Any"
                ? new JsonObject { ["k"] = "local", ["name"] = "result" }
                : new JsonObject { ["k"] = "cast", ["type"] = retTok, ["e"] = new JsonObject { ["k"] = "local", ["name"] = "result" } }));
            return FieldOf(field, retTok);
        }

        // The inline `suspendCoroutineUninterceptedOrReturn { c -> stmts; tail }` intrinsic, lowered to a real cold
        // suspension point (kotc's emitSuspendIntrinsic/coSelfCont over the cold SM — BirEmitter.kt:1669-1688):
        //   <inlined closure stmts, with `c`->this(SM) and captures->fields>
        //   this.label = state
        //   result = <tail>                          // the block's return value (e.g. COROUTINE_SUSPENDED)
        //   if (result !== COROUTINE_SUSPENDED) goto resumeLabel      // synchronous fast path
        //   return COROUTINE_SUSPENDED
        //   resumeLabel: throwOnFailure(result); __aw = (T)result     // resumed value (or the sync tail)
        // The block receives THIS coroutine's own continuation as `c` — and the SM IS a Continuation, so `c` binds
        // to the SM itself (`smSelf`, surviving the this->$this member rewrite). Captured `this` means the ENCLOSING
        // receiver, so it rewrites to `$this`. For `yield` the tail is a literal COROUTINE_SUSPENDED and the result
        // is Unit (discarded); the general form supports a synchronous value too.
        JsonNode EmitIntrinsicSuspension(JsonObject block, List<JsonNode> outp)
        {
            var closureNew = IntrinsicClosureNew(block);
            var closureType = Str(closureNew?["closureType"]);
            if (closureType == null || !_closures.TryGetValue(closureType, out var closureCls))
            {
                // Failure posture (LOUD): the suspendCoroutineUninterceptedOrReturn body is UNRESOLVABLE — its
                // `{ c -> … }` closure class was not found in the compilation. Emitting a bare unconditional
                // `return COROUTINE_SUSPENDED` here would compile to a coroutine that suspends PERMANENTLY (a silent
                // runtime hang), turning a routing miss into a distant symptom. Fail at TRANSFORM time instead so the
                // mis-routing is visible: either the closure class was dropped upstream, or the recognizer matched a
                // non-intrinsic block. (A genuinely valid intrinsic always carries a resolvable closure class.)
                throw new InvalidOperationException(
                    $"unresolved suspendCoroutineUninterceptedOrReturn closure in '{(_ownerClass ?? _fileClass)}.{_name}' " +
                    $"(closureType={closureType ?? "<none>"}): the intrinsic's `{{ c -> … }}` closure class is not in the " +
                    "compilation — refusing to emit a permanently-suspending coroutine");
            }

            var invoke = (closureCls["methods"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(m => Str(m["name"]) == "invoke");
            var cParam = (invoke?["params"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() is JsonObject cp
                ? Str(cp["name"]) : null;

            // capture[i] binds closure field[i] (declaration order). A field read in the invoke body ->
            // the corresponding capture expression (later rewritten: a captured `this` -> $this).
            var capMap = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
            if (closureCls["fields"] is JsonArray flds && closureNew["captures"] is JsonArray caps)
                for (var i = 0; i < flds.Count && i < caps.Count; i++)
                    if (flds[i] is JsonObject fo && Str(fo["name"]) is string fn)
                        capMap[fn] = caps[i];

            var invBody = invoke?["body"] as JsonArray ?? new JsonArray();
            // Split the invoke body: everything but the final `return` is a pre-statement; the return VALUE is the tail.
            JsonNode tail = Suspended();
            var pre = new List<JsonNode>();
            for (var i = 0; i < invBody.Count; i++)
            {
                if (i == invBody.Count - 1 && invBody[i] is JsonObject last && Str(last["k"]) == "return")
                    tail = last["value"] ?? NullConst("kotlin.Any");
                else
                    pre.Add(invBody[i]);
            }

            var retTok = NonEmpty(Str(block["type"])) ?? "kotlin.Any";
            if (retTok is "void" or "kotlin.Unit") retTok = "kotlin.Any";
            var state = ++_state;
            var resumeLabel = NextLabel();
            RegisterResume(state, resumeLabel);
            var field = "__aw$" + state;
            AddFieldTyped(field, retTok);

            // Inlined pre-statements (c->smSelf, captures->exprs), then rewritten (smSelf->this, captures' this->$this).
            foreach (var s in pre)
                EmitStmt(SubstClosure(s, capMap, cParam, closureType), outp);

            outp.Add(SetField("label", IntConst(state)));
            outp.Add(new JsonObject { ["k"] = "setLocal", ["name"] = "result",
                ["value"] = Rewrite(SubstClosure(tail, capMap, cParam, closureType), outp) });
            outp.Add(BrIf(new JsonObject
            {
                ["k"] = "objEq",
                ["l"] = new JsonObject { ["k"] = "local", ["name"] = "result" },
                ["r"] = Suspended(),
            }, false, resumeLabel));
            if (_needSuspendGuard) outp.Add(SetField(SuspendingField, BoolConst(true)));   // BUG 1: mark the suspend-return
            outp.Add(Ret(Suspended()));
            outp.Add(Label(resumeLabel));
            outp.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = ThrowOnFailure() });
            outp.Add(SetField(field, retTok == "kotlin.Any"
                ? new JsonObject { ["k"] = "local", ["name"] = "result" }
                : new JsonObject { ["k"] = "cast", ["type"] = retTok, ["e"] = new JsonObject { ["k"] = "local", ["name"] = "result" } }));
            return FieldOf(field, retTok);
        }

        // F2 — a CROSS-MODULE `suspendCoroutine { … }` / `suspendCoroutineUninterceptedOrReturn { … }` call, lowered to
        // a real cold suspension point. Our compiler does NOT inline @InlineOnly cross-module, so (unlike the same-module
        // valueBlock intrinsic that EmitIntrinsicSuspension handles) the app carries a plain
        // `callStatic <name>(<closureNew|delegateNew>)`, its wrapper body NOT inlined. We reconstruct it here:
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
                    $"(closureNew/delegateNew) could not be resolved in the compilation — refusing to emit a broken coroutine");

            var resultT = NonEmpty(Str(callNode["retType"])) ?? NonEmpty(Str(callNode["ret"])) ?? "kotlin.Any";
            var retTok = resultT is "void" or "kotlin.Unit" ? "kotlin.Any" : resultT;

            var state = ++_state;
            var resumeLabel = NextLabel();
            RegisterResume(state, resumeLabel);
            var awField = "__aw$" + state;
            AddFieldTyped(awField, retTok);

            JsonNode tail;
            if (wrapper)
            {
                var safeField = "__safe$" + state;
                AddFieldTyped(safeField, ContinuationOfAny);
                // this.__safe = newSafeContinuation((Continuation<Any?>) this)   — the SM is its own delegate.
                outp.Add(SetField(safeField, new JsonObject
                {
                    ["k"] = "callStatic", ["owner"] = ThrowOnFailureOwner, ["method"] = "newSafeContinuation",
                    ["args"] = new JsonArray
                    {
                        new JsonObject { ["k"] = "cast", ["type"] = ContinuationOfAny, ["e"] = new JsonObject { ["k"] = "this" } },
                    },
                    ["ret"] = ContinuationOfAny,
                }));
                var cBinding = SmSelfField(safeField, ContinuationOfAny);   // smSelf recv survives the this->$this member rewrite
                foreach (var s in invBody) EmitStmt(SubstBlock(s, capMap, cParam, cBinding, closureType), outp);
                tail = new JsonObject
                {
                    ["k"] = "callStatic", ["owner"] = ThrowOnFailureOwner, ["method"] = "safeGetOrThrow",
                    ["args"] = new JsonArray { SmSelfField(safeField, ContinuationOfAny) }, ["ret"] = "kotlin.Any",
                };
            }
            else
            {
                var cBinding = new JsonObject { ["k"] = "smSelf" };
                JsonNode t = Suspended();
                var pre = new List<JsonNode>();
                for (var i = 0; i < invBody.Count; i++)
                    if (i == invBody.Count - 1 && invBody[i] is JsonObject last && Str(last["k"]) == "return")
                        t = last["value"] ?? NullConst("kotlin.Any");
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
                ["l"] = new JsonObject { ["k"] = "local", ["name"] = "result" },
                ["r"] = Suspended(),
            }, false, resumeLabel));
            if (_needSuspendGuard) outp.Add(SetField(SuspendingField, BoolConst(true)));
            outp.Add(Ret(Suspended()));
            outp.Add(Label(resumeLabel));
            outp.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = ThrowOnFailure() });
            outp.Add(SetField(awField, retTok == "kotlin.Any"
                ? new JsonObject { ["k"] = "local", ["name"] = "result" }
                : new JsonObject { ["k"] = "cast", ["type"] = retTok, ["e"] = new JsonObject { ["k"] = "local", ["name"] = "result" } }));
            return FieldOf(awField, retTok);
        }

        // Resolve a suspendCoroutine block arg (closureNew -> a top-level closure class in _closures; delegateNew -> a
        // top-level __lambdaN in _lambdaMethods) to its invoke body, continuation-param name, and capture map (empty
        // for delegateNew). Returns (null, …) when unresolvable.
        (JsonArray body, string cParam, Dictionary<string, JsonNode> capMap, string closureType)
        ResolveBlockLambda(JsonObject arg)
        {
            var capMap = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
            JsonObject invoke;
            string closureType = null;
            if (arg != null && Str(arg["k"]) == "closureNew")
            {
                closureType = Str(arg["closureType"]);
                if (closureType == null || !_closures.TryGetValue(closureType, out var cls))
                    return (null, null, null, null);
                invoke = (cls["methods"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(m => Str(m["name"]) == "invoke");
                if (cls["fields"] is JsonArray flds && arg["captures"] is JsonArray caps)
                    for (var i = 0; i < flds.Count && i < caps.Count; i++)
                        if (flds[i] is JsonObject fo && Str(fo["name"]) is string fn) capMap[fn] = caps[i];
            }
            else if (arg != null && Str(arg["k"]) == "delegateNew")
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

        // As SubstClosure, but binds the continuation param to an ARBITRARY expression (the SafeContinuation field for
        // the wrapper; `smSelf` for the unintercepted form) rather than always `smSelf`.
        JsonNode SubstBlock(JsonNode node, Dictionary<string, JsonNode> capMap, string cParam, JsonNode cBinding, string closureType)
        {
            if (node is JsonObject o)
            {
                var k = Str(o["k"]);
                if (k == "field" && closureType != null && Str(o["ownerType"]) == closureType && Str(o["name"]) is string fn
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
        JsonObject SmSelfField(string name, string type) => new()
        {
            ["k"] = "field",
            ["ownerType"] = _smTypeInst,
            ["recv"] = new JsonObject { ["k"] = "smSelf" },
            ["name"] = name,
            ["retType"] = type,
        };

        // Substitute a closure-class invoke body for inlining: a field read of the closure's own captured field ->
        // its capture expression; the `c`/continuation param -> `smSelf` (the SM itself). Produces a tree still in
        // the ENCLOSING method's vocabulary, which Rewrite/EmitStmt then lower (smSelf->this, captured this->$this).
        JsonNode SubstClosure(JsonNode node, Dictionary<string, JsonNode> capMap, string cParam, string closureType)
        {
            if (node is JsonObject o)
            {
                var k = Str(o["k"]);
                if (k == "field" && Str(o["ownerType"]) == closureType && Str(o["name"]) is string fn
                    && capMap.TryGetValue(fn, out var cap))
                    return cap.DeepClone();
                if (k == "local" && cParam != null && Str(o["name"]) == cParam)
                    return new JsonObject { ["k"] = "smSelf" };
                var copy = new JsonObject();
                foreach (var kv in o) copy[kv.Key] = kv.Value == null ? null : SubstClosure(kv.Value, capMap, cParam, closureType);
                return copy;
            }
            if (node is JsonArray a)
            {
                var copy = new JsonArray();
                foreach (var it in a) copy.Add(it == null ? null : SubstClosure(it, capMap, cParam, closureType));
                return copy;
            }
            return node?.DeepClone();
        }

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
            var generic = Str(awaitNode["k"]) == "clrGenericStatic";
            string resultTok = "kotlin.Unit", taskType, awaiterType;
            if (generic)
            {
                resultTok = NonEmpty((awaitNode["typeArgs"] as JsonArray)?.FirstOrDefault() is JsonValue tv
                    && tv.TryGetValue<string>(out var t0) ? t0 : null) ?? "kotlin.Any";
                taskType = "clrg:" + TaskFqn + "[" + resultTok + "]";
                awaiterType = "clrg:" + TaskAwaiterFqn + "[" + resultTok + "]";
            }
            else
            {
                taskType = "clr:" + TaskFqn;
                awaiterType = "clr:" + TaskAwaiterFqn;
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
                ["k"] = "clrInstance", ["type"] = taskType, ["method"] = "GetAwaiter",
                ["recv"] = task, ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
                ["ret"] = awaiterType,
            }));
            // if (this.<aw>.IsCompleted) goto L_state;   (sync fast path — no suspension)
            outp.Add(BrIf(new JsonObject
            {
                ["k"] = "clrPropGet", ["type"] = awaiterType, ["name"] = "IsCompleted",
                ["static"] = false, ["recv"] = FieldOf(awField, awaiterType), ["retType"] = "kotlin.Boolean",
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
                    ["k"] = "clrInstance", ["type"] = awaiterType, ["method"] = "OnCompleted",
                    ["recv"] = FieldOf(awField, awaiterType),
                    ["argTypes"] = new JsonArray { "clr:" + ActionFqn },
                    ["args"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["k"] = "boundDelegateNew", ["funcType"] = "func:void:",
                            ["ownerType"] = _smTypeInst, ["method"] = cbName, ["virtual"] = false,
                            ["recv"] = new JsonObject { ["k"] = "this" },
                        },
                    },
                    ["ret"] = "void",
                },
            });
            if (_needSuspendGuard) outp.Add(SetField(SuspendingField, BoolConst(true)));   // BUG 1: mark the suspend-return
            outp.Add(Ret(Suspended()));
            outp.Add(Label(afterLabel));

            // L_state: <value> = this.<aw>.GetResult();   (throws on a faulted/canceled task)
            var getResult = new JsonObject
            {
                ["k"] = "clrInstance", ["type"] = awaiterType, ["method"] = "GetResult",
                ["recv"] = FieldOf(awField, awaiterType), ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
                ["ret"] = generic ? resultTok : "void",
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
            return NullConst("kotlin.Unit");
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
                ["ownerType"] = ContinuationOfAny,
                ["virtual"] = true,
                ["recv"] = new JsonObject { ["k"] = "this" },
                ["method"] = "resumeWith",
                ["sig"] = "@kotlin.Result[kotlin.Any]",
                ["retType"] = "void",
                ["args"] = new JsonArray
                {
                    // Result.success(null): the PUBLIC static companion factory (the internal `Result(value)` ctor is
                    // inaccessible cross-assembly, so an app SM cannot `new kotlin.Result`). typeArgs erased to Any by
                    // ContinuationErasure to hit the Result<object> resumeWith slot.
                    new JsonObject
                    {
                        ["k"] = "callStatic", ["owner"] = "kotlin.Result", ["method"] = "success",
                        ["typeArgs"] = new JsonArray { "kotlin.Any" },
                        ["args"] = new JsonArray { NullConst("kotlin.Any") },
                        ["ret"] = "@kotlin.Result[kotlin.Any]",
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
                ["ret"] = "void",
                ["body"] = new JsonArray { new JsonObject { ["k"] = "exprStmt", ["expr"] = resumeCall } },
                ["attrs"] = new JsonArray(),
            };
        }

        string FieldType(string name)
        {
            foreach (var (n, t) in _fieldDecls) if (n == name) return t;
            return "kotlin.Any";
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
                ["type"] = ContinuationOfAny,
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
                    ["ret"] = "kotlin.Any",
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
                    argTypes.Add("clrg:" + ContinuationOfAny);
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
                    ["ownerType"] = Str(callNode["ownerType"]),
                    ["virtual"] = Bool(callNode["virtual"]),
                    ["recv"] = recvRw,
                    ["method"] = method,
                    ["args"] = args,
                    ["retType"] = "kotlin.Any",
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
                    ["ret"] = "kotlin.Any",
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
            var origSig = NonEmpty(Str(callNode["sig"]));
            call["sig"] = origSig == null ? ContinuationOfAny : origSig + "," + ContinuationOfAny;
            return call;
        }

        // ---- declaration synthesis ----

        JsonObject SmType(JsonArray invokeBody)
        {
            var fields = new JsonArray();
            foreach (var (n, t) in _fieldDecls)
                fields.Add(new JsonObject { ["name"] = n, ["type"] = t, ["vis"] = "internal" });

            var ctorParams = new JsonArray();
            var ctorBody = new JsonArray();
            if (_isMember)
            {
                ctorParams.Add(new JsonObject { ["name"] = ThisField, ["type"] = _selfType });
                ctorBody.Add(SetField(ThisField, new JsonObject { ["k"] = "local", ["name"] = ThisField }));
            }
            foreach (var p in _params)
            {
                var pn = Str(p["name"]);
                ctorParams.Add(new JsonObject { ["name"] = pn, ["type"] = Str(p["type"]) });
                ctorBody.Add(SetField(pn, new JsonObject { ["k"] = "local", ["name"] = pn }));
            }
            ctorParams.Add(new JsonObject { ["name"] = "completion", ["type"] = ContinuationOfAny });

            var invoke = new JsonObject
            {
                ["name"] = "invokeSuspend",
                ["static"] = false,
                ["override"] = _baseIsLocal,
                ["virtual"] = false,
                ["abstract"] = false,
                ["objectOverride"] = false,
                ["vis"] = "public",
                ["params"] = new JsonArray { new JsonObject { ["name"] = "result", ["type"] = "kotlin.Any" } },
                ["ret"] = "kotlin.Any",
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
                ["isSealed"] = false,
                ["base"] = _baseIsLocal ? ContinuationImplFqn : "clr:" + ContinuationImplFqn,
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
                            NullConst("kotlin.coroutines.CoroutineContext"),
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
                fields.Add(new JsonObject { ["name"] = n, ["type"] = t, ["vis"] = "internal" });

            var ctorParams = new JsonArray();
            var ctorBody = new JsonArray();
            foreach (var (n, t) in _captures)
            {
                ctorParams.Add(new JsonObject { ["name"] = n, ["type"] = t });
                ctorBody.Add(SetField(n, new JsonObject { ["k"] = "local", ["name"] = n }));
            }
            ctorParams.Add(new JsonObject { ["name"] = "completion", ["type"] = ContinuationOfAny });

            var invoke = new JsonObject
            {
                ["name"] = "invokeSuspend",
                ["static"] = false,
                ["override"] = _baseIsLocal,
                ["virtual"] = false,
                ["abstract"] = false,
                ["objectOverride"] = false,
                ["vis"] = "public",
                ["params"] = new JsonArray { new JsonObject { ["name"] = "result", ["type"] = "kotlin.Any" } },
                ["ret"] = "kotlin.Any",
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
                ["isSealed"] = false,
                ["base"] = _baseIsLocal ? lambdaBaseFqn : "clr:" + lambdaBaseFqn,
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
        // Matches BaseContinuationImpl.create's erased CLR ABI: params (Continuation<object>) / (object,
        // Continuation<object>), return Continuation<object> (Unit-as-typearg erases to object). ilemit binds
        // the base slot by name + param types (clrOverride), so the param types MUST match exactly.
        IEnumerable<JsonObject> CreateMethods()
        {
            if (_arity == 0)
            {
                yield return CreateMethod(
                    new JsonArray { new JsonObject { ["name"] = "completion", ["type"] = ContinuationOfAny } },
                    new JsonArray { Ret(NewSm()) });
            }
            else
            {
                // arity-1: the lambda's single own param (extension receiver OR value) -> a field set here.
                // The `value` param is erased `object`; storing it into the (possibly value-typed) param field
                // needs an explicit unbox/castclass (ilemit's setField does not auto-coerce object -> value) —
                // the same `cast` wrap FunGen uses for await fields. A kotlin.Any field takes the value verbatim.
                var paramName = Str(_params[0]["name"]);
                var paramType = Str(_params[0]["type"]) ?? "kotlin.Any";
                JsonNode storedValue = paramType == "kotlin.Any"
                    ? new JsonObject { ["k"] = "local", ["name"] = "value" }
                    : new JsonObject { ["k"] = "cast", ["type"] = paramType, ["e"] = new JsonObject { ["k"] = "local", ["name"] = "value" } };
                yield return CreateMethod(
                    new JsonArray
                    {
                        new JsonObject { ["name"] = "value", ["type"] = "kotlin.Any" },
                        new JsonObject { ["name"] = "completion", ["type"] = ContinuationOfAny },
                    },
                    new JsonArray
                    {
                        new JsonObject
                        {
                            ["k"] = "var",
                            ["name"] = "__sm",
                            ["type"] = _smTypeInst,
                            ["init"] = NewSm(),
                        },
                        new JsonObject
                        {
                            ["k"] = "setField",
                            ["ownerType"] = _smTypeInst,
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
            argTypes.Add(ContinuationOfAny);
            return new JsonObject { ["k"] = "new", ["type"] = _smTypeInst, ["argTypes"] = argTypes, ["args"] = args };
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
                ["ret"] = ContinuationOfUnit,
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
            if (_isMember) argTypes.Add(_selfType);
            foreach (var p in _params) argTypes.Add(Str(p["type"]));
            argTypes.Add(ContinuationOfAny);

            var body = new JsonArray
            {
                new JsonObject
                {
                    ["k"] = "var",
                    ["name"] = "__sm",
                    ["type"] = _smTypeInst,
                    ["init"] = new JsonObject { ["k"] = "new", ["type"] = _smTypeInst, ["argTypes"] = argTypes, ["args"] = ctorArgs },
                },
                Ret(new JsonObject
                {
                    ["k"] = "callInstance",
                    ["ownerType"] = _smTypeInst,
                    ["virtual"] = true,
                    ["recv"] = new JsonObject { ["k"] = "local", ["name"] = "__sm" },
                    ["method"] = "invokeSuspend",
                    ["sig"] = "kotlin.Any",
                    ["args"] = new JsonArray { NullConst("kotlin.Any") },
                    ["retType"] = "kotlin.Any",
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
            if (_resultType is "void" or "kotlin.Unit"
                && !(cloned.Count > 0 && cloned[^1] is JsonObject last && Str(last["k"]) == "return"))
                cloned.Add(Ret(NullConst("kotlin.Any")));
            return ColdMethod(cloned);
        }

        JsonObject ColdMethod(JsonArray body)
        {
            var ps = new JsonArray();
            foreach (var p in _params) ps.Add(p.DeepClone());
            ps.Add(new JsonObject { ["name"] = "completion", ["type"] = ContinuationOfAny });
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
                ["ret"] = "kotlin.Any",
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
            ps.Add(new JsonObject { ["name"] = "completion", ["type"] = ContinuationOfAny });
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
                ["ret"] = "kotlin.Any",
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
                fwd.Add(NullConst(ContinuationOfAny));
                body = new JsonArray
                {
                    new JsonObject
                    {
                        ["k"] = "exprStmt",
                        ["expr"] = new JsonObject
                        {
                            ["k"] = "callStatic", ["owner"] = null, ["method"] = _coldName,
                            ["args"] = fwd, ["ret"] = "kotlin.Any",
                        },
                    },
                };
            }
            else
            {
                // main returns Unit, so the root sink is typed over Unit.
                var tcsType = "clrg:" + _tcsBcl + "[kotlin.Unit]";
                var taskType = "clrg:" + _taskBcl + "[kotlin.Unit]";
                var rootType = "clrg:" + RootContinuationFqn + "[kotlin.Unit]";

                var coldArgs = new JsonArray();
                foreach (var p in _params) coldArgs.Add(new JsonObject { ["k"] = "local", ["name"] = Str(p["name"]) });
                coldArgs.Add(new JsonObject { ["k"] = "cast", ["type"] = ContinuationOfAny, ["e"] = Local("__root") });

                body = new JsonArray
                {
                    new JsonObject
                    {
                        ["k"] = "var", ["name"] = "__tcs", ["type"] = tcsType,
                        ["init"] = new JsonObject { ["k"] = "clrNew", ["type"] = tcsType, ["argTypes"] = new JsonArray(), ["args"] = new JsonArray() },
                    },
                    new JsonObject
                    {
                        ["k"] = "var", ["name"] = "__root", ["type"] = rootType,
                        ["init"] = new JsonObject
                        {
                            ["k"] = "clrNew", ["type"] = rootType,
                            ["argTypes"] = new JsonArray { tcsType }, ["args"] = new JsonArray { Local("__tcs") },
                        },
                    },
                    // r = main$dotkt_suspend(args..., (Continuation)root)   — a synchronous throw propagates RAW.
                    new JsonObject
                    {
                        ["k"] = "var", ["name"] = "__r", ["type"] = "kotlin.Any",
                        ["init"] = new JsonObject
                        {
                            ["k"] = "callStatic", ["owner"] = null, ["method"] = _coldName,
                            ["args"] = coldArgs, ["ret"] = "kotlin.Any",
                        },
                    },
                };
                // if (r !== COROUTINE_SUSPENDED) return;   else  tcs.Task.Wait();   (block for the async resume)
                var skipL = NextLabel();
                body.Add(BrIf(new JsonObject { ["k"] = "objEq", ["l"] = Local("__r"), ["r"] = Suspended() }, false, skipL));
                body.Add(new JsonObject
                {
                    ["k"] = "exprStmt",
                    ["expr"] = new JsonObject
                    {
                        ["k"] = "clrInstance", ["type"] = taskType, ["method"] = "Wait",
                        ["recv"] = new JsonObject
                        {
                            ["k"] = "clrPropGet", ["type"] = tcsType, ["name"] = "Task", ["static"] = false,
                            ["recv"] = Local("__tcs"), ["retType"] = taskType,
                        },
                        ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(), ["ret"] = "void",
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
                ["ret"] = "kotlin.Unit",
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
            var isUnit = _resultType is "void" or "kotlin.Unit";
            var rKotlin = isUnit ? "kotlin.Unit" : _resultType;
            // coroutine-abi.md §1: `suspend fun f(): Unit` -> a NON-generic public `Task` (the C#-idiomatic
            // async-void-returning-Task shape); `suspend fun f(): R` -> `Task<R>`. The internal drive stays generic
            // over Unit (TaskCompletionSource<Unit> / RootContinuation<Unit>); the returned `__tcs.Task` (a Task<Unit>)
            // upcasts to the non-generic Task on return (Task<T> : Task). So ONLY the PUBLIC return type differs for Unit.
            var taskType = "clrg:" + _taskBcl + "[" + rKotlin + "]";    // TaskCompletionSource<R>.Task runtime type
            var taskRetType = isUnit ? "clr:" + _taskBcl : taskType;   // the public bridge return type

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
                    ["ret"] = taskRetType,
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

            var tcsType = "clrg:" + _tcsBcl + "[" + rKotlin + "]";
            var rootType = "clrg:" + RootContinuationFqn + "[" + rKotlin + "]";

            var body = new JsonArray
            {
                new JsonObject
                {
                    ["k"] = "var", ["name"] = "__tcs", ["type"] = tcsType,
                    ["init"] = new JsonObject { ["k"] = "clrNew", ["type"] = tcsType, ["argTypes"] = new JsonArray(), ["args"] = new JsonArray() },
                },
                new JsonObject
                {
                    ["k"] = "var", ["name"] = "__root", ["type"] = rootType,
                    ["init"] = new JsonObject
                    {
                        ["k"] = "clrNew", ["type"] = rootType,
                        ["argTypes"] = new JsonArray { tcsType },
                        ["args"] = new JsonArray { Local("__tcs") },
                    },
                },
                new JsonObject { ["k"] = "var", ["name"] = "__r", ["type"] = "kotlin.Any", ["init"] = Suspended() },
                new JsonObject
                {
                    ["k"] = "try",
                    ["type"] = "void",
                    ["body"] = new JsonArray
                    {
                        new JsonObject { ["k"] = "setLocal", ["name"] = "__r", ["value"] = BridgeColdCall() },
                    },
                    ["catches"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["excType"] = "kotlin.Throwable",
                            ["var"] = "__e",
                            ["body"] = new JsonArray
                            {
                                new JsonObject { ["k"] = "exprStmt", ["expr"] = TcsCall(tcsType, "TrySetException", "kotlin.Throwable", Local("__e")) },
                                new JsonObject { ["k"] = "setLocal", ["name"] = "__r", ["value"] = Suspended() },
                            },
                        },
                    },
                },
            };

            var skipL = NextLabel();
            body.Add(BrIf(new JsonObject { ["k"] = "objEq", ["l"] = Local("__r"), ["r"] = Suspended() }, true, skipL));
            JsonNode resultVal = rKotlin == "kotlin.Any"
                ? Local("__r")
                : new JsonObject { ["k"] = "cast", ["type"] = rKotlin, ["e"] = Local("__r") };
            body.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = TcsCall(tcsType, "TrySetResult", rKotlin, resultVal) });
            body.Add(Label(skipL));
            JsonNode tcsTask = new JsonObject
            {
                ["k"] = "clrPropGet", ["type"] = tcsType, ["name"] = "Task", ["static"] = false,
                ["recv"] = Local("__tcs"), ["retType"] = taskType,
            };
            // Unit: upcast the Task<Unit> (TCS<Unit>.Task) to the non-generic public `Task` return (Task<T> : Task).
            if (isUnit) tcsTask = new JsonObject { ["k"] = "cast", ["type"] = taskRetType, ["e"] = tcsTask };
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
                ["ret"] = taskRetType,
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
            if (!Bool(_m?["retNullable"])) return null;   // no outer `?` -> nothing nullable to encode
            var rKotlin = _resultType is "void" or "kotlin.Unit" ? "kotlin.Unit" : _resultType;
            var flags = new List<int> { 1 };             // the Task<...> outer node is a non-null reference
            if (!WalkNullable(rKotlin, outerNullable: true, flags)) return null;   // R was a value type -> Nullable<T>
            var arr = new JsonArray();
            foreach (var b in flags) arr.Add(b);
            return arr;
        }

        // Append the pre-order NRT bytes for `token` (a Kotlin type FQN, possibly `Owner[arg,...]`). Returns whether any
        // nullable (2) byte was emitted. `outerNullable` marks this node's own `?`; inner args are non-null.
        static bool WalkNullable(string token, bool outerNullable, List<int> flags)
        {
            if (token == null) return false;
            var br = token.IndexOf('[');
            var open = br < 0 ? token : token.Substring(0, br);
            if (ValueTypeFqns.Contains(open) || open is "kotlin.Unit" or "void") return false;   // value/void -> no byte
            flags.Add(outerNullable ? 2 : 1);
            var any = outerNullable;
            if (br >= 0)
            {
                var inner = token.Substring(br + 1, token.Length - br - 2);
                foreach (var arg in SplitTopLevelArgs(inner))
                    any |= WalkNullable(arg.Trim(), outerNullable: false, flags);
            }
            return any;
        }

        static IEnumerable<string> SplitTopLevelArgs(string s)
        {
            var depth = 0; var start = 0;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '[') depth++;
                else if (s[i] == ']') depth--;
                else if (s[i] == ',' && depth == 0) { yield return s.Substring(start, i - start); start = i + 1; }
            }
            if (s.Length > start) yield return s.Substring(start);
        }

        // The bridge's cold-entry call: forward the bridge params + the RootContinuation (cast to the erased
        // Continuation<Any> completion). typeArgs thread the bridge's own generic params to the generic cold entry.
        JsonObject BridgeColdCall()
        {
            var args = new JsonArray();
            foreach (var p in _params) args.Add(Local(Str(p["name"])));
            args.Add(new JsonObject { ["k"] = "cast", ["type"] = ContinuationOfAny, ["e"] = Local("__root") });

            // On a GENERIC enclosing class the callee's declaring type is the CONSTRUCTED self `Box[gp:T]` (matching
            // `this`), never the open `Box` — else verification rejects the recv type.
            var selfOwner = _ownerTypeParams.Count == 0
                ? _ownerClass
                : _ownerClass + "[" + string.Join(",", _ownerTypeParams.Select(t => "gp:" + t)) + "]";

            JsonObject call;
            if (_isMember)
                call = new JsonObject
                {
                    ["k"] = "callInstance",
                    ["ownerType"] = selfOwner,
                    ["virtual"] = false,
                    ["recv"] = new JsonObject { ["k"] = "this" },
                    ["method"] = _coldName,
                    ["args"] = args,
                    ["retType"] = "kotlin.Any",
                };
            else
                call = new JsonObject
                {
                    ["k"] = "callStatic",
                    ["owner"] = null,
                    ["method"] = _coldName,
                    ["args"] = args,
                    ["ret"] = "kotlin.Any",
                };
            if (_typeParams.Count > 0)
            {
                var ta = new JsonArray();
                foreach (var n in _typeParams) ta.Add("gp:" + n);
                call["typeArgs"] = ta;
            }
            return call;
        }

        // A substituted @ClrIntrinsic instance call on the TaskCompletionSource<R> sink (TrySetResult/TrySetException).
        // Emitted post-MemberCallSubstitution, so already in the BCL-owner clrInstance form (result discarded via exprStmt).
        JsonObject TcsCall(string tcsType, string method, string argType, JsonNode arg) => new()
        {
            ["k"] = "clrInstance",
            ["type"] = tcsType,
            ["method"] = method,
            ["recv"] = Local("__tcs"),
            ["argTypes"] = new JsonArray { argType },
            ["args"] = new JsonArray { arg },
            ["ret"] = "kotlin.Boolean",
        };

        static JsonObject Local(string name) => new() { ["k"] = "local", ["name"] = name };

        // ---- small node builders ----

        JsonObject SetField(string name, JsonNode value) => new()
        {
            ["k"] = "setField",
            ["ownerType"] = _smTypeInst,
            ["recv"] = new JsonObject { ["k"] = "this" },
            ["name"] = name,
            ["value"] = value,
        };

        JsonObject FieldOf(string name, string type) => new()
        {
            ["k"] = "field",
            ["ownerType"] = _smTypeInst,
            ["recv"] = new JsonObject { ["k"] = "this" },
            ["name"] = name,
            ["retType"] = type,
        };

        static JsonObject Suspended() => new()
        {
            ["k"] = "callStatic",
            ["owner"] = IntrinsicsKtFqn,
            ["method"] = "get_COROUTINE_SUSPENDED",
            ["args"] = new JsonArray(),
            ["ret"] = "kotlin.Any",
        };

        static JsonObject ThrowOnFailure() => new()
        {
            ["k"] = "callStatic",
            ["owner"] = ThrowOnFailureOwner,
            ["method"] = "throwOnFailure",
            ["args"] = new JsonArray { new JsonObject { ["k"] = "local", ["name"] = "result" } },
            ["ret"] = "void",
        };

        static JsonObject Ret(JsonNode value) => new() { ["k"] = "return", ["value"] = value };
        static JsonObject IntConst(int v) => new() { ["k"] = "const", ["type"] = "kotlin.Int", ["value"] = v };
        static JsonObject BoolConst(bool v) => new() { ["k"] = "const", ["type"] = "kotlin.Boolean", ["value"] = v };
        static JsonObject NullConst(string type) => new() { ["k"] = "const", ["type"] = type, ["value"] = null };
        static JsonObject Label(int id) => new() { ["k"] = "label", ["id"] = id };
        static JsonObject Goto(int id) => new() { ["k"] = "goto", ["id"] = id };
        static JsonObject BrIf(JsonNode cond, bool on, int id) => new()
            { ["k"] = "brIf", ["cond"] = cond, ["on"] = on, ["id"] = id };
        static JsonObject BinEq(JsonNode l, JsonNode r) => new()
            { ["k"] = "bin", ["op"] = "==", ["l"] = l, ["r"] = r };
    }
}
