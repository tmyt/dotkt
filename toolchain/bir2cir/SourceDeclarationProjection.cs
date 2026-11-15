using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;

// One explicit projection of producer-reported retired forests. This records
// actual occurrences only; it neither selects source declarations nor admits
// declarations from their IDs, names, metadata or former physical layout.
sealed class SourceDeclarationProjection
{
    sealed record TypeVector(BirFileDeclaration File, IBirTypeDeclaration[] Original,
        IBirTypeDeclaration[] Continuing, HashSet<IBirTypeDeclaration> Retired);
    readonly CurrentDeclarationBindings bindings;
    readonly BirFileDeclaration[] roots;
    readonly Dictionary<BirFileDeclaration, TypeVector> types = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<BirGraphValue> retired = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<BirFunctionDeclaration> published = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<BirGraphValue, JsonObject> projections = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<BirFunctionDeclaration, JsonObject> methods = new(ReferenceEqualityComparer.Instance);
    readonly List<JsonObject> forestRoots = new();
    readonly HashSet<JsonObject> materializedMethods = new(ReferenceEqualityComparer.Instance);
    bool projected;
    bool released;

    internal SourceDeclarationProjection(IReadOnlyList<BirFileDeclaration> roots, CurrentDeclarationBindings bindings)
    {
        this.roots = roots.ToArray(); this.bindings = bindings;
        foreach (var file in roots)
        {
            var vector = file.Types.OrEmpty().ToArray();
            if (!types.TryAdd(file, new(file, vector, vector, new(ReferenceEqualityComparer.Instance)))) throw Mismatch();
        }
    }

    internal IReadOnlyDictionary<BirFunctionDeclaration, JsonObject> RetiredMethods => projected && !released ? methods : throw Mismatch();
    internal IReadOnlySet<JsonObject> MaterializedMethods => projected && !released ? materializedMethods : throw Mismatch();

    internal void RetireForest(BirGraphValue actual)
    {
        if (projected || actual == null || !retired.Add(actual)) throw Mismatch();
        bindings.RetireSourceForest(actual);
    }

    internal void AppendTypes(BirFileDeclaration file, IReadOnlyList<IBirTypeDeclaration> appended)
    {
        if (projected || !types.TryGetValue(file, out var row)) throw Mismatch();
        var next = row.Continuing.Concat(appended).ToArray();
        if (next.Distinct(ReferenceEqualityComparer.Instance).Count() != next.Length
            || !file.Types.OrEmpty().SequenceEqual(next, ReferenceEqualityComparer.Instance)) throw Mismatch();
        types[file] = row with { Continuing = next };
    }

    internal void RetireTypes(BirFileDeclaration file, IReadOnlyList<IBirTypeDeclaration> removed)
    {
        if (projected || !types.TryGetValue(file, out var row)
            || !file.Types.OrEmpty().SequenceEqual(row.Continuing, ReferenceEqualityComparer.Instance)
            || removed.Distinct(ReferenceEqualityComparer.Instance).Count() != removed.Count
            || removed.Any(type => row.Continuing.Count(actual => ReferenceEquals(actual, type)) != 1
                || !row.Retired.Add(type))) throw Mismatch();
        types[file] = row with { Continuing = row.Continuing.Where(type => !removed.Any(actual => ReferenceEquals(actual, type))).ToArray() };
        foreach (var type in removed) RetireForest((BirGraphValue)type);
    }

    internal void PublishMethods(IReadOnlyList<BirFunctionDeclaration> appended)
    {
        if (projected || appended.Any(method => !published.Add(method))) throw Mismatch();
    }

    // This observer is composed with the sole continuing ToDom and reused for
    // every retired forest root. Known bindings enforce their own uniqueness.
    internal void ObserveProjection(BirGraphValue actual, JsonObject target)
    {
        bindings.ObserveProjection(actual, target);
        if (actual is IBirTypeDeclaration or BirFunctionDeclaration or BirFieldDeclaration or BirRefTypeDeclaration
            || bindings.TryGetCapturedSource(actual, out _))
            if (!projections.TryAdd(actual, target)) throw Mismatch();
    }

