using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class AliasDeclarationTransitions
{
    internal static void HeaderFactsSelfTest()
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Alias logical-header facts: " + message); }
        var source = new BirClassDefinition {
            Name = "Alias", Kind = "class", Generated = false,
            TypeParams = new[] { new BirTypeParameter { Name = "T", Variance = "out" } },
            Attrs = Array.Empty<BirAttribute>(), KotlinSupertypesPre = SourceTypeFacts.Empty,
            CapturedTypeParams = new[] { new BirTypeParameter { Name = "Captured" } },
            Base = new TypeNode.Fqn("System.Object"), SemanticOwner = "Enclosing", Mods = new BirModifiers { Inner = true },
            Fields = new[] { new BirFieldDeclaration { Name = "value", Type = new TypeNode.Tv("type", 0) } },
            Methods = new[] { new BirFunctionDeclaration { Name = "read" } },
        };
        var facts = ReadLogicalHeaderFacts(source);
        Require(ReferenceEquals(facts.TypeParams.Value, source.TypeParams.Value)
            && ReferenceEquals(facts.KotlinSupertypesPre.Value, source.KotlinSupertypesPre.Value)
            && facts.Name.Value == "Alias" && facts.Kind.Value == "class" && facts.Generated.HasValue && !facts.Generated.Value
            && facts.TypeParams.Value[0].Name.Value == "T" && facts.TypeParams.Value[0].Variance.Value == "out"
            && facts.Attrs.HasValue && facts.Attrs.Value.Length == 0 && facts.KotlinSupertypesPre.Value == SourceTypeFacts.Empty,
            "logical header facts/presence or actual parameter identity changed");
        var empty = new BirInterfaceDefinition { TypeParams = Array.Empty<BirTypeParameter>(),
            Attrs = OptionalField<BirAttribute[]>.Null, KotlinSupertypesPre = OptionalField<SourceTypeFacts>.Null };
        var emptyFacts = ReadLogicalHeaderFacts(empty);
        Require(!emptyFacts.Name.IsPresent && emptyFacts.TypeParams.HasValue && emptyFacts.TypeParams.Value.Length == 0
            && emptyFacts.Attrs.IsNull && emptyFacts.KotlinSupertypesPre.IsNull
            && !ReadLogicalHeaderFacts(new BirEnumDefinition()).Name.IsPresent
            && !ReadLogicalHeaderFacts(new BirEnumDefinition()).KotlinSupertypesPre.IsPresent,
            "absent/null/empty header facts changed");

        var ledger = new AliasDeclarationTransitions();
        var first = source;
        var host = new BirClassDefinition { Name = "Alias", Kind = "class", Generated = true,
            TypeParams = Array.Empty<BirTypeParameter>() };
        var firstFile = new BirFileDeclaration { FileClass = "First", Types = new IBirTypeDeclaration[] { host } };
        ledger.Record(firstFile, new IBirTypeDeclaration[] { first }, firstFile.Types.Value,
            new[] { new Replacement(first, host, null) });
        var second = new BirClassDefinition { Name = "Alias", Kind = "class", TypeParams = new[] {
            new BirTypeParameter { Name = "Second", Variance = "in" } } };
        var third = new BirClassDefinition { Name = "Alias", Kind = "class", TypeParams = new[] {
            new BirTypeParameter { Name = "Third", Variance = "out" } } };
        var kept = new BirClassDefinition { Name = "Kept", Kind = "class" };
        var secondFile = new BirFileDeclaration { FileClass = "Second", Types = new IBirTypeDeclaration[] { kept } };
        ledger.Record(secondFile, new IBirTypeDeclaration[] { second, third, kept }, secondFile.Types.Value,
            new[] { new Replacement(third, null, null), new Replacement(second, null, null) });
        Require(ledger.TryRetiredLogicalHeader("Alias", out var selected)
            && ReferenceEquals(selected.TypeParams.Value, third.TypeParams.Value)
            && !ledger.TryRetiredLogicalHeader("Kept", out _) && !ledger.TryRetiredLogicalHeader("Missing", out _),
            "explicit retirement identity or last file/Original precedence changed");

        // Current-definition precedence belongs to the root projection context:
        // it sees this actual host first; the ledger still exposes the distinct
        // retired source facts, and must not pretend they are a physical owner.
        var current = new CurrentDeclarationBindings(new[] { firstFile, secondFile });
        Require(ReferenceEquals(current.SourceDefinitions["Alias"].Source, host)
            && ReadLogicalHeaderFacts((IBirTypeDeclaration)current.SourceDefinitions["Alias"].Source).Generated.Value
            && selected.TypeParams.Value[0].Name.Value == "Third",
            "header reader repaired/replaced the root's current-definition authority");
        var frame = new NullableRepresentationFrame(1, new[] { 0 });
        third.TypeParams = OptionalField<BirTypeParameter[]>.Null;
        third.KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(frame);
        Require(ledger.TryRetiredLogicalHeader("Alias", out selected) && selected.TypeParams.IsNull
            && ReferenceEquals(selected.KotlinSupertypesPre.Value.NullableFrame, frame), "logical facts were frozen instead of read live");
        third.Kind = OptionalField<string>.Null;
        Require(ledger.TryRetiredLogicalHeader("Alias", out selected)
            && ReferenceEquals(selected.TypeParams.Value, second.TypeParams.Value), "null Kind incorrectly supplied a local header");
        third.Kind = "class";
        third.Name = "Renamed";
        Require(ledger.TryRetiredLogicalHeader("Alias", out selected)
            && ReferenceEquals(selected.TypeParams.Value, second.TypeParams.Value)
            && ledger.TryRetiredLogicalHeader("Renamed", out selected) && selected.TypeParams.IsNull,
            "a memoized name/Kind selection lost current retired-record facts");
        third.Name = OptionalField<string>.Null;
        Require(!ledger.TryRetiredLogicalHeader("Renamed", out _)
            && ledger.TryRetiredLogicalHeader("Alias", out selected)
            && ReferenceEquals(selected.TypeParams.Value, second.TypeParams.Value), "null Name incorrectly supplied a local header");
        ledger.Release();
        Require(!ledger.HasTransitions && !ledger.TryRetiredLogicalHeader("Alias", out _)
            && !ledger.TryRetiredLogicalHeader("Renamed", out _), "Release retained logical declaration identities");
        Console.WriteLine("[alias logical-header facts] self-test OK (actual header presence, explicit retirement, live facts, current-host separation and release)");
    }
}
