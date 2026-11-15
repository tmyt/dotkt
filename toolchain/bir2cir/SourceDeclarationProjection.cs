using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// The SAME producer-report ledger validates terminal custody on actual records.
// It never selects declarations from IDs, names, metadata or physical layout.
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
    readonly Dictionary<BirFileDeclaration, MethodPlacement> placements = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<IBirTypeDeclaration, BirFunctionDeclaration[]> placementTargets = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<BirFunctionDeclaration, IdentityConsumption> consumedIdentities = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<BirGraphValue, int> occurrences = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<BirGraphValue> retiredOccurrences = new(ReferenceEqualityComparer.Instance);
    Action validateMethods;
    bool isSealed;
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
        if (isSealed || released || identity == null || !types.TryGetValue(file, out var row)
            || !file.Types.OrEmpty().SequenceEqual(row.Continuing, ReferenceEqualityComparer.Instance)
            || owner != null && row.Continuing.Count(actual => ReferenceEquals(actual, owner)) != 1
            || CurrentMethods(file, owner).Count(actual => ReferenceEquals(actual, method)) != 1
            || !method.DeclarationId.HasValue || method.DeclarationId.Value != identity
            || !consumedIdentities.TryAdd(method, new(file, owner, identity, metadata))) throw Mismatch();
    }

    internal bool WasIdentityConsumed(BirFunctionDeclaration method, string identity)
    {
        RequirePhase();
        if (!consumedIdentities.TryGetValue(method, out var row)) return false;
        ValidateIdentity(method, row);
        if (row.Identity != identity) throw Mismatch();
        return true;
    }

    static void ValidateIdentity(BirFunctionDeclaration method, IdentityConsumption row)
    {
        if (method.DeclarationId.IsPresent
            || CurrentMethods(row.File, row.Owner).Count(actual => ReferenceEquals(actual, method)) != 1)
            throw Mismatch();
        if (row.Metadata != null
            && (!method.RoundtripDeclarationIdentity.HasValue
                || !ReferenceEquals(method.RoundtripDeclarationIdentity.Value, row.Metadata)
                || row.Metadata.DeclarationId.OrDefault() != row.Identity))
            throw Mismatch();
    }

    static BirFunctionDeclaration[] CurrentMethods(BirFileDeclaration file, IBirTypeDeclaration owner)
        => owner?.Methods.OrEmpty() ?? file.Methods.OrEmpty();

    internal void RetireForest(BirGraphValue actual)
    {
        if (isSealed || actual == null || !retired.Add(actual)) throw Mismatch();
        bindings.RetireSourceForest(actual);
    }

    internal void AppendTypes(BirFileDeclaration file, IReadOnlyList<IBirTypeDeclaration> appended)
    {
        if (isSealed || !types.TryGetValue(file, out var row)) throw Mismatch();
        var next = row.Continuing.Concat(appended).ToArray();
        if (next.Distinct(ReferenceEqualityComparer.Instance).Count() != next.Length
            || !file.Types.OrEmpty().SequenceEqual(next, ReferenceEqualityComparer.Instance)) throw Mismatch();
        types[file] = row with { Continuing = next };
    }

    internal void RetireTypes(BirFileDeclaration file, IReadOnlyList<IBirTypeDeclaration> removed)
    {
        if (isSealed || !types.TryGetValue(file, out var row)
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
        if (isSealed || released || !types.ContainsKey(file)
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
        if (isSealed || released || !types.ContainsKey(file) || moves.Count == 0
            || !placements.TryAdd(file, new(before, after, moves.ToArray()))) throw Mismatch();
        foreach (var owner in moves.Select(move => move.Destination).Distinct<IBirTypeDeclaration>(ReferenceEqualityComparer.Instance))
            placementTargets[owner] = owner.Methods.OrEmpty().ToArray();
    }

    internal bool TryMethodPlacement(BirFileDeclaration file, out MethodPlacement placement)
    {
        if (!isSealed || released) throw Mismatch();
        return placements.TryGetValue(file, out placement);
    }

    // The backend owns the complete method vectors. Reuse its final validator
    // rather than capturing another current method/selection catalog here.
    internal void RegisterMethodVectorValidation(Action validate)
    {
        if (isSealed || released || validateMethods != null || validate == null) throw Mismatch();
        validateMethods = validate;
    }

    internal void SealSourceContinuations()
    {
        if (isSealed || released) throw Mismatch();
        validateMethods?.Invoke();
        foreach (var row in types.Values)
            if (!row.File.Types.OrEmpty().SequenceEqual(row.Continuing, ReferenceEqualityComparer.Instance)) throw Mismatch();
        foreach (var (file, placement) in placements)
            if (!file.Methods.OrEmpty().SequenceEqual(placement.After, ReferenceEqualityComparer.Instance)) throw Mismatch();
        foreach (var (owner, methods) in placementTargets)
            if (!owner.Methods.OrEmpty().SequenceEqual(methods, ReferenceEqualityComparer.Instance)) throw Mismatch();

        // Validation uses temporary reference counts, not a semantic index.
        // Walk the old structural domain, including captured frozen anchors.
        var counts = new Dictionary<BirGraphValue, int>(ReferenceEqualityComparer.Instance);
        var live = new HashSet<BirGraphValue>(ReferenceEqualityComparer.Instance);
        foreach (var root in roots) Walk(root, value => {
            counts[value] = counts.GetValueOrDefault(value) + 1;
            live.Add(value);
        });
        var descendants = new HashSet<BirGraphValue>(ReferenceEqualityComparer.Instance);
        foreach (var root in retired)
            root.VisitChildren(child => Walk(child, value => descendants.Add(value)));
        var detached = new HashSet<BirGraphValue>(ReferenceEqualityComparer.Instance);
        foreach (var root in retired.Where(value => !descendants.Contains(value)))
            Walk(root, value => {
                if (live.Contains(value)) throw Mismatch();
                counts[value] = counts.GetValueOrDefault(value) + 1;
                detached.Add(value);
            });
        // Match the old observer's uniqueness domain. Shared pure metadata is
        // not newly forbidden; any explicitly queried occurrence still must be unique.
        foreach (var (actual, count) in counts)
            if (count != 1 && (actual is BirFileDeclaration or IBirTypeDeclaration or BirFunctionDeclaration
                or BirFieldDeclaration or BirRefTypeDeclaration or BirRoundtripDeclarationIdentity
                || bindings.TryGetCapturedSource(actual, out _))) throw Mismatch();
        foreach (var actual in retired)
            if (!detached.Contains(actual)) throw Mismatch();
        foreach (var method in published)
            if (!live.Contains(method) || counts.GetValueOrDefault(method) != 1) throw Mismatch();
        foreach (var (method, row) in consumedIdentities)
        {
            if (!live.Contains(method) || counts.GetValueOrDefault(method) != 1) throw Mismatch();
            ValidateIdentity(method, row);
        }
        foreach (var (actual, count) in counts) occurrences.Add(actual, count);
        retiredOccurrences.UnionWith(detached);
        isSealed = true;
    }

    void RequirePhase()
    {
        if (!isSealed || released) throw Mismatch();
    }

    internal void RequireAuthority(CurrentDeclarationBindings actual)
    {
        RequirePhase();
        if (!ReferenceEquals(bindings, actual)) throw Mismatch();
    }

    internal void RequireOccurrence(BirGraphValue actual)
    {
        RequirePhase();
        if (actual == null || occurrences.GetValueOrDefault(actual) != 1) throw Mismatch();
    }

    internal bool IsRetired(BirGraphValue actual)
    {
        RequireOccurrence(actual);
        return retiredOccurrences.Contains(actual);
    }

    internal bool IsPublished(BirFunctionDeclaration method)
    {
        RequirePhase();
        return published.Contains(method);
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

    internal IEnumerable<IBirTypeDeclaration> RetiredTypes => isSealed && !released
        ? types.Values.SelectMany(row => row.Retired) : throw Mismatch();

    internal void Release()
    {
        RequirePhase();
        released = true;
        Array.Clear(roots); types.Clear(); retired.Clear(); published.Clear();
        occurrences.Clear(); retiredOccurrences.Clear(); placements.Clear();
        placementTargets.Clear(); consumedIdentities.Clear(); validateMethods = null;
    }

    static void Walk(BirGraphValue node, Action<BirGraphValue> visit)
    {
        if (node is IBirExecutablePayload) return;
        visit(node);
        node.VisitChildren(child => Walk(child, visit));
    }
    static InvalidOperationException Mismatch() => new("Source declaration transition has no exact owning continuation");
}
