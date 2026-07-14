// #26 APP: consumes the re-imported cross-module `dotktx.foo.bar` Kotlin library through a <ProjectReference>.
// `val c = state(0)` binds a local of the cross-module type `State<Int>`; the lambda captures `c` and is stored
// as a delegate via `register`; `fire()` invokes it cross-module. Reading `c.value` back through the captured
// field must yield the updated value — before the #26 fix the captured `c` was NULL through the stored delegate
// (NRE) because the `dotktx.*` owner FQN was mis-classified by bir2cir's over-broad `StartsWith("dotkt")` guard.
import dotktx.foo.bar.State
import dotktx.foo.bar.state
import dotktx.foo.bar.register
import dotktx.foo.bar.fire

fun main() {
    val c: State<Int> = state(0)
    register { c.value = c.value + 1 }
    fire()
    fire()
    println(c.value)   // 2  — captured cross-module local survives through the stored delegate
}
