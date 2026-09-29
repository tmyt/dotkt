package nonreturninginline

import kotlin.coroutines.*

inline fun <R> spinWhile(step: () -> Unit): R { while (true) step() }
inline fun <R> spinDoWhile(step: () -> Unit): R { do { step() } while (true) }
inline fun <R> throwAfterStep(step: () -> Unit): R { step(); error("after step") }
inline fun runUnitStep(step: () -> Unit) { step() }
inline fun <R> returnStep(step: () -> R): R = step()
inline fun concreteSpin(step: () -> Unit): Int { while (true) step() }
inline fun <R> nestedSpin(step: () -> Unit): R = spinWhile<R>(step)
inline fun <R> spinWithFinally(step: () -> Unit, finished: () -> Unit): R {
    try { while (true) step() } finally { finished() }
}
var pendingNonReturning: Continuation<Unit>? = null
inline fun <R> suspendingSpin(pause: () -> Unit, step: () -> Unit): R {
    while (true) {
        pause()
        step()
    }
}
