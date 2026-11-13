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

    readonly List<(SourceMethod Fact, LiveMethod Method)> sourceMethods = new();
    bool domBound;
    GenericRepresentationPolicy representations;

    internal static ValueRepresentationLowering CreateSource(IReadOnlyList<BirFileDeclaration> roots,
        ReferenceMetadataIndex refs, bool referenceBuild, ValueRepresentationSourcePolicy policy, SourceIndex source,
        GenericRepresentationPolicy representations)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new ValueRepresentationLowering(refs, referenceBuild, policy);
        result.representations = representations;
        foreach (var shape in source.Parameters)
            // An absent/null later declaration did not replace an earlier
            // declared vector in the original source index; empty did.
            if (shape.Variances.HasValue) result.localParameters[shape.Owner] = shape.Variances.Value;
        var identities = source.Methods.Select(method => method.Id).ToHashSet(StringComparer.Ordinal);
        var actual = new Dictionary<string, List<BirFunctionDeclaration>>(StringComparer.Ordinal);
        void Methods(IEnumerable<BirFunctionDeclaration> methods)
        {
            foreach (var method in methods)
                if (method.DeclarationId.HasValue && identities.Contains(method.DeclarationId.Value))
                {
                    if (!actual.TryGetValue(method.DeclarationId.Value, out var occurrences))
                        actual.Add(method.DeclarationId.Value, occurrences = new());
                    occurrences.Add(method);
                }
        }
        foreach (var root in roots)
        {
            Methods(root.Methods.OrEmpty());
            foreach (var type in root.Types.OrEmpty()) Methods(type.Methods.OrEmpty());
        }
        foreach (var expected in source.Methods.GroupBy(method => method.Id, StringComparer.Ordinal))
            if (!actual.TryGetValue(expected.Key, out var occurrences) || occurrences.Count != expected.Count())
                throw new InvalidOperationException("Value representation source declaration occurrences changed before source association: " + expected.Key);
        foreach (var method in source.Methods)
        {
            var view = new LiveMethod(actual[method.Id][method.Occurrence]);
            result.sourceMethods.Add((method, view));
            result.locals[method.Id] = (method.Owner, view);
            if (!method.ValueMember) continue;
            result.adapters[method.Id] = method.Adapter;
            result.members[method.Id] = (method.Owner, view, method.Slot);
        }
        return result;
    }

    internal void RetireSourceDeclaration(BirFunctionDeclaration actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        if (domBound) throw new InvalidOperationException("Source value declaration retired after DOM handoff");
        // Expansion may create helpers outside the original source catalog. Only actual tracked occurrences retire.
        foreach (var method in sourceMethods)
            if (method.Method.IsSource(actual)) method.Method.Retire();
    }

    internal void BindDomDeclarations(IReadOnlyList<JsonNode> roots,
        IReadOnlyDictionary<BirFunctionDeclaration, JsonObject> retiredDeclarations = null)
    {
        if (domBound) throw new InvalidOperationException("Value representation declarations already handed to DOM");
        var identities = sourceMethods.Select(method => method.Fact.Id).ToHashSet(StringComparer.Ordinal);
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
        foreach (var expected in sourceMethods.GroupBy(method => method.Fact.Id, StringComparer.Ordinal))
            if ((actual.GetValueOrDefault(expected.Key)?.Count ?? 0) != expected.Count(method => !method.Method.Retired))
                throw new InvalidOperationException("Value representation source declaration occurrences changed before physical association: " + expected.Key);
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var method in sourceMethods)
        {
            if (method.Method.Retired)
            {
                // This is the same one-way declaration frontier, including explicitly detached declarations.
                method.Method.BindRetired(retiredDeclarations);
                continue;
            }
            var position = positions.GetValueOrDefault(method.Fact.Id);
            method.Method.Bind(actual[method.Fact.Id][position]);
            positions[method.Fact.Id] = position + 1;
        }
        sourceMethods.Clear();
        domBound = true;
    }

    // A source occurrence and its continuing physical declaration are one authority, never two snapshots.
    sealed class LiveMethod
    {
        BirFunctionDeclaration source;
        JsonObject dom;
        readonly MemberBinding reference;
        internal LiveMethod(BirFunctionDeclaration source) => this.source = source;
        internal LiveMethod(MemberBinding reference) => this.reference = reference;
        internal bool HasSource => source != null;
        internal bool Retired { get; private set; }
        internal bool IsSource(BirFunctionDeclaration declaration) => ReferenceEquals(source, declaration);
        internal void Retire() => Retired = true;
        internal void BindRetired(IReadOnlyDictionary<BirFunctionDeclaration, JsonObject> declarations)
        {
            if (!Retired || source == null || declarations == null
                || !declarations.TryGetValue(source, out var declaration) || declaration == null)
                throw new InvalidOperationException("Value declaration lacks its explicit source retirement");
            Bind(declaration);
        }
        internal void Bind(JsonObject declaration) { dom = declaration; source = null; }
        internal string Name => source != null ? source.Name.OrDefault() : reference != null
            ? reference.SourcePropertyName ?? reference.DeclarationSourceName ?? reference.Name : Text(dom["name"]);
        internal string Id => source != null ? source.DeclarationId.OrDefault() : reference?.DeclarationId ?? Text(dom[DeclarationIdentityBinding.Key]);
        internal bool Abstract => source != null ? source.Abstract.OrDefault() : reference?.IsAbstract ?? Bool(dom["abstract"]);
        internal bool HasParameters => source != null ? source.Params.HasValue : reference != null || dom["params"] is JsonArray;
        internal TypeNode[] Parameters => source != null ? source.Params.OrEmpty().Select(parameter => parameter.Type.OrDefault()).ToArray()
            : reference != null ? reference.DeclarationSemanticParams ?? reference.ParamTypeNodes
            : (dom["params"] as JsonArray ?? new()).OfType<JsonObject>().Select(parameter => TypeJson.Read(parameter["type"])).ToArray();
        internal TypeNode Return => source != null ? source.Ret.OrDefault() : reference != null
            ? reference.DeclarationSemanticReturn ?? reference.ReturnTypeNode : TypeJson.Read(dom["ret"]);
        internal JsonObject Dom => dom ??= reference != null ? new JsonObject {
            ["name"] = Name, [KotlinPropertyAccessors.KindKey] = reference.AccessorKind,
            [DeclarationIdentityBinding.Key] = Id, ["abstract"] = Abstract,
            ["params"] = new JsonArray(Parameters.Select((type, index) => (JsonNode)new JsonObject {
                ["name"] = "p" + index, ["type"] = TypeJson.Write(type) }).ToArray()),
            ["ret"] = TypeJson.Write(Return),
        } : throw new InvalidOperationException("Source value declaration used before its DOM handoff");
    }

    string HelperIdentity(string owner, string name, IReadOnlyList<TypeNode> parameters)
    {
        var local = locals.Where(entry => entry.Value.Owner == owner && entry.Value.Method.Name == name)
            .Where(entry => entry.Value.Method.HasParameters && entry.Value.Method.Parameters.Length == parameters.Count
                && entry.Value.Method.Parameters.Select((type, index) =>
                    ReferenceMetadataIndex.SourceDeclarationDescribesCall(type, parameters[index])).All(match => match))
            .Select(entry => entry.Key).ToArray();
        return local.Length == 1 ? local[0] : refs.AuthoredKotlinHelper(owner, name, 0, parameters.ToArray()).DeclarationId;
    }
}
