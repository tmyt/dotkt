using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ConstrainedTypeParameterReceiverBinding
{
    static ConstraintSlot[] ContinuationOwnerFrame(BirGraphValue owner)
    {
        if (owner is not IBirTypeDeclaration type) return Array.Empty<ConstraintSlot>();
        var facts = NullableRepresentationViews.TypeFacts(owner);
        return ResolvedConstraintFrame(TypeParameterFrame.Declarations(type).ToArray(), DispatchBounds(owner),
            facts?.Bounds, facts?.NullableFrame);
    }

    static IEnumerable<(string Name, TypeNode Type)> ContinuationParameters(BirGraphValue body) => body switch {
        BirFunctionDeclaration value => value.Params.OrEmpty().Select(parameter => (parameter.Name.OrDefault(), parameter.Type.OrDefault())),
        BirConstructor value => value.Params.OrEmpty().Select(parameter => (parameter.Name.OrDefault(), parameter.Type.OrDefault())),
        BirNewSuspendLambda value => value.Params.OrEmpty().Select(parameter => (parameter.Name.OrDefault(), parameter.Type.OrDefault())),
        BirInlineLambda value => value.Params.OrEmpty().Select(parameter => (parameter.Name.OrDefault(), parameter.Type.OrDefault())),
        BirClosureDefinition value => value.Params.OrEmpty().Select(parameter => (parameter.Name.OrDefault(), parameter.Type.OrDefault())),
        _ => throw new InvalidOperationException("Unaccounted constrained body owner: " + body.GetType().Name),
    };

    static ConstraintSlot[] ContinuationMethodFrame(IReadOnlyList<BirTypeParameter> parameters)
        => parameters.Select(parameter => new ConstraintSlot(parameter?.Constraints.OrEmpty() ?? Array.Empty<TypeNode>(),
            parameter?.ErasedInnerConstraints.OrEmpty() ?? Array.Empty<TypeNode>())).ToArray();

    internal static void CloseMethodOwners(BirGraphValue body, BirGraphValue owner,
        IReadOnlyList<BirTypeParameter> methodParameters, ReferenceMetadataIndex refs)
        => CloseBody(ContinuationParameters(body), FBoundStarProjectionErasure.SourceCallableBody(body).OrEmpty(),
            ContinuationOwnerFrame(owner), ContinuationMethodFrame(methodParameters), refs);

    // A source suspend construction explicitly supplies a donor owner/method
    // frame. These are rows, not a manufactured declaration or inferred owner.
    internal static void CloseMethodOwners(BirGraphValue body, IReadOnlyList<BirTypeParameter> ownerParameters,
        IReadOnlyList<BirTypeParameter> methodParameters, ReferenceMetadataIndex refs)
        => CloseBody(ContinuationParameters(body), FBoundStarProjectionErasure.SourceCallableBody(body).OrEmpty(),
            ContinuationMethodFrame(ownerParameters), ContinuationMethodFrame(methodParameters), refs);

    internal static void CloseTypeOwners(IBirTypeDeclaration type)
    {
        var frame = ContinuationOwnerFrame((BirGraphValue)type);
        foreach (var method in type.Methods.OrEmpty()) CloseMethod(method, frame);
    }

    // Both DOM and owning callers supply the same already-selected owner and
    // declaration-owned constraint graph. There is no spelling/layout recovery.
    static TypeNode.Fqn ConstraintAtPhysical(TypeNode.Tv variable, string ownerName,
        IReadOnlyList<ConstraintSlot> owner, IReadOnlyList<ConstraintSlot> method,
        ReferenceMetadataIndex refs, out bool erased)
    {
        erased = false;
        var matches = ConstraintDeclarations(variable, owner, method).Where(candidate =>
            candidate.Constraint.Name == ownerName
            || refs.ExactReflectedOwner(candidate.Constraint.Name, refs.OwnerArity(candidate.Constraint.Name))
                == refs.ExactReflectedOwner(ownerName, refs.OwnerArity(ownerName))
            || refs.TryResolveClrOwner(candidate.Constraint.Name, out var physical, out _) && physical == ownerName).ToList();
        if (matches.Count != 1) return null;
        erased = matches[0].Erased;
        return matches[0].Constraint;
    }
}
