// #26: a cross-module Kotlin LIBRARY whose package FQN STARTS WITH `dotkt` (`dotktx.foo.bar`) but is NOT the
// compiler's own `dotkt`/`dotkt$...` vocabulary. This is the exact shape of the reporter's `dotktx.ui.avalonia`
// windowing lib. The app captures a local of this lib's type (`State<T>`) inside a lambda that is stored as a
// delegate and invoked later. Before the #26 fix, bir2cir's ResolveNetType matched the owner FQN with a bare
// `StartsWith("dotkt")` — so `dotktx.foo.bar.State` was wrongly classified as "not a .NET/reference type", the
// cross-module reference type was mishandled, and the captured local read back NULL at runtime (NRE), even though
// compile stayed clean. The fix makes the guard match `dotkt` only as a complete leading segment (`dotkt`/`dotkt.`/
// `dotkt$`), never as a prefix of a longer identifier like `dotktx`.
package dotktx.foo.bar

class State<T>(var value: T)

fun <T> state(x: T): State<T> = State(x)

private var stored: (() -> Unit)? = null

fun register(cb: () -> Unit) { stored = cb }

fun fire() { stored?.invoke() }
