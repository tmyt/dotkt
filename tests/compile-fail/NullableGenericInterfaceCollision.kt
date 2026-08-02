// #86 — the SUPERTYPE half of the overload collision carrier-argument erasure creates.
//
// A nullable possibly-value type argument is `System.Object`, so `Comparer<Int?>` and `Comparer<Long?>` are the same
// CLR interface `Comparer<object>`. A type cannot implement one interface twice, and the two `rank` bodies it owes
// have one slot between them — so the emitted type would silently run whichever the emitter wired.
//
// Note what does NOT collide, and must keep compiling: `Comparer<Int?>` beside `Comparer<Int>` stays two edges
// (`Comparer<object>` vs `Comparer<int32>`), because only the NULLABLE argument moves.
interface Comparer<T> { fun rank(x: T): Int }

class Ranker : Comparer<Int?>, Comparer<Long?> {
    override fun rank(x: Int?): Int = x ?: -1
    override fun rank(x: Long?): Int = (x ?: -1L).toInt()
}

fun main() {
    println(Ranker().rank(3))
}
