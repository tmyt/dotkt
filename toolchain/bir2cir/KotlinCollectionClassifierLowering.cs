using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A compiler-provided CLR storage face is not evidence of Kotlin MutableCollection membership.
// Apply the declaration-owned nominal guard while the tested Kotlin classifier is still explicit.
static class KotlinCollectionClassifierLowering
{
    const string RuntimeOwner = "DotKt.Runtime.CompilerServices.StarProjectionRuntimeKt";
    const string Candidate = "kotlinCollectionCandidate";
    const string CastCandidate = "kotlinCollectionCastCandidate";

    public static void Apply(JsonNode node)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array) Apply(child);
            return;
        }
        if (node is not JsonObject obj) return;
        foreach (var child in obj.Select(pair => pair.Value).ToArray()) Apply(child);
        if (Text(obj["k"]) is not ("isInst" or "isInstRef" or "cast") || obj["e"] is not JsonObject operand) return;
        obj.Remove("reifiedTypeOperand");
        var witness = obj[KotlinTypeWitness.OperandKey]?.DeepClone();
        obj.Remove(KotlinTypeWitness.OperandKey);
        if (witness == null)
        {
            var flags = KotlinTypeWitness.Flags(TypeJson.Read(obj["type"]));
            if ((flags & ~1) == 0) return;
            witness = KotlinTypeWitness.Constant(flags);
        }
        var helper = Text(obj["k"]) == "cast" ? CastCandidate : Candidate;
        if (Text(operand["k"]) == "callStatic" && Text(operand["method"]) == helper
            && TypeJson.OwnerName(operand["owner"]) == RuntimeOwner) return;
        var nullableObject = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
        obj["e"] = new JsonObject {
            ["k"] = "callStatic", ["owner"] = TypeJson.Fqn(RuntimeOwner), ["method"] = helper,
            ["sig"] = new JsonArray(TypeJson.Write(nullableObject), TypeJson.Fqn("kotlin.Int")),
            ["ret"] = TypeJson.Write(nullableObject),
            ["args"] = new JsonArray(operand.DeepClone(), witness),
        };
    }

    static string Text(JsonNode node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
