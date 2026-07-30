using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

// TRY-VALUE OPERAND HOIST (bundle-6 `tryexprop`): CLR eval-order normalization for a value-producing
// try/catch(/finally) used in an OPERAND slot (`1 + try{..}`, `"x" + try{..}`, `f(try{..})`).
//
// kotc already emits the correct value-form: a `valueBlock` whose `stmts` are `[ var dotkt_tryvalN;
// try{ ..setLocal dotkt_tryvalN.. } catch{ ..setLocal.. } ]` with `result: local(dotkt_tryvalN)`.
// The problem is downstream (ilemit): a `valueBlock` is emitted INLINE — its stmts run in place — but a
// CLR protected region must be ENTERED WITH AN EMPTY EVALUATION STACK (`leave` clears the stack). When
// the try-valueBlock sits in a non-first operand slot, the left operand is already pushed, so entering
// the try wipes it -> InvalidProgramException.
//
// The fix belongs to bir2cir (Kotlin<->CLR relation; kotc has no CLR-stack knowledge, ilemit has no
// Kotlin-aware rescheduling). Mirroring the suspend-spill precedent (SuspendColdLowering), we HOIST a
// try-valueBlock out of the operand position to PRECEDING statements: its `stmts` (the var + the try)
// become statements of the enclosing statement, and the operand slot is replaced with the result local
// `{"k":"local","name":"dotkt_tryvalN"}`. Left-to-right Kotlin evaluation order is preserved: any
// side-effecting operand evaluated BEFORE a hoisted try is itself spilled to a preceding temp first.
//
// A try-valueBlock that is ALREADY at an empty-stack position (the first thing evaluated in its
// statement — e.g. `val x = try{..}` directly, or the sole/leading argument of a call) is left inline;
// ilemit handles it fine there.
static class TryValueOperandHoist
{
    static int _tmp;

    public static void Apply(JsonNode root) => Walk(root, BirScope.Empty);

    // Statement-list-valued keys (their elements are statements, not operand expressions).
    static readonly HashSet<string> StmtListKeys = new(StringComparer.Ordinal) { "body", "stmts", "finally" };
    // Keys the generic operand recurse must NOT descend into (they are statement lists, handled by Walk).
    static readonly HashSet<string> SkipGenericKeys = new(StringComparer.Ordinal) { "body", "stmts", "finally", "catches", "branches" };
    // Q4 of the four value questions (roster in bir-common/ValueStability.cs) — STACK-NEUTRAL: may this operand stay
    // in its slot when a LATER sibling hoists out of the expression? A hoist moves the sibling's evaluation to a
    // PRECEDING statement, so every operand left of it now runs after what used to run after it. That is invisible
    // only for an operand whose evaluation neither has an effect nor can fail — a literal, a local read, `this`.
    // Anything else (including a load that merely dereferences, so `arrayGet`/`field` too) is spilled to a temp by
    // SpillIfNeeded, which is what preserves left-to-right order.
    //
    // Not a restatement of Q2 (droppable) or Q3 (resume-stable): this one asks about ORDER against a hoisted try,
    // not about skipping an evaluation or about surviving a resume, and the three land differently per kind.
    static readonly HashSet<string> StackNeutralKinds = new(StringComparer.Ordinal) { "const", "local", "this" };

