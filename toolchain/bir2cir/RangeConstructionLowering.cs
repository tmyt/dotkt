using System.Text.Json.Nodes;

// Value-position range construction (#73 Phase 2b-1, the "range partial" continued). kotc emits the FAITHFUL
// `callInstance kotlin.<Prim>.rangeTo/rangeUntil(b)` for `a..b` / `a..<b` in value position (CLR primitives have no
// instance methods, but that is a CLR fact kotc stays out of). This pass MATERIALIZES the stdlib range class — the
// Kotlin<->CLR realization belongs in bir2cir:
//
//   a..b   (rangeTo)   -> new IntRange/LongRange/CharRange(a, b)
//   a..<b  (rangeUntil) -> new IntRange/LongRange/CharRange(a, b - 1)   (half-open: last = b - 1)
//
// Fires ONLY for the primitive OPERATOR (ownerType ∈ {kotlin.Byte,Short,Int,Long,Char}); a user-defined `rangeTo`
// (a custom ClosedRange) is a real method call, left untouched. Structured for-loops are counter-lowered in kotc's
// birForLoop (it intercepts the range at the IR level, so this member call is never emitted for a for-range).
//
// Runs FIRST in the per-file loop, right after RangeForLowering — before MemberCallSubstitution (whose Rule-4
// make-it-loud gate would otherwise refuse the unbound `kotlin.Int.rangeTo`) and before any type-erasing pass, so the
// realized `new` and its recv/arg values enter the ordinary CIR lowering pipeline at its canonical construction point.
static class RangeConstructionLowering
{
    public static void Apply(JsonNode node)
    {
        if (node is JsonObject o)
        {
            foreach (var kv in o) if (kv.Value != null) Apply(kv.Value);
            Rewrite(o);
        }
        else if (node is JsonArray a)
        {
            for (var i = 0; i < a.Count; i++)
                if (a[i] is JsonNode c)
                {
                    Apply(c);
                    if (a[i] is JsonObject co) Rewrite(co);
                }
        }
    }

    // In-place rewrite of a value-position rangeTo/rangeUntil callInstance to a `new <Range>` node.
    static void Rewrite(JsonObject o)
    {
        if (Str(o["k"]) != "callInstance") return;
        var method = Str(o["method"]);
        if (method != "rangeTo" && method != "rangeUntil") return;
        var owner = TypeJson.OwnerName(o["ownerType"]);
        var rangeType = owner switch
        {
            "kotlin.Byte" or "kotlin.Short" or "kotlin.Int" => "kotlin.ranges.IntRange",
            "kotlin.Long" => "kotlin.ranges.LongRange",
            "kotlin.Char" => "kotlin.ranges.CharRange",
            _ => null,
        };
        if (rangeType == null) return;
        if (o["recv"] is not JsonNode recv || o["args"] is not JsonArray args || args.Count != 1) return;
        var end = args[0];
        JsonNode endExpr = method == "rangeUntil"
            ? new JsonObject
            {
                ["k"] = "binOp",
                ["op"] = "-",
                ["lhs"] = end!.DeepClone(),
                ["rhs"] = new JsonObject
                {
                    ["k"] = "const",
                    ["type"] = TypeJson.Fqn(owner == "kotlin.Long" ? "kotlin.Long" : "kotlin.Int"),
                    ["value"] = 1,
                },
            }
            : end!.DeepClone();

        var recvClone = recv.DeepClone();
        foreach (var key in new System.Collections.Generic.List<string>(((System.Collections.Generic.IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
        o["k"] = "new";
        o["type"] = TypeJson.Fqn(rangeType);
        var endpointType = TypeJson.Fqn(rangeType switch
        {
            "kotlin.ranges.LongRange" => "kotlin.Long",
            "kotlin.ranges.CharRange" => "kotlin.Char",
            _ => "kotlin.Int",
        });
        o["argTypes"] = new JsonArray { endpointType.DeepClone(), endpointType };
        o["args"] = new JsonArray { recvClone, endExpr };
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
