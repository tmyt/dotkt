using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Late CLR bridge synthesis must consume the allocated iterator ABI, not recreate its Kotlin generic signature.
static class KotlinIteratorPhysicalProtocol
{
    const string Iterator = "kotlin.collections.Iterator";

    internal static TypeNode Carrier(ReferenceMetadataIndex refs) =>
        refs.TryExistentialPhysicalOwner(Iterator, out var carrier)
            ? new TypeNode.Fqn(carrier)
            : throw new InvalidOperationException("Iterator declaration has no existential physical owner");

    internal static JsonObject Call(ReferenceMetadataIndex refs, JsonObject receiver,
        string member, TypeNode element, TypeNode result)
    {
        if (!refs.TryStarProjectionMember(new TypeNode.Fqn(Iterator, new[] { element }), member,
                null, 0, Array.Empty<TypeNode>(), 0, null, out var owner, out var name,
                out var signature, out _, out var physicalReturn))
            throw new InvalidOperationException($"Iterator member {member} has no existential physical slot");
        var call = new JsonObject
        {
            ["k"] = "callInstance", ["ownerType"] = TypeJson.Write(new TypeNode.Fqn(owner)),
            ["virtual"] = true, ["recv"] = receiver, ["method"] = name,
            ["sig"] = new JsonArray(signature.Select(TypeJson.Write).ToArray()),
            ["ret"] = TypeJson.Write(physicalReturn), ["args"] = new JsonArray(),
        };
        return physicalReturn.Equals(result) ? call
            : new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Write(result), ["e"] = call };
    }
}
