using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Cir;

// A variance query needs declaration signatures, not executable bodies or an owning IR graph.
// Keep its snapshot separate from the mutable declarations whose physical flags will be changed.
static class ClrVarianceFacts
{
    internal readonly record struct Occurrence(TypeNode Type, int Context);
    internal sealed record Declaration(string Kind, int CapturedCount,
        IReadOnlyList<string> Parameters, IReadOnlyList<Occurrence> Occurrences);

    internal static Declaration FromCir(CirTypeDeclaration definition)
    {
        var kind = definition.Kind.ToString().ToLowerInvariant();
        var captured = definition.CapturedTypeParams.OrEmpty().Length;
        var variances = definition.TypeParams.OrEmpty().Select(parameter => parameter.Variance.OrDefault()).ToArray();
        if (definition.Kind is not (CirTypeKind.Interface or CirTypeKind.Delegate))
            return new(kind, captured, variances, []);
        var occurrences = new List<Occurrence>();
        void Add(TypeNode type, int context) { if (type != null) occurrences.Add(new(type, context)); }
        Add(definition.Base.OrDefault(), 1);
        foreach (var type in definition.Interfaces.OrEmpty()) Add(type, 1);
        void Signature(IEnumerable<CirParameter> parameters, TypeNode result, IEnumerable<CirTypeParameter> generic)
        {
            foreach (var parameter in parameters) Add(parameter.Type, -1);
            Add(result, 1);
            foreach (var parameter in generic)
                foreach (var constraint in parameter.Constraints.OrEmpty()) Add(constraint, -1);
        }
        if (definition.Kind == CirTypeKind.Delegate)
            Signature(definition.Params.OrEmpty(), definition.Ret.OrDefault(), definition.TypeParams.OrEmpty());
        foreach (var method in definition.Methods.OrEmpty())
            if (!method.Static || method.Virtual || method.Abstract.OrDefault())
                Signature(method.Params, method.Ret, method.TypeParams.OrEmpty());
        return new(kind, captured, variances, occurrences.ToArray());
    }

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

    static string Text(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;
}
