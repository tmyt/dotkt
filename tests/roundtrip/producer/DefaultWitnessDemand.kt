package roundtrip.defaultwitness

import roundtrip.suspenddefaultframes.SuspendDefaultGate

suspend inline fun <reified T> selectDefault(
    item: Any,
    noinline block: suspend () -> T? = { if (item is T) item else null },
): T? = block()

suspend inline fun <reified T> matchesDefault(
    item: Any?,
    noinline block: suspend () -> Boolean = { item is T },
): Boolean = block()

suspend inline fun <reified T> nestedDefault(
    item: Any?,
    noinline block: suspend () -> Boolean = { matchesDefault<T>(item) },
): Boolean = block()

inline fun <reified T> ordinaryDefault(
    item: Any?,
    noinline block: () -> Boolean = { item is T },
): Boolean = block()

inline fun <reified T> nullDefault(noinline block: () -> Boolean = { null is T }): Boolean = block()

suspend inline fun <reified T> delayedDefault(
    item: Any?,
    gate: SuspendDefaultGate,
    noinline block: suspend () -> Boolean = { gate.pause(); item is T },
): Boolean = block()
