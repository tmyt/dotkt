using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ExistentialSharedCellAlignment
{
    internal static void SelfTestSource()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning shared-cell alignment: " + message); }
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var variable = new TypeNode.Tv("type", 0);
        var logical = new TypeNode.Nullable(new TypeNode.Fqn("CellValue", new TypeNode[] { variable }));
        var physical = new TypeNode.Nullable(new TypeNode.Fqn("CellValue$star"));
        BirClassDefinition Cell(string name, bool native = false) => new() { Name = name, Kind = "class", Generated = true,
            TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Fields = new[] { new BirFieldDeclaration { Name = "v", Type = logical,
                _nativeLocation = native ? OptionalField<bool>.Of(true) : default } },
            Ctors = new[] { new BirConstructor { Params = new[] { new BirParameter { Name = "value", Type = logical } },
                Body = Array.Empty<IBirStatement>() } },
        };
        var first = Cell("Cell");
        var second = Cell("Cell");
        var native = Cell("NativeCell", true);
        var construction = new BirNew { Type = new TypeNode.Fqn("Cell", new TypeNode[] { new TypeNode.Fqn("kotlin.String") }),
            ArgTypes = new[] { logical }, MemberSignature = new[] { logical },
            Args = new IBirExpression[] { new BirLocal { Name = "value", Sty = new TypeNode.Fqn("CellValue$star") } } };
        var read = new BirFieldGet { OwnerType = construction.Type, Name = "v", Recv = new BirLocal { Name = "cell" },
            Sty = logical, MemberType = OptionalField<TypeNode>.Null };
        var nativeConstruction = new BirNew { Type = new TypeNode.Fqn("NativeCell", new TypeNode[] { variable }),
            ArgTypes = new[] { logical }, Args = new IBirExpression[] { new BirLocal { Name = "value", Sty = new TypeNode.Fqn("CellValue$star") } } };
        var body = new IBirStatement[] {
            new BirVariable { Name = "cell", Type = construction.Type, Init = construction },
            new BirExpressionStatement { Expr = read },
            new BirVariable { Name = "native", Type = nativeConstruction.Type, Init = nativeConstruction },
        };
        var root = new BirFileDeclaration { Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "CellValue", Kind = "class", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
                Interfaces = new TypeNode[] { new TypeNode.Fqn("CellValue$star") } },
            new BirInterfaceDefinition { Name = "CellValue$star", Kind = "interface", Generated = true }, first, second, native },
            Methods = new[] { new BirFunctionDeclaration { Name = "use", TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Body = body } } };
        var types = root.Types.Value;
        var firstField = first.Fields.Value.Single();
        var firstParameter = first.Ctors.Value.Single().Params.Value.Single();
        ApplyAll(new[] { root }, new[] { new SharedSyntheticSynthesis.CellStorage("Cell", "v", 0),
            new SharedSyntheticSynthesis.CellStorage("NativeCell", "v", 0) },
            new Dictionary<string, string> { ["CellValue"] = "CellValue$star" }, refs);
        Require(ReferenceEquals(root.Types.Value, types) && ReferenceEquals(first.Fields.Value.Single(), firstField)
            && ReferenceEquals(first.Ctors.Value.Single().Params.Value.Single(), firstParameter)
            && firstField.Type.Value == physical && second.Fields.Value.Single().Type.Value == physical
            && firstParameter.Type.Value == physical && second.Ctors.Value.Single().Params.Value.Single().Type.Value == physical,
            "cell declaration occurrences or duplicate-owner alignment changed");
        Require(read.Sty.Value == physical && read.MemberType.IsNull
            && construction.ArgTypes.Value.Single() == physical && construction.MemberSignature.Value.Single() == physical,
            "cell uses lost their physical nullable storage or absent/null selected-member fact");
        Require(native.Fields.Value.Single().Type.Value == logical && nativeConstruction.ArgTypes.Value.Single() == logical,
            "a native cell location was projected as ordinary value storage");
        Console.WriteLine("[owning shared cell alignment] self-test OK (actual fields/ctors, duplicate owners, nullable storage, native exclusion, presence)");
    }
}
