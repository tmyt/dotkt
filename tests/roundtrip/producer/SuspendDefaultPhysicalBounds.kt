package roundtrip.suspendphysicalbounds

class PhysicalBoundOwner<A : CharSequence, T>(val label: A, val value: T) {
    suspend inline fun <reified R> matches(
        action: () -> Unit, item: Any?,
        noinline block: suspend () -> Pair<T, Boolean> = {
            Pair(value, label.length > 0 && item is R)
        },
    ): Pair<T, Boolean> { action(); return block() }
}
