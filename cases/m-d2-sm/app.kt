import clr.Api2
import clr.Coro
import clr.await

@clr.Sm
suspend fun chain(): Int {
	val a = Api2.step(10).await()
	val b = Api2.step(20).await()
	return a + b
}

@clr.Sm
suspend fun fetchDouble(n: Int): Int {
	val a = Api2.step(n).await()
	return a * 2
}

// Coroutine composition: a state-machine coroutine awaits ANOTHER coroutine (direct suspend call).
@clr.Sm
suspend fun useChain(): Int {
	val c = chain()
	return c + 5
}

// Suspension INSIDE A LOOP — impossible for the old linear codegen; needs real CPS lowering.
@clr.Sm
suspend fun sumLoop(n: Int): Int {
	var acc = 0
	var i = 0
	while (i < n) {
		val s = Api2.step(i).await()   // suspends every iteration; `acc`/`i` must survive as fields
		acc = acc + s
		i = i + 1
	}
	return acc
}

// Suspension INSIDE A BRANCH — a different number of suspension points per path.
@clr.Sm
suspend fun branch(flag: Boolean): Int {
	val x = Api2.step(10).await()
	if (flag) {
		val y = Api2.step(5).await()
		return x + y
	} else {
		return x
	}
}

fun main() {
	println("chain = ${Coro.run { chain() }}")
	println("fetchDouble(7) = ${Coro.run { fetchDouble(7) }}")
	println("useChain = ${Coro.run { useChain() }}")
	println("sumLoop(4) = ${Coro.run { sumLoop(4) }}")
	println("branch(true) = ${Coro.run { branch(true) }}")
	println("branch(false) = ${Coro.run { branch(false) }}")
}
