using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A marked stdlib declaration has already performed its Kotlin element predicate and ends by viewing an
// object-elemented Sequence as Sequence<R>. That source cast is valid on erased platforms but cannot alter CLR's
// reified IEnumerable element interface. Replace only that exact declaration-local representation boundary with a
// named generic adapter; the predicate and every other Kotlin semantic remain in the original body.
static class SequenceElementAdapterLowering
{
    const string Marker = "kotlin.clr.ClrSequenceElementAdapter";
    const string Sequence = "kotlin.sequences.Sequence";
    const string Adapter = "kotlin.sequences.ClrSequenceElementAdapter";

    public static void Apply(IEnumerable<JsonNode> roots, ValueTypeOracle isValueFqn)
    {
        foreach (var root in roots.OfType<JsonObject>()) WalkOwner(root, isValueFqn);
    }

    static void WalkOwner(JsonObject owner, ValueTypeOracle isValueFqn)
    {
        if (owner["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>())
                if (HasMarker(method)) RewriteMarkedMethod(method, isValueFqn);
        if (owner["types"] is JsonArray types)
            foreach (var type in types.OfType<JsonObject>()) WalkOwner(type, isValueFqn);
    }

    static bool HasMarker(JsonObject method) =>
        method["attrs"] is JsonArray attrs
        && attrs.OfType<JsonObject>().Any(attr => TypeJson.OwnerName(attr["attr"]) == Marker);

    static void RewriteMarkedMethod(JsonObject method, ValueTypeOracle isValueFqn)
    {
        var typeParams = method["typeParams"] as JsonArray;
        var parameters = method["params"] as JsonArray;
        var body = method["body"] as JsonArray;
        if (method["static"]?.GetValue<bool>() != true
            || typeParams is not { Count: 1 }
            || parameters is not { Count: 1 }
            || parameters[0] is not JsonObject parameter
            || TypeJson.Read(parameter["type"]) is not TypeNode.Fqn { Name: Sequence, Args: { Length: 1 } }
            || body is not { Count: 1 }
            || body[0] is not JsonObject { } ret
            || Str(ret["k"]) != "return"
            || ret["value"] is not JsonNode value
            || TypeJson.Read(method["ret"]) is not TypeNode.Fqn
                { Name: Sequence, Args: [TypeNode.Tv { Scope: "method", I: 0 }] })
            throw Malformed(method, "expected one static generic Sequence declaration and one return");

        var boundaries = new List<JsonObject>();
        void CollectBoundaries(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (IsElementBoundary(obj, method["ret"], isValueFqn, out _)) boundaries.Add(obj);
                foreach (var child in obj.Select(kv => kv.Value).Where(child => child != null))
                    CollectBoundaries(child);
            }
            else if (node is JsonArray array)
                foreach (var child in array.Where(child => child != null)) CollectBoundaries(child);
        }

        CollectBoundaries(value);
        if (boundaries.Count != 1
            || !IsElementBoundary(boundaries[0], method["ret"], isValueFqn, out var operand))
            throw Malformed(method, $"expected one object-element cast boundary, found {boundaries.Count}");

        JsonNode Rewrite(JsonNode node)
        {
            if (ReferenceEquals(node, boundaries[0]))
                return new JsonObject
                {
                    ["k"] = "new",
                    ["type"] = TypeJson.Write(new TypeNode.Fqn(Adapter,
                        new TypeNode[] { new TypeNode.Tv("method", 0) })),
                    ["argTypes"] = new JsonArray
                    {
                        TypeJson.Write(new TypeNode.Fqn(Sequence, new TypeNode[]
                        {
                            new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")),
                        })),
                    },
                    ["args"] = new JsonArray { operand.DeepClone() },
                };
            if (node is JsonObject obj)
                foreach (var key in obj.Select(kv => kv.Key).ToArray())
                {
                    var child = obj[key];
                    if (child == null) continue;
                    var rewritten = Rewrite(child);
                    if (!ReferenceEquals(child, rewritten)) obj[key] = rewritten;
                }
            else if (node is JsonArray array)
                for (var i = 0; i < array.Count; i++)
                {
                    var child = array[i];
                    if (child == null) continue;
                    var rewritten = Rewrite(child);
                    if (!ReferenceEquals(child, rewritten)) array[i] = rewritten;
                }
            return node;
        }

        var rewrittenValue = Rewrite(value);
        if (!ReferenceEquals(value, rewrittenValue)) ret["value"] = rewrittenValue;
    }

    static bool IsElementBoundary(JsonObject node, JsonNode resultType, ValueTypeOracle isValueFqn,
        out JsonObject operand)
    {
        operand = null;
        if (Str(node["k"]) != "cast"
            || !JsonNode.DeepEquals(node["type"], resultType)
            || node["e"] is not JsonObject source
            || TypeJson.Read(source["sty"]) is not TypeNode.Fqn sequence
            || sequence.Name != Sequence
            || sequence.Args is not { Length: 1 }
            || !IsObjectErasedElement(sequence.Args[0], isValueFqn))
            return false;
        operand = source;
        return true;
    }

    static bool IsObjectErasedElement(TypeNode element, ValueTypeOracle isValueFqn) =>
        IsObjectType(NullableGenericErasure.EraseArgument(element, isValueFqn));

    // Reference-nullability wrappers disappear before physical type emission. The nullable-generic rule above owns
    // which possibly-value arguments box; this final spelling check only recognizes the equivalent object leaves.
    static bool IsObjectType(TypeNode type) => type switch
    {
        TypeNode.Nullable nullable => IsObjectType(nullable.Of),
        TypeNode.Oblivious oblivious => IsObjectType(oblivious.Of),
        TypeNode.Fqn { Args: null } f =>
            f.Name is "object" or "System.Object" or "kotlin.Any" or "kotlin.Nothing",
        _ => false,
    };

    static string Str(JsonNode node) =>
        (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;

    static InvalidOperationException Malformed(JsonObject method, string detail) =>
        new($"bir2cir: malformed @ClrSequenceElementAdapter declaration '{Str(method["name"])}': {detail}");
}
