using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A marked stdlib declaration has already performed its Kotlin element predicate and ends by viewing the resulting
// Sequence<Any?> as Sequence<R>. That source cast is valid on erased platforms but cannot alter CLR's reified
// IEnumerable element interface. Replace only that exact declaration-local representation boundary with a named
// generic adapter; the predicate and every other Kotlin semantic remain in the original body.
static class SequenceElementAdapterLowering
{
    const string Marker = "kotlin.clr.ClrSequenceElementAdapter";
    const string Sequence = "kotlin.sequences.Sequence";
    const string Adapter = "kotlin.sequences.ClrSequenceElementAdapter";

    public static void Apply(IEnumerable<JsonNode> roots)
    {
        foreach (var root in roots.OfType<JsonObject>()) WalkOwner(root);
    }

    static void WalkOwner(JsonObject owner)
    {
        if (owner["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>())
                if (HasMarker(method)) RewriteMarkedMethod(method);
        if (owner["types"] is JsonArray types)
            foreach (var type in types.OfType<JsonObject>()) WalkOwner(type);
    }

    static bool HasMarker(JsonObject method) =>
        method["attrs"] is JsonArray attrs
        && attrs.OfType<JsonObject>().Any(attr => TypeJson.OwnerName(attr["attr"]) == Marker);

    static void RewriteMarkedMethod(JsonObject method)
    {
        var typeParams = method["typeParams"] as JsonArray;
        var parameters = method["params"] as JsonArray;
        var body = method["body"] as JsonArray;
        if (method["static"]?.GetValue<bool>() != true
            || typeParams is not { Count: 1 }
            || parameters is not { Count: 1 }
            || body is not { Count: 1 }
            || body[0] is not JsonObject { } ret
            || Str(ret["k"]) != "return"
            || ret["value"] is not JsonObject { } cast
            || Str(cast["k"]) != "cast"
            || cast["e"] == null
            || TypeJson.Read(method["ret"]) is not TypeNode.Fqn
                { Name: Sequence, Args: [TypeNode.Tv { Scope: "method", I: 0 }] }
            || TypeJson.Read(cast["type"]) is not TypeNode.Fqn
                { Name: Sequence, Args: [TypeNode.Tv { Scope: "method", I: 0 }] })
            throw new InvalidOperationException("bir2cir: malformed @ClrSequenceElementAdapter declaration");

        var anyNullable = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
        var input = new TypeNode.Fqn(Sequence, new TypeNode[] { anyNullable });
        ret["value"] = new JsonObject
        {
            ["k"] = "new",
            ["type"] = TypeJson.Write(new TypeNode.Fqn(Adapter,
                new TypeNode[] { new TypeNode.Tv("method", 0) })),
            ["argTypes"] = new JsonArray { TypeJson.Write(input) },
            ["args"] = new JsonArray { cast["e"]!.DeepClone() },
        };
    }

    static string Str(JsonNode node) =>
        (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;
}
