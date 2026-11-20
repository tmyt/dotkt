using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class PublishedApiTypeVisibilityLowering
{
    internal static void SelfTestSourceVisibility()
    {
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Owning PublishedApi visibility: " + message);
        }
        BirAttribute Published() => new() { Attr = new TypeNode.Fqn(Marker) };
        var marker = Published();
        var preceding = new BirAttribute { Attr = new TypeNode.Fqn("Example.UserAttribute") };
        var promoted = new BirClassDefinition { Kind = "class", Name = "Owner.Published", SemanticOwner = "Owner",
            Vis = "internal", Attrs = new[] { preceding, marker } };
        var contract = new BirInterfaceDefinition { Kind = "interface", Name = "PublishedContract", Vis = "internal", Attrs = new[] { Published() } };
        var enumeration = new BirEnumDefinition { Kind = "enum", Name = "PublishedEnum", Vis = "internal", Attrs = new[] { Published() } };
        var internalType = new BirClassDefinition { Kind = "class", Name = "Owner", Vis = "internal", Attrs = OptionalField<BirAttribute[]>.Null };
        var privateType = new BirClassDefinition { Kind = "class", Name = "Private", Vis = "private", Attrs = new[] { Published() } };
        var publicType = new BirClassDefinition { Kind = "class", Name = "Public", Vis = "public", Attrs = new[] { Published() } };
        var absent = new BirClassDefinition { Kind = "class", Name = "Absent", Attrs = new[] { Published() } };
        var nullVisibility = new BirClassDefinition { Kind = "class", Name = "Null", Vis = OptionalField<string>.Null,
            Attrs = new[] { Published() } };
        var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] {
            promoted, contract, enumeration, internalType, privateType, publicType, absent, nullVisibility,
        } };
        var types = file.Types.Value;
        var promotedTypes = new IBirTypeDeclaration[] { promoted, contract, enumeration };
        var markers = promotedTypes.Select(type => type.Attrs.Value[^1]).ToArray();
        ApplyAll(new[] { file });
        Require(ReferenceEquals(types, file.Types.Value) && ReferenceEquals(file.Types.Value[0], promoted)
            && promoted.SemanticOwner.Value == "Owner" && !promoted.NestedIn.IsPresent,
            "visibility processing changed owning occurrences or selected nesting early");
        for (var index = 0; index < promotedTypes.Length; index++)
        {
            var type = promotedTypes[index];
            Require(type.Vis.Value == "public" && ReferenceEquals(type.Attrs.Value[^2], markers[index]),
                "explicit marker was not retained in order on the promoted declaration");
            var carrier = type.Attrs.Value[^1];
            Require(carrier.Attr.Value is TypeNode.Fqn { Name: "DotKt.Runtime.CompilerServices.KotlinTypeVisibilityAttribute" }
                && carrier.ArgTypes.Value is [TypeNode.Fqn { Name: "System.String" }, TypeNode.Array { Elem: TypeNode.Fqn { Name: "System.Byte" } }]
                && carrier.Args.Value is [BirAttributeLiteral { Value.Value: BirLiteral.TextLiteral { Value: BirCarrier.MessagePackV1 } }, BirOpaqueBytes bytes]
                && bytes.Bytes.Value.SequenceEqual(RoundtripMetadata.VisibilityContent("internal")),
                "source visibility or its current carrier ABI was not preserved before promotion");
        }
        Require(promoted.Attrs.Value.Length == 3 && ReferenceEquals(promoted.Attrs.Value[0], preceding)
            && internalType.Vis.Value == "internal" && internalType.Attrs.IsNull
            && privateType.Vis.Value == "private" && privateType.Attrs.Value.Length == 1
            && publicType.Vis.Value == "public" && publicType.Attrs.Value.Length == 1
            && !absent.Vis.IsPresent && nullVisibility.Vis.IsNull,
            "unmarked/other visibility, field presence or preceding attributes changed");
        var attributes = promoted.Attrs.Value;
        ApplyAll(new[] { file });
        Require(ReferenceEquals(attributes, promoted.Attrs.Value), "unchanged effective visibility appended another carrier");
        Console.WriteLine("[PublishedApi type visibility] self-test OK (source visibility carrier, declaration identity, presence)");
    }
}
