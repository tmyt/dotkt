using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Kotlin keeps @PublishedApi declarations source-internal while making them part of the cross-module inline ABI.
// BIR retains both facts. This CLR-representation boundary consumes the explicit annotation into public TypeDef
// accessibility; ilemit then maps the resulting CIR visibility one-to-one.
static class PublishedApiTypeVisibilityLowering
{
    const string Marker = "kotlin.PublishedApi";

    public static void ApplyAll(IEnumerable<JsonNode> roots)
    {
        foreach (var root in roots.OfType<JsonObject>())
            if (root["types"] is JsonArray types)
                foreach (var type in types.OfType<JsonObject>())
                    Apply(type);
    }

    static void Apply(JsonObject type)
    {
        if (ResolveVisibility(type) is { } visibility)
            type["vis"] = visibility;
    }

    // Synthesized representations must use the same effective accessibility as their source declaration,
    // even when they are allocated before the final TypeDef visibility pass. Do not copy semantic annotations
    // onto a representation solely to make that later pass recognize it.
    internal static string ResolveVisibility(JsonObject type)
    {
        var visibility = type["vis"]?.GetValue<string>();
        return visibility == "internal" && HasMarker(type) ? "public" : visibility;
    }

    static bool HasMarker(JsonObject type) =>
        type["attrs"] is JsonArray attrs
        && attrs.OfType<JsonObject>().Any(attr => TypeJson.OwnerName(attr["attr"]) == Marker);
}
