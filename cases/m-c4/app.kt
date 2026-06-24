// C-1: repeat + use (IDisposable via try/finally).
class Res(val tag: String) : AutoCloseable {
	fun work(): Int { println("work $tag"); return tag.length }
	override fun close() { println("closed $tag") }
}
fun main() {
	repeat(3) { println("i=$it") }
	val n = Res("db").use { it.work() }
	println("n=$n")
}
