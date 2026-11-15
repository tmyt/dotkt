using System;
using System.Linq;

// A CLR generic argument cannot distinguish `String` from `String?` (or `Int` from `Int?` after the Kotlin
// instantiation has selected the same open `!!T` body). Collection storage also merges read-only/mutable faces.
// Preserve nullability, nominal flags and the runtime classifier as one explicit ABI witness for each METHOD
// type parameter whose body transitively performs a type-sensitive operation. Kotlin
// `reified` is retained separately as declaration metadata; it never selects this physical ABI. This layer threads
// structurally-derived demand through exact declaration identities and explicit lifted-frame correspondences, while
// ilemit merely emits the resulting CIR expression.
static partial class ReifiedNullabilityWitnessLowering
{
    internal const string SemanticIndicesKey = "semanticReifiedIndices";
    internal const string WitnessIndicesKey = "nullableWitnessIndices";
    const string PendingWitnessesKey = "nullablePendingWitnesses";
    const string Prefix = "dotkt$nullableWitness$";

    // Early owning materialization and the late signature repair have the same
    // vector contract; neither may silently supply a missing Kotlin argument.
    static int MissingWitnessTypes(string key, int vectorCount, int argumentCount, int witnessCount)
    {
        if (vectorCount > argumentCount || argumentCount - vectorCount > witnessCount)
            throw new InvalidOperationException(
                $"bir2cir: nullable-sensitive call has inconsistent '{key}' ({vectorCount}) and argument ({argumentCount}) counts");
        return argumentCount - vectorCount;
    }

    static string Sanitize(string value) => new(value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
}
