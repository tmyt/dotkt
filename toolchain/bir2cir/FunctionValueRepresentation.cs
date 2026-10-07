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
    internal const string RestorationKey = "functionValueRestoration";
    static readonly TypeNode Object = new TypeNode.Fqn("object");

    // Preserve the complete source slot, including nested generic function arguments,
    // before either nullable-frame expansion or function representation changes it.
    public static void PreserveSourceFacts(IEnumerable<JsonNode> roots)
    {
        foreach (var root in roots.OfType<JsonObject>()) PreserveOwner(root);
    }

    static void PreserveOwner(JsonObject owner)
    {
        var sourceEdges = new JsonObject();
        if (ContainsOrdinaryFunction(owner["base"]))
            sourceEdges["base"] = owner["base"].DeepClone();
        if (owner["interfaces"] is JsonArray interfaces)
        {
            var moving = new JsonArray(interfaces.Where(ContainsOrdinaryFunction)
                .Select(edge => edge.DeepClone()).ToArray());
            if (moving.Count != 0) sourceEdges["interfaces"] = moving;
        }
        if (owner["typeParams"] is JsonArray parameters)
        {
            var bounds = new JsonObject();
            for (var i = 0; i < parameters.Count; i++)
                if (parameters[i] is JsonObject parameter
                    && parameter["constraints"] is JsonArray constraints
                    && constraints.Any(ContainsOrdinaryFunction))
                    bounds[i.ToString()] = constraints.DeepClone();
            if (bounds.Count != 0) sourceEdges["bounds"] = bounds;
        }
        KotlinSupertypesRecord.Merge(owner, sourceEdges);
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
        // Suspend function slots also lose their generic parameter shapes during
        // value erasure. Capture the source before later passes can publish a
        // partially erased shape through the higher-priority KotlinType carrier.
        if (slot[sourceKey] == null && ContainsSourceFunction(slot[typeKey]))
            slot[sourceKey] = slot[typeKey].ToJsonString();
    }

    static bool ContainsSourceFunction(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (TypeJson.Read(obj) is TypeNode.Fn { Clr: null }) return true;
            return obj.Any(pair => pair.Value != null && ContainsSourceFunction(pair.Value));
        }
        return node is JsonArray array && array.Any(ContainsSourceFunction);
    }

    internal static bool ContainsOrdinaryFunction(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["t"] is JsonValue tag && tag.TryGetValue<string>(out var kind) && kind == "fn"
                && TypeJson.Read(obj) is TypeNode.Fn { Suspend: false, Clr: null }) return true;
            return obj.Any(pair => pair.Value != null && ContainsOrdinaryFunction(pair.Value));
        }
        return node is JsonArray array && array.Any(ContainsOrdinaryFunction);
    }

    public static void Apply(JsonNode root, ValueTypeOracle isByRefLike)
    {
        FunctionSignatureIdentity.Capture(root);
        Walk(root, isByRefLike);
    }

    static TypeNode Carrier(TypeNode type, ValueTypeOracle isByRefLike) => type switch
    {
        // Changing the CLR value representation must not change the slot's nullability contract.
        TypeNode.Nullable n => new TypeNode.Nullable(Carrier(n.Of, isByRefLike)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(Carrier(o.Of, isByRefLike)),
        TypeNode.Fqn named when isByRefLike(named) => type,
        _ => Object,
    };

    static void Walk(JsonNode node, ValueTypeOracle isByRefLike)
    {
        if (node is JsonObject obj)
        {
            if (obj["k"] is JsonValue callTag && callTag.TryGetValue<string>(out var callKind)
                && callKind is "callStatic" or "callInstance" or "constrainedCall"
                && obj[FunctionSignatureIdentity.CallKey] == null
                && obj["sig"] is JsonArray signature && ContainsOrdinaryFunction(signature))
                obj[FunctionSignatureIdentity.CallKey] = signature.DeepClone();
            var invocationResult = obj["k"] is JsonValue nodeTag
                && nodeTag.TryGetValue<string>(out var nodeKind) && nodeKind == "delegateInvoke"
                && TypeJson.Read(obj["funcType"]) is TypeNode.Fn { Suspend: false, Clr: null } invocation
                    ? invocation.Ret : null;
            if (obj["t"] is JsonValue tag && tag.TryGetValue<string>(out var kind) && kind == "fn")
            {
                var fn = (TypeNode.Fn)TypeJson.Read(obj);
                if (fn.Suspend || fn.Clr != null) return;
                // A ref struct cannot cross an object slot. Keep its exact CLR signature even for a stored
                // Kotlin function; all boxable slots still share the ordinary identity-preserving carrier.
                var physical = new TypeNode.Fn(false, Carrier(fn.Ret, isByRefLike),
                    fn.Params.Select(parameter => Carrier(parameter, isByRefLike)).ToArray(),
                    fn.Recv == null ? null : Carrier(fn.Recv, isByRefLike));
                var replacement = (JsonObject)TypeJson.Write(physical);
                obj.Clear();
                foreach (var pair in replacement.ToList()) obj[pair.Key] = pair.Value?.DeepClone();
                return;
            }
            foreach (var pair in obj.ToList())
            {
                // These are authored Kotlin facts, not physical slots.
                if (pair.Key is DeclarationIdentityBinding.SemanticSignatureKey or FunctionSignatureIdentity.Key or FunctionSignatureIdentity.CallKey
                    or "memberSignature" or "delegationSig" or "inheritedClassMethods" or "attrs" or "retAttrs") continue;
                // A literal SAM conversion constructs its declared CLR delegate directly. Its target
                // signature is not an ordinary Kotlin function value, and can contain unboxable slots.
                // Stored function conversions still erase their operand and adapt at the SAM boundary.
                if (pair.Key == "funcType" && ClrMemberResolution.HasDeclaredDelegateSlot(obj)) continue;
                if (pair.Value != null) Walk(pair.Value, isByRefLike);
            }
            if (invocationResult != null)
            {
                var call = obj.DeepClone();
                var resultType = TypeJson.Write(invocationResult);
                Walk(resultType, isByRefLike);
                obj.Clear();
                obj["k"] = "cast";
                obj["type"] = resultType;
                // This restores the function's selected result slot. It is not a source-level
                // unchecked classifier cast and must retain its constructed generic arguments.
                obj["_exactBridgeCast"] = true;
                obj[RestorationKey] = true;
                obj["e"] = call;
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array) if (child != null) Walk(child, isByRefLike);
    }
}
