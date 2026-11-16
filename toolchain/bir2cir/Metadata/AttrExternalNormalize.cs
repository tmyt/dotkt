using DotKt.Bir;
using DotKt.Bir.Lowering;

// APPLIED-ATTRIBUTE EXTERNAL NORMALIZE — mint the ilemit-facing `attrExternal` bool for an applied attribute (#48).
// The `attr` type is a structured `{t:fqn}` identity node; ilemit's BuildCab/StampMemberAttrs/DefineParamNames stamp
// an applied attribute ONLY when its type is emitted in THIS assembly (`_types`) or is flagged EXTERNAL
// (`attrExternal:true`, resolved from a referenced .NET assembly); a merely-referenced type is skipped.
//
// kotc flags an imported .NET attribute (a reference-KLIB-projected annotation class) with `"attrClr":true`.
// Consume that frontend origin fact in every build: drop `attrClr` and set `attrExternal:true`. The type lives
// in the BCL or another referenced assembly. bir2cir owns this CLR-relation decision; ilemit just reads the flag.
static class AttrExternalNormalize
{
    public static void Apply(BirGraphValue root) => TypedAttributeBinding.Visit(root, attribute =>
    {
        if (attribute.Attr.OrDefault() is TypeNode.Fqn { Name: not null } && attribute.AttrClr.OrDefault())
        {
            attribute.AttrClr = default;
            attribute.AttrExternal = true;
        }
    });
}
