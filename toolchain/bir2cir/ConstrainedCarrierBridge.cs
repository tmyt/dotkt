using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// R : OwnerT cannot be stated on a non-generic existential interface. Preserve that constraint on a private
// forwarding thunk, and bind a typed delegate to its actual CLR instantiation from the unconstrained carrier slot.
// The thunk retains ordinary virtual dispatch to the original method. Arguments never pass through object[].
static class ConstrainedCarrierBridge
{
    internal const string ExactOwnerKey = "_constrainedCarrierOwner";
    internal static bool Used { get; private set; }
    internal static void Reset() => Used = false;

    internal static JsonObject CreateThunk(JsonObject owner, JsonObject source, JsonObject bridge)
    {
        Used = true;
        var occupied = new HashSet<string>(((JsonArray)owner["methods"]).OfType<JsonObject>()
            .Select(method => method["name"]?.GetValue<string>()), StringComparer.Ordinal);
        var ordinal = 0;
        string name;
        do name = "$constrained$" + ordinal++; while (occupied.Contains(name));
        var thunk = bridge.DeepClone().AsObject();
        thunk["name"] = name;
        thunk["virtual"] = false;
        thunk["typeParams"] = source["typeParams"].DeepClone();
        thunk.Remove("clrInterfaceImpls");
        thunk.Remove(KotlinPropertyAccessors.PhysicalSlotBridgeKey);
        thunk.Remove(KotlinPropertyAccessors.ClrInterfaceSlotBridgeKey);

        var typeType = new TypeNode.Fqn("System.Type");
        var anyType = new TypeNode.Fqn("kotlin.Any");
        var stringType = new TypeNode.Fqn("kotlin.String");
        var delegateType = new TypeNode.Fqn("System.Delegate");
        var ownerType = new TypeNode.Fqn(owner["name"].GetValue<string>(),
            Enumerable.Range(0, (owner["typeParams"] as JsonArray)?.Count ?? 0)
                .Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray());
        var resultType = TypeJson.Read(bridge["ret"]);
        var parameters = (JsonArray)bridge["params"];
        var functionType = new TypeNode.Fn(false, resultType,
            parameters.OfType<JsonObject>().Select(parameter => TypeJson.Read(parameter["type"])).ToArray());
        JsonObject ClassRef(TypeNode type) => new()
        {
            ["k"] = "classRef", ["type"] = TypeJson.Write(type),
        };
        var ownerRef = ClassRef(ownerType);
        ownerRef[ExactOwnerKey] = true;
        var methodTypes = new JsonArray(Enumerable.Range(0, ((JsonArray)source["typeParams"]).Count)
            .Select(index => (JsonNode)ClassRef(new TypeNode.Tv("method", index))).ToArray());
        var runtimeCall = new JsonObject
        {
            ["k"] = "callStatic",
            ["owner"] = TypeJson.Write(new TypeNode.Fqn(
                "DotKt.Runtime.CompilerServices.ConstrainedCarrierRuntimeKt")),
            ["method"] = "constrainedCarrierDelegate",
            ["sig"] = new JsonArray(new TypeNode[]
                { anyType, typeType, stringType, new TypeNode.Array(typeType), typeType }
                .Select(TypeJson.Write).ToArray()),
            ["ret"] = TypeJson.Write(delegateType),
            ["args"] = new JsonArray(
                new JsonObject { ["k"] = "this" }, ownerRef,
                new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(stringType), ["value"] = name },
                new JsonObject { ["k"] = "newArray", ["elem"] = TypeJson.Write(typeType), ["elems"] = methodTypes },
                ClassRef(functionType)),
        };
        var invoke = new JsonObject
        {
            ["k"] = "delegateInvoke", ["funcType"] = TypeJson.Write(functionType),
            ["recv"] = new JsonObject
            {
                ["k"] = "cast", ["type"] = TypeJson.Write(functionType), ["e"] = runtimeCall,
            },
            ["args"] = new JsonArray(parameters.OfType<JsonObject>().Select(parameter => (JsonNode)new JsonObject
            {
                ["k"] = "local", ["name"] = parameter["name"].DeepClone(),
            }).ToArray()),
        };
        bridge["body"] = resultType is TypeNode.Fqn { Name: "kotlin.Unit" or "void" }
            ? new JsonArray(new JsonObject { ["k"] = "exprStmt", ["expr"] = invoke })
            : new JsonArray(new JsonObject { ["k"] = "return", ["value"] = invoke });
        return thunk;
    }
}
