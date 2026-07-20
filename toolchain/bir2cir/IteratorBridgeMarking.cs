using System.Linq;
using System.Text.Json.Nodes;

// #139 — REVERSE-ENUMERATOR-BRIDGE semantic markers. ilemit knows NO Kotlin, so the Kotlin knowledge "the
// `kotlin.collections.Iterator` interface with hasNext()/next() is THE shape the reverse GetEnumerator bridge wraps,
// and a class's `iterator()` is what feeds it" lives HERE (the Kotlin<->CLR relation layer), not in ilemit. This pass
// stamps a per-method `clrBridgeRole` marker onto the exact nodes ilemit's retired Kotlin-name predicates keyed on:
//
//   * "hasNext" / "next"  on kotlin.collections.Iterator's own two members — the adapter's MoveNext/Current sources.
//                          Only the BASE Iterator (not MutableIterator): the adapter wraps `Iterator<T>`.
//   * "iterator"          on every class/interface member method named `iterator` — the `this.iterator()` call the
//                          synthesized GetEnumerator makes. Predicate is NAME-only, mirroring ilemit's retired
//                          `ti.Methods["iterator"]` lookup EXACTLY (its interface/EnumerableDerived guard stays in
//                          ilemit) so the emitted IL is byte-identical. Top-level (file-class) functions live in the
//                          root `methods`, not in `types`, so a top-level `fun iterator()` is never marked — matching
//                          ilemit, whose GetEnumerator bridge fires only on non-file, non-interface classes.
//
// ilemit's EmitEnumeratorAdapter / GenerateGetEnumeratorIfNeeded read this marker instead of the Kotlin FQN/member
// strings. The marker is a bir2cir->ilemit CIR hint (never a .NET custom attribute), so it never reaches the emitted
// dll — the assembly bytes are unchanged. Runs in ALL builds (ref/rt/app), on the fully-lowered tree (the type NAME
// and the un-aliased Iterator FQN survive lowering), so ref.dll / rt.dll / app all mark the same nodes.
static class IteratorBridgeMarking
{
    const string IteratorFqn = "kotlin.collections.Iterator";

    public static void Apply(JsonNode root)
    {
        if (root is not JsonObject o || o["types"] is not JsonArray types) return;
        foreach (var t in types)
        {
            if (t is not JsonObject to || to["methods"] is not JsonArray methods) continue;
            var typeName = (to["name"] as JsonValue)?.GetValue<string>();
            foreach (var m in methods.OfType<JsonObject>())
            {
                var mName = (m["name"] as JsonValue)?.GetValue<string>();
                if (typeName == IteratorFqn && mName is "hasNext" or "next")
                    m["clrBridgeRole"] = mName;                 // the wrapped Kotlin iterator's MoveNext/Current sources
                else if (mName == "iterator")
                    m["clrBridgeRole"] = "iterator";            // the this.iterator() feeding a synthesized GetEnumerator
            }
        }
    }
}
