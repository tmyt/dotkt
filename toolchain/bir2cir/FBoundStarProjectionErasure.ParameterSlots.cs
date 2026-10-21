using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class FBoundStarProjectionErasure
{
    // A source Kotlin method consumes an erased generic view, not proof of one CLR construction.
    // Constructors, property accessors, generated linkage methods and native overrides retain their contracts.
    static bool IsOrdinaryKotlinParameter(JsonObject parameter,
        IReadOnlyDictionary<string, JsonObject> definitions, ReferenceMetadataIndex refs)
    {
        if (!IsParameterTypeSlot(parameter, "type")
            || parameter.Parent?.Parent is not JsonObject method
            || method[DeclarationIdentityBinding.Key] == null
            || KotlinPropertyAccessors.TryIdentity(method, out _, out _)
            || method.Parent is not JsonArray entries || entries.Parent is not JsonObject owner
            || !ReferenceEquals(owner["methods"], entries)
            || Bool(method["generated"]) || Bool(owner["generated"]) || HasClrTypeAlias(owner)
            || (method["attrs"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Any(attribute => TypeJson.OwnerName(attribute["attr"]) == "kotlin.clr.ClrIntrinsic")) return false;
        foreach (var edge in (method["overrides"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var name = TypeJson.OwnerName(edge["owner"]);
            if (name == null) continue;
            if (definitions.TryGetValue(name, out var local))
            {
                if (HasClrTypeAlias(local)) return false;
            }
            else if (!refs.HasDotKtOwner(name) || refs.TryResolveClrOwner(name, out _, out _)) return false;
        }
        return true;
    }

    static TypeNode ProjectOrdinaryParameter(TypeNode type,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs) => type switch
    {
        TypeNode.Nullable nullable => new TypeNode.Nullable(ProjectOrdinaryParameter(nullable.Of, owners, refs)),
        TypeNode.Oblivious oblivious => new TypeNode.Oblivious(ProjectOrdinaryParameter(oblivious.Of, owners, refs)),
        TypeNode.Fqn { Args: { Length: > 0 } } named
            when (!owners.TryGetValue(named.Name, out var local)
                    || Str(local.Def["kind"]) is "class" or "interface")
                && !refs.IsValueType(named)
                && TryExistentialCarrier(named.Name, owners, refs, out var carrier) => new TypeNode.Fqn(carrier),
        _ => type,
    };
}
