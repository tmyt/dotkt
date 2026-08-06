using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

// A BIR subtree clone must rename lexical declaration ids and every edge to them as one operation. Ownership remains
// encoded by tree nesting; the id is only the reference relation for recursive/forward/cross-expression uses.
static class LexicalDeclarationIds
{
    static int _cloneCounter;

    public static void Freshen(params JsonNode[] roots)
    {
        var cloneId = System.Threading.Interlocked.Increment(ref _cloneCounter);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        void Collect(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (Str(obj["k"]) == "localFun" && Str(obj["id"]) is string id)
                {
                    if (!map.TryAdd(id, id + "$clone" + cloneId))
                        throw new InvalidOperationException(
                            $"duplicate local function declaration id '{id}' in one cloned BIR payload");
                }
                foreach (var child in obj.Select(pair => pair.Value).Where(value => value != null)) Collect(child);
            }
            else if (node is JsonArray array)
                foreach (var child in array.Where(value => value != null)) Collect(child);
        }

        void Apply(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                var kind = Str(obj["k"]);
                if (kind is "localFun" or "callLocal" or "localFunRef"
                    && Str(obj["id"]) is string id && map.TryGetValue(id, out var fresh))
                    obj["id"] = fresh;
                foreach (var child in obj.Select(pair => pair.Value).Where(value => value != null)) Apply(child);
            }
            else if (node is JsonArray array)
                foreach (var child in array.Where(value => value != null)) Apply(child);
        }

        foreach (var root in roots) if (root != null) Collect(root);
        if (map.Count == 0) return;
        foreach (var root in roots) if (root != null) Apply(root);
    }

    static string Str(JsonNode node) => (node as JsonValue)?.GetValue<string>();
}
