import clr.Coro

// A Kotlin suspend function that drives a real .NET async operation and awaits its result.
suspend fun compute(): Int {
	val task = Coro.delayThenValue(50, 21)
	return task.value * 2
}

fun main() {
	val result = Coro.run { compute() }
	println("result = $result")
}
