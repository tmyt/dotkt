using System;
using System.Linq;
using System.Text.Json.Nodes;

// RoundtripMetadata has already consumed declaration source types into ordinary attributes.
// Copies on executable nodes or linkage descriptors have no serialized CIR meaning.
static class CirMetadataBoundary
{
    public static void Apply(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("kotlinType");
            obj.Remove("retKotlinType");
            obj.Remove(AliasHelperHoist.OwnershipHostKey);
            // Final binding and property allocation are complete, including late
            // synthetic bridges. Their source-selection records are not CIR operands.
            obj.Remove(DeclarationIdentityBinding.Key);
            obj.Remove(KotlinPropertyAccessors.PhysicalSlotBridgeKey);
            foreach (var child in obj.Select(pair => pair.Value).ToArray()) Apply(child);
        }
        else if (node is JsonArray array)
            foreach (var child in array) Apply(child);
    }

    public static void SelfTest()
    {
        var root = JsonNode.Parse("""
            {"methods":[{"retKotlinType":"source return","params":[{"kotlinType":"source parameter"}],
              "physicalSlotBridge":true,
              "body":[{"k":"var","kotlinType":"source local","declarationId":"selected call"}],
              "clrInterfaceImpls":[{"retKotlinType":"source linkage"}],
              "attrs":[{"args":["{\"t\":\"array\",\"elem\":{\"t\":\"fqn\",\"name\":\"T\"}}"]}],
              "memberRef":{"returnType":{"t":"mod","req":false,
                "m":{"t":"fqn","name":"Marker"},"of":{"t":"fqn","name":"System.Object"}}}}]}
            """);
        var method = root["methods"][0];
        var attributes = method["attrs"].DeepClone();
        var reference = method["memberRef"].DeepClone();
        Apply(root);
        if (method["retKotlinType"] != null || method["params"][0]["kotlinType"] != null
            || method["body"][0]["kotlinType"] != null
            || method["physicalSlotBridge"] != null
            || method["body"][0]["declarationId"] != null
            || method["clrInterfaceImpls"][0]["retKotlinType"] != null
            || !JsonNode.DeepEquals(attributes, method["attrs"])
            || !JsonNode.DeepEquals(reference, method["memberRef"]))
            throw new InvalidOperationException("CIR metadata boundary changed serialized attributes or linkage");
        var runtime = JsonNode.Parse("""
            {"types":[{"name":"Owner","companionCarrier":{"owner":"Owner","visibility":"internal"},
              "methods":[{"name":"factory","existentialInnerConstructorFactory":"selected constructor"}]}]}
            """);
        RoundtripMetadata.StripRuntimeAttrs(runtime);
        if (runtime["types"][0]["companionCarrier"] != null
            || runtime["types"][0]["methods"][0][FBoundStarProjectionErasure.InnerConstructorFactoryKey] != null)
            throw new InvalidOperationException("Runtime metadata path retained declaration carrier inputs");
        var linkage = JsonNode.Parse("""
            {"methods":[{"name":"physicalName","declarationId":"source-id",
              "declarationSourceName":"sourceName","params":[]}]}
            """);
        RoundtripMetadata.StripRuntimeAttrs(linkage);
        var linkedMethod = linkage["methods"][0];
        if (linkedMethod["declarationId"] != null
            || linkedMethod["declarationSourceName"] != null
            || linkedMethod["attrs"] is not JsonArray { Count: 1 } linkageAttrs
            || TypeJson.OwnerName(linkageAttrs[0]["attr"]) !=
                RoundtripMetadata.AKPhysicalDeclarationIdentity
            || linkageAttrs[0]["args"][0]["value"]?.GetValue<string>() != "source-id")
            throw new InvalidOperationException("Runtime metadata lost exact declaration linkage identity");
        var call = JsonNode.Parse("""
            {"k":"new","type":{"t":"fqn","name":"Box"},"args":[],
             "resolvedMemberParams":[],"resolvedMemberReturn":{"t":"fqn","name":"void"},
             "memberRef":{"kind":"ctor","assembly":"Test","declaringType":{"t":"fqn","name":"Box"},
               "name":".ctor","genericArity":0,"returnType":{"t":"fqn","name":"void"},
               "callingConvention":"instance","parameterTypes":[]}}
            """);
        var constructor = call["memberRef"].DeepClone();
        ForeignNullableGenericCrossing.Check(call, "CIR-boundary-selftest");
        if (call["resolvedMemberParams"] != null || call["resolvedMemberReturn"] != null
            || !JsonNode.DeepEquals(constructor, call["memberRef"]))
            throw new InvalidOperationException("CIR boundary did not consume constructor resolution records");
        Console.WriteLine("[CIR metadata boundary] self-test OK (source records consumed; attributes and linkage preserved)");
    }
}
