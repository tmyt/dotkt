// Phase 0 — NON-TRIVIAL suspend lambdas are CPS-linearized (each becomes a coroutine: a lifted method / closure
// `invoke` lowered to a state machine + Task<T> kickoff), not just the trivial `{ f() }` forward case.
// Driven via Coro.run (a `suspend ()->Int` == Func<Task<int>>), so it rides the existing Task sink.
import clr.Api2
import clr.Coro
import clr.await

fun main() {
    // Non-capturing, multi-suspension lambda body (two awaits + arithmetic across a suspension).
    println(Coro.run {
        val a = Api2.step(10).await()
        val b = Api2.step(20).await()
        a + b                              // 30
    })

    // Non-capturing, suspension INSIDE A LOOP (acc/i survive every iteration as state-machine fields).
    println(Coro.run {
        var acc = 0
        var i = 0
        while (i < 4) {
            acc = acc + Api2.step(i).await()
            i = i + 1
        }
        acc                                // 0+1+2+3 = 6
    })

    // CAPTURING lambda: closes over `base`; the closure `invoke` is an INSTANCE coroutine (this captured into SM).
    val base = 100
    println(Coro.run {
        val x = Api2.step(5).await()
        base + x                           // 105
    })

    // CAPTURING + loop: closes over `factor`, multi-suspension across iterations.
    val factor = 3
    println(Coro.run {
        var sum = 0
        var i = 1
        while (i <= 3) {
            sum = sum + Api2.step(i).await() * factor
            i = i + 1
        }
        sum                                // (1+2+3)*3 = 18
    })
}
