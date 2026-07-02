// Public STATIC members of a normal injected class are surfaced on a synthesized companion, reachable BOTH ways:
// IMPLICITLY as `App.start(cb)` / `App.Count` (kotc eagerly links the generated companion via
// replaceCompanionObjectSymbol + sets the FIR-internal ownerGenerator — see ClrTypeInjection.kt/FirInternals.java),
// and explicitly as `App.Companion.start(cb)`. Both forms produce IDENTICAL BIR (the backend emits .NET static calls;
// the lambda binds to the .NET delegate).
import Kfc.App
fun main() {
    // implicit (no .Companion) — the form .NET code naturally reads as
    App.start({ p -> println("p=" + p) })             // -> p=42
    println(App.Count)                                 // -> 7
    println(App.Answer)                                // -> 99  (static FIELD, surfaced as a property -> ldsfld)
    println(App.Magic)                                 // -> 123 (const/literal FIELD -> inlined value)
    // explicit .Companion — regression coverage for the original form
    App.Companion.start({ p -> println("p=" + p) })   // -> p=42
    println(App.Companion.Count)                       // -> 7
    println(App.Companion.Answer)                      // -> 99
    println(App.Companion.Magic)                       // -> 123
}
