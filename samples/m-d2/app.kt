import clr.Api
import clr.Coro
import clr.await

// Drives real .NET async APIs (returning Task<T>) via the generic `.await()` interop point.
suspend fun compute(): Int {
	val task = Api.fetchAsync(30, 21)   // a real .NET Task<Int>
	val v = task.await()                // generic interop point -> await
	return v * 2
}

fun main() {
	println("result = ${Coro.run { compute() }}")
}
