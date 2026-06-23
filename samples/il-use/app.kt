// `use {}` on a Closeable/AutoCloseable -> try { block(it) } finally { close()/Dispose() }, returning the block's
// value (the CLR analogue of C# `using`). close() maps to IDisposable.Dispose().
class Res(val tag: String) : AutoCloseable {
    fun read(): Int = tag.length
    override fun close() { println("close " + tag) }
}
fun main() {
    val n = Res("abcd").use { it.read() }   // block value returned; close runs in finally first
    println("n=" + n)                        // close abcd / n=4
    try {
        Res("x").use { throw RuntimeException("boom") }   // close still runs on throw
    } catch (e: Exception) { println("caught:" + e.message) }   // close x / caught:boom
}
