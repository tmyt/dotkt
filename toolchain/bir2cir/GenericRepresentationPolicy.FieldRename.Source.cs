using DotKt.Bir;
using DotKt.Bir.Lowering;

sealed partial class GenericRepresentationPolicy
{
    // The producer changed these actual fields, not native-storage eligibility.
    // Carry that operation through the already selected keys and occurrences.
    internal void ApplyFieldRenames(IReadOnlyList<BackingFieldRename.Rename> reports)
    {
        if (!_sourceAwaitingDom || _pendingNativeOrigins == null || _pendingNativePhysicalBindings == null
            || _refreshedNativeLocations == null)
            throw new InvalidOperationException("Field rename requires the pending native physical continuation");
        var seen = new HashSet<BirFieldDeclaration>(ReferenceEqualityComparer.Instance);
        var plans = new List<(int Index, NativePhysicalOccurrence Before,
            (string Owner, string Name, bool Static, string Receiver) Next, BirGraphValue[] Origins)>();
        foreach (var report in reports)
        {
            var field = report.Field;
            var (owner, fields) = report.Owner switch {
                BirFileDeclaration file => (file.FileClass.OrDefault(), file.Fields.OrEmpty()),
                IBirTypeDeclaration type => (type.Name.OrDefault(), type.Fields.OrEmpty()),
                _ => ((string)null, Array.Empty<BirFieldDeclaration>()),
            };
            if (field == null || owner == null || !seen.Add(field)
                || fields.Count(actual => ReferenceEquals(actual, field)) != 1
                || field.Name.OrDefault() != report.NewName || report.OldName == null
                || report.NewName == null || report.OldName == report.NewName)
                throw new InvalidOperationException("Field rename lost its exact declaration operation");
            var positions = Enumerable.Range(0, _refreshedNativeLocations.Length)
                .Where(index => ReferenceEquals(_refreshedNativeLocations[index].Declaration, field)).ToArray();
            var origins = _pendingNativeOrigins.Keys.Where(origin => ReferenceEquals(origin, field)
                || _nativeStorageMaterializations.TryGetValue(origin, out var physical)
                    && ReferenceEquals(physical, field)).ToArray();
            if (positions.Length == 0 && origins.Length == 0 && !field._nativeLocation.OrDefault()) continue;
            if (positions.Length != 1 || !field._nativeLocation.OrDefault())
                throw new InvalidOperationException("Native field rename has no unique selected occurrence");
            var occurrence = _refreshedNativeLocations[positions[0]];
            var previous = FieldKey(owner, report.OldName, field.Static.OrDefault(), field.CompanionReceiver.OrDefault());
            var next = FieldKey(owner, report.NewName, field.Static.OrDefault(), field.CompanionReceiver.OrDefault());
            if (occurrence.Cell || !ReferenceEquals(occurrence.Owner, report.Owner) || occurrence.Key != previous
                || origins.Any(origin => _pendingNativeOrigins[origin] != previous))
                throw new InvalidOperationException("Native field rename changed its selected owner or role");
            ValidateOriginalNativeAdmission(occurrence);
            plans.Add((positions[0], occurrence, next, origins));
        }
        var previousKeys = plans.Select(plan => plan.Before.Key).ToHashSet();
        var nextKeys = plans.Select(plan => plan.Next).ToHashSet();
        if (previousKeys.Count != plans.Count || nextKeys.Count != plans.Count
            || nextKeys.Any(key => _pendingNativePhysicalBindings.Contains(key) && !previousKeys.Contains(key))
            || _refreshedNativeLocations.Any(row => nextKeys.Contains(row.Key) && !previousKeys.Contains(row.Key)))
            throw new InvalidOperationException("Native field rename collided with another selected occurrence");
        // Validate the complete batch before mutating any ledger. Keep the same
        // source-origin objects and maps; no scan may select a new native field.
        var originalKeys = plans.Where(plan => _pendingNativePhysicalBindings.Contains(plan.Before.Key))
            .Select(plan => plan.Before.Key).ToHashSet();
        _pendingNativePhysicalBindings.ExceptWith(originalKeys);
        foreach (var plan in plans)
        {
            if (originalKeys.Contains(plan.Before.Key)) _pendingNativePhysicalBindings.Add(plan.Next);
            foreach (var origin in plan.Origins) _pendingNativeOrigins[origin] = plan.Next;
            _refreshedNativeLocations[plan.Index] = plan.Before with { Key = plan.Next };
            // Source and already placed spellings coexist until the frontier;
            // renamed physical uses now acquire the same selected native role.
            _nativeFieldNames.Add(plan.Next);
        }
    }
}
