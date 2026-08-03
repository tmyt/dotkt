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
// (ReferenceMetadataIndex.IsValueTypeFqn + local enum/struct types), not a hardcoded FQN set. `kotlin.Unit` is the one
// name answered here rather than by the oracle — it is a CLASS on the CLR but the type ECMA `void` projects to, and
// the reader answers both with one rule (see the [TypeNode.Fqn] arm).
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
    //   Both markers therefore travel TOGETHER through every delegating arm, and [Head] resolves them in one place. An
    // arm that forwarded only one of them decided the precedence a second time, by omission: dropping `obliviousHere`
    // at the nullable wrapper wrote 2 where the rule says 0. That is one position's nullability, not a shift — the
    // reader's traversal is driven by the signature, so a wrong byte VALUE leaves every other position where it was;
    // it is a wrong byte COUNT (the value-type rule above) that moves them.
    static bool Walk(TypeNode t, bool nullableHere, List<int> flags, Func<string, bool> isValue, bool obliviousHere = false)
    {
        // The head byte for a node that HOLDS one. Oblivious wins over nullable: `T!` is the un-annotated position.
        int Head() => obliviousHere ? 0 : nullableHere ? 2 : 1;
        // Did the head byte come out NULLABLE? The return value of every byte-holding arm — `Compute` emits an
        // attribute only when some position really is 2, and an oblivious-suppressed position is 0, not 2.
        bool HeadIsNullable() => Head() == 2;
        switch (t)
        {
            case TypeNode.Nullable n:
                return Walk(n.Of, nullableHere: true, flags, isValue, obliviousHere);
            case TypeNode.Oblivious o:
                // NRT-oblivious position (NullableAttribute = 0). kotc emits it for every FLEXIBLE/platform type —
                // `{t:oblivious}` wrapping the NOT-NULL core (BirEmitterTypes) — so the FRONTEND cannot hand over an
                // `Oblivious(Nullable(..))`: `birType` builds a `{t:nullable}` only for a MARKED-nullable type, and the
                // oblivious arm wraps `birType(t.makeNotNull())`. A PASS here can: `Oblivious(Tv)` is an ordinary kotc
                // shape (a .NET generic's un-annotated member), and the owner-tv substituters that preserve this
                // wrapper (SupertypeGraph, InheritedClassInterfaceBridge, CovariantInterfaceReturnBridge, …) put the
                // instantiation's argument under it — a nullable one for a Kotlin type implementing such a member at
                // `String?`, whose bridge shape KotlinOverrideSlotBridge feeds straight back into `Compute`. Not
                // observed in the current corpus (see tests/ir/lowering/oblivious-over-nullable-byte, which is the
                // witness and records the measurement); nothing makes it unreachable.
                return Walk(o.Of, nullableHere: false, flags, isValue, obliviousHere: true);
            case TypeNode.Fqn f:
                // `kotlin.Unit` holds NO byte and takes no annotation, wherever it stands. It is the type ECMA `void`
                // projects to, and the reader answers `void` and `Unit` with one rule (dll2klib seeds `kotlin.Unit`
                // into its value-name set), so writing a byte here would put every later byte in the slot one position
                // off — `Pair<Unit, String?>` re-imported as `Pair<Unit!, String>`. It is a DotKt deviation from what
                // csc would flatten for the `Unit` CLASS, and it is stated as one in docs/dotkt-semantics.md § 9.
                if (f.Name == "kotlin.Unit") return false;
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
                var any = HeadIsNullable();
                if (f.Args != null)
                    foreach (var a in f.Args) any |= Walk(a, nullableHere: false, flags, isValue);
                return any;
            case TypeNode.Array a:
                flags.Add(Head());
                var anyA = HeadIsNullable();
                anyA |= Walk(a.Elem, nullableHere: false, flags, isValue);
                return anyA;
            case TypeNode.Fn:
                // A function type is a reference (delegate / object-erased state machine); its inner shape is not walked
                // for NRT. See the FUNCTION-TYPE note in the header: the reader DOES walk the delegate's type arguments,
                // so a function-typed slot must reach it through its `[KotlinType]` carrier, not through these bytes.
                flags.Add(Head());
                return HeadIsNullable();
            case TypeNode.Tv:
                // A type variable is treated as a reference position (bare + NRT byte); a struct-constrained tv would be
                // a value `Nullable<T>` but is resolved to a value elsewhere / erased by the object-erasure lifelines.
                flags.Add(Head());
                return HeadIsNullable();
            case TypeNode.ByRef b:
                // `ref T` is transparent for nullability — the referent's nullability is what matters.
                return Walk(b.Of, nullableHere, flags, isValue, obliviousHere);
            default:
                return false;
        }
    }
}
