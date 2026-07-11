using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Text.Json.Nodes;
using DotKt.Bir;

// INLINE SPLICE (#71/#75). Consumes kotc's `callInline` node.
//
//      the GENERIC arm (#71/#75) — an inline fun taking a lambda, whose non-local `return` (or suspend) through the lambda
//      strictly requires the body to live inline at the call site. kotc emits the bindings; we RESOLVE the callee's RAW BIR
//      body by the overload key {owner|name|pc|ga}, disambiguating same-name overloads by a structural `paramSig` match
//      (§4.2) — same-module candidates from InlineBirStash's index, cross-module from [KotlinInline] on the --ref'd assembly
//      (ReferenceMetadataIndex.InlineCandidates / OwnerlessInlineCandidates) — and
//      SPLICE it at BIR level into a value-producing `valueBlock`. Because InlineSplice runs BEFORE all lowering
//      (Program.cs, before ClosureSynthesis/MemberCallSubstitution/BirTypeLowering), the spliced RAW body re-lowers IN
//      THIS app's context (@ClrIntrinsic binds against the app ref.dll, generics resolve with call-site type args,
//      reified is free on CLR). This SUPERSEDES ilemit's old EmitInlineSplice (deleted) — which spliced post-lowering,
//      void-only, and could not re-lower.
//
// The splice mirrors kotc's own same-module `inlineCall` (BirEmitterInline.kt) output shape: value/receiver params bound
// to fresh temps, each lambda-param `invoke` replaced by the carried caller-scope lambda body (its own param bound to a
// temp), the callee's own returns routed to a result-local + end-label, a bare non-local `return` kept verbatim (= the
// caller's return). HYGIENE is mandatory (payload label ids come from the ORIGIN file's dense-from-0 counter, so they
// collide with the consuming file): fresh SEQUENTIAL cfg ids above the file max, per-clone, and a per-splice local
// prefix. On any unsupported shape we FAIL LOUD (#95/§4.5 splice-all: kotc emits a `callInline` for EVERY inline call
// carrying a lambda arg — same- and cross-module — so this engine MUST splice it; there is no non-splice fallback, and an
// un-spliceable shape is a hard build-break to fix here or in the kotc gate, never a silently-degraded plain call).
//
// Runs at the same phase-1 position RepeatInlineLowering did (before ClosureSynthesis so any nested closure in the
// spliced body is synthesized once, before MemberCallSubstitution). Unconditional (ref + rt + app).
static class InlineSplice
{
    static int _counter;                       // global unique splice-instance id (local-prefix minting)
    static int _nextLabelId;                    // per-file fresh cfg label id (set at Apply entry)
    static ReferenceMetadataIndex _refs;

    public static void Apply(JsonNode root, ReferenceMetadataIndex refs)
    {
        _refs = refs;
        _nextLabelId = MaxLabelId(root) + 1;
        Walk(root, 0);
        // POST-PASS: an IMPLICIT `Nullable<VT> -> VT` flow left by SubstTv concretizing a payload's generic `V?` to a
        // concrete `Nullable<VT>` struct, then flowing that local into a non-nullable value-type slot WITHOUT a cast
        // node — a cond branch (`getOrPut`'s `else value`), a var init (a `?.let` receiver `__self = tmp0_safe_receiver`),
        // a return. ilemit emits the raw struct into the VT slot and reads its HasValue bit (=1). Runs whole-tree (the
        // `?.let` receiver binding is minted by STEP 5, AFTER the per-splice STEP-2c cast normalization). Types are still
        // the pre-lowering `kotlin.*` form here, so IsValueTypeFqn matches (same oracle as NormalizeConcretizedCasts).
        NormalizeImplicitNullableUnwrap(root);
        WidenCovariantConstruction(root);
        RetypeReceiverToConcrete(root);
    }

    static void Walk(JsonNode node, int depth)
    {
        if (node is JsonObject o)
        {
            foreach (var kv in o) if (kv.Value != null) Walk(kv.Value, depth);
            Rewrite(o, depth);
        }
        else if (node is JsonArray a)
        {
            for (var i = 0; i < a.Count; i++)
                if (a[i] is JsonNode c)
                {
                    Walk(c, depth);
                    if (a[i] is JsonObject co) Rewrite(co, depth);
                }
        }
    }

    static void Rewrite(JsonObject o, int depth)
    {
        // Every `callInline` kotc emits under splice-all carries `pc`/`ga`/`paramSig` (+ an `owner` that is a file-class
        // string for a facadegen-injected user fn OR JSON-null for a stdlib scope-fn/@InlineOnly fn whose owner bir2cir
        // resolves). `kotlin.repeat` now splices through this same path (kotc no longer emits the specialized loop node).
        if (Str(o["k"]) == "callInline" && o.ContainsKey("pc")) RewriteGeneric(o, depth);
    }

    // ---- (2) the generic cross-module splicer ---------------------------------------------------------------------

