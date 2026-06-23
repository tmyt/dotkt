// Public STATIC members of a normal injected class are surfaced on a synthesized companion, so they're reachable as
// `App.Companion.start(cb)` / `App.Companion.Count` (the backend emits .NET static calls; the lambda binds to the
// .NET delegate). RULE: the `.Companion` qualifier is REQUIRED — the current compiler doesn't resolve the implicit
// companion (`App.start`) of a plugin-generated class.
import Kfc.App
fun main() {
    App.Companion.start({ p -> println("p=" + p) })   // -> p=42
    println(App.Companion.Count)                       // -> 7
    println(App.Companion.Answer)                      // -> 99  (static FIELD, surfaced as a property -> ldsfld)
}
