fun runB(f: () -> Unit) { f() }
fun main() { fromA(); runB { println("B1") } }
