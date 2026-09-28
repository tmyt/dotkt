package roundtrip.unitcallback

inline fun <R> bodyCallback(crossinline block: () -> R): () -> R = { block() }

inline fun <R> wideCallback(
    action: () -> Unit,
    value: R,
    noinline callback: (R, R, R, R, R, R, R, R, R, R, R, R, R, R, R, R, R) -> R =
        { first, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _ -> first },
): R {
    action()
    return callback(value, value, value, value, value, value, value, value, value,
        value, value, value, value, value, value, value, value)
}

inline fun <R> selectedCallback(block: () -> R, absent: () -> R = { error("unused") }): R = block()

inline fun <R> invokedCallback(action: () -> Unit, value: R, noinline callback: (R) -> R = { it }): R {
    action()
    return callback(value)
}

inline fun <R> capturedCallback(action: () -> Unit, value: R, noinline callback: () -> R = { value }): R {
    action()
    return callback()
}

class CallbackValue<R>(val value: R) {
    var calls: Int = 0
    fun read(): R { calls++; return value }
}

inline fun <R> deferredCallback(
    action: () -> Unit,
    value: CallbackValue<R>,
    noinline callback: () -> R = { value.read() },
): () -> R {
    action()
    return callback
}

fun sameModuleUnitCallbacks(): Int {
    var calls = 0
    selectedCallback({ calls++; Unit })
    invokedCallback({ calls++ }, Unit)
    capturedCallback({ calls++ }, Unit)
    return calls
}
