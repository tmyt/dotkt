using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class ValueRepresentationLowering
{
    internal sealed record SourceParameters(string Owner, OptionalField<string[]> Variances);
    internal sealed record SourceMethod(string Owner, string Id, int Occurrence, bool ValueMember, string Adapter, string Slot);

    // Only scalar source facts cross this boundary. In particular, no mutable
    // source declaration stands in for the continuing physical MethodDef.
    internal sealed class SourceIndex
    {
        internal SourceIndex(SourceParameters[] parameters, SourceMethod[] methods)
        {
            Parameters = parameters;
            Methods = methods;
        }
        internal IReadOnlyList<SourceParameters> Parameters { get; }
        internal IReadOnlyList<SourceMethod> Methods { get; }
    }

    internal static SourceIndex CaptureSource(IReadOnlyList<BirFileDeclaration> roots,
        IReadOnlySet<string> indexedDeclarationIds = null)
    {
        var parameters = new List<SourceParameters>();
        var methods = new List<SourceMethod>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        static string Argument(BirAttribute attribute, int index)
        {
            var args = attribute.Args.OrDefault();
            if (args == null) return null;
            return args[index] switch {
                BirConstant { Value.HasValue: true, Value.Value: BirLiteral.TextLiteral text } => text.Value,
                BirAttributeLiteral { Value.HasValue: true, Value.Value: BirLiteral.TextLiteral text } => text.Value,
                _ => null,
            };
        }
        void Methods(string owner, IEnumerable<BirFunctionDeclaration> declarations)
        {
            foreach (var method in declarations)
            {
                if (!method.DeclarationId.HasValue || indexedDeclarationIds != null
                    && !indexedDeclarationIds.Contains(method.DeclarationId.Value)) continue;
                var id = method.DeclarationId.Value;
                var occurrence = occurrences.GetValueOrDefault(id);
                occurrences[id] = occurrence + 1;
                var marker = method.Attrs.OrEmpty().SingleOrDefault(attribute =>
                    attribute.Attr.OrDefault() is TypeNode.Fqn { Name: MemberAttribute });
                methods.Add(new SourceMethod(owner, id, occurrence, marker != null,
                    marker == null ? null : Argument(marker, 0), marker == null ? null : Argument(marker, 1)));
            }
        }
        foreach (var root in roots)
        {
            Methods(root.FileClass.OrDefault(), root.Methods.OrEmpty());
            foreach (var declaration in root.Types.OrEmpty())
            {
                var (name, typeParameters, declarations) = declaration switch {
                    BirClassDefinition value => (value.Name, value.TypeParams, value.Methods),
                    BirInterfaceDefinition value => (value.Name, value.TypeParams, value.Methods),
                    BirEnumDefinition value => (value.Name, value.TypeParams, value.Methods),
                    _ => throw new InvalidOperationException("Unaccounted value representation source owner: " + declaration.GetType().Name),
                };
                if (name.HasValue)
                    parameters.Add(new SourceParameters(name.Value, typeParameters.HasValue
                        ? OptionalField<string[]>.Of(typeParameters.Value.Select(parameter => parameter.Variance.OrDefault()).ToArray())
                        : typeParameters.IsNull ? OptionalField<string[]>.Null : default));
                Methods(name.OrDefault(), declarations.OrEmpty());
            }
        }
        return new SourceIndex(parameters.ToArray(), methods.ToArray());
    }

    void BindSource(IReadOnlyList<JsonNode> roots, SourceIndex source)
    {
        ArgumentNullException.ThrowIfNull(source);
        foreach (var shape in source.Parameters)
            // An absent/null later declaration did not replace an earlier
            // declared vector in the original source index; empty did.
            if (shape.Variances.HasValue) localParameters[shape.Owner] = shape.Variances.Value;
        var identities = source.Methods.Select(method => method.Id).ToHashSet(StringComparer.Ordinal);
        var actual = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        void Index(JsonObject declaration)
        {
            foreach (var method in (declaration["methods"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (Text(method[DeclarationIdentityBinding.Key]) is string id && identities.Contains(id))
                {
                    if (!actual.TryGetValue(id, out var occurrences)) actual.Add(id, occurrences = new List<JsonObject>());
                    occurrences.Add(method);
                }
            foreach (var type in (declaration["types"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()) Index(type);
        }
        foreach (var root in roots.OfType<JsonObject>()) Index(root);
        // Capture -> continuing DOM association preserves declaration order.
        // Occurrences only retain the old dictionary overwrite behavior for an
        // exact repeated identity; they do not infer an origin for a new method.
        foreach (var expected in source.Methods.GroupBy(method => method.Id, StringComparer.Ordinal))
            if (!actual.TryGetValue(expected.Key, out var occurrences) || occurrences.Count != expected.Count())
                throw new InvalidOperationException("Value representation source declaration occurrences changed before physical association: " + expected.Key);
        foreach (var method in source.Methods)
        {
            var declaration = actual[method.Id][method.Occurrence];
            locals[method.Id] = (method.Owner, declaration);
            if (!method.ValueMember) continue;
            adapters[method.Id] = method.Adapter;
            members[method.Id] = (method.Owner, declaration, method.Slot);
        }
    }
}
