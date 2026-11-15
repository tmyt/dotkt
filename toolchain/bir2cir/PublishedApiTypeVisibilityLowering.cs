using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Kotlin keeps @PublishedApi declarations source-internal while making them part of the cross-module inline ABI.
// BIR retains both facts. This CLR-representation boundary consumes the explicit annotation into public TypeDef
// accessibility; ilemit then maps the resulting CIR visibility one-to-one.
static partial class PublishedApiTypeVisibilityLowering
{
    const string Marker = "kotlin.PublishedApi";

    // Synthesized representations must use the same effective accessibility as their source declaration,
    // even when they are allocated before the final TypeDef visibility pass. Do not copy semantic annotations
    // onto a representation solely to make that later pass recognize it.
    internal static string ResolveVisibility(IBirTypeDeclaration type) =>
        ResolveVisibility(type.Vis.OrDefault(), type.Attrs.OrEmpty().Any(attribute =>
            attribute.Attr.OrDefault() is TypeNode.Fqn { Name: Marker }));

    internal static string ResolveVisibility(string visibility, bool published) =>
        visibility == "internal" && published ? "public" : visibility;

}
