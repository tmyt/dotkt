using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class GenericStaticOwnerBinding
{
    // Final placement has separated captured and own parameters. Read the
    // complete current physical frame, not an early semantic inventory.
    internal static void ApplyAll(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs)
    {
        var local = new Dictionary<string, string>(StringComparer.Ordinal);
        var genericOwners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in roots.SelectMany(root => root.Types.OrEmpty()))
        {
            if (type.StaticCarrier.OrDefault()?.Owner.OrDefault() is string owner
                && type.Name.OrDefault() is string physical)
                local.Add(owner, physical);
            if (TypeParameterFrame.Count(type) > 0 && type.Name.OrDefault() is string name)
                genericOwners.Add(name);
        }
        void Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            BindSourceStaticOwner(node, local, genericOwners, refs);
            node.VisitChildren(Walk);
        }
        foreach (var root in roots) Walk(root);
    }

    static void BindSourceStaticOwner(BirGraphValue node, IReadOnlyDictionary<string, string> local,
        IReadOnlySet<string> genericOwners, ReferenceMetadataIndex refs)
    {
        string Select(TypeNode type) => SelectCarrier(type, ((BirGraphNode)node).Tag, local, genericOwners, refs);
        // The actual input vocabulary has one staticFieldSet expression, not
        // the obsolete setStaticField/setStaticFieldExpr spellings.
        switch (node)
        {
            case BirStaticFieldGet field:
                if (Select(field.OwnerType.OrDefault()) is { } readOwner)
                {
                    field.OwnerType = new TypeNode.Fqn(readOwner);
                    if (field.MemberOwnerTypeParams.IsPresent) field.MemberOwnerTypeParams = Array.Empty<BirTypeParameter>();
                }
                break;
            case BirStaticFieldSet field:
                if (Select(field.OwnerType.OrDefault()) is { } writeOwner)
                {
                    field.OwnerType = new TypeNode.Fqn(writeOwner);
                    if (field.MemberOwnerTypeParams.IsPresent) field.MemberOwnerTypeParams = Array.Empty<BirTypeParameter>();
                }
                break;
            case BirLateinitGet field when field.Static.OrDefault():
                if (Select(field.OwnerType.OrDefault()) is { } lateOwner)
                {
                    field.OwnerType = new TypeNode.Fqn(lateOwner);
                    if (field.MemberOwnerTypeParams.IsPresent) field.MemberOwnerTypeParams = Array.Empty<BirTypeParameter>();
                }
                break;
            case BirEventGet value when value.Static.OrDefault():
                if (Select(value.Type.OrDefault()) is { } eventOwner) value.Type = new TypeNode.Fqn(eventOwner);
                break;
            case BirEventRaise value when value.Static.OrDefault():
                if (Select(value.Type.OrDefault()) is { } raiseOwner) value.Type = new TypeNode.Fqn(raiseOwner);
                break;
            case BirCallStatic call:
                string selected = null;
                // Preserve the two selected axes' original priority. Only a
                // successful selection consumes ownerType into physical owner.
                if (Select(call.OwnerType.OrDefault()) is { } logical)
                {
                    call.OwnerType = new TypeNode.Fqn(logical);
                    selected = logical;
                }
                if (Select(call.Owner.OrDefault()) is { } physical)
                {
                    call.Owner = new TypeNode.Fqn(physical);
                    selected = physical;
                }
                if (selected != null)
                {
                    if (call.MemberOwnerTypeParams.IsPresent) call.MemberOwnerTypeParams = Array.Empty<BirTypeParameter>();
                    call.Owner = new TypeNode.Fqn(selected);
                    call.OwnerType = default;
                }
                break;
        }
    }
}
