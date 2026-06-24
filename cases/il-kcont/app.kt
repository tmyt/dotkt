// Phase 1 — the Continuation-CORE coroutine form (Path B): @KCont lowers a suspend fun to a CLASS implementing
// DotKt.Coroutines.Continuation<object> (resumeWith/invokeSuspend + label), driven via the Task sink (NewRoot).
// Same observable behavior as the struct/IAsyncStateMachine form — proves the new codegen + shared runtime.
import clr.Api2
import clr.Coro
import clr.KCont
import clr.await

@KCont suspend fun chainK(): Int {
    val a = Api2.step(10).await()
    val b = Api2.step(20).await()
    return a + b                       // 30
}

@KCont suspend fun fetchDoubleK(n: Int): Int {
    val a = Api2.step(n).await()
    return a * 2                       // n*2
}

@KCont suspend fun sumLoopK(n: Int): Int {
    var acc = 0
    var i = 0
    while (i < n) {
        acc = acc + Api2.step(i).await()
        i = i + 1
    }
    return acc                         // 0+1+2+3 = 6
}

@KCont suspend fun branchK(flag: Boolean): Int {
    val x = Api2.step(10).await()
    if (flag) {
        val y = Api2.step(5).await()
        return x + y                   // 15
    }
    return x                           // 10
}

@KCont suspend fun tryCatchK(): Int {
    try {
        val a = Api2.boom(5).await()   // faults after suspending
        return a
    } catch (e: Exception) {
        return -99
    }
}

fun main() {
    println(Coro.run { chainK() })
    println(Coro.run { fetchDoubleK(7) })
    println(Coro.run { sumLoopK(4) })
    println(Coro.run { branchK(true) })
    println(Coro.run { branchK(false) })
    println(Coro.run { tryCatchK() })
}
