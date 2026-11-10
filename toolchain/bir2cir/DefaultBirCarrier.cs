using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// kotc states the default expression and its Kotlin argument position. The CLR layer freezes that source fact
// before ordinary walkers can lower it, and owns the physical compiler-metadata attribute just as for inline BIR.
static class DefaultBirCarrier
{
    internal const string AttributeName = "DotKt.Runtime.CompilerServices.KotlinDefaultAttribute";

    internal static void FreezeSourceDefaults(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var child in obj.Select(pair => pair.Value).ToArray()) FreezeSourceDefaults(child);
            if (obj.Remove("defaultBir", out var source))
            {
                var attribute = Create(source["index"].GetValue<int>(), source["body"]);
                if (obj["attrs"] is not JsonArray attrs) obj["attrs"] = attrs = new JsonArray();
                attrs.Add(attribute);
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array) FreezeSourceDefaults(child);
    }

    internal static JsonObject Create(int index, JsonNode body) => new()
    {
        ["attr"] = TypeJson.Fqn(AttributeName),
        ["argTypes"] = new JsonArray(TypeJson.Fqn("System.Int32"), TypeJson.Fqn("System.String"),
            new JsonObject { ["t"] = "array", ["elem"] = TypeJson.Fqn("System.Byte") }),
        ["args"] = new JsonArray(
            new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("System.Int32"), ["value"] = index },
            new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("System.String"), ["value"] = BirCarrier.JsonV1 },
            new JsonObject { ["bytes"] = Convert.ToBase64String(BirCarrier.EncodeBody(BirCarrier.JsonV1, body)) }),
    };

    internal static bool IsCarrier(JsonObject node) => TypeJson.OwnerName(node["attr"]) == AttributeName;

    internal static JsonNode Decode(JsonObject attribute) => BirCarrier.DecodeBody(
        attribute["args"][1]["value"].GetValue<string>(),
        Convert.FromBase64String(attribute["args"][2]["bytes"].GetValue<string>()));

    internal static void Rewrite(JsonObject attribute, Action<JsonNode> rewrite)
    {
        var payload = Decode(attribute);
        rewrite(payload);
        var args = attribute["args"];
        args[2]["bytes"] = Convert.ToBase64String(BirCarrier.EncodeBody(args[1]["value"].GetValue<string>(), payload));
    }

    internal static JsonNode OfParameter(JsonObject parameter) => parameter["attrs"] is JsonArray attrs
        ? attrs.OfType<JsonObject>().Where(IsCarrier).Select(Decode).SingleOrDefault() : null;

    internal static void SelfTest()
    {
        var body = new JsonObject { ["k"] = "sourcePoint", ["span"] = new JsonObject { ["line"] = 7 },
            ["e"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Int"), ["value"] = 42 } };
        var source = new JsonObject { ["name"] = "value", ["type"] = TypeJson.Fqn("kotlin.Int"),
            ["defaultBir"] = new JsonObject { ["index"] = 2, ["body"] = body } };
        FreezeSourceDefaults(source);
        if (source["defaultBir"] != null || !JsonNode.DeepEquals(OfParameter(source), body))
            throw new InvalidOperationException("Default BIR source fact did not round-trip through the shared codec");
        var attribute = (JsonObject)source["attrs"][0];
        if (TypeJson.OwnerName(attribute["argTypes"][0]) != "System.Int32"
            || TypeJson.OwnerName(attribute["argTypes"][1]) != "System.String")
            throw new InvalidOperationException("Default carrier must name its exact physical constructor slots");
        MaterializedBirPayload.HideSourceSpans(attribute);
        if (Decode(attribute)["span"] != null || body["span"] == null
            || attribute["args"][0]["value"].GetValue<int>() != 2)
            throw new InvalidOperationException("Default carrier rewriting changed source ownership or argument position");
    }
}
