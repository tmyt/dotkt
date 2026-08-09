using System.Collections.Generic;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A field declaration's required IsVolatile modifier is a CLR representation fact. Carry the resulting access prefix
// explicitly on every CIR access so ilemit emits it one-to-one. This must include local generic fields: resolving a
// field on a constructed TypeBuilder owner returns an anchored FieldInfo whose identity is not its declaration builder.
static class VolatileFieldLowering
{
    static readonly HashSet<string> FieldAccessKinds = new()
    {
        "field", "setField", "setFieldExpr",
        "staticField", "staticFieldSet", "setStaticField", "setStaticFieldExpr",
        "lateinitGet", "clrPropGet", "clrPropSet",
    };

    public static void ApplyAll(IReadOnlyList<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var localFields = new HashSet<(string Owner, string Name)>();
        var localVolatileFields = new HashSet<(string Owner, string Name)>();
        foreach (var root in roots.OfType<JsonObject>())
            CollectDeclarations(root, Str(root["fileClass"]), localFields, localVolatileFields);
        foreach (var root in roots) Walk(root, localFields, localVolatileFields, refs);
        foreach (var root in roots) PropagateByRefLocals(root);
    }

    static void CollectDeclarations(JsonObject container, string owner,
        HashSet<(string Owner, string Name)> localFields,
        HashSet<(string Owner, string Name)> localVolatileFields)
    {
        if (owner != null && container["fields"] is JsonArray fields)
            foreach (var field in fields.OfType<JsonObject>())
                if (Str(field["name"]) is string name)
                {
                    localFields.Add((owner, name));
                    if (Bool(field["volatile"])) localVolatileFields.Add((owner, name));
                }

        if (container["types"] is JsonArray types)
            foreach (var type in types.OfType<JsonObject>())
                CollectDeclarations(type, Str(type["name"]), localFields, localVolatileFields);
    }

    static void Walk(JsonNode node,
        IReadOnlySet<(string Owner, string Name)> localFields,
        IReadOnlySet<(string Owner, string Name)> localVolatileFields,
        ReferenceMetadataIndex refs)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var child in obj.ToArray())
                    if (child.Value != null) Walk(child.Value, localFields, localVolatileFields, refs);
                if (Str(obj["k"]) is string kind && FieldAccessKinds.Contains(kind)
                    && TypeJson.Read(kind is "clrPropGet" or "clrPropSet" ? obj["type"] : obj["ownerType"])
                        is TypeNode.Fqn owner
                    && Str(obj["name"]) is string name
                    && (localVolatileFields.Contains((owner.Name, name)) ||
                        !localFields.Contains((owner.Name, name)) && refs.TryResolveVolatileField(owner, name)))
                    obj["volatile"] = true;
                break;
            case JsonArray array:
                foreach (var child in array.ToArray())
                    if (child != null) Walk(child, localFields, localVolatileFields, refs);
                break;
        }
    }

    // `var x by byref(volatileProperty)` stores the field's managed pointer in a local and performs later reads/writes
    // as byrefLoad/byrefStore. The volatile fact belongs to those ldobj/stobj operations, not to ldflda itself, so
    // carry it from the address initializer to every access through that pointer within the declaration body.
    static void PropagateByRefLocals(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["params"] is JsonArray && obj["body"] is JsonArray body)
                {
                    var volatilePointers = new HashSet<string>(System.StringComparer.Ordinal);
                    CollectVolatilePointers(body, volatilePointers);
                    if (volatilePointers.Count > 0) MarkPointerAccesses(body, volatilePointers);
                }
                foreach (var child in obj.ToArray())
                    if (child.Value != null) PropagateByRefLocals(child.Value);
                break;
            case JsonArray array:
                foreach (var child in array.ToArray())
                    if (child != null) PropagateByRefLocals(child);
                break;
        }
    }

    static void CollectVolatilePointers(JsonNode node, ISet<string> into)
    {
        switch (node)
        {
            case JsonObject obj:
                if (Str(obj["k"]) == "var" && Str(obj["name"]) is string name &&
                    obj["init"] is JsonObject init && Str(init["k"]) == "byrefOf" &&
                    init["inner"] is JsonObject inner && Bool(inner["volatile"]))
                    into.Add(name);
                foreach (var child in obj.ToArray())
                    if (child.Value != null) CollectVolatilePointers(child.Value, into);
                break;
            case JsonArray array:
                foreach (var child in array.ToArray())
                    if (child != null) CollectVolatilePointers(child, into);
                break;
        }
    }

    static void MarkPointerAccesses(JsonNode node, IReadOnlySet<string> volatilePointers)
    {
        switch (node)
        {
            case JsonObject obj:
                if (Str(obj["k"]) is ("byrefLoad" or "byrefStore") &&
                    Str(obj["local"]) is string local && volatilePointers.Contains(local))
                    obj["volatile"] = true;
                foreach (var child in obj.ToArray())
                    if (child.Value != null) MarkPointerAccesses(child.Value, volatilePointers);
                break;
            case JsonArray array:
                foreach (var child in array.ToArray())
                    if (child != null) MarkPointerAccesses(child, volatilePointers);
                break;
        }
    }

    static bool Bool(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var result) && result;

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}
