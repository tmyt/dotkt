// A cross-module Kotlin library in the ordinary user package `dotkt.foo.bar`. Only the unspeakable
// `dotkt$...` generated-type prefix is compiler-owned; the `dotkt.*` namespace must resolve normally.
package dotkt.foo.bar

// `Signal` (not `State`) so the simple name is UNIQUE across this shared producer assembly — a same-simple-name
// collision with another package's generic type must not alter the generic member identity.
class Signal<T>(var value: T)

fun <T> state(x: T): Signal<T> = Signal(x)

private var stored: (() -> Unit)? = null

fun register(cb: () -> Unit) { stored = cb }

fun fire() { stored?.invoke() }
