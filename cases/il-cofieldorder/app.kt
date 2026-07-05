// N4 (bir2cir SuspendColdLowering, eval-order across a suspension — the FIELD-read case): in `x + bump()`
// where `bump()` is a suspend call that MUTATES the member field `x`, the LEFT operand `x` must be read
// BEFORE bump() runs (Kotlin is strict left-to-right). A raw backing-field / `@ClrField` read was mis-classed
// PURE, so it was left inline and evaluated AFTER bump()'s suspension resumed -> it observed the POST-mutation
// value (a miscompile). A source-level PROPERTY read went through a getter (impure) and was already correct;
// only the direct field read slipped through. bir2cir now spills a `field`/`staticField`/`lateinitGet` read
// that sits left of a suspension into an SM temp before the suspension. `@ClrField var x` is a plain CLR field,
// so its read is a raw `field` node (no getter) — exactly the affected shape. Drained by the synthesized plain
// `main` (relay() completes synchronously, so the reorder is observable purely through the field mutation).
annotation class ClrField   // recognized by short name -> keeps the sample standalone

suspend fun relay(): Int { return 5 }   // a suspend call that completes synchronously

class Box {
    @ClrField var x: Int = 10           // plain CLR field -> a raw `field` read (no property getter)

    suspend fun bump(): Int {           // MUTATES x, then suspends (so `x + bump()` has a suspension on the right)
        x = 100
        return relay()
    }

    suspend fun compute(): Int {
        return x + bump()               // x read LEFT of the suspending bump() -> must be 10, not 100
    }
}

suspend fun main() {
    val b = Box()
    println(b.compute())                // expect 10 + 5 = 15 (a miscompile prints 105)
    println(b.x)                         // 100 — bump() did run and mutate the field
}
