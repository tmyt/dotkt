using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinOverrideSlotBridge
{
    static void SourceDeclarationSelfTest()
    {
        var references = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var variable = new TypeNode.Tv("type", 0);
        var integer = new TypeNode.Fqn("kotlin.Int");
        var unit = new TypeNode.Fqn("kotlin.Unit");
        TypeNode Box(TypeNode type) => new TypeNode.Fqn("Box", new[] { type });
        BirOverride Edge(string member, string kind, string identity) => new() {
            Owner = new TypeNode.Fqn("Base", new[] { integer }), Member = member, Kind = kind, DeclarationId = identity,
        };
        var sourceParameter = new TypeNode.Nullable(integer);
        var parameter = new BirParameter { Name = "value", Type = Box(sourceParameter), NullableFlags = OptionalField<int[]>.Null };
        var implementation = new BirFunctionDeclaration { Name = "move", DeclarationId = "body-move", Params = new[] { parameter },
            Ret = Box(sourceParameter), RetNullableFlags = OptionalField<int[]>.Null,
            Body = Array.Empty<IBirStatement>(), Overrides = new[] { Edge("move", "method", "slot-move") } };
        var suspend = new BirFunctionDeclaration { Name = "read", DeclarationId = "body-read", Params = Array.Empty<BirParameter>(),
            Ret = sourceParameter, Mods = new BirModifiers { Suspend = true }, Body = Array.Empty<IBirStatement>(),
            Overrides = new[] { Edge("read", "method", "slot-read") } };
        var getter = new BirFunctionDeclaration { Name = "bodyGetter", DeclarationId = "body-getter", PropertyName = "value", PropertyAccessor = "get",
            Params = Array.Empty<BirParameter>(), Ret = integer, Body = Array.Empty<IBirStatement>(),
            Overrides = new[] { Edge("value", "getter", "slot-getter") } };
        var ancestor = new BirInterfaceDefinition { Kind = "interface", Name = "Base", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Methods = new[] {
                new BirFunctionDeclaration { Name = "move", DeclarationId = "slot-move", Abstract = true,
                    Params = new[] { new BirParameter { Name = "value", Type = Box(new TypeNode.Nullable(variable)) } }, Ret = Box(new TypeNode.Nullable(variable)) },
                new BirFunctionDeclaration { Name = "read", DeclarationId = "slot-read", Abstract = true, Mods = new BirModifiers { Suspend = true },
                    Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Nullable(variable) },
                new BirFunctionDeclaration { Name = "sourceGetter", DeclarationId = "slot-getter", Abstract = true, ExplicitClrName = "exactGetter",
                    PropertyName = "value", PropertyAccessor = "get", Params = Array.Empty<BirParameter>(), Ret = integer },
            } };
        var owner = new BirClassDefinition { Kind = "class", Name = "Derived", Interfaces = new TypeNode[] { new TypeNode.Fqn("Base", new[] { integer }) },
            Methods = new[] { implementation, suspend, getter } };
        var root = new BirFileDeclaration { FileClass = "OverrideSource", Types = new IBirTypeDeclaration[] { ancestor, owner } };
        var originalBody = implementation.Body.Value;
        var policy = GenericRepresentationPolicy.CreateSource(new[] { root }, new Dictionary<string, string>(), references);
        PropagateErasedSlots(new[] { root }, type => type.Name == "kotlin.Int", references, true, policy, _ => false);
        var objectType = new TypeNode.Fqn("object");
        if (parameter.Type.Value != Box(objectType) || implementation.Ret.Value != Box(objectType)
            || parameter.NullableGeneric.Value != Box(sourceParameter) || implementation.NullableGenericRet.Value != Box(sourceParameter)
            || !parameter.NullableFlags.IsNull || !implementation.RetNullableFlags.IsNull
            || !ReferenceEquals(implementation.Body.Value, originalBody) || implementation.DeclarationId.Value != "body-move"
            || !ReferenceEquals(owner.Methods.Value[0], implementation)
            || suspend.SuspendTaskResult.Value != objectType || suspend.Ret.Value != sourceParameter
            || !getter.Virtual.OrDefault() || getter.ClrInterfaceImpls.Value.Single().Member.Value != "exactGetter")
            throw new InvalidOperationException("Typed override propagation lost current declaration identity, nullable facts, suspend result, or exact property slot");

        // Exercise the same declaration-edit function for an ordinary-function
        // source signature, including null/present source metadata semantics.
        var function = new TypeNode.Fn(false, unit, Array.Empty<TypeNode>());
        var callback = new BirParameter { Name = "callback", Type = Box(function) };
        new OverrideParameter(callback).Move(Box(objectType), _ => false, null);
        if (callback.FunctionSignatureType.Value != Box(function) || callback.NullableGeneric.Value != Box(function))
            throw new InvalidOperationException("Typed override move lost its original ordinary-function discriminator");
        Console.WriteLine("[source override declarations] self-test OK (nested moves, preserved presence/identity, suspend results, exact property slots)");
    }
}
