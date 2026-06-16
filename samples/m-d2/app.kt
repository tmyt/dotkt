import clr.Api
import clr.Coro
import clr.await

suspend fun compute(): Int {
	val v = Api.fetchAsync(20, 21).await()
	return v * 2
}

suspend fun sumAsync(n: Int): Int {
	var total = 0
	var i = 1
	while (i <= n) {
		total = total + Api.fetchAsync(2, i).await()
		i = i + 1
	}
	return total
}

suspend fun safe(): Int {
	try {
		return Api.failAsync().await()
	} catch (e: Exception) {
		return -1
	}
}

fun main() {
	println("result = ${Coro.run { compute() }}")
	println("sum = ${Coro.run { sumAsync(5) }}")
	println("safe = ${Coro.run { safe() }}")
}
