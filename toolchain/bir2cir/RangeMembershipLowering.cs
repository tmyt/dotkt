using System.Collections.Generic;
using System.Text.Json.Nodes;

// RANGE MEMBERSHIP `x in a..b` (#73 M2). kotc emits the FAITHFUL `contains` member call on the range receiver
// (`callInstance kotlin.ranges.IntRange.contains(x)`, recv = the un-materialized `a.rangeTo(b)` / `a until b`) — by
// identity, NO comparison synthesis and NO FQN gate. A USER type with `operator fun rangeTo`+`contains` therefore
// stays a real method dispatch (kotc's old bare-name lowering MISCOMPILED it to primitive comparisons). This pass
// lowers the membership to the short-circuit fast path — FQN-keyed, so it fires ONLY for a stdlib PRIMITIVE range:
//
//   x in a..b   (rangeTo)                 -> (x >= a && x <= b)
//   x in a..<b  (rangeUntil) / a until b  -> (x >= a && x <  b)
//
// The subject `x` renders into BOTH comparison legs, so a side-effecting operand is bound ONCE via a temp local
// (bindOnce): a `const`/`local` operand splices directly, anything else becomes a `valueBlock { var __rangein$N = x }`.
// The bounds a/b each appear in exactly one leg, so they need no temp.
//
// Gate: the `contains` owner is `kotlin.ranges.{IntRange,LongRange,CharRange}` AND the recv is the primitive range
// construction (`callInstance kotlin.<Prim>.rangeTo/rangeUntil` or the `callStatic until` extension over a primitive).
// A variable-held range (`val r = a..b; x in r`) has a `local` recv -> not matched -> the real IntRange.contains
// binding handles it, exactly as kotc's old `range as? IrCall` gate required an inline range call.
//
// Runs BEFORE RangeConstructionLowering (which would otherwise materialize the recv rangeTo into `new IntRange`) and
// before MemberCallSubstitution (which would bind the unresolved `contains`/`until`) — so the produced binOp/cond
// nodes flow through every downstream pass exactly as kotc's retired call-site lowering did (byte-identical IL).
static class RangeMembershipLowering
{
    static int _counter;

    static readonly HashSet<string> RangeOwners = new(System.StringComparer.Ordinal)
    { "kotlin.ranges.IntRange", "kotlin.ranges.LongRange", "kotlin.ranges.CharRange" };

    static readonly HashSet<string> PrimReceivers = new(System.StringComparer.Ordinal)
    { "kotlin.Byte", "kotlin.Short", "kotlin.Int", "kotlin.Long", "kotlin.Char" };

    public static void Apply(JsonNode node, ISet<string> localTopLevelFns, bool appBuild)
    {
        if (node is JsonObject o)
        {
            foreach (var kv in o) if (kv.Value != null) Apply(kv.Value, localTopLevelFns, appBuild);
            Rewrite(o, localTopLevelFns, appBuild);
        }
        else if (node is JsonArray a)
        {
            for (var i = 0; i < a.Count; i++)
                if (a[i] is JsonNode c)
                {
                    Apply(c, localTopLevelFns, appBuild);
                    if (a[i] is JsonObject co) Rewrite(co, localTopLevelFns, appBuild);
                }
        }
    }

    static void Rewrite(JsonObject o, ISet<string> localTopLevelFns, bool appBuild)
    {
        if (Str(o["k"]) != "callInstance" || Str(o["method"]) != "contains") return;
        if (TypeJson.OwnerName(o["ownerType"]) is not string owner || !RangeOwners.Contains(owner)) return;
        if (o["args"] is not JsonArray cargs || cargs.Count != 1 || cargs[0] is not JsonNode x) return;
        if (o["recv"] is not JsonObject recv) return;
        if (ExtractBounds(recv, localTopLevelFns, appBuild) is not (JsonNode lo, JsonNode hi, string cmp)) return;

        // bindOnce: a side-effecting subject must not run in both legs. A const / plain local / `this` read is stable
        // (side-effect-free, deterministic) — mirrors kotc's bindOnce (const | non-refcell param/val | receiver).
        var subjKind = Str(x["k"]);
        var stable = subjKind is "const" or "local" or "this";
        JsonNode read, tempStmt = null;
        if (stable)
            read = x;
        else
        {
            // x is the `contains` parameter type = the range element (IntRange.contains(Int), LongRange.contains(Long),
            // CharRange.contains(Char)). It is always attached (kotc emits the overload sig unconditionally); if it is
            // ever absent we cannot type the temp correctly (a mis-typed Long/Char temp is invalid IL), so bail to the
            // real `contains` dispatch rather than guess.
            if ((o["sig"] as JsonArray) is not { Count: > 0 } sig || sig[0] is not JsonNode xType) return;
            var name = "__rangein$" + System.Threading.Interlocked.Increment(ref _counter);
            tempStmt = new JsonObject { ["k"] = "var", ["name"] = name, ["type"] = xType.DeepClone(), ["init"] = x.DeepClone() };
            read = new JsonObject { ["k"] = "local", ["name"] = name };
        }

        var core = new JsonObject
        {
            ["k"] = "cond",
            ["cond"] = new JsonObject { ["k"] = "binOp", ["op"] = ">=", ["lhs"] = read.DeepClone(), ["rhs"] = lo.DeepClone() },
            ["then"] = new JsonObject { ["k"] = "binOp", ["op"] = cmp, ["lhs"] = read.DeepClone(), ["rhs"] = hi.DeepClone() },
            ["else"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Boolean"), ["value"] = false },
        };

        JsonNode repl = tempStmt == null
            ? core
            : new JsonObject { ["k"] = "valueBlock", ["stmts"] = new JsonArray { tempStmt }, ["result"] = core };

        foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
        foreach (var kv in (JsonObject)repl) o[kv.Key] = kv.Value?.DeepClone();
    }

    // The (lo, hi, comparison-op) of a PRIMITIVE range construction used as a `contains` receiver, or null if the recv
    // is not one (a user rangeTo, a variable-held range, a non-primitive receiver).
    static (JsonNode Lo, JsonNode Hi, string Cmp)? ExtractBounds(JsonObject recv, ISet<string> localTopLevelFns, bool appBuild)
    {
        var k = Str(recv["k"]);
        var method = Str(recv["method"]);
        // Member operator: `a.rangeTo(b)` / `a.rangeUntil(b)` -> recv=a, args[0]=b.
        if (k == "callInstance" && (method == "rangeTo" || method == "rangeUntil"))
        {
            if (!PrimReceivers.Contains(TypeJson.OwnerName(recv["ownerType"]) ?? "")) return null;
            if (recv["recv"] is not JsonNode lo) return null;
            if (recv["args"] is not JsonArray a || a.Count != 1 || a[0] is not JsonNode hi) return null;
            return (lo, hi, method == "rangeUntil" ? "<" : "<=");
        }
        // Infix extension: `a until b` -> callStatic owner:null method:until sig:[recvPrim, boundPrim] args:[a, b].
        if (k == "callStatic" && recv["owner"] == null && method == "until")
        {
            // A user top-level `fun until(...)` shadow (app build) is NOT the kotlin.ranges intrinsic — leave it.
            if (appBuild && localTopLevelFns.Contains("until")) return null;
            if (recv["sig"] is not JsonArray sig || sig.Count == 0
                || !PrimReceivers.Contains(TypeJson.OwnerName(sig[0]) ?? "")) return null;
            if (recv["args"] is not JsonArray a || a.Count != 2 || a[0] is not JsonNode lo || a[1] is not JsonNode hi) return null;
            return (lo, hi, "<");
        }
        return null;
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
