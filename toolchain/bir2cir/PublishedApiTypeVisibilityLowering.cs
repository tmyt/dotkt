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
        if (type["vis"]?.GetValue<string>() == "internal" && HasMarker(type))
            type["vis"] = "public";
    }

    static bool HasMarker(JsonObject type) =>
        type["attrs"] is JsonArray attrs
        && attrs.OfType<JsonObject>().Any(attr => TypeJson.OwnerName(attr["attr"]) == Marker);
}
