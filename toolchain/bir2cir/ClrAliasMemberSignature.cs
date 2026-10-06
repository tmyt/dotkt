using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Keep a selected foreign declaration separate from its current owner application.
// Projecting a Kotlin type argument changes the application, not fixed types written
// in the CLR MethodDef's parameter list.
static class ClrAliasMemberSignature
{
    const string Key = "_aliasDeclarationParameters";

    public static void Capture(JsonObject call, ReferenceMetadataIndex refs)
    {
        if (TypeJson.Read(call["type"]) is not TypeNode.Fqn { Args: { Length: > 0 } arguments } owner
            || call["argTypes"] is not JsonArray signature
            || call["method"] is not JsonValue name) return;
        var parameters = signature.Select(TypeJson.Read).ToArray();
        var physicalOwner = refs.TryResolveClrOwner(owner.Name, out var aliasOwner, out _) ? aliasOwner : owner.Name;
        var selected = refs.TryResolveStaticMemberSignature(physicalOwner, name.GetValue<string>(),
                (call["typeArgs"] as JsonArray)?.Count ?? 0, call["k"]?.GetValue<string>() == "clrStatic",
                parameters, arguments, out var declaration, out var method, out var declaringOwner);
        if (!selected
            || method.DeclaringType != declaringOwner
            || !declaration.Select(type => SupertypeGraph.SubstOwnerTvs(type, arguments)).SequenceEqual(parameters))
            return;
        // Opaque within physical type rewriting: these are declaration-frame facts.
        call[Key] = new JsonArray(declaration.Select(TypeJson.Write).ToArray()).ToJsonString();
    }

    public static void Apply(JsonObject call)
    {
        if (call[Key] is not JsonValue encoded) return;
        var kind = call["k"]?.GetValue<string>();
        var ownerSlot = kind == "constrainedCall" ? "iface" : "type";
        if (TypeJson.Read(call[ownerSlot]) is not TypeNode.Fqn { Args: { } arguments }) return;
        var declaration = JsonNode.Parse(encoded.GetValue<string>()).AsArray();
        call[kind == "constrainedCall" ? "sig" : "argTypes"] = new JsonArray(declaration
            .Select(TypeJson.Read).Select(type => SupertypeGraph.SubstOwnerTvs(type, arguments))
            .Select(TypeJson.Write).ToArray());
        call.Remove(Key);
    }
}
