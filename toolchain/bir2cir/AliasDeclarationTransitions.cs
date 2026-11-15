using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// A representation owner reports its exact source occurrences, never a new
// semantic catalog. The original vectors remain intact for strict association.
sealed partial class AliasDeclarationTransitions
{
    internal sealed record Replacement(IBirTypeDeclaration Source, BirClassDefinition Host,
        BirClassDefinition Helper);
    sealed record FileTransition(BirFileDeclaration File, IBirTypeDeclaration[] Original,
        IBirTypeDeclaration[] Continuing, Replacement[] Replacements,
        (string Owner, BirFieldDeclaration Field)[] RetiredFields);
    readonly List<FileTransition> files = new();
    readonly HashSet<IBirTypeDeclaration> retiredPhysicalOwners = new(ReferenceEqualityComparer.Instance);

    internal void Record(BirFileDeclaration file, IBirTypeDeclaration[] original,
        IBirTypeDeclaration[] continuing, IReadOnlyList<Replacement> replacements)
    {
        if (files.Any(entry => ReferenceEquals(entry.File, file))) throw Mismatch();
        var expected = new List<IBirTypeDeclaration>();
        foreach (var declaration in original)
        {
            var replacement = replacements.SingleOrDefault(row => ReferenceEquals(row.Source, declaration));
            if (replacement == null) expected.Add(declaration);
            else
            {
                if (replacement.Host != null) expected.Add(replacement.Host);
                if (replacement.Helper != null) expected.Add(replacement.Helper);
            }
        }
        if (replacements.Any(row => !original.Any(value => ReferenceEquals(value, row.Source)))
            || !Same(expected, continuing)) throw Mismatch();
        // Disposition belongs to the representation decision, not a later read
        // of the mutable host. A lost retained field must not become a retirement.
        var retiredFields = replacements.SelectMany(replacement => replacement.Source.Fields.OrEmpty()
            .Where(declaration => replacement.Host == null || !replacement.Host.Fields.OrEmpty()
                .Any(value => ReferenceEquals(value, declaration)))
            .Select(declaration => (replacement.Source.Name.OrDefault(), declaration))).ToArray();
        files.Add(new(file, original, continuing, replacements.ToArray(), retiredFields));
        foreach (var source in original)
            if (replacements.Any(row => ReferenceEquals(row.Source, source))) retiredLogicalOwners.Add(source);
    }

    static bool Same(IEnumerable<IBirTypeDeclaration> left, IEnumerable<IBirTypeDeclaration> right)
        => left.SequenceEqual(right, ReferenceEqualityComparer.Instance);

    // Later materialization may append physical declarations, but cannot change
    // the reported representation of any preceding source occurrence. These
    // declarations extend the physical continuation, not the original logical owners.
    internal void AdmitMaterializedTypes(BirFileDeclaration file, IReadOnlyList<IBirTypeDeclaration> appended)
    {
        if (files.Count == 0) return; // Reference builds have no alias transition ledger.
        var index = files.FindIndex(entry => ReferenceEquals(entry.File, file));
        if (index < 0) throw Mismatch();
        var entry = files[index];
        var expected = entry.Continuing.Concat(appended).ToArray();
        if (!Same(file.Types.OrEmpty(), expected)) throw Mismatch();
        files[index] = entry with { Continuing = expected };
    }

    internal void RetireMaterializedTypes(BirFileDeclaration file, IReadOnlyList<IBirTypeDeclaration> removed)
    {
        if (files.Count == 0) return;
        var index = files.FindIndex(entry => ReferenceEquals(entry.File, file));
        if (index < 0) throw Mismatch();
        var entry = files[index];
        if (!Same(file.Types.OrEmpty(), entry.Continuing)
            || removed.Distinct(ReferenceEqualityComparer.Instance).Count() != removed.Count
            || removed.Any(owner => entry.Continuing.Count(actual => ReferenceEquals(actual, owner)) != 1
                || !retiredPhysicalOwners.Add(owner))) throw Mismatch();
        files[index] = entry with { Continuing = entry.Continuing.Where(owner =>
            !removed.Any(actual => ReferenceEquals(actual, owner))).ToArray() };
    }

