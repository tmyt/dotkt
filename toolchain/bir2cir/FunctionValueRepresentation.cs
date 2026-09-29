using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Ordinary Kotlin function variance must not change the identity of the stored value.
// Choose argument/result carriers at construction, rather than allocating adapters at
// each assignment or generic use. A function carrying an explicit physical delegate
// family retains its exact signature.
static class FunctionValueRepresentation
{
    static readonly TypeNode Object = new TypeNode.Fqn("object");

    // Preserve the complete source slot, including nested generic function arguments,
    // before either nullable-frame expansion or function representation changes it.
    public static void PreserveSourceFacts(IEnumerable<JsonNode> roots)
    {
        foreach (var root in roots.OfType<JsonObject>()) PreserveOwner(root);
    }

    static void PreserveOwner(JsonObject owner)
    {
        if (owner["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>())
            {
                PreserveSlot(method, "ret", "retKotlinType");
                PreserveSlots(method["params"]);
            }
        if (owner["ctors"] is JsonArray ctors)
            foreach (var ctor in ctors.OfType<JsonObject>()) PreserveSlots(ctor["params"]);
        PreserveSlots(owner["fields"]);
        PreserveSlots(owner["properties"]);
        if (owner["types"] is JsonArray types)
            foreach (var type in types.OfType<JsonObject>()) PreserveOwner(type);
    }

    static void PreserveSlots(JsonNode slots)
    {
        if (slots is JsonArray array)
            foreach (var slot in array.OfType<JsonObject>()) PreserveSlot(slot, "type", "kotlinType");
    }

    static void PreserveSlot(JsonObject slot, string typeKey, string sourceKey)
    {
        if (slot[sourceKey] == null && ContainsOrdinaryFunction(slot[typeKey]))
            slot[sourceKey] = slot[typeKey].ToJsonString();
    }

    static bool ContainsOrdinaryFunction(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["t"] is JsonValue tag && tag.TryGetValue<string>(out var kind) && kind == "fn"
                && TypeJson.Read(obj) is TypeNode.Fn { Suspend: false, Clr: null }) return true;
            return obj.Any(pair => pair.Value != null && ContainsOrdinaryFunction(pair.Value));
        }
        return node is JsonArray array && array.Any(ContainsOrdinaryFunction);
    }

    public static void Apply(JsonNode root)
    {
        Walk(root);
    }

    static void Walk(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var invocationResult = obj["k"] is JsonValue nodeTag
                && nodeTag.TryGetValue<string>(out var nodeKind) && nodeKind == "delegateInvoke"
                && TypeJson.Read(obj["funcType"]) is TypeNode.Fn { Suspend: false, Clr: null } invocation
                    ? invocation.Ret : null;
            if (obj["t"] is JsonValue tag && tag.TryGetValue<string>(out var kind) && kind == "fn")
            {
                var fn = (TypeNode.Fn)TypeJson.Read(obj);
                if (fn.Suspend || fn.Clr != null) return;
                var physical = new TypeNode.Fn(false, Object,
                    fn.Params.Select(_ => Object).ToArray(), fn.Recv == null ? null : Object);
                var replacement = (JsonObject)TypeJson.Write(physical);
                obj.Clear();
                foreach (var pair in replacement.ToList()) obj[pair.Key] = pair.Value?.DeepClone();
                return;
            }
            foreach (var pair in obj.ToList())
            {
                // These are authored Kotlin facts, not physical slots.
                if (pair.Key is DeclarationIdentityBinding.SemanticSignatureKey or "attrs" or "retAttrs") continue;
                // A literal SAM conversion constructs its declared CLR delegate directly. Its target
                // signature is not an ordinary Kotlin function value, and can contain unboxable slots.
                // Stored function conversions still erase their operand and adapt at the SAM boundary.
                if (pair.Key == "funcType" && ClrMemberResolution.HasDeclaredDelegateSlot(obj)) continue;
                if (pair.Value != null) Walk(pair.Value);
            }
            if (invocationResult != null)
            {
                var call = obj.DeepClone();
                var resultType = TypeJson.Write(invocationResult);
                Walk(resultType);
                obj.Clear();
                obj["k"] = "cast";
                obj["type"] = resultType;
                obj["e"] = call;
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array) if (child != null) Walk(child);
    }
}
