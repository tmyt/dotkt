using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinPropertyAccessors
{
    internal static void SelfTestAccessorSignatures()
    {
        var logical = new TypeNode.Tv("type", 0);
        var physical = new TypeNode.Fqn("object");
        var property = new BirPropertyDeclaration { Name = "value", Type = logical,
            PropertyAssociation = "p", KotlinAccessors = new[] { "get", "set" } };
        var getter = new BirFunctionDeclaration { Name = "value", PropertyName = "value", PropertyAssociation = "p", PropertyAccessor = "get",
            DeclarationId = "read-id", Params = Array.Empty<BirParameter>(), Ret = physical };
        var setter = new BirFunctionDeclaration { Name = "narrow-write", PropertyName = "value", PropertyAssociation = "p", PropertyAccessor = "set",
            Params = new[] { new BirParameter { Name = "value", Type = physical } }, Ret = new TypeNode.Fqn("void") };
        var root = new BirFileDeclaration { FileClass = "PropertyTests", Methods = new[] { getter, setter }, Properties = new[] { property } };
        AllocateDeclarationsAndProperties(new[] { root });
        if (!TypeNodeDataEquality.Equal(property.GetRet.Value, getter.Ret.Value)
            || !TypeNodeDataEquality.Equal(property.SetRet.Value, setter.Ret.Value)
            || TypeNodeDataEquality.Equal(property.GetRet.Value, property.Type.Value)
            || property.Get.Value != PhysicalName("value", "get") || property.Set.Value != "narrow-write"
            || getter.DeclarationId.Value != "read-id" || getter.PropertyAssociation.Value != "p")
            throw new InvalidOperationException("Property accessor links lost their selected MethodDef identities or return types");
        var genericProperty = new BirPropertyDeclaration { Name = "generic", Type = logical, PropertyAssociation = "gp", KotlinAccessors = new[] { "get" } };
        var generic = new BirFunctionDeclaration { Name = "generic", PropertyName = "generic", PropertyAssociation = "gp", PropertyAccessor = "get",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Params = Array.Empty<BirParameter>(), Ret = logical,
            KotlinPropertyAccessorCarrier = new EventPropertySourceFacts { SourceAssociation = "source", Event = "native-event", Inherited = true } };
        var nested = new BirClassDefinition { Name = "Nested", Kind = "class", Methods = new[] { generic }, Properties = new[] { genericProperty } };
        root.Types = new IBirTypeDeclaration[] { nested };
        AllocateDeclarationsAndProperties(new[] { root });
        var carrier = generic.KotlinPropertyAccessorCarrier.Value;
        if (carrier.Name.Value != "generic" || carrier.Kind.Value != "get" || carrier.Association.Value != "gp"
            || carrier.SourceAssociation.Value != "source" || carrier.Event.Value != "native-event" || !carrier.Inherited.Value
            || genericProperty.GetMethodArity.Value != 1 || !genericProperty.Set.IsNull || genericProperty.SetSig.IsPresent
            || nested.Properties.Value.Length != 1)
            throw new InvalidOperationException("Initial property placement lost its generic or bridge carrier before late allocation");
        var function = new TypeNode.Fn(false, physical, Array.Empty<TypeNode>());
        var parameter = new BirParameter { Type = physical, FunctionSignatureType = function };
        if (FunctionSignatureIdentity.SignatureType(parameter) is not TypeNode.Mod { Req: false })
            throw new InvalidOperationException("Property signature lost its exact function discriminator");
    }
}
