using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// A representation owner reports its exact source occurrences, never a new
// semantic catalog. The original vectors remain intact for strict association.
sealed class AliasDeclarationTransitions
{
    internal sealed record Replacement(IBirTypeDeclaration Source, BirClassDefinition Host,
        BirClassDefinition Helper);
    sealed record FileTransition(BirFileDeclaration File, IBirTypeDeclaration[] Original,
        IBirTypeDeclaration[] Continuing, Replacement[] Replacements,
        (string Owner, BirFieldDeclaration Field)[] RetiredFields);
    readonly List<FileTransition> files = new();

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
    }

    static bool Same(IEnumerable<IBirTypeDeclaration> left, IEnumerable<IBirTypeDeclaration> right)
        => left.SequenceEqual(right, ReferenceEqualityComparer.Instance);

    internal void ValidateProjection(IReadOnlyList<JsonNode> roots)
    {
        if (files.Count == 0) return; // Reference builds do not run the representation owner.
        if (roots.Count != files.Count) throw Mismatch();
        for (var index = 0; index < files.Count; index++)
        {
            var entry = files[index];
            if (!Same(entry.File.Types.OrEmpty(), entry.Continuing)
                || roots[index] is not JsonObject root
                || (root["fileClass"] as JsonValue)?.GetValue<string>() != entry.File.FileClass.OrDefault()) throw Mismatch();
            var projected = root["types"] as JsonArray;
            if (projected == null && entry.Continuing.Length == 0) continue;
            if (projected == null || projected.Count != entry.Continuing.Length) throw Mismatch();
            for (var type = 0; type < projected.Count; type++)
                if (projected[type] is not JsonObject declaration
                    || (declaration["name"] as JsonValue)?.GetValue<string>() != entry.Continuing[type].Name.OrDefault()) throw Mismatch();
        }
    }

    internal bool TryOwner(BirFileDeclaration file, int originalPosition, IBirTypeDeclaration original,
        out int continuingPosition, out bool retired)
    {
        var entry = files.SingleOrDefault(row => ReferenceEquals(row.File, file));
        continuingPosition = originalPosition; retired = false;
        if (entry == null) return false;
        if (originalPosition >= entry.Original.Length || !ReferenceEquals(entry.Original[originalPosition], original)
            || !Same(file.Types.OrEmpty(), entry.Continuing)) throw Mismatch();
        retired = entry.Replacements.Any(row => ReferenceEquals(row.Source, original));
        continuingPosition = Array.FindIndex(entry.Continuing, value => ReferenceEquals(value, original));
        if (retired == (continuingPosition >= 0)) throw Mismatch();
        return true;
    }

    internal IEnumerable<JsonObject> LogicalOwners(int fileIndex, JsonObject projection)
    {
        var entry = files[fileIndex];
        foreach (var source in entry.Original)
        {
            if (entry.Replacements.Any(row => ReferenceEquals(row.Source, source))) yield return LogicalHeader(source);
            else
            {
                var position = Array.FindIndex(entry.Continuing, value => ReferenceEquals(value, source));
                if (position < 0 || projection["types"] is not JsonArray types || types[position] is not JsonObject owner) throw Mismatch();
                yield return owner;
            }
        }
    }

    internal bool HasTransitions => files.Count != 0;

    internal void ValidateNativeField(BirFieldDeclaration actual)
    {
        var retired = RetiredFields.Any(row => ReferenceEquals(row.Field, actual));
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

    // Only the header facts consumed by GenericRepresentationPolicy's existing
    // declaration view cross here. Bodies and native fields have their own
    // single projections; they are not copied into this logical header.
    internal static JsonObject LogicalHeader(IBirTypeDeclaration source)
    {
        var header = new JsonObject();
        void Add<T>(string key, OptionalField<T> value, Func<T, JsonNode> project)
        { if (value.IsPresent) header[key] = value.IsNull ? null : project(value.Value); }
        Add("name", source.Name, value => JsonValue.Create(value));
        Add("kind", source.Kind, value => JsonValue.Create(value));
        Add("generated", source.Generated, value => JsonValue.Create(value));
        Add("typeParams", source.TypeParams, values => new JsonArray(values.Select(value => (JsonNode)value.ToDom()).ToArray()));
        Add("attrs", source.Attrs, values => new JsonArray(values.Select(value => (JsonNode)value.ToDom()).ToArray()));
        var facts = source switch {
            BirClassDefinition value => value.KotlinSupertypesPre,
            BirInterfaceDefinition value => value.KotlinSupertypesPre,
            BirEnumDefinition value => value.KotlinSupertypesPre,
            _ => default,
        };
        Add(KotlinSupertypesRecord.PreKey, facts, SourceTypeFacts.ToDom);
        return header;
    }

    internal void Release() => files.Clear();
    static InvalidOperationException Mismatch() => new("Alias representation does not match its reported source occurrences");
}
