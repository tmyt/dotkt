package nonreturninginline

inline fun <R> spinWhile(step: () -> Unit): R { while (true) step() }
inline fun <R> spinDoWhile(step: () -> Unit): R { do { step() } while (true) }
inline fun <R> throwAfterStep(step: () -> Unit): R { step(); error("after step") }
inline fun runUnitStep(step: () -> Unit) { step() }
inline fun <R> returnStep(step: () -> R): R = step()
