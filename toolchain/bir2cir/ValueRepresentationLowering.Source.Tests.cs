using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class ValueRepresentationLowering
{
    static void SourceIndexSelfTest()
    {
        var sourceParameter = new BirTypeParameter { Name = "T", Variance = "out" };
        var marker = new BirAttribute { Attr = new TypeNode.Fqn(MemberAttribute), Args = new IBirAttributeValue[] {
            new BirConstant { Value = new BirLiteral.TextLiteral("Helpers.original") },
            new BirAttributeLiteral { Value = new BirLiteral.TextLiteral("Slots.original") },
        } };
        var method = new BirFunctionDeclaration {
            Name = "original", DeclarationId = "source-value-method", Generated = true,
            Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("kotlin.String"), Attrs = new[] { marker },
        };
        var owner = new BirClassDefinition { Name = "SourceShape", Kind = "class",
            TypeParams = new[] { sourceParameter }, CapturedTypeParams = new[] { new BirTypeParameter { Name = "Outer" } },
            Methods = new[] { method } };
        var root = new BirFileDeclaration { FileClass = "SourceValues", Types = new IBirTypeDeclaration[] {
            owner,
            new BirClassDefinition { Name = "AbsentShape", Kind = "class" },
            new BirClassDefinition { Name = "NullShape", Kind = "class", TypeParams = OptionalField<BirTypeParameter[]>.Null },
            new BirClassDefinition { Name = "EmptyShape", Kind = "class", TypeParams = Array.Empty<BirTypeParameter>() },
        } };
        var membership = new SourceModuleIndex(new[] { root }, new Dictionary<string, string>()).DeclarationIds;
        var policy = new ValueRepresentationSourcePolicy(new[] { root }, null, false);
        var index = CaptureSource(new[] { root }, membership);
        var shapes = index.Parameters.ToDictionary(shape => shape.Owner, shape => shape.Variances, StringComparer.Ordinal);
        if (!shapes["SourceShape"].Value.SequenceEqual(new[] { "out" })
            || shapes["AbsentShape"].IsPresent || !shapes["NullShape"].IsNull
            || !shapes["EmptyShape"].HasValue || shapes["EmptyShape"].Value.Length != 0)
            throw new InvalidOperationException("Value source index lost declared parameter presence or concatenated captured parameters");

        // Model the continuing physical declaration rather than recapturing it:
        // generic expansion and attribute consumption do not change source facts.
        sourceParameter.Variance = "in";
        owner.TypeParams = new[] { sourceParameter, new BirTypeParameter { Name = "$storage0" } };
        method.Attrs = Array.Empty<BirAttribute>();
        var continuation = root.ToDom();
        var physicalOwner = continuation["types"][0].AsObject();
        physicalOwner["name"] = "PhysicalShape";
        var physicalMethod = physicalOwner["methods"][0].AsObject();
        physicalMethod["name"] = "physicalEntry";
        var indexed = new ValueRepresentationLowering(new JsonNode[] { continuation }, null, false, policy, index);
        physicalMethod["ret"] = TypeJson.Fqn("kotlin.Int");
        if (!indexed.localParameters["SourceShape"].SequenceEqual(new[] { "out" })
            || indexed.localParameters.ContainsKey("PhysicalShape") || indexed.localParameters.ContainsKey("AbsentShape")
            || indexed.localParameters.ContainsKey("NullShape") || indexed.localParameters["EmptyShape"].Count != 0
            || indexed.locals["source-value-method"].Owner != "SourceShape"
            || !ReferenceEquals(indexed.locals["source-value-method"].Method, physicalMethod)
            || indexed.adapters["source-value-method"] != "Helpers.original"
            || indexed.members["source-value-method"].Slot != "Slots.original"
            || !ReferenceEquals(indexed.members["source-value-method"].Method, physicalMethod)
            || TypeJson.Read(indexed.locals["source-value-method"].Method["ret"]) != new TypeNode.Fqn("kotlin.Int"))
            throw new InvalidOperationException("Value representation recaptured expanded source facts or detached its continuing physical method");
        Console.WriteLine("[value source index] self-test OK (parameter presence, initial shape, captured metadata, exact live identity)");
    }
}
