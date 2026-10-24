package interop.projectedmutableliststate

import MutableListInterop.DualMutableList
import MutableListInterop.RawMutableList
import NUnit.Framework.TestAttribute

private fun <T> checkGenericState(exact: MutableList<T>, first: T, replacement: T) {
    val mutate = {
        val projected: MutableList<in T> = exact
        val iterator = projected.listIterator()
        check(iterator.next() == first)
        iterator.set(replacement)
        check(projected.listIterator(1).previous() == replacement)
        val slice = projected.subList(0, 1)
        check(slice[0] == replacement)
        check(slice.addAll(1, listOf(first)))
        check(slice.size == 2 && exact[1] == first)
    }
    mutate()
}

private class AuthoredStateList(private val backing: MutableList<Int> = mutableListOf(7, 9)) :
    MutableList<Int> by backing {
    var calls = 0
    override fun listIterator(): MutableListIterator<Int> {
        calls++
        return mutableListOf(41).listIterator()
    }
    override fun listIterator(index: Int): MutableListIterator<Int> {
        calls++
        return mutableListOf(42, 43).listIterator(index)
    }
    override fun subList(fromIndex: Int, toIndex: Int): MutableList<Int> {
        calls++
        return mutableListOf(44)
    }
}

private fun checkStateAmbiguity(operation: () -> Any?) {
    var rejected = false
    try { operation() }
    catch (failure: IllegalStateException) {
        check(failure.message?.startsWith("Ambiguous projected view") == true)
        rejected = true
    }
    check(rejected)
}

private fun checkUnselectedState(list: MutableList<*>) {
    checkStateAmbiguity { list.listIterator() }
    checkStateAmbiguity { list.listIterator(1) }
    checkStateAmbiguity { list.subList(0, 1) }
}

class ProjectedMutableListStateTests {
    @TestAttribute fun iteratorsAndNestedSlicesKeepTheSelectedNativeFace() {
        val native = DualMutableList()
        val exact = (native as Any) as MutableList<Any?>
        val projected: MutableList<in Any?> = exact
        val iterator = projected.listIterator()
        check(iterator.next() == "a")
        iterator.set(null)
        iterator.add("inserted")
        check(iterator.previous() == "inserted")
        iterator.remove()
        check(projected.listIterator(1).next() == "b")
        val slice = projected.subList(0, 2)
        check(slice.size == 2 && slice[0] == null && slice.set(1, "set") == "b")
        check(slice.addAll(1, listOf<Any?>("x", null)))
        check(slice.size == 4 && exact.size == 5 && exact[3] == "set")
        val nested = slice.subList(1, 3)
        check(nested.addAll(1, listOf("y")))
        check(nested.size == 3 && slice.size == 5 && nested.removeAt(0) == "x")
        check(nested.set(0, 42) == "y")
        nested.clear()
        check(nested.isEmpty() && slice.size == 2 && exact.size == 3)
        val sliceIterator = slice.listIterator()
        check(sliceIterator.next() == null)
        sliceIterator.set("slice")
        sliceIterator.add(9)
        check(sliceIterator.previous() == 9)
        sliceIterator.remove()
        check(exact.size == 3 && exact[0] == "slice" && exact[1] == "set" && exact[2] == "c")
        val integers = (native as Any) as MutableList<Int>
        val star: MutableList<*> = integers
        val integerIterator = star.listIterator(1)
        check(integerIterator.previous() == 7)
        integerIterator.remove()
        check(star.subList(0, 1).removeAt(0) == 9)
        check(native.Count == 0 && exact.size == 3)
    }

    @TestAttribute fun genericClosuresAndAuthoredStateDefaultsRemainAuthoritative() {
        val native = DualMutableList()
        val objects = (native as Any) as MutableList<Any?>
        checkGenericState(objects, "a", null)
        check(objects.size == 4 && objects[0] == null && objects[1] == "a")
        val integers = (native as Any) as MutableList<Int>
        checkGenericState(integers, 7, 47)
        check(native.Count == 3 && integers[0] == 47 && integers[1] == 7 && objects.size == 4)
        val authored = AuthoredStateList()
        val exact = (authored as Any) as MutableList<Int>
        val projected: MutableList<*> = exact
        check(projected.listIterator().next() == 41)
        check(projected.listIterator(1).next() == 43)
        check(projected.subList(0, 1)[0] == 44 && authored.calls == 3)
    }

    @TestAttribute fun missingPreferencesStayAmbiguousAndRawFallbackStillWorks() {
        val native = DualMutableList()
        checkUnselectedState((native as Any) as MutableList<*>)
        val unmatched = (native as Any) as MutableList<String>
        val projected: MutableList<in String> = unmatched
        checkStateAmbiguity { projected.listIterator() }
        checkStateAmbiguity { projected.listIterator(1) }
        checkStateAmbiguity { projected.subList(0, 1) }
        check(native.Count == 2 && ((native as Any) as MutableList<Any?>).size == 3)
        val raw = RawMutableList()
        val rawList = (raw as Any) as MutableList<*>
        val rawIterator = rawList.listIterator()
        check(rawIterator.next() == 7)
        rawIterator.remove()
        check(rawList.subList(0, 1)[0] == 9 && raw.Count == 1)
    }
}
