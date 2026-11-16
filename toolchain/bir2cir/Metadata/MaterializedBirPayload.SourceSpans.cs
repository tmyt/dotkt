using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class MaterializedBirPayload
{
    internal static void HideSourceSpans(BirGraphValue payload)
    {
        if (payload is BirSourcePoint point) point.Span = default;
        if (payload is BirAttribute attribute && attribute.Attr.OrDefault() is TypeNode.Fqn { Name: DefaultBirCarrier.AttributeName })
            DefaultBirCarrier.Rewrite(attribute, expression => {
                HideSourceSpans((BirGraphValue)expression);
                return expression;
            });
        payload.VisitChildren(HideSourceSpans);
    }
}
