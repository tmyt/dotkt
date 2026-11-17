using DotKt.Bir;
using DotKt.Bir.Lowering;
using DefaultKey = ReferenceMetadataIndex.DefaultKey;
using SignatureKey = ReferenceMetadataIndex.SignatureKey;

// Fixed imported executable facts, not live declarations or a reflection service.
// Capture severs mutable scan-input aliases once; all lookup views share these
// private snapshots. Queries return independently owned splice ingredients.
// The index owns this managed-only catalog for its compilation lifetime.
sealed class ReferenceExecutablePayloads
{
    // Borrowed decoded inputs at the scan/capture frontier. This is not an
    // immutable graph claim: construction copies them before publishing queries.
    // No other declaration facts or mutation capabilities enter the catalog.
    internal sealed record ScanInput(
        IReadOnlyDictionary<DefaultKey, Dictionary<int, IBirExpression>> KotlinDefaults,
        IReadOnlySet<DefaultKey> KotlinDefaultsConflicted,
        IReadOnlyDictionary<string, Dictionary<int, IBirExpression>> KotlinDefaultsByDeclarationId,
        IReadOnlyDictionary<string, List<BirInlinePayload>> InlinePayloads,
        IReadOnlyDictionary<string, BirInlinePayload> InlinePayloadsByDeclarationId);

    readonly Dictionary<DefaultKey, Dictionary<int, IBirExpression>> defaults = new();
    readonly HashSet<DefaultKey> conflictingDefaults = new();
    readonly Dictionary<string, Dictionary<int, IBirExpression>> defaultsById = new(StringComparer.Ordinal);
    readonly HashSet<string> conflictingDefaultIds = new(StringComparer.Ordinal);
    readonly Dictionary<string, Dictionary<int, IBirExpression>> ownerlessDefaults = new(StringComparer.Ordinal);
    readonly HashSet<string> ambiguousDefaults = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<BirInlinePayload>> inline = new(StringComparer.Ordinal);
    readonly Dictionary<string, BirInlinePayload> inlineById = new(StringComparer.Ordinal);
    readonly HashSet<string> conflictingInlineIds = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<BirInlinePayload>> ownerlessInline = new(StringComparer.Ordinal);

    internal ReferenceExecutablePayloads(IEnumerable<ScanInput> inputs)
    {
        // A declaration's default map is indexed by signature, relaxed signature,
        // arity and identity; an inline payload by candidate shape and identity.
        // Capture each input object once, not once per derived lookup entry.
        var defaultCopies = new Dictionary<Dictionary<int, IBirExpression>, Dictionary<int, IBirExpression>>(
            ReferenceEqualityComparer.Instance);
        var inlineCopies = new Dictionary<BirInlinePayload, BirInlinePayload>(ReferenceEqualityComparer.Instance);
        Dictionary<int, IBirExpression> CaptureDefaults(Dictionary<int, IBirExpression> source)
        {
            if (!defaultCopies.TryGetValue(source, out var captured))
                defaultCopies.Add(source, captured = CloneDefaults(source));
            return captured;
        }
        BirInlinePayload CaptureInline(BirInlinePayload source)
        {
            if (!inlineCopies.TryGetValue(source, out var captured))
                inlineCopies.Add(source, captured = (BirInlinePayload)source.DeepClone());
            return captured;
        }
        foreach (var input in inputs)
        {
            conflictingDefaults.UnionWith(input.KotlinDefaultsConflicted);
            foreach (var (key, source) in input.KotlinDefaults)
            {
                var captured = CaptureDefaults(source);
                defaults.TryAdd(key, captured);
                // Signature entries do not fold, and construction always states
                // its owner: unrelated constructors never become ownerless calls.
                if (key.Signature != null || key.Method == ReferenceMetadataIndex.CtorKeyName) continue;
                var name = key.Method + "|" + key.ParamCount;
                if (ambiguousDefaults.Contains(name)) continue;
                if (input.KotlinDefaultsConflicted.Contains(key)
                    || ownerlessDefaults.TryGetValue(name, out var previous) && !ExecutableBirCodec.SameDefaults(previous, captured))
                {
                    ownerlessDefaults.Remove(name);
                    ambiguousDefaults.Add(name);
                }
                else ownerlessDefaults.TryAdd(name, captured);
            }
            foreach (var (id, source) in input.KotlinDefaultsByDeclarationId)
            {
                var captured = CaptureDefaults(source);
                if (!defaultsById.TryAdd(id, captured) && !ExecutableBirCodec.SameDefaults(defaultsById[id], captured))
                    conflictingDefaultIds.Add(id);
            }
            foreach (var (key, payloads) in input.InlinePayloads)
            {
                if (!inline.TryGetValue(key, out var candidates)) inline[key] = candidates = new();
                candidates.AddRange(payloads.Select(CaptureInline));
            }
            foreach (var (id, source) in input.InlinePayloadsByDeclarationId)
            {
                var captured = CaptureInline(source);
                if (!inlineById.TryAdd(id, captured) && !ExecutableBirCodec.SameInline(inlineById[id], captured))
                    conflictingInlineIds.Add(id);
            }
        }
        // Preserve the current stdlib-only ownerless candidate policy. The
        // caller's structural selection, not this index, chooses an overload.
        foreach (var (key, payloads) in inline)
        {
            var separator = key.IndexOf('|');
            if (separator < 0 || !key.AsSpan(0, separator).StartsWith("kotlin.")) continue;
            var name = key[(separator + 1)..];
            if (!ownerlessInline.TryGetValue(name, out var candidates)) ownerlessInline[name] = candidates = new();
            candidates.AddRange(payloads);
        }
    }

