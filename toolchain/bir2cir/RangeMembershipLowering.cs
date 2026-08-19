using System.Collections.Generic;
using System.Text.Json.Nodes;
using DotKt.Bir;

// RANGE MEMBERSHIP `x in a..b` (#73 M2). kotc emits the FAITHFUL `contains` member call on the range receiver
// (`callInstance kotlin.ranges.IntRange.contains(x)`, recv = the un-materialized `a.rangeTo(b)` / `a until b`) — by
// identity, NO comparison synthesis and NO FQN gate. A USER type with `operator fun rangeTo`+`contains` therefore
// stays a real method dispatch (kotc's old bare-name lowering MISCOMPILED it to primitive comparisons). This pass
// lowers the membership to the short-circuit fast path — FQN-keyed, so it fires ONLY for a stdlib PRIMITIVE range:
//
//   x in a..b   (rangeTo)                 -> (x >= a && x <= b)
//   x in a..<b  (rangeUntil) / a until b  -> (x >= a && x <  b)
//
// EVALUATION ORDER IS PART OF THE MEANING, and the fast path must not change it. `x in a()..b()` is
// `(a()..b()).contains(x)`: the RANGE is constructed first, so `a()` and `b()` BOTH run — unconditionally, in that
// order — and only then `x`. The short-circuit `cond` reverses that on its own (the `<= b` leg sits under the `>= a`
// test, and the subject renders into both legs), so the three operands are BOUND HERE, each exactly once, in Kotlin
// order lo, hi, subject:
//
//   valueBlock { var __rangelo$N = a; var __rangehi$N = b; var __rangein$N = x
//                result = if (__rangein$N >= __rangelo$N) __rangein$N <op> __rangehi$N else false }
//
// An operand is spliced in place INSTEAD of bound only when `ValueStability.IsReReadable` accepts it (Q1, the roster
// in bir-common/ValueStability.cs: `const`/`this`/`bindRef`). Those are exactly the operands whose value cannot
// depend on when they are read, so moving the read later — or duplicating it across both legs — is unobservable.
// Everything else, INCLUDING a plain `local` (BIR does not distinguish `val` from `var`, and a sibling bound can
// assign it), gets a temp. So `5 in 1..10` still lowers to bare comparisons with no temps at all.
//
// The temp's declared type is the FRONTEND-RESOLVED slot type, never re-derived: the bounds take the `rangeTo`/
// `until` receiver+parameter types, the subject takes `contains`' parameter type (`IntRange.contains(Int)`,
// `LongRange.contains(Long)`, `CharRange.contains(Char)`). A mis-typed Long/Char temp is invalid IL, so if a needed
// slot type is absent we leave the membership alone and let the real `contains` dispatch handle it — which evaluates
// both bounds and the subject in the same order anyway.
//
// Gate: the `contains` owner is `kotlin.ranges.{IntRange,LongRange,CharRange}` AND the recv is the primitive range
// construction (`callInstance kotlin.<Prim>.rangeTo/rangeUntil` or the `callStatic until` extension over a primitive).
// A variable-held range (`val r = a..b; x in r`) has a `local` recv -> not matched -> the real IntRange.contains
// binding handles it, exactly as kotc's old `range as? IrCall` gate required an inline range call. `downTo`/`step`
// build an `IntProgression`, not a `*Range`, so they never reach here either.
//
// Runs BEFORE RangeConstructionLowering (which would otherwise materialize the recv rangeTo into `new IntRange`) and
// before MemberCallSubstitution (which would bind the unresolved `contains`/`until`) — so the produced binOp/cond
// nodes flow through every downstream pass exactly as kotc's retired call-site lowering did.
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
        if (ExtractBounds(recv, localTopLevelFns, appBuild) is not { } bounds) return;
        var (lo, hi, loType, hiType, cmp) = bounds;
        // The subject's slot is `contains`' parameter = the range element (IntRange.contains(Int),
        // LongRange.contains(Long), CharRange.contains(Char)). kotc emits the overload sig unconditionally.
        var xType = (o["sig"] as JsonArray) is { Count: > 0 } sig ? sig[0] : null;

        // Bind lo, hi and the subject in KOTLIN order — the range's two bounds, then the membership subject — each
        // exactly once. An `IsReReadable` operand (Q1) is spliced instead: its value cannot change with when it is
        // read, so the `cond` may read it late and twice. Anything else gets a temp evaluated up front.
        var stmts = new JsonArray();
        var site = System.Threading.Interlocked.Increment(ref _counter);
        var readLo = Bind(lo, loType, "lo", site, stmts);
        var readHi = Bind(hi, hiType, "hi", site, stmts);
        var readX = Bind(x, xType, "in", site, stmts);
        // The fast path needs a declaration-owned Type for every temporary it introduces. If the current input is
        // incomplete, preserve the original contains call so the owning validation/resolution path reports it.
        if (readLo == null || readHi == null || readX == null) return;

        var core = new JsonObject
        {
            ["k"] = "cond",
            ["cond"] = new JsonObject { ["k"] = "binOp", ["op"] = ">=", ["lhs"] = readX.DeepClone(), ["rhs"] = readLo.DeepClone() },
            ["then"] = new JsonObject { ["k"] = "binOp", ["op"] = cmp, ["lhs"] = readX.DeepClone(), ["rhs"] = readHi.DeepClone() },
            ["else"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Boolean"), ["value"] = false },
        };

        JsonNode repl = stmts.Count == 0
            ? core
            : new JsonObject { ["k"] = "valueBlock", ["stmts"] = stmts, ["result"] = core };

        foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
        foreach (var kv in (JsonObject)repl) o[kv.Key] = kv.Value?.DeepClone();
    }

    // One operand of the membership, rendered so the `cond` may read it: the operand itself when re-reading it is
    // order-immune, else a read of a fresh `__range<role>$N` temp appended to `stmts` (so it evaluates where Kotlin
    // evaluates it). Null iff a temporary is needed but the current structured slot type is absent or malformed.
    static JsonNode Bind(JsonNode value, JsonNode slotType, string role, int site, JsonArray stmts)
    {
        if (ValueStability.IsReReadable(value)) return value;
        if (!TypeJson.IsType(slotType)) return null;
        var name = "__range" + role + "$" + site;
        stmts.Add(new JsonObject { ["k"] = "var", ["name"] = name, ["type"] = slotType.DeepClone(), ["init"] = value.DeepClone() });
        return new JsonObject { ["k"] = "local", ["name"] = name };
    }

    // The (lo, hi, their declared slot types, comparison-op) of a PRIMITIVE range construction used as a `contains`
    // receiver, or null if the recv is not one (a user rangeTo, a variable-held range, a non-primitive receiver).
    static (JsonNode Lo, JsonNode Hi, JsonNode LoType, JsonNode HiType, string Cmp)? ExtractBounds(
        JsonObject recv, ISet<string> localTopLevelFns, bool appBuild)
    {
        var k = Str(recv["k"]);
        var method = Str(recv["method"]);
        // Member operator: `a.rangeTo(b)` / `a.rangeUntil(b)` -> recv=a, args[0]=b. The slots are the operator's own
        // receiver type (`ownerType`, the primitive this gate already read) and its single parameter (`sig[0]`).
        if (k == "callInstance" && (method == "rangeTo" || method == "rangeUntil"))
        {
            if (recv["ownerType"] is not JsonNode loType || !PrimReceivers.Contains(TypeJson.OwnerName(loType) ?? "")) return null;
            if (recv["recv"] is not JsonNode lo) return null;
            if (recv["args"] is not JsonArray a || a.Count != 1 || a[0] is not JsonNode hi) return null;
            var hiType = (recv["sig"] as JsonArray) is { Count: > 0 } rsig ? rsig[0] : null;
            return (lo, hi, loType, hiType, method == "rangeUntil" ? "<" : "<=");
        }
        // Infix extension: `a until b` -> callStatic owner:null method:until sig:[recvPrim, boundPrim] args:[a, b].
        if (k == "callStatic" && recv["owner"] == null && method == "until")
        {
            // A user top-level `fun until(...)` shadow (app build) is NOT the kotlin.ranges intrinsic — leave it.
            if (appBuild && localTopLevelFns.Contains("until")) return null;
            if (recv["sig"] is not JsonArray sig || sig.Count == 0
                || !PrimReceivers.Contains(TypeJson.OwnerName(sig[0]) ?? "")) return null;
            if (recv["args"] is not JsonArray a || a.Count != 2 || a[0] is not JsonNode lo || a[1] is not JsonNode hi) return null;
            return (lo, hi, sig[0], sig.Count > 1 ? sig[1] : null, "<");
        }
        return null;
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
