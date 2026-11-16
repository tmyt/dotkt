using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static void ArraySourceFactsSelfTest()
    {
        var text = new TypeNode.Fqn("kotlin.String");
        var array = new TypeNode.Array(text);
        var nested = new TypeNode.Fqn("Container", new TypeNode[] { new TypeNode.Nullable(array) });
        var sibling = new TypeNode.Fqn("Sibling");
        BirTypeParameter Row(string name) => new() { Name = name,
            Constraints = new TypeNode[] { nested, sibling } };
        var earlier = new TypeNode.Fqn("Earlier");
        var method = new BirFunctionDeclaration { Name = "read", Params = Array.Empty<BirParameter>(),
            TypeParams = new[] { Row("T"), Row("U") },
            MethodTypeParameterBoundsPre = new SourceBoundsSnapshot(
                new Dictionary<int, TypeNode[]> { [0] = new TypeNode[] { earlier } }) };
        var implementation = new BirInheritedImplementation { Kind = "method",
            TypeParams = new[] { Row("V") } };
        var inherited = new BirInheritedClassMethod { Member = "inherited",
            Params = Array.Empty<BirParameter>(), TypeParams = new[] { Row("W") },
            InheritedImplementation = implementation };
        var owner = new BirClassDefinition { Name = "Owner", Kind = "class", Base = nested,
            Interfaces = new TypeNode[] { sibling,
                new TypeNode.Fqn("Contract", new TypeNode[] { new TypeNode.Oblivious(array) }) },
            TypeParams = new[] { Row("T") },
            CapturedTypeParams = new[] { Row("Captured") },
            Methods = new[] { method }, InheritedClassMethods = new[] { inherited },
            KotlinSupertypesPre = SourceTypeFacts.Empty.Merge(earlier, null, null) };
        var frame = new NullableRepresentationFrame(1, Array.Empty<int>());
        var framedMethod = new BirFunctionDeclaration { Name = "physical", Params = Array.Empty<BirParameter>(),
            TypeParams = new[] { Row("T") }, NullableRepresentationFrame = frame };
        var framedOwner = new BirInterfaceDefinition { Name = "Physical", Kind = "interface", Base = nested,
            TypeParams = new[] { Row("T") },
            KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(frame), Methods = new[] { framedMethod } };
        var plain = new BirEnumDefinition { Name = "Plain", Kind = "enum", Base = sibling,
            TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { sibling } } } };
        var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { owner, framedOwner, plain } };
        var declarations = file.Types.Value;
        var constraints = owner.TypeParams.Value[0].Constraints.Value;
        var edges = owner.Interfaces.Value;

        RecordArrayTypeEdges(new[] { file });
        var facts = owner.KotlinSupertypesPre.Value;
        var methodFacts = method.MethodTypeParameterBoundsPre.Value;
        if (!ReferenceEquals(file.Types.Value, declarations) || !ReferenceEquals(owner.Methods.Value[0], method)
            || !ReferenceEquals(owner.TypeParams.Value[0].Constraints.Value, constraints)
            || !ReferenceEquals(owner.Interfaces.Value, edges)
            || facts.Base != earlier || facts.Interfaces.Count != 1 || facts.Bounds.Count != 1
            || facts.Bounds[0].Length != 2 || facts.Bounds[0][1] != sibling
            || ReferenceEquals(facts.Bounds[0][0], nested) || ReferenceEquals(facts.Interfaces[0], edges[1])
            || methodFacts.Bounds[0][0] != earlier || methodFacts.Bounds[1].Length != 2
            || ReferenceEquals(methodFacts.Bounds[1][0], nested)
            || implementation.KotlinSupertypesPre.Value.Bounds[0].Length != 2
            || inherited.MethodTypeParameterBoundsPre.IsPresent)
            throw new InvalidOperationException("Array source facts lost first-writer rows, complete constraints, ownership or descriptor policy");
        if (!ReferenceEquals(framedOwner.KotlinSupertypesPre.Value.NullableFrame, frame)
            || framedOwner.KotlinSupertypesPre.Value.Bounds.Count != 0
            || framedOwner.KotlinSupertypesPre.Value.Base == null || framedMethod.MethodTypeParameterBoundsPre.IsPresent
            || plain.KotlinSupertypesPre.IsPresent)
            throw new InvalidOperationException("Array source facts captured physical nullable companion bounds or unchanged declarations");

        var once = facts.FrozenText;
        var methodOnce = methodFacts.FrozenText;
        var implementationOnce = implementation.KotlinSupertypesPre.Value.FrozenText;
        RecordArrayTypeEdges(new[] { file });
        if (owner.KotlinSupertypesPre.Value.FrozenText != once
            || method.MethodTypeParameterBoundsPre.Value.FrozenText != methodOnce
            || implementation.KotlinSupertypesPre.Value.FrozenText != implementationOnce)
            throw new InvalidOperationException("Array source fact recording changed existing immutable snapshots");
        // Subsequent representation rewrites may mutate argument vectors, but
        // neither source carrier may alias that continuing physical graph.
        ((TypeNode.Fqn)nested).Args[0] = sibling;
        if (owner.KotlinSupertypesPre.Value.FrozenText != once
            || method.MethodTypeParameterBoundsPre.Value.FrozenText != methodOnce)
            throw new InvalidOperationException("Array source snapshots retained mutable physical type arguments");
        Console.WriteLine("[typed array source facts] self-test OK (edges, complete bounds, first writer, frames and occurrence ownership)");
    }
}
