using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

// A singleton-class enum has the same Kotlin comparison contract as a native CLR enum. Give it a real
// compiler-owned comparison face so dispatch works without an impossible System.Enum bound or taking over a
// user-authored CLR IComparable implementation.
// The ordinal field comes only from the producer's explicit enum map, never from a physical-name convention.
static class RichEnumComparisonSynthesis
{
    internal const string Face = "DotKt.Runtime.CompilerServices.KotlinEnumComparison";
    internal static void Apply(IEnumerable<JsonNode> roots)
    {
        foreach (var type in roots.OfType<JsonObject>()
            .SelectMany(root => (root["types"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()))
        {
            if (type["richEnum"] is not JsonObject metadata) continue;
            var owner = type["name"].GetValue<string>();
            var ordinal = metadata["ordinal"].GetValue<string>();
            type["base"] = TypeJson.Fqn("DotKt.Runtime.CompilerServices.KotlinEnumComparable");
            foreach (var constructor in (type["ctors"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (constructor["thisArgs"] is not JsonArray)
                {
                    constructor["baseArgs"] = new JsonArray();
                    constructor["delegationSig"] = new JsonArray();
                    constructor.Remove("delegationDeclarationId");
                }
            var interfaces = type["interfaces"] as JsonArray ?? new JsonArray();
            if (type["interfaces"] == null) type["interfaces"] = interfaces;
            interfaces.Add(TypeJson.Fqn(Face));
            var methods = type["methods"] as JsonArray ?? new JsonArray();
            if (type["methods"] == null) type["methods"] = methods;
            var names = methods.OfType<JsonObject>().Select(method => method["name"]?.GetValue<string>()).ToHashSet();
            var suffix = 0;
            string name;
            do { name = "$enumComparison$" + suffix++; } while (names.Contains(name));
            JsonObject Ordinal(JsonObject receiver) => new() {
                ["k"] = "field", ["ownerType"] = TypeJson.Fqn(owner), ["name"] = ordinal, ["recv"] = receiver,
            };
            methods.Add(new JsonObject {
                ["name"] = name, ["static"] = false, ["virtual"] = true, ["override"] = false,
                ["abstract"] = false, ["vis"] = "private", ["generated"] = true,
                [KotlinPropertyAccessors.PhysicalSlotBridgeKey] = true,
                ["params"] = new JsonArray(new JsonObject { ["name"] = "other", ["type"] = TypeJson.Fqn("kotlin.Any") }),
                ["ret"] = TypeJson.Fqn("kotlin.Int"),
                ["body"] = new JsonArray(new JsonObject { ["k"] = "return", ["value"] = new JsonObject {
                    ["k"] = "binOp", ["op"] = "-",
                    ["lhs"] = Ordinal(new JsonObject { ["k"] = "this" }),
                    ["rhs"] = Ordinal(new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Fqn(owner),
                        ["e"] = new JsonObject { ["k"] = "local", ["name"] = "other" } }),
                } }),
                ["clrInterfaceImpls"] = new JsonArray(new JsonObject {
                    ["owner"] = TypeJson.Fqn(Face), ["member"] = "compareEnumTo", ["arity"] = 0,
                    ["params"] = new JsonArray(TypeJson.Fqn("kotlin.Any")), ["ret"] = TypeJson.Fqn("kotlin.Int"),
                }),
            });
        }
    }
}
