import clr.Coro

// A Kotlin suspend function that awaits real .NET async operations (non-blocking).
suspend fun compute(): Int {
	Coro.delay(20)
	val v = Coro.fetchValue(30, 21)
	return v * 2
}

fun main() {
	val result = Coro.run { compute() }
	println("result = $result")
}
