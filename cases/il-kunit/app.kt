// Phase 4b — UNIT-result Continuation-class coroutines: a `suspend fun … : Unit` surfaces as a non-generic Task
// (RootUnit sink). Exercised by another suspend fun that awaits it, then returns a value.
import clr.Api2
import clr.Coro
import clr.KCont
import clr.await

// Unit-returning @KCont suspend fun (class form -> RootUnit). Performs a suspension, returns Unit.
@KCont suspend fun warmUp(n: Int): Unit {
    Api2.step(n).await()
    Api2.step(n).await()
}

// Calls the Unit suspend fun (awaits its Task), then produces an Int.
@KCont suspend fun useUnit(): Int {
    warmUp(3)
    val v = Api2.step(40).await()
    return v + 2                       // 42
}

fun main() {
    println(Coro.run { useUnit() })    // 42
}
