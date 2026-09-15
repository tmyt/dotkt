using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A semantic shared variable need not be a heap allocation when inline splicing has removed every capture.
// Scalar-replace a cell only when every use is an access to its value in the same executable frame. Passing,
// aliasing or capturing the cell keeps shared storage. This is type-independent: in particular a byref-like
// value used only by inline code must remain a legal CLR local, not become an illegal heap field.
static class LocalRefCellLowering
{
    static string Str(JsonNode node) => (node as JsonValue)?.GetValue<string>();

    public static void Apply(JsonObject file)
    {
        if (file["refTypes"] is not JsonArray registry) return;
        var specs = registry.OfType<JsonObject>().ToDictionary(spec => Str(spec["name"]), StringComparer.Ordinal);
        foreach (var frame in Frames(file).ToArray()) LowerFrame(frame, specs);

        // Do not synthesize an unused cell class, which could itself contain an illegal byref-like field.
        // Follow only the explicit registry/type identities, never generated-name conventions.
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        void Collect(JsonNode node)
        {
            if (ReferenceEquals(node, registry)) return;
            if (node is JsonObject obj)
            {
                if (Str(obj["t"]) == "fqn" && Str(obj["name"]) is string name) referenced.Add(name);
                foreach (var pair in obj) Collect(pair.Value);
            }
            else if (node is JsonArray array) foreach (var child in array) Collect(child);
        }
        Collect(file);
        var count = -1;
        while (count != referenced.Count)
        {
            count = referenced.Count;
            foreach (var name in referenced.ToArray())
                if (specs.TryGetValue(name, out var spec)) Collect(spec);
        }
        for (var i = registry.Count - 1; i >= 0; i--)
            if (!referenced.Contains(Str(registry[i]["name"]))) registry.RemoveAt(i);
    }

    static IEnumerable<JsonNode> Frames(JsonObject owner)
    {
        foreach (var key in new[] { "methods", "ctors" })
            if (owner[key] is JsonArray declarations)
                foreach (var declaration in declarations.OfType<JsonObject>())
                    yield return declaration;
        if (owner["fields"] is JsonArray fields)
            foreach (var field in fields.OfType<JsonObject>())
                if (field["init"] is JsonNode init) yield return init;
        if (owner["types"] is JsonArray types)
            foreach (var type in types.OfType<JsonObject>())
                foreach (var frame in Frames(type)) yield return frame;
    }

    static IEnumerable<JsonObject> Objects(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (TypeJson.IsType(obj)) yield break;
            yield return obj;
            foreach (var pair in obj)
                foreach (var child in Objects(pair.Value)) yield return child;
        }
        else if (node is JsonArray array)
            foreach (var item in array)
                foreach (var child in Objects(item)) yield return child;
    }

    static void LowerFrame(JsonNode frame, Dictionary<string, JsonObject> specs)
    {
        var objects = Objects(frame).ToArray();
        var declarations = objects.Where(obj => Str(obj["k"]) == "var").ToArray();
        foreach (var declaration in declarations)
        {
            var name = Str(declaration["name"]);
            if (name == null || declarations.Count(other => Str(other["name"]) == name) != 1) continue;
            var cellType = declaration["type"];
            if (cellType is not JsonObject type || Str(type["t"]) != "fqn"
                || !specs.ContainsKey(Str(type["name"]))
                || declaration["init"] is not JsonObject allocation || Str(allocation["k"]) != "new"
                || !JsonNode.DeepEquals(allocation["type"], cellType)
                || allocation["args"] is not JsonArray { Count: 1 } arguments
                || allocation["argTypes"] is not JsonArray { Count: 1 } argumentTypes) continue;

            var accesses = new HashSet<JsonObject>();
            var escapes = false;
            foreach (var use in objects.Where(obj => Str(obj["name"]) == name && !ReferenceEquals(obj, declaration)))
            {
                // This also rejects another binder, an implicit capture descriptor, or a direct setLocal.
                if (Str(use["k"]) != "local" || use.Parent is not JsonObject access
                    || !ReferenceEquals(access["recv"], use) || Str(access["name"]) != "v"
                    || Str(access["k"]) is not ("field" or "setField")
                    || !JsonNode.DeepEquals(access["ownerType"], cellType))
                { escapes = true; break; }
                accesses.Add(access);
            }
            if (escapes) continue;

            var element = argumentTypes[0].DeepClone();
            var initializer = arguments[0];
            arguments.RemoveAt(0);
            declaration["type"] = element.DeepClone();
            declaration["init"] = initializer;
            foreach (var access in accesses)
            {
                var write = Str(access["k"]) == "setField";
                var value = access["value"];
                access.Remove("value");
                var position = access["pos"]?.DeepClone();
                access.Clear();
                access["k"] = write ? "setLocal" : "local";
                access["name"] = name;
                if (write) access["value"] = value;
                else access["sty"] = element.DeepClone();
                if (position != null) access["pos"] = position;
            }
        }
    }
}
