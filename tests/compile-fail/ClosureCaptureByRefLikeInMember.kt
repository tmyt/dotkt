// The same closure-capture refusal reached from a CLASS MEMBER, and through a lambda stored in a value rather
// than passed straight to a call — the fault is the capture, not the shape of the call around it.
import System.Span

class CfBox(val n: Int) {
    fun pick(): Int {
        val s = Span<Int>(arrayOf(1, 2, 3, 4))
        val f: () -> Int = { s.Length + n }
        return f()
    }
}

fun main() {
    println(CfBox(1).pick())
}
