// #199-① regression (roundtrip lane), half B — the same-simple-name twin of GenclashA.kt's `Cell<T>` in a DIFFERENT
// package. See GenclashA.kt for the full rationale: dll2klib must keep `roundtrip.genclash.a.Cell` and
// `roundtrip.genclash.b.Cell` distinct through namespace-qualified KLIB identities.
package roundtrip.genclash.b

class Cell<T>(var value: T) { fun boxed(): T = value }

fun <T> cellB(v: T): Cell<T> = Cell(v)
