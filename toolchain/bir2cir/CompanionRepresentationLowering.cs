using DotKt.Bir;
using DotKt.Bir.Lowering;

// Every companion has one non-generic carrier and exactly one self-typed $INSTANCE. A companion whose physical
// owner has generic slots is hoisted, so closed owner instantiations do not multiply the Kotlin singleton.
static partial class CompanionRepresentationLowering
{
    internal const string HoistedMarker = "$companion$";

    internal sealed record Association(
        string OwnerName, string KotlinOwnerName, string SemanticName,
        string SourceName, string Visibility, string PhysicalName, string PhysicalOwner, int PhysicalOwnerArity)
    {
        internal bool Hoisted => PhysicalOwnerArity > 0;
    }
    public sealed class LocalIndex
    {
        internal LocalIndex(IReadOnlyList<Association> associations) => Associations = associations;
        internal IReadOnlyList<Association> Associations { get; }
    }

    // Before a splice only the selected declaration owner is bound; values remain the later BindUses decision.
    public static void BindSpliceUses(BirFileDeclaration root, ReferenceMetadataIndex refs)
        => root.Visit(node => {
            if (node is BirCallInline call && call.Owner.OrDefault() is TypeNode.Fqn owner
                && refs.TryCompanionPhysicalOwner(owner.Name, out var physicalOwner))
                call.Owner = owner with { Name = physicalOwner };
        });

    sealed record UseRepresentation(string SemanticType, string Carrier);

    static UseRepresentation ResolveUse(
        BirCompanionValue value, IReadOnlyList<Association> local, ReferenceMetadataIndex refs, bool bindExternal)
    {
        var semantic = (value.CompanionType.OrDefault() as TypeNode.Fqn)?.Name;
        if (semantic != null)
        {
            var association = local.SingleOrDefault(a => a.SemanticName == semantic);
            if (association != null) return new(association.SemanticName, association.PhysicalName);
        }
        if (!bindExternal) return null;
        if (semantic != null && (refs.TryCompanionPhysicalOwner(semantic, out var selectedCarrier)
                || refs.TryCompanionMetadataCarrier(semantic, out selectedCarrier)))
            return new(semantic, selectedCarrier);
        if (value.OwnerType.OrDefault() is not TypeNode.Fqn physicalOwner) return null;
        return refs.TryCompanionMetadataCarrier(physicalOwner.Name, out var carrier)
                || refs.TryCompanionCarrierByPhysicalOwner(physicalOwner.Name, out carrier)
            ? new(semantic, carrier) : null;
    }

    static UseRepresentation ResolveMarker(string marker, IReadOnlyList<Association> local, ReferenceMetadataIndex refs, bool bindExternal)
    {
        var association = local.SingleOrDefault(a => a.SemanticName == marker);
        if (association != null) return new(association.SemanticName, association.PhysicalName);
        if (!bindExternal) return null;
        var semantic = marker;
        var physical = marker;
        if (refs.TryCompanionPhysicalOwner(marker, out var selected)) physical = selected;
        else if (!refs.TryCompanionSemanticType(marker, out semantic)) return null;
        return refs.TryCompanionMetadataCarrier(physical, out var carrier)
                || refs.TryCompanionCarrierByPhysicalOwner(physical, out carrier)
            ? new(semantic, carrier) : null;
    }

    static bool SameCompanionMemberOwner(string owner, UseRepresentation representation) =>
        owner == representation.SemanticType || owner == representation.Carrier
        || owner.Replace('+', '.') == representation.Carrier.Replace('+', '.');
    static bool IsCompanionMemberOwner(string owner, Association association) =>
        owner == association.SemanticName || owner == association.PhysicalName;

}
