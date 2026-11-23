using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class AliasDeclarationTransitions
{
    // A read-only view of exactly the existing logical-header transport. These
    // vectors remain the producer's actual records, not a second declaration
    // authority. In particular captured parameters, bodies and fields are absent.
    internal readonly record struct LogicalHeaderFacts(OptionalField<string> Name, OptionalField<string> Kind,
        OptionalField<bool> Generated, bool NamedLocal, OptionalField<BirTypeParameter[]> TypeParams,
        OptionalField<BirAttribute[]> Attrs, OptionalField<SourceTypeFacts> KotlinSupertypesPre);

    internal static LogicalHeaderFacts ReadLogicalHeaderFacts(IBirTypeDeclaration source) => new(
        source.Name, source.Kind, source.Generated,
        source is BirClassDefinition local && local.LocalClassifierName.OrDefault() is { Length: > 0 },
        source.TypeParams, source.Attrs, source switch {
            BirClassDefinition value => value.KotlinSupertypesPre,
            BirInterfaceDefinition value => value.KotlinSupertypesPre,
            BirEnumDefinition value => value.KotlinSupertypesPre,
            _ => default,
        });

    // Record adds only explicit retired Source identities, in file/Original
    // order, regardless of the replacement list's order. Facts and Name/Kind
    // selection are read live: no stale memo can hide a later producer change.
    readonly List<IBirTypeDeclaration> retiredLogicalOwners = new();

    // The caller first checks the captured current-definition inventory. This
    // reader is only the retained logical-header leg of that existing precedence;
    // it never discovers a physical continuation by matching a name.
    internal bool TryRetiredLogicalHeader(string name, out LogicalHeaderFacts facts)
    {
        for (var index = retiredLogicalOwners.Count - 1; index >= 0; index--)
        {
            var source = retiredLogicalOwners[index];
            if (!source.Name.HasValue || !source.Kind.HasValue || source.Name.Value != name) continue;
            facts = ReadLogicalHeaderFacts(source);
            return true;
        }
        facts = default;
        return false;
    }

}
