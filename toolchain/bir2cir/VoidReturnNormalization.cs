using System.Text.Json.Nodes;
using DotKt.Bir;

// A Kotlin Unit declaration becomes a CLR void method at the BIR -> CIR representation boundary. Inline splicing
// can leave the source `return <Unit expression>` shape in that declaration after its return slot has become void.
// Preserve evaluation of the expression, then make the CIR return physically value-less. ilemit must not infer this
// mismatch from System.Void; CIR states the exact stack contract it emits.
static class VoidReturnNormalization
{
    static readonly TypeNode VoidType = new TypeNode.Fqn("void");

    public static void Apply(JsonNode root) => VisitFrames(root);

    static void VisitFrames(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (IsVoidFrame(obj) && obj["body"] is JsonArray body)
                obj["body"] = NormalizeNode(body);

            foreach (var property in obj)
                if (property.Value != null) VisitFrames(property.Value);
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
                if (item != null) VisitFrames(item);
        }
    }

    static bool IsVoidFrame(JsonObject obj) =>
        obj["body"] is JsonArray
        && obj["params"] is JsonArray
        && obj["ret"] is JsonNode ret
        && TypeJson.IsType(ret)
        && TypeNode.Parse(ret.ToJsonString()) == VoidType;

    static JsonNode NormalizeNode(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            // A synthesized method/lambda nested in this body owns a distinct return frame. VisitFrames will process
            // it independently using its own physical return slot.
            if (IsDeclarationFrame(obj)) return obj.DeepClone();

            var kind = StringValue(obj["k"]);
            if (kind == "return" && obj["value"] is JsonNode returnValue)
                return new JsonObject
                {
                    ["k"] = "block",
                    ["body"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["k"] = "exprStmt",
                            ["expr"] = NormalizeNode(returnValue),
                        },
                        new JsonObject { ["k"] = "return" },
                    },
                };
            if (kind == "returnExpr" && obj["value"] is JsonNode returnExprValue)
                return new JsonObject
                {
                    ["k"] = "valueBlock",
                    ["stmts"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["k"] = "exprStmt",
                            ["expr"] = NormalizeNode(returnExprValue),
                        },
                    },
                    ["result"] = new JsonObject { ["k"] = "returnExpr" },
                };

            var copy = new JsonObject();
            foreach (var property in obj)
                copy[property.Key] = property.Value == null
                    ? null
                    : NormalizeNode(property.Value);
            return copy;
        }
        if (node is JsonArray array)
        {
            var copy = new JsonArray();
            foreach (var item in array)
                copy.Add(item == null ? null : NormalizeNode(item));
            return copy;
        }
        return node?.DeepClone();
    }

    static bool IsDeclarationFrame(JsonObject obj) =>
        obj["body"] is JsonArray && obj["params"] is JsonArray && obj["ret"] is JsonNode ret && TypeJson.IsType(ret);

    static string StringValue(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
