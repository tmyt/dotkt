using System.Text.Json.Nodes;
using DotKt.Bir;

// Facts erased by CLR type projection, transported in the declaration-owned generic frame.
// Bit zero is nullability; the remaining value is the nominal collection classifier. These
// codes are consumed by the trusted stdlib kotlinCollectionMatches helper, not by ilemit.
static class KotlinTypeWitness
{
    internal const string OperandKey = "kotlinTypeWitness";
    // A Kotlin unchecked cast to an ordinary T does not inspect T's Kotlin classifier.
    // kotc retains the checked reified operand fact through lifting/inlining; a method's
    // reified parameter annotation alone does not select this ABI.
    internal static bool NeedsWitness(JsonObject operation) =>
        operation["k"]?.GetValue<string>() == "isInst"
        || operation["k"]?.GetValue<string>() is "cast" or "isInstRef"
            && operation["reifiedTypeOperand"] is JsonValue value
            && value.TryGetValue<bool>(out var reified) && reified;
    internal static TypeNode.Tv Variable(TypeNode type) => type switch {
        TypeNode.Nullable n => Variable(n.Of),
        TypeNode.Oblivious o => Variable(o.Of),
        TypeNode.Tv tv => tv,
        _ => null,
    };

    internal static int Flags(TypeNode type) => type switch {
        TypeNode.Nullable n => Flags(n.Of) | 1,
        TypeNode.Oblivious o => Flags(o.Of),
        TypeNode.Fqn f => f.Name switch {
            "kotlin.collections.Collection" => 2,
            "kotlin.collections.MutableCollection" => 4,
            "kotlin.collections.List" => 6,
            "kotlin.collections.MutableList" => 8,
            "kotlin.collections.Set" => 10,
            "kotlin.collections.MutableSet" => 12,
            "kotlin.collections.Iterable" => 14,
            "kotlin.collections.MutableIterable" => 16,
            _ => 0,
        },
        _ => 0,
    };

    internal static JsonObject Constant(int flags) => new() {
        ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Int"), ["value"] = flags,
    };

    internal static JsonObject Binary(string op, JsonNode left, JsonNode right) => new() {
        ["k"] = "binOp", ["op"] = op, ["lhs"] = left, ["rhs"] = right,
    };

    internal static JsonObject AllowsNull(JsonNode witness) =>
        Binary("!=", Binary("&", witness.DeepClone(), Constant(1)), Constant(0));
}
