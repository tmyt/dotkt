using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static void ConstraintSurfacesSourceSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning constraint surfaces: " + message); }
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var outerVariable = new TypeNode.Tv("type", 0);
        var projected = new TypeNode.Fqn("Box", new TypeNode[] { new TypeNode.Star() });
        var array = new TypeNode.Array(projected);
        var unit = new TypeNode.Fqn("kotlin.Unit");
        var native = new TypeNode.Fqn("System.IDisposable");
        var owner = new BirClassDefinition { Name = "Box", Kind = "class", Vis = "public",
            TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { projected } } },
            Fields = new[] { new BirFieldDeclaration { Name = "items", Type = array } } };
        var parameter = new BirParameter { Name = "items", Type = array };
        var invoke = new BirFunctionDeclaration { Name = "invoke", DeclarationId = "invoke", Params = new[] { parameter },
            Ret = array, Abstract = true, Body = Array.Empty<IBirStatement>() };
        var functionInterface = new BirInterfaceDefinition { Name = "ArrayFunction", Kind = "interface", Vis = "public",
            Methods = new[] { invoke } };
        var inner = new BirClassDefinition { Name = "Box.Inner", Kind = "class", SemanticOwner = "Box", Vis = "public",
            Mods = new BirModifiers { Inner = true }, OuterTypeParamCount = 1,
            TypeParams = new[] { new BirTypeParameter { Name = "T" },
                new BirTypeParameter { Name = "E", Constraints = new TypeNode[] { outerVariable } } } };
        var generatedParameter = new BirTypeParameter { Name = "E", Constraints = new TypeNode[] { outerVariable } };
        var generatedOwn = new BirTypeParameter { Name = "Own", Constraints = new TypeNode[] { native } };
        var generated = new BirClassDefinition { Name = "CapturedInner", Kind = "class", Generated = true,
            SemanticOwner = "Box.Inner", OuterTypeParamCount = 2,
            TypeParams = new[] { new BirTypeParameter { Name = "T" }, generatedParameter, generatedOwn } };
        var frame = new NullableRepresentationFrame(2, Array.Empty<int>(), storageIndices: new[] { 0 });
        var physicalFrames = new BirClassDefinition { Name = "PhysicalFrames", Kind = "class", Generated = true,
            TypeParams = new[] { new BirTypeParameter { Name = "T" },
                new BirTypeParameter { Name = "U", Constraints = new TypeNode[] { new TypeNode.Array(outerVariable) } },
                new BirTypeParameter { Name = "Storage" } },
            KotlinSupertypesPre = new SourceTypeFacts(new Dictionary<int, TypeNode[]> { [0] = new TypeNode[] { native } })
                .MergeNullableFrame(frame) };
        var root = new BirFileDeclaration { FileClass = "ConstraintSurfaces",
            Types = new IBirTypeDeclaration[] { owner, functionInterface, inner, generated, physicalFrames } };
        var context = SourceSynthesisTestContext(root, refs);
        RecordDeclarationSurfacesSource(root, context);
        Require(invoke.RetKotlinType.Value == array && parameter.KotlinType.Value == array
            && owner.Fields.Value[0].KotlinType.Value == array && owner.KotlinSupertypesPre.Value.Bounds[0].Single() == projected,
            "actual fun-interface invoke/parameter/field array truth or complete owner bound snapshot was lost");
        Require(!physicalFrames.KotlinSupertypesPre.Value.Bounds.ContainsKey(1),
            "physical companion constraints were captured as Kotlin source bounds");
        var priorInvokeFact = invoke.RetKotlinType.Value;
        invoke.Ret = new TypeNode.Fqn("kotlin.Any");
        RecordDeclarationSurfacesSource(root, context);
        Require(ReferenceEquals(invoke.RetKotlinType.Value, priorInvokeFact), "a later physical return overwrote the first source fact");

        var changes = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        ProjectGeneratedConstraintFramesSource(context, changes);
        Require(changes["PhysicalFrames"].Contains(1)
            && physicalFrames.TypeParams.Value[1].Constraints.Value.Length == 0
            && physicalFrames._ownerConstraintDispatchBounds.Value.Bounds[1].Single() == new TypeNode.Array(new TypeNode.Star())
            && !physicalFrames.KotlinSupertypesPre.Value.Bounds.ContainsKey(1),
            "generated physical dispatch projection changed the preserved Kotlin frame");
        var dependencies = CaptureInnerConstraintDependenciesSource(context);
        var weakened = WeakenOwnerDependentInnerConstraintsSource(context, dependencies);
        SynchronizeGeneratedOwnerConstraintPrefixesSource(context, weakened);
        Require(weakened["Box.Inner"].Contains(1) && inner.TypeParams.Value[1].Constraints.Value.Length == 0
            && generatedParameter.Constraints.Value.Length == 0 && generatedParameter.ErasedInnerConstraints.Value.Single() == outerVariable
            && !ReferenceEquals(generatedParameter.ErasedInnerConstraints.Value, inner.TypeParams.Value[1].ErasedInnerConstraints.Value)
            && ReferenceEquals(generated.TypeParams.Value[1], generatedParameter)
            && ReferenceEquals(generated.TypeParams.Value[2], generatedOwn) && generatedOwn.Constraints.Value.Single() == native,
            "exact generated prefix synchronization replaced declarations, shared vectors or weakened an own parameter");
        projected.Args[0] = new TypeNode.Fqn("kotlin.String");
        Require(owner.KotlinSupertypesPre.Value.Bounds[0].Single() is TypeNode.Fqn { Args: [TypeNode.Star] }
            && ((TypeNode.Array)parameter.KotlinType.Value).Elem is TypeNode.Fqn { Args: [TypeNode.Star] },
            "saved source facts shared a mutable type argument array with physical declarations");
        Console.WriteLine("[owning constraint surfaces] self-test OK (method/field/parameter metadata, physical frames, inner weakening and exact prefixes)");
    }
}
