package roundtrip.nativemutablesnapshot.consumer

import NUnit.Framework.TestAttribute
import MutableSnapshotInterop.NativeSnapshotFailure
import roundtrip.nativemutablesnapshot.nativeSnapshotCursor
import roundtrip.nativemutablesnapshot.erasedSnapshotNext

private class EqualSnapshotValue {
    override fun equals(other: Any?): Boolean = other is EqualSnapshotValue
    override fun hashCode(): Int = 1
}

class NativeMutableSnapshotTests {
    @TestAttribute
    fun snapshotPreservesErasedValuesUntilTypedConsumptionAndRemovesTheReturnedOccurrence() {
        val native = System.Collections.Generic.LinkedList<String>()
        native.AddLast("value")
        @Suppress("UNCHECKED_CAST")
        val viewed = native as MutableCollection<Int>
        var collectionCalls = 0
        val cursor = nativeSnapshotCursor { collectionCalls++; viewed }
        check(collectionCalls == 1 && cursor.hasNext() && cursor.hasNext())
        var illegalRemove = false
        try { cursor.remove() } catch (expected: IllegalStateException) { illegalRemove = true }
        check(illegalRemove)
        var iteratorCalls = 0
        check(erasedSnapshotNext { iteratorCalls++; cursor } == "value" && iteratorCalls == 1)
        check(!cursor.hasNext())
        try {
            erasedSnapshotNext { iteratorCalls++; cursor }
            error("exhausted snapshot returned a value")
        } catch (expected: NoSuchElementException) { check(iteratorCalls == 2) }
        cursor.remove()
        check(native.Count == 0)
        illegalRemove = false
        try { cursor.remove() } catch (expected: IllegalStateException) { illegalRemove = true }
        check(illegalRemove)

        native.AddLast("value")
        val typed = nativeSnapshotCursor { viewed }
        try {
            val integer: Int = typed.next()
            error("typed Int extraction accepted a String: $integer")
        } catch (expected: ClassCastException) { }

        val ints = System.Collections.Generic.LinkedList<Int>()
        ints.AddLast(7)
        check(erasedSnapshotNext { nativeSnapshotCursor { ints as MutableCollection<Int> } } == 7)
        val nullable = System.Collections.Generic.LinkedList<Int?>()
        nullable.AddLast(null)
        nullable.AddLast(9)
        val nullableCursor = nativeSnapshotCursor { nullable as MutableCollection<Int?> }
        check(erasedSnapshotNext { nullableCursor } == null)
        check(erasedSnapshotNext { nullableCursor } == 9)

        val first = EqualSnapshotValue()
        val second = EqualSnapshotValue()
        val third = EqualSnapshotValue()
        val duplicates = System.Collections.Generic.LinkedList<EqualSnapshotValue>()
        duplicates.AddLast(first)
        duplicates.AddLast(second)
        duplicates.AddLast(third)
        @Suppress("UNCHECKED_CAST")
        val unrelated = duplicates as MutableCollection<Int>
        val duplicateCursor = nativeSnapshotCursor { unrelated }
        check(erasedSnapshotNext { duplicateCursor } === first)
        check(erasedSnapshotNext { duplicateCursor } === second)
        duplicateCursor.remove()
        val remaining = ArrayList<Any?>()
        for (value in duplicates) remaining.add(value)
        check(remaining.size == 2 && remaining[0] === first && remaining[1] === third)
        check(erasedSnapshotNext { duplicateCursor } === third && !duplicateCursor.hasNext())

        val failure = NativeSnapshotFailure()
        try {
            nativeSnapshotCursor { failure as MutableCollection<Any?> }
            error("native enumeration exception was suppressed")
        } catch (actual: Throwable) { check(actual === failure.Failure && failure.EnumerationCalls == 1) }
    }
}
