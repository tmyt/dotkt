// N5 — same-name same-package top-level overloads restored from DIFFERENT .NET file facades (UtilsKt.foo() vs
// HelpersKt.foo(Int)). They share CallableId(N5, "foo"); the A2 interop-no-registry flat `Map<CallableId,String>`
// collapsed to last-put-wins and mis-routed one call to the wrong file class. The overload-aware key disambiguates
// each by the resolved callee's value-param arity, so both route to their OWN file class (1:1).
import N5.*

fun main() {
    println(foo())      // -> N5.UtilsKt.foo()      = 100
    println(foo(41))    // -> N5.HelpersKt.foo(41)  = 42
}
