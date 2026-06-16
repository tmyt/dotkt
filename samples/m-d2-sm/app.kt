import clr.Api2
import clr.Coro
import clr.await

// Compiled to a state machine on the coroutine runtime (NOT C# async/await) — strategy B.
@clr.Sm
suspend fun chain(): Int {
	val a = Api2.step(10).await()
	val b = Api2.step(20).await()
	return a + b
}

fun main() {
	println("chain = ${Coro.run { chain() }}")
}
