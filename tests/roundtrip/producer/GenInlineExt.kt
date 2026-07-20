// Migrated verify-roundtrip.sh section `roundtrip-generic-inline-ext` (#133) — the library half.
// A generic INLINE EXTENSION on a generic receiver: `c.update { it + 1 }` must infer T=Int from `c: Cell<Int>`
// and splice the lambda cross-module (the atomicfu-port fidelity regression, fixed by 299ba89).
package roundtrip.gie

class Cell<T>(var v: T)
inline fun <T> Cell<T>.update(fn: (T) -> T) { v = fn(v) }   // generic inline ext on a generic receiver
