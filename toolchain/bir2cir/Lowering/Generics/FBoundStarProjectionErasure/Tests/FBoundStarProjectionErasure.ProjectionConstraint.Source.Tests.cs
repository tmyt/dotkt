using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static void ProjectionConstraintSelfTest()
    {
        PhysicalAssignabilitySelfTest();
        static void Need(bool value, string reason)
        { if (!value) throw new InvalidOperationException("Constraint projections: " + reason); }
        static BirClassDefinition Generic(string name, string variance = null) => new() {
            Name = name, Kind = "class", TypeParams = new[] { new BirTypeParameter { Name = "T", Variance = variance } },
        };
        static BirExpressionStatement Evaluate(IBirExpression value) => new() { Expr = OptionalField<IBirExpression>.Of(value) };
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var variable = new TypeNode.Tv("method", 0);
        var exact = new TypeNode.Fqn("Bound", new TypeNode[] { variable });
        var star = exact with { Args = new TypeNode[] { new TypeNode.Star() } };
        var projected = exact with { Args = new TypeNode[] { new TypeNode.Projection("out", variable) } };
        Need(!ContainsUseSiteProjection(exact) && ContainsUseSiteProjection(star)
            && ContainsUseSiteProjection(new TypeNode.Nullable(star)) && ContainsUseSiteProjection(projected),
            "star/exact argument distinction changed");

        var inner = new TypeNode.Fqn("Owner.Item", new TypeNode[] { variable });
        var innerOwner = Generic("Owner.Item");
        innerOwner.SemanticOwner = "Owner"; innerOwner.Mods = new BirModifiers { Inner = true };
        innerOwner.OuterTypeParamCount = 1;
        var constructionFrame = new NullableRepresentationFrame(2,
            Array.Empty<int>(), new[] { 2, 0, 3, 1 }, storageIndices: new[] { 0, 1 });
        var constructionOwner = new BirClassDefinition { Name = "Construction", Kind = "class",
            TypeParams = new[] { "P", "T", "Q", "U" }.Select(name => new BirTypeParameter { Name = name }).ToArray(),
            KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(constructionFrame) };
        var alias = Generic("SourceAlias");
        alias.Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrTypeAlias"),
            Args = new IBirAttributeValue[] { new BirConstant { Value = new BirLiteral.TextLiteral("System.Collections.Generic.IReadOnlyList`1") } } } };
        var local = new BirLocal { Name = "value", Sty = exact };
        var exactCast = new BirCast { Type = exact, _physicalDeclarationSignature = true, E = OptionalField<IBirExpression>.Of(local) };
        var physicalCall = new BirCallInstance { _physicalDeclarationSignature = true,
            Sig = new[] { exact }, CalleeParams = new[] { exact }, Ret = exact, DynRet = exact, Sty = exact,
            Args = new IBirExpression[] { exactCast } };
        var factory = new BirCallStatic { _innerFactoryArguments = true,
            TypeArgs = new TypeNode[] { inner, inner with { Args = new TypeNode[] { new TypeNode.Star() } } } };
        var sourceDescriptor = new BirMethodImpl { Owner = new TypeNode.Fqn("SourceAlias", new TypeNode[] { inner }),
            Params = Array.Empty<TypeNode>(), Ret = inner };
        var nativeDescriptor = new BirMethodImpl {
            Owner = new TypeNode.Fqn("System.Collections.Generic.IReadOnlyList`1", new TypeNode[] { inner }),
            Params = Array.Empty<TypeNode>(), Ret = inner };
        var operations = new BirGraphValue[] {
            new BirNativeInstance { Type = inner }, new BirNativeStatic { Type = inner },
            new BirGenericInstance { Type = inner }, new BirPropertyGet { Type = inner },
            new BirPropertySet { Type = inner }, new BirNewClr { Type = inner },
        };
        var use = new BirFunctionDeclaration { Name = "use", Params = Array.Empty<BirParameter>(),
            ClrInterfaceImpls = new[] { sourceDescriptor, nativeDescriptor },
            Body = new IBirStatement[] { Evaluate(physicalCall), Evaluate(factory) }
                .Concat(operations.Select(operation => (IBirStatement)Evaluate((IBirExpression)operation))).ToArray() };
        var fixture = new BirClassDefinition { Name = "ProjectionFixture", Kind = "class", Generated = true, Methods = new[] { use } };
        var late = Generic("LatePhysical"); late.Generated = true;
        var rootBound = new TypeNode.Fqn("ClassBound");
        var ifaceBound = new TypeNode.Fqn("InterfaceBound");
        var leaf = Generic("DirectLeaf"); leaf.Base = rootBound; leaf.Interfaces = new TypeNode[] { ifaceBound };
        var commonBase = new BirClassDefinition { Name = "Both", Kind = "class", Base = rootBound, Interfaces = new TypeNode[] { ifaceBound } };
        var commonLeaf = Generic("CommonLeaf"); commonLeaf.Base = new TypeNode.Fqn("Both");
        var nonMember = Generic("Unmarked"); nonMember.Base = rootBound;
        var intersection = Generic("Intersection");
        intersection.TypeParams.Value[0].Constraints = new TypeNode[] { rootBound, ifaceBound };
        var methodBounds = new BirFunctionDeclaration { Name = "choose", DeclarationId = "choose", Static = true,
            TypeParams = new[] { rootBound, new TypeNode.Fqn("Both") }.Select(b =>
                new BirTypeParameter { Constraints = new TypeNode[] { b } }).ToArray() };
        leaf.Methods = new[] { methodBounds };
        var methodCall = new BirCallStatic { Owner = new TypeNode.Fqn("DirectLeaf", [rootBound]), Method = "choose",
            DeclarationId = "choose", TypeArgs = new TypeNode[] {
                new TypeNode.Fqn("DirectLeaf", [new TypeNode.Star()]), new TypeNode.Fqn("CommonLeaf", [new TypeNode.Star()]) } };
        use.Body = use.Body.Value.Append(Evaluate(methodCall)).ToArray();
        var interfaceOnly = Generic("FBounded");
        interfaceOnly.TypeParams.Value[0].Constraints = new TypeNode[] {
            new TypeNode.Fqn("Bound", [new TypeNode.Tv("type", 0)]), ifaceBound,
        };
        var twoIntersections = Generic("TwoIntersections");
        twoIntersections.TypeParams = new[] { "X", "Y" }.Select(name => new BirTypeParameter {
            Name = name, Constraints = new TypeNode[] { rootBound, ifaceBound },
        }).ToArray();
        var reorderedFrame = new NullableRepresentationFrame(2, [], [1, 0]);
        var reordered = new BirClassDefinition { Name = "Reordered", Kind = "class", Generated = true,
            TypeParams = new[] { new BirTypeParameter { Name = "B", Constraints = new[] { rootBound } }, new BirTypeParameter { Name = "A" } },
            KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(reorderedFrame) };
        var root = new BirFileDeclaration { FileClass = "ProjectionConstraints", Types = new IBirTypeDeclaration[] {
            Generic("Owner"), innerOwner, Generic("Bound"), constructionOwner, Generic("Variant", "out"), alias, fixture, late,
            new BirClassDefinition { Name = rootBound.Name, Kind = "class" },
            new BirInterfaceDefinition { Name = ifaceBound.Name, Kind = "interface" },
            leaf, commonBase, commonLeaf, nonMember, intersection, twoIntersections, interfaceOnly, reordered,
        } };
        var context = SourceSynthesisTestContext(root, refs);
        foreach (var owner in context.Owners.Values) owner.Needed = true;
        var projection = context.Projection;
        var hiddenLeaf = new TypeNode.Fqn("DirectLeaf", [new TypeNode.Star()]);
        var independent = new TypeNode.Fqn("kotlin.String");
        var reorderedApplication = projection.SelectPhysicalClassApplication(
            new TypeNode.Fqn("Reordered", reorderedFrame.Close([independent, hiddenLeaf], t => t, t => t)));
        var physicalPair = new TypeNode.Fqn("Pair", [new TypeNode.Tv("type", 0), new TypeNode.Tv("type", 1)]);
        Need(!context.Owners.ContainsKey("Reordered") && reorderedApplication.Layout?.SourcePosition(1) == 0
            && reorderedApplication.Slot(0).CloseBound(physicalPair, _ => independent) ==
                new TypeNode.Fqn("Pair", [hiddenLeaf, independent])
            && projection.ClassStorageWitness(reorderedApplication.Slot(0), null, false)?.Class == rootBound
            && projection.ClassStorageWitness(reorderedApplication.Slot(1), null, false) == null
            && projection.ClassStorageWitness(projection.SelectPhysicalClassApplication(new TypeNode.Fqn("Reordered",
                [new TypeNode.Fqn("Owner", [new TypeNode.Star()]), hiddenLeaf])).Slot(0), null, false) == null,
            "same-arity physical applications must not be re-ordered by source index");

        var method = new BirFunctionDeclaration {
            TypeParams = new[] { new BirTypeParameter(), new BirTypeParameter(), new BirTypeParameter() },
        };
        var methodApplication = projection.SelectPhysicalMethodApplication(method, "method", null,
            new TypeNode.Fqn("Owner", [rootBound, independent]), [independent, rootBound, hiddenLeaf]);
        var bothBinders = new TypeNode.Fqn("BothBinders",
            [new TypeNode.Tv("type", 0), new TypeNode.Tv("type", 1), new TypeNode.Tv("method", 0), new TypeNode.Tv("method", 2)]);
        var closedSlots = new List<int>();
        var closedBound = methodApplication.Slot(2).CloseBound(bothBinders, index => { closedSlots.Add(index); return ifaceBound; });
        Need(closedBound == new TypeNode.Fqn("BothBinders", [rootBound, independent, ifaceBound, hiddenLeaf])
            && closedSlots.SequenceEqual([0]),
            "owner slot zero and method slot zero have separate substitution environments");

        CarrierProjectionContext.StorageWitness Witness(string leafName, bool split) {
            var app = new TypeNode.Fqn(leafName, [new TypeNode.Star()]);
            return projection.ClassStorageWitness(projection.SelectPhysicalClassApplication(new TypeNode.Fqn("Intersection", [app])).Slot(0),
                null, split);
        }
        Need(Witness("DirectLeaf", false) == null
            && Witness("DirectLeaf", true) is { Class: var cls, Interfaces: [var iface] } && cls == rootBound && iface == ifaceBound
            && Witness("CommonLeaf", true) is { Class.Name: "Both", Interfaces.Length: 0 }
            && Witness("Unmarked", true) == null, "projected storage needs real, independently proven class and interface bounds");
        var layout = projection.StorageFrame("Intersection");
        var namedFacet = new TypeNode.Fqn("Facet", [new TypeNode.Tv("type", 1)]);
        var physicalFacet = new TypeNode.Fqn("Facet", [new TypeNode.Tv("method", 1)]);
        var published = new ProjectedConstructorPlan(2, 1, [
            ProjectedConstructorPlan.BindFacet(1, namedFacet, false,
                [(ifaceBound, ifaceBound), (physicalFacet, physicalFacet)], t => t),
        ]);
        Need(published.InnerSlots().SequenceEqual([1, 2]) &&
            published.Close([rootBound, independent], (_, b) => b).Last() == new TypeNode.Fqn("Facet", [independent]),
            "the selected constructor facet binds a later constraint row in the shipped method binder");
        Need(projection.StorageFrame("FBounded") == null,
            "a source class bound already projected to a CLR interface does not require separate storage");
        TypeNode.Fqn Applied(string name) => new("Intersection", [new TypeNode.Fqn(name, [new TypeNode.Star()])]);
        var implementationParameters = layout.Declare();
        Need(implementationParameters.Length == 2 && implementationParameters[0].Constraints.Value.SequenceEqual([rootBound])
            && implementationParameters[1].Constraints.Value.SequenceEqual([ifaceBound])
            && intersection.TypeParams.Value[0].Constraints.Value.Length == 2
            && projection.ProjectedConstructorArguments(projection.SelectPhysicalClassApplication(Applied("DirectLeaf")), layout.Constructor(0))
                .SequenceEqual([rootBound, ifaceBound])
            && projection.ProjectedConstructorArguments(projection.SelectPhysicalClassApplication(Applied("CommonLeaf")), layout.Constructor(0)) == null,
            "the private implementation requires a separate frame without changing the exact owner");
        var partlyProven = new TypeNode.Fqn("TwoIntersections", [Applied("DirectLeaf").Args[0], Applied("Unmarked").Args[0]]);
        Need(projection.ProjectedConstructorArguments(projection.SelectPhysicalClassApplication(partlyProven),
                projection.StorageFrame(partlyProven.Name).Constructor(0)) == null,
            "one proven intersection cannot authorize splitting another parameter with a missing interface");
        TypeNode View(string name) => new TypeNode.Fqn(context.Owners[name].ErasedName);
        FunctionRewriteTests.Check("F-bound rewrite", type => projection.Rewrite(type));
        var function = new TypeNode.Fn(false, variable, new TypeNode[] { variable }, null, "System.Func");
        Need(projection.Rewrite(new TypeNode.Mod(false, new TypeNode.Star(), function)) is
            TypeNode.Mod { M: TypeNode.Fqn { Name: "kotlin.Any" }, Of: TypeNode.Fn { Clr: "System.Func" } },
            "modifier traversal or physical delegate family changed");
        Need(projection.Rewrite(inner) == View("Owner.Item") && projection.Rewrite(exact) == View("Bound")
            && projection.Rewrite(inner, boundDeclaration: true) == inner
            && projection.Rewrite(inner, preserveConstructedHead: true) == inner,
            "ordinary carrier projection or exact linkage slot changed");

        ProjectMaterializedTypesSource(new[] { fixture }, context);
        Need(methodCall.TypeArgs.Value.SequenceEqual(new TypeNode[] { rootBound, new TypeNode.Fqn("Both") }),
            "each method witness must come from the actual and constraint at its own physical ordinal");
        Need(physicalCall.Sig.Value[0] == exact && physicalCall.CalleeParams.Value[0] == exact
            && physicalCall.Ret.Value == exact && exactCast.Type.Value == exact && local.Sty.Value == View("Bound")
            && ReferenceEquals(physicalCall.Args.Value[0], exactCast) && ReferenceEquals(exactCast.E.Value, local),
            "physical adapter froze its ordinary operands or lost its declaration contract/identity");
        Need(factory.TypeArgs.Value[0] == inner && factory.TypeArgs.Value[1] == View("Owner.Item"),
            "inner factory lost its exact witness or fabricated a projected construction");
        Need(sourceDescriptor.Ret.Value == View("Owner.Item") && nativeDescriptor.Ret.Value == inner,
            "source alias and selected native MethodImpl lost their distinct value roles");
        foreach (var operation in operations)
            Need(operation.OwnTypeOrReturnStamp == inner,
                "a CLR operation declaring owner became an ordinary Kotlin value slot");
        RemoveTransientFacts(new[] { root });
        Need(!physicalCall._physicalDeclarationSignature.IsPresent && !exactCast._physicalDeclarationSignature.IsPresent
            && !factory._innerFactoryArguments.IsPresent,
            "a consumed physical adapter/factory role remained in the owning graph");

        var component = new TypeNode.Fqn("Bound", new TypeNode[] { new TypeNode.Fqn("kotlin.String") });
        foreach (var element in new TypeNode[] { component, new TypeNode.Nullable(component) })
            Need(projection.Rewrite(new TypeNode.Array(element), boundDeclaration: true) == new TypeNode.Array(element)
                && projection.Rewrite(new TypeNode.Array(element)) == new TypeNode.Array(View("Bound")),
                "native array descriptors stay exact; Kotlin array storage accepts erased values");
        var unresolved = new TypeNode.Fqn("Unresolved.Container", new TypeNode[] { inner });
        var explicitUnresolved = unresolved with { Args = new TypeNode[] {
            new TypeNode.Fqn("Unresolved.Other", new TypeNode[] { new TypeNode.Fqn("System.String") }) } };
        Need(projection.Rewrite(unresolved, preserveConstructedHead: true)
                == new TypeNode.Fqn(unresolved.Name, new[] { View("Owner.Item") })
            && projection.Rewrite(explicitUnresolved, preserveConstructedHead: true) == explicitUnresolved
            && projection.Rewrite(unresolved, boundDeclaration: true) == unresolved,
            "unresolved operation values and bound descriptors were confused");
        var construction = new TypeNode.Fqn("Construction", Enumerable.Repeat<TypeNode>(inner, 4).ToArray());
        var rewritten = (TypeNode.Fqn)projection.Rewrite(construction, preserveConstructedHead: true);
        Need(rewritten.Args[0] == View("Owner.Item") && rewritten.Args[1] == inner
            && rewritten.Args[2] == View("Owner.Item") && rewritten.Args[3] == inner
            && projection.Rewrite(construction) == View("Construction"),
            "physical nullable frame confused ordinary witnesses and storage companions");
        var lateType = new TypeNode.Fqn("LatePhysical", new TypeNode[] { variable });
        Need(projection.Rewrite(lateType) == lateType && !projection.TryExistentialCarrier("LatePhysical", out _),
            "late local physical definition consulted foreign carrier metadata");
        var variant = new TypeNode.Fqn("Variant", new TypeNode[] { variable });
        var variants = construction with { Args = Enumerable.Repeat<TypeNode>(variant, 4).ToArray() };
        Need(((TypeNode.Fqn)projection.Rewrite(variants, preserveConstructedHead: true)).Args.All(argument => argument == View("Variant"))
            && projection.Rewrite(variant, preserveConstructedHead: true) == variant,
            "nested variance regained an invariant witness or changed an exact construction");
        OwnerConstrainedMethodLowering.Reset(refs);
        SourceProjectionContractSelfTest();
        OwnerConstrainedMethodLowering.Reset(refs);
        NativeProofSourceSelfTest();
        LocalAliasDeclarationSelfTest();
        Console.WriteLine("[constraint projections] self-test OK (owning roles, exact descriptors, source frames, physical adapters and shared scalar policies)");
    }

    static void PhysicalAssignabilitySelfTest()
    {
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var definitions = new Dictionary<string, IBirTypeDeclaration>(StringComparer.Ordinal) {
            ["object"] = new BirClassDefinition { Name = "object", Kind = "class" },
            ["Proof.Narrow"] = new BirClassDefinition { Name = "Proof.Narrow", Kind = "class", Base = new TypeNode.Fqn("object") },
            ["Proof.Value"] = new BirClassDefinition { Name = "Proof.Value", Kind = "struct",
                Interfaces = new TypeNode[] { new TypeNode.Fqn("Proof.Marker") } },
            ["Proof.Marker"] = new BirInterfaceDefinition { Name = "Proof.Marker", Kind = "interface" },
            ["Proof.Stream"] = new BirInterfaceDefinition { Name = "Proof.Stream", Kind = "interface",
                TypeParams = new[] { new BirTypeParameter { Name = "T", Variance = "out" } } },
        };
        TypeNode Stream(string element) => new TypeNode.Fqn("Proof.Stream", new[] { new TypeNode.Fqn(element) });
        if (!IsClrAssignable(new TypeNode.Fqn("Proof.Narrow"), new TypeNode.Fqn("object"), definitions, refs, physicalTypes: true)
            || !IsClrAssignable(Stream("Proof.Narrow"), Stream("object"), definitions, refs, physicalTypes: true)
            || IsClrAssignable(Stream("Proof.Value"), Stream("object"), definitions, refs, physicalTypes: true)
            || IsClrAssignable(Stream("object"), Stream("Proof.Narrow"), definitions, refs, physicalTypes: true))
            throw new InvalidOperationException("Physical assignability lost reference-only widening");
        if (!IsClrAssignable(new TypeNode.Fqn("Proof.Value"), new TypeNode.Fqn("object"), definitions, refs,
                physicalTypes: true, allowBoxing: true)
            || !IsClrAssignable(new TypeNode.Fqn("Proof.Value"), new TypeNode.Fqn("Proof.Marker"), definitions, refs,
                physicalTypes: true, allowBoxing: true)
            || IsClrAssignable(Stream("Proof.Value"), Stream("object"), definitions, refs,
                physicalTypes: true, allowBoxing: true))
            throw new InvalidOperationException("Root boxing leaked into CLR generic variance or lost its interface");
        Console.WriteLine("[physical assignability] self-test OK (reference widening, root boxing, invariant value arguments)");
    }
}
