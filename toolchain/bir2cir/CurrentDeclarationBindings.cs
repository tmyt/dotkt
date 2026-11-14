using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// The current FBound input inventory keeps its membership through the one
// projection. This is not another source-eligibility or member-selection index.
sealed class CurrentDeclarationBindings
{
    internal sealed class DeclarationBinding
    {
        BirGraphValue source;
        JsonObject physical;
        internal DeclarationBinding(BirGraphValue source) => this.source = source;
        internal DeclarationBinding(JsonObject physical) => this.physical = physical;
        internal BirGraphValue Source => source
            ?? throw new InvalidOperationException("Declaration already crossed its owning frontier");
        internal JsonObject Physical => physical
            ?? throw new InvalidOperationException("Declaration has not crossed its owning frontier");
        internal bool IsPhysical => physical != null;
        internal void Bind(JsonObject actual) { physical = actual; source = null; }
    }

    readonly Dictionary<BirGraphValue, DeclarationBinding> source = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<JsonObject, DeclarationBinding> physical = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<BirGraphValue, JsonObject> projected = new(ReferenceEqualityComparer.Instance);
    bool bound;
    internal bool IsBound => bound;

    internal IReadOnlyList<DeclarationBinding> SourceRoots { get; }
    internal IReadOnlyDictionary<string, DeclarationBinding> SourceDefinitions { get; }
    internal IReadOnlyDictionary<(string Owner, string Identity), DeclarationBinding> SourceLocalDeclarations { get; }
    internal IReadOnlyDictionary<string, IReadOnlyList<DeclarationBinding>> SourceLocalMethods { get; }
    internal IReadOnlyDictionary<string, SupertypeGraph.Def> SourceSupertypes { get; }

    internal CurrentDeclarationBindings(IReadOnlyList<BirFileDeclaration> roots)
    {
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

    internal bool TryGetCapturedSource(BirGraphValue actual, out DeclarationBinding binding)
    {
        if (bound) throw new InvalidOperationException("Source declaration read after its owning frontier");
        return source.TryGetValue(actual, out binding);
    }

    // Called by the generated projector at the actual construction, not by
    // walking an unrelated graph and matching IDs, names or physical layout.
    internal void ObserveProjection(BirGraphValue actual, JsonObject result)
    {
        if (bound) throw new InvalidOperationException("Declaration projection observed after binding");
        if (source.ContainsKey(actual) && !projected.TryAdd(actual, result))
            throw new InvalidOperationException("A captured declaration has multiple projected occurrences");
    }

    internal void BindProjected() => BindProjected(projected);

    internal void BindProjected(IReadOnlyDictionary<BirGraphValue, JsonObject> actual)
    {
        if (bound) throw new InvalidOperationException("Declaration projection was already bound");
        var targets = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
        foreach (var declaration in source.Keys)
            if (!actual.TryGetValue(declaration, out var target) || target == null || !targets.Add(target))
                throw new InvalidOperationException("A captured declaration has no unique actual DOM continuation");
        // Validation above is complete before any binding changes phase.
        foreach (var (declaration, binding) in source)
        {
            var target = actual[declaration];
            binding.Bind(target);
            physical.Add(target, binding);
        }
        source.Clear();
        projected.Clear();
        bound = true;
    }

    internal bool TryGetPhysical(JsonObject actual, out DeclarationBinding binding)
    {
        if (!bound) throw new InvalidOperationException("Physical declarations read before the owning frontier");
        return physical.TryGetValue(actual, out binding);
    }

    internal DeclarationBinding GetPhysical(JsonObject actual) => TryGetPhysical(actual, out var binding)
        ? binding : throw new InvalidOperationException("The declaration has no captured or explicitly admitted physical occurrence");

    internal DeclarationBinding RegisterPhysical(JsonObject actual)
    {
        if (!bound) throw new InvalidOperationException("Physical declaration admitted before the owning frontier");
        if (!physical.TryGetValue(actual, out var binding)) physical.Add(actual, binding = new(actual));
        return binding;
    }
}
