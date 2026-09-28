package roundtrip.defaultcompanion

class Segment<T>(var value: T?) {
    val storage: Array<T?>? = null
}

class DefaultCompanionOwner<T>(val segment: Segment<T>) {
    inline fun <R> select(
        item: T,
        block: () -> R,
        absent: (Segment<T>, T) -> R = { _, _ -> error("unused callback") },
    ): R = block()

    fun sameModule(item: T): T = select(item, { item })

    inline fun <R> invokeDefault(
        item: T,
        result: R,
        noinline callback: (Segment<T>, T, R) -> R = { s, value, r -> s.value = value; r },
    ): R = callback(segment, item, result)

    inline fun <R> invokeCaptured(
        item: T,
        result: R,
        noinline callback: () -> R = { segment.value = item; result },
    ): R = callback()
}

fun <T> sameModuleDefault(item: T): T = DefaultCompanionOwner(Segment<T>(null)).sameModule(item)
