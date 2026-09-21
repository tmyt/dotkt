package interop.mutablelistfamilies

import NUnit.Framework.TestAttribute
import MutableListInterop.*
import CollectionStorageInterop.MutableListWithDictionaryStorage

private fun mutableSize(value: MutableList<*>): Int = value.size
private fun mutableEmpty(value: MutableList<*>): Boolean = value.isEmpty()
private fun mutableFirst(value: MutableList<*>): Any? = value[0]
private fun nullableMutableSize(value: MutableList<*>?): Int = value?.size ?: -1

private class AuthoredMutableList : AbstractMutableList<Int>() {
    override val size: Int get() = 1
    override fun get(index: Int): Int = 7
    override fun isEmpty(): Boolean = true
    override fun add(index: Int, element: Int) { throw UnsupportedOperationException() }
    override fun set(index: Int, element: Int): Int = throw UnsupportedOperationException()
    override fun removeAt(index: Int): Int = throw UnsupportedOperationException()
}

class MutableListFamilyTests {
    @TestAttribute fun directSizeIgnoresUnrelatedReadonlyList() {
        val source: Any = MutableAndReadOnlyList()
        check((source as MutableList<*>).size == 2)
    }

    @TestAttribute fun directEmptinessIgnoresUnrelatedReadonlyList() {
        val source: Any = MutableAndReadOnlyList()
        check(!(source as MutableList<*>).isEmpty())
    }

    @TestAttribute fun directGetIgnoresUnrelatedReadonlyList() {
        val source: Any = MutableAndReadOnlyList()
        check((source as MutableList<*>)[0] == 7)
    }

    @TestAttribute fun parametersAndLocalsRetainTheMutableFamily() {
        val source: Any = MutableAndReadOnlyList()
        val list = source as MutableList<*>
        check(list.size == 2 && list[1] == 9)
        check(mutableSize(list) == 2 && !mutableEmpty(list) && mutableFirst(list) == 7)
    }

    @TestAttribute fun nullableParametersRetainTheMutableFamily() {
        check(nullableMutableSize(null) == -1)
        val source: Any = MutableAndReadOnlyList()
        check(nullableMutableSize(source as MutableList<*>) == 2)
    }

    @TestAttribute fun readonlyPreferenceIsNotChangedForTheSameElement() {
        val source: Any = SameElementList()
        check(mutableSize(source as MutableList<*>) == 2)
        check(mutableFirst(source as MutableList<*>) == 7)
        check((source as List<*>).size == 3)
        check((source as List<*>)[0] == 100)
    }

    @TestAttribute fun rawIListRemainsUsable() {
        val source: Any = RawMutableList()
        val list = source as MutableList<*>
        check(mutableSize(list) == 2 && !mutableEmpty(list))
        check(mutableFirst(list) == 7 && list[1] == 9)
        check(list.listIterator().next() == 7)
        check(list.subList(1, 2)[0] == 9)
    }

    @TestAttribute fun dictionaryStorageDoesNotJoinTheMutableFamily() {
        val source: Any = MutableListWithDictionaryStorage()
        val list = source as MutableList<*>
        check(mutableSize(list) == 2 && !mutableEmpty(list))
        check(mutableFirst(list) == "x" && list[1] == "y")
        check(list.listIterator().next() == "x")
    }

    @TestAttribute fun genuineMutableAmbiguityIsNotHiddenByARawList() {
        val source: Any = DualMutableList()
        val list = source as MutableList<*>
        var failures = 0
        try { mutableSize(list) } catch (_: IllegalStateException) { failures++ }
        try { mutableEmpty(list) } catch (_: IllegalStateException) { failures++ }
        try { mutableFirst(list) } catch (_: IllegalStateException) { failures++ }
        check(failures == 3)
    }

    @TestAttribute fun sourceAuthoredExactMutableWitnessIsNotReopened() {
        val source: Any = DualMutableList()
        val exact = source as MutableList<Any?>
        check(exact.size == 3 && exact[0] == "a")
        val star: MutableList<*> = exact
        check(star.size == 3 && star[1] == "b")
    }

    @TestAttribute fun authoredEmptinessOverrideStillWins() {
        val source: Any = AuthoredMutableList()
        val list = source as MutableList<*>
        check(mutableSize(list) == 1 && mutableFirst(list) == 7)
        check(mutableEmpty(list))
    }

    @TestAttribute fun listIteratorAndSubListUseTheSameMutableElements() {
        val source: Any = MutableAndReadOnlyList()
        val list = source as MutableList<*>
        val iterator = list.listIterator()
        check(iterator.next() == 7 && iterator.next() == 9 && !iterator.hasNext())
        val sub = list.subList(1, 2)
        check(sub.size == 1 && sub[0] == 9 && !sub.isEmpty())
    }

    @TestAttribute fun nativeGetterExceptionsAreUnwrapped() {
        val source: Any = ThrowingMutableList()
        val list = source as MutableList<*>
        var failures = 0
        try { mutableSize(list) }
        catch (error: IllegalStateException) { check(error.message == "mutable-count"); failures++ }
        try { mutableEmpty(list) }
        catch (error: IllegalStateException) { check(error.message == "mutable-count"); failures++ }
        try { mutableFirst(list) }
        catch (error: IllegalStateException) { check(error.message == "mutable-get"); failures++ }
        check(failures == 3)
    }
}
