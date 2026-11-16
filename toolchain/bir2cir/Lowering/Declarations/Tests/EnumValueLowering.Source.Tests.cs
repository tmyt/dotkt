using System.Buffers;
using System.Text.Json;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class EnumValueLowering
{
    internal static void SelfTest()
    {
        var declaration = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { new BirClassDefinition {
            Kind = "class", Name = "MappedRich", EnumRich = true, RichEnum = new BirRichEnumDeclaration {
                Entries = Array.Empty<BirRichEnumEntry>(), Name = "display", Ordinal = "rank", Values = "all", ValueOf = "parse" } } } };
        var fields = CollectRichInstanceFields(new[] { new BirFileDeclaration(), declaration });
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var none = new Dictionary<string, BasicEnumMetadata>();
        foreach (var propertyCall in new[] { false, true })
            foreach (var (property, field, result) in new[] { ("name", "display", "kotlin.String"), ("ordinal", "rank", "kotlin.Int") })
            {
                var owner = new TypeNode.Fqn("MappedRich");
                var receiver = new BirLocal { Name = "value" };
                IBirExpression expression = propertyCall
                    ? new BirCallInstance { OwnerType = owner, Method = property, Prop = "get", Recv = receiver, Args = Array.Empty<IBirExpression>() }
                    : property == "name" ? new BirEnumName { Type = owner, E = receiver } : new BirEnumOrdinal { Type = owner, E = receiver };
                if (!propertyCall && BirNodeType.Of((BirGraphNode)expression) != new TypeNode.Fqn(result))
                    throw new InvalidOperationException("Semantic enum access lost its node-local result before lowering");
                var slot = new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(expression) };
                var method = new BirFunctionDeclaration { Name = "use", Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] { slot } };
                var file = new BirFileDeclaration { Methods = new[] { method } };
                Apply(file, refs, fields, new HashSet<string>(), none, none);
                if (slot.Expr.Value is not BirFieldGet read || read.Name.Value != field || read.OwnerType.Value != owner
                    || read.Recv.Value is not BirLocal { Name.Value: "value" } || ReferenceEquals(read.Recv.Value, receiver)
                    || read.Ret.Value != new TypeNode.Fqn(result) || !ReferenceEquals(file.Methods.Value[0], method))
                    throw new InvalidOperationException("Typed rich enum access lost its storage map, copied receiver or continuing method");
            }
        var selected = new BirCallInstance { OwnerType = new TypeNode.Fqn("MappedRich.Entry"), Method = "name", Prop = "get", DeclarationId = "selected-name",
            Args = Array.Empty<IBirExpression>(), Recv = new BirLocal { Name = "entry" }, Overrides = new[] { new BirOverride {
                Owner = new TypeNode.Fqn("kotlin.Enum", new TypeNode[] { new TypeNode.Fqn("MappedRich") }), Member = "name", Kind = "getter", DeclarationId = "selected-name" } } };
        var selectedField = new BirFieldDeclaration { Name = "name", Init = selected };
        Apply(new BirFileDeclaration { Fields = new[] { selectedField } }, refs, fields, new HashSet<string>(), none, none);
        if (selectedField.Init.Value is not BirFieldGet { OwnerType.Value: TypeNode.Fqn { Name: "MappedRich" }, Name.Value: "display" })
            throw new InvalidOperationException("Typed enum property used an entry-specific dispatch owner instead of its selected declaration");
        ExplicitValuesSelfTest(refs, fields);
        Console.WriteLine("[enum instance fields] self-test OK (actual slots, selected inherited property, exact ordered constants/parse and both codecs)");
    }

    static void ExplicitValuesSelfTest(ReferenceMetadataIndex refs, IReadOnlyDictionary<string, (string Name, string Ordinal)> rich)
    {
        var type = new TypeNode.Fqn("Sparse");
        var metadata = new BasicEnumMetadata("System.UInt64", new[] { new BasicEnumEntry("FIRST", 0, "18446744073709551615"),
            new BasicEnumEntry("ALIAS", 1, "18446744073709551615"), new BasicEnumEntry("ZERO", 2, "0") });
        var maps = new Dictionary<string, BasicEnumMetadata> { ["Sparse"] = metadata };
        var entry = new BirEnumValue { Type = type, Entry = "FIRST", Ordinal = 0 };
        var ordinal = new BirEnumOrdinal { Type = type, E = new BirLocal { Name = "entry" } };
        var values = new BirEnumValues { Type = type };
        var input = new BirCallStatic { Owner = new TypeNode.Fqn("Probe"), Method = "sourceName", Args = Array.Empty<IBirExpression>(), Ret = new TypeNode.Fqn("kotlin.String") };
        var parse = new BirEnumParse { Type = type, Arg = input };
        var genericParse = new BirEnumParse { Type = new TypeNode.Tv("method", 0), Arg = new BirLocal { Name = "name" } };
        var statements = new IBirStatement[] { new BirExpressionStatement { Expr = entry }, new BirExpressionStatement { Expr = ordinal },
            new BirExpressionStatement { Expr = values }, new BirExpressionStatement { Expr = parse }, new BirExpressionStatement { Expr = genericParse } };
        var method = new BirFunctionDeclaration { Name = "use", DeclarationId = "enum:use", Params = new[] {
            new BirParameter { Name = "entry", Type = type }, new BirParameter { Name = "name", Type = new TypeNode.Fqn("kotlin.String") } },
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Body = statements };
        var file = new BirFileDeclaration { FileClass = "EnumProbe", Methods = new[] { method }, Types = Array.Empty<IBirTypeDeclaration>() };
        if (!Apply(file, refs, rich, new HashSet<string>(), maps, maps)
            || !ReferenceEquals(((BirExpressionStatement)statements[0]).Expr.Value, entry)
            || entry.Underlying.Value != "System.UInt64" || entry.PhysicalValue.Value != "18446744073709551615"
            || !ordinal.Values.Value.Select(value => value.PhysicalValue.Value).SequenceEqual(new[] { "18446744073709551615", "18446744073709551615", "0" })
            || !values.Values.Value.Select(value => value.Ordinal.Value).SequenceEqual(new[] { 0, 1, 2 })
            || ReferenceEquals(ordinal.Values.Value[0], values.Values.Value[0]))
            throw new InvalidOperationException("Typed enum value tables lost unsigned bits, aliases, declaration order or independent ownership");
        var block = ((BirExpressionStatement)statements[3]).Expr.Value as BirValueBlock;
        if (block?.Stmts.Value is not [BirVariable { Init.Value: BirCallStatic { Method.Value: "sourceName" } } temporary]
            || ReferenceEquals(temporary.Init.Value, input))
            throw new InvalidOperationException("Typed enum parse did not snapshot the original name once");
        var current = block.Result.Value;
        foreach (var expected in metadata.Entries)
        {
            if (current is not BirConditional branch || branch.Cond.Value is not BirObjectEquals {
                    Lhs.Value: BirLocal local, Rhs.Value: BirConstant { Value.Value: BirLiteral.TextLiteral literal } }
                || local.Name.Value != temporary.Name.Value || literal.Value != expected.Name
                || branch.Then.Value is not BirEnumValue physical || physical.PhysicalValue.Value != expected.PhysicalValue)
                throw new InvalidOperationException("Typed enum parse changed the exact name comparison/declaration order");
            current = branch.Else.Value;
        }
        if (current is not BirThrowExpression { Value.Value: BirNew { Type.Value: TypeNode.Fqn { Name: "kotlin.IllegalArgumentException" } } }
            || ((BirExpressionStatement)statements[4]).Expr.Value is not BirCast { E.Value: BirCallStatic {
                Method.Value: "kotlinEnumValueOf", Args.Value: [BirClassReference { Type.Value: TypeNode.Tv { Scope: "method", I: 0 } }, BirLocal { Name.Value: "name" }] } })
            throw new InvalidOperationException("Typed enum parse lost its Kotlin failure or runtime generic frame");
        var evaluations = 0;
        file.Visit(node => { if (node is BirCallStatic { Method.Value: "sourceName" }) evaluations++; });
        if (evaluations != 1) throw new InvalidOperationException("Typed enum parse evaluates its name more than once");
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) { file.WriteJson(writer); writer.Flush(); }
        if (!file.StructurallyEquals(BirGraphJson.ReadFile(buffer.WrittenSpan.ToArray()))
            || !file.StructurallyEquals(BirFileCodec.Decode(BirFileCodec.Encode(file))))
            throw new InvalidOperationException("Typed enum operations lost current JSON or MessagePack facts");
    }
}