    internal void ValidateSourceContinuations(IReadOnlyList<BirFileDeclaration> roots)
    {
        if (files.Count == 0) return; // Reference builds do not run the representation owner.
        if (roots.Count != files.Count) throw Mismatch();
        for (var index = 0; index < files.Count; index++)
            if (!ReferenceEquals(roots[index], files[index].File)
                || !Same(files[index].File.Types.OrEmpty(), files[index].Continuing)) throw Mismatch();
    }

    internal bool TryOwner(BirFileDeclaration file, int originalPosition, IBirTypeDeclaration original,
        out int continuingPosition, out bool retired)
    {
        var entry = files.SingleOrDefault(row => ReferenceEquals(row.File, file));
        continuingPosition = originalPosition; retired = false;
        if (entry == null) return false;
        if (originalPosition >= entry.Original.Length || !ReferenceEquals(entry.Original[originalPosition], original)
            || !Same(file.Types.OrEmpty(), entry.Continuing)) throw Mismatch();
        retired = entry.Replacements.Any(row => ReferenceEquals(row.Source, original)) || retiredPhysicalOwners.Contains(original);
        continuingPosition = Array.FindIndex(entry.Continuing, value => ReferenceEquals(value, original));
        if (retired == (continuingPosition >= 0)) throw Mismatch();
        return true;
    }

    internal IEnumerable<IBirTypeDeclaration> LogicalOwners(int fileIndex,
        SourceDeclarationProjection declarations = null)
    {
        if (fileIndex < 0 || fileIndex >= files.Count) throw Mismatch();
        var entry = files[fileIndex];
        if (!Same(entry.File.Types.OrEmpty(), entry.Continuing)) throw Mismatch();
        foreach (var source in entry.Original)
        {
            // A replacement retains only its original logical header facts.
            // Returning that SAME source is not physical field/method admission.
            if (entry.Replacements.Any(row => ReferenceEquals(row.Source, source))) yield return source;
            else if (retiredPhysicalOwners.Contains(source))
            {
                if (declarations == null || !declarations.IsRetired((BirGraphValue)source)) throw Mismatch();
                yield return source;
            }
            else
            {
                var position = Array.FindIndex(entry.Continuing, value => ReferenceEquals(value, source));
                if (position < 0) throw Mismatch();
                declarations?.RequireOccurrence((BirGraphValue)source);
                yield return source;
            }
        }
    }

    internal bool HasTransitions => files.Count != 0;

    // A removed logical alias is an explicit producer disposition, not an
    // absent physical owner to recover by name or by scanning its former body.
    internal bool IsReplacedLogicalOwner(IBirTypeDeclaration actual)
        => files.Any(file => file.Replacements.Any(row => ReferenceEquals(row.Source, actual)));

    internal void ValidateNativeField(BirFieldDeclaration actual)
    {
        var retired = RetiredFields.Any(row => ReferenceEquals(row.Field, actual))
            || retiredPhysicalOwners.Any(owner => owner.Fields.OrEmpty().Any(field => ReferenceEquals(field, actual)));
        var live = files.Sum(entry => entry.File.Fields.OrEmpty().Count(field => ReferenceEquals(field, actual))
            + entry.File.Types.OrEmpty().Sum(type => type.Fields.OrEmpty().Count(field => ReferenceEquals(field, actual))));
        if (live != (retired ? 0 : 1)) throw Mismatch();
    }

    internal IEnumerable<(string Owner, BirFieldDeclaration Field)> RetiredFields
    {
        get
        {
            foreach (var entry in files)
                foreach (var declaration in entry.RetiredFields)
                    yield return declaration;
        }
    }

    internal void Release() { files.Clear(); retiredLogicalOwners.Clear(); retiredPhysicalOwners.Clear(); }
    static InvalidOperationException Mismatch() => new("Alias representation does not match its reported source occurrences");
}
