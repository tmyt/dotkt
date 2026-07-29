// A PLAIN (non-suspend) lambda capturing a byref-like value. No coroutine is involved: a capture is always heap
// storage — a field of the synthesized closure class — so the refusal needs no liveness question. The C# CS8352
// mirror.
import System.Span

fun cfHold(f: () -> Int): Int = f()

fun cfCapture(): Int {
    val s = Span<Int>(arrayOf(1, 2, 3))
    return cfHold { s.Length }
}

fun main() {
    println(cfCapture())
}
