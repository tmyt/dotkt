using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CompanionRepresentationLowering
{
    // The continuing declarations stay in place; only companion expression occurrences change kind.
    public static void BindUses(BirFileDeclaration root, LocalIndex local, ReferenceMetadataIndex refs, bool bindExternal)
    {
        RewriteMarkedCaptures(root, local.Associations, refs, bindExternal);
        RewriteUses(root, local.Associations);
        if (bindExternal) BindExternalUses(root, refs);
    }

    static void RewriteMarkedCaptures(
        BirGraphValue node, IReadOnlyList<Association> local, ReferenceMetadataIndex refs, bool bindExternal)
    {
        if (node is IBirExecutablePayload) return;
        var marker = node switch {
            BirClassDefinition value => value.CompanionCaptureOwner.OrDefault(),
            BirNew value => value.CompanionCaptureOwner.OrDefault(),
            BirNewSuspendLambda value => value.CompanionCaptureOwner.OrDefault(),
            _ => null,
        };
        if (marker != null)
        {
            var representation = ResolveMarker(marker, local, refs, bindExternal);
            // A reference build leaves imported receivers for body squashing, without a transient marker.
            if (representation == null && (bindExternal || marker.Contains("<companion:", StringComparison.Ordinal)))
                throw new InvalidOperationException($"unknown companion capture association '{marker}'");
            if (representation != null) NormalizeCaptureTypesAndOwners(node, representation);
            switch (node) {
                case BirClassDefinition value: value.CompanionCaptureOwner = default; break;
                case BirNew value: value.CompanionCaptureOwner = default; break;
                case BirNewSuspendLambda value: value.CompanionCaptureOwner = default; break;
            }
        }
        node.VisitChildren(child => RewriteMarkedCaptures(child, local, refs, bindExternal));
    }

    static void NormalizeCaptureTypesAndOwners(BirGraphValue node, UseRepresentation representation)
    {
        if (node is IBirExecutablePayload) return;
        var semanticHead = node is BirCompanionValue value
            ? value.CompanionType.OrDefault() as TypeNode.Fqn : null;
        node.RewriteTypeFieldsInPlace(type => MapCompanionTypes(type, named =>
            named.Name == representation.SemanticType ? named with { Name = representation.Carrier } : named));
        // companionType's head is the association key. Its arguments, like every capture slot, still normalize.
        if (semanticHead != null && node is BirCompanionValue companion
            && companion.CompanionType.OrDefault() is TypeNode.Fqn rewritten)
            companion.CompanionType = rewritten with { Name = semanticHead.Name };
        node.VisitChildren(child => NormalizeCaptureTypesAndOwners(child, representation));
    }

    static void BindExternalUses(BirFileDeclaration root, ReferenceMetadataIndex refs)
    {
        BirGraphValue Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return node;
            if (node is BirStaticFieldGet accessor && accessor.OwnerType.OrDefault() is TypeNode.Fqn accessorOwner
                && accessor.Name.HasValue
                && refs.TryCompanionAccessor(accessorOwner.Name, accessor.Name.Value, out var accessorCarrier))
                return SingletonLoad(accessorCarrier);

            var member = CompanionMemberUse(node);
            // Do this before the general token rewrite: the semantic owner also carries the companion-call fact.
            if (node is BirCallInstance or BirNewBoundDelegate or BirFieldGet or BirSetField
                    or BirFieldSetExpression or BirLateinitGet
                && member.Owner is TypeNode.Fqn semanticOwner
                && refs.TryCompanionPhysicalOwner(semanticOwner.Name, out var physicalOwner))
            {
                member.SetOwner(new TypeNode.Fqn(physicalOwner));
                if (node is BirCallInstance call) call.CompanionCall = true;
                if (node is BirNewBoundDelegate bound) {
                    bound.CompanionCall = true;
                    bound.CalleeOwner = new TypeNode.Fqn(physicalOwner);
                }
                member = CompanionMemberUse(node);
            }
            if (member.Receiver is BirCompanionValue direct
                && ResolveUse(direct, [], refs, bindExternal: true) is { } directRepresentation
                && member.Owner is TypeNode.Fqn directOwner
                && SameCompanionMemberOwner(directOwner.Name, directRepresentation))
            {
                member.SetOwner(new TypeNode.Fqn(directRepresentation.Carrier));
                member.SetReceiver(SingletonLoad(directRepresentation.Carrier));
                if (node is BirNewBoundDelegate bound)
                    bound.CalleeOwner = new TypeNode.Fqn(directRepresentation.Carrier);
            }
            if (node is BirCompanionValue companion
                && ResolveUse(companion, [], refs, bindExternal: true) is { } representation)
                return SingletonLoad(representation.Carrier);

            node.RewriteTypeFieldsInPlace(type => MapCompanionTypes(type, named =>
                refs.TryCompanionMetadataCarrier(named.Name, out var exact)
                    || refs.TryCompanionPhysicalOwner(named.Name, out exact)
                    ? named with { Name = exact } : named));
            node.VisitChildSlots(slot => slot.Replace(Walk(slot.Value)));
            return node;
        }
        Walk(root);
    }

    // All current source shapes with an ownerType and a receiver participate in the direct-receiver rule.
    // The semantic-owner-only rule above deliberately remains the narrower six-kind rule.
    readonly record struct CompanionMember(
        TypeNode Owner, IBirExpression Receiver, Action<TypeNode> SetOwner, Action<IBirExpression> SetReceiver);
    static CompanionMember CompanionMemberUse(BirGraphValue node) => node switch {
        BirCallInstance value => new(value.OwnerType.OrDefault(), value.Recv.OrDefault(),
            type => value.OwnerType = type, expr => value.Recv = OptionalField<IBirExpression>.Of(expr)),
        BirFieldGet value => new(value.OwnerType.OrDefault(), value.Recv.OrDefault(),
            type => value.OwnerType = type, expr => value.Recv = OptionalField<IBirExpression>.Of(expr)),
        BirFieldSetExpression value => new(value.OwnerType.OrDefault(), value.Recv.OrDefault(),
            type => value.OwnerType = type, expr => value.Recv = OptionalField<IBirExpression>.Of(expr)),
        BirLateinitGet value => new(value.OwnerType.OrDefault(), value.Recv.OrDefault(),
            type => value.OwnerType = type, expr => value.Recv = OptionalField<IBirExpression>.Of(expr)),
        BirLateinitIsInitialized value => new(value.OwnerType.OrDefault(), value.Recv.OrDefault(),
            type => value.OwnerType = type, expr => value.Recv = OptionalField<IBirExpression>.Of(expr)),
        BirClrEventForwarder value => new(value.OwnerType.OrDefault(), value.Recv.OrDefault(),
            type => value.OwnerType = type, expr => value.Recv = OptionalField<IBirExpression>.Of(expr)),
        BirNewBoundDelegate value => new(value.OwnerType.OrDefault(), value.Recv.OrDefault(),
            type => value.OwnerType = type, expr => value.Recv = OptionalField<IBirExpression>.Of(expr)),
        BirSetField value => new(value.OwnerType.OrDefault(), value.Recv.OrDefault(),
            type => value.OwnerType = type, expr => value.Recv = OptionalField<IBirExpression>.Of(expr)),
        _ => default,
    };
}
