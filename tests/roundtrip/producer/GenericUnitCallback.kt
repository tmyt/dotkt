package roundtrip.unitcallback

inline fun <R> selectedCallback(block: () -> R, absent: () -> R = { error("unused") }): R = block()

inline fun <R> invokedCallback(action: () -> Unit, value: R, noinline callback: (R) -> R = { it }): R {
    action()
    return callback(value)
}

inline fun <R> capturedCallback(action: () -> Unit, value: R, noinline callback: () -> R = { value }): R {
    action()
    return callback()
}

fun sameModuleUnitCallbacks(): Int {
    var calls = 0
    selectedCallback({ calls++; Unit })
    invokedCallback({ calls++ }, Unit)
    capturedCallback({ calls++ }, Unit)
    return calls
}
