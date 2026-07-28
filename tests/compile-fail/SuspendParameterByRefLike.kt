// A suspend function PARAMETER of byref-like type. A parameter is unconditional state-machine storage (the SM
// constructor writes it), so it is refused as soon as the body actually suspends — the C# CS4012 mirror. A
// suspend function with no suspension point keeps its parameters in the cold entry's own frame and is accepted
// (see tests/coroutines/fixtures/ByRefLikeStorageTests.kt).
import System.Span

suspend fun cfParamTick(n: Int): Int = n + 1

suspend fun cfConsume(s: Span<Int>): Int {
    val t = cfParamTick(1)
    return s.Length + t
}

suspend fun main() {
    println(cfConsume(Span<Int>(arrayOf(1, 2))))
}
