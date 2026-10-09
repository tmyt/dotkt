using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A mutable capture's heap cell keeps the original Kotlin generic frame, but its value field must accept the
// representation actually written to it. Only cells explicitly assembled from refTypes participate: neither
// ordinary CLR fields nor generated-class names/layout are evidence of mutable-capture storage.
static class ExistentialSharedCellAlignment
{
    public static void ApplyAll(IReadOnlyList<JsonNode> roots,
        IReadOnlyList<SharedSyntheticSynthesis.CellStorage> cells,
        IReadOnlyDictionary<string, string> localCarriers, ReferenceMetadataIndex refs)
    {
        var slots = cells.Distinct().ToDictionary(cell => cell.Owner, StringComparer.Ordinal);
        var definitions = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        void Collect(JsonNode node)
        {
            if (node is not JsonObject owner) return;
            if (owner["types"] is not JsonArray types) return;
            foreach (var type in types.OfType<JsonObject>())
            {
                if (Str(type["name"]) is string name && slots.ContainsKey(name))
                {
                    if (!definitions.TryGetValue(name, out var declarations))
                        definitions[name] = declarations = new List<JsonObject>();
                    declarations.Add(type);
                }
                Collect(type);
            }
        }
        foreach (var root in roots) Collect(root);
        var types = SupertypeGraph.Collect(roots);
        var carriers = localCarriers.Values.ToHashSet(StringComparer.Ordinal);
        var projected = new Dictionary<string, TypeNode>(StringComparer.Ordinal);

        void Walk(JsonNode node, Action<JsonObject> action)
        {
            if (node is JsonObject obj)
            {
                action(obj);
                foreach (var value in obj.Select(pair => pair.Value).ToList()) Walk(value, action);
            }
            else if (node is JsonArray array)
                foreach (var value in array) Walk(value, action);
        }

        void FindWrite(JsonObject write)
        {
            var kind = Str(write["k"]);
            if (kind is not ("setField" or "new")
                || TypeJson.Read(kind == "new" ? write["type"] : write["ownerType"]) is not TypeNode.Fqn owner
                || !slots.TryGetValue(owner.Name, out var slot)
                || kind == "setField" && Str(write["name"]) != slot.Field
                || !definitions.TryGetValue(owner.Name, out var declarations)
                || declarations[0]["fields"] is not JsonArray fields) return;
            var field = fields.OfType<JsonObject>().Single(field => Str(field["name"]) == slot.Field);
            var declared = TypeJson.Read(field["type"]);
            var value = kind == "new"
                ? ((JsonArray)write["args"])[slot.ConstructorParameter] : write["value"];
            if (Core(declared) is not TypeNode.Fqn { Args: { Length: > 0 } } logical
                || Core(NodeType.Of(value)) is not TypeNode.Fqn physical
                || !(carriers.Contains(physical.Name) || refs.IsExistentialPhysicalOwner(physical.Name))) return;
            var carrier = localCarriers.GetValueOrDefault(logical.Name);
            if (carrier == null) refs.TryExistentialPhysicalOwner(logical.Name, out carrier);
            if (carrier != null && SupertypeGraph.Reaches(physical, new TypeNode.Fqn(carrier), types, refs))
                projected[owner.Name] = declared switch
                {
                    TypeNode.Nullable => new TypeNode.Nullable(new TypeNode.Fqn(carrier)),
                    TypeNode.Oblivious => new TypeNode.Oblivious(new TypeNode.Fqn(carrier)),
                    _ => new TypeNode.Fqn(carrier),
                };
        }
        foreach (var root in roots) Walk(root, FindWrite);

        foreach (var (owner, physical) in projected)
        {
            var slot = slots[owner];
            foreach (var definition in definitions[owner])
            {
                var field = ((JsonArray)definition["fields"]).OfType<JsonObject>()
                    .Single(field => Str(field["name"]) == slot.Field);
                field["type"] = TypeJson.Write(physical);
                var constructor = ((JsonArray)definition["ctors"]).OfType<JsonObject>().Single();
                var parameter = (JsonObject)((JsonArray)constructor["params"])[slot.ConstructorParameter];
                parameter["type"] = TypeJson.Write(physical);
            }
        }

        void AlignUse(JsonObject node)
        {
            var kind = Str(node["k"]);
            var owner = TypeJson.Read(kind == "new" ? node["type"] : node["ownerType"]) as TypeNode.Fqn;
            if (owner == null || !projected.TryGetValue(owner.Name, out var physical)) return;
            var slot = slots[owner.Name];
            if (kind is "field" or "setField" && Str(node["name"]) == slot.Field)
            {
                if (kind == "field") node["sty"] = TypeJson.Write(physical);
                if (node["memberType"] != null) node["memberType"] = TypeJson.Write(physical);
            }
            if (kind == "new")
                foreach (var key in new[] { "sig", "argTypes", "memberSignature" })
                    if (node[key] is JsonArray signature)
                        signature[slot.ConstructorParameter] = TypeJson.Write(physical);
        }
        foreach (var root in roots) Walk(root, AlignUse);
    }

    static TypeNode Core(TypeNode type) => type switch
    {
        TypeNode.Nullable nullable => Core(nullable.Of),
        TypeNode.Oblivious oblivious => Core(oblivious.Of),
        _ => type,
    };

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
