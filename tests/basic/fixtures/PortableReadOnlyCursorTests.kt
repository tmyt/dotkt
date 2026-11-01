package portablereadonlycursors

import NUnit.Framework.TestAttribute

private fun <T> exhaustWithoutMoving(cursor: ListIterator<T>, forward: Boolean) {
    val index = cursor.nextIndex()
    repeat(2) {
        var failed = false
        try { if (forward) cursor.next() else cursor.previous() }
        catch (_: NoSuchElementException) { failed = true }
        check(failed && cursor.nextIndex() == index && cursor.previousIndex() == index - 1)
    }
}

private class AuthoredCursor : AbstractList<Int>() {
    override val size: Int get() = 2
    override fun get(index: Int): Int = index + 1
    override fun listIterator(): ListIterator<Int> = listOf(99).listIterator()
    override fun listIterator(index: Int): ListIterator<Int> = listOf(77).listIterator(index)
}

class PortableReadOnlyCursorTests {
    @TestAttribute fun nativeReadonlyCursorsFollowLiveGrowthAndShrink() {
        val source = System.Collections.Generic.List<Int>()
        source.Add(1)
        val native: Any = source
        val readonly = native as List<Int>
        val cursors = listOf(
            readonly.listIterator(1),
            kotlin.collections.clrListListIterator(readonly, 1),
            kotlin.collections.clrProjectedListListIterator<Int>(native, 1),
        )
        for (cursor in cursors) exhaustWithoutMoving(cursor, true)
        source.Add(2)
        for (cursor in cursors) {
            check(cursor.hasNext() && cursor.next() == 2 && cursor.nextIndex() == 2)
            exhaustWithoutMoving(cursor, true)
            check(cursor.previous() == 2 && cursor.previous() == 1)
            exhaustWithoutMoving(cursor, false)
        }
        source.Clear()
        for (cursor in cursors) {
            check(!cursor.hasNext())
            exhaustWithoutMoving(cursor, true)
        }
        source.Add(3)
        for (cursor in cursors) check(cursor.hasNext() && cursor.next() == 3)
    }

    @TestAttribute fun starAndWidenedCursorsKeepTheirLiveSourceAndNulls() {
        val source = System.Collections.Generic.List<Int>()
        val native: Any = source
        val readonly = native as List<Int>
        val stars: List<*> = readonly
        val wide: List<Any?> = readonly
        val starCursor = stars.listIterator()
        val wideCursor = wide.listIterator(0)
        source.Add(7)
        check(starCursor.hasNext() && starCursor.next() == 7)
        check(wideCursor.hasNext() && wideCursor.next() == 7)
        val strings = System.Collections.Generic.List<String?>()
        strings.Add(null)
        val texts = (strings as Any) as List<String?>
        val cursor = texts.listIterator()
        check(cursor.next() == null)
        strings.Add("later")
        check(cursor.hasNext() && cursor.next() == "later")
        strings.Clear()
        check(!cursor.hasNext())
    }

    @TestAttribute fun factoryArgumentsAreEvaluatedOnceAndPositionsAreChecked() {
        val source = System.Collections.Generic.List<Int>()
        source.Add(1)
        val readonly = (source as Any) as List<Int>
        var calls = ""
        fun receiver(): List<Int> { calls += "r"; return readonly }
        fun position(): Int { calls += "i"; return 1 }
        val cursor = receiver().listIterator(position())
        check(calls == "ri" && cursor.nextIndex() == 1)
        for (index in listOf(-1, 2)) {
            var rejected = false
            try { readonly.listIterator(index) }
            catch (_: IndexOutOfBoundsException) { rejected = true }
            check(rejected)
        }
        source.Add(2)
        check(cursor.hasNext() && cursor.next() == 2 && calls == "ri")
        val slice = readonly.subList(0, 1)
        source.Add(3)
        check(slice.size == 1 && slice.listIterator().next() == 1)
    }

    @TestAttribute fun authoredIteratorOverridesStillOwnProjectedCalls() {
        val source = AuthoredCursor()
        val readonly: List<Int> = source
        check(readonly.listIterator().next() == 99)
        check(readonly.listIterator(0).next() == 77)
        check(kotlin.collections.clrProjectedListIterator<Int>(source).next() == 99)
        check(kotlin.collections.clrProjectedListListIterator<Int>(source, 0).next() == 77)
        check(kotlin.collections.clrProjectedViewListIterator<Int>(source, null, 3).next() == 99)
        check(kotlin.collections.clrProjectedViewListIteratorAt<Int>(source, null, 3, 0).next() == 77)
    }
}