    static void RewriteGeneric(JsonObject o, int depth)
    {
        var owner = Str(o["owner"]);
        var callee = Str(o["callee"]);
        var name = callee != null && callee.Contains('.') ? callee[(callee.LastIndexOf('.') + 1)..] : callee;
        int pc = Int(o["pc"]);
        int ga = Int(o["ga"]);
        var paramSig = o["paramSig"] as JsonArray;   // §4.2 overload key: the callee's DECLARED param type nodes (kotc-emitted)

        if (depth > 32) { FailLoud(o, owner, name, pc, ga, "inline splice depth > 32 (recursive-inline data corruption)"); return; }

        // RESOLVE (§4.2, #75 S4b): pick the UNIQUE inline overload whose declared params match the call's `paramSig`.
        //  - OWNER-FUL (a facadegen-injected user fn, or a same-module kotc inlineSpliceCallSameModule): candidates come from
        //    the same-module stash first, else the ref.dll [KotlinInline] under that owner (ResolveInlinePayload).
        //  - OWNER-LESS (a `kotlin.*` stdlib scope-fn/@InlineOnly fn — kotc can't name the klib file class): the bare
        //    name|pc|ga collides across owners (Iterable/Array/IntArray `filter`/`map`/…), so gather candidates across EVERY
        //    kotlin.* host and let paramSig pick the winner, whose own `owner` names the host. In the stdlib SELF-BUILD the
        //    target is same-module (in the stash); in an app build it is cross-module (the ref.dll) — try the stash FIRST.
        //    `sameModule` gates the §4.6 newDelegate guard.
        JsonObject payload;
        bool sameModule;
        if (owner != null)
        {
            var r = ResolveInlinePayload(owner, name, pc, ga, paramSig);
            if (r.payload == null) { FailLoud(o, owner, name, pc, ga, r.diag); return; }
            payload = r.payload; sameModule = r.sameModule;
        }
        else
        {
            var (hit, sm, diag) = ResolveOwnerless(name, pc, ga, paramSig);
            if (hit == null) { FailLoud(o, null, name, pc, ga, diag); return; }
            payload = (JsonObject)hit.DeepClone();
            owner = Str(payload["owner"]);   // the winner's own file-class host
            sameModule = sm;
        }

        // GUARD SCAN -> FAIL LOUD (#95/§4.5 splice-all): kotc emits a callInline for EVERY inline call with a lambda arg,
        // so this engine MUST splice it — there is no plain-call fallback and every un-handled shape is a hard build-break,
        // not a silently-degradable call. o is untouched until step 7, so a mid-stream FailLoud is sound.
        if (Int(payload["v"]) != 1) { FailLoud(o, owner, name, pc, ga, "stale [KotlinInline] payload (pre-raw-BIR)"); return; }
        var pParams = payload["params"] as JsonArray ?? new JsonArray();
        var pBody = payload["body"] as JsonArray;
        if (pBody == null) { FailLoud(o, owner, name, pc, ga, "payload has no body"); return; }
        var typeArgs = o["typeArgs"] as JsonArray ?? new JsonArray();
        if (typeArgs.Count < ga) { FailLoud(o, owner, name, pc, ga, "fewer typeArgs than generic arity"); return; }
        // §4.6: a payload `newDelegate` is a capture-less nested lambda lifted to the ORIGIN file's __lambdaN class; that
        // type token dangles when spliced into another file. Legal same-module (the class is in this file); across a
        // ref.dll edge it is a hard error (engine-side zero-capture synthesis is a future follow-up, never a kotc hack).
        if (!sameModule && HasNode(pBody, "newDelegate")) { FailLoud(o, owner, name, pc, ga, "cross-module payload carries a newDelegate (origin-file __lambdaN) — dangles when spliced (§4.6)"); return; }
        // D1: a callee-body return in EXPRESSION position (`x ?: return v`) is a distinct kind whose routing is not yet
        // implemented — splicing it verbatim would emit a raw caller-frame `ret` with the callee's value. Fail loud.
        if (HasNodeNonClosure(pBody, "returnExpr")) { FailLoud(o, owner, name, pc, ga, "callee-body returnExpr (expression-position return) — not yet routed"); return; }
        // FINDING 1 (#75 holistic, silent-miscompile guard): a capturing `newSuspendLambda` in the payload — the SM
        // ctor binds args by DESCRIPTOR NAME, which the splice's local-prefix/`{k:this}` rewrites do not touch, so the SM
        // field and the invokeSuspend body ref diverge and the ctor arg latches a caller-frame local/`this`. Rare; fail
        // loud until descriptor-name rewriting (treat its captures like a newClosure's) lands.
        if (HasCapturingSuspendLambda(pBody)) { FailLoud(o, owner, name, pc, ga, "payload builds a capturing newSuspendLambda — SM capture descriptors are not splice-rewritten (silent-miscompile guard, #75)"); return; }

        var pRet = payload["ret"]?.DeepClone();

        // STEP 2 — positional type-param subst (payload tv{scope:method,i} -> the call's typeArgs[i]).
        // STEP 2a0 — rename each spliced `newSam`/`newClosure` synthClass to a PER-SPLICE-INSTANCE unique name FIRST: the
        // same inline fn spliced at 2+ sites with DIFFERENT type args yields divergent (SubstTv-specialized) class bodies
        // under the SAME origin name (`dotkt$…$SamN`); ClosureSynthesis' name-dedup would collapse them to the first, so the
        // other sites construct the wrong class (boxgen: `compareBy{it}` vs `compareBy{it.first}` both -> Sam102).
        RenameSpliceSynthClasses(pBody, Interlocked.Increment(ref _counter));
        // F2B: the dispatch receiver's concretized owning-class type args (in the flattened `scope:type` index space), or
        // null when kotc could not supply them (no dispatch / non-generic owner / receiver-class != owner / tv-render /
        // arity mismatch). Substituted alongside `scope:method` in the SAME fused STEP-2 pass over the pristine payload.
        var dispatchTypeArgs = (o["recvs"] as JsonObject)?["dispatchTypeArgs"] as JsonArray;
        if (dispatchTypeArgs != null && dispatchTypeArgs.Count == 0) dispatchTypeArgs = null;
        SubstTv(pBody, typeArgs, ga, dispatchTypeArgs);
        SubstTvIn(pRet, typeArgs, ga, dispatchTypeArgs);
        foreach (var p in pParams) if (p is JsonObject po && po["type"] is JsonNode pt) SubstTvIn(pt, typeArgs, ga, dispatchTypeArgs);
        // STEP 2b — a spliced `newSam`/`newClosure` whose synthClass SubstTv just made FULLY CONCRETE (every tv replaced by a
        // call-site type) but still declares generic `typeParams` + a positional `typeArgs` is a generic-class-with-UNUSED-
        // params implementing a CONCRETE (value-type) interface (`Sam102<T> : Comparator<Int32>`) — value-type interface
        // dispatch then can't bind `compare` (EntryPointNotFound). Drop the now-unused typeParams AND the node's typeArgs to
        // make it a non-generic `Sam102 : Comparator<Int32>`. No-op when any tv survives (a genuinely-generic closure/SAM).
        PruneConcreteSynthClasses(pBody);
        // STEP 2c (seq/single,last): kotc lowers a CONCRETE value-type `as` (`x as Int`) to `nullableValue` (an unbox), but a
        // GENERIC `x as T` to a raw `cast` node. When SubstTv concretizes `cast to tv{T}` -> `cast to Int32` (a value type),
        // the raw `cast` of a boxed/nullable operand is a ref castclass on a value type -> unverifiable native IL (SIGSEGV,
        // e.g. `Iterable.single`/`last`'s `return single as T`). kotc never emits a concrete value-type `cast`, so any
        // `cast to <value type>` here is a concretized generic -> rewrite it to `nullableValue` (ilemit Unbox_Any).
        NormalizeConcretizedCasts(pBody);

        // (FINDING 2 — #75 holistic — a residual `tv{scope:type}` (a generic OWNER class's param) in a dispatch (member)
        // inline. NO GUARD: the signal is not cleanly separable at this layer. The common SAME-CLASS splice — a generic
        // class's own inline member called from its own methods (`ArrayDeque<E>.filterInPlace` into `retainAll`) — is
        // SOUND (caller class IS owner class, so ilemit binds the payload's `tv{scope:type,i}` positionally to the caller's
        // param[i] correctly) and stdlib-load-bearing, so a blanket residual-scope:type guard is a hard FP that breaks the
        // stdlib; and even gated on cross-class it can mis-fire on a transient referenced-interface scope:type that a
        // downstream pass resolves. The genuinely unsound case — a CROSS-class same-module generic-owner member inline
        // (emittable: BirEmitterInline.inlineSpliceCallSameModule has no same-class restriction) — resolves SILENTLY in
        // ilemit (Emitter.Types.ResolveTv binds positionally then falls back to `object`, never throws). CROSS-LAYER
        // FOLLOW-UP (coordinator): kotc carries the dispatch receiver's instantiated type args on `recvs`; RewriteGeneric
        // substitutes `tv{scope:type,i}` like SubstTv does scope:method — the correct fix, and it also types the dispatch
        // temp precisely instead of the bare `Fqn(owner)`.)

        // STEP 3 — hygiene: fresh cfg ids + per-splice local prefix over the callee body.
        int n = Interlocked.Increment(ref _counter);
        string prefix = "__inls" + n + "$";
        FreshenLabels(pBody);
        PrefixLocals(pBody, prefix);

        // STEP 4 — route the callee's OWN returns to a result-local + end-label (BEFORE lambda splicing: at this point
        // every `{k:return}` in the body is the origin fn's, not a caller-lambda's). Unit callee -> no result-local.
        bool unit = IsUnit(pRet);
        JsonNode result = RouteReturns(pBody, unit, pRet, prefix);
        // FINDING 3a-fold (#75 holistic): RouteReturns TAIL-FOLDS a no-early-return `return <T?-local>` into `result` = a
        // bare Nullable<VT> local (e.g. `if (x==null) throw …; return x`). That local feeds the caller's concrete VT slot,
        // so the raw struct's HasValue bit would be read as the value. The whole-tree var/cond/setLocal arms don't see a
        // valueBlock `result` local — unwrap it HERE against the concretized return type (an EARLY return routes through
        // the setLocal arm instead). Prefixed body locals already carry their declared `nullable(VT)` type.
        if (!unit && VtFqnOf(pRet) is string foldVt && result != null)
        {
            var pbTypes = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
            CollectVarTypes(pBody, pbTypes);
            if (UnwrapNullableLocal(result, foldVt, pbTypes) is JsonNode folded) result = folded;
        }

        // STEP 5 — bind extension receiver + value params to temps; register lambda args; rewrite body param refs.
        var stmts = new JsonArray();
        var subst = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var lambdaMap = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var lambdaFuncType = new Dictionary<string, JsonNode>(StringComparer.Ordinal);   // §4.4ii: lamParam -> its funcType (for materialization)
        var callArgs = o["args"] as JsonArray ?? new JsonArray();
        bool ext = Str(payload["recv"]) == "extensionParam";
        var recvs = o["recvs"] as JsonObject;

        // §4.3 — DISPATCH RECEIVER (member inline fn): the payload's `this` refs are the callee's dispatch receiver, not
        // the CALLER's `this`. Bind kotc's carried `recvs.dispatch` to a fresh temp and rewrite the payload body's own
        // `{k:this}` (not descending into nested closures/type-defs — their `this` is their own). Dormant until kotc wires
        // the cross-module member-inline gate (S4b), but required-and-fail-loud when a `dispatch` payload does resolve.
        if (Str(payload["recv"]) == "dispatch")
        {
            if (recvs?["dispatch"] is not JsonNode disp)
            { FailLoud(o, owner, name, pc, ga, "dispatch (member) inline but no recvs.dispatch carried"); return; }
            string thisTemp = prefix + "this";
            // owner = the enclosing type name for a member payload (InlineBirStash keys members under the type), so it is
            // the dispatch receiver's declared type.
            // F2B: type the dispatch temp at the CONCRETE `owner<dispatchTypeArgs>` when carried (else the bare owner) — so
            // a `this.field: T` member read on it binds the precise instantiation instead of the erased owner.
            var thisType = dispatchTypeArgs != null
                ? new JsonObject { ["t"] = "fqn", ["name"] = owner, ["args"] = dispatchTypeArgs.DeepClone() }
                : (JsonNode)TypeJson.Fqn(owner);
            stmts.Add(new JsonObject { ["k"] = "var", ["name"] = thisTemp, ["type"] = thisType, ["init"] = disp.DeepClone() });
            RewriteThis(pBody, thisTemp);
            RewriteThis(result, thisTemp);
        }

        for (int i = 0; i < pParams.Count; i++)
        {
            if (pParams[i] is not JsonObject p) continue;
            string pn = Str(p["name"]);
            var ptype = p["type"];
            // The extension receiver rides payload param[0] == "__self"; its value is recvs.extension, then the call
            // args align to the REMAINING params. Non-extension: args align 1:1.
            JsonNode argNode;
            if (ext && i == 0) argNode = recvs?["extension"];
            else argNode = callArgs.ElementAtOrDefault(ext ? i - 1 : i);

            if (argNode is JsonObject ao && Str(ao["k"]) == "inlineLambda")
            {
                // M1 hygiene: key lambdaMap by the PREFIXED param name (and rewrite the payload's refs to it via `subst`),
                // so the key is splice-local and cannot collide with a caller-CAPTURED var of the SAME name (e.g. a user
                // local `transform` free-referenced inside the escaping lambda) that gets spliced into the payload at STEP
                // 6 — which would otherwise mis-forward/mis-detect it. The carrier's own captured refs stay unprefixed.
                string lname = prefix + pn;
                lambdaMap[lname] = ao;   // spliced at its invoke sites (step 6) / forwarded (§4.4), no temp
                if (ptype != null) lambdaFuncType[lname] = ptype;   // §4.4ii: the delegate/funcType for a materialized carrier
                subst[pn] = new JsonObject { ["k"] = "local", ["name"] = lname };
                continue;
            }
            // A null arg slot = an OMITTED DEFAULT (splice-all: kotc emits a `null` in the defaulted param's slot). Fill it
            // with the payload param's own default expr (SubstTv'd into the call's type frame like the body). An un-carried
            // extension receiver, or a null slot with NO default, is a real error -> fail loud (no fallback under #95).
            // (DefaultArgSplice covers only app-build callStatic/callInstance, AFTER InlineSplice — so the splice fills its
            // own default slots here. A default referencing an earlier param is out of scope, as for DefaultArgSplice.)
            if (argNode == null)
            {
                if (ext && i == 0) { FailLoud(o, owner, name, pc, ga, "extension receiver not carried"); return; }
                if (p["default"] is not JsonNode pdef) { FailLoud(o, owner, name, pc, ga, $"missing (non-defaulted) arg for param {pn}"); return; }
                argNode = pdef.DeepClone();
                SubstTvIn(argNode, typeArgs, ga, dispatchTypeArgs);
                RewriteLocalRefs(argNode, subst);   // a default that references an EARLIER param -> its already-bound temp
                // FINDING 1 (default-expr, #75 holistic): the default expr is RewriteLocalRefs'd (earlier-param refs
                // renamed) but lives OUTSIDE pBody, so the payload-side suspend guard never scanned it. A capturing
                // newSuspendLambda built inside a param default is the same descriptor-skew unsoundness — fail loud.
                if (HasCapturingSuspendLambda(argNode)) { FailLoud(o, owner, name, pc, ga, "param default expr builds a capturing newSuspendLambda — SM capture descriptors are not splice-rewritten (silent-miscompile guard, #75)"); return; }
            }
            string temp = prefix + pn;
            stmts.Add(new JsonObject { ["k"] = "var", ["name"] = temp, ["type"] = ptype?.DeepClone(), ["init"] = argNode.DeepClone() });
            subst[pn] = new JsonObject { ["k"] = "local", ["name"] = temp };
        }
        RewriteLocalRefs(pBody, subst);
        RewriteLocalRefs(result, subst);   // D2: a tail-folded `result` (`= action(x)`) keeps raw param refs otherwise

        // STEP 6 — splice each lambda-param `invoke` with the carried caller-scope lambda body (fresh per invocation).
        SpliceLambdaInvokes(pBody, lambdaMap);
        SpliceLambdaInvokes(result, lambdaMap);   // D2: the folded `result` may itself BE the invoke (`= action(x)`)

        // §4.4(i) — FORWARDING: a lambda param passed BY NAME into a NESTED stdlib-inline call (`map` forwards `transform`
        // to the plain `callStatic mapTo(dest, transform)`) is not a direct invoke here — convert that nested call into a
        // callInline carrying the caller's carrier, so STEP 8's fixpoint splices it where mapTo invokes `transform`.
        ForwardLambdaArgs(pBody, lambdaMap);
        ForwardLambdaArgs(result, lambdaMap);

        // §4.4(ii) — MATERIALIZE any lambda-param carrier still referenced in a NON-INVOKE position (captured in a
        // `newClosure`, e.g. `AtomicArray(size, init) = AtomicArray(Array(size){ init(it) })` where `init` is a capture of
        // the `{ init(it) }` closure; or forwarded to a non-inline call) as a real `newClosure` VALUE bound to a fresh
        // temp, rewriting the surviving `{k:local,name:<lamParam>}` refs to that temp. Legal here: InlineSplice runs BEFORE
        // ClosureSynthesis, which assembles the class from the emitted `synthClass`.
        foreach (var (lname, carrier) in lambdaMap.ToList())
        {
            if (!HasLocalIn(pBody, new[] { lname }) && !HasLocalIn(result, new[] { lname })) continue;   // already invoked/forwarded
            var matTemp = MaterializeCarrier(carrier, lambdaFuncType.GetValueOrDefault(lname), stmts);
            if (matTemp == null)
                // Un-materializable (a NON-LOCAL-return carrier can't be a delegate; or a `{k:local}` capture kotc did not
                // list on the carrier) — fail loud (no fallback under #95; never a silent miscompile).
                { FailLoud(o, owner, name, pc, ga, $"lambda param '{lname}' in a non-invoke position could not be materialized (§4.4ii) — non-local-return or unlisted-capture carrier"); return; }
            var rebind = new Dictionary<string, JsonNode>(StringComparer.Ordinal) { [lname] = new JsonObject { ["k"] = "local", ["name"] = matTemp } };
            RewriteLocalRefs(pBody, rebind);
            RewriteLocalRefs(result, rebind);
        }

        // D3 remainder: any lambda-param ref STILL present after §4.4ii materialization is a dangling local — fail loud.
        if (HasLocalIn(pBody, lambdaMap.Keys) || HasLocalIn(result, lambdaMap.Keys))
        { FailLoud(o, owner, name, pc, ga, "lambda param aliased to a non-invoke position (not directly invoked) — not materialized (§4.4ii remainder)"); return; }

        // STEP 7 — assemble the value-producing valueBlock, swap it in-place.
        foreach (var st in pBody) if (st != null) stmts.Add(st.DeepClone());
        var repl = new JsonObject { ["k"] = "valueBlock", ["stmts"] = stmts, ["result"] = result };
        foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
        foreach (var kv in repl) o[kv.Key] = kv.Value?.DeepClone();

        // STEP 8 — fixpoint: the spliced body may itself contain a nested `callInline` (e.g. kotlin.repeat).
        Walk(o, depth + 1);
    }

    // §4.5 FAIL LOUD (#95 splice-all): kotc emits a callInline for EVERY inline call carrying a lambda arg, so this engine
    // MUST splice it — there is no plain-call fallback, and any shape it cannot splice is a hard build-break (a dropped
    // non-local return / trapped suspension), never a silently-degradable plain call. Throw with the full overload key +
    // reason so the failing call site is identifiable.
    static void FailLoud(JsonObject o, string owner, string name, int pc, int ga, string reason) =>
        throw new NotSupportedException(
            $"inline splice: cannot splice {owner}.{name} (pc={pc} ga={ga}): {reason} — "
            + "under splice-all a callInline arrives for every inline+lambda call, so there is no fallback; fix the splice shape or the kotc gate.");

