package roundtrip.defaultcapture

inline fun <R> deferredValue(
    action: () -> Unit,
    value: R,
    noinline effect: () -> Unit,
    noinline callback: () -> R = { effect(); value },
): () -> R { action(); return callback }

inline fun sharedDefault(
    action: () -> Unit,
    noinline effect: () -> Unit,
    noinline first: () -> Unit = effect,
    noinline second: () -> Unit = effect,
): () -> Unit {
    action()
    check(first === effect)
    check(second === effect)
    effect()
    return { first(); second() }
}

inline fun String.orderedDefault(
    action: () -> Unit,
    noinline effect: () -> Int,
    first: Int = effect(),
    second: Int = effect(),
): String {
    action()
    return this + first + ":" + second
}

fun sameModuleDeferred(effect: () -> Unit): () -> String =
    deferredValue({}, "same", effect)
