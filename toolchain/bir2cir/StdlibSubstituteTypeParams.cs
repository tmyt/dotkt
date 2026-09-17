using System.Text.Json.Nodes;

// #66 — kotc emits ONE substitute-INDEPENDENT BIR: the stdlib REFERENCE and RUNTIME builds get BIT-IDENTICAL type
// params (the pure-Kotlin shape, keeping a `kotlin.Comparable` upper bound and `in` declaration-site variance). The
// ref/rt divergence for those two is a SUBSTITUTION CONSEQUENCE, so it lives HERE — the RUNTIME stdlib build only
// (`--build-stdlib=runtime`, `SubstituteStdlibBuild`). It reproduces exactly what BirEmitter.typeParamsJson used to do
// under `stdlibSubstitute`:
//
//   (1) DROP every interface type-param constraint in the executable runtime CIR. Kotlin/frontend constraints remain
//       complete in the metadata/reference CIR, but reifying the same bound on a CLR interface type param is unsafe for
//       generic DIM forwarding: CoreCLR validates a nested `Key<!!E>` using its shared `object` canon before substituting
//       the forwarding method's `E : Element`, and rejects the valid MethodSpec as `Key<object>`. Method constraints are
//       untouched; only the runtime interface TYPE declaration drops the duplicate CLR constraint.
//   (2) DROP `in` (contravariant) declaration-site variance. The CLR's variance-validity check is stricter than
//       Kotlin's (e.g. `Continuation<in T>.resumeWith(Result<out T>)` — T in an input position — is rejected). Runtime
//       types don't need declaration-site variance (a compile-time concern; the ref.dll keeps it).
//
// Class and method bounds remain intact: BirTypeLowering projects them to their exact CLR declarations, including
// IComparable<T>. Removing such a bound after constrained-call selection invalidates that call's verifier proof.
// After the drops, a name-only type param collapses to the BIR schema's bare-string
// shorthand; the object and shorthand forms have the same constraint-free CLR meaning.
static class StdlibSubstituteTypeParams
{
    public static void Apply(JsonNode node)
    {
        if (node is JsonObject o)
        {
            if (o["typeParams"] is JsonArray tps) Rewrite(tps, Str(o["kind"]) == "interface");
            foreach (var kv in o) if (kv.Value != null) Apply(kv.Value);
        }
        else if (node is JsonArray a)
            foreach (var it in a) if (it != null) Apply(it);
    }

    static void Rewrite(JsonArray tps, bool runtimeInterface)
    {
        for (int i = 0; i < tps.Count; i++)
        {
            if (tps[i] is not JsonObject tp) continue;   // already a bare-string param — nothing to drop

            // Interface constraints follow the existing DIM declaration policy. Class/method bounds are not
            // substitution-incompatible merely because they still use Kotlin names before type lowering.
            if (runtimeInterface && tp["constraints"] is JsonArray cs)
            {
                cs.Clear();
                if (cs.Count == 0) tp.Remove("constraints");
            }

            // (2) drop `in` declaration-site variance (keep `out`).
            if (Str(tp["variance"]) == "in") tp.Remove("variance");

            // Normalize a now name-only declaration to the BIR schema's bare-string shorthand.
            bool hasConstraints = tp["constraints"] is JsonArray rem && rem.Count > 0;
            bool hasVariance = tp["variance"] != null;
            if (!hasConstraints && !hasVariance && Str(tp["name"]) is string name)
                tps[i] = JsonValue.Create(name);
        }
    }

    internal static void SelfTest()
    {
        var root = JsonNode.Parse("""
        {"methods":[{"name":"compare","typeParams":[{"name":"T","constraints":[
          {"t":"fqn","name":"kotlin.Comparable","args":[{"t":"tv","scope":"method","i":0}]}]}]}],
         "types":[{"kind":"class","name":"Range","typeParams":[{"name":"T","constraints":[
          {"t":"fqn","name":"kotlin.Comparable","args":[{"t":"tv","scope":"type","i":0}]}]}]}]}
        """);
        var expected = root.DeepClone();
        Apply(root);
        if (!JsonNode.DeepEquals(root, expected))
            throw new System.InvalidOperationException("Runtime substitution removed a class or method bound required by constrained dispatch");
        System.Console.WriteLine("[runtime generic bounds] self-test OK (class and method CLR constraint proof retained)");
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
