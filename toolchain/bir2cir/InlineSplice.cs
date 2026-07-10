using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Text.Json.Nodes;
using DotKt.Bir;

// INLINE SPLICE (#71/#75). Consumes kotc's `callInline` node. TWO arms:
//
//  (1) `callee:"kotlin.repeat"` — the specialized counted-loop shape (RewriteRepeat), unchanged: kotc carries the
//      loop var + the spliced-un-closured body; we wrap it in `repeatInline` (honours a non-local `return`).
//
//  (2) the GENERIC arm (#71/#75 S1) — a CROSS-MODULE inline fun taking a lambda, whose non-local `return` (or suspend)
//      through the lambda strictly requires the body to live inline at the call site. kotc emits the bindings; we
//      RESOLVE the callee's RAW BIR body ({owner|name|pc|ga}) — same-module from InlineBirStash's index (dormant in S1),
//      cross-module by reading [KotlinInline] off the --ref'd assembly (ReferenceMetadataIndex.TryReadInlineBir) — and
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
// prefix. On any unsupported shape we swap in kotc's carried `fallback` (the plain call) and log loudly.
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
        if (Str(o["k"]) != "callInline") return;
        // Discriminate by FIELD SHAPE, not callee-FQN: the GENERIC S1 node carries `owner` (+ `fallback`); the
        // specialized `kotlin.repeat` node carries `count`/`var`/`body` and no `owner` (kotc inlineRepeat).
        if (o.ContainsKey("owner")) { RewriteGeneric(o, depth); return; }
        RewriteRepeat(o);
    }

    // ---- (2) the generic cross-module splicer ---------------------------------------------------------------------

    static void RewriteGeneric(JsonObject o, int depth)
    {
        var owner = Str(o["owner"]);
        var callee = Str(o["callee"]);
        var name = callee != null && callee.Contains('.') ? callee[(callee.LastIndexOf('.') + 1)..] : callee;
        int pc = Int(o["pc"]);
        int ga = Int(o["ga"]);

        if (depth > 32) { Fallback(o, owner, name, pc, ga, "inline splice depth > 32 (recursive-inline data corruption)"); return; }

        // RESOLVE: same-module stash index (dormant in S1), then cross-module ref.dll [KotlinInline]. A POISONED
        // same-module entry (null = an owner|name|pc|ga overload collision) is skipped -> cross-module -> fallback.
        JsonObject payload = null;
        if (InlineBirStash.Index.TryGetValue($"{owner}|{name}|{pc}|{ga}", out var idx) && idx != null)
            payload = (JsonObject)idx.DeepClone();
        else if (_refs?.TryReadInlineBir(owner, name, pc, ga) is JsonObject cross)
            payload = (JsonObject)cross.DeepClone();

        // GUARD SCAN -> fallback (leave the plain call, log the semantic cost). The splice is applied ONLY to shapes it
        // is proven to lower correctly; any shape it does not fully handle (a callee-body return in expression position,
        // a lambda param aliased/forwarded rather than directly invoked, a missing default arg, an extension receiver
        // not yet carried) falls back to kotc's plain call — safe except it drops a non-local return, which those shapes
        // do not exercise in the S1 corpus. o is untouched until step 7, so a mid-stream Fallback is sound.
        if (payload == null) { Fallback(o, owner, name, pc, ga, "no [KotlinInline] payload found"); return; }
        if (Int(payload["v"]) != 1) { Fallback(o, owner, name, pc, ga, "stale [KotlinInline] payload (pre-raw-BIR)"); return; }
        if (Str(payload["recv"]) == "dispatch") { Fallback(o, owner, name, pc, ga, "dispatch (member) inline — deferred to S4"); return; }
        var pParams = payload["params"] as JsonArray ?? new JsonArray();
        var pBody = payload["body"] as JsonArray;
        if (pBody == null) { Fallback(o, owner, name, pc, ga, "payload has no body"); return; }
        var typeArgs = o["typeArgs"] as JsonArray ?? new JsonArray();
        if (typeArgs.Count < ga) { Fallback(o, owner, name, pc, ga, "fewer typeArgs than generic arity"); return; }
        if (ga > 0 && HasNode(pBody, "newClosure", "newDelegate")) { Fallback(o, owner, name, pc, ga, "generic × lifted closure — deferred"); return; }
        // D1: a callee-body return in EXPRESSION position (`x ?: return v`) is a distinct kind whose routing is not yet
        // implemented — splicing it verbatim would emit a raw caller-frame `ret` with the callee's value. Fall back.
        if (HasNodeNonClosure(pBody, "returnExpr")) { Fallback(o, owner, name, pc, ga, "callee-body returnExpr (expression-position return) — deferred"); return; }

        var pRet = payload["ret"]?.DeepClone();

        // STEP 2 — positional type-param subst (payload tv{scope:method,i} -> the call's typeArgs[i]).
        SubstTv(pBody, typeArgs, ga);
        SubstTvIn(pRet, typeArgs, ga);
        foreach (var p in pParams) if (p is JsonObject po && po["type"] is JsonNode pt) SubstTvIn(pt, typeArgs, ga);

        // STEP 3 — hygiene: fresh cfg ids + per-splice local prefix over the callee body.
        int n = Interlocked.Increment(ref _counter);
        string prefix = "__inls" + n + "$";
        FreshenLabels(pBody);
        PrefixLocals(pBody, prefix);

        // STEP 4 — route the callee's OWN returns to a result-local + end-label (BEFORE lambda splicing: at this point
        // every `{k:return}` in the body is the origin fn's, not a caller-lambda's). Unit callee -> no result-local.
        bool unit = IsUnit(pRet);
        JsonNode result = RouteReturns(pBody, unit, pRet, prefix);

        // STEP 5 — bind extension receiver + value params to temps; register lambda args; rewrite body param refs.
        var stmts = new JsonArray();
        var subst = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var lambdaMap = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var callArgs = o["args"] as JsonArray ?? new JsonArray();
        bool ext = Str(payload["recv"]) == "extensionParam";
        var recvs = o["recvs"] as JsonObject;

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
                lambdaMap[pn] = ao;   // spliced at its invoke sites (step 6), no temp
                continue;
            }
            // D7/D9: a null arg is a DEFAULTED param (kotc emits literal `null`) or an un-carried extension receiver, not
            // a Unit value — binding Unit-null to a typed param is garbage. Fall back (the plain call handles defaults).
            if (argNode == null) { Fallback(o, owner, name, pc, ga, ext && i == 0 ? "extension receiver not carried" : $"missing (defaulted) arg for param {pn}"); return; }
            string temp = prefix + pn;
            stmts.Add(new JsonObject { ["k"] = "var", ["name"] = temp, ["type"] = ptype?.DeepClone(), ["init"] = argNode.DeepClone() });
            subst[pn] = new JsonObject { ["k"] = "local", ["name"] = temp };
        }
        RewriteLocalRefs(pBody, subst);
        RewriteLocalRefs(result, subst);   // D2: a tail-folded `result` (`= action(x)`) keeps raw param refs otherwise

        // STEP 6 — splice each lambda-param `invoke` with the carried caller-scope lambda body (fresh per invocation).
        SpliceLambdaInvokes(pBody, lambdaMap);
        SpliceLambdaInvokes(result, lambdaMap);   // D2: the folded `result` may itself BE the invoke (`= action(x)`)

        // D3: any lambda-param reference that was NOT a direct `invoke` (aliased `val f = action`, forwarded to a nested
        // callInline) is now a dangling local with no binding — fall back rather than emit an unresolved-local miscompile.
        if (HasLocalIn(pBody, lambdaMap.Keys) || HasLocalIn(result, lambdaMap.Keys))
        { Fallback(o, owner, name, pc, ga, "lambda param aliased/forwarded (not directly invoked) — deferred"); return; }

        // STEP 7 — assemble the value-producing valueBlock, swap it in-place, drop the fallback.
        foreach (var st in pBody) if (st != null) stmts.Add(st.DeepClone());
        var repl = new JsonObject { ["k"] = "valueBlock", ["stmts"] = stmts, ["result"] = result };
        foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
        foreach (var kv in repl) o[kv.Key] = kv.Value?.DeepClone();

        // STEP 8 — fixpoint: the spliced body may itself contain a nested `callInline` (e.g. kotlin.repeat).
        Walk(o, depth + 1);
    }

    static void Fallback(JsonObject o, string owner, string name, int pc, int ga, string reason)
    {
        Console.Error.WriteLine($"bir2cir: inline splice fallback for {owner}.{name} (pc={pc} ga={ga}): {reason} — "
            + "emitting the plain call; a non-local return through a lambda arg will NOT return from the caller");
        if (o["fallback"] is not JsonNode fb)
            throw new NotSupportedException($"inline splice: no fallback for {owner}.{name} ({reason})");
        var f = fb.DeepClone();
        foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
        if (f is JsonObject fo) foreach (var kv in fo) o[kv.Key] = kv.Value?.DeepClone();
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
        return new JsonObject
        {
            ["k"] = "valueBlock",
            ["stmts"] = stmts,
            ["result"] = lamResult ?? new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Unit"), ["value"] = null },
        };
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

    // A subtree whose `{k:return}` belongs to a NESTED function (closure / lifted delegate / type-def method), not the
    // spliced origin fn — return routing / early-return scanning must not descend into it.
    static bool IsClosureBoundary(JsonObject o)
    {
        var k = Str(o["k"]);
        return k == "newClosure" || k == "newDelegate";
    }

    // ---- hygiene helpers -------------------------------------------------------------------------------------------

    // Fresh SEQUENTIAL cfg ids for the DISTINCT ids of the id-carrying kinds (label/goto/brIf). The Joint variant maps
    // the ids of MULTIPLE roots through ONE map (a carrier's `body` + `result` cross-reference each other's labels).
    static void FreshenLabels(JsonNode node) => FreshenLabelsJoint(node);

    static void FreshenLabelsJoint(params JsonNode[] roots)
    {
        var map = new Dictionary<int, int>();
        foreach (var r in roots) if (r != null) CollectIds(r, map);
        if (map.Count == 0) return;
        foreach (var key in new List<int>(map.Keys)) map[key] = _nextLabelId++;
        foreach (var r in roots) if (r != null) ApplyIds(r, map);
    }

    static void CollectIds(JsonNode nn, Dictionary<int, int> map)
    {
        if (nn is JsonObject o)
        {
            var k = Str(o["k"]);
            if ((k == "label" || k == "goto" || k == "brIf") && o["id"] is JsonValue) map.TryAdd(Int(o["id"]), 0);
            foreach (var kv in o) if (kv.Value != null) CollectIds(kv.Value, map);
        }
        else if (nn is JsonArray a) foreach (var c in a) if (c != null) CollectIds(c, map);
    }

    static void ApplyIds(JsonNode nn, Dictionary<int, int> map)
    {
        if (nn is JsonObject o)
        {
            var k = Str(o["k"]);
            if ((k == "label" || k == "goto" || k == "brIf") && o["id"] is JsonValue && map.TryGetValue(Int(o["id"]), out var nid))
                o["id"] = nid;
            foreach (var kv in o) if (kv.Value != null) ApplyIds(kv.Value, map);
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

    static void CollectDeclared(JsonNode nn, HashSet<string> declared)
    {
        if (nn is JsonObject o)
        {
            var k = Str(o["k"]);
            if (k == "var" && Str(o["name"]) is string vn) declared.Add(vn);
            if ((k == "forIn" || k == "forArray" || k == "repeatInline" || k == "callInline") && Str(o["var"]) is string fv) declared.Add(fv);
            if (k == "try" && o["catches"] is JsonArray cs)
                foreach (var c in cs) if (c is JsonObject co && Str(co["var"]) is string cv) declared.Add(cv);
            foreach (var kv in o) if (kv.Value != null) CollectDeclared(kv.Value, declared);
        }
        else if (nn is JsonArray a) foreach (var c in a) if (c != null) CollectDeclared(c, declared);
    }

    static void ApplyPrefix(JsonNode nn, HashSet<string> declared, string prefix)
    {
        if (nn is JsonObject o)
        {
            var k = Str(o["k"]);
            if ((k == "var" || k == "local" || k == "setLocal") && Str(o["name"]) is string nm && declared.Contains(nm))
                o["name"] = prefix + nm;
            if ((k == "forIn" || k == "forArray" || k == "repeatInline" || k == "callInline") && Str(o["var"]) is string fv && declared.Contains(fv))
                o["var"] = prefix + fv;
            if (k == "try" && o["catches"] is JsonArray cs)
                foreach (var c in cs) if (c is JsonObject co && Str(co["var"]) is string cv && declared.Contains(cv)) co["var"] = prefix + cv;
            foreach (var kv in o) if (kv.Value != null) ApplyPrefix(kv.Value, declared, prefix);
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
            foreach (var kv in o) if (kv.Value != null) RewriteLocalRefs(kv.Value, subst);
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

    // Positional type-param subst over a statement array (walks all nodes) — replace {t:tv,scope:method,i<ga} in place.
    static void SubstTv(JsonArray body, JsonArray typeArgs, int ga)
    {
        foreach (var st in body) SubstTvIn(st, typeArgs, ga);
    }

    static void SubstTvIn(JsonNode node, JsonArray typeArgs, int ga)
    {
        if (node is JsonObject o)
        {
            if (Str(o["t"]) == "tv" && Str(o["scope"]) == "method")
            {
                int i = Int(o["i"]);
                if (i < ga && typeArgs.ElementAtOrDefault(i) is JsonNode ta)
                {
                    var c = ta.DeepClone();
                    foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
                    if (c is JsonObject co) foreach (var kv in co) o[kv.Key] = kv.Value?.DeepClone();
                    return;
                }
            }
            foreach (var kv in o) if (kv.Value != null) SubstTvIn(kv.Value, typeArgs, ga);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) SubstTvIn(c, typeArgs, ga);
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

    static void RewriteRepeat(JsonObject o)
    {
        if (o["count"] is not JsonNode count || o["body"] is not JsonArray body || Str(o["var"]) is not string loopVar)
            return;
        var countType = o["countType"]?.DeepClone() ?? TypeJson.Fqn("kotlin.Int");
        var repnVar = "__repns$" + Interlocked.Increment(ref _counter);

        var repl = new JsonObject
        {
            ["k"] = "valueBlock",
            ["stmts"] = new JsonArray
            {
                new JsonObject { ["k"] = "var", ["name"] = repnVar, ["type"] = countType, ["init"] = count.DeepClone() },
            },
            ["result"] = new JsonObject
            {
                ["k"] = "repeatInline",
                ["var"] = loopVar,
                ["count"] = new JsonObject { ["k"] = "local", ["name"] = repnVar },
                ["body"] = body.DeepClone(),
            },
        };

        foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
        foreach (var kv in repl) o[kv.Key] = kv.Value?.DeepClone();
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
    static int Int(JsonNode n) => (n as JsonValue)?.TryGetValue<int>(out var i) == true ? i : 0;
}
