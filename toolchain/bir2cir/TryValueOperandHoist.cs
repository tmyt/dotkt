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
// Kotlin-aware rescheduling). Mirroring the suspend-spill precedent (SuspendColdLowering), we HOIST such
// a block out of the operand position to PRECEDING statements: its inline statements (the var + the try)
// become statements of the enclosing statement, and the operand slot is replaced with the block's
// `result`. Left-to-right Kotlin evaluation order is preserved: any side-effecting operand evaluated
// BEFORE a hoisted try is itself spilled to a preceding temp first.
//
// THE HAZARD IS THE BLOCK, NOT kotc's SPELLING OF IT. kotc's try-value form has the `try` as a direct
// statement, but it is not the only producer of an inline block in an operand slot: several lowerings
// materialise an operand into a MINTED `valueBlock` whose `var` initializer is then a try-valued
// expression — a call-evaluation plan's bindings (CallEvalLowering, and MemberCallSubstitution for a
// constructor argument the CLR mapping maps away), RangeMembershipLowering's bounds, PreconditionLowering's
// subject, NetInteropBinding's adapters. The stack is just as non-empty when those statements run, so the
// hoist looks for a `try` ANYWHERE the block runs inline (`RunsATry`) rather than only at its top level.
// Missing that was an InvalidProgramException in shapes like `f("x", g(try { 1 } catch { 2 }) in 1..5)`.
//
// A hazardous block that is ALREADY at an empty-stack position (the first thing evaluated in its
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

    // Value-list keys whose elements ilemit evaluates while a construction receiver (and, for an array, its index)
    // is already on the CLR stack. These are deliberately keyed by NODE KIND: arrays such as `sig`, `argTypes`,
    // `typeArgs`, and declaration `params` are type/signature vocabulary, not ordered value operands. A try in even
    // the FIRST listed element therefore starts non-empty and must move. `newMap.entries` is handled separately below
    // because each entry contributes two ordered operands (`key`, then `value`).
    static readonly Dictionary<string, string> ConstructionValueLists = new(StringComparer.Ordinal)
    {
        ["newArray"] = "elems",
        ["newList"] = "elems",
        ["newSet"] = "elems",
    };

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

        // A hazardous block at a NON-EMPTY stack moves; one at an empty stack is already safe and stays. Being safe
        // is not being finished, though: the block's own subexpressions still need normalizing, so the empty-stack
        // case FALLS THROUGH to the generic descent below rather than returning. Returning early there is what made
        // widening the predicate a regression — a `when` whose subject is a try became "hazardous", and the descent
        // that used to reach a try in one of its branches' operand slots stopped happening.
        if (k == "valueBlock" && IsTryValueBlock(o) && !atEmpty)
        {
            HoistTryValueBlock(o, pre);
            // The RESULT is still evaluated in the slot, at the same non-empty stack, so a block nested there is
            // hoisted in turn — after this block's own statements, which is the order it ran in.
            return o["result"] is JsonNode r ? HoistExpr(r.DeepClone(), pre, atEmpty, scope) : o;
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
        if (k != null && ConstructionValueLists.TryGetValue(k, out var valueListKey)
            && o[valueListKey] is JsonArray values)
        {
            // ilemit has already pushed the new array/list/set (and an array index) before it evaluates an element.
            // Consequently no element inherits the enclosing expression's empty-stack position, including element 0.
            HoistOrdered(values.Count, i => values[i], (i, v) => values[i] = v,
                atEmpty: false, pre, scope, spillAllAfterHoist: true);
            return o;
        }
        if (k == "newMap" && o["entries"] is JsonArray entries)
        {
            HoistMapEntries(entries, pre, scope);
            return o;
        }
        if (k == "spreadConcat" && o["parts"] is JsonArray spreadParts)
        {
            HoistSpreadParts(spreadParts, pre, scope);
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

    // Dictionary construction evaluates entry 0 key, entry 0 value, entry 1 key, entry 1 value, ... while the
    // dictionary receiver is live on the stack. Flatten that exact stream so a try-valued key/value hoists and every
    // side-effecting value before a later hoist spills in Kotlin order. Treating an entry object as one operand would
    // lose the key-before-value boundary and would ask SpillType to type a schema container rather than an expression.
    static void HoistMapEntries(JsonArray entries, List<JsonNode> pre, BirScope scope)
    {
        var slots = new List<(JsonObject Entry, string Key)>();
        foreach (var entry in entries)
            if (entry is JsonObject eo)
            {
                if (eo["key"] != null) slots.Add((eo, "key"));
                if (eo["value"] != null) slots.Add((eo, "value"));
            }
        HoistOrdered(slots.Count,
            i => slots[i].Entry[slots[i].Key],
            (i, v) => slots[i].Entry[slots[i].Key] = v,
            atEmpty: false, pre, scope, spillAllAfterHoist: true);
    }

    // A spread-concatenated vararg is accumulated through `List.Add`/`AddRange`: ilemit loads that accumulator before
    // each part's expression. The value stream is `parts[i].e`; `spread` is only the already-decided Add/AddRange
    // selector and is not an expression. As with the factory constructions above, all source arguments are evaluated
    // before the call body consumes them, so a hoist materializes the whole non-neutral stream first.
    static void HoistSpreadParts(JsonArray parts, List<JsonNode> pre, BirScope scope)
    {
        var slots = parts.OfType<JsonObject>().Where(p => p["e"] != null).ToList();
        HoistOrdered(slots.Count,
            i => slots[i]["e"],
            (i, v) => slots[i]["e"] = v,
            atEmpty: false, pre, scope, spillAllAfterHoist: true);
    }

    // Core ordered-operand normalization. Evaluation is left-to-right: only the FIRST slot inherits the
    // enclosing empty-stack flag; every later slot begins with a non-empty stack. Any operand that
    // precedes a slot which hoists must itself be spilled (if side-effecting) so relative order holds.
    static void HoistOrdered(int n, Func<int, JsonNode> get, Action<int, JsonNode> set, bool atEmpty,
                             List<JsonNode> pre, BirScope scope, bool spillAllAfterHoist = false)
    {
        var lastHoist = -1;
        for (var i = 0; i < n; i++) if (WillHoist(get(i), i == 0 && atEmpty)) lastHoist = i;
        for (var i = 0; i < n; i++)
        {
            var node = get(i);
            set(i, null);
            var resolved = HoistExpr(node, pre, i == 0 && atEmpty, scope);
            // Ordinary operators/calls only need the prefix: evaluation resumes in their slots after the hoisted
            // block. A construction is different — its emitter consumes each value immediately through stelem/Add/
            // set_Item, while the Kotlin factory/vararg call evaluated EVERY source argument before that body began.
            // Once any protected region moves, materialize the whole non-neutral stream before construction so a
            // suffix expression cannot slide behind an earlier element's user-observable hash/equality operation.
            if (lastHoist >= 0 && (spillAllAfterHoist || i < lastHoist))
                resolved = SpillIfNeeded(resolved, pre, scope);
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
            // …and then the block's RESULT is spilled like any other operand. Moving only the statements would
            // leave the result to be evaluated after the LATER slot's hoisted try, which is the reorder this
            // function exists to prevent. It was invisible while the only such block was kotc's own try-value
            // form, whose result is a stack-neutral `local`; a block a lowering mints has an arbitrary result —
            // a `newClr` whose constructor can throw — and that one has to move with its statements.
            return ro["result"] is JsonNode r ? SpillIfNeeded(r.DeepClone(), pre, scope) : resolved;
        }
        if (IsStackNeutral(resolved)) return resolved;
        var tmp = "dotkt$hoist" + System.Threading.Interlocked.Increment(ref _tmp);
        pre.Add(new JsonObject { ["k"] = "var", ["name"] = tmp, ["type"] = SpillType(resolved, scope), ["init"] = resolved.DeepClone() });
        return new JsonObject { ["k"] = "local", ["name"] = tmp };
    }

    // Move a hazardous block's INLINE statements into the preceding-statement buffer; the caller replaces
    // the node with its `result`. A `valueBlock` may carry either statement list and its consumers run
    // `stmts` and then `body` (CallEvalLowering.MergeNestedResult), so they are moved in that order.
    static void HoistTryValueBlock(JsonObject o, List<JsonNode> pre)
    {
        foreach (var key in new[] { "stmts", "body" })
            if (o[key] is JsonArray st)
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
        if (k != null && ConstructionValueLists.TryGetValue(k, out var valueListKey)
            && o[valueListKey] is JsonArray values)
        {
            foreach (var value in values) if (WillHoist(value, atEmpty: false)) return true;
            return false;
        }
        if (k == "newMap" && o["entries"] is JsonArray entries)
        {
            foreach (var entry in entries)
                if (entry is JsonObject eo
                    && (WillHoist(eo["key"], atEmpty: false) || WillHoist(eo["value"], atEmpty: false)))
                    return true;
            return false;
        }
        if (k == "spreadConcat" && o["parts"] is JsonArray spreadParts)
        {
            foreach (var part in spreadParts)
                if (part is JsonObject po && WillHoist(po["e"], atEmpty: false)) return true;
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

    // A `valueBlock` that ENTERS A PROTECTED REGION while the enclosing expression's operands are on the stack.
    // Every part of it except the enclosing slot itself runs inline — the two statement lists and then the result —
    // so a `try` in any of them is the same hazard, whether it is kotc's own top-level try statement or a
    // try-valued initializer a later lowering wrapped in a minted block.
    static bool IsTryValueBlock(JsonObject o)
        => o["result"] != null && (RunsATry(o["stmts"]) || RunsATry(o["body"]) || RunsATry(o["result"]));

    // Does evaluating this subtree ENTER a protected region here, on the current evaluation stack? The walk stops at
    // a nested DECLARATION (anything carrying `params` — a lambda, a local function): its body runs when it is
    // invoked, on an evaluation stack of its own, so a `try` in there is not this expression's hazard. It also stops
    // at a type node, which carries no code (and spells its parameter TYPES as `params`).
    static bool RunsATry(JsonNode n)
    {
        switch (n)
        {
            case JsonObject o:
                if (TypeJson.IsType(o)) return false;
                if (K(o) == "try") return true;
                if (o["params"] is JsonArray) return false;
                foreach (var kv in o) if (kv.Value != null && RunsATry(kv.Value)) return true;
                return false;
            case JsonArray a:
                foreach (var it in a) if (it != null && RunsATry(it)) return true;
                return false;
            default:
                return false;
        }
    }

    static bool IsStackNeutral(JsonNode n) => n is JsonObject o && StackNeutralKinds.Contains(K(o));

    // The static type of a spilled temp (needed only for a side-effecting operand that precedes a hoisted try; the
    // stack-neutral operands left in place never spill). It is the SHARED answer — `StaticType.Surface` over the
    // node-local deriver every other spill site types its locals with — read against the lexical scope this walk
    // carries, so a spilled `local`/synthesized temp resolves to its declared type rather than to a box. Copying
    // whichever of `type`/`ret`/`dynRet` the node happened to carry, as this did, typed `f() + try{…}`'s spilled
    // `f()` as `kotlin.Any` and the emitted unbox faulted at runtime.
    //
    // An underivable spill is an ERROR here, the same contract as every other spill site (SuspendOperandPlan's
    // binding typing, CallEvalLowering's address pins): `kotlin.Any` boxes a value type and hides a type the CLR
    // would refuse, so substituting it turns an earlier layer's DROP into a runtime fault instead of a diagnostic.
    // A null from the shared deriver at this point is a hole in the DERIVER, and the message says so.
    static JsonNode SpillType(JsonNode n, BirScope scope)
        => StaticType.Surface(n, scope) is DotKt.Bir.TypeNode t
            ? TypeJson.Write(t)
            : throw new System.NotSupportedException(
                $"bir2cir: try-value hoist: the `{K(n) ?? "?"}` operand evaluated before a hoisted `try` expression "
                + "carries no static type, so the temporary that holds its value across the hoist would be untyped. "
                + "An earlier lowering dropped the operand's type, or its node kind needs an arm in "
                + "bir-common/NodeType.cs.");
}
