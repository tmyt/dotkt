using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using DotKt.Bir;

// The flattened NullableAttribute (NRT) byte walk, shared across the decl-position NRT collection (params / method
// returns / fields / properties) and the suspend Task-bridge return (#37/#48 nullability fold). A reference type's
// `?` no longer rides a decl-level scalar flag nor a `System.Nullable<>` wrapper — it is stripped to the bare type by
// BirTypeLowering, and its nullability is carried HERE as a `NullableAttribute` byte array (ilemit stamps it verbatim;
// facadegen reads it back). One byte per reference type NODE in pre-order (0 = oblivious, 1 = non-null, 2 = nullable);
// a VALUE type / struct-constrained tv contributes NO byte (it is the structural `Nullable<T>`, not an NRT annotation).
//
// This is the promoted, oracle-keyed generalization of SuspendColdLowering's former private `WalkNullable` +
// `ValueTypeFqns` (which only ever handled the Task<R> return). The value-ness decision is the struct-ness ORACLE
// (ReferenceMetadataIndex.IsValueTypeFqn + local enum/struct types), not a hardcoded FQN set.
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
    // that THIS position was reached through an outer `{t:nullable}` wrapper (so the head node's own byte is 2).
    static bool Walk(TypeNode t, bool nullableHere, List<int> flags, Func<string, bool> isValue)
    {
        switch (t)
        {
            case TypeNode.Nullable n:
                // The wrapper is not itself a node — it marks the wrapped type as nullable.
                return Walk(n.Of, nullableHere: true, flags, isValue);
            case TypeNode.Oblivious o:
                // NRT-oblivious reference (NullableAttribute = 0). kotc never emits this; facadegen META round-trip only.
                flags.Add(0);
                var anyOb = false;
                if (o.Of is TypeNode.Fqn { Args: { } oargs })
                    foreach (var a in oargs) anyOb |= Walk(a, nullableHere: false, flags, isValue);
                return anyOb;
            case TypeNode.Fqn f:
                // A value type contributes NO byte and does not recurse (its args are erased for NRT purposes — the
                // structural Nullable<T> / value generic carries no reference-nullability). Mirrors csc's flattening.
                if (isValue(f.Name)) return false;
                flags.Add(nullableHere ? 2 : 1);
                var any = nullableHere;
                if (f.Args != null)
                    foreach (var a in f.Args) any |= Walk(a, nullableHere: false, flags, isValue);
                return any;
            case TypeNode.Array a:
                flags.Add(nullableHere ? 2 : 1);
                var anyA = nullableHere;
                anyA |= Walk(a.Elem, nullableHere: false, flags, isValue);
                return anyA;
            case TypeNode.Fn:
                // A function type is a reference (delegate / object-erased state machine); its inner shape is not walked
                // for NRT (the erased CLR type is a single reference node).
                flags.Add(nullableHere ? 2 : 1);
                return nullableHere;
            case TypeNode.Tv:
                // A type variable is treated as a reference position (bare + NRT byte); a struct-constrained tv would be
                // a value `Nullable<T>` but is resolved to a value elsewhere / erased by the object-erasure lifelines.
                flags.Add(nullableHere ? 2 : 1);
                return nullableHere;
            case TypeNode.ByRef b:
                // `ref T` is transparent for nullability — the referent's nullability is what matters.
                return Walk(b.Of, nullableHere, flags, isValue);
            default:
                return false;
        }
    }
}
