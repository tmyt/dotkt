package roundtrip.clrrefparameters

import kotlin.clr.ClrRef
import kotlin.clr.byref

class InnerReferenceOwner<out Unrelated, T> {
    inner class Writer(first: ClrRef<ReferenceBox<T>>, second: ClrRef<ReferenceBox<T>>,
                       replacement: ReferenceBox<T>, fail: Boolean = false) {
        val observed: ReferenceBox<T>
        init {
            first.value = replacement
            observed = second.value
            if (fail) throw IllegalStateException("written")
        }
    }
    inner class GenericWriter<U>(first: ClrRef<ReferenceBox<T>>, second: ClrRef<ReferenceBox<U>>,
                                 value: ReferenceBox<T>, other: ReferenceBox<U>) {
        init { first.value = value; second.value = other }
    }
    inner class ScalarWriter(slot: ClrRef<T>, replacement: T) {
        init { slot.value = replacement }
    }
    inner class Overloaded {
        val selection: Int
        constructor(slot: ClrRef<ReferenceBox<T>>, replacement: ReferenceBox<T>) {
            slot.value = replacement
            selection = 1
        }
        constructor(slot: ClrRef<ReferenceBox<String>>, replacement: ReferenceBox<String>) {
            slot.value = replacement
            selection = 2
        }
    }
}

fun sameModuleInnerReferences(): Boolean {
    val outer: InnerReferenceOwner<Any, String> = InnerReferenceOwner<Int, String>()
    var value = ReferenceBox("before")
    val replacement = ReferenceBox("after")
    val writer = outer.Writer(byref(value), byref(value), replacement)
    return value === replacement && writer.observed === replacement
}

fun <A, T> innerReferenceForward(outer: InnerReferenceOwner<A, T>, slot: ClrRef<ReferenceBox<T>>,
                               replacement: ReferenceBox<T>): ReferenceBox<T> =
    outer.Writer(slot, slot, replacement).observed

fun <A, T> sameModuleInnerReferenceOverload(outer: InnerReferenceOwner<A, T>, slot: ClrRef<ReferenceBox<T>>,
                                          replacement: ReferenceBox<T>): Int =
    outer.Overloaded(slot, replacement).selection

class BoundedInnerReferenceOwner<out Unrelated, T : ReferenceBoundBase> {
    inner class Writer(slot: ClrRef<ConstrainedReferenceBox<T>>, replacement: ConstrainedReferenceBox<T>) {
        init { slot.value = replacement }
    }
}