    static string K(JsonNode n) => (n as JsonObject)?["k"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // Post-order whole-tree walk: normalize INNER statement lists first (so a hoisted try-valueBlock's
    // stmts are already internally normalized), then this node's own statement lists. A declaration node
    // extends the scope with its params, and a statement sequence records each `var` for its SUBSEQUENT
    // siblings only (lexical block scoping) — the environment GuessType types a spilled temp from.
    static void Walk(JsonNode node, BirScope scope)
    {
        if (node is JsonObject obj)
        {
            // A TYPE node (`{t:…}`, docs/bir-cir-spec.md §1) declares nothing and carries no statement, so the walk
            // STOPS at it. That is not tidiness: a function type spells its parameter TYPES as `params`, the same
            // key a declaration spells its parameters with, so descending asked `Extend` for a scope at every
            // `(A) -> B` in the file — 2909 of them in `_Arrays.bir` alone, each copying the whole environment to
            // harvest nothing (a type node's `params` entries are types, which carry no `name` + `type` pair, so
            // none of them could ever enter a scope). Types and value nodes are disjoint by the schema — a `{t:…}`
            // node never carries `k` — so this cannot skip a node the hoist owns.
            if (TypeJson.IsType(obj)) return;
            // `Extend` returns `this` unless the node really has parameters, so after the skip above the only nodes
            // that pay for a scope copy are the ones that declare some (a method/accessor/constructor, a lambda).
            var child = scope.Extend(obj);
            foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value, child);
            NormalizeLists(obj, child);
        }
        else if (node is JsonArray arr)
        {
            var cur = scope;
            foreach (var it in arr)
            {
                if (it != null) Walk(it, cur);
                if (it is JsonObject vo && K(vo) == "var")
                {
                    if (ReferenceEquals(cur, scope)) cur = scope.Child();
                    cur.Declare(vo);
                }
            }
        }
    }

    static void NormalizeLists(JsonObject obj, BirScope scope)
    {
        foreach (var key in StmtListKeys)
            if (obj[key] is JsonArray a) obj[key] = HoistList(a, scope);
        if (obj["catches"] is JsonArray catches)
            foreach (var c in catches) if (c is JsonObject co && co["body"] is JsonArray cb) co["body"] = HoistList(cb, scope);
        if (obj["branches"] is JsonArray branches)
            foreach (var b in branches) if (b is JsonObject bo && bo["body"] is JsonArray bb) bo["body"] = HoistList(bb, scope);
    }

    // Rebuild a statement list: for each statement, extract operand-position try-valueBlocks from its
    // single-eval expression child(ren) into preceding statements, then emit `pre + stmt`. A `var` enters
    // scope for the statements AFTER it (its own initializer is evaluated before it exists).
    static JsonArray HoistList(JsonArray list, BirScope scope)
    {
        var outList = new JsonArray();
        var cur = scope;
        foreach (var stmt in list)
        {
            var clone = stmt?.DeepClone();
            if (clone is JsonObject so)
            {
                var pre = new List<JsonNode>();
                HoistStmtExprs(so, pre, cur);
                foreach (var p in pre) outList.Add(p);
            }
            outList.Add(clone);
            if (clone is JsonObject vo && K(vo) == "var")
            {
                if (ReferenceEquals(cur, scope)) cur = scope.Child();
                cur.Declare(vo);
            }
        }
        return outList;
    }

    // Only statements whose operand expression is evaluated EXACTLY ONCE at the statement's start are
    // eligible (var init, assignment value, expr-statement, return/throw value). Loop/if conditions are
    // NOT hoisted (a cond is re-evaluated per iteration / guards branch entry — hoisting would change
    // semantics); a try-valueBlock in such a position is out of scope for this normalization.
    static void HoistStmtExprs(JsonObject stmt, List<JsonNode> pre, BirScope scope)
    {
        switch (K(stmt))
        {
            case "var": HoistChild(stmt, "init", pre, scope); break;
            case "setLocal": HoistChild(stmt, "value", pre, scope); break;
            case "return": HoistChild(stmt, "value", pre, scope); break;
            case "throw": HoistChild(stmt, "value", pre, scope); break;
            case "exprStmt": HoistChild(stmt, "expr", pre, scope); break;
            case "setField": HoistNamedSlots(stmt, new[] { "recv", "value" }, atEmpty: true, pre, scope); break;
        }
    }

    static void HoistChild(JsonObject o, string key, List<JsonNode> pre, BirScope scope)
    {
        if (o[key] is JsonNode c) { o[key] = null; o[key] = HoistExpr(c, pre, atEmpty: true, scope); }
    }

