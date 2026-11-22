using DotKt.Bir;
using DotKt.Cir;
using A = DotKt.Cir.AttributeArgument;

static partial class RoundtripMetadata
{
    // These are the compiler-owned, explicitly declared MessagePack carrier
    // contracts. Do not infer a carrier from an arbitrary attribute's arguments.
    // All source capture/rewriting is complete before the physical projection
    // invokes this boundary. Nested opaque bytes remain byte-for-byte intact.
    internal static int CarrierCodecSlot(string name) => name switch {
            AKInline or AKType or AKNullableGen or AKCollIdentity or AKSuspendFn or AKSuspendResult
                or AKTypeAlias or AKSourceCallable or AKDslScope or AKConstructorAdapter or AKRichEnum or AKBasicEnum
                or AKDeclarationIdentity or AKTypeVisibility or CallableVisibilityLowering.Carrier or AKSupertypes
                or AKTypeParameterBounds or AKLateinitState or AKPropertyStorage or AKExtensionCore or AKSourceMethod
                or AKPropertyAccessor or AKManagedReferenceContract or AKInnerConstructorFactory or AKProjectedConstructor or AKProjectedMethod
                or AKCompanion or AKCompanionExt or AKStaticCarrier => 0,
            DefaultBirCarrier.AttributeName => 1,
            _ => -1,
        };

    internal static AttributeApplication SelectCarrierTransport(AttributeApplication attribute, bool release)
    {
        if (!release || attribute.AttrExternal == true) return attribute;
        var slot = CarrierCodecSlot((attribute.Attr as TypeNode.Fqn)?.Name);
        if (slot < 0) return attribute;
        var codec = (A.TextValue)attribute.Args[slot];
        var bytes = (A.BytesValue)attribute.Args[slot + 1];
        var selected = BirCarrier.SelectTransport(codec.Value, bytes.Bytes, release);
        if (selected.Codec == codec.Value) return attribute;
        attribute.Args[slot] = new A.TextValue(selected.Codec);
        attribute.Args[slot + 1] = new A.BytesValue(selected.Content);
        return attribute;
    }
}
