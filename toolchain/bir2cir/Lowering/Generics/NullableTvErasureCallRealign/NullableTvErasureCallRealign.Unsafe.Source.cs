using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableTvErasureCallRealign
{
    // The same canonical index survives the physical prefix. New holder types
    // are admitted additively; existing owner snapshots are never recaptured.
    internal static void CollectNewSyntheticTypes(BirFileDeclaration file, DeclIndex index)
    {
        index.SourceApplicationOrder?.Add(InnerApplicationOrder.Capture(file));
        foreach (var type in file.Types.OrEmpty())
            if (type.Name.OrDefault() is string owner) AdmitNewType(index, owner, () => CaptureRecipe(type));
    }

    // A generated accessor on an existing host has only the old fallback
    // name/arity admission. Do not grow original exact-ID/property eligibility.
    internal static void CollectNewSyntheticMembers(BirFileDeclaration file, DeclIndex index)
    {
        CollectGeneratedMethods(file.Methods.OrEmpty(), index.TopLevel);
        foreach (var type in file.Types.OrEmpty())
            if (type.Name.OrDefault() is string owner)
            {
                if (!index.ByOwner.TryGetValue(owner, out var methods))
                    index.ByOwner[owner] = methods = new(StringComparer.Ordinal);
                CollectGeneratedMethods(type.Methods.OrEmpty(), methods);
            }
    }

    static void CollectGeneratedMethods(IEnumerable<BirFunctionDeclaration> methods, Dictionary<string, DeclSig> declarations)
    {
        foreach (var method in methods)
            if (method.Generated.OrDefault() && method.Name.OrDefault() is string name && ReadSig(method) is { } signature)
                AddUnambiguous(declarations, name + "|" + signature.Params.Length, signature);
    }

    internal static BirFileDeclaration ApplyAfterUnsafeAccessorSynthesis(BirFileDeclaration file, DeclIndex index,
        ValueTypeOracle isValue, ReferenceMetadataIndex references)
    {
        if (index.SourceApplicationOrder != null)
            throw new InvalidOperationException("Post-Unsafe nullable flow requires physical declaration applications");
        return ApplySourceUses(file, index, isValue, references);
    }

    // Both new-type and generated-member reads need this fact before it is
    // consumed. It is lowering provenance, not a final metadata carrier.
    internal static void ConsumeNullableErasureOwnership(BirFileDeclaration file)
    {
        void Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            if (node is BirFunctionDeclaration method) method.NullableErasureOwnershipKnown = default;
            node.VisitOwnChildSlots((_, role, slot) => {
                if (role != BirGenericFrameRole.FrozenSourceFact) Walk(slot.Value);
            });
        }
        Walk(file);
    }

    // A backing-field producer changed the physical key, not the logical
    // property's identity or the original declaration snapshot. Preserve a
    // poisoned original slot too; current types must not repair ambiguity.
    internal static void ApplyFieldRenames(DeclIndex index, IReadOnlyList<BackingFieldRename.Rename> renames)
    {
        foreach (var rename in renames)
        {
            if (rename.Field.Name.OrDefault() != rename.NewName)
                throw new InvalidOperationException("Nullable field-key transition does not address the actual renamed field");
            var owner = rename.Owner switch {
                BirFileDeclaration file => file.FileClass.OrDefault(),
                IBirTypeDeclaration type => type.Name.OrDefault(),
                _ => null,
            };
            if (owner == null) continue;
            if (!index.Slots.TryGetValue(owner, out var slots)) index.Slots[owner] = slots = new(StringComparer.Ordinal);
            var captured = slots.TryGetValue(rename.OldName, out var original);
            var physical = captured ? original : rename.Field.Type.OrDefault();
            if (physical == null && !captured) continue;
            if (physical == null) slots[rename.NewName] = null;
            else AddSlotSnapshot(slots, rename.NewName, BirGraphValue.CopyType(physical));
        }
    }
}