    // Walk an operand expression, hoisting any operand-position try-valueBlock into `pre`. `atEmpty`
    // is true iff evaluating this node begins with an empty CLR eval stack.
    static JsonNode HoistExpr(JsonNode node, List<JsonNode> pre, bool atEmpty, BirScope scope)
    {
        if (node is not JsonObject o) return node;
        var k = K(o);

        if (k == "valueBlock" && IsTryValueBlock(o))
        {
            if (atEmpty) return o;              // safe: enters the try with an empty stack
            HoistTryValueBlock(o, pre);
            return o["result"] is JsonNode r ? r.DeepClone() : o;
        }

        if (k == "binOp" && o["lhs"] != null && o["rhs"] != null)
        {
            HoistOrdered(2, i => i == 0 ? o["lhs"] : o["rhs"], (i, v) => { if (i == 0) o["lhs"] = v; else o["rhs"] = v; }, atEmpty, pre, scope);
            return o;
        }
        if (k == "concat" && o["parts"] is JsonArray parts)
        {
            HoistOrdered(parts.Count, i => parts[i], (i, v) => parts[i] = v, atEmpty, pre, scope);
            return o;
        }
        if (o["args"] is JsonArray || o["recv"] != null)
        {
            HoistCallSlots(o, atEmpty, pre, scope);
            return o;
        }

        // Generic single-eval wrapper (cast, etc.): descend into object children (never a statement list).
        foreach (var kv in o.ToList())
            if (!SkipGenericKeys.Contains(kv.Key) && kv.Value is JsonObject)
            { o[kv.Key] = null; o[kv.Key] = HoistExpr(kv.Value, pre, atEmpty, scope); }
        return o;
    }

    // recv (if present) then args[..] as an ordered slot list.
    static void HoistCallSlots(JsonObject o, bool atEmpty, List<JsonNode> pre, BirScope scope)
    {
        var hasRecv = o["recv"] != null;
        var args = o["args"] as JsonArray;
        var argc = args?.Count ?? 0;
        var n = (hasRecv ? 1 : 0) + argc;
        JsonNode Get(int i) => (hasRecv && i == 0) ? o["recv"] : args[i - (hasRecv ? 1 : 0)];
        void Set(int i, JsonNode v) { if (hasRecv && i == 0) o["recv"] = v; else args[i - (hasRecv ? 1 : 0)] = v; }
        HoistOrdered(n, Get, Set, atEmpty, pre, scope);
    }

    static void HoistNamedSlots(JsonObject o, string[] keys, bool atEmpty, List<JsonNode> pre, BirScope scope)
    {
        var present = keys.Where(kk => o[kk] != null).ToArray();
        HoistOrdered(present.Length, i => o[present[i]], (i, v) => o[present[i]] = v, atEmpty, pre, scope);
    }

    // Core ordered-operand normalization. Evaluation is left-to-right: only the FIRST slot inherits the
    // enclosing empty-stack flag; every later slot begins with a non-empty stack. Any operand that
    // precedes a slot which hoists must itself be spilled (if side-effecting) so relative order holds.
    static void HoistOrdered(int n, Func<int, JsonNode> get, Action<int, JsonNode> set, bool atEmpty, List<JsonNode> pre, BirScope scope)
    {
        var lastHoist = -1;
        for (var i = 0; i < n; i++) if (WillHoist(get(i), i == 0 && atEmpty)) lastHoist = i;
        for (var i = 0; i < n; i++)
        {
            var node = get(i);
            set(i, null);
            var resolved = HoistExpr(node, pre, i == 0 && atEmpty, scope);
            if (i < lastHoist) resolved = SpillIfNeeded(resolved, pre, scope);
            set(i, resolved);
        }
    }

    // A slot evaluated before a hoisted try: a leftover leading try-valueBlock (safe-at-empty but now
    // reordered) is hoisted; any other side-effecting operand is spilled to a preceding temp; a
    // stack-neutral operand (const/local/this) is left untouched.
    static JsonNode SpillIfNeeded(JsonNode resolved, List<JsonNode> pre, BirScope scope)
    {
        if (resolved is JsonObject ro && K(ro) == "valueBlock" && IsTryValueBlock(ro))
        {
            HoistTryValueBlock(ro, pre);
            return ro["result"] is JsonNode r ? r.DeepClone() : resolved;
        }
        if (IsStackNeutral(resolved)) return resolved;
        var tmp = "dotkt$hoist" + System.Threading.Interlocked.Increment(ref _tmp);
        pre.Add(new JsonObject { ["k"] = "var", ["name"] = tmp, ["type"] = GuessType(resolved, scope), ["init"] = resolved.DeepClone() });
        return new JsonObject { ["k"] = "local", ["name"] = tmp };
    }

