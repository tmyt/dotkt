// B (via b/LINQ): Kotlin collection operations map/filter/forEach with value-returning lambdas.
fun main() {
	val xs = listOf(1, 2, 3, 4)
	xs.map { it * 2 }.forEach { println(it) }
	println("evens=${xs.filter { it % 2 == 0 }.size}")
}
