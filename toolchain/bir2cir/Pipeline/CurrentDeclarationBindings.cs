using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// The captured input inventory keeps its SAME source anchors until terminal
// custody completes. This is not another eligibility or physical-member index.
sealed class CurrentDeclarationBindings
{
    internal sealed class DeclarationBinding
    {
        BirGraphValue source;
        internal DeclarationBinding(BirGraphValue source) => this.source = source;
        internal BirGraphValue Source => source
            ?? throw new InvalidOperationException("Declaration already crossed its owning frontier");
        internal void ReleaseSource() => source = null;
    }

    readonly Dictionary<BirGraphValue, DeclarationBinding> source = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<BirFunctionDeclaration, IBirTypeDeclaration> retiredMethods = new(ReferenceEqualityComparer.Instance);
    readonly List<(BirGraphValue Owner, BirPropertyDeclaration Property)> retiredProperties = new();
    readonly HashSet<BirGraphValue> retiredForests = new(ReferenceEqualityComparer.Instance);
    readonly BirFileDeclaration[] sourceFiles;
    bool bound;
    internal bool IsBound => bound;

    internal IReadOnlyList<DeclarationBinding> SourceRoots { get; }
    internal IReadOnlyDictionary<string, DeclarationBinding> SourceDefinitions { get; }
    internal IReadOnlyDictionary<(string Owner, string Identity), DeclarationBinding> SourceLocalDeclarations { get; }
    internal IReadOnlyDictionary<string, IReadOnlyList<DeclarationBinding>> SourceLocalMethods { get; }
    internal IReadOnlyDictionary<string, SupertypeGraph.Def> SourceSupertypes { get; }