    // §4.2 (#75 S4b) — RESOLVE the callee's raw-BIR payload by the overload key owner|name|pc|ga, disambiguating same-name
    // inline OVERLOADS by a STRUCTURAL match of each candidate's declared param types against the call's `paramSig`
    // (InlineBirStash.SelectByParamSig — DeepEquals, exact because both sides are kotc-emitted decl type nodes). Same-module
    // candidates come from the in-run stash; cross-module from the ref.dll [KotlinInline]. Returns the (cloned) unique
    // payload + whether it is same-module, or (null, false, diag) with a fail-loud reason (no match / ambiguous overloads).
    static (JsonObject payload, bool sameModule, string diag) ResolveInlinePayload(string owner, string name, int pc, int ga, JsonArray paramSig)
    {
        if (InlineBirStash.Index.TryGetValue($"{owner}|{name}|{pc}|{ga}", out var smCands) && smCands.Count > 0)
        {
            var hit = InlineBirStash.SelectByParamSig(smCands, paramSig, out int mc);
            if (hit != null) return ((JsonObject)hit.DeepClone(), true, null);
            return (null, false, $"no unique same-module inline overload for the call's param signature "
                + $"({smCands.Count} candidate(s), {mc} matched) — structurally-ambiguous inline overloads (e.g. differ only in generic bounds like ifEmpty)");
        }
        var crossCands = _refs?.InlineCandidates(owner, name, pc, ga);
        if (crossCands != null && crossCands.Count > 0)
        {
            var hit = InlineBirStash.SelectByParamSig(crossCands, paramSig, out int mc);
            if (hit != null) return ((JsonObject)hit.DeepClone(), false, null);
            return (null, false, $"no unique cross-module [KotlinInline] overload for the call's param signature "
                + $"({crossCands.Count} candidate(s), {mc} matched) — structurally-ambiguous inline overloads (e.g. differ only in generic bounds like ifEmpty)");
        }
        return (null, false, "no [KotlinInline] payload found");
    }

    // §4.4(i) / §4.2 — resolve an OWNER-LESS kotlin.* inline call (a scope-fn/@InlineOnly fn kotc could not name): try the
    // SAME-MODULE stash FIRST (stdlib self-build — the target is being compiled this run), then the ref.dll (app build).
    // Returns the unique paramSig-matched payload (NOT cloned) + whether it is same-module, or (null, false, diag). The
    // winner's own `owner` field names the host file class.
    static (JsonObject hit, bool sameModule, string diag) ResolveOwnerless(string name, int pc, int ga, JsonArray paramSig)
    {
        var smCands = InlineBirStash.OwnerlessCandidates(name, pc, ga);
        if (smCands != null)
        {
            var hit = InlineBirStash.SelectByParamSig(smCands, paramSig, out int mc);
            if (hit != null) return (hit, true, null);
            if (mc > 1) return (null, false, $"owner-less callInline: no unique same-module paramSig match among {smCands.Count} kotlin.* candidate(s) ({mc} matched) — structurally-ambiguous overloads (e.g. differ only in generic bounds like ifEmpty)");
            // 0 same-module matches -> fall through to the ref.dll (a same-name kotlin.* fn may live cross-module).
        }
        var xCands = _refs?.OwnerlessInlineCandidates(name, pc, ga);
        if (xCands != null)
        {
            var hit = InlineBirStash.SelectByParamSig(xCands, paramSig, out int mc);
            if (hit != null) return (hit, false, null);
            return (null, false, $"owner-less callInline: no unique paramSig match among {xCands.Count} kotlin.* candidate(s) ({mc} matched) — structurally-ambiguous overloads (e.g. differ only in generic bounds like ifEmpty)");
        }
        return (null, false, "owner-less callInline: no file class (same-module stash or ref.dll) hosts this inline fn");
    }

