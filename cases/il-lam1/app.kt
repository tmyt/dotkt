// Bundle-6 P3 wave-2b — the suspend-LAMBDA payoff. `blockOn { 42 }` drives a `suspend () -> Int`
// literal to completion on the cold Continuation core. kotc emits the block as a `suspendLambdaNew`
// node; bir2cir turns it into a `kotlin.coroutines.clr.internal.SuspendLambda` state machine, and
// `blockOn` (kotlin.clr, expect/actual) starts it (create -> resume) and drains the root sink.
import kotlin.clr.blockOn

fun main() {
    println(blockOn { 42 })   // 42
}
