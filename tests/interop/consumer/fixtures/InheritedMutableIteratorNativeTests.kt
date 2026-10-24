package interop.inheritedmutableiterator

import MutableIteratorInterop.NativeOnlyNumbers
import MutableIteratorInterop.NativeSetMutations
import NUnit.Framework.TestAttribute
import kotlin.collections.ClrEnumerable
import kotlin.collections.iteratorOverEnumerable

private class NativeDefaultSet<T>(val values: MutableList<T>) : AbstractMutableSet<T>() {
    var iteratorCalls = 0
    override val size: Int get() = values.size
    override fun add(element: T): Boolean = if (values.contains(element)) false else values.add(element)
    override fun iterator(): MutableIterator<T> { iteratorCalls++; return values.listIterator() }
}

class InheritedMutableIteratorNativeTests {
    @TestAttribute fun nativeReferenceRemoveAndClearUseTheAuthoredIterator() {
        val values = mutableListOf("a", "b")
        val set = NativeDefaultSet(values)
        check(NativeSetMutations.RemoveString(set, "b"))
        check(values.size == 1 && values[0] == "a" && set.iteratorCalls == 1)
        NativeSetMutations.ClearString(set)
        check(values.isEmpty() && set.iteratorCalls == 2)
    }

    @TestAttribute fun nativeValueRemoveAndClearUseTheAuthoredIterator() {
        val values = mutableListOf(7, 9)
        val set = NativeDefaultSet(values)
        check(NativeSetMutations.RemoveInt(set, 9))
        check(values.size == 1 && values[0] == 7 && set.iteratorCalls == 1)
        NativeSetMutations.ClearInt(set)
        check(values.isEmpty() && set.iteratorCalls == 2)
    }

    @TestAttribute fun enumerableOnlyFallbackKeepsTheReadonlyIteratorProtocol() {
        val source = (NativeOnlyNumbers() as Any) as ClrEnumerable<Int>
        val iterator = iteratorOverEnumerable(source)
        check(iterator !is MutableIterator<*>)
        check(iterator.hasNext() && iterator.hasNext() && iterator.next() == 7)
        check(iterator.next() == 9 && !iterator.hasNext())
    }
}
