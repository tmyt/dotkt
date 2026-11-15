using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// One explicit projection of producer-reported retired forests. This records
// actual occurrences only; it neither selects source declarations nor admits
// declarations from their IDs, names, metadata or former physical layout.
sealed class SourceDeclarationProjection
{
    sealed record TypeVector(BirFileDeclaration File, IBirTypeDeclaration[] Original,
        IBirTypeDeclaration[] Continuing, HashSet<IBirTypeDeclaration> Retired);
    sealed record IdentityConsumption(BirFileDeclaration File, IBirTypeDeclaration Owner, string Identity,
        BirRoundtripDeclarationIdentity Metadata = null);
    internal sealed class MethodPlacement
    {
        internal BirFunctionDeclaration[] Before { get; }
        internal BirFunctionDeclaration[] After { get; private set; }
        internal IReadOnlyList<TypeOwnershipLowering.SourceMethodMove> Moves { get; }
        readonly Dictionary<BirFunctionDeclaration, TypeOwnershipLowering.SourceMethodMove> moved;
        internal MethodPlacement(BirFunctionDeclaration[] before, BirFunctionDeclaration[] after,
            IReadOnlyList<TypeOwnershipLowering.SourceMethodMove> moves)
        {
            Before = before; After = after; Moves = moves.ToArray();
            moved = new(ReferenceEqualityComparer.Instance);
            foreach (var move in Moves) if (!moved.TryAdd(move.Method, move)) throw Mismatch();
        }
        internal bool TryMove(BirFunctionDeclaration method, out TypeOwnershipLowering.SourceMethodMove move)
            => moved.TryGetValue(method, out move);
        internal void Append(BirFunctionDeclaration[] next) => After = next;
    }
    readonly CurrentDeclarationBindings bindings;
    readonly BirFileDeclaration[] roots;
    readonly Dictionary<BirFileDeclaration, TypeVector> types = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<BirGraphValue> retired = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<BirFunctionDeclaration> published = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<BirGraphValue, JsonObject> projections = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<BirFunctionDeclaration, JsonObject> methods = new(ReferenceEqualityComparer.Instance);
    readonly List<JsonObject> forestRoots = new();
    readonly HashSet<JsonObject> materializedMethods = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<BirFileDeclaration, MethodPlacement> placements = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<IBirTypeDeclaration, BirFunctionDeclaration[]> placementTargets = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<BirFunctionDeclaration, IdentityConsumption> consumedIdentities = new(ReferenceEqualityComparer.Instance);
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

    // Physical name allocation consumes an emission identity, not the original
    // selection key. Only its producer can report this exact attached occurrence
    // before removing the field; binders never reconstruct a missing identity.
    internal void ReportPhysicalIdentityConsumption(BirFileDeclaration file, IBirTypeDeclaration owner,
        BirFunctionDeclaration method, string identity)
        => ReportIdentityConsumption(file, owner, method, identity, null);

    internal void ReportMetadataIdentityTransfer(BirFileDeclaration file, IBirTypeDeclaration owner,
        BirFunctionDeclaration method, BirRoundtripDeclarationIdentity identity)
    {
        if (identity == null || !method.RoundtripDeclarationIdentity.HasValue
            || !ReferenceEquals(method.RoundtripDeclarationIdentity.Value, identity)
            || !identity.DeclarationId.HasValue) throw Mismatch();
        ReportIdentityConsumption(file, owner, method, identity.DeclarationId.Value, identity);
    }

    void ReportIdentityConsumption(BirFileDeclaration file, IBirTypeDeclaration owner,
        BirFunctionDeclaration method, string identity, BirRoundtripDeclarationIdentity metadata)
    {
        if (projected || released || identity == null || !types.TryGetValue(file, out var row)
            || !file.Types.OrEmpty().SequenceEqual(row.Continuing, ReferenceEqualityComparer.Instance)
            || owner != null && row.Continuing.Count(actual => ReferenceEquals(actual, owner)) != 1
            || CurrentMethods(file, owner).Count(actual => ReferenceEquals(actual, method)) != 1
            || !method.DeclarationId.HasValue || method.DeclarationId.Value != identity
            || !consumedIdentities.TryAdd(method, new(file, owner, identity, metadata))) throw Mismatch();
    }

