using System.Text.Json.Nodes;
using DotKt.Bir;

// A lateinit slot must distinguish an uninitialized T from every initialized value, including default(T).
// Ordinary declarations therefore use private object storage and typed Kotlin accessor signatures. Queries
// address the selected declaration through a private helper so existential receivers never invoke a checked read.
static class LateinitInitializationLowering
{
    const string Identity = "lateinitDeclarationId";
    sealed record Storage(string Owner, JsonObject Declaration, JsonObject Field, JsonNode ValueType)
    {
        public string QueryMethod;
        public bool Static => Field["static"]?.GetValue<bool>() == true;
    }

    public static void ApplyAll(IReadOnlyList<JsonNode> roots)
    {
        var identities = new Dictionary<string, Storage>(StringComparer.Ordinal);
        var fields = new Dictionary<(string Owner, string Name, bool Static), Storage>();
        foreach (var root in roots) Collect(root, identities, fields);
        foreach (var root in roots) Rewrite(root, identities, fields);
        foreach (var storage in identities.Values)
        {
            storage.Field.Remove(Identity);
        }
    }

    static void Collect(JsonNode node, Dictionary<string, Storage> identities,
        Dictionary<(string Owner, string Name, bool Static), Storage> fields)
    {
        if (node is not JsonObject declaration) return;
        var owner = Str(declaration["name"]) ?? Str(declaration["fileClass"]);
        if (owner != null && declaration["fields"] is JsonArray declaredFields)
            foreach (var field in declaredFields.OfType<JsonObject>())
            {
                if (Str(field[Identity]) is not string id) continue;
                var storage = new Storage(owner, declaration, field, field["type"]!.DeepClone());
                identities.Add(id, storage);
                fields.Add((owner, Str(field["name"]), storage.Static), storage);
                if (storage.Static && Str(declaration["staticCarrier"]?["owner"]) is string sourceOwner)
                    fields.Add((sourceOwner, Str(field["name"]), true), storage);
                field["type"] = TypeJson.Fqn("object");
                field["vis"] = "private";
            }
        if (declaration["types"] is JsonArray types)
            foreach (var type in types) Collect(type, identities, fields);
    }

    static void Rewrite(JsonNode node, IReadOnlyDictionary<string, Storage> identities,
        IReadOnlyDictionary<(string Owner, string Name, bool Static), Storage> fields)
    {
        if (node is JsonObject obj)
        {
            foreach (var child in obj.ToArray()) if (child.Value != null) Rewrite(child.Value, identities, fields);
            if (obj["params"] is JsonArray && obj["body"] is JsonArray
                && Str(obj["declarationId"]) is string declarationId && identities.ContainsKey(declarationId))
                obj["lateinit"] = true;
            var kind = Str(obj["k"]);
            if (kind == "lateinitIsInitialized")
            {
                var storage = identities[Str(obj[Identity])];
                var method = EnsureQueryMethod(storage);
                var sourceOwner = TypeJson.Read(obj["ownerType"]) as TypeNode.Fqn;
                var receiver = obj["recv"]?.DeepClone();
                var style = obj["sty"]?.DeepClone();
                obj.Clear();
                if (style != null) obj["sty"] = style;
                obj["k"] = storage.Static ? "callStatic" : "callInstance";
                obj["ownerType"] = TypeJson.Write(new TypeNode.Fqn(storage.Owner,
                    storage.Static ? null : sourceOwner?.Args));
                obj["method"] = method;
                obj["args"] = new JsonArray();
                obj["sig"] = new JsonArray();
                obj["ret"] = TypeJson.Fqn("kotlin.Boolean");
                if (!storage.Static) { obj["recv"] = receiver; obj["virtual"] = false; }
                return;
            }
            if (kind is not ("lateinitGet" or "setField" or "setFieldExpr" or "staticFieldSet")) return;
            if (TypeJson.OwnerName(obj["ownerType"]) is not string owner || Str(obj["name"]) is not string name
                || !fields.TryGetValue((owner, name, kind == "staticFieldSet"
                    || obj["static"]?.GetValue<bool>() == true), out var slot)) return;
            if (kind != "lateinitGet")
            {
                obj["value"] = new JsonObject {
                    ["k"] = "cast", ["type"] = TypeJson.Fqn("object"), ["e"] = obj["value"]?.DeepClone(),
                };
                return;
            }
            var read = RawRead(slot, obj["ownerType"]!.DeepClone(), obj["recv"]?.DeepClone());
            var checkedRead = new JsonObject {
                ["k"] = "lateinitGet", ["name"] = name, ["value"] = read,
                ["lateinitSourceName"] = Str(obj["lateinitSourceName"]) ?? name,
            };
            var logicalType = obj["sty"]?.DeepClone() ?? obj["ret"]?.DeepClone() ?? slot.ValueType.DeepClone();
            obj.Clear();
            obj["k"] = "cast";
            obj["type"] = logicalType;
            obj["e"] = checkedRead;
        }
        else if (node is JsonArray array)
            foreach (var child in array.ToArray()) if (child != null) Rewrite(child, identities, fields);
    }

    static JsonObject RawRead(Storage slot, JsonNode owner, JsonNode receiver)
    {
        var read = new JsonObject {
            ["k"] = slot.Static ? "staticField" : "field", ["ownerType"] = owner,
            ["name"] = slot.Field["name"]!.DeepClone(),
        };
        if (!slot.Static) read["recv"] = receiver;
        return read;
    }

    static string EnsureQueryMethod(Storage slot)
    {
        if (slot.QueryMethod != null) return slot.QueryMethod;
        if (slot.Declaration["methods"] is not JsonArray methods) slot.Declaration["methods"] = methods = new JsonArray();
        var names = methods.OfType<JsonObject>().Select(method => Str(method["name"])).ToHashSet(StringComparer.Ordinal);
        var ordinal = 0;
        while (!names.Add(slot.QueryMethod = "dotkt<lateinitInitialized>" + ordinal++)) { }
        var arguments = (slot.Declaration["typeParams"] as JsonArray ?? new JsonArray())
            .Select((_, index) => (TypeNode)new TypeNode.Tv("type", index)).ToArray();
        var read = RawRead(slot, TypeJson.Write(new TypeNode.Fqn(slot.Owner, arguments.Length == 0 ? null : arguments)),
            new JsonObject { ["k"] = "this" });
        methods.Add(new JsonObject {
            ["name"] = slot.QueryMethod, ["static"] = slot.Static, ["vis"] = "private", ["generated"] = true,
            ["override"] = false, ["virtual"] = false, ["abstract"] = false, ["objectOverride"] = false,
            ["params"] = new JsonArray(), ["ret"] = TypeJson.Fqn("kotlin.Boolean"),
            ["body"] = new JsonArray(new JsonObject { ["k"] = "return", ["value"] = new JsonObject {
                ["k"] = "binOp", ["op"] = "!=",
                ["lhs"] = new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Fqn("object"), ["e"] = read },
                ["rhs"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("object"), ["value"] = null },
            }}),
        });
        return slot.QueryMethod;
    }

    static string Str(JsonNode node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
