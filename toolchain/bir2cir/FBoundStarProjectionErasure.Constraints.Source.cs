using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static void ProjectGeneratedConstraintFramesSource(CarrierRealizationContext context,
        Dictionary<string, HashSet<int>> changedSlots)
    {
        foreach (var (name, binding) in context.SourceDefinitions)
        {
            var declaration = (IBirTypeDeclaration)binding.Source;
            if (!declaration.Generated.OrDefault() || !declaration.TypeParams.HasValue) continue;
            var parameters = declaration.TypeParams.Value;
            var prefix = (declaration as BirClassDefinition)?.OuterTypeParamCount.OrDefault() ?? 0;
            var facts = NullableRepresentationViews.TypeFacts(binding.Source);
            var recorded = new Dictionary<int, TypeNode[]>();
            var dispatch = SourceDispatch(declaration)?.Bounds.ToDictionary(pair => pair.Key, pair => pair.Value)
                ?? new Dictionary<int, TypeNode[]>();
            for (var index = prefix; index < parameters.Length; index++)
            {
                var parameter = parameters[index];
                if (!parameter.Constraints.HasValue) continue;
                var retained = new List<TypeNode>();
                var removed = new List<TypeNode>();
                foreach (var bound in parameter.Constraints.Value)
                    if (ProjectsCapturedBound(bound, index, prefix, facts?.NullableFrame, context.References))
                    {
                        removed.Add(ProjectOwnerMethodBound(bound, context.Projection, physical: false));
                        if (PhysicalBoundClassifier(bound, context.Projection) is { } classifier) retained.Add(classifier);
                        retained.AddRange(OwnerConstrainedMethodLowering.IndependentBounds(bound, parameters));
                    }
                    else retained.Add(bound);
                if (removed.Count == 0) continue;
                recorded[index] = parameter.Constraints.Value.Select(BirGraphValue.CopyType).ToArray();
                dispatch[index] = removed.Select(BirGraphValue.CopyType).ToArray();
                parameter.Constraints = retained.Distinct().Select(BirGraphValue.CopyType).ToArray();
                parameter.ErasedInnerConstraints = removed.Select(BirGraphValue.CopyType).ToArray();
                if (!changedSlots.TryGetValue(name, out var slots)) changedSlots[name] = slots = new();
                slots.Add(index);
            }
            if (recorded.Count == 0 && dispatch.Count == 0) continue;
            if (facts?.NullableFrame == null)
                NullableRepresentationViews.SetTypeFacts(binding.Source, (facts ?? SourceTypeFacts.Empty).Merge(recorded));
            OwnerConstrainedMethodLowering.SetDispatchBoundsSource((BirGraphValue)declaration, dispatch);
            ConstrainedTypeParameterReceiverBinding.CloseTypeOwners(declaration);
        }
    }

    static OwnerConstraintDispatchBounds SourceDispatch(IBirTypeDeclaration declaration) => declaration switch {
        BirClassDefinition type => type._ownerConstraintDispatchBounds.OrDefault(),
        BirInterfaceDefinition type => type._ownerConstraintDispatchBounds.OrDefault(),
        _ => null,
    };

    internal static void CloseSuspendLambdaOwnersSource(BirFileDeclaration root, CarrierRealizationContext context)
    {
        void Walk(BirGraphValue node, IBirTypeDeclaration owner)
        {
            if (node is IBirExecutablePayload) return;
            if (node is IBirTypeDeclaration declaration) owner = declaration;
            if (node is BirNewSuspendLambda lambda)
            {
                var methodFrame = new List<BirTypeParameter>();
                var typeFrame = owner != null ? TypeParameterFrame.CloneDeclarations(owner).ToList() : new List<BirTypeParameter>();
                var captures = lambda.TypeParams.OrEmpty();
                var arguments = lambda._suspendDeclarationTypeArgs.OrDefault() ?? lambda.TypeArgs.OrDefault();
                var dense = lambda.TypeFrame.OrDefault() == "dense";
                for (var index = 0; index < captures.Length; index++)
                    if (arguments[index] is TypeNode.Tv variable)
                    {
                        var frame = variable.Scope == "method" ? methodFrame : typeFrame;
                        var slot = dense ? index : variable.I;
                        while (frame.Count <= slot) frame.Add(null);
                        frame[slot] = captures[index] == null ? null : (BirTypeParameter)captures[index].DeepClone();
                    }
                OwnerConstrainedMethodLowering.CloseBodyOwnerViewsSource(lambda, methodFrame.ToArray(), typeFrame.ToArray(), context,
                    ContainsOwnerTv, bound => ProjectOwnerMethodBound(bound, context.Projection, physical: false),
                    bound => PhysicalBoundClassifier(bound, context.Projection));
            }
            node.VisitChildren(child => Walk(child, owner));
        }
        Walk(root, null);
    }

    internal static IReadOnlyDictionary<string, (HashSet<int> Dependent, HashSet<int> Direct)>
        CaptureInnerConstraintDependenciesSource(CarrierRealizationContext context)
        => context.Owners.Values.Where(owner => owner.Needed && owner.Inner).ToDictionary(owner => owner.Name,
            owner => (OwnerDependentInnerParametersSource(owner, owner.OuterCount, context),
                DirectOwnerDependentInnerParametersSource(owner, owner.OuterCount, context)), StringComparer.Ordinal);

    internal static Dictionary<string, HashSet<int>> WeakenOwnerDependentInnerConstraintsSource(CarrierRealizationContext context,
        IReadOnlyDictionary<string, (HashSet<int> Dependent, HashSet<int> Direct)> sourceDependencies)
    {
        var result = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var inner in context.Owners.Values.Where(owner => owner.Needed && owner.Inner))
        {
            var source = sourceDependencies.GetValueOrDefault(inner.Name);
            var dependent = source.Dependent ?? OwnerDependentInnerParametersSource(inner, inner.OuterCount, context);
            var direct = source.Direct ?? DirectOwnerDependentInnerParametersSource(inner, inner.OuterCount, context);
            var declaration = context.Definition(inner);
            if (dependent.Count == 0 || !declaration.TypeParams.HasValue) continue;
            var parameters = declaration.TypeParams.Value;
            var recorded = new Dictionary<int, TypeNode[]>();
            foreach (var index in dependent.OrderBy(index => index))
            {
                var parameter = parameters[index];
                if (!parameter.Constraints.HasValue || parameter.Constraints.Value.Length == 0) continue;
                var retained = new List<TypeNode>();
                var removed = new List<TypeNode>();
                foreach (var bound in parameter.Constraints.Value)
                    (InnerConstraintIsRepresentable(index, bound, inner.OuterCount, direct) ? retained : removed)
                        .Add(BirGraphValue.CopyType(bound));
                if (removed.Count == 0) continue;
                recorded[index] = parameter.Constraints.Value.Select(BirGraphValue.CopyType).ToArray();
                parameter.ErasedInnerConstraints = removed.ToArray();
                parameter.Constraints = retained.ToArray();
                if (!result.TryGetValue(inner.Name, out var changed)) result[inner.Name] = changed = new();
                changed.Add(index);
            }
            var facts = NullableRepresentationViews.TypeFacts((BirGraphValue)declaration);
            if (recorded.Count > 0 && facts?.NullableFrame == null)
                NullableRepresentationViews.SetTypeFacts((BirGraphValue)declaration, (facts ?? SourceTypeFacts.Empty).Merge(recorded));
        }
        return result;
    }

    internal static void SynchronizeGeneratedOwnerConstraintPrefixesSource(CarrierRealizationContext context,
        IReadOnlyDictionary<string, HashSet<int>> weakenedOwnerSlots)
    {
        var definitions = context.SourceDefinitions.ToDictionary(pair => pair.Key,
            pair => (IBirTypeDeclaration)pair.Value.Source, StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var completed = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        HashSet<int> Synchronize(string name, IBirTypeDeclaration generated)
        {
            if (completed.TryGetValue(name, out var prior)) return prior;
            if (!visiting.Add(name)) throw new InvalidOperationException($"cyclic semantic owner chain at generated type '{name}'");
            try
            {
                var changed = weakenedOwnerSlots.TryGetValue(name, out var directlyWeakened) ? new HashSet<int>(directlyWeakened) : new();
                if (!generated.SemanticOwner.HasValue || !definitions.TryGetValue(generated.SemanticOwner.Value, out var owner))
                    return completed[name] = changed;
                var ownerName = generated.SemanticOwner.Value;
                var ownerChanges = owner.Generated.OrDefault() ? Synchronize(ownerName, owner)
                    : weakenedOwnerSlots.TryGetValue(ownerName, out var weakened) ? weakened : new HashSet<int>();
                if (ownerChanges.Count == 0) return completed[name] = changed;
                var count = (generated as BirClassDefinition)?.OuterTypeParamCount.OrDefault() ?? 0;
                if (count <= 0 || !generated.TypeParams.HasValue || !owner.TypeParams.HasValue
                    || count > generated.TypeParams.Value.Length || count != owner.TypeParams.Value.Length)
                    throw new InvalidOperationException($"generated type '{name}' cannot synchronize its weakened semantic owner '{ownerName}' frame");
                foreach (var index in ownerChanges.OrderBy(index => index))
                {
                    if (index < 0 || index >= count)
                        throw new InvalidOperationException($"generated type '{name}' has no declaration for weakened owner slot {index}");
                    var source = owner.TypeParams.Value[index];
                    var target = generated.TypeParams.Value[index];
                    target.Constraints = source.Constraints.HasValue
                        ? OptionalField<TypeNode[]>.Of(source.Constraints.Value.Select(BirGraphValue.CopyType).ToArray()) : default;
                    target.ErasedInnerConstraints = source.ErasedInnerConstraints.HasValue
                        ? OptionalField<TypeNode[]>.Of(source.ErasedInnerConstraints.Value.Select(BirGraphValue.CopyType).ToArray()) : default;
                    changed.Add(index);
                }
                return completed[name] = changed;
            }
            finally { visiting.Remove(name); }
        }
        foreach (var (name, definition) in definitions)
            if (definition.Generated.OrDefault()) Synchronize(name, definition);
    }
}
