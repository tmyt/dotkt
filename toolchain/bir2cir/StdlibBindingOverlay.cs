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

    public static IReadOnlyDictionary<string, int[]> Apply(IEnumerable<JsonNode> roots, string path)
    {
        if (path == null) return new Dictionary<string, int[]>(StringComparer.Ordinal);
        JsonNode document;
        try
        {
            document = JsonNode.Parse(File.ReadAllText(path), documentOptions: DotKt.Bir.BirJson.DocOptions);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            throw new InvalidDataException($"stdlib binding overlay '{path}' could not be read", ex);
        }

        return ApplyDocument(roots, document, path);
    }

    static IReadOnlyDictionary<string, int[]> ApplyDocument(
        IEnumerable<JsonNode> roots, JsonNode document, string source)
    {
        if (document is not JsonObject root
            || Str(root["type"]) != CodecType
            || Int(root["version"]) != CodecVersion
            || root["declarations"] is not JsonArray declarations)
            throw new InvalidDataException(
                $"stdlib binding overlay '{source}' must be {CodecType}/{CodecVersion} with a declarations array");

        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in declarations.OfType<JsonObject>())
        {
            if (Str(item["declarationId"]) is string declarationId) selectedIds.Add(declarationId);
            if (Str(item["implementationDeclarationId"]) is string implementationId) selectedIds.Add(implementationId);
        }
        var methods = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var birRoot in roots) IndexMethods(birRoot, selectedIds, methods);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var physicalParameterIndices = new Dictionary<string, int[]>(StringComparer.Ordinal);
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
            if (binding.ContainsKey("physicalName"))
            {
                var physicalName = RequiredStringFact(binding, "physicalName", source, id);
                if (string.IsNullOrWhiteSpace(physicalName))
                    throw new InvalidDataException(
                        $"stdlib binding overlay '{source}' gives declaration '{id}' an empty physicalName");
                if (!binding.ContainsKey("expectedPhysicalName"))
                    throw new InvalidDataException(
                        $"stdlib binding overlay '{source}' physicalName for declaration '{id}' has no expectedPhysicalName");
                var expectedNode = binding["expectedPhysicalName"];
                var expectedPhysicalName = expectedNode == null ? null : Str(expectedNode)
                    ?? throw new InvalidDataException(
                        $"stdlib binding overlay '{source}' declaration '{id}' has a non-string expectedPhysicalName");
                var existingPhysicalName = Str(method["explicitClrName"]);
                if (existingPhysicalName != expectedPhysicalName)
                    throw new InvalidDataException(
                        $"stdlib binding overlay '{source}' declaration '{id}' expected frontend physical name "
                        + $"'{expectedPhysicalName ?? "<none>"}', but found '{existingPhysicalName ?? "<none>"}'");
                method["explicitClrName"] = physicalName;
                applied = true;
            }

            if (binding.ContainsKey("sequenceElementAdapter"))
            {
                var adapter = RequiredBooleanFact(binding, "sequenceElementAdapter", source, id);
                if (!adapter)
                    throw new InvalidDataException(
                        $"stdlib binding overlay '{source}' gives declaration '{id}' a false sequenceElementAdapter");
                AddMarker(method, SequenceElementAdapter);
                applied = true;
            }

            if (binding.ContainsKey("implementationDeclarationId"))
            {
                var implementationId = RequiredStringFact(binding, "implementationDeclarationId", source, id);
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
                if (implementation["body"] is not JsonArray { Count: > 0 } body)
                    throw new InvalidDataException(
                        $"stdlib binding overlay '{source}' implementation declaration '{implementationId}' has no non-empty body");
                method["body"] = body.DeepClone();
                applied = true;
            }

            if (binding.ContainsKey("physicalParameterTypes"))
            {
                if (binding["physicalParameterTypes"] is not JsonArray { Count: > 0 } physicalParameterTypes
                    || method["params"] is not JsonArray parameters)
                    throw new InvalidDataException(
                        $"stdlib binding overlay '{source}' declaration '{id}' has malformed physicalParameterTypes");
                var rewritten = new HashSet<int>();
                foreach (var entry in physicalParameterTypes)
                {
                    if (entry is not JsonObject parameterBinding
                        || Int(parameterBinding["index"]) is not int index
                        || parameterBinding["expectedType"] is not JsonNode expectedType
                        || parameterBinding["type"] is not JsonNode physicalType
                        || index < 0 || index >= parameters.Count
                        || parameters[index] is not JsonObject parameter)
                        throw new InvalidDataException(
                            $"stdlib binding overlay '{source}' declaration '{id}' has a malformed physical parameter binding");
                    if (!rewritten.Add(index))
                        throw new InvalidDataException(
                            $"stdlib binding overlay '{source}' declaration '{id}' repeats physical parameter {index}");
                    if (TypeJson.Read(expectedType) == null || !JsonNode.DeepEquals(parameter["type"], expectedType))
                        throw new InvalidDataException(
                            $"stdlib binding overlay '{source}' declaration '{id}' expected parameter {index} type "
                            + $"'{expectedType.ToJsonString()}', but found '{parameter["type"]?.ToJsonString() ?? "<none>"}'");
                    if (TypeJson.Read(physicalType) == null)
                        throw new InvalidDataException(
                            $"stdlib binding overlay '{source}' declaration '{id}' gives parameter {index} an invalid physical type");
                    if (parameter["kotlinType"] != null)
                        throw new InvalidDataException(
                            $"stdlib binding overlay '{source}' declaration '{id}' parameter {index} already has a Kotlin type carrier");
                    // The CLR slot and Kotlin surface intentionally diverge. RoundtripMetadata emits the source type
                    // as [KotlinType], while declaration identity exposes the rewritten MethodDef signature to calls.
                    parameter["kotlinType"] = TypeNode.ToJson(TypeJson.Read(parameter["type"])!);
                    parameter["type"] = physicalType.DeepClone();
                }
                physicalParameterIndices[id] = rewritten.OrderBy(index => index).ToArray();
                applied = true;
            }

            if (!applied)
                throw new InvalidDataException(
                    $"stdlib binding overlay '{source}' declaration '{id}' supplies no binding fact");
        }
        return physicalParameterIndices;
    }

    static void ValidateImplementationSignature(
        JsonObject declaration, JsonObject implementation, string source, string declarationId, string implementationId)
    {
        foreach (var slot in new[] { "static", "mods", "suspendRet", "typeParams", "params", "ret" })
            if (!JsonNode.DeepEquals(declaration[slot], implementation[slot]))
                throw new InvalidDataException(
                    $"stdlib binding overlay '{source}' implementation declaration '{implementationId}' does not match "
                    + $"declaration '{declarationId}' in its {slot}");
    }

    static void IndexMethods(
        JsonNode node, IReadOnlySet<string> selectedIds, Dictionary<string, JsonObject> methods)
    {
        if (node is not JsonObject owner) return;
        if (owner["methods"] is JsonArray declarations)
            foreach (var method in declarations.OfType<JsonObject>())
                if (Str(method["declarationId"]) is string id && selectedIds.Contains(id) && !methods.TryAdd(id, method))
                    throw new InvalidDataException($"duplicate frontend declaration identity '{id}'");
        if (owner["types"] is JsonArray types)
            foreach (var type in types) IndexMethods(type, selectedIds, methods);
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

    static string RequiredStringFact(JsonObject binding, string key, string source, string declarationId) =>
        binding[key] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : throw new InvalidDataException(
                $"stdlib binding overlay '{source}' declaration '{declarationId}' has a non-string {key}");

    static bool RequiredBooleanFact(JsonObject binding, string key, string source, string declarationId) =>
        binding[key] is JsonValue value && value.TryGetValue<bool>(out var result)
            ? result
            : throw new InvalidDataException(
                $"stdlib binding overlay '{source}' declaration '{declarationId}' has a non-boolean {key}");

    public static void SelfTest()
    {
        const string id = "dotkt-declaration-v1:test";
        var method = new JsonObject
        {
            ["name"] = "source",
            ["declarationId"] = id,
            ["attrs"] = new JsonArray(),
            ["params"] = new JsonArray
            {
                new JsonObject { ["name"] = "value", ["type"] = TypeJson.Fqn("System.Int32") },
            },
            ["ret"] = TypeJson.Fqn("System.Int32"),
            ["body"] = new JsonArray { new JsonObject { ["k"] = "return", ["e"] = new JsonObject { ["k"] = "const", ["type"] = "int", ["v"] = 0 } } },
        };
        var implementation = new JsonObject
        {
            ["name"] = "implementation",
            ["declarationId"] = id + ":implementation",
            ["attrs"] = new JsonArray(),
            ["params"] = new JsonArray
            {
                new JsonObject { ["name"] = "value", ["type"] = TypeJson.Fqn("System.Int32") },
            },
            ["ret"] = TypeJson.Fqn("System.Int32"),
            ["body"] = new JsonArray { new JsonObject { ["k"] = "return", ["e"] = new JsonObject { ["k"] = "const", ["type"] = "int", ["v"] = 1 } } },
        };
        var unrelatedDuplicateA = new JsonObject { ["name"] = "unrelatedA", ["declarationId"] = id + ":unrelated" };
        var unrelatedDuplicateB = new JsonObject { ["name"] = "unrelatedB", ["declarationId"] = id + ":unrelated" };
        var bir = new JsonObject
        {
            ["methods"] = new JsonArray { method, implementation, unrelatedDuplicateA, unrelatedDuplicateB },
        };
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
                    ["expectedPhysicalName"] = null,
                    ["sequenceElementAdapter"] = true,
                    ["implementationDeclarationId"] = id + ":implementation",
                    ["implementationSourceName"] = "implementation",
                    ["physicalParameterTypes"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["index"] = 0,
                            ["expectedType"] = TypeJson.Fqn("System.Int32"),
                            ["type"] = TypeJson.Fqn("System.Object"),
                        },
                    },
                },
            },
        };

        var physicalParameterIndices = ApplyDocument(new[] { bir }, overlay, "selftest");
        var semanticRoot = new JsonObject { ["methods"] = new JsonArray { method.DeepClone() } };
        var semanticSignature = DeclarationIdentityBinding.PreserveSourceFacts(new[] { semanticRoot })[id];
        if (Str(method["explicitClrName"]) != "physical"
            || method["attrs"] is not JsonArray attrs
            || !attrs.OfType<JsonObject>().Any(attr => TypeJson.OwnerName(attr["attr"]) == SequenceElementAdapter)
            || TypeJson.Read(method["params"]![0]!["type"]) is not TypeNode.Fqn { Name: "System.Object" }
            || Str(method["params"]![0]!["kotlinType"]) != TypeNode.ToJson(new TypeNode.Fqn("System.Int32"))
            || TypeJson.Read(semanticSignature["params"]![0]) is not TypeNode.Fqn { Name: "System.Int32" }
            || !physicalParameterIndices.TryGetValue(id, out var rewrittenIndices)
            || !rewrittenIndices.SequenceEqual(new[] { 0 })
            || ((method["body"] as JsonArray)?[0]?["e"]?["v"] as JsonValue)?.GetValue<int>() != 1)
            throw new InvalidOperationException("StdlibBindingOverlay self-test failed");

        var stalePhysicalName = overlay.DeepClone().AsObject();
        stalePhysicalName["declarations"]![0]!["expectedPhysicalName"] = "unexpected";
        ExpectInvalid(() => ApplyDocument(new[] { bir.DeepClone() }, stalePhysicalName, "selftest-stale-name"),
            "stale physical name");

        var duplicateSelected = bir.DeepClone().AsObject();
        duplicateSelected["methods"]!.AsArray().Add(method.DeepClone());
        ExpectInvalid(() => ApplyDocument(new[] { duplicateSelected }, overlay, "selftest-duplicate"),
            "duplicate selected declaration");

        var wrongShape = implementation.DeepClone().AsObject();
        wrongShape["static"] = true;
        ExpectInvalid(() => ValidateImplementationSignature(method, wrongShape, "selftest-shape", id,
            id + ":implementation"), "implementation shape mismatch");

        var nonBooleanAdapter = overlay.DeepClone().AsObject();
        nonBooleanAdapter["declarations"]![0]!["sequenceElementAdapter"] = "yes";
        ExpectInvalid(() => ApplyDocument(new[] { bir.DeepClone() }, nonBooleanAdapter, "selftest-adapter-type"),
            "non-boolean sequence element adapter");

        var nonStringImplementation = overlay.DeepClone().AsObject();
        nonStringImplementation["declarations"]![0]!["implementationDeclarationId"] = true;
        ExpectInvalid(() => ApplyDocument(new[] { bir.DeepClone() }, nonStringImplementation,
            "selftest-implementation-type"), "non-string implementation declaration identity");

        var staleParameterType = overlay.DeepClone().AsObject();
        staleParameterType["declarations"]![0]!["physicalParameterTypes"]![0]!["expectedType"] =
            TypeJson.Fqn("System.String");
        ExpectInvalid(() => ApplyDocument(new[] { bir.DeepClone() }, staleParameterType,
            "selftest-stale-parameter"), "stale physical parameter type");
    }

    static void ExpectInvalid(Action action, string scenario)
    {
        try
        {
            action();
            throw new InvalidOperationException($"StdlibBindingOverlay self-test accepted {scenario}");
        }
        catch (InvalidDataException)
        {
        }
    }
}
