using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// The compiler-provided stdlib keeps its common sources byte-for-byte aligned with upstream Kotlin. CLR-only
// representation facts that upstream cannot spell live in this explicit sidecar instead. The sidecar selects an exact
// frontend declaration identity; it is data supplied by the trusted stdlib build, never a name/body heuristic in the
// compiler. A stale identity fails the build rather than silently binding a nearby overload.
static class StdlibBindingOverlay
{
    const string CodecType = "dotkt-stdlib-bindings";
    const int CodecVersion = 1;
    const string SequenceElementAdapter = "kotlin.clr.ClrSequenceElementAdapter";

    public static void Apply(IEnumerable<JsonNode> roots, string path)
    {
        if (path == null) return;
        JsonNode document;
        try
        {
            document = JsonNode.Parse(File.ReadAllText(path), documentOptions: DotKt.Bir.BirJson.DocOptions);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            throw new InvalidDataException($"stdlib binding overlay '{path}' could not be read", ex);
        }

        ApplyDocument(roots, document, path);
    }

    static void ApplyDocument(IEnumerable<JsonNode> roots, JsonNode document, string source)
    {
        if (document is not JsonObject root
            || Str(root["type"]) != CodecType
            || Int(root["version"]) != CodecVersion
            || root["declarations"] is not JsonArray declarations)
            throw new InvalidDataException(
                $"stdlib binding overlay '{source}' must be {CodecType}/{CodecVersion} with a declarations array");

        var methods = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var birRoot in roots) IndexMethods(birRoot, methods);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in declarations)
        {
            if (item is not JsonObject binding
                || Str(binding["declarationId"]) is not string id
                || Str(binding["sourceName"]) is not string sourceName)
                throw new InvalidDataException(
                    $"stdlib binding overlay '{source}' contains a declaration without declarationId/sourceName");
            if (!seen.Add(id))
                throw new InvalidDataException($"stdlib binding overlay '{source}' repeats declaration '{id}'");
            if (!methods.TryGetValue(id, out var method))
                throw new InvalidDataException(
                    $"stdlib binding overlay '{source}' references missing declaration '{id}' ({sourceName})");
            if (Str(method["name"]) != sourceName)
                throw new InvalidDataException(
                    $"stdlib binding overlay '{source}' expected declaration '{id}' to be named '{sourceName}', "
                    + $"but the frontend supplied '{Str(method["name"])}'");

            var applied = false;
            if (binding["physicalName"] is JsonValue physicalValue
                && physicalValue.TryGetValue<string>(out var physicalName))
            {
                if (string.IsNullOrWhiteSpace(physicalName))
                    throw new InvalidDataException(
                        $"stdlib binding overlay '{source}' gives declaration '{id}' an empty physicalName");
                method["explicitClrName"] = physicalName;
                applied = true;
            }

            if (binding["sequenceElementAdapter"] is JsonValue adapterValue
                && adapterValue.TryGetValue<bool>(out var adapter))
            {
                if (!adapter)
                    throw new InvalidDataException(
                        $"stdlib binding overlay '{source}' gives declaration '{id}' a false sequenceElementAdapter");
                AddMarker(method, SequenceElementAdapter);
                applied = true;
            }

            if (binding["implementationDeclarationId"] is JsonValue implementationValue
                && implementationValue.TryGetValue<string>(out var implementationId))
            {
                if (Str(binding["implementationSourceName"]) is not string implementationSourceName)
                    throw new InvalidDataException(
                        $"stdlib binding overlay '{source}' implementation for declaration '{id}' has no implementationSourceName");
                if (implementationId == id || !methods.TryGetValue(implementationId, out var implementation))
                    throw new InvalidDataException(
                        $"stdlib binding overlay '{source}' references missing implementation declaration "
                        + $"'{implementationId}' ({implementationSourceName}) for '{id}'");
                if (Str(implementation["name"]) != implementationSourceName)
                    throw new InvalidDataException(
                        $"stdlib binding overlay '{source}' expected implementation declaration '{implementationId}' "
                        + $"to be named '{implementationSourceName}', but the frontend supplied "
                        + $"'{Str(implementation["name"])}'");
                ValidateImplementationSignature(method, implementation, source, id, implementationId);
                if (implementation["body"] is not JsonNode body)
                    throw new InvalidDataException(
                        $"stdlib binding overlay '{source}' implementation declaration '{implementationId}' has no body");
                method["body"] = body.DeepClone();
                applied = true;
            }

            if (!applied)
                throw new InvalidDataException(
                    $"stdlib binding overlay '{source}' declaration '{id}' supplies no binding fact");
        }
    }

    static void ValidateImplementationSignature(
        JsonObject declaration, JsonObject implementation, string source, string declarationId, string implementationId)
    {
        foreach (var slot in new[] { "typeParams", "params", "ret" })
            if (!JsonNode.DeepEquals(declaration[slot], implementation[slot]))
                throw new InvalidDataException(
                    $"stdlib binding overlay '{source}' implementation declaration '{implementationId}' does not match "
                    + $"declaration '{declarationId}' in its {slot}");
    }

    static void IndexMethods(JsonNode node, Dictionary<string, JsonObject> methods)
    {
        if (node is not JsonObject owner) return;
        if (owner["methods"] is JsonArray declarations)
            foreach (var method in declarations.OfType<JsonObject>())
                if (Str(method["declarationId"]) is string id && !methods.TryAdd(id, method))
                    throw new InvalidDataException($"duplicate frontend declaration identity '{id}'");
        if (owner["types"] is JsonArray types)
            foreach (var type in types) IndexMethods(type, methods);
    }

    static void AddMarker(JsonObject method, string marker)
    {
        var attrs = method["attrs"] as JsonArray;
        if (attrs == null) method["attrs"] = attrs = new JsonArray();
        if (attrs.OfType<JsonObject>().Any(attr => TypeJson.OwnerName(attr["attr"]) == marker)) return;
        attrs.Add(new JsonObject
        {
            ["attr"] = TypeJson.Write(new TypeNode.Fqn(marker)),
            ["argTypes"] = new JsonArray(),
            ["args"] = new JsonArray(),
        });
    }

    static string Str(JsonNode node) =>
        (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;

    static int? Int(JsonNode node) =>
        (node as JsonValue)?.TryGetValue<int>(out var value) == true ? value : null;

    public static void SelfTest()
    {
        const string id = "dotkt-declaration-v1:test";
        var method = new JsonObject
        {
            ["name"] = "source",
            ["declarationId"] = id,
            ["attrs"] = new JsonArray(),
            ["params"] = new JsonArray(),
            ["ret"] = TypeJson.Fqn("System.Int32"),
            ["body"] = new JsonArray { new JsonObject { ["k"] = "return", ["e"] = new JsonObject { ["k"] = "const", ["type"] = "int", ["v"] = 0 } } },
        };
        var implementation = new JsonObject
        {
            ["name"] = "implementation",
            ["declarationId"] = id + ":implementation",
            ["attrs"] = new JsonArray(),
            ["params"] = new JsonArray(),
            ["ret"] = TypeJson.Fqn("System.Int32"),
            ["body"] = new JsonArray { new JsonObject { ["k"] = "return", ["e"] = new JsonObject { ["k"] = "const", ["type"] = "int", ["v"] = 1 } } },
        };
        var bir = new JsonObject { ["methods"] = new JsonArray { method, implementation } };
        var overlay = new JsonObject
        {
            ["type"] = CodecType,
            ["version"] = CodecVersion,
            ["declarations"] = new JsonArray
            {
                new JsonObject
                {
                    ["declarationId"] = id,
                    ["sourceName"] = "source",
                    ["physicalName"] = "physical",
                    ["sequenceElementAdapter"] = true,
                    ["implementationDeclarationId"] = id + ":implementation",
                    ["implementationSourceName"] = "implementation",
                },
            },
        };

        ApplyDocument(new[] { bir }, overlay, "selftest");
        if (Str(method["explicitClrName"]) != "physical"
            || method["attrs"] is not JsonArray attrs
            || !attrs.OfType<JsonObject>().Any(attr => TypeJson.OwnerName(attr["attr"]) == SequenceElementAdapter)
            || ((method["body"] as JsonArray)?[0]?["e"]?["v"] as JsonValue)?.GetValue<int>() != 1)
            throw new InvalidOperationException("StdlibBindingOverlay self-test failed");
    }
}
