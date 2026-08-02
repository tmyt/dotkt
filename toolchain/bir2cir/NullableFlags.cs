using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using DotKt.Bir;

// The flattened NullableAttribute (NRT) byte walk, shared across the decl-position NRT collection (params / method
// returns / fields / properties) and the suspend Task-bridge return (#37/#48 nullability fold). A reference type's
// `?` no longer rides a decl-level scalar flag nor a `System.Nullable<>` wrapper — it is stripped to the bare type by
// BirTypeLowering, and its nullability is carried HERE as a `NullableAttribute` byte array (RoundtripMetadata folds it
// into the decl's `attrs`/`retAttrs` for ilemit to stamp; dll2klib reads it back off the dll). One byte per NODE in
// pre-order (0 = oblivious, 1 = non-null, 2 = nullable). A VALUE type carries no ANNOTATION — it is the structural
// `Nullable<T>`, not an NRT one — but it still holds a byte POSITION (always 0) once it is constructed, and its
// arguments are walked either way; `dll2klib`'s reader implements the same rule from the other side.
//
// This is the promoted, oracle-keyed generalization of SuspendColdLowering's former private `WalkNullable` +
// `ValueTypeFqns` (which only ever handled the Task<R> return). The value-ness decision is the struct-ness ORACLE
// (ReferenceMetadataIndex.IsValueTypeFqn + local enum/struct types), not a hardcoded FQN set.
//
// KNOWN DIVERGENCES from that reader, both older than the value-type rule above and both narrow:
//   * a FUNCTION TYPE writes ONE byte here while the reader walks the delegate's type arguments, so
//     `(String?) -> String?` does not round-trip through these bytes (it rides its `[KotlinType]` carrier instead);
//   * `Compute` emits nothing unless some position is NULLABLE, so an all-oblivious type writes no attribute and
//     reimports under the declaration's `[NullableContext(1)]` as non-null.
static class NullableFlags
{
    // Compute the flattened NRT byte array for a SEMANTIC type node (BEFORE BirTypeLowering strips reference wrappers),
    // or null when the type carries NO nullable (2) position — in which case the type's [NullableContext(1)] non-null
    // default already covers every node, so no per-position override is needed. `isValue` is the struct-ness oracle.
    public static JsonArray Compute(TypeNode t, Func<string, bool> isValue)
    {
        if (t == null) return null;
        var flags = new List<int>();
        bool anyNullable = Walk(t, nullableHere: false, flags, isValue);
        if (!anyNullable) return null;
        var arr = new JsonArray();
        foreach (var b in flags) arr.Add(b);
        return arr;
    }

    // Append the pre-order NRT bytes for `t`, returning whether any nullable (2) byte was emitted. `nullableHere` marks
    // that THIS position was reached through an outer `{t:nullable}` wrapper (so the head node's own byte is 2), and
    // `obliviousHere` that it was reached through an `{t:oblivious}` one (byte 0). Both wrappers are markers on the node
    // BELOW them, not nodes of their own, so each delegates rather than emitting a byte and re-deciding the traversal —
    // which is what let `Oblivious(Array)` and `Oblivious(byref)` stop walking their children.
    static bool Walk(TypeNode t, bool nullableHere, List<int> flags, Func<string, bool> isValue, bool obliviousHere = false)
    {
        // The head byte for a node that HOLDS one. Oblivious wins over nullable: `T!` is the un-annotated position.
        int Head() => obliviousHere ? 0 : nullableHere ? 2 : 1;
        switch (t)
        {
            case TypeNode.Nullable n:
                return Walk(n.Of, nullableHere: true, flags, isValue);
            case TypeNode.Oblivious o:
                // NRT-oblivious position (NullableAttribute = 0). kotc never emits this; dll2klib META round-trip only.
                return Walk(o.Of, nullableHere: false, flags, isValue, obliviousHere: true);
            case TypeNode.Fqn f:
                // A value type carries NO annotation — its one nullable form is the structural `Nullable<T>`. It still
                // holds a byte POSITION when it is CONSTRUCTED, and its arguments are always walked, because that is
                // how the flattening a .NET consumer reads back is shaped: `KeyValuePair<string?, int>` is `[0, 2]`,
                // `Dictionary<E, string?>` (E an enum) is `[1, 2]`. Dropping the position, or the arguments under it,
                // shifts every later byte in the same slot. `Args` is tested for EMPTINESS, not for null, because the
                // reader asks the projected type's argument COUNT and an empty non-null list is not a construction.
                if (isValue(f.Name))
                {
                    if (f.Args == null || f.Args.Length == 0) return false;
                    flags.Add(0);
                    var anyV = false;
                    foreach (var a in f.Args) anyV |= Walk(a, nullableHere: false, flags, isValue);
                    return anyV;
                }
                flags.Add(Head());
                var any = nullableHere;
                if (f.Args != null)
                    foreach (var a in f.Args) any |= Walk(a, nullableHere: false, flags, isValue);
                return any;
            case TypeNode.Array a:
                flags.Add(Head());
                var anyA = nullableHere;
                anyA |= Walk(a.Elem, nullableHere: false, flags, isValue);
                return anyA;
            case TypeNode.Fn:
                // A function type is a reference (delegate / object-erased state machine); its inner shape is not walked
                // for NRT. See the FUNCTION-TYPE note in the header: the reader DOES walk the delegate's type arguments,
                // so a function-typed slot must reach it through its `[KotlinType]` carrier, not through these bytes.
                flags.Add(Head());
                return nullableHere;
            case TypeNode.Tv:
                // A type variable is treated as a reference position (bare + NRT byte); a struct-constrained tv would be
                // a value `Nullable<T>` but is resolved to a value elsewhere / erased by the object-erasure lifelines.
                flags.Add(Head());
                return nullableHere;
            case TypeNode.ByRef b:
                // `ref T` is transparent for nullability — the referent's nullability is what matters.
                return Walk(b.Of, nullableHere, flags, isValue, obliviousHere);
            default:
                return false;
        }
    }
}
