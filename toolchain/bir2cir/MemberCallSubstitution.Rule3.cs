using DotKt.Bir;

static partial class MemberCallSubstitution
{
    // Source and materialized callers share this declaration/frame decision.
    // Each owning graph constructs its own expressions from the scalar plan.
    internal sealed record Rule3HelperPlan(TypeNode.Fqn Owner, string Method,
        string DeclarationId, TypeNode[] TypeArgs, TypeNode[] Signature,
        TypeNode[] CalleeParams, TypeNode CalleeReturn, TypeNode Return,
        int? ProjectedHelperReceiver);

    internal static Rule3HelperPlan PlanRule3Helper(ReferenceMetadataIndex refs,
        TypeNode.Fqn owner, string member, string sourceIdentity, bool instance,
        bool hasReceiver, int suppliedArgumentCount, TypeNode[] declarationSignature,
        TypeNode[] methodTypeArgs, TypeNode knownReturn, TypeNode projectedFallbackReturn,
        IReadOnlySet<string> selectedLocalIds, GenericRepresentationPolicy representations)
    {
        var ownerName = owner.Name;
        var hasInstance = instance && hasReceiver;
        var arity = refs.OwnerArity(ownerName);
        var classArguments = (owner.Args ?? Array.Empty<TypeNode>()).Where(type => type != null).ToList();
        while (classArguments.Count < arity) classArguments.Add(ObjType);
        var typeArguments = classArguments.Concat(methodTypeArgs ?? Array.Empty<TypeNode>()).ToArray();
        var signature = new List<TypeNode>();
        if (hasInstance)
        {
            var frame = refs.NullableTypeFrames.TryGetValue(ownerName, out var recorded)
                ? recorded : new NullableRepresentationFrame(arity, Array.Empty<int>());
            signature.Add(RemapHoistedTypeVars(AliasHelperHoist.ReceiverType(ownerName, frame, representations), arity));
        }
        foreach (var parameter in declarationSignature ?? Array.Empty<TypeNode>())
            signature.Add(RemapHoistedTypeVars(parameter, arity));

        var local = sourceIdentity != null && selectedLocalIds?.Contains(sourceIdentity) == true;
        var identity = local ? AliasHelperHoist.DeclarationIdentity(sourceIdentity) : null;
        TypeNode[] calleeParameters = null;
        TypeNode calleeReturn = null;
        if (sourceIdentity != null && !local)
        {
            // Reference metadata owns the exact semantic declaration. Project
            // its helper descriptor using the same class-to-method frame transfer.
            var selected = refs.AliasHelperSourceSignature(sourceIdentity, ownerName);
            member = selected.Name;
            var linkage = new List<TypeNode>();
            if (hasInstance) linkage.Add(signature[0]);
            linkage.AddRange(selected.Parameters.Select(parameter => RemapHoistedTypeVars(parameter, arity)));
            calleeParameters = linkage.ToArray();
            calleeReturn = RemapHoistedTypeVars(selected.Return, arity);
        }

        // Keep a concrete caller result, never borrow a callee's open variable.
        // Projected foreign receivers retain their explicit caller result even
        // when it is a type variable; their later physical crossing owns it.
        var result = knownReturn is TypeNode.Tv ? null : knownReturn;
        var projected = instance && ForeignStarProjectionBinding.IsForeignStarType(owner, refs);
        if (projected)
            result = knownReturn ?? projectedFallbackReturn
                ?? throw new InvalidOperationException("Projected alias helper call has no declared result type");
        return new Rule3HelperPlan(new TypeNode.Fqn(ReferenceMetadataIndex.HelperTypeName(ownerName)), member,
            identity, typeArguments.Length == 0 ? null : typeArguments,
            signature.Count >= suppliedArgumentCount + (hasInstance ? 1 : 0) ? signature.ToArray() : null,
            calleeParameters, calleeReturn, result, projected ? 0 : null);
    }
}