    internal bool WasIdentityConsumed(BirFunctionDeclaration method, string identity)
    {
        if (released) throw Mismatch();
        if (!consumedIdentities.TryGetValue(method, out var row)) return false;
        if (!projected || row.Identity != identity || method.DeclarationId.IsPresent
            || CurrentMethods(row.File, row.Owner).Count(actual => ReferenceEquals(actual, method)) != 1)
            throw Mismatch();
        if (row.Metadata != null
            && (!method.RoundtripDeclarationIdentity.HasValue
                || !ReferenceEquals(method.RoundtripDeclarationIdentity.Value, row.Metadata)
                || row.Metadata.DeclarationId.OrDefault() != identity
                || !projections.TryGetValue(row.Metadata, out var target)
                || !ReferenceEquals(Get(method)["roundtripDeclarationIdentity"], target)
                || target["declarationId"] is not JsonValue value
                || !value.TryGetValue<string>(out var transferred) || transferred != identity))
            throw Mismatch();
        return true;
    }

    static BirFunctionDeclaration[] CurrentMethods(BirFileDeclaration file, IBirTypeDeclaration owner)
        => owner?.Methods.OrEmpty() ?? file.Methods.OrEmpty();

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

    internal void PublishMethods(BirFileDeclaration file, IBirTypeDeclaration owner,
        IReadOnlyList<BirFunctionDeclaration> appended)
    {
        if (projected || released || !types.ContainsKey(file)
            || appended.Distinct(ReferenceEqualityComparer.Instance).Count() != appended.Count
            || appended.Any(published.Contains)) throw Mismatch();
        MethodPlacement placement = null;
        BirFunctionDeclaration[] next = null;
        if (owner == null && placements.TryGetValue(file, out placement))
        {
            next = placement.After.Concat(appended).ToArray();
            if (!file.Methods.OrEmpty().SequenceEqual(next, ReferenceEqualityComparer.Instance)) throw Mismatch();
        }
        else if (owner != null && placementTargets.TryGetValue(owner, out var methods))
        {
            next = methods.Concat(appended).ToArray();
            if (!owner.Methods.OrEmpty().SequenceEqual(next, ReferenceEqualityComparer.Instance)) throw Mismatch();
        }
        // Publication is a producer-reported tail extension of the same actual
        // owner. Before/Moves and all original source memberships stay frozen.
        foreach (var method in appended) published.Add(method);
        if (placement != null) placement.Append(next);
        else if (next != null) placementTargets[owner] = next;
    }

    // These are owning-edge edits, not declaration births or retirements. The
    // producer's transition already checked both complete vectors at mutation.
    internal void MoveMethods(BirFileDeclaration file, BirFunctionDeclaration[] before,
        BirFunctionDeclaration[] after, IReadOnlyList<TypeOwnershipLowering.SourceMethodMove> moves)
    {
        if (projected || released || !types.ContainsKey(file) || moves.Count == 0
            || !placements.TryAdd(file, new(before, after, moves.ToArray()))) throw Mismatch();
        foreach (var owner in moves.Select(move => move.Destination).Distinct<IBirTypeDeclaration>(ReferenceEqualityComparer.Instance))
            placementTargets[owner] = owner.Methods.OrEmpty().ToArray();
    }

    internal bool TryMethodPlacement(BirFileDeclaration file, out MethodPlacement placement)
    {
        if (!projected || released) throw Mismatch();
        return placements.TryGetValue(file, out placement);
    }

    // This observer is composed with the sole continuing ToDom and reused for
    // every retired forest root. Known bindings enforce their own uniqueness.
    internal void ObserveProjection(BirGraphValue actual, JsonObject target)
    {
        bindings.ObserveProjection(actual, target);
        if (actual is BirFileDeclaration or IBirTypeDeclaration or BirFunctionDeclaration or BirFieldDeclaration
            or BirRefTypeDeclaration or BirRoundtripDeclarationIdentity
            || bindings.TryGetCapturedSource(actual, out _))
            if (!projections.TryAdd(actual, target)) throw Mismatch();
    }

    internal void ProjectRetiredForests()
    {
        if (projected) throw Mismatch();
        foreach (var row in types.Values)
            if (!row.File.Types.OrEmpty().SequenceEqual(row.Continuing, ReferenceEqualityComparer.Instance)) throw Mismatch();
        foreach (var (file, placement) in placements)
            if (!file.Methods.OrEmpty().SequenceEqual(placement.After, ReferenceEqualityComparer.Instance)) throw Mismatch();
        foreach (var (owner, methods) in placementTargets)
            if (!owner.Methods.OrEmpty().SequenceEqual(methods, ReferenceEqualityComparer.Instance)) throw Mismatch();
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
        placements.Clear(); placementTargets.Clear(); consumedIdentities.Clear();
    }

    static void Walk(BirGraphValue node, Action<BirGraphValue> visit)
    {
        if (node is IBirExecutablePayload) return;
        visit(node);
        node.VisitChildren(child => Walk(child, visit));
    }
    static InvalidOperationException Mismatch() => new("Source declaration transition has no exact owning projection");
}
