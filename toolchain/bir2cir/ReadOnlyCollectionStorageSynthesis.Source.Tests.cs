using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ReadOnlyCollectionStorageSynthesis
{
    internal static void SourceSelfTest()
    {
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Typed read-only storage: " + message); }
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var item = new TypeNode.Tv("type", 0);
        var other = new TypeNode.Tv("type", 1);
        var roots = new[] { new BirFileDeclaration { Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "ReadOnly.Current", Kind = "class",
                CapturedTypeParams = new[] { new BirTypeParameter { Name = "Captured" } },
                TypeParams = new[] { new BirTypeParameter { Name = "Own" } },
                Interfaces = new TypeNode[] { Face(CollectionViewFaces.IReadOnlyList, item),
                    Face(CollectionViewFaces.IReadOnlyCollection, other) } },
        } } };
        var definitions = SupertypeGraph.CollectSource(roots);
        var reached = SupertypeGraph.Reachable(definitions["ReadOnly.Current"], definitions, refs).Select(edge => edge.spec).ToArray();
        var faces = StorageFaces(reached, definitions, refs).ToArray();
        Check(faces.Length == 2 && faces[0] == Face(ListCarrierName, item) && faces[1] == Face(CarrierName, other),
            "list-before-collection selection or captured/own storage frame changed");
        Check(StorageFaces(new[] { Face(CollectionViewFaces.IReadOnlyList + "`1", item),
                Face(CollectionViewFaces.IReadOnlyCollection + "`1", item) }, definitions, refs).SequenceEqual(new[] { Face(ListCarrierName, item) }),
            "constructed arity spelling or list-provided collection suppression changed");
        Check(!StorageFaces(new[] { Face(CollectionViewFaces.IReadOnlyList, item), Face(CollectionViewFaces.IList, item),
                Face(CollectionViewFaces.IReadOnlyCollection, item), Face(CollectionViewFaces.ICollection, item) }, definitions, refs).Any()
            && !StorageFaces(new[] { new TypeNode.Fqn(CollectionViewFaces.IReadOnlyList) }, definitions, refs).Any(),
            "native mutable face suppression or exact unary generic eligibility changed");
        var mapOnly = new BirClassDefinition { Name = "ReadOnly.MapOnly", Kind = "class",
            Interfaces = new TypeNode[] { new TypeNode.Fqn("System.Collections.Generic.IDictionary", new TypeNode[] {
                new TypeNode.Fqn("System.String"), new TypeNode.Fqn("System.Int32") }) } };
        var mapFile = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { mapOnly } };
        var vector = mapFile.Types.Value;
        Check(ApplyAll(new[] { mapFile }, refs, type => type,
                (_, _) => throw new InvalidOperationException("Unselected read-only carrier was published")).Count == 0
            && ReferenceEquals(mapFile.Types.Value, vector), "dictionary storage acquired a fabricated read-only Count carrier");
        var mutation = SourceMethod("set_Item", Void,
            new[] { SourceParameter("index", Int), SourceParameter("value", Element) }, UnsupportedSource(), CollectionViewFaces.IList);
        Check(mutation.ClrInterfaceImpls.Value[0].Owner.Value == Face(CollectionViewFaces.IList, Element)
            && mutation.Body.Value is [BirThrow] && mutation.Params.Value[1].Type.Value == Element,
            "read-only mutation stub lost exact list MethodImpl/element storage contract");
        var source = (BirCast)ReadOnlySelfSource(CollectionViewFaces.IReadOnlyList);
        Check(source.Type.Value == Face(CollectionViewFaces.IReadOnlyList, Element) && source.E.Value is BirThis,
            "read operations no longer dispatch on the same original object");
        Console.WriteLine("[typed read-only storage] self-test OK (physical frame, exact faces, native suppression, map exclusion and immutable mutation surface)");
    }
}
