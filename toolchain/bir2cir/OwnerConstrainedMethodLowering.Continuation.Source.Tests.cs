using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class OwnerConstrainedMethodLowering
{
    internal static void ContinuationSourceSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning method constraints: " + message); }
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var ownerVariable = new TypeNode.Tv("type", 0);
        var methodVariable = new TypeNode.Tv("method", 0);
        var native = new TypeNode.Fqn("System.IDisposable");
        var markerBound = new TypeNode.Fqn("Marker", new TypeNode[] { ownerVariable });
        var unit = new TypeNode.Fqn("kotlin.Unit");
        var captured = new BirFunctionDeclaration { Name = "capture", DeclarationId = "capture", Generated = true, Static = true,
            Params = Array.Empty<BirParameter>(), Ret = unit, Body = Array.Empty<IBirStatement>(),
            TypeParams = new[] { new BirTypeParameter { Name = "U", Constraints = new TypeNode[] { new TypeNode.Tv("method", 1) } },
                new BirTypeParameter { Name = "T", Constraints = new TypeNode[] {
                    new TypeNode.Fqn("Marker", new TypeNode[] { new TypeNode.Tv("method", 1) }), native } } } };
        var call = new BirCallInstance { OwnerType = new TypeNode.Fqn("Marker"), Method = "use",
            Recv = OptionalField<IBirExpression>.Of(new BirLocal { Name = "value", Sty = methodVariable }),
            Args = Array.Empty<IBirExpression>(), Ret = unit };
        var caller = new BirFunctionDeclaration { Name = "caller", DeclarationId = "caller", Ret = unit,
            TypeParams = new[] { new BirTypeParameter { Name = "U", Constraints = new TypeNode[] { ownerVariable } } },
            Params = new[] { new BirParameter { Name = "value", Type = methodVariable } },
            Body = new IBirStatement[] {
                new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(new BirNewDelegate {
                    Method = "capture", DeclarationId = "capture", CalleeOwner = new TypeNode.Fqn("ConstraintStatics"),
                    TypeArgs = new TypeNode[] { methodVariable, ownerVariable }, Sig = Array.Empty<TypeNode>(),
                    FuncType = new TypeNode.Fn(false, unit, Array.Empty<TypeNode>()) }) },
                new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(call) } } };
        var owner = new BirClassDefinition { Name = "ConstraintOwner", Kind = "class", Vis = "public",
            TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { markerBound, native } } },
            Methods = new[] { caller } };
        var marker = new BirInterfaceDefinition { Name = "Marker", Kind = "interface", Vis = "public",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = Array.Empty<BirFunctionDeclaration>() };
        var root = new BirFileDeclaration { FileClass = "ConstraintStatics", Types = new IBirTypeDeclaration[] { owner, marker },
            Methods = new[] { captured } };
        var roots = new[] { root };
        var values = GenericRepresentationPolicy.CreateSource(roots, refs.Aliases, refs);
        var bindings = FBoundStarProjectionErasure.PrepareCurrentDeclarations(roots, values, out var preparation);
        var proofs = FBoundStarProjectionErasure.PrepareNativeProofs(bindings, refs, values);
        var surface = FBoundStarProjectionErasure.PrepareCarrierSurface(preparation, proofs, refs, values);
        var context = new FBoundStarProjectionErasure.CarrierRealizationContext(surface,
            FBoundStarProjectionErasure.CarrierProjectionContext.ForSource(surface.Demand, refs), refs, surface.Demand.ReservedNames);
        var originalCatalog = bindings.SourceLocalDeclarations;
        var originalCount = originalCatalog.Count;
        ConfigurePreparedMethodsSource(bindings, refs);
        var changed = PropagateCapturedBoundsSource(context, roots, FBoundStarProjectionErasure.ContainsOwnerTv,
            type => type, _ => null);
        Require(changed.Count == 0 && captured.TypeParams.Value[0].Constraints.Value.SequenceEqual(new[] { native })
            && captured.TypeParams.Value[0].ErasedInnerConstraints.Value.Single() == new TypeNode.Tv("method", 1)
            && captured.MethodTypeParameterBoundsPre.Value.Bounds[0].Single() == new TypeNode.Tv("method", 1),
            "generated top-level capture lost its exact original obligation or used a nonempty root owner frame");
        CloseOwnersSource(context, FBoundStarProjectionErasure.ContainsOwnerTv, type => type, _ => null);
        Require(call.OwnerType.Value == markerBound && call._erasedConstraintDispatch.Value,
            "refs-aware body closure lost its selected erased bound or constructed owner");
        ApplySource(context, FBoundStarProjectionErasure.ContainsOwnerTv, type => type, _ => null);
        Require(caller.TypeParams.Value[0].Constraints.Value.SequenceEqual(new[] { native })
            && caller.TypeParams.Value[0].ErasedInnerConstraints.Value.Single() == ownerVariable
            && caller._ownerConstraintDispatchBounds.Value.Bounds[0].Single() == ownerVariable
            && ReferenceEquals(bindings.SourceLocalDeclarations, originalCatalog) && originalCatalog.Count == originalCount,
            "final physical constraints/dispatch facts or original catalog membership changed");
        var physical = root.ToDom(bindings.ObserveProjection);
        bindings.BindProjected();
        ConfigurePreparedMethods(bindings, refs, _ => null, (method, index) => proofs.Bounds(method, index));
        Require(Methods.Any(row => ReferenceEquals(row.Method, physical["methods"][0]))
            && Methods.Any(row => ReferenceEquals(row.Method, physical["types"][0]["methods"][0])),
            "source registration did not continue to the exact physical method objects");

        var plans = PlanConstraintRewrite(new TypeNode[][] { null, Array.Empty<TypeNode>() }, Array.Empty<TypeNode[]>(),
            null, null, refs, _ => false, type => type, _ => null, _ => new[] { native });
        Require(plans[0] == null && plans[1].Retained.Single() == native && plans[1].Removed.Length == 0,
            "absent and empty constraint vectors were conflated");
        Reset(refs, _ => null);
        Console.WriteLine("[owning method constraint continuation] self-test OK (captured root method, explicit rows, owner closing, absence and one frontier)");
    }
}
