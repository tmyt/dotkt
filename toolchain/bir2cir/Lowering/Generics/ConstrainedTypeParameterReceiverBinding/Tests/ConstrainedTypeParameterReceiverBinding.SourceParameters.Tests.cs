using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class ConstrainedTypeParameterReceiverBinding
{
    static void SourceParameterCloneSelfTest()
    {
        static void Require(bool condition)
        { if (!condition) throw new InvalidOperationException("Typed erased-bound header clone changed its declaration frame or ownership"); }
        var retained = new TypeNode.Fqn("Retained");
        var dispatch = new TypeNode.Fqn("Dispatch");
        var projected = new TypeNode.Fqn("Contract", new TypeNode[] { new TypeNode.Tv("method", 1), new TypeNode.Star() });
        var method = new BirFunctionDeclaration {
            TypeParams = new[] {
                new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { retained } },
                new BirTypeParameter { Name = "U" }, new BirTypeParameter { Name = "Untouched" },
            },
            _ownerConstraintDispatchBounds = new OwnerConstraintDispatchBounds(new Dictionary<int, TypeNode[]> {
                [0] = new TypeNode[] { dispatch }, [1] = Array.Empty<TypeNode>(),
            }),
            MethodTypeParameterBoundsPre = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> {
                [0] = new TypeNode[] { projected },
            }),
        };
        var copied = CloneMethodParametersWithErasedSourceBounds(method);
        Require(copied.Length == 3 && !ReferenceEquals(copied[0], method.TypeParams.Value[0])
            && copied[0].Constraints.Value.Single() == retained
            && TypeNodeDataEquality.Equal(copied[0].ErasedInnerConstraints.Value.Single(), projected)
            && copied[1].ErasedInnerConstraints.HasValue && copied[1].ErasedInnerConstraints.Value.Length == 0
            && !copied[2].ErasedInnerConstraints.IsPresent);
        copied[0].Constraints.Value[0] = dispatch;
        ((TypeNode.Fqn)copied[0].ErasedInnerConstraints.Value[0]).Args[0] = new TypeNode.Star();
        Require(method.TypeParams.Value[0].Constraints.Value[0] == retained
            && !method.TypeParams.Value[0].ErasedInnerConstraints.IsPresent
            && projected.Args[0] == new TypeNode.Tv("method", 1));

        var frame = new NullableRepresentationFrame(2, Array.Empty<int>(), new[] { 2, 1, 0 }, storageIndices: new[] { 0 });
        var source = new SourceTypeFacts(new Dictionary<int, TypeNode[]> {
            [0] = new TypeNode[] { new TypeNode.Fqn("Contract", new TypeNode[] { new TypeNode.Tv("type", 0), new TypeNode.Star() }) },
        }).MergeNullableFrame(frame);
        var owner = new BirClassDefinition {
            CapturedTypeParams = new[] { new BirTypeParameter { Name = "Storage" } },
            TypeParams = new[] { new BirTypeParameter { Name = "Other" }, new BirTypeParameter { Name = "Source" } },
            KotlinSupertypesPre = source,
        };
        var ownerCopy = CloneTypeParametersWithErasedSourceBounds(owner);
        Require(ownerCopy.Length == 3 && ownerCopy[0].Name.Value == "Storage"
            && !ownerCopy[0].ErasedInnerConstraints.IsPresent
            && ownerCopy[2].ErasedInnerConstraints.Value is [TypeNode.Fqn { Args: [TypeNode.Tv { Scope: "type", I: 2 }, TypeNode.Star] }]
            && source.Bounds[0][0] is TypeNode.Fqn { Args: [TypeNode.Tv { I: 0 }, TypeNode.Star] });
        owner.KotlinSupertypesPre = default;
        owner.RoundtripSupertypesPre = source;
        Require(CloneTypeParametersWithErasedSourceBounds(owner).All(parameter => !parameter.ErasedInnerConstraints.IsPresent));
    }
}