    internal void ProjectRetiredForests()
    {
        if (projected) throw Mismatch();
        foreach (var row in types.Values)
            if (!row.File.Types.OrEmpty().SequenceEqual(row.Continuing, ReferenceEqualityComparer.Instance)) throw Mismatch();
        var live = new HashSet<BirGraphValue>(ReferenceEqualityComparer.Instance);
        foreach (var root in roots) Walk(root, value => live.Add(value));
        if (retired.Any(live.Contains)) throw Mismatch();

        // A containing retired type/method owns the descendant's one projection.
        // Do not independently re-project a reported descendant inside it.
        var descendants = new HashSet<BirGraphValue>(ReferenceEqualityComparer.Instance);
        foreach (var root in retired)
            root.VisitChildren(child => Walk(child, value => descendants.Add(value)));
        foreach (var root in retired.Where(value => !descendants.Contains(value)))
        {
            var members = new List<BirGraphValue>();
            Walk(root, members.Add);
            if (members.Any(live.Contains)) throw Mismatch();
            var dom = root.ToDom(ObserveProjection);
            forestRoots.Add(dom);
            foreach (var method in members.OfType<BirFunctionDeclaration>())
                if (!methods.TryAdd(method, projections[method])) throw Mismatch();
        }
        foreach (var method in published)
            if (!projections.TryGetValue(method, out var target) || !materializedMethods.Add(target)) throw Mismatch();
        projected = true;
    }

    internal bool TryGet(BirGraphValue actual, out JsonObject target)
    {
        if (released) throw Mismatch();
        return projections.TryGetValue(actual, out target);
    }
    internal JsonObject Get(BirGraphValue actual) => TryGet(actual, out var target) ? target : throw Mismatch();
    internal bool IsRetired(BirGraphValue actual, JsonObject target)
    {
        if (!projected || !TryGet(actual, out var expected) || !ReferenceEquals(expected, target)) return false;
        JsonNode root = target;
        while (root.Parent != null) root = root.Parent;
        return forestRoots.Any(forest => ReferenceEquals(forest, root));
    }

    internal bool TryOwner(BirFileDeclaration file, int originalPosition, IBirTypeDeclaration owner,
        out int currentPosition, out bool isRetired)
    {
        currentPosition = originalPosition; isRetired = false;
        if (!types.TryGetValue(file, out var row)) return false;
        if (originalPosition < 0 || originalPosition >= row.Original.Length
            || !ReferenceEquals(row.Original[originalPosition], owner)
            || !file.Types.OrEmpty().SequenceEqual(row.Continuing, ReferenceEqualityComparer.Instance)) throw Mismatch();
        currentPosition = Array.FindIndex(row.Continuing, actual => ReferenceEquals(actual, owner));
        isRetired = row.Retired.Contains(owner);
        if (isRetired == (currentPosition >= 0)) throw Mismatch();
        return true;
    }

    internal IEnumerable<IBirTypeDeclaration> RetiredTypes => projected && !released
        ? types.Values.SelectMany(row => row.Retired) : throw Mismatch();
    internal IEnumerable<JsonObject> LogicalOwners(int fileIndex, JsonObject projection)
    {
        if (!projected || fileIndex >= roots.Length || !types.TryGetValue(roots[fileIndex], out var row)) throw Mismatch();
        foreach (var owner in row.Original)
        {
            if (row.Retired.Contains(owner)) yield return Get((BirGraphValue)owner);
            else
            {
                var index = Array.FindIndex(row.Continuing, current => ReferenceEquals(current, owner));
                if (index < 0 || projection["types"] is not JsonArray declarations
                    || index >= declarations.Count || declarations[index] is not JsonObject actual) throw Mismatch();
                yield return actual;
            }
        }
    }
    internal void Release()
    {
        if (!projected || released) throw Mismatch();
        released = true;
        Array.Clear(roots); types.Clear(); retired.Clear(); published.Clear(); projections.Clear(); methods.Clear();
        forestRoots.Clear(); materializedMethods.Clear();
    }

    static void Walk(BirGraphValue node, Action<BirGraphValue> visit)
    {
        if (node is IBirExecutablePayload) return;
        visit(node);
        node.VisitChildren(child => Walk(child, visit));
    }
    static InvalidOperationException Mismatch() => new("Source declaration transition has no exact owning projection");
}
