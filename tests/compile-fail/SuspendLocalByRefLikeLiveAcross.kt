// A byref-like (`ref struct`) local still needed AFTER a suspension. The state machine would have to hold it in
// an instance field across the resume, which the CLR refuses for a `ref struct` — so the compiler refuses it
// instead (the C# CS4007 mirror). Contrast tests/coroutines/fixtures/ByRefLikeStorageTests.kt, where the same
// type is created and consumed entirely between suspensions and stays a MoveNext local.
import System.Span

suspend fun cfTick(n: Int): Int = n + 1

suspend fun cfLiveAcross(): Int {
    val s = Span<Int>(arrayOf(1, 2, 3))
    val t = cfTick(4)
    return s.Length + t
}

suspend fun main() {
    println(cfLiveAcross())
}
