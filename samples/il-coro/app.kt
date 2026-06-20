// Phase 3 — coroutines (suspend fun) lowered to a CLR-native async state machine in pure IL (strategy B).
// suspend fun f(): T  <=>  Task<T> f()  (the Continuation is never exposed; ABI is coroutine-abi-decision).
// Each `.await()` is a suspension point; live locals/params become struct fields; if/while linearize.
import clr.Api2
import clr.Coro
import clr.await

// Linear: two awaits in sequence; `a` survives across the second suspension as a field.
suspend fun chain(): Int {
    val a = Api2.step(10).await()
    val b = Api2.step(20).await()
    return a + b
}

// Single await + arithmetic on a param (param `n` becomes a field).
suspend fun fetchDouble(n: Int): Int {
    val a = Api2.step(n).await()
    return a * 2
}

// Direct suspend call: awaiting ANOTHER coroutine (chain()'s kickoff Task).
suspend fun useChain(): Int {
    val c = chain()
    return c + 5
}

// Suspension INSIDE A LOOP — `acc`/`i` must survive every iteration as fields.
suspend fun sumLoop(n: Int): Int {
    var acc = 0
    var i = 0
    while (i < n) {
        val s = Api2.step(i).await()
        acc = acc + s
        i = i + 1
    }
    return acc
}

// Suspension INSIDE A BRANCH — a different number of suspension points per path.
suspend fun branch(flag: Boolean): Int {
    val x = Api2.step(10).await()
    if (flag) {
        val y = Api2.step(5).await()
        return x + y
    } else {
        return x
    }
}

fun twice(n: Int): Int = n * 2

// SPILLING — two awaits in ONE expression. `Api2.step(10).await()` is hoisted into a field that must survive
// the SECOND suspension (`Api2.step(20).await()`); the residual `__sp0 + __sp1` is suspension-free.
suspend fun spillSum(): Int {
    return Api2.step(10).await() + Api2.step(20).await()   // 10 + 20 = 30
}

// SPILLING — awaits nested inside arithmetic, in a val initializer.
suspend fun spillNested(): Int {
    val x = Api2.step(7).await() * 2 + Api2.step(3).await()   // 14 + 3 = 17
    return x
}

// SPILLING — an await as the ARGUMENT to an ordinary (non-suspend) function.
suspend fun spillArg(): Int {
    return twice(Api2.step(8).await())   // twice(8) = 16
}

// CONDITION-POSITION suspend — the WHILE condition itself awaits, re-suspending every iteration.
suspend fun loopCond(n: Int): Int {
    var i = 0
    var acc = 0
    while (Api2.step(i).await() < n) {   // await IN the loop condition (step(i) echoes i)
        acc = acc + i
        i = i + 1
    }
    return acc
}

// CONDITION-POSITION suspend in a statement-`if` whose BRANCH also suspends (emitWhenCps cond-spill + coCondGoto).
suspend fun condBranch(): Int {
    if (Api2.step(1).await() > 0) {      // await in the if condition
        val y = Api2.step(5).await()     // await in the branch
        return y + 1                     // 5 + 1 = 6
    }
    return -1
}

// TRY/CATCH around await — happy path: the try body's await succeeds, returns from the try.
suspend fun tryOk(): Int {
    try {
        val a = Api2.step(10).await()
        return a + 1                    // 11
    } catch (e: Exception) {
        return -1
    }
}

// TRY/CATCH around await — the awaited task FAULTS; GetResult() throws inside the .try and the catch runs.
suspend fun tryCatch(): Int {
    try {
        val a = Api2.boom(5).await()    // faults after suspending
        return a                        // unreached
    } catch (e: Exception) {
        return -99
    }
}

// TRY/CATCH with FALL-THROUGH (neither try nor catch returns; control continues after the try).
suspend fun tryFallthrough(): Int {
    var x = 0
    try {
        x = Api2.boom(1).await()        // faults
    } catch (e: Exception) {
        x = 7
    }
    return x + 1                        // 8
}

fun main() {
    println("tryOk = ${Coro.run { tryOk() }}")
    println("tryCatch = ${Coro.run { tryCatch() }}")
    println("tryFallthrough = ${Coro.run { tryFallthrough() }}")
    println("loopCond = ${Coro.run { loopCond(3) }}")
    println("condBranch = ${Coro.run { condBranch() }}")
    println("spillSum = ${Coro.run { spillSum() }}")
    println("spillNested = ${Coro.run { spillNested() }}")
    println("spillArg = ${Coro.run { spillArg() }}")
    println("chain = ${Coro.run { chain() }}")
    println("fetchDouble(7) = ${Coro.run { fetchDouble(7) }}")
    println("useChain = ${Coro.run { useChain() }}")
    println("sumLoop(4) = ${Coro.run { sumLoop(4) }}")
    println("branch(true) = ${Coro.run { branch(true) }}")
    println("branch(false) = ${Coro.run { branch(false) }}")
}
