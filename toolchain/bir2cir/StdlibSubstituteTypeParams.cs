using System.Text.Json.Nodes;

// #66 — kotc emits ONE substitute-INDEPENDENT BIR: the stdlib REFERENCE and RUNTIME builds get BIT-IDENTICAL type
// params (the pure-Kotlin shape, keeping a `kotlin.Comparable` upper bound and `in` declaration-site variance). The
// ref/rt divergence for those two is a SUBSTITUTION CONSEQUENCE, so it lives HERE — the RUNTIME stdlib build only
// (DOTKT_STDLIB_COMPILE + DOTKT_STDLIB_SUBSTITUTE). It reproduces exactly what BirEmitter.typeParamsJson used to do
// under `stdlibSubstitute`:
//
//   (1) DROP a `kotlin.Comparable<…>` upper bound. A substituted BCL primitive (Int32) does NOT implement
//       kotlin.Comparable, so `ClosedRange<Int>` would violate the constraint at CLR type-load; the body's compareTo
//       already dispatches through `constrained. System.IComparable<T>::CompareTo` (which primitives satisfy), and
//       runtime constraints are not enforced anyway (the app type-checked against the ref).
//   (2) DROP `in` (contravariant) declaration-site variance. The CLR's variance-validity check is stricter than
//       Kotlin's (e.g. `Continuation<in T>.resumeWith(Result<out T>)` — T in an input position — is rejected). Runtime
//       types don't need declaration-site variance (a compile-time concern; the ref.dll keeps it).
//
// Runs BEFORE BirTypeLowering so the constraint is still the pure `kotlin.Comparable` token (after lowering it would
// already be `System.IComparable`). After the drops, a name-only type param collapses back to the bare-string form
// kotc's rt build used to emit, so the emitted rt.dll is byte-identical.
static class StdlibSubstituteTypeParams
{
    public static void Apply(JsonNode node)
    {
        if (node is JsonObject o)
        {
            if (o["typeParams"] is JsonArray tps) Rewrite(tps);
            foreach (var kv in o) if (kv.Value != null) Apply(kv.Value);
        }
        else if (node is JsonArray a)
            foreach (var it in a) if (it != null) Apply(it);
    }

    static void Rewrite(JsonArray tps)
    {
        for (int i = 0; i < tps.Count; i++)
        {
            if (tps[i] is not JsonObject tp) continue;   // already a bare-string param — nothing to drop

            // (1) drop the kotlin.Comparable upper bound (keep every other bound).
            if (tp["constraints"] is JsonArray cs)
            {
                for (int j = cs.Count - 1; j >= 0; j--)
                    if (IsComparableFqn(cs[j])) cs.RemoveAt(j);
                if (cs.Count == 0) tp.Remove("constraints");
            }

            // (2) drop `in` declaration-site variance (keep `out`).
            if (Str(tp["variance"]) == "in") tp.Remove("variance");

            // Collapse a now name-only param back to the bare-string form kotc's rt build emitted.
            bool hasConstraints = tp["constraints"] is JsonArray rem && rem.Count > 0;
            bool hasVariance = tp["variance"] != null;
            if (!hasConstraints && !hasVariance && Str(tp["name"]) is string name)
                tps[i] = JsonValue.Create(name);
        }
    }

    static bool IsComparableFqn(JsonNode c) =>
        c is JsonObject co && Str(co["t"]) == "fqn" && Str(co["name"]) == "kotlin.Comparable";

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