    // Move a try-valueBlock's `stmts` (the `var dotkt_tryvalN` + the `try`) into the preceding-statement
    // buffer; the caller replaces the node with `result` (a `local`).
    static void HoistTryValueBlock(JsonObject o, List<JsonNode> pre)
    {
        if (o["stmts"] is JsonArray st)
            foreach (var s in st) if (s != null) pre.Add(s.DeepClone());
    }

    // Would HoistExpr append any preceding statement for this subtree? (i.e. does it contain a
    // try-valueBlock that will be hoisted from a non-empty-stack position.)
    static bool WillHoist(JsonNode node, bool atEmpty)
    {
        if (node is not JsonObject o) return false;
        var k = K(o);
        if (k == "valueBlock" && IsTryValueBlock(o)) return !atEmpty;
        if (k == "binOp" && o["lhs"] != null && o["rhs"] != null)
            return WillHoist(o["lhs"], atEmpty) || WillHoist(o["rhs"], false);
        if (k == "concat" && o["parts"] is JsonArray parts)
        {
            for (var i = 0; i < parts.Count; i++) if (WillHoist(parts[i], i == 0 && atEmpty)) return true;
            return false;
        }
        if (o["args"] is JsonArray || o["recv"] != null)
        {
            var hasRecv = o["recv"] != null;
            if (hasRecv && WillHoist(o["recv"], atEmpty)) return true;
            if (o["args"] is JsonArray a)
                for (var i = 0; i < a.Count; i++) if (WillHoist(a[i], !hasRecv && i == 0 && atEmpty)) return true;
            return false;
        }
        foreach (var kv in o)
            if (!SkipGenericKeys.Contains(kv.Key) && kv.Value is JsonObject && WillHoist(kv.Value, atEmpty)) return true;
        return false;
    }

    static bool IsTryValueBlock(JsonObject o)
        => o["stmts"] is JsonArray st && st.Any(s => K(s) == "try") && o["result"] != null;

    static bool IsStackNeutral(JsonNode n) => n is JsonObject o && StackNeutralKinds.Contains(K(o));

    // The static type of a spilled temp (needed only for a side-effecting operand that precedes a hoisted try; the
    // stack-neutral operands left in place never spill). It is the SHARED answer — `StaticType.Surface` over the
    // node-local deriver every other spill site types its locals with — read against the lexical scope this walk
    // carries, so a spilled `local`/synthesized temp resolves to its declared type rather than to a box. Copying
    // whichever of `type`/`ret`/`dynRet` the node happened to carry, as this did, typed `f() + try{…}`'s spilled
    // `f()` as `kotlin.Any` and the emitted unbox faulted at runtime.
    //
    // KNOWN GAP — the `kotlin.Any` fallback is the last one in the spill family. Everywhere else an underivable slot
    // is an ERROR: `kotlin.Any` boxes a value type and hides a type the CLR would refuse, so a lowering that cannot
    // type a spill is reporting a DROP by an earlier one (SuspendColdLowering's evaluation-order spill and field
    // gate, CallEvalLowering's address pins). This hoist is plan-external and runs BEFORE the suspend lowering, so
    // it keeps the fallback until the change that errorizes the remaining `kotlin.Any` slots takes it; it now fires
    // only for a node the shared deriver itself cannot answer, which is a hole in the deriver.
    static JsonNode GuessType(JsonNode n, BirScope scope)
        => StaticType.Surface(n, scope) is DotKt.Bir.TypeNode t ? TypeJson.Write(t) : TypeJson.Fqn("kotlin.Any");
}
