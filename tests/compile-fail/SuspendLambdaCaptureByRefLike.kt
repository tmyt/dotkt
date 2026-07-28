// A SUSPEND LAMBDA capturing a byref-like value. The capture becomes a field of the lambda's state-machine
// class, which the CLR refuses — the CS4012 mirror, reached through the same storage gate as a parameter.
import System.Span

suspend fun cfCapTick(n: Int): Int = n + 1

fun cfBuild(): suspend () -> Int {
    val s = Span<Int>(arrayOf(1, 2, 3))
    return { s.Length + cfCapTick(1) }
}

suspend fun main() {
    println(cfBuild()())
}
