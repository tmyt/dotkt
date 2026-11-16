using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinCollectionIdentitySynthesis
{
    internal static void SourceSelfTest()
    {
        var metadata = new ReferenceDotKtMetadata();
        metadata.DotKtOwners.UnionWith(new[] { Iterable, MutableIterable, Collection, MutableCollection, List, MutableList, Set, MutableSet });
        var refs = ReferenceMetadataIndex.DefaultDemandTestIndex(metadata);
        var mutableSet = new BirClassDefinition { Name = "MutableSetOwner", Kind = "class",
            Interfaces = new TypeNode[] { new TypeNode.Fqn(MutableSet) } };
        var split = new BirInterfaceDefinition { Name = "SeparateSetAndMutation", Kind = "interface",
            Interfaces = new TypeNode[] { new TypeNode.Fqn(Set), new TypeNode.Fqn(MutableCollection) } };
        var mixed = new BirClassDefinition { Name = "CollectionAndMutableIteration", Kind = "class",
            Interfaces = new TypeNode[] { new TypeNode.Fqn(Collection), new TypeNode.Fqn(MutableIterable) } };
        var inherited = new BirClassDefinition { Name = "Derived", Kind = "class", Base = new TypeNode.Fqn("MutableSetOwner") };
        var plain = new BirEnumDefinition { Name = "Plain", Kind = "enum", Interfaces = OptionalField<TypeNode[]>.Null };
        var sourceFacts = SourceTypeFacts.Empty.Merge(null, new TypeNode[] { new TypeNode.Fqn(Set) }, null);
        split.KotlinSupertypesPre = sourceFacts;
        var first = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { inherited, split, plain } };
        var second = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { mutableSet, mixed } };
        var members = new BirFunctionDeclaration { Name = "same", Params = Array.Empty<BirParameter>() };
        inherited.Methods = new[] { members };
        var records = first.Types.Value;
        ApplyAll(new[] { first, second }, refs);
        static string[] Names(IBirTypeDeclaration type) => type.Interfaces.OrEmpty().OfType<TypeNode.Fqn>().Select(face => face.Name).ToArray();
        if (!ReferenceEquals(first.Types.Value, records) || !ReferenceEquals(inherited.Methods.Value[0], members)
            || !Names(mutableSet).SequenceEqual(new[] { MutableSet, MutableSetIdentity })
            || !Names(inherited).SequenceEqual(new[] { MutableSetIdentity })
            || !Names(split).SequenceEqual(new[] { Set, MutableCollection, SetIdentity, MutableCollectionIdentity })
            || !Names(mixed).SequenceEqual(new[] { Collection, MutableIterable, CollectionIdentity, MutableIterableIdentity })
            || !plain.Interfaces.IsNull || !ReferenceEquals(split.KotlinSupertypesPre.Value, sourceFacts))
            throw new InvalidOperationException("Typed nominal collection markers changed ownership, original facts or independent Kotlin identities");
        var once = first.DeepClone();
        ApplyAll(new[] { first, second }, refs);
        if (!first.StructurallyEquals(once))
            throw new InvalidOperationException("Typed nominal collection markers were appended more than once");
        Console.WriteLine("[typed nominal collection identities] self-test OK (module BFS, independent set/mutability/iteration, edge order, source facts and idempotence)");
    }
}
