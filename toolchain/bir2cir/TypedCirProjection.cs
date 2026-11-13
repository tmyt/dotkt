using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using A = DotKt.Cir.AttributeArgument;

// The physical projection consumes an owning lowering graph. No JSON objects or
// text participate here; exact binding facts and opaque carrier bytes pass through.
sealed partial class TypedCirProjection(IReadOnlySet<string> emitted)
{
    internal static string TypeName(IBirTypeDeclaration type) => type switch {
        BirClassDefinition value => value.Name.OrDefault(),
        BirInterfaceDefinition value => value.Name.OrDefault(),
        BirEnumDefinition value => value.Name.OrDefault(),
        _ => throw new FormatException(),
    };

    static T Require<T>(OptionalField<T> value) => value.HasValue ? value.Value : throw new FormatException();

    static OptionalField<TResult> ProjectOptional<T, TResult>(OptionalField<T> value,
        Func<T, TResult> project, bool allowNull)
        => !value.IsPresent ? default : value.IsNull
            ? allowNull ? OptionalField<TResult>.Null : throw new FormatException()
            : OptionalField<TResult>.Of(project(value.Value));

    static void CheckSourceStamp(OptionalField<TypeNode> stamp, OptionalField<TypeNode> result)
    {
        if (stamp.HasValue && result.HasValue && !IrSanity.StampAgrees(stamp.Value, result.Value))
            throw new InvalidOperationException("Unconsumed stale physical result stamp");
    }

    static IScalarValue ProjectExecutableLiteral(BirLiteral literal, TypeNode slot)
    {
        var scalarType = BirTypeLowering.LowerType(slot, refBuild: true, force: true, typeArg: false);
        return (IScalarValue)AttributeProjection.Literal(literal, scalarType);
    }

    static MemberRefNode ProjectMemberReference(BirMemberReference value)
    {
        var member = new MemberRefNode(Require(value.Kind), Require(value.Assembly), Require(value.DeclaringType),
            Require(value.Name), Require(value.GenericArity), Require(value.ReturnType),
            value.CallingConvention.OrDefault(), value.ParameterTypes.OrDefault());
        member.Validate();
        return member;
    }

    bool AdmitAttribute(BirAttribute attribute) => attribute.AttrExternal.OrDefault()
        || emitted.Contains((attribute.Attr.OrDefault() as TypeNode.Fqn)?.Name ?? "");

    AttributeApplication ProjectAttribute(BirAttribute attribute) => new(
        Require(attribute.Attr), Require(attribute.ArgTypes), Require(attribute.Args).Select(ProjectAttributeArgument).ToArray(),
        attribute.AttrExternal.HasValue ? attribute.AttrExternal.Value : null, attribute.AttrAssembly.OrDefault(),
        attribute.MemberRef.HasValue ? ProjectMemberReference(attribute.MemberRef.Value) : null,
        attribute.NamedArgs.HasValue ? attribute.NamedArgs.Value.Select(argument => new NamedAttributeArgument(
            Require(argument.Kind), Require(argument.Name), Require(argument.Type),
            ProjectAttributeArgument(Require(argument.Value)))).ToArray() : null);

    A ProjectAttributeArgument(IBirAttributeValue value) => value switch
    {
        BirOpaqueBytes bytes => new A.BytesValue(Require(bytes.Bytes)),
        BirClassReference reference => new A.TypeValue(Require(reference.Type)),
        BirNewArray array => new A.ArrayValue(Require(array.Elems).Select(ProjectAttributeArgument).ToArray(), array.Elem.OrDefault()),
        BirEnumValue constant => new A.EnumValue(constant.Underlying.OrDefault(), constant.PhysicalValue.OrDefault(),
            constant.Ordinal.HasValue ? constant.Ordinal.Value : null, constant.Type.OrDefault()),
        BirConstant constant => MetadataLiteral(constant.Value, constant.Type.OrDefault()),
        BirAttributeLiteral constant => MetadataLiteral(constant.Value, constant.Type.OrDefault()),
        _ => new A.UnencodableValue(),
    };

    static A MetadataLiteral(OptionalField<BirLiteral> literal, TypeNode type) => !literal.IsPresent
        ? new A.UnencodableValue() : literal.IsNull ? new A.NullValue() : AttributeProjection.Literal(literal.Value, type);
}
