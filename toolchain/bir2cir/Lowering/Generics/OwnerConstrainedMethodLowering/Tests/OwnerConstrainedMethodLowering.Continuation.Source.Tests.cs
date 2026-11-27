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
            FBoundStarProjectionErasure.CarrierProjectionContext.ForSource(surface.Demand, refs), refs);
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
        var ledger = new SourceDeclarationProjection(roots, bindings);
        ledger.SealSourceContinuations();
        Require(Methods.Any(row => ReferenceEquals(row.Method, captured))
            && Methods.Any(row => ReferenceEquals(row.Method, caller))
            && ReferenceEquals(root.Methods.Value[0], captured) && ReferenceEquals(owner.Methods.Value[0], caller),
            "source registration did not retain the exact continuing method objects");
        FBoundStarProjectionErasure.CompleteRealization(context);
        bindings.CompleteSourceCustody(ledger);
        ledger.Release();

        var plans = PlanConstraintRewrite(new TypeNode[][] { null, Array.Empty<TypeNode>() }, Array.Empty<TypeNode[]>(),
            null, null, refs, _ => false, type => type, _ => null, _ => new[] { native });
        Require(plans[0] == null && plans[1].Retained.Single() == native && plans[1].Removed.Length == 0,
            "absent and empty constraint vectors were conflated");
        Reset(refs);
        Console.WriteLine("[owning method constraint continuation] self-test OK (captured root method, explicit rows, owner closing, absence and one frontier)");
    }

    static void ConstraintRewritePlanSelfTest(TypeNode nonGenericBound)
    {
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var unchanged = PlanConstraintRewrite(new TypeNode[][] { new[] { nonGenericBound }, null },
            Array.Empty<TypeNode[]>(), null, null, refs, _ => false, type => type, _ => null, _ => Array.Empty<TypeNode>());
        if (unchanged[0].Retained.Single() != nonGenericBound || unchanged[0].Removed.Length != 0 || unchanged[1] != null)
            throw new InvalidOperationException("Non-generic physical bound was removed or an absent constraint row was created");
        var ownerVariable = new TypeNode.Tv("type", 0);
        var value = new TypeNode.Fqn("Value");
        var other = new TypeNode.Fqn("Other");
        BirTypeParameter Parameter(string name, params TypeNode[] constraints) => new() { Name = name, Constraints = constraints };
        var implementation = new BirFunctionDeclaration { TypeParams = new[] {
            Parameter("P", value), Parameter("Y", other), Parameter("X", ownerVariable, value),
        }, NullableRepresentationFrame = new NullableRepresentationFrame(2, Array.Empty<int>(), new[] { 2, 1, 0 }, storageIndices: new[] { 0 }) };
        RecordOverride(implementation, new[] { Parameter("X", ownerVariable), Parameter("Y") }, new TypeNode[] { ownerVariable },
            new[] { Parameter("T") }, new[] { Parameter("B", value) }, sourceParameters: true);
        var plans = PlanConstraintRewrite(implementation.TypeParams.Value.Select(parameter => parameter.Constraints.Value).ToArray(),
            new TypeNode[][] { new[] { value } }, implementation.NullableRepresentationFrame.Value,
            implementation._ownerConstraintOverrideBounds.Value, refs, _ => false, type => type, _ => null, _ => Array.Empty<TypeNode>());
        if (plans[0].Retained.Length != 0 || plans[2].Retained.Length != 0 || plans[1].Retained.Single() != other
            || plans[0].Removed[0] != ownerVariable || plans[2].Removed.Any(bound => bound != ownerVariable)
            || implementation.TypeParams.Value[0].Constraints.Value.Single() != value)
            throw new InvalidOperationException("Selected override slot lost its transitive plan, unrelated row or planner input isolation");
        var bounds = new[] { nonGenericBound, other };
        foreach (var order in new[] { bounds, bounds.Reverse().ToArray() })
        {
            var slots = new OwnerConstraintOverrideBounds(order.Select(bound =>
                new OwnerConstraintOverrideBounds.Row(0, value, ownerVariable, [bound], [value])));
            var merged = PlanConstraintRewrite([[value]], [], null, slots, refs, _ => false,
                type => type, _ => null, _ => [])[0];
            if (!merged.Retained.ToHashSet().SetEquals(bounds) || !merged.Removed.SequenceEqual([value]))
                throw new InvalidOperationException("Override order selected only one inherited constraint for the same bound");
        }
    }

    static void CapturedBoundsSourceSelfTest()
    {
        var oldMethods = Methods.ToArray();
        var oldBindings = MethodBindings;
        var oldPrepared = PreparedMethods;
        var oldReferences = References;
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var unit = new TypeNode.Fqn("kotlin.Unit");
        BirTypeParameter Parameter(string name, params TypeNode[] constraints) => new() {
            Name = name, Constraints = constraints.Length == 0 ? default(OptionalField<TypeNode[]>) : OptionalField<TypeNode[]>.Of(constraints),
        };
        FBoundStarProjectionErasure.CarrierRealizationContext Context(BirFileDeclaration file)
        {
            var roots = new[] { file };
            var values = GenericRepresentationPolicy.CreateSource(roots, refs.Aliases, refs);
            var bindings = FBoundStarProjectionErasure.PrepareCurrentDeclarations(roots, values, out var preparation);
            var proofs = FBoundStarProjectionErasure.PrepareNativeProofs(bindings, refs, values);
            var surface = FBoundStarProjectionErasure.PrepareCarrierSurface(preparation, proofs, refs, values);
            return new(surface, FBoundStarProjectionErasure.CarrierProjectionContext.ForSource(surface.Demand, refs),
                refs);
        }
        void Propagate(BirFileDeclaration root, FBoundStarProjectionErasure.CarrierRealizationContext context)
            => PropagateCapturedBoundsSource(context, new[] { root }, _ => false, type => type, _ => null);
        try
        {
            var named = new BirClassDefinition { Name = "Capture", Kind = "class", Generated = true,
                TypeParams = new[] { Parameter("P", new TypeNode.Array(new TypeNode.Tv("type", 1))), Parameter("T") },
                KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(new NullableRepresentationFrame(2,
                    Array.Empty<int>(), new[] { 1, 0 })) };
            var caller = new BirFunctionDeclaration { Name = "caller", Params = Array.Empty<BirParameter>(), Ret = unit,
                TypeParams = new[] { Parameter("T"), new BirTypeParameter { Name = "P", Constraints = Array.Empty<TypeNode>(),
                    ErasedInnerConstraints = new TypeNode[] { new TypeNode.Array(new TypeNode.Tv("method", 0)) } } },
                Body = new IBirStatement[] { new BirExpressionStatement { Expr = new BirNewClosure {
                    ClosureType = new TypeNode.Fqn("Capture"), TypeArgs = new TypeNode[] {
                        new TypeNode.Tv("method", 1), new TypeNode.Tv("method", 0) }, Captures = Array.Empty<IBirExpression>() } } } };
            var root = new BirFileDeclaration { FileClass = "NamedCaptures", Types = new IBirTypeDeclaration[] { named }, Methods = new[] { caller } };
            var context = Context(root);
            var source = named.KotlinSupertypesPre.Value;
            Propagate(root, context);
            if (!ReferenceEquals(source, named.KotlinSupertypesPre.Value)
                || named.TypeParams.Value[0].Name.Value != "P" || named.TypeParams.Value[0].Constraints.Value.Length != 0
                || named.TypeParams.Value[0].ErasedInnerConstraints.Value.Single() != new TypeNode.Array(new TypeNode.Tv("type", 1)))
                throw new InvalidOperationException("Named capture slot lost its physical dispatch bound or changed original Kotlin frame facts");
            caller.TypeParams.Value[1].ErasedInnerConstraints = default;
            caller.TypeParams.Value[1].Constraints = new TypeNode[] { new TypeNode.Tv("method", 0) };
            named.TypeParams.Value[0].Constraints = new TypeNode[] { new TypeNode.Tv("type", 1) };
            named._ownerConstraintDispatchBounds = default;
            Propagate(root, context);
            if (named.TypeParams.Value[0].Constraints.Value.Single() != new TypeNode.Tv("type", 1))
                throw new InvalidOperationException("Named capture slot lost its retained physical bound");

            foreach (var carriesUncaptured in new[] { true, false })
            {
                var shared = new BirClassDefinition { Name = "Shared", Kind = "class", Generated = true,
                    TypeParams = new[] { Parameter("T") } };
                var bounds = new List<TypeNode> { new TypeNode.Fqn("kotlin.Enum") };
                if (carriesUncaptured) bounds.Add(new TypeNode.Tv("method", 1));
                var use = new BirFunctionDeclaration { Name = "use", Params = Array.Empty<BirParameter>(), Ret = unit,
                    TypeParams = new[] { Parameter("T", bounds.ToArray()), Parameter("N") }, Body = new IBirStatement[] {
                        new BirExpressionStatement { Expr = new BirNew { Type = new TypeNode.Fqn("Shared",
                            new TypeNode[] { new TypeNode.Tv("method", 0) }), Args = Array.Empty<IBirExpression>() } } } };
                var file = new BirFileDeclaration { FileClass = "SharedCaptures", Types = new IBirTypeDeclaration[] { shared }, Methods = new[] { use } };
                Propagate(file, Context(file));
                if (shared.TypeParams.Value[0].Constraints.IsPresent)
                    throw new InvalidOperationException("Caller strengthened an unrelated shared generated declaration");
            }

            foreach (var scope in new[] { "type", "method", "constructor", "ordinary" })
            {
                var sourceBounds = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> { [0] = Array.Empty<TypeNode>() });
                var target = new BirFunctionDeclaration { Name = "capture", DeclarationId = "capture-static", Generated = scope != "ordinary",
                    Static = true, Params = Array.Empty<BirParameter>(), Ret = unit, Body = Array.Empty<IBirStatement>(),
                    TypeParams = new[] { Parameter("T"), Parameter("S", new TypeNode.Array(new TypeNode.Tv("method", 0))) },
                    NullableRepresentationFrame = new NullableRepresentationFrame(1, Array.Empty<int>(), storageIndices: new[] { 0 }),
                    MethodTypeParameterBoundsPre = sourceBounds };
                var methodScope = scope == "method";
                var parameterScope = methodScope ? "method" : "type";
                var parameters = new[] { Parameter("T"), new BirTypeParameter { Name = "S",
                    ErasedInnerConstraints = new TypeNode[] { new TypeNode.Array(new TypeNode.Tv(parameterScope, 0)) } } };
                var body = new IBirStatement[] { new BirExpressionStatement { Expr = new BirNewDelegate {
                    Method = "capture", DeclarationId = "capture-static", CalleeOwner = new TypeNode.Fqn("CaptureStatics"),
                    TypeArgs = new TypeNode[] { new TypeNode.Tv(parameterScope, 0), new TypeNode.Tv(parameterScope, 1) },
                    FuncType = new TypeNode.Fn(false, unit, Array.Empty<TypeNode>()), Sig = Array.Empty<TypeNode>() } } };
                var owner = new BirClassDefinition { Name = "Lexical", Kind = "class", TypeParams = methodScope ? default : parameters };
                var use = new BirFunctionDeclaration { Name = "use", Params = Array.Empty<BirParameter>(), Ret = unit,
                    TypeParams = methodScope ? parameters : default, Body = body };
                if (scope == "constructor") owner.Ctors = new[] { new BirConstructor { Params = Array.Empty<BirParameter>(), Body = body } };
                else if (!methodScope) owner.Methods = new[] { use };
                var file = new BirFileDeclaration { FileClass = "CaptureStatics", Types = new IBirTypeDeclaration[] { owner },
                    Methods = methodScope ? new[] { target, use } : new[] { target } };
                Propagate(file, Context(file));
                if (!ReferenceEquals(target.MethodTypeParameterBoundsPre.Value, sourceBounds)
                    || (scope == "ordinary" ? target.TypeParams.Value[1].Constraints.Value.Length != 1
                        : target.TypeParams.Value[1].Constraints.Value.Length != 0
                            || target.TypeParams.Value[1].ErasedInnerConstraints.Value.Single() != new TypeNode.Array(new TypeNode.Tv("method", 0))))
                    throw new InvalidOperationException("Static capture changed its original source bounds, lost its " + scope + " frame or admitted an ordinary method");
            }
        }
        finally
        {
            Methods.Clear(); Methods.AddRange(oldMethods); MethodBindings = oldBindings; PreparedMethods = oldPrepared; References = oldReferences;
        }
    }
}
