using DotKt.Bir;
using DotKt.Cir;
using A = DotKt.Cir.AttributeArgument;

static partial class RoundtripMetadata
{
    // These are the compiler-owned, explicitly declared MessagePack carrier
    // contracts. Do not infer a carrier from an arbitrary attribute's arguments.
    // All source capture/rewriting is complete before the physical projection
    // invokes this boundary. Nested opaque bytes remain byte-for-byte intact.
    internal static AttributeApplication SelectCarrierTransport(AttributeApplication attribute, bool release)
    {
        if (!release) return attribute;
        var slot = (attribute.Attr as TypeNode.Fqn)?.Name switch {
            AKInline or AKType or AKNullableGen or AKCollIdentity or AKSuspendFn or AKSuspendResult
                or AKTypeAlias or AKConstructorAdapter => 0,
            DefaultBirCarrier.AttributeName => 1,
            _ => -1,
        };
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
