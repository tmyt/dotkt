using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A Kotlin Any.equals call has one meaning regardless of its receiver's static
// type. Native CLR Equals calls and nonvirtual super calls retain their target.
static class KotlinEqualityLowering
{
    internal static JsonObject Call(JsonNode left, JsonNode right, bool nullSafe) => new()
    {
        ["k"] = "callStatic",
        ["owner"] = TypeJson.Fqn("DotKt.Runtime.CompilerServices.KotlinEqualityKt"),
        ["method"] = nullSafe ? "kotlinEquals" : "kotlinInstanceEquals",
        ["sig"] = new JsonArray { AnyNullable(), AnyNullable() },
        ["args"] = new JsonArray { left?.DeepClone(), right?.DeepClone() },
    };

    static JsonNode AnyNullable() => new JsonObject { ["t"] = "nullable", ["of"] = TypeJson.Fqn("kotlin.Any") };

    static bool IsNull(JsonNode value) => value is JsonObject obj
        && obj["k"]?.GetValue<string>() == "const" && obj["value"] == null;

    internal static JsonObject NullComparison(JsonNode left, JsonNode right) =>
        IsNull(left) || IsNull(right) ? new JsonObject {
            ["k"] = "binOp", ["op"] = "===",
            ["lhs"] = new JsonObject { ["k"] = "cast", ["type"] = AnyNullable(), ["e"] = left?.DeepClone() },
            ["rhs"] = new JsonObject { ["k"] = "cast", ["type"] = AnyNullable(), ["e"] = right?.DeepClone() },
        } : null;

    internal static void Apply(JsonNode node, bool preserveSuperIntent = false)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array) if (child != null) Apply(child, preserveSuperIntent);
            return;
        }
        if (node is not JsonObject obj) return;
        foreach (var child in obj.Select(pair => pair.Value).ToArray()) if (child != null) Apply(child, preserveSuperIntent);
        var kind = obj["k"]?.GetValue<string>();
        // Compiler-authored null guards (including !!) test a reference, not
        // virtual equality. Calling Equals here would observe the receiver twice.
        if (kind == "objEq" && NullComparison(obj["lhs"], obj["rhs"]) is JsonObject check)
        {
            Replace(obj, check);
            return;
        }
        if (kind is not ("objMethod" or "callInstance")) return;
        if (obj["method"]?.GetValue<string>() != "Equals") return;
        var semanticCall = kind == "objMethod" || (kind == "callInstance" && obj["anySlot"]?.GetValue<bool>() == true);
        if (!semanticCall) return;
        if (preserveSuperIntent && obj["super"]?.GetValue<bool>() == true) return;
        obj.Remove("anySlot");
        if (obj["super"]?.GetValue<bool>() == true) return;
        var right = kind == "objMethod" ? obj["arg"] : (obj["args"] as JsonArray)?[0];
        var replacement = Call(obj["recv"], right, nullSafe: false);
        Replace(obj, replacement);
    }

    static void Replace(JsonObject obj, JsonObject replacement)
    {
        obj.Clear();
        foreach (var pair in replacement.ToArray()) obj[pair.Key] = pair.Value?.DeepClone();
    }
}
