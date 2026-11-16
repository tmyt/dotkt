using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class TypedAttributeBinding
{
    internal static void SelfTest()
    {
        var attributes = new List<BirAttribute>();
        BirAttribute Bound()
        {
            var attribute = new BirAttribute {
                Attr = new TypeNode.Fqn("System.ObsoleteAttribute"), AttrClr = true, AttrExternal = false,
                ArgTypes = Array.Empty<TypeNode>(), Args = Array.Empty<IBirAttributeValue>(),
                MemberRef = BirMemberReference.FromPhysical(new MemberRefNode(MemberRefNode.Kinds.Ctor,
                    typeof(ObsoleteAttribute).Assembly.GetName().Name, new TypeNode.Fqn("System.ObsoleteAttribute"),
                    ".ctor", 0, new TypeNode.Fqn("System.Void"), MemberRefNode.Instance, Array.Empty<TypeNode>())),
            };
            attributes.Add(attribute);
            return attribute;
        }
        var falseOrigin = new BirAttribute { Attr = new TypeNode.Fqn("probe.Marker"), AttrClr = false };
        var nullOrigin = new BirAttribute { Attr = new TypeNode.Fqn("probe.Marker"), AttrClr = OptionalField<bool>.Null };
        var nullReference = new BirAttribute { Attr = new TypeNode.Fqn("System.ObsoleteAttribute"), AttrExternal = true,
            ArgTypes = Array.Empty<TypeNode>(), MemberRef = OptionalField<BirMemberReference>.Null };
        var unresolved = new BirAttribute { Attr = new TypeNode.Fqn("probe.UnresolvedAttribute"), AttrClr = true,
            ArgTypes = Array.Empty<TypeNode>(), Args = Array.Empty<IBirAttributeValue>() };
        var root = new BirFileDeclaration {
            FileClass = "probe.File", AssemblyAttrs = new[] { Bound() },
            Methods = new[] { new BirFunctionDeclaration {
                Name = "method", Ret = new TypeNode.Fqn("System.Void"), Attrs = new[] { Bound() },
                RetAttrs = new[] { Bound() }, Params = new[] { new BirParameter {
                    Name = "parameter", Type = new TypeNode.Fqn("System.Int32"), Attrs = new[] { Bound() },
                } },
            } },
            Fields = new[] { new BirFieldDeclaration { Name = "field", Type = new TypeNode.Fqn("System.Int32"),
                Attrs = new[] { Bound() } } },
            Types = new IBirTypeDeclaration[] { new BirClassDefinition {
                Name = "probe.Type", Attrs = new[] { Bound(), falseOrigin, nullOrigin, nullReference, unresolved },
            } },
        };
        var selected = attributes.Select(attribute => attribute.MemberRef.Value).ToArray();
        Apply(root, ReferenceMetadataIndex.Build(Array.Empty<string>()), "attributes");
        for (var index = 0; index < attributes.Count; index++)
            if (attributes[index].AttrClr.IsPresent || !attributes[index].AttrExternal.OrDefault()
                || !ReferenceEquals(attributes[index].MemberRef.Value, selected[index]))
                throw new InvalidOperationException("Typed attribute binding missed a carrier or replaced an exact selected constructor");
        if (!falseOrigin.AttrClr.HasValue || falseOrigin.AttrClr.Value || falseOrigin.AttrExternal.IsPresent
            || !nullOrigin.AttrClr.IsNull || nullOrigin.AttrExternal.IsPresent || !nullReference.MemberRef.IsNull
            || unresolved.AttrClr.IsPresent || !unresolved.AttrExternal.OrDefault() || unresolved.MemberRef.IsPresent)
            throw new InvalidOperationException("Typed attribute binding changed false/null origin, explicit-null identity, or unresolved marker policy");

        var savedAttribute = Bound();
        AttrExternalNormalize.Apply(new BirInlinePayload { Params = new[] { new BirParameter {
            Name = "saved", Type = new TypeNode.Fqn("System.Int32"), Attrs = new[] { savedAttribute },
        } } });
        if (!savedAttribute.AttrClr.OrDefault() || savedAttribute.AttrExternal.OrDefault())
            throw new InvalidOperationException("Physical attribute binding entered a saved executable payload");

        TypeNode integer = new TypeNode.Fqn("System.Int32");
        var nullable = new TypeNode.Fqn("System.Nullable`1", new[] { integer });
        ForeignNullableGenericCrossing.CheckParameters(new[] { nullable }, "attributes", "probe.Owner", "method");
        var rejected = false;
        var crossingAttribute = Bound();
        crossingAttribute.MemberRef.Value.ParameterTypes = new TypeNode[] { new TypeNode.Array(nullable) };
        try
        {
            Apply(new BirFileDeclaration { AssemblyAttrs = new[] { crossingAttribute } },
                ReferenceMetadataIndex.Build(Array.Empty<string>()), "attributes");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("parameter 0", StringComparison.Ordinal))
        {
            rejected = true;
        }
        if (!rejected) throw new InvalidOperationException("Applied attribute binding lost foreign nullable-parameter validation");
        Console.WriteLine("[typed applied attributes] self-test OK (all carriers, exact identity/presence, opaque payloads, shared crossing policy)");
    }
}
