import clr.Api2
import clr.Coro
import clr.await

@clr.Sm
suspend fun chain(): Int {
	val a = Api2.step(10).await()
	val b = Api2.step(20).await()
	return a + b
}

// Parameterized suspend fun -> state machine with the param stored as a field.
@clr.Sm
suspend fun fetchDouble(n: Int): Int {
	val a = Api2.step(n).await()
	return a * 2
}

fun main() {
	println("chain = ${Coro.run { chain() }}")
	println("fetchDouble(7) = ${Coro.run { fetchDouble(7) }}")
}
