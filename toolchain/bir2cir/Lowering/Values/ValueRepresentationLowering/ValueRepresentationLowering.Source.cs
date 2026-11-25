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
    bool sourceCompleted;
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
        if (sourceCompleted) throw new InvalidOperationException("Source value declaration retired after source completion");
        // Expansion may create helpers outside the original source catalog. Only actual tracked occurrences retire.
        foreach (var method in sourceMethods)
            if (method.Method.IsSource(actual)) method.Method.Retire();
    }

    internal void CompleteSourceContinuation(IReadOnlyList<BirFileDeclaration> roots,
        AliasDeclarationTransitions aliases, SourceDeclarationProjection declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        if (sourceCompleted) throw new InvalidOperationException("Value representation declarations already completed their source continuation");
        foreach (var root in roots) declarations.RequireOccurrence(root);
        aliases?.ValidateSourceContinuations(roots);
        var identities = sourceMethods.Select(method => method.Fact.Id).ToHashSet(StringComparer.Ordinal);
        var actual = new Dictionary<string, List<BirFunctionDeclaration>>(StringComparer.Ordinal);
        void Methods(IEnumerable<BirFunctionDeclaration> methods)
        {
            foreach (var method in methods)
                if (method.DeclarationId.HasValue && identities.Contains(method.DeclarationId.Value)
                    && !declarations.IsPublished(method))
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
        // Counts retain original membership, not current traversal order.
        // Continuing repeated-ID rows are paired by the SAME actual occurrence.
        foreach (var expected in sourceMethods.GroupBy(method => method.Fact.Id, StringComparer.Ordinal))
            if ((actual.GetValueOrDefault(expected.Key)?.Count ?? 0) != expected.Count(method => !method.Method.Retired
                && !declarations.WasIdentityConsumed(method.Method.SourceOccurrence, method.Fact.Id)))
                throw new InvalidOperationException("Value representation source declaration occurrences changed before physical association: " + expected.Key);
        foreach (var method in sourceMethods)
        {
            var source = method.Method.SourceOccurrence;
            declarations.RequireOccurrence(source);
            if (method.Method.Retired)
            {
                if (!declarations.IsRetired(source))
                    throw new InvalidOperationException("Value declaration lacks its explicit source retirement");
                continue;
            }
            if (declarations.IsRetired(source))
                throw new InvalidOperationException("Live value declaration is inside a reported retired forest");
            var live = actual.GetValueOrDefault(method.Fact.Id) ?? new List<BirFunctionDeclaration>();
            var consumed = declarations.WasIdentityConsumed(source, method.Fact.Id);
            if (consumed ? source.DeclarationId.IsPresent || live.Any(row => ReferenceEquals(row, source))
                : source.DeclarationId.OrDefault() != method.Fact.Id || !live.Any(row => ReferenceEquals(row, source)))
                throw new InvalidOperationException("Value declaration is not its exact continuing occurrence");
        }
        sourceMethods.Clear();
        sourceCompleted = true;
    }

    // Local current/retired methods stay the SAME owning records. References
    // remain selected metadata facts; neither needs a rendered declaration.
    sealed class LiveMethod
    {
        readonly BirFunctionDeclaration source;
        readonly MemberBinding reference;
        internal LiveMethod(BirFunctionDeclaration source) => this.source = source;
        readonly NativeSignatureReader.Signature metadataSignature;
        internal LiveMethod(MemberBinding reference, bool metadataBuild)
        {
            this.reference = reference;
            if (metadataBuild) metadataSignature = reference.MetadataValueSignature;
        }
        internal bool HasSource => source != null;
        internal BirFunctionDeclaration SourceOccurrence => source
            ?? throw new InvalidOperationException("Referenced value declaration has no local owning occurrence");
        internal bool Retired { get; private set; }
        internal bool IsSource(BirFunctionDeclaration declaration) => ReferenceEquals(source, declaration);
        internal void Retire() => Retired = true;
        internal string Name => source != null ? source.Name.OrDefault()
            : reference.SourcePropertyName ?? reference.DeclarationSourceName ?? reference.Name;
        internal string Id => source != null ? source.DeclarationId.OrDefault() : reference.DeclarationId;
        internal bool Abstract => source != null ? source.Abstract.OrDefault() : reference.IsAbstract;
        internal bool HasParameters => source != null ? source.Params.HasValue : true;
        internal TypeNode[] Parameters => source != null ? source.Params.OrEmpty().Select(parameter => parameter.Type.OrDefault()).ToArray()
            : metadataSignature?.Parameters ?? reference.DeclarationSemanticParams ?? reference.ParamTypeNodes;
        internal TypeNode Return => source != null ? source.Ret.OrDefault()
            : metadataSignature?.Return ?? reference.DeclarationSemanticReturn ?? reference.ReturnTypeNode;
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
