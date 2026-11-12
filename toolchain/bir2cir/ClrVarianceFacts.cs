using System.Text.Json.Nodes;
using DotKt.Bir;

// A variance query needs declaration signatures, not executable bodies or an owning IR graph.
// Keep its snapshot separate from the mutable declarations whose physical flags will be changed.
static class ClrVarianceFacts
{
    internal readonly record struct Occurrence(TypeNode Type, int Context);
    internal sealed record Declaration(string Kind, int CapturedCount,
        IReadOnlyList<string> Parameters, IReadOnlyList<Occurrence> Occurrences);

    // Temporary neighbour of the remaining DOM owners. This projection reads only the signature
    // facts used below; it neither imports bodies nor serializes and reparses declarations.
    internal static Declaration FromDom(JsonObject definition)
    {
        var kind = Text(definition["kind"]);
        var capturedCount = (definition["capturedTypeParams"] as JsonArray)?.Count ?? 0;
        var variances = (definition["typeParams"] as JsonArray)?.Select(parameter =>
            parameter is JsonObject obj ? Text(obj["variance"]) : null).ToArray() ?? [];
        if (kind is not ("interface" or "delegate")) return new(kind, capturedCount, variances, []);

        var occurrences = new List<Occurrence>();
        void Add(JsonNode slot, int context)
        {
            if (TypeJson.Read(slot) is TypeNode type) occurrences.Add(new(type, context));
        }
        Add(definition["base"], 1);
        if (definition["interfaces"] is JsonArray interfaces)
            foreach (var type in interfaces) Add(type, 1);

        var methods = (definition["methods"] as JsonArray)?.OfType<JsonObject>()
            ?? Enumerable.Empty<JsonObject>();
        if (kind == "delegate") methods = methods.Prepend(definition);
        foreach (var method in methods)
        {
            if (method["static"]?.GetValue<bool>() == true
                && method["virtual"]?.GetValue<bool>() != true
                && method["abstract"]?.GetValue<bool>() != true) continue;
            if (method["params"] is JsonArray parameters)
                foreach (var parameter in parameters.OfType<JsonObject>()) Add(parameter["type"], -1);
            Add(method["ret"], 1);
            // ECMA-335 II.9.7: method constraints are inputs. The containing type's
            // own generic constraints impose no variance restriction.
            if (method["typeParams"] is JsonArray genericParameters)
                foreach (var parameter in genericParameters.OfType<JsonObject>())
                    if (parameter["constraints"] is JsonArray constraints)
                        foreach (var constraint in constraints) Add(constraint, -1);
        }

        return new(kind, capturedCount, variances, occurrences.ToArray());
    }

    internal static IReadOnlyDictionary<string, Declaration> FromDom(
        IReadOnlyDictionary<string, JsonObject> definitions) => definitions.ToDictionary(
            pair => pair.Key, pair => FromDom(pair.Value), StringComparer.Ordinal);

    static string Text(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;
}