    // §4.3 — rewrite a member-inline payload's own `{k:this}` to a bound dispatch-receiver temp. A `typeDef`/`newSuspendLambda`
    // (its body + `this` are its OWN scope) is skipped WHOLE; a `newClosure`/`newSam`'s SAM/invoke body lives in `synthClass`
    // (its `this` is the closure's own) so only the `synthClass` KEY is skipped — the node's `captures` are STILL descended,
    // so a capture VALUE `{k:this}` (a lambda that captured the ENCLOSING receiver) is correctly rebound to the dispatch temp.
    static void RewriteThis(JsonNode node, string thisTemp)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) is "typeDef" or "newSuspendLambda") return;
            if (Str(o["k"]) == "this")
            {
                foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
                o["k"] = "local";
                o["name"] = thisTemp;
                return;
            }
            foreach (var kv in o) if (kv.Value != null && kv.Key != "synthClass") RewriteThis(kv.Value, thisTemp);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) RewriteThis(c, thisTemp);
    }

    // §4.4(i) — FORWARDING: a lambda param passed BY NAME into a NESTED call that is itself a stdlib inline fn. stdlib
    // `map`'s raw payload is `return mapTo(__self, ArrayList(...), transform)` — a PLAIN owner-less `callStatic` to the
    // inline `mapTo`, `transform` forwarded by name (_Collections.kt:1559, verified against the ref.dll payload). Because
    // the caller's lambda ESCAPES, `mapTo` must ALSO be spliced, so we CONVERT that callStatic into an owner-less
    // `callInline` (recvs.extension + regular args, the forwarded lambda local -> the caller's carrier) and let STEP 8's
    // fixpoint splice it where `mapTo` invokes `transform`. An already-formed nested `callInline` arg is handled directly.
    // A forward we cannot resolve to a stdlib inline fn is LEFT as-is -> the D3-remainder fail-loud (never a silent drop).
    static void ForwardLambdaArgs(JsonNode node, Dictionary<string, JsonObject> lambdaMap)
    {
        if (lambdaMap.Count == 0 || node == null) return;
        if (node is JsonObject o)
        {
            var k = Str(o["k"]);
            // (braces MANDATORY: without them the `else if` dangles onto the inner for-loop `if` and never runs.)
            if (k == "callInline" && o["args"] is JsonArray cargs)
            {
                for (int i = 0; i < cargs.Count; i++)
                    if (cargs[i] is JsonObject ao && Str(ao["k"]) == "local"
                        && Str(ao["name"]) is string ln && lambdaMap.TryGetValue(ln, out var lam))
                        cargs[i] = lam.DeepClone();
            }
            else if (k == "callStatic" && o["owner"] is not JsonObject && Str(o["owner"]) == null
                     && o["args"] is JsonArray sargs
                     && sargs.Any(a => a is JsonObject so && Str(so["k"]) == "local" && Str(so["name"]) is string n && lambdaMap.ContainsKey(n)))
                TryForwardCallStatic(o, lambdaMap);
            foreach (var kv in o) if (kv.Value != null) ForwardLambdaArgs(kv.Value, lambdaMap);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) ForwardLambdaArgs(c, lambdaMap);
    }

    // Convert an owner-less `callStatic` to a stdlib inline fn (that a lambda param is forwarded into) into an owner-less
    // `callInline`, mirroring kotc's inlineSpliceCallOwnerless shape so RewriteGeneric can splice it at the fixpoint. pc =
    // arg count, ga = typeArg count. The overload is disambiguated by the call's `sig` — the callee's DECLARED param type
    // nodes (kotc emits `sig` from `birType(param.type)`, identical to the payload's declared `params[i].type`), which
    // become the new callInline's `paramSig` (§4.2). An extension callee (recv==extensionParam) takes args[0] as
    // recvs.extension; the remaining args become regular args, with a forwarded lambda local swapped for the caller's carrier.
    static void TryForwardCallStatic(JsonObject o, Dictionary<string, JsonObject> lambdaMap)
    {
        var name = Str(o["method"]);
        var sargs = o["args"] as JsonArray;
        if (name == null || sargs == null) return;
        var sig = o["sig"] as JsonArray;
        var typeArgs = o["typeArgs"] as JsonArray;
        int pc = sargs.Count;
        int ga = typeArgs?.Count ?? 0;
        // Owner-less (a `kotlin.*` stdlib inline fn — e.g. apply/let/also/run/with forwarding a lambda param, or map->mapTo):
        // resolve across kotlin.* hosts, SAME-MODULE stash first (stdlib self-build) then the ref.dll (app build), by the
        // call's `sig` (the callee's declared type nodes — the same source as `paramSig`). The rebuilt callInline stays
        // owner-less (RewriteGeneric's owner-less branch re-resolves it by paramSig at the fixpoint, same-module or cross).
        if (ResolveOwnerless(name, pc, ga, sig).hit is not JsonObject payload) return;  // not a resolvable/unique inline fn -> leave; D3-remainder fails loud
        var recv = Str(payload["recv"]);

        var recvs = new JsonObject();
        var callArgs = new JsonArray();
        int start = 0;
        if (recv == "extensionParam") { recvs["extension"] = sargs[0]?.DeepClone(); start = 1; }
        for (int i = start; i < sargs.Count; i++)
        {
            if (sargs[i] is JsonObject ao && Str(ao["k"]) == "local" && Str(ao["name"]) is string ln && lambdaMap.TryGetValue(ln, out var lam))
                callArgs.Add(lam.DeepClone());
            else callArgs.Add(sargs[i]?.DeepClone());
        }
        var repl = new JsonObject
        {
            ["k"] = "callInline",
            ["callee"] = name,
            ["owner"] = null,
            ["pc"] = pc,
            ["ga"] = ga,
            ["paramSig"] = sig?.DeepClone() ?? new JsonArray(),
            ["typeArgs"] = typeArgs?.DeepClone() ?? new JsonArray(),
            ["recvs"] = recvs,
            ["args"] = callArgs,
            ["retType"] = o["ret"]?.DeepClone(),
        };
        foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
        foreach (var kv in repl) o[kv.Key] = kv.Value?.DeepClone();
    }

    // Splice invocations of a lambda param: `{k:callInstance, method:invoke, recv:{k:local,name:<lamParam>}}` where
    // <lamParam> is a registered lambda -> the carried lambda body, freshened+prefixed PER INVOCATION, with the lambda's
    // own param(s) bound to temps initialized from the invoke args. A bare `{k:return}` inside stays the caller's NLR.
    static void SpliceLambdaInvokes(JsonNode node, Dictionary<string, JsonObject> lambdaMap)
    {
        if (node is JsonObject o)
        {
            foreach (var kv in o) if (kv.Value != null) SpliceLambdaInvokes(kv.Value, lambdaMap);
            if (Str(o["k"]) == "callInstance" && Str(o["method"]) == "invoke"
                && o["recv"] is JsonObject rc && Str(rc["k"]) == "local"
                && Str(rc["name"]) is string ln && lambdaMap.TryGetValue(ln, out var lam))
            {
                var repl = BuildLambdaSplice(lam, o["args"] as JsonArray ?? new JsonArray());
                foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
                foreach (var kv in repl) o[kv.Key] = kv.Value?.DeepClone();
            }
        }
        else if (node is JsonArray a)
        {
            foreach (var c in a) if (c != null) SpliceLambdaInvokes(c, lambdaMap);
        }
    }

    static JsonObject BuildLambdaSplice(JsonObject lam, JsonArray invokeArgs)
    {
        int m = Interlocked.Increment(ref _counter);
        string prefix = "__inll" + m + "$";
        var lamParams = lam["params"] as JsonArray ?? new JsonArray();
        var lamBody = (lam["body"] as JsonArray)?.DeepClone() as JsonArray ?? new JsonArray();
        var lamResult = lam["result"]?.DeepClone();

        // FINDING 1 (CARRIER side, #75 holistic): a `newSuspendLambda` in this carrier whose descriptor names a carrier-
        // DECLARED local or a carrier PARAM — PrefixLocalsJoint (below) renames those body refs to `__inll…` while the SM
        // ctor binds the arg by the unprefixed descriptor, so field vs body-ref diverge. Compute the carrier's own name
        // set from the RAW (pre-prefix) body+params and fail loud if a suspend descriptor is in it. (A free caller-frame
        // capture ∉ this set is sound.)
        var carrierNames = new HashSet<string>(StringComparer.Ordinal);
        CollectDeclaredLocals(lamBody, carrierNames);
        foreach (var lp in lamParams) if (lp is JsonObject lpo && Str(lpo["name"]) is string lpn) carrierNames.Add(lpn);
        if ((SuspendDescriptorIn(lamBody, carrierNames) ?? (lamResult != null ? SuspendDescriptorIn(lamResult, carrierNames) : null)) is string badDesc)
            throw new NotSupportedException($"inline splice: a carrier lambda builds a capturing newSuspendLambda whose descriptor '{badDesc}' names a carrier-declared local/param — SM capture descriptors are not splice-rewritten (silent-miscompile guard, #75)");

        // D5: hygiene the carrier's `body` + `result` JOINTLY — the spliceBodyWithReturns carrier's `result` (a value-
        // producing `return@f`) is `{k:local,name:__inlRetN}` whose `var` decl + end-label live in `body`, so an
        // independent per-node map would leave the result naming an un-renamed local. One id-map + one declared-set,
        // applied to both. (The same body may be spliced N times — forEach3 invokes 3× — hence fresh per invocation.)
        FreshenLabelsJoint(lamBody, lamResult);
        PrefixLocalsJoint(prefix, lamBody, lamResult);

        var stmts = new JsonArray();
        var subst = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        for (int j = 0; j < lamParams.Count; j++)
        {
            if (lamParams[j] is not JsonObject lp) continue;
            string pn = Str(lp["name"]);
            string temp = prefix + pn;
            stmts.Add(new JsonObject
            {
                ["k"] = "var",
                ["name"] = temp,
                ["type"] = lp["type"]?.DeepClone(),
                ["init"] = invokeArgs.ElementAtOrDefault(j)?.DeepClone()
                           ?? new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Unit"), ["value"] = null },
            });
            subst[pn] = new JsonObject { ["k"] = "local", ["name"] = temp };
        }
        RewriteLocalRefs(lamBody, subst);
        RewriteLocalRefs(lamResult, subst);

        foreach (var st in lamBody) if (st != null) stmts.Add(st.DeepClone());
        // FLATTEN a `valueBlock` result into THIS splice's own stmts. When the carrier's lambda has a VALUE-POSITION
        // valueBlock body (a `try`/`when`/`elvis`/`run` expression, e.g. `map { try { it.toInt() } catch { 0 } }` whose
        // kotc `tryExpr` result is `{k:valueBlock, stmts:[var tryval; try{…}], result:tryval}`), leaving it as the outer
        // `result` produces a NESTED valueBlock: TryValueOperandHoist then lifts the INNER stmts (`var tryval; try{…}`)
        // above, but the param binding `var __inllN$it = item` stays in the arg position — so the DECL becomes a sibling
        // AFTER the read (`load unknown var __inllN$it`). Splicing the inner stmts here keeps it single-layer + in order.
        while (lamResult is JsonObject ro && Str(ro["k"]) == "valueBlock")
        {
            if (ro["stmts"] is JsonArray rs) foreach (var st in rs) if (st != null) stmts.Add(st.DeepClone());
            if (ro["body"] is JsonArray rb) foreach (var st in rb) if (st != null) stmts.Add(st.DeepClone());
            lamResult = ro["result"]?.DeepClone();
        }
        return new JsonObject
        {
            ["k"] = "valueBlock",
            ["stmts"] = stmts,
            ["result"] = lamResult ?? new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Unit"), ["value"] = null },
        };
    }

    // §4.4(ii) — build a `newClosure` VALUE from a lambda-param carrier (its params + body + result + kotc-emitted
    // `captures`) and bind it to a fresh temp in `stmts`; return the temp name (or null if unmaterializable). Mirrors
    // kotc's lambda() synthClass shape (fields=captures, invoke params/ret/body, generic typeParams over the tvs the reified
    // closure references) so ClosureSynthesis assembles the closure class exactly as for a source lambda.
    //
    // CAPTURES come DIRECTLY from the carrier (kotc lists them, typed in the ENCLOSING frame — a captured local's declared
    // type; `__outer` = the enclosing class type WITH its type args): each becomes a closure FIELD verbatim; the invoke
    // body's `{k:local,name:X}` -> `this.X`, `{k:this}` -> `this.__outer`; the ctor arg (spliced at the materialization
    // site, in the ENCLOSING scope) is the enclosing `{k:local,name:X}` / `{k:this}`. So the generic-class-member case is
    // handled by construction — no scope reconstruction. NON-MATERIALIZABLE (returns null -> caller fails loud): a
    // NON-LOCAL-return carrier (cannot be a delegate); a residual `{k:local}` kotc did not list (e.g. a nested-closure capture).
    static string MaterializeCarrier(JsonObject carrier, JsonNode funcType, JsonArray stmts)
    {
        if (funcType is not JsonObject ft || Str(ft["t"]) != "fn") return null;   // no delegate type to build the closure against
        var lamParams = carrier["params"] as JsonArray ?? new JsonArray();
        var lamBody = (carrier["body"] as JsonArray)?.DeepClone() as JsonArray ?? new JsonArray();
        var lamResult = carrier["result"]?.DeepClone();

        // A bare `{k:return}` (non-local return, targeting the enclosing caller) in the body OR the `result` cannot survive
        // as a closure invoke body.
        if (HasNodeNonClosure(lamBody, "return") || (lamResult != null && HasNodeNonClosure(lamResult, "return"))) return null;

        int n = Interlocked.Increment(ref _counter);
        var cname = "dotkt$inlmat" + n + "$Closure";

        // The invoke body = the carrier body statements, then `return <result>`. A SINGLE-EXPRESSION lambda (e.g.
        // `Iterable { this.iterator() }`) has body=[] and carries the whole value — INCLUDING its captured `this`/locals — in
        // `result`, so the synthetic `return` is where those refs live. ALL of materialization below (capture rewrite,
        // stray/declared scan, nested-closure guard, tv collection) operates on this full `invBody` so `result` is covered.
        var invBody = new JsonArray();
        foreach (var st in lamBody) if (st != null) invBody.Add(st.DeepClone());
        // FLATTEN a value-position `valueBlock` result (a `try`/`when`/`elvis`/`run` body) into the invoke stmts, then
        // return its inner result — same rationale as BuildLambdaSplice (avoid a nested valueBlock that TryValueOperandHoist
        // would split across the return).
        while (lamResult is JsonObject rvb && Str(rvb["k"]) == "valueBlock")
        {
            if (rvb["stmts"] is JsonArray rs) foreach (var st in rs) if (st != null) invBody.Add(st.DeepClone());
            if (rvb["body"] is JsonArray rb) foreach (var st in rb) if (st != null) invBody.Add(st.DeepClone());
            lamResult = rvb["result"]?.DeepClone();
        }
        // The invoke returns the funcType's ret. A void/Unit invoke (a `() -> Unit` lambda, e.g. `{ n += 10 }`) must NOT
        // `return <value>` — a value on the stack at a void `ret` is unverifiable IL (ilverify ReturnVoid). Evaluate a
        // non-const result for its side effect, then a BARE return; a non-void invoke returns the (coerced) result value.
        bool retVoid = TypeJson.Read(ft["ret"]) is TypeNode.Fqn { Args: null, Name: "void" or "kotlin.Unit" } || ft["ret"] == null;
        if (retVoid)
        {
            if (lamResult is JsonObject lr && Str(lr["k"]) is string rk && rk != "const")
                invBody.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = lamResult });
            invBody.Add(new JsonObject { ["k"] = "return" });
        }
        else
            invBody.Add(new JsonObject { ["k"] = "return", ["value"] = lamResult ?? UnitConst() });

        // A NESTED lambda/closure ANYWHERE (body or result) could capture an enclosing local/`this` that our field rewrite
        // does not reach (RewriteCapturesToFields/HasStrayLocal stop at a nested-closure boundary). Refuse rather than risk a
        // silently-mis-captured nested closure — fail loud (conservative; §4.4ii carriers rarely nest a closure).
        if (HasNestedClosure(invBody)) return null;

        // Consume kotc's carrier `captures`: one closure FIELD each (verbatim {name,type}); ctor arg = the enclosing local
        // (`{k:local,name}`) or, for `outer:true`, the enclosing `{k:this}`. Field + ctor-arg order match (positional ctor).
        var fields = new JsonArray();
        var captures = new JsonArray();      // ctor arg VALUES — same order as `fields` (positional ctor in ClosureSynthesis)
        var capNames = new HashSet<string>(StringComparer.Ordinal);
        string outerName = null;
        if (carrier["captures"] is JsonArray caps)
            foreach (var c in caps.OfType<JsonObject>())
            {
                if (Str(c["name"]) is not string cn || c["type"] is not JsonNode ct) return null;
                fields.Add(new JsonObject { ["name"] = cn, ["type"] = ct.DeepClone() });
                if (c["outer"] is JsonValue ov && ov.TryGetValue<bool>(out var ob) && ob)
                { outerName = cn; captures.Add(new JsonObject { ["k"] = "this" }); }   // enclosing `this`
                else { capNames.Add(cn); captures.Add(new JsonObject { ["k"] = "local", ["name"] = cn }); }
            }
        // A bare `{k:this}` (the enclosing receiver — a lambda has no `this` of its own) with NO `outer:true` capture listed
        // is an UNLISTED enclosing-`this` capture: fail loud (symmetric to the stray-local check; else the `{k:this}` would
        // survive into the invoke body and resolve to the CLOSURE instance — a silent miscompile). Checked BEFORE the field
        // rewrite introduces legitimate `field.recv:{k:this}` nodes; the nested-closure guard above ensures no closure-owned
        // `this` is present.
        if (outerName == null && HasNodeNonClosure(invBody, "this")) return null;
        RewriteCapturesToFields(invBody, capNames, outerName, cname);
        // Any residual `{k:local}` that is neither an invoke param NOR one of the carrier's OWN declared locals is a capture
        // kotc did not list -> fail loud rather than leak an unbound local to ilemit.
        var allowed = new HashSet<string>(lamParams.OfType<JsonObject>().Select(p => Str(p["name"])).Where(x => x != null), StringComparer.Ordinal);
        CollectDeclaredLocals(invBody, allowed);
        if (HasStrayLocal(invBody, allowed)) return null;

        // Generic-ness: the closure must be generic over every tv its funcType/params/ret/body/FIELDS reference (reified CLR
        // generics — an unresolved tv is a BadImageFormat). Key tvs by (SCOPE, index): kotc numbers method type-params
        // independently of type (class) type-params, so `tv{method,0}` and `tv{type,0}` are DISTINCT enclosing params and
        // must map to DISTINCT closure params. Renumber the closure-frame refs to a 0-based space, declare that many
        // typeParams, and pass the ORIGINAL enclosing tvs (scope+index) as `typeArgs`. The `newClosure.funcType` stays the
        // CALLER-frame `ft` (un-renumbered) — ilemit resolves it in the caller, `typeArgs` bind the closure params to it.
        var invParams = (JsonArray)lamParams.DeepClone();
        var invRet = ft["ret"]?.DeepClone() ?? TypeJson.Fqn("kotlin.Unit");
        var keys = new SortedSet<(string scope, int i)>();
        CollectTvKeys(invBody, keys); CollectTvKeys(invParams, keys); CollectTvKeys(invRet, keys);
        CollectTvKeys(ft, keys); CollectTvKeys(fields, keys);
        var remap = new Dictionary<(string, int), int>();
        var typeArgs = new JsonArray();
        var typeParams = new JsonArray();
        foreach (var key in keys)
        {
            int ni = remap.Count;
            remap[key] = ni;
            typeArgs.Add(new JsonObject { ["t"] = "tv", ["scope"] = key.scope, ["i"] = key.i });
            typeParams.Add("T" + ni);
        }
        if (remap.Count > 0)
        {
            RenumberTvs(invBody, remap); RenumberTvs(invParams, remap); RenumberTvs(invRet, remap); RenumberTvs(fields, remap);
        }

        var synthClass = new JsonObject
        {
            ["name"] = cname,
            ["fields"] = fields,
            ["params"] = invParams,
            ["ret"] = invRet,
            ["body"] = invBody,
        };
        if (typeParams.Count > 0) synthClass["typeParams"] = typeParams;

        var newClosure = new JsonObject
        {
            ["k"] = "newClosure",
            ["closureType"] = TypeJson.Fqn(cname),
            ["captures"] = captures,
            ["method"] = "invoke",
            ["funcType"] = ft.DeepClone(),
            ["synthClass"] = synthClass,
        };
        if (typeArgs.Count > 0) newClosure["typeArgs"] = typeArgs;

        var matTemp = "__inlmat" + n;
        stmts.Add(new JsonObject { ["k"] = "var", ["name"] = matTemp, ["type"] = ft.DeepClone(), ["init"] = newClosure });
        return matTemp;
    }

    // Rewrite the closure invoke body: a captured `{k:local,name:X}` (X in `capNames`) -> `this.X` field read; the enclosing
    // `{k:this}` -> `this.<outerName>` field read (when the carrier captured `this`, `outerName != null`). Replaces the node
    // at its slot (does NOT recurse into the built replacement's `{k:this}` recv), and does NOT descend into a nested
    // lambda/closure (its `X`/`this` are its own).
    static void RewriteCapturesToFields(JsonNode node, HashSet<string> capNames, string outerName, string cname)
    {
        JsonNode FieldOf(string fn) => new JsonObject
        {
            ["k"] = "field",
            ["ownerType"] = TypeJson.Fqn(cname),
            ["recv"] = new JsonObject { ["k"] = "this" },
            ["name"] = fn,
        };
        bool IsCapLocal(JsonNode v) => v is JsonObject lo && Str(lo["k"]) == "local" && Str(lo["name"]) is string ln && capNames.Contains(ln);
        bool IsOuterThis(JsonNode v) => outerName != null && v is JsonObject to && Str(to["k"]) == "this";
        if (node is JsonArray a)
        {
            for (int i = 0; i < a.Count; i++)
                if (IsCapLocal(a[i])) a[i] = FieldOf(Str(((JsonObject)a[i])["name"]));
                else if (IsOuterThis(a[i])) a[i] = FieldOf(outerName);
                else if (a[i] != null) RewriteCapturesToFields(a[i], capNames, outerName, cname);
            return;
        }
        if (node is not JsonObject o) return;
        // Skip the WHOLE nested-closure node (incl. its captures). Safe ONLY because MaterializeCarrier gates on
        // HasNestedClosure FIRST — a carrier containing any of these fails loud at §4.4ii, so this scan never actually
        // meets one. If that refusal is ever relaxed these must instead skip only the `synthClass` key and DESCEND into
        // the node's `captures`/`args` (as RewriteThis/RewriteLocalRefs do), else a nested closure capturing a
        // carrier-captured local silently misses its field rewrite. (Same note applies to the three siblings below.)
        if (Str(o["k"]) is "inlineLambda" or "newClosure" or "newDelegate" or "newSuspendLambda" or "newSam") return;
        foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys))
            if (IsCapLocal(o[key])) o[key] = FieldOf(Str(((JsonObject)o[key])["name"]));
            else if (IsOuterThis(o[key])) o[key] = FieldOf(outerName);
            else if (o[key] != null) RewriteCapturesToFields(o[key], capNames, outerName, cname);
    }

    // True if the subtree still holds a `{k:local,name:X}` with X ∉ `allowed` (a capture kotc did not list) — not descending
    // into a nested lambda/closure (its locals are its own).
    static bool HasStrayLocal(JsonNode node, HashSet<string> allowed)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "local") return Str(o["name"]) is string ln && !allowed.Contains(ln);
            if (Str(o["k"]) is "inlineLambda" or "newClosure" or "newDelegate" or "newSuspendLambda" or "newSam") return false;
            foreach (var kv in o) if (kv.Value != null && HasStrayLocal(kv.Value, allowed)) return true;
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null && HasStrayLocal(c, allowed)) return true;
        return false;
    }

    // Add every `var`-declared name in the subtree to `set` (the carrier's OWN locals — valid `{k:local}` targets). Does not
    // descend into a nested lambda/closure (its locals are its own scope).
    static void CollectDeclaredLocals(JsonNode node, HashSet<string> set)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) is "inlineLambda" or "newClosure" or "newDelegate" or "newSuspendLambda" or "newSam") return;
            if (Str(o["k"]) == "var" && Str(o["name"]) is string vn) set.Add(vn);
            foreach (var kv in o) if (kv.Value != null) CollectDeclaredLocals(kv.Value, set);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) CollectDeclaredLocals(c, set);
    }

    // True if the subtree contains a nested lambda/closure node.
    static bool HasNestedClosure(JsonNode node)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) is "inlineLambda" or "newClosure" or "newDelegate" or "newSuspendLambda" or "newSam") return true;
            foreach (var kv in o) if (kv.Value != null && HasNestedClosure(kv.Value)) return true;
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null && HasNestedClosure(c)) return true;
        return false;
    }

    // Collect the distinct `{t:tv}` (scope, index) KEYS in the subtree. kotc numbers method type-params independently of
    // type (class) type-params, so scope is part of a tv's identity.
    static void CollectTvKeys(JsonNode node, SortedSet<(string scope, int i)> keys)
    {
        if (node is JsonObject o)
        {
            if (Str(o["t"]) == "tv") keys.Add((Str(o["scope"]) ?? "method", Int(o["i"])));
            foreach (var kv in o) if (kv.Value != null) CollectTvKeys(kv.Value, keys);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) CollectTvKeys(c, keys);
    }

    // Renumber every `{t:tv}` index in place via `remap` keyed by (scope, index). Scope is PRESERVED on the ref (ilemit's
    // cross-pool positional ResolveTv resolves it against the closure class's type params by the renumbered index).
    static void RenumberTvs(JsonNode node, Dictionary<(string, int), int> remap)
    {
        if (node is JsonObject o)
        {
            if (Str(o["t"]) == "tv" && o["i"] is JsonValue iv && iv.TryGetValue<int>(out var i)
                && remap.TryGetValue((Str(o["scope"]) ?? "method", i), out var ni)) o["i"] = ni;
            foreach (var kv in o) if (kv.Value != null) RenumberTvs(kv.Value, remap);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) RenumberTvs(c, remap);
    }

    // Route origin-fn returns: mirror kotc spliceBodyWithReturns. Tail `{k:return,value}` folds to the block value; an
    // early return routes through `setLocal res + goto end`. Does NOT descend into nested closure/type-def subtrees.
    static JsonNode RouteReturns(JsonArray body, bool unit, JsonNode retType, string prefix)
    {
        bool hasEarly = HasEarlyReturn(body);
        if (!hasEarly)
        {
            if (!unit && body.Count > 0 && body[^1] is JsonObject last && Str(last["k"]) == "return" && last["value"] is JsonNode tv)
            {
                // Fold a tail `{k:return,value}` into the block value.
                var v = tv.DeepClone();
                body.RemoveAt(body.Count - 1);
                return v;
            }
            // D4: a UNIT callee ending in an explicit `{k:return}` (possibly with a side-effecting value) must NOT leave
            // that bare return in the block — it would return from the CALLER. Strip it (hoisting a non-trivial value as
            // a trailing exprStmt so its side effect survives).
            if (unit && body.Count > 0 && body[^1] is JsonObject ulast && Str(ulast["k"]) == "return")
            {
                body.RemoveAt(body.Count - 1);
                if (ulast["value"] is JsonNode uv && Str(uv["k"]) is string vk && vk != "const")
                    body.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = uv.DeepClone() });
            }
            return UnitConst();
        }

        int end = _nextLabelId++;
        JsonNode result;
        if (!unit)
        {
            string res = prefix + "ret";
            var rt = retType?.DeepClone() ?? TypeJson.Fqn("kotlin.Unit");
            body.Insert(0, new JsonObject
            {
                ["k"] = "var",
                ["name"] = res,
                ["type"] = rt.DeepClone(),
                ["init"] = new JsonObject { ["k"] = "default", ["type"] = rt.DeepClone() },
            });
            RewriteReturns(body, res, end);
            body.Add(new JsonObject { ["k"] = "label", ["id"] = end });
            result = new JsonObject { ["k"] = "local", ["name"] = res };
        }
        else
        {
            RewriteReturns(body, null, end);
            body.Add(new JsonObject { ["k"] = "label", ["id"] = end });
            result = UnitConst();
        }
        return result;
    }

    // Rewrite every origin `{k:return}` (top-level of the body, non-descending into closures) into the routed form.
    static void RewriteReturns(JsonNode node, string res, int end)
    {
        if (node is JsonArray a)
        {
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] is JsonObject ro && Str(ro["k"]) == "return")
                {
                    var repl = new JsonArray();
                    if (res != null && ro["value"] is JsonNode rv)
                        repl.Add(new JsonObject { ["k"] = "setLocal", ["name"] = res, ["value"] = rv.DeepClone() });
                    // A UNIT callee's early `return sideEffect()` (res == null) still evaluates its value for effect —
                    // hoist a non-trivial value as an exprStmt (symmetric with the D4 tail-strip); a const is dropped.
                    else if (res == null && ro["value"] is JsonObject uv && Str(uv["k"]) != "const")
                        repl.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = uv.DeepClone() });
                    repl.Add(new JsonObject { ["k"] = "goto", ["id"] = end });
                    a.RemoveAt(i);
                    for (int j = 0; j < repl.Count; j++) { a.Insert(i + j, repl[j].DeepClone()); }
                    i += repl.Count - 1;
                }
                else if (a[i] is JsonNode c) RewriteReturns(c, res, end);
            }
        }
        else if (node is JsonObject o)
        {
            if (IsClosureBoundary(o)) return;
            foreach (var kv in o) if (kv.Value != null) RewriteReturns(kv.Value, res, end);
        }
    }

    static bool HasEarlyReturn(JsonNode node)
    {
        bool found = false;
        void Rec(JsonNode nn, bool top, JsonArray parent, int idx)
        {
            if (found) return;
            if (nn is JsonArray a) { for (int i = 0; i < a.Count && !found; i++) Rec(a[i], top, a, i); }
            else if (nn is JsonObject o)
            {
                if (IsClosureBoundary(o)) return;
                if (Str(o["k"]) == "return")
                {
                    // A tail return as the LAST top-level statement folds into the value (not "early").
                    bool tailTop = top && parent != null && idx == parent.Count - 1;
                    if (!tailTop) { found = true; return; }
                }
                foreach (var kv in o) if (kv.Value != null) Rec(kv.Value, false, null, -1);
            }
        }
        if (node is JsonArray arr) for (int i = 0; i < arr.Count && !found; i++) Rec(arr[i], true, arr, i);
        return found;
    }

    // A subtree whose `{k:return}` belongs to a NESTED function (closure / SAM / lifted delegate / type-def method), not the
    // spliced origin fn — return routing / early-return scanning must not descend into it. `newSam` carries a `synthClass`
    // whose SAM-override method body has its own `{k:return}` (kotc emits plain IrReturn) — treat it as a boundary too, else
    // RewriteReturns would re-route the SAM method's return into the caller's end-label (a dangling goto).
    static bool IsClosureBoundary(JsonObject o)
    {
        var k = Str(o["k"]);
        return k == "newClosure" || k == "newDelegate" || k == "newSam" || k == "newSuspendLambda";
    }

    // ---- hygiene helpers -------------------------------------------------------------------------------------------

    // Fresh SEQUENTIAL cfg ids for the DISTINCT ids of the DECLARED labels only (§4.1). The Joint variant maps the ids of
    // MULTIPLE roots through ONE map (a carrier's `body` + `result` cross-reference each other's labels).
    static void FreshenLabels(JsonNode node) => FreshenLabelsJoint(node);

    static void FreshenLabelsJoint(params JsonNode[] roots)
    {
        var map = new Dictionary<int, int>();
        foreach (var r in roots) if (r != null) CollectIds(r, map);
        if (map.Count == 0) return;
        foreach (var key in new List<int>(map.Keys)) map[key] = _nextLabelId++;
        foreach (var r in roots) if (r != null) ApplyIds(r, map);
    }

    // §4.1 HYGIENE (silent-miscompile fix): collect ONLY `label`-declared ids. A carrier body can carry a non-local
    // `{k:goto,id:<caller-loop-label>}` (arm-b break/continue, #95 traffic) whose matching `{k:label}` lives OUTSIDE this
    // region, in the surrounding function. If we re-minted that goto's id (as the old `label||goto||brIf` collect did) it
    // would dangle — and worse, a later same-file splice could mint the same fresh id for an unrelated label, silently
    // branching to a foreign program point. Collecting only labels leaves an out-of-region goto untouched (ApplyIds's
    // TryGetValue guard skips it) so it keeps resolving against the caller's live label.
    static void CollectIds(JsonNode nn, Dictionary<int, int> map)
    {
        if (nn is JsonObject o)
        {
            if (Str(o["k"]) == "typeDef") return;   // a lifted local class's labels are its own scope
            var k = Str(o["k"]);
            if (k == "label" && o["id"] is JsonValue) map.TryAdd(Int(o["id"]), 0);
            foreach (var kv in o) if (kv.Value != null && kv.Key != "synthClass") CollectIds(kv.Value, map);
        }
        else if (nn is JsonArray a) foreach (var c in a) if (c != null) CollectIds(c, map);
    }

    // Remap label/goto/brIf ids — but ONLY when the id is in the map (i.e. a label DECLARED in this freshened region;
    // §4.1). A goto/brIf targeting an out-of-region (caller) label is NOT in the map and passes through untouched. Do NOT
    // "simplify" this into an unconditional remap: that reintroduces the dangling-goto miscompile.
    static void ApplyIds(JsonNode nn, Dictionary<int, int> map)
    {
        if (nn is JsonObject o)
        {
            if (Str(o["k"]) == "typeDef") return;   // scope boundary (its labels are its own)
            var k = Str(o["k"]);
            if ((k == "label" || k == "goto" || k == "brIf") && o["id"] is JsonValue && map.TryGetValue(Int(o["id"]), out var nid))
                o["id"] = nid;
            foreach (var kv in o) if (kv.Value != null && kv.Key != "synthClass") ApplyIds(kv.Value, map);
        }
        else if (nn is JsonArray a) foreach (var c in a) if (c != null) ApplyIds(c, map);
    }

    // Prefix-rename locals DECLARED in this clone (var/forIn.var/forArray.var/repeatInline.var/callInline.var (nested
    // kotlin.repeat)/try.catches[].var) plus every matching {k:local}/{k:setLocal} reference. Leaves param refs (not in
    // the declared set) for step-5 binding. The Joint variant shares ONE declared-set across roots (body + result).
    static void PrefixLocals(JsonNode node, string prefix) => PrefixLocalsJoint(prefix, node);

    static void PrefixLocalsJoint(string prefix, params JsonNode[] roots)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in roots) if (r != null) CollectDeclared(r, declared);
        if (declared.Count == 0) return;
        foreach (var r in roots) if (r != null) ApplyPrefix(r, declared, prefix);
    }

    // SCOPE BOUNDARY (typeDef whole / `synthClass` key): a nested lifted class's own `var`s must NOT enter the splice's
    // declared-set (else a splice ref of the same name is wrongly prefixed), and its ctor/method local refs must NOT be
    // prefixed (they are its OWN scope). Capture VALUES on the node ride the sibling keys, still descended.
    static void CollectDeclared(JsonNode nn, HashSet<string> declared)
    {
        if (nn is JsonObject o)
        {
            if (Str(o["k"]) == "typeDef") return;
            var k = Str(o["k"]);
            if (k == "var" && Str(o["name"]) is string vn) declared.Add(vn);
            if ((k == "forIn" || k == "forArray" || k == "repeatInline" || k == "callInline") && Str(o["var"]) is string fv) declared.Add(fv);
            if (k == "try" && o["catches"] is JsonArray cs)
                foreach (var c in cs) if (c is JsonObject co && Str(co["var"]) is string cv) declared.Add(cv);
            foreach (var kv in o) if (kv.Value != null && kv.Key != "synthClass") CollectDeclared(kv.Value, declared);
        }
        else if (nn is JsonArray a) foreach (var c in a) if (c != null) CollectDeclared(c, declared);
    }

    static void ApplyPrefix(JsonNode nn, HashSet<string> declared, string prefix)
    {
        if (nn is JsonObject o)
        {
            if (Str(o["k"]) == "typeDef") return;
            var k = Str(o["k"]);
            if ((k == "var" || k == "local" || k == "setLocal") && Str(o["name"]) is string nm && declared.Contains(nm))
                o["name"] = prefix + nm;
            if ((k == "forIn" || k == "forArray" || k == "repeatInline" || k == "callInline") && Str(o["var"]) is string fv && declared.Contains(fv))
                o["var"] = prefix + fv;
            if (k == "try" && o["catches"] is JsonArray cs)
                foreach (var c in cs) if (c is JsonObject co && Str(co["var"]) is string cv && declared.Contains(cv)) co["var"] = prefix + cv;
            foreach (var kv in o) if (kv.Value != null && kv.Key != "synthClass") ApplyPrefix(kv.Value, declared, prefix);
        }
        else if (nn is JsonArray a) foreach (var c in a) if (c != null) ApplyPrefix(c, declared, prefix);
    }

    // Replace {k:local,name} references whose name is in `subst` with the bound node; retarget a {k:setLocal,name} on a
    // bound param to the temp's name (D9: assignment to an inline value param — rare, tailrec starg).
    static void RewriteLocalRefs(JsonNode node, Dictionary<string, JsonNode> subst)
    {
        if (node == null || subst.Count == 0) return;
        if (node is JsonObject o)
        {
            // SCOPE BOUNDARY: a nested `typeDef` (lifted local class) is its OWN scope; a `synthClass` (the closure/SAM class
            // ON a newClosure/newSam node) is likewise — its ctor/method params+locals are NOT the splice's. Do NOT rewrite
            // their `{k:local}` refs (a SAM ctor's `{local selector}` = its OWN ctor param, not the spliced `selector`). But
            // DO descend into the node's `captures`/`args` (those capture VALUES are in the splice scope). So skip only the
            // `synthClass` KEY here, and a `typeDef` node whole.
            if (Str(o["k"]) == "typeDef") return;
            foreach (var kv in o) if (kv.Value != null && kv.Key != "synthClass") RewriteLocalRefs(kv.Value, subst);
            var k = Str(o["k"]);
            if (k == "local" && Str(o["name"]) is string nm && subst.TryGetValue(nm, out var b))
            {
                foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
                if (b.DeepClone() is JsonObject bo) foreach (var kv in bo) o[kv.Key] = kv.Value?.DeepClone();
            }
            else if (k == "setLocal" && Str(o["name"]) is string sn && subst.TryGetValue(sn, out var sb)
                     && sb is JsonObject sbo && Str(sbo["name"]) is string tn)
                o["name"] = tn;
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) RewriteLocalRefs(c, subst);
    }

    // Positional type-param subst over a statement array (walks all nodes) — {t:tv,scope:method,i<ga} <- typeArgs[i],
    // {t:tv,scope:type,i} <- dispatchTypeArgs[i] (F2B), in ONE fused pass (see SubstTvIn).
    static void SubstTv(JsonArray body, JsonArray typeArgs, int ga, JsonArray dispatchTypeArgs = null)
    {
        foreach (var st in body) SubstTvIn(st, typeArgs, ga, dispatchTypeArgs);
    }

    // FUSED single walk (F2B): each `{t:tv}` node is replaced IN-PLACE by its scope's positional value — `scope:method` <-
    // the node's `typeArgs[i]` (caller-frame types), `scope:type` <- `dispatchTypeArgs[i]` (the dispatch receiver's
    // concretized owning-class args, in the flattened `scope:type` index space). The replacement RETURNS immediately (never
    // re-walked), so the two substitutions can NEVER see each other's output: a caller `tv{scope:type,j}` INSIDE an inserted
    // `scope:method` value stays (it resolves a frame up), and a `tv` entry in `dispatchTypeArgs` (same-class `this`
    // identity, or a caller method param) is likewise inserted verbatim, not re-substituted. `dispatchTypeArgs` is null/empty
    // when kotc carried none (no dispatch / non-generic owner / receiver-class != owner / tv-render / arity mismatch).
    // TYPE-SCOPE BOUNDARY (hazard): a `synthClass` (closure/SAM class), a `{k:typeDef}` local class, and a
    // `newSuspendLambda` encode their OWN class type params as `tv{scope:type,i}` — the OWNER-class dispatchTypeArgs must
    // NOT reach them. `typeScope` flips off descending through those; METHOD-scope subst continues everywhere (a closure
    // body legitimately references the enclosing `tv{scope:method,i}`).
    static void SubstTvIn(JsonNode node, JsonArray typeArgs, int ga, JsonArray dispatchTypeArgs = null, bool typeScope = true)
    {
        if (node is JsonObject o)
        {
            if (Str(o["t"]) == "tv")
            {
                var scope = Str(o["scope"]);
                JsonNode repl = scope == "method" && Int(o["i"]) is int mi && mi < ga ? typeArgs.ElementAtOrDefault(mi)
                              : scope == "type" && typeScope && dispatchTypeArgs != null && Int(o["i"]) is int ti && ti < dispatchTypeArgs.Count ? dispatchTypeArgs.ElementAtOrDefault(ti)
                              : null;
                if (repl != null)
                {
                    var c = repl.DeepClone();
                    foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
                    if (c is JsonObject co) foreach (var kv in co) o[kv.Key] = kv.Value?.DeepClone();
                    return;
                }
            }
            // This node opens its OWN class-type-param scope -> the owner-class dispatchTypeArgs stop here.
            bool ownBoundary = Str(o["k"]) is "typeDef" or "newSuspendLambda";
            foreach (var kv in o)
                // §4.2: a nested call's `sig`/`paramSig` is the NESTED callee's DECLARED type-param frame (its own tv{method,i}),
                // NOT the outer method's — substituting the outer method's typeArgs into it corrupts the overload key the
                // fixpoint (paramSig) / forwarding (sig) matches against. The nested call's ACTUAL type args ride `typeArgs`
                // (a sibling key), which IS substituted. So skip these two keys; a callee's declared params can never
                // legitimately reference the outer method's tvs.
                if (kv.Value != null && kv.Key != "sig" && kv.Key != "paramSig")
                    SubstTvIn(kv.Value, typeArgs, ga, dispatchTypeArgs, typeScope && !ownBoundary && kv.Key != "synthClass");
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) SubstTvIn(c, typeArgs, ga, dispatchTypeArgs, typeScope);
    }

    // STEP 2b (boxgen): after SubstTv, a `newSam`/`newClosure` whose node `typeArgs` are all CONCRETE (the call-site bound
    // the closure/SAM class's type params to real types) is EFFECTIVELY non-generic — but the synthClass still declares
    // `typeParams` and encodes its OWN params as `tv{scope:type,i}` (which SubstTv, scope:method-only, never touched), while
    // the interface got decoupled to a CONCRETE arg (`Sam102<T> : Comparator<Int32>` — the value-type dispatch then can't
    // bind `compare`). Concretize the class's own params (`tv{scope:type,i}` -> the concrete typeArgs[i]), strip the now-
    // spurious type-args off self-references to the class, and drop `typeParams` + the node's `typeArgs` -> a clean
    // non-generic class. No-op when a typeArg is itself a tv (a genuinely-generic instantiation keeps the class generic).
    static void PruneConcreteSynthClasses(JsonNode node)
    {
        if (node is JsonObject o)
        {
            if ((Str(o["k"]) == "newSam" || Str(o["k"]) == "newClosure") && o["synthClass"] is JsonObject sc
                && Str(sc["name"]) is string cname
                && sc["typeParams"] is JsonArray tps && tps.Count > 0
                && o["typeArgs"] is JsonArray ta && ta.Count > 0 && !HasTv(ta))
            {
                SubstTypeScopeTvs(sc, ta);       // tv{scope:type,i} -> the concrete typeArgs[i]
                StripSelfGenericArgs(sc, cname);  // Sam102<...> (self-ref) -> Sam102 (now non-generic)
                sc.Remove("typeParams");
                o.Remove("typeArgs");
            }
            foreach (var kv in o) if (kv.Value != null) PruneConcreteSynthClasses(kv.Value);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) PruneConcreteSynthClasses(c);
    }

    // Substitute a synthClass's OWN type params (`tv{scope:type,i}`) with the concrete `typeArgs[i]` bound at construction.
    static void SubstTypeScopeTvs(JsonNode node, JsonArray typeArgs)
    {
        if (node is JsonObject o)
        {
            if (Str(o["t"]) == "tv" && Str(o["scope"]) == "type" && typeArgs.ElementAtOrDefault(Int(o["i"])) is JsonNode t)
            {
                var c = t.DeepClone();
                foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
                if (c is JsonObject co) foreach (var kv in co) o[kv.Key] = kv.Value?.DeepClone();
                return;
            }
            foreach (var kv in o) if (kv.Value != null) SubstTypeScopeTvs(kv.Value, typeArgs);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) SubstTypeScopeTvs(c, typeArgs);
    }

    // Strip the generic type-args off a SELF-reference to the (now non-generic) class `cname` (`{t:fqn,name:cname,args:[…]}`
    // -> drop `args`). Leaves references to OTHER types intact.
    static void StripSelfGenericArgs(JsonNode node, string cname)
    {
        if (node is JsonObject o)
        {
            if (Str(o["t"]) == "fqn" && Str(o["name"]) == cname) o.Remove("args");
            foreach (var kv in o) if (kv.Value != null) StripSelfGenericArgs(kv.Value, cname);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) StripSelfGenericArgs(c, cname);
    }

    // A `cast to <value type X>` whose OPERAND is a `Nullable<X>` value (a `T?` local, once SubstTv concretized T->X): the
    // ilemit `cast` path emits `unbox.any X` — INVALID on a `Nullable<X>` STRUCT operand (not a boxed object) -> SIGSEGV
    // (`Iterable.single`/`last`'s `return single as T`, single: T?). kotc lowers a concrete `Int? as Int` to `nullableValue`
    // (Nullable.get_Value); mirror that. DISCRIMINATE by the operand: convert ONLY a `{k:local}` whose declared type is
    // exactly `nullable(X)` — a boxed/object operand (`Any? as Int` in Result.getOrElse) stays a `cast` (ilemit unbox.any is
    // correct there).
    static void NormalizeConcretizedCasts(JsonNode node)
    {
        var varTypes = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        CollectVarTypes(node, varTypes);
        NormalizeCastsWith(node, varTypes);
    }

    static void CollectVarTypes(JsonNode node, Dictionary<string, JsonNode> map)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "var" && Str(o["name"]) is string vn && o["type"] is JsonNode vt) map[vn] = vt;
            foreach (var kv in o) if (kv.Value != null) CollectVarTypes(kv.Value, map);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) CollectVarTypes(c, map);
    }

    static void NormalizeCastsWith(JsonNode node, Dictionary<string, JsonNode> varTypes)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "cast" && o["type"] is JsonObject ct && Str(ct["t"]) == "fqn" && Str(ct["name"]) is string cn
                && _refs != null && _refs.IsValueTypeFqn(cn)
                && o["e"] is JsonObject e && Str(e["k"]) == "local" && Str(e["name"]) is string en
                && varTypes.TryGetValue(en, out var et)
                && et is JsonObject eto && Str(eto["t"]) == "nullable" && eto["of"] is JsonObject ofo
                && Str(ofo["t"]) == "fqn" && Str(ofo["name"]) == cn)
            {
                o["k"] = "nullableValue";
                var elemT = o["type"].DeepClone();
                o.Remove("type");
                o["elem"] = elemT;
            }
            foreach (var kv in o) if (kv.Value != null) NormalizeCastsWith(kv.Value, varTypes);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) NormalizeCastsWith(c, varTypes);
    }

    // Whole-tree companion to NormalizeConcretizedCasts for the IMPLICIT positions (no `cast` node): a `Nullable<VT>`
    // local flowing into a non-nullable value-type slot. Two observed shapes (both #75-splice-induced):
    //   - a `cond` typed VT whose `then`/`else` branch is a bare `Nullable<VT>` local (getOrPut's `else value`);
    //   - a `var` typed VT whose `init` is a bare `Nullable<VT>` local (a spliced `?.let` receiver `__self = tmp_safe`).
    // Same SAFE discrimination as the cast pass: only a `{k:local}` whose DECLARED type is exactly `nullable(VT)` — a
    // struct — is unwrapped (get_Value); a boxed/object operand is left alone. Runs after ALL splicing, so a param-binding
    // var minted by STEP 5 (outside the per-splice STEP-2c pass) is covered too.
    static readonly string[] CondBranchKeys = { "then", "else" };
    static void NormalizeImplicitNullableUnwrap(JsonNode root)
    {
        // Per-METHOD var-type scope: collect + normalize within each function `body` subtree independently, so a
        // same-named local in a SIBLING method (a user `x: Int?` in one, `x: Int` in another) can never cross-alias and
        // wrongly unwrap a non-nullable copy. The first `body`-bearing node owns its whole subtree (nested closures/
        // local fns included) — recursion stops there. (Splice temps are already `_counter`-unique; this hardens the
        // rare user-var case.)
        void FindScopes(JsonNode n)
        {
            if (n is JsonObject o)
            {
                if (o["body"] is JsonArray)
                {
                    var vt = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
                    CollectVarTypes(o, vt);
                    NormalizeFlowsWith(o, vt);
                }
                else foreach (var kv in o) if (kv.Value != null) FindScopes(kv.Value);
            }
            else if (n is JsonArray a) foreach (var c in a) if (c != null) FindScopes(c);
        }
        FindScopes(root);
    }

    // The Kotlin declaration-site-COVARIANT classes whose covariance is unrepresentable on a CLR (invariant) class, so a
    // `new G<narrow>` stored into a `G<wide>` slot needs the construction widened to match the slot's stack type. Only
    // these two: both are `<out ...>` data classes routinely upcast (`partition`'s `Pair<List,List>` -> the declared
    // `Pair<List(=IReadOnlyList),...>`). NOT a general widen — a `<in T>` class would be UNSOUND to widen this way.
    static readonly HashSet<string> CovariantClasses = new(StringComparer.Ordinal) { "kotlin.Pair", "kotlin.Triple" };

    // A `var X: G<wideArgs> = new G<narrowArgs>` (the `new` directly, or the sole `result` of the init valueBlock): a
    // Kotlin declaration-site-COVARIANT class (`Pair<out A,out B>` from `partition`/`to`, `Triple`) constructed with the
    // concrete element types but stored into a slot typed with the widened supertypes (`List` -> `IReadOnlyList`). A CLR
    // class is INVARIANT, so the concrete `G<narrow>` is not the declared `G<wide>` — it RUNS (each element IS the wider
    // type) but ilverify rejects the stack type (`Pair<List,List>` vs `Pair<IReadOnlyList,IReadOnlyList>`). Adopt the
    // declared slot's args onto the `new`: the ctor params are the class's own `tv{scope:type,i}` vars, so each narrow
    // arg is assignable to its now-wide param, and the frontend already proved `X : G<wide> = new G<narrow>` sound.
    static void WidenCovariantConstruction(JsonNode node)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "var" && o["type"] is JsonObject vt && Str(vt["t"]) == "fqn"
                && Str(vt["name"]) is string g && CovariantClasses.Contains(g) && vt["args"] is JsonArray wargs
                && !HasTv(wargs))   // an unconcretized `tv` slot arg would corrupt (or dangle) the construction
            {
                var ctor = o["init"] as JsonObject;
                if (ctor != null && Str(ctor["k"]) == "valueBlock") ctor = ctor["result"] as JsonObject;
                if (ctor != null && Str(ctor["k"]) == "new" && ctor["type"] is JsonObject ntn
                    && Str(ntn["t"]) == "fqn" && Str(ntn["name"]) == g && ntn["args"] is JsonArray nargs
                    && nargs.Count == wargs.Count && !JsonNode.DeepEquals(nargs, wargs)
                    && nargs.All(IsRefTypeArg))   // every narrow arg must be a REFERENCE type: a value-narrow -> ref-wide
                    ntn["args"] = wargs.DeepClone();  // (`Pair<Int,Int> -> Pair<Any,Any>`) needs a box the widened ctor arg
            }                                          // won't get -> unboxed int32 into an object field -> memory corruption.
            foreach (var kv in o) if (kv.Value != null) WidenCovariantConstruction(kv.Value);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) WidenCovariantConstruction(c);
    }

    // F8 Piece 1 — retype ANY local (a SPLICED extension `__self`/`groupByTo`-dest temp, OR a NAMED `val g = groupBy{}`)
    // declared at a covariance-erased INVARIANT-collection type — kotc's use-site `out K`/`in K`->`kotlin.Any`
    // approximation, or a declaration-site covariant value (`Map<K, out V>`: nested `List` erased to `IReadOnlyList`) —
    // but bound to a statically-MORE-CONCRETE same-family value. The concrete value's `stloc` into the invariant local is
    // unverifiable (`Map<Int,…>`/`Map<…,List>` is NOT `Map<Any,…>`/`Map<…,IReadOnlyList>`) and coercing is impossible (a
    // `castclass` to the projection throws at runtime). Retype the local at the arg's actual static type so the store
    // verifies EXACTLY; nested frames fix recursively (their receiver is now this local). ONLY fires on the already-
    // unverifiable concrete-into-projected shape (args DIFFER) — gate-monotone, regression-safe. Runs pre-lowering
    // (kotlin.* FQNs), so `MapVarianceRealign.InvariantCollections` matches. Pairs with Piece 2 (stdlib Any-helpers).
    static void RetypeReceiverToConcrete(JsonNode root)
    {
        StaticType.Refs = _refs;
        StaticType.LocalTypes = StaticType.CollectTypes(root);
        void ProcessMethod(JsonObject method)
        {
            var scope = BirScope.Empty.Child();
            if (method["params"] is JsonArray ps)
                foreach (var p in ps)
                    if (p is JsonObject po && Str(po["name"]) is string pn && TypeJson.Read(po["type"]) is TypeNode pt)
                        scope.VarTypes[pn] = pt;
            CollectVarTypesForRetype(method["body"], scope.VarTypes);   // flat seed (splice temps are _counter-unique)
            RetypeVarsWalk(method["body"], scope);
        }
        if (root is JsonObject ro)
        {
            if (ro["methods"] is JsonArray ms) foreach (var m in ms) if (m is JsonObject mo) ProcessMethod(mo);
            if (ro["types"] is JsonArray ts) foreach (var t in ts)
                if (t is JsonObject to && to["methods"] is JsonArray tms) foreach (var m in tms) if (m is JsonObject mo) ProcessMethod(mo);
        }
    }

    static void CollectVarTypesForRetype(JsonNode node, Dictionary<string, TypeNode> vt)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "var" && Str(o["name"]) is string n && TypeJson.Read(o["type"]) is TypeNode t) vt[n] = t;
            foreach (var kv in o) if (kv.Value != null) CollectVarTypesForRetype(kv.Value, vt);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) CollectVarTypesForRetype(c, vt);
    }

    static void RetypeVarsWalk(JsonNode node, BirScope scope)
    {
        if (node is JsonObject o)
        {
            // POST-ORDER (compositional): retype the INNER temps of a var's init FIRST, so this var's guard sees the
            // CONCRETE Surface of an init that itself contains just-retyped temps (a chained `val h = g.mapValues{}` whose
            // concreteness materializes only after g's own receiver temp is retyped is otherwise a one-pass miss). Sibling
            // propagation is preserved: doc-order + this var's scope update lands before the next sibling is visited.
            foreach (var kv in o) if (kv.Value != null) RetypeVarsWalk(kv.Value, scope);
            // Any local (a SPLICED `__self`/dest temp OR a NAMED `val g = groupBy{}`) declared at a covariance-erased
            // invariant-collection type but bound to a statically-MORE-CONCRETE same-family value — the spliced-receiver,
            // helper-param, and named-result stores are all this ONE root. Guard: invariant-collection head + same arity
            // + no-tv + args-differ (fires ONLY on the already-unverifiable concrete-into-projected shape, so a sound
            // `arg == declared` local is untouched — gate-monotone: an already-green method cannot go red). COND-JOIN:
            // skip when the init is a `cond` whose arms disagree — Surface reports only the FIRST arm, so retyping to it
            // could make the other arm's store newly unverifiable.
            if (Str(o["k"]) == "var" && Str(o["name"]) is string vn
                && o["init"] is JsonNode init && CondArmsAgree(init, scope)
                && TypeJson.Read(o["type"]) is TypeNode declT
                && InvariantCollArgs(declT) is TypeNode[] declArgs
                && StaticType.Surface(init, scope) is TypeNode argT && InvariantCollArgs(argT) is TypeNode[] argArgs
                && argArgs.Length == declArgs.Length && !HasTvType(argT)
                && !argArgs.SequenceEqual(declArgs))
            {
                o["type"] = TypeJson.Write(argT);
                scope.VarTypes[vn] = argT;   // propagate the concrete type to a nested frame whose receiver is this temp
            }
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) RetypeVarsWalk(c, scope);
    }

    // The type-args of an INVARIANT-collection Fqn (`kotlin.collections.Map<K,V>` -> [K,V]), seeing through nullable/
    // oblivious wrappers; null for anything else. Reuses MapVarianceRealign's canonical invariant-collection set.
    static TypeNode[] InvariantCollArgs(TypeNode t) => t switch
    {
        TypeNode.Nullable n => InvariantCollArgs(n.Of),
        TypeNode.Oblivious ob => InvariantCollArgs(ob.Of),
        TypeNode.Fqn { Args: { } args } f when MapVarianceRealign.InvariantCollections.Contains(f.Name) => args,
        _ => null,
    };

    // COND-JOIN guard: if `node` (unwrapping valueBlock results) is a `cond`, both arms must Surface to the SAME type —
    // `StaticType.Surface` reports only the `then` arm (StaticTypeResolver.cs), so retyping to it could make the `else`
    // arm's store newly unverifiable. True (safe) for a non-cond or a cond whose arms agree.
    static bool CondArmsAgree(JsonNode node, BirScope scope)
    {
        while (node is JsonObject vb && Str(vb["k"]) == "valueBlock" && vb["result"] is JsonNode r) node = r;
        if (node is not JsonObject o || Str(o["k"]) != "cond") return true;
        var thenT = o["then"] is JsonNode tn ? StaticType.Surface(tn, scope) : null;
        var elseT = o["else"] is JsonNode en ? StaticType.Surface(en, scope) : null;
        return thenT != null && elseT != null && thenT == elseT;
    }

    static bool HasTvType(TypeNode t) => t switch
    {
        TypeNode.Tv => true,
        TypeNode.Nullable n => HasTvType(n.Of),
        TypeNode.Oblivious ob => HasTvType(ob.Of),
        TypeNode.Array a => HasTvType(a.Elem),
        TypeNode.ByRef b => HasTvType(b.Of),
        TypeNode.Fn fn => HasTvType(fn.Ret) || fn.Params.Any(HasTvType),
        TypeNode.Fqn f => f.Args != null && f.Args.Any(HasTvType),
        _ => false,
    };

    // A construction type-arg that is a REFERENCE type (a plain `{t:fqn}` whose name is not a value-type primitive/struct):
    // safe to widen to a reference supertype with no boxing. A value-type (`kotlin.Int`), a `nullable`(-of-value struct),
    // or a `tv` is NOT — widening its slot to `object`/an interface would leave the unboxed value in a reference field.
    static bool IsRefTypeArg(JsonNode argType) =>
        argType is JsonObject a && Str(a["t"]) == "fqn" && Str(a["name"]) is string nm && !(_refs?.IsValueTypeFqn(nm) ?? false);

    static void NormalizeFlowsWith(JsonNode node, Dictionary<string, JsonNode> varTypes)
    {
        if (node is JsonObject o)
        {
            var k = Str(o["k"]);
            if (k == "cond" && VtFqnOf(o["type"]) is string condVt)
            {
                foreach (var br in CondBranchKeys)
                    if (o[br] is JsonNode bn && UnwrapNullableLocal(bn, condVt, varTypes) is JsonNode w) o[br] = w;
            }
            else if (k == "var" && VtFqnOf(o["type"]) is string slotVt
                     && o["init"] is JsonNode ini && UnwrapNullableLocal(ini, slotVt, varTypes) is JsonNode wi)
                o["init"] = wi;
            // FINDING 3a: a `setLocal <VT-local> = <Nullable<VT> local>` — the shape RouteReturns MINTS for an early
            // `return <T?-local>` with a concretized value-type return (`setLocal __inlsN$ret(:Int32) = {nullable Int32}`).
            // Without the unwrap ilemit stores the raw struct and later reads its HasValue bit as the value.
            else if (k == "setLocal" && Str(o["name"]) is string sn && varTypes.TryGetValue(sn, out var st)
                     && VtFqnOf(st) is string setVt && o["value"] is JsonNode sv
                     && UnwrapNullableLocal(sv, setVt, varTypes) is JsonNode sw)
                o["value"] = sw;
            // FINDING 3b: a `cast to <VT>` over a Nullable<VT> splice local that STEP-2c NormalizeConcretizedCasts MISSED
            // — a payload PARAM operand (params bind to vars only in STEP 5, AFTER 2c's var-decl-only scan), e.g.
            // `inline fun <T> f(x: T?) = x as T` with T=Int. Same unbox as seq/single; re-tag the cast to nullableValue.
            else if (k == "cast" && VtFqnOf(o["type"]) is string castVt && o["e"] is JsonNode ce
                     && UnwrapNullableLocal(ce, castVt, varTypes) != null)
            {
                o["elem"] = o["type"].DeepClone();
                o.Remove("type");
                o["k"] = "nullableValue";
            }
            foreach (var kv in o) if (kv.Value != null) NormalizeFlowsWith(kv.Value, varTypes);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) NormalizeFlowsWith(c, varTypes);
    }

    // The fqn of a `{t:fqn,name}` type node when it names a (non-nullable) value type, else null.
    static string VtFqnOf(JsonNode typeNode) =>
        typeNode is JsonObject t && Str(t["t"]) == "fqn" && Str(t["name"]) is string nm
            && _refs != null && _refs.IsValueTypeFqn(nm) ? nm : null;

    // A `{k:local}` whose declared type is exactly `nullable(vtFqn)` -> a `nullableValue` (Nullable.get_Value) wrapper;
    // else null (leave the node untouched — a boxed/object or non-matching operand).
    static JsonNode UnwrapNullableLocal(JsonNode value, string vtFqn, Dictionary<string, JsonNode> varTypes)
    {
        if (value is not JsonObject e || Str(e["k"]) != "local" || Str(e["name"]) is not string en) return null;
        // Gate on a SPLICE-MINTED local (`__inls<N>$`/`__inll<N>$`, always `__inl`-prefixed): the raw-Nullable<VT>-into-VT
        // pathology is purely splice-induced (SubstTv concretizing a payload's `V?`), and those temps are `_counter`-unique
        // so they can never alias a same-named user local (which kotc emits under its raw source name). A user's own
        // `val x: Int? ... else x` never hits this — it is not a struct-reinterpret bug.
        if (!en.Contains("__inl", StringComparison.Ordinal)) return null;
        if (!varTypes.TryGetValue(en, out var et) || et is not JsonObject eto) return null;
        if (Str(eto["t"]) != "nullable" || eto["of"] is not JsonObject ofo
            || Str(ofo["t"]) != "fqn" || Str(ofo["name"]) != vtFqn) return null;
        return new JsonObject { ["k"] = "nullableValue", ["elem"] = new JsonObject { ["t"] = "fqn", ["name"] = vtFqn }, ["e"] = value.DeepClone() };
    }

    // Rename each spliced `newSam`/`newClosure` synthClass (and every reference to it — the node's samType/closureType and
    // the class's own self-references) to a per-splice-instance unique name, so divergent instantiations of the same origin
    // class never collide under one name.
    static void RenameSpliceSynthClasses(JsonNode node, int id)
    {
        if (node is JsonObject o)
        {
            if ((Str(o["k"]) == "newSam" || Str(o["k"]) == "newClosure") && o["synthClass"] is JsonObject sc
                && Str(sc["name"]) is string old && !old.EndsWith("$sp" + id, StringComparison.Ordinal))
                RenameFqnRefs(o, old, old + "$sp" + id);
            foreach (var kv in o) if (kv.Value != null) RenameSpliceSynthClasses(kv.Value, id);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) RenameSpliceSynthClasses(c, id);
    }

    static void RenameFqnRefs(JsonNode node, string old, string neu)
    {
        if (node is JsonObject o)
        {
            if (Str(o["t"]) == "fqn" && Str(o["name"]) == old) o["name"] = neu;                                   // a type ref
            else if (Str(o["name"]) == old && (o.ContainsKey("fields") || o.ContainsKey("methods"))) o["name"] = neu;  // the class decl
            foreach (var kv in o) if (kv.Value != null) RenameFqnRefs(kv.Value, old, neu);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) RenameFqnRefs(c, old, neu);
    }

    static bool HasTv(JsonNode node)
    {
        if (node is JsonObject o)
        {
            if (Str(o["t"]) == "tv") return true;
            foreach (var kv in o) if (kv.Value != null && HasTv(kv.Value)) return true;
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null && HasTv(c)) return true;
        return false;
    }

    static bool HasNode(JsonNode node, params string[] kinds)
    {
        if (node is JsonObject o)
        {
            if (kinds.Contains(Str(o["k"]))) return true;
            foreach (var kv in o) if (kv.Value != null && HasNode(kv.Value, kinds)) return true;
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null && HasNode(c, kinds)) return true;
        return false;
    }

    // FINDING 1 guard (PAYLOAD side) — a `newSuspendLambda` with a NON-EMPTY `captures` (a name/type descriptor list, no
    // `k`) in the payload FRAME. The SM ctor mints its args by descriptor NAME while the splice's PrefixLocals/
    // RewriteLocalRefs rename the sibling body `{k:local}` refs and RewriteThis skips the node whole — so a capturing
    // suspend lambda diverges (field vs body ref) and its ctor arg binds a caller-frame local/`this`. An EMPTY-capture
    // suspend lambda has no such descriptor to skew. Boundary-aligned with RewriteLocalRefs (skip `typeDef` whole + the
    // `synthClass` key): a suspend lambda inside a payload's local class / a nested closure's synthClass is that frame's
    // own — the splice renamers never touch it, so it is sound and must NOT trip the guard.
    static bool HasCapturingSuspendLambda(JsonNode node)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) is "typeDef") return false;
            if (Str(o["k"]) == "newSuspendLambda" && o["captures"] is JsonArray caps && caps.Count > 0) return true;
            foreach (var kv in o) if (kv.Key != "synthClass" && kv.Value != null && HasCapturingSuspendLambda(kv.Value)) return true;
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null && HasCapturingSuspendLambda(c)) return true;
        return false;
    }

    // FINDING 1 guard (CARRIER side) — a `newSuspendLambda` whose descriptor NAMES a carrier-declared local or param (in
    // `names`) is unsound: BuildLambdaSplice's PrefixLocalsJoint/RewriteLocalRefs prefix those body refs to `__inll…`
    // while the SM ctor keeps the unprefixed descriptor. A capture of a FREE caller-frame local (declared OUTSIDE the
    // carrier, so ∉ names) is SOUND — nothing renames it. Returns the first offending descriptor name, else null. Same
    // frame boundaries as the payload scanner.
    static string SuspendDescriptorIn(JsonNode node, HashSet<string> names)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) is "typeDef") return null;
            if (Str(o["k"]) == "newSuspendLambda" && o["captures"] is JsonArray caps)
                foreach (var c in caps)
                    if (c is JsonObject co && Str(co["name"]) is string cn && names.Contains(cn)) return cn;
            foreach (var kv in o)
                if (kv.Key != "synthClass" && kv.Value != null && SuspendDescriptorIn(kv.Value, names) is string hit) return hit;
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null && SuspendDescriptorIn(c, names) is string hit) return hit;
        return null;
    }

    // Like HasNode, but does NOT descend into a nested-fn boundary (a `returnExpr` inside a closure is the closure's).
    static bool HasNodeNonClosure(JsonNode node, string kind)
    {
        if (node is JsonObject o)
        {
            if (IsClosureBoundary(o)) return false;
            if (Str(o["k"]) == kind) return true;
            foreach (var kv in o) if (kv.Value != null && HasNodeNonClosure(kv.Value, kind)) return true;
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null && HasNodeNonClosure(c, kind)) return true;
        return false;
    }

    // Any `{k:local,name}` whose name is one of `names` (a lambda param that was never bound = aliased/forwarded).
    static bool HasLocalIn(JsonNode node, IEnumerable<string> names)
    {
        var set = names as HashSet<string> ?? new HashSet<string>(names, StringComparer.Ordinal);
        if (set.Count == 0) return false;
        bool Rec(JsonNode nn)
        {
            if (nn is JsonObject o)
            {
                if (Str(o["k"]) == "local" && Str(o["name"]) is string nm && set.Contains(nm)) return true;
                foreach (var kv in o) if (kv.Value != null && Rec(kv.Value)) return true;
            }
            else if (nn is JsonArray a) foreach (var c in a) if (c != null && Rec(c)) return true;
            return false;
        }
        return Rec(node);
    }

    // Seeds _nextLabelId (Apply entry). Scans label||goto||brIf DELIBERATELY WIDE (asymmetric with CollectIds's label-only
    // §4.1 narrowing): _nextLabelId must stay strictly above EVERY id in the file so a freshly-minted label can never
    // collide with a caller-loop label that a non-local goto still points at.
    static int MaxLabelId(JsonNode node)
    {
        int max = -1;
        void Rec(JsonNode nn)
        {
            if (nn is JsonObject o)
            {
                var k = Str(o["k"]);
                if ((k == "label" || k == "goto" || k == "brIf") && o["id"] is JsonValue) max = Math.Max(max, Int(o["id"]));
                foreach (var kv in o) if (kv.Value != null) Rec(kv.Value);
            }
            else if (nn is JsonArray a) foreach (var c in a) if (c != null) Rec(c);
        }
        Rec(node);
        return max;
    }

    static bool IsUnit(JsonNode retType)
    {
        if (retType is JsonObject o && Str(o["t"]) == "fqn")
        {
            var nm = Str(o["name"]);
            return nm == "kotlin.Unit" || nm == "void";
        }
        return retType == null;
    }

    static JsonObject UnitConst() =>
        new() { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Unit"), ["value"] = null };

    // ---- (1) kotlin.repeat -> counted loop (unchanged) ------------------------------------------------------------

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
    static int Int(JsonNode n) => (n as JsonValue)?.TryGetValue<int>(out var i) == true ? i : 0;
}