    internal Dictionary<int, IBirExpression> DefaultValues(string owner, string method, int parameterCount,
        SignatureKey signature, SignatureKey relaxedSignature)
    {
        if (method == null) return null;
        if (owner == null)
            return CloneDefaults(ownerlessDefaults.GetValueOrDefault(method + "|" + parameterCount));
        if (signature != null)
        {
            var exact = new DefaultKey(owner, method, parameterCount, signature);
            if (defaults.TryGetValue(exact, out var selected) && !conflictingDefaults.Contains(exact))
                return CloneDefaults(selected);
            var relaxed = new DefaultKey(owner, method, parameterCount, relaxedSignature, Relaxed: true);
            if (defaults.TryGetValue(relaxed, out selected) && !conflictingDefaults.Contains(relaxed))
                return CloneDefaults(selected);
        }
        var key = new DefaultKey(owner, method, parameterCount);
        return conflictingDefaults.Contains(key) ? null : CloneDefaults(defaults.GetValueOrDefault(key));
    }

    internal Dictionary<int, IBirExpression> DefaultValuesByIdentity(string id)
    {
        if (id != null && conflictingDefaultIds.Contains(id))
            throw new InvalidOperationException($"conflicting defaults for Kotlin declaration identity '{id}'");
        return id == null ? null : CloneDefaults(defaultsById.GetValueOrDefault(id));
    }

    internal bool DefaultsAmbiguous(string owner, string method, int parameterCount) =>
        method != null && (owner == null ? ambiguousDefaults.Contains(method + "|" + parameterCount)
            : conflictingDefaults.Contains(new DefaultKey(owner, method, parameterCount)));

    internal BirInlinePayload InlineByIdentity(string id)
    {
        if (id != null && conflictingInlineIds.Contains(id))
            throw new InvalidOperationException($"conflicting inline declaration identity '{id}'");
        return id != null && inlineById.TryGetValue(id, out var payload) ? (BirInlinePayload)payload.DeepClone() : null;
    }

    internal IReadOnlyList<BirInlinePayload> InlineCandidates(string owner, string method, int parameters, int arity) =>
        owner == null || method == null ? null : CloneInline(inline.GetValueOrDefault($"{owner}|{method}|{parameters}|{arity}"));

    internal IReadOnlyList<BirInlinePayload> OwnerlessInlineCandidates(string method, int parameters, int arity) =>
        method == null ? null : CloneInline(ownerlessInline.GetValueOrDefault($"{method}|{parameters}|{arity}"));

    static Dictionary<int, IBirExpression> CloneDefaults(IReadOnlyDictionary<int, IBirExpression> values) =>
        values?.ToDictionary(pair => pair.Key, pair => (IBirExpression)((BirGraphValue)pair.Value).DeepClone());

    static IReadOnlyList<BirInlinePayload> CloneInline(IReadOnlyList<BirInlinePayload> payloads) =>
        payloads?.Select(payload => (BirInlinePayload)payload.DeepClone()).ToArray();

}
