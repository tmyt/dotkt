// Public STATIC members of a normal injected class are surfaced on a synthesized companion, so they're reachable as
// `App.Companion.start(cb)` / `App.Companion.Count` (the backend emits .NET static calls; the lambda binds to the
// .NET delegate). NOTE: the bare `App.start` form needs the implicit-companion resolution fix (tracked separately).
import Kfc.App
fun main() {
    App.Companion.start({ p -> println("p=" + p) })   // -> p=42
    println(App.Companion.Count)                       // -> 7
}
