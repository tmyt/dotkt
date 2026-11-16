using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static void RealizeCarriersSource(IReadOnlyList<BirFileDeclaration> roots,
        CarrierRealizationContext context, IReadOnlyList<SharedSyntheticSynthesis.CellStorage> sharedCells,
        Func<TypeNode, IBirExpression, IBirExpression> componentPredicate,
        Action<BirFileDeclaration, IReadOnlyList<IBirTypeDeclaration>> materializedTypes)
    {
        OwnerConstrainedMethodLowering.ConfigurePreparedMethodsSource(context.Bindings, context.References);
        foreach (var owner in context.Owners.Values.Where(owner => owner.Needed)) SynthesizeSource(owner, context);
        foreach (var root in roots) RecordDeclarationSurfacesSource(root, context);
        TypeNode Classifier(TypeNode bound) => PhysicalBoundClassifier(bound, context.Projection);
        TypeNode ProjectBound(TypeNode bound) => ProjectOwnerMethodBound(bound, context.Projection, physical: false);
        var generated = OwnerConstrainedMethodLowering.PropagateCapturedBoundsSource(context, roots,
            ContainsOwnerTv, ProjectBound, Classifier);
        foreach (var root in roots) EraseProjectedAliasConstraintsSource(root, context.References);
        ProjectGeneratedConstraintFramesSource(context, generated);
        OwnerConstrainedMethodLowering.CloseOwnersSource(context, ContainsOwnerTv, ProjectBound, Classifier);
        foreach (var root in roots) CloseSuspendLambdaOwnersSource(root, context);

        var carriers = context.Owners.Values.Where(owner => owner.Needed)
            .ToDictionary(owner => owner.Name, owner => owner.ErasedName, StringComparer.Ordinal);
        var foreign = ForeignStarProjectionBinding.ApplyAll(roots, carriers, context.References,
            context.Preparation.LocalClrAliases,
            (type, shape) => context.Projection.Rewrite(type, preserveArrayShape: shape),
            types => {
                foreach (var type in types)
                {
                    // This producer reports actual appended declarations. These
                    // addresses extend bindings, never original eligibility or
                    // the captured semantic/signature catalogs.
                    context.Bindings.GetSource(type);
                    foreach (var constructor in type.Ctors.OrEmpty()) context.Bindings.GetSource(constructor);
                    foreach (var method in type.Methods.OrEmpty())
                    {
                        context.Bindings.GetSource(method);
                        foreach (var implementation in method.ClrInterfaceImpls.OrEmpty())
                            context.Bindings.GetSource(implementation);
                    }
                }
                ProjectMaterializedTypesSource(types, context);
            });
        foreach (var report in foreign) materializedTypes(report.File, report.AppendedTypes);

        // Snapshot this classification after factory/foreign materialization,
        // before ordinary value projection weakens the source bound graph.
        var dependencies = CaptureInnerConstraintDependenciesSource(context);
        RewriteCarrierBodiesSource(roots, context, sharedCells, componentPredicate);
        var weakened = WeakenOwnerDependentInnerConstraintsSource(context, dependencies);
        foreach (var (name, slots) in generated)
        {
            if (!weakened.TryGetValue(name, out var changed)) weakened[name] = changed = new HashSet<int>();
            changed.UnionWith(slots);
        }
        SynchronizeGeneratedOwnerConstraintPrefixesSource(context, weakened);
        OwnerConstrainedMethodLowering.ApplySource(context, ContainsOwnerTv,
            bound => ProjectOwnerMethodBound(bound, context.Projection), Classifier);
        foreach (var root in roots) ErasedElementValues.ClearRetainedVariables(root);
    }
}
