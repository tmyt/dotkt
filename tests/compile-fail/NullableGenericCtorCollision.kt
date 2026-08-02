// #86 — the CONSTRUCTOR half of the overload collision carrier-argument erasure creates.
//
// `X?` for a possibly-value `X` is `System.Object` in every reified argument, so `List<Int?>` and `List<Long?>` are
// both an `IReadOnlyList<object>`. These two constructors are distinct to the Kotlin frontend and one
// `.ctor(IReadOnlyList<object>)` to the CLR: whichever the emitter binds wins every `Bag(...)` and the other is
// unreachable. A constructor has no generic arity of its own, so the erased parameter vector IS its whole key.
//
// Note what does NOT collide, and must keep compiling: `List<Int?>` beside `List<Int>` stays two signatures
// (`IReadOnlyList<object>` vs `IReadOnlyList<int32>`), because only the NULLABLE argument moves.
class Bag {
    constructor(xs: List<Int?>) { println("ints " + xs.size) }
    constructor(ys: List<Long?>) { println("longs " + ys.size) }
}

fun main() {
    Bag(listOf<Int?>(1, null))
    Bag(listOf<Long?>(1L, null))
}
