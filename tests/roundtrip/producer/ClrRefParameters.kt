package roundtrip.clrrefparameters

import kotlin.clr.ClrRef

fun incrementReferenced(slot: ClrRef<Int>, delta: Int): Int {
    slot.value = slot.value + delta
    return slot.value
}

inline fun incrementReferencedInline(slot: ClrRef<Int>, delta: Int): Int {
    slot.value = slot.value + delta
    return slot.value
}

inline fun updateReferencedInline(slot: ClrRef<Int>, transform: (Int) -> Int): Int {
    slot.value = transform(slot.value)
    return slot.value
}

fun updateReferencedThroughSameModuleInline(slot: ClrRef<Int>): Int =
    updateReferencedInline(slot) { it + 4 }

fun <T> swapReferenced(first: ClrRef<T>, second: ClrRef<T>) {
    val saved = first.value
    first.value = second.value
    second.value = saved
}
