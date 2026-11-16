using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ForeignNullableGenericCrossing
{
    readonly record struct TypeView(BirGraphValue Declaration, string Name, string Kind, bool Abstract,
        IReadOnlyList<BirFunctionDeclaration> Methods);

    static TypeView ReadType(IBirTypeDeclaration declaration) => declaration switch {
        BirClassDefinition type => new(type, type.Name.OrDefault(), type.Kind.OrDefault(),
            type.Abstract.OrDefault(), type.Methods.OrEmpty()),
        BirInterfaceDefinition type => new(type, type.Name.OrDefault(), type.Kind.OrDefault(),
            type.Abstract.OrDefault(), type.Methods.OrEmpty()),
        BirEnumDefinition type => new(type, type.Name.OrDefault(), type.Kind.OrDefault(),
            type.Abstract.OrDefault(), type.Methods.OrEmpty()),
        _ => throw new InvalidOperationException("Unaccounted foreign-slot declaration: " + declaration.GetType().Name),
    };

    static string AttributeText(IBirAttributeValue value) => value switch {
        BirConstant { Value.HasValue: true, Value.Value: BirLiteral.TextLiteral text } => text.Value,
        BirAttributeLiteral { Value.HasValue: true, Value.Value: BirLiteral.TextLiteral text } => text.Value,
        _ => null,
    };

    // These records belong to declaration slots, not to physical type trees or
    // encoded carrier contents. Consume the actual owning graph occurrences.
    static void DropSlotRecords(BirGraphValue node)
    {
        switch (node)
        {
            case BirParameter parameter: parameter.NullableGeneric = default; break;
            case BirFunctionDeclaration method: method.NullableGenericRet = default; break;
            case BirFieldDeclaration field: field.NullableGeneric = default; break;
            case BirPropertyDeclaration property: property.NullableGeneric = default; break;
            case BirCaptureDescriptor capture: capture.NullableGeneric = default; break;
        }
        node.VisitChildren(DropSlotRecords);
    }
}
