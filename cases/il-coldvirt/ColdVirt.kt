// bir2cir SuspendColdLowering P5 A1b — a suspending instance member of a GENERIC class over the cold
// core. Exercises piece 1 (generic-class instance-member SM): the SM `Box_getTwice$sm[T]` is generic
// over the enclosing class's `T`, its `$this` field is the CONSTRUCTED self `Box[gp:T]`, and the awaited
// value crosses the suspension typed in `T`. The cold entry is an instance `getTwice$dotkt_suspend[](…)`
// on `Box<T>`; drained by the synthesized plain `main` (sync completion).
//
// (The virtual/abstract/OVERRIDE cold-entry piece — SequenceScope/SequenceBuilderIterator — is verified at
// the CIR + rt-stdlib-emit level; its full RUNTIME E2E is blocked by an orthogonal, pre-existing ilemit
// gap in generic-class INHERITANCE construction (`class D<T> : Base<T>()` crashes in the base ctor,
// "not fully instantiated", even with no suspend), so it is not driven from a user fixture here.)

suspend fun <T> echo(x: T): T = x        // a generic top-level cold entry (the member's suspension target)

class Box<T>(val v: T) {
    suspend fun getTwice(): T {
        val a = echo(v)                  // a suspend call -> the member's generic SM suspends here
        return a
    }
}

suspend fun main() {
    println(Box(42).getTwice())          // T = Int (value type through the SM's T-typed field)
    println(Box("hi").getTwice())        // T = String (reference type)
}