    internal CurrentDeclarationBindings(IReadOnlyList<BirFileDeclaration> roots)
    {
        sourceFiles = roots.ToArray();
        SourceRoots = roots.Select(root => GetSource(root)).ToArray();
        var definitions = new Dictionary<string, DeclarationBinding>(StringComparer.Ordinal);
        foreach (var root in roots)
            foreach (var declaration in root.Types.OrEmpty())
                if (declaration.Name.HasValue)
                    definitions.TryAdd(declaration.Name.Value, GetSource((BirGraphValue)declaration));
        SourceDefinitions = definitions;
        var declarations = new Dictionary<(string Owner, string Identity), DeclarationBinding>();
        var methods = new Dictionary<string, List<DeclarationBinding>>(StringComparer.Ordinal);
        void AddMethods(string owner, IEnumerable<BirFunctionDeclaration> rows)
        {
            foreach (var method in rows)
            {
                if (!method.Name.HasValue) continue;
                var key = owner + "\0" + method.Name.Value;
                if (!methods.TryGetValue(key, out var candidates)) methods.Add(key, candidates = new());
                var binding = GetSource(method);
                candidates.Add(binding);
                if (method.DeclarationId.HasValue) declarations.Add((owner, method.DeclarationId.Value), binding);
            }
        }
        foreach (var root in roots)
        {
            // A nameless facade does not own a method-selection bucket.
            if (!root.FileClass.HasValue) continue;
            AddMethods(root.FileClass.Value, root.Methods.OrEmpty());
            foreach (var owner in root.Types.OrEmpty())
                if (owner.Name.HasValue) AddMethods(owner.Name.Value, owner.Methods.OrEmpty());
        }
        SourceLocalDeclarations = declarations;
        SourceLocalMethods = methods.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<DeclarationBinding>)pair.Value, StringComparer.Ordinal);
        SourceSupertypes = SupertypeGraph.CollectSource(roots);
    }

    internal DeclarationBinding GetSource(BirGraphValue actual)
    {
        if (bound) throw new InvalidOperationException("Source declaration admitted after its owning frontier");
        if (!source.TryGetValue(actual, out var binding)) source.Add(actual, binding = new(actual));
        return binding;
    }

    internal DeclarationBinding GetCapturedSource(BirGraphValue actual)
    {
        return TryGetCapturedSource(actual, out var binding) ? binding
            : throw new InvalidOperationException("The declaration has no captured source occurrence");
    }

    // A physical producer reports exact owned removals before detaching them.
    // Existing proof/signature buyers keep their same captured method binding;
    // no missing declaration is admitted or rediscovered at the frontier.
    internal void RetireSourceMembers(IBirTypeDeclaration owner,
        IReadOnlyList<BirFunctionDeclaration> methods, IReadOnlyList<BirPropertyDeclaration> properties)
    {
        if (bound) throw new InvalidOperationException("Source declaration retired after its owning frontier");
        var methodSet = new HashSet<BirFunctionDeclaration>(methods, ReferenceEqualityComparer.Instance);
        var propertySet = new HashSet<BirPropertyDeclaration>(properties, ReferenceEqualityComparer.Instance);
        if (methodSet.Count != methods.Count || propertySet.Count != properties.Count
            || methods.Any(method => !source.ContainsKey(method) || retiredMethods.ContainsKey(method)
                || owner.Methods.OrEmpty().Count(actual => ReferenceEquals(actual, method)) != 1)
            || properties.Any(property => retiredProperties.Any(row => ReferenceEquals(row.Property, property))
                || owner.Properties.OrEmpty().Count(actual => ReferenceEquals(actual, property)) != 1))
            throw new InvalidOperationException("Retirement does not name unique captured and currently owned declarations");
        foreach (var method in methods) retiredMethods.Add(method, owner);
        foreach (var property in properties) retiredProperties.Add(((BirGraphValue)owner, property));
    }

    // Late property allocation may retire an unrepresentable PropertyDef on a
    // file facade as well as on an ordinary type. The producer reports the
    // actual owner and still-attached rows; names are not an ownership witness.
    internal void RetireSourceProperties(BirGraphValue owner, IReadOnlyList<BirPropertyDeclaration> properties)
    {
        if (bound) throw new InvalidOperationException("Source declaration retired after its owning frontier");
        var current = CurrentProperties(owner);
        if (properties.Distinct(ReferenceEqualityComparer.Instance).Count() != properties.Count
            || properties.Any(property => retiredProperties.Any(row => ReferenceEquals(row.Property, property))
                || current.Count(actual => ReferenceEquals(actual, property)) != 1))
            throw new InvalidOperationException("Retirement does not name unique currently owned properties");
        foreach (var property in properties) retiredProperties.Add((owner, property));
    }

    static BirPropertyDeclaration[] CurrentProperties(BirGraphValue owner) => owner switch
    {
        BirFileDeclaration file => file.Properties.OrEmpty(),
        IBirTypeDeclaration type => type.Properties.OrEmpty(),
        _ => throw new InvalidOperationException("Property retirement requires its actual file or type owner"),
    };

    internal bool IsRetiredSource(BirFunctionDeclaration method)
    {
        if (bound) throw new InvalidOperationException("Source retirement read after its owning frontier");
        return retiredMethods.ContainsKey(method);
    }

    internal bool TryGetCapturedSource(BirGraphValue actual, out DeclarationBinding binding)
    {
        if (bound) throw new InvalidOperationException("Source declaration read after its owning frontier");
        return source.TryGetValue(actual, out binding);
    }

    // A producer reports the actual discarded forest before deleting its
    // owning edge. Descendants keep their captured binding and proof rows.
    internal void RetireSourceForest(BirGraphValue actual)
    {
        if (bound || actual == null || !retiredForests.Add(actual))
            throw new InvalidOperationException("Source forest retirement is duplicated or outside its owning phase");
    }

    internal void CompleteSourceCustody(SourceDeclarationProjection ledger)
    {
        if (bound) throw new InvalidOperationException("Source declaration custody was already completed");
        ArgumentNullException.ThrowIfNull(ledger);
        ledger.RequireAuthority(this);
        foreach (var forest in retiredForests)
            if (!ledger.IsRetired(forest))
                throw new InvalidOperationException("A retired source forest has no exact reported continuation");
        if (retiredMethods.Any(row => !ledger.IsRetired((BirGraphValue)row.Value)
                && row.Value.Methods.OrEmpty().Any(method => ReferenceEquals(method, row.Key)))
            || retiredProperties.Any(row => !ledger.IsRetired(row.Owner)
                && CurrentProperties(row.Owner).Any(property => ReferenceEquals(property, row.Property))))
            throw new InvalidOperationException("A retired declaration is still owned by its producer");
        foreach (var declaration in source.Keys) ledger.RequireOccurrence(declaration);
        // All checks finish before releasing ANY captured binding. Failed
        // completion leaves the same source authority available for diagnosis.
        foreach (var binding in source.Values) binding.ReleaseSource();
        source.Clear();
        retiredMethods.Clear(); retiredProperties.Clear(); retiredForests.Clear();
        Array.Clear(sourceFiles);
        bound = true;
    }
}
