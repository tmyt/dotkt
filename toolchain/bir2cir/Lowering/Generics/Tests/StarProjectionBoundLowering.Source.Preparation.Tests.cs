using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class StarProjectionBoundLowering
{
    internal static void SourcePreparationSelfTest()
    {
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Star-bound snapshot: " + message); }
        var integer = new TypeNode.Fqn("System.Int32"); var text = new TypeNode.Fqn("System.String");
        var captured = new TypeNode.Fqn(typeof(IEnumerable<>).FullName, new TypeNode[] { integer });
        var own = new TypeNode.Fqn("System.IDisposable");
        var owner = new BirClassDefinition { Name = "Snapshot.Carrier", Kind = "class",
            CapturedTypeParams = new[] { new BirTypeParameter { Name = "Captured", Constraints = new TypeNode[] {
                new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")), captured, new TypeNode.Fqn("System.ICloneable") } } },
            TypeParams = new[] { new BirTypeParameter { Name = "Own", Constraints = new TypeNode[] { new TypeNode.Fqn("System.Object"), own } },
                new BirTypeParameter { Name = "Unbounded", Constraints = new TypeNode[] { new TypeNode.Fqn("object") } } } };
        var later = new BirClassDefinition { Name = "Snapshot.Carrier", Kind = "class",
            TypeParams = new[] { new BirTypeParameter { Name = "Other", Constraints = new TypeNode[] { text } } } };
        var plain = new BirInterfaceDefinition { Name = "Snapshot.Unbounded", Kind = "interface",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } } };
        var roots = new[] { new BirFileDeclaration { Types = new IBirTypeDeclaration[] { owner, plain } },
            new BirFileDeclaration { Types = new IBirTypeDeclaration[] { later } } };
        var bounds = CollectTypeParamBounds(roots);
        Check(bounds.Count == 1 && bounds["Snapshot.Carrier"].Length == 3
            && TypeNodeDataEquality.Equal(bounds["Snapshot.Carrier"][0], captured)
            && TypeNodeDataEquality.Equal(bounds["Snapshot.Carrier"][1], own)
            && bounds["Snapshot.Carrier"][2] == null, "captured/own order, first concrete bound or first owner changed");
        var copy = (TypeNode.Fqn)bounds["Snapshot.Carrier"][0];
        Check(!ReferenceEquals(copy.Args, captured.Args), "nested bound vectors alias the declaration");
        captured.Args[0] = text;
        Check(copy.Args[0] is TypeNode.Fqn { Name: "System.Int32" }, "source mutation changed prepared bounds");
        copy.Args[0] = new TypeNode.Fqn("System.Double");
        Check(captured.Args[0] is TypeNode.Fqn { Name: "System.String" }, "prepared mutation changed source bounds");
        captured.Args[0] = integer; copy.Args[0] = new TypeNode.Fqn("System.Int32");
        var application = new TypeNode.Fqn("Snapshot.Carrier", Enumerable.Repeat<TypeNode>(new TypeNode.Fqn("kotlin.Any"), 3).ToArray());
        var field = new BirFieldDeclaration { Name = "use", Type = application };
        var use = new BirFileDeclaration { Fields = new[] { field } };
        Apply(use, bounds, ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location }));
        var result = (TypeNode.Fqn)field.Type.Value;
        Check(TypeNodeDataEquality.Equal(result.Args[0], captured) && TypeNodeDataEquality.Equal(result.Args[1], own)
            && result.Args[2] is TypeNode.Fqn { Name: "kotlin.Any" }, "retained consumer ignored the prepared physical frame");
        Console.WriteLine("[star-bound preparation] self-test OK (captured/own order, first bound/owner, deep isolation and retained consumer)");
    }
}
