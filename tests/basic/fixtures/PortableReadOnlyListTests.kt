package portablereadonlylists

import NUnit.Framework.TestAttribute

private class Token(val label: String)

private fun renderAsObject(value: Any): String = value.toString()

private fun <T> failedStepKeepsPosition(iterator: ListIterator<T>, forward: Boolean) {
    val index = iterator.nextIndex()
    var failed = false
    try { if (forward) iterator.next() else iterator.previous() }
    catch (_: NoSuchElementException) { failed = true }
    check(failed && iterator.nextIndex() == index && iterator.previousIndex() == index - 1)
}

private fun <T> checkLiveSlice(source: MutableList<T>, first: T, second: T, replacement: T) {
    val readonly: List<T> = source
    val view = readonly.subList(1, 3)
    check(view.size == 2 && !view.isEmpty())
    check(view == listOf(first, second) && view.hashCode() == listOf(first, second).hashCode())
    check(view.toString() == listOf(first, second).toString())
    check(renderAsObject(view) == listOf(first, second).toString())
    check(view.containsAll(listOf(first, second)) && view.indexOf(first) == 0 && view.lastIndexOf(second) == 1)
    val nested = view.subList(0, 2).subList(1, 2)
    val cursor = view.listIterator(2)
    source[2] = replacement
    check(view[1] == replacement && nested[0] == replacement && cursor.previous() == replacement)
    val plain = kotlin.collections.clrListSubList(readonly, 1, 3)
    check(plain == view && plain.hashCode() == view.hashCode())
    check(renderAsObject(plain) == listOf(first, replacement).toString())
    val plainNested = plain.subList(1, 2)
    source[2] = second
    check(plainNested.single() == second && plain.last() == second && nested.single() == second)
}

private class CustomSearch : AbstractList<String>() {
    override val size: Int get() = 3
    override fun get(index: Int): String = listOf("first", "second", "last")[index]
    override fun contains(element: String): Boolean = false
    override fun indexOf(element: String): Int = -1
    override fun lastIndexOf(element: String): Int = -1
}

class PortableReadOnlyListTests {
    @TestAttribute fun nativeIteratorKeepsItsCursorAfterFailedSteps() {
        val values: List<Int> = arrayListOf(1, 2)
        for (iterator in listOf(values.listIterator(), kotlin.collections.clrListListIterator(values, 0))) {
            repeat(2) { failedStepKeepsPosition(iterator, false) }
            check(iterator.next() == 1 && iterator.next() == 2)
            repeat(2) { failedStepKeepsPosition(iterator, true) }
            check(iterator.previous() == 2 && iterator.previous() == 1)
            failedStepKeepsPosition(iterator, false)
            check(iterator.next() == 1)
        }
    }

    @TestAttribute fun nullableValueAndReferenceSlicesStayLive() {
        checkLiveSlice(mutableListOf(0, 1, 2, 3), 1, 2, 9)
        checkLiveSlice(mutableListOf<Int?>(0, null, 2, 3), null, 2, null)
        checkLiveSlice(mutableListOf<String?>("outer", null, "second", "tail"), null, "second", "new")
    }

    @TestAttribute fun widenedSlicesRetainElementIdentity() {
        val first = Token("first")
        val second = Token("second")
        val replacement = Token("replacement")
        val source = arrayListOf<Token?>(null, first, second, null)
        val wide: List<Any?> = source
        val view = wide.subList(1, 3)
        check(view[0] === first && view[1] === second)
        val nested = view.subList(1, 2)
        source[2] = replacement
        check(nested.single() === replacement && view.last() === replacement)
    }

    @TestAttribute fun floatingSearchAndEqualityUseKotlinPolicy() {
        val source = arrayListOf(10.0, 0.0, -0.0, Double.NaN, Double.NaN, 20.0)
        val readonly: List<Double> = source
        val view = readonly.subList(1, 5)
        check(view.indexOf(0.0) == 0 && view.lastIndexOf(0.0) == 0)
        check(view.indexOf(-0.0) == 1 && view.lastIndexOf(-0.0) == 1)
        check(view.indexOf(Double.NaN) == 2 && view.lastIndexOf(Double.NaN) == 3)
        val expected = listOf(0.0, -0.0, Double.NaN, Double.NaN)
        check(view == expected && view.hashCode() == expected.hashCode())
    }

    @TestAttribute fun emptyAndInvalidSlicesKeepRangeAndIteratorContracts() {
        val values: List<String> = arrayListOf("first", "second")
        val empty = values.subList(1, 1)
        check(empty.isEmpty() && empty == emptyList<String>() && empty.hashCode() == 1)
        val iterator = empty.listIterator()
        failedStepKeepsPosition(iterator, false)
        failedStepKeepsPosition(iterator, true)
        var badStart = false
        try { values.subList(-1, 1) } catch (_: IndexOutOfBoundsException) { badStart = true }
        var badEnd = false
        try { values.subList(0, 3) } catch (_: IndexOutOfBoundsException) { badEnd = true }
        var reversed = false
        try { values.subList(2, 1) } catch (_: IllegalArgumentException) { reversed = true }
        var badCursor = false
        try { values.listIterator(3) } catch (_: IndexOutOfBoundsException) { badCursor = true }
        var overlapping = false
        try { values.subList(-1, -2) } catch (_: IndexOutOfBoundsException) { overlapping = true }
        check(badStart && badEnd && reversed && badCursor && overlapping)
    }

    @TestAttribute fun slicesUseTheirOwnSearchRatherThanBackingOverrides() {
        val source: List<String> = CustomSearch()
        check(!source.contains("second") && source.indexOf("second") == -1)
        val view = source.subList(1, 3)
        check(view.contains("second") && view.indexOf("second") == 0 && view.lastIndexOf("last") == 1)
        check(view == listOf("second", "last"))
    }
}
