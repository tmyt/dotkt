// ktproj-dotktpkg (#26 follow-up): a cross-module Kotlin LIBRARY in the ordinary user package `dotkt.foo.bar`.
// The pre-stdlib compiler-intrinsics runtime once used the `dotkt.*` space, but that runtime is retired; only the
// unspeakable `dotkt$...` generated-type prefix remains compiler-owned. ResolveNetType/ResolveRefType nevertheless
// skipped the complete `dotkt` namespace, so a referenced type here was never reflected. The consumer captures a
// local of this library's `Signal<Int>` inside a stored delegate — the same compile-clean NRE/InvalidProgram shape
// that #26 exposed for the formerly over-broad `StartsWith("dotkt")` treatment of `dotktx.*` user packages.
package dotkt.foo.bar

// `Signal` (not `State`) so the simple name is UNIQUE across this shared producer assembly — a same-simple-name
// collision with another package's generic type breaks facadegen's re-import of the generic member (the `var value`
// mutability / generic factory return degrade). The case tests that `dotkt.*` is an ordinary package, not the name.
class Signal<T>(var value: T)

fun <T> state(x: T): Signal<T> = Signal(x)

private var stored: (() -> Unit)? = null

fun register(cb: () -> Unit) { stored = cb }

fun fire() { stored?.invoke() }
