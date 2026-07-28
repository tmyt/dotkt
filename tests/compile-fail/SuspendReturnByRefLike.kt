// A suspend function RETURNING a byref-like value. At the call site the awaited result is written at the resume
// point and read after it, so it is state-machine storage by construction and cannot be a `ref struct`.
import System.Span

suspend fun cfRetTick(n: Int): Int = n + 1

suspend fun cfMake(): Span<Int> {
    val t = cfRetTick(1)
    return Span<Int>(arrayOf(t))
}

suspend fun main() {
    println(cfMake().Length)
}
