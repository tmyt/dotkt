package roundtrip.clrrefparameters

import kotlin.clr.ClrRef
import kotlin.clr.byref

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

class ReferenceBox<T>(val value: T)

open class ReferenceWriter<T, Unused> {
    open fun replace(first: ClrRef<ReferenceBox<T>>, second: ClrRef<ReferenceBox<T>>,
                     value: ReferenceBox<T>): ReferenceBox<T> {
        first.value = value
        return second.value
    }

    fun <R> replaceAndReturn(slot: ClrRef<ReferenceBox<T>>, value: ReferenceBox<T>, token: R): R {
        slot.value = value
        return token
    }

    fun replaceAndThrow(slot: ClrRef<ReferenceBox<T>>, value: ReferenceBox<T>) {
        slot.value = value
        throw IllegalStateException("after write")
    }
}

class DerivedReferenceWriter<A, B> : ReferenceWriter<B, A>()

class OverridingReferenceWriter : ReferenceWriter<String, Int>() {
    var calls = 0
    override fun replace(first: ClrRef<ReferenceBox<String>>, second: ClrRef<ReferenceBox<String>>,
                         value: ReferenceBox<String>): ReferenceBox<String> {
        calls += 1
        first.value = value
        return second.value
    }
}

interface ReferenceInterface<T> {
    fun replace(first: ClrRef<ReferenceBox<T>>, second: ClrRef<ReferenceBox<T>>,
                value: ReferenceBox<T>): ReferenceBox<T> {
        first.value = value
        return second.value
    }
}
class StringReferenceInterface : ReferenceInterface<String>

class ScalarReferenceWriter<Unused, T> {
    fun replace(first: ClrRef<T>, second: ClrRef<T>, value: T): T {
        first.value = value
        return second.value
    }
}

class CovariantReferenceBox<out T>(val value: T)
class CovariantReferenceWriter<T> {
    fun keep(slot: ClrRef<CovariantReferenceBox<T>>) { slot.value = slot.value }
}
fun anyReferenceView(): CovariantReferenceBox<Any> = CovariantReferenceBox(1)

interface ReferenceBound
class ReferenceItem : ReferenceBound
class BoundReferenceWriter<R, T : R> {
    fun keep(slot: ClrRef<T>) { slot.value = slot.value }
}

open class ReferenceBoundBase
open class ReferenceBoundMiddle : ReferenceBoundBase()
class ReferenceBoundLeaf : ReferenceBoundMiddle()
class ConstrainedReferenceBox<T : ReferenceBoundBase>(val value: T)
class ConstrainedReferenceWriter<R : ReferenceBoundBase, T : R> {
    fun keep(slot: ClrRef<ConstrainedReferenceBox<T>>) { slot.value = slot.value }
}

fun sameModuleReferenceAlias(): Boolean {
    var value = ReferenceBox("before")
    val replacement = ReferenceBox("after")
    check(ReferenceWriter<String, Int>().replace(byref(value), byref(value), replacement) === replacement)
    val derived = DerivedReferenceWriter<Int, String>()
    return derived.replace(byref(value), byref(value), replacement) === replacement && value === replacement
}
