// ktproj-dotktpkg (#26): a cross-module Kotlin LIBRARY whose package FQN STARTS WITH `dotkt` (`dotktx.foo.bar`)
// but is a USER package, NOT the compiler's own `dotkt`/`dotkt$...` synthetic vocabulary — the exact shape of the
// reporter's `dotktx.ui.avalonia` windowing lib. The consumer captures a local of this lib's `State<Int>` inside a
// lambda stored as a delegate and fired later cross-module. Before the #26 fix bir2cir's ResolveNetType matched the
// owner FQN with a bare `StartsWith("dotkt")`, so `dotktx.foo.bar.State` was wrongly classified as "not a .NET/
// reference type" — the captured cross-module local read back NULL (NRE) even though compile stayed clean. The guard
// now matches `dotkt` only as a complete leading segment (`dotkt`/`dotkt.`/`dotkt$`), never a prefix of `dotktx`.
package dotktx.foo.bar

// `Signal` (not `State`) so the simple name is UNIQUE across this shared producer assembly — a same-simple-name
// collision with another package's generic type breaks facadegen's re-import of the generic member (the `var value`
// mutability / generic factory return degrade). The case tests the dotkt-PREFIX package guard (#26), not the name.
class Signal<T>(var value: T)

fun <T> state(x: T): Signal<T> = Signal(x)

private var stored: (() -> Unit)? = null

fun register(cb: () -> Unit) { stored = cb }

fun fire() { stored?.invoke() }
