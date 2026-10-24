package interop.mutablelistmutations

import NUnit.Framework.TestAttribute
import MutableListInterop.*
import MutableMutationInterop.*
import CollectionStorageInterop.MutableListWithDictionaryStorage
import MixedCollectionContracts.ListWithMutableDictionary
import kotlin.collections.clrProjectedListAdd

private fun writableSlice(source: Any): MutableList<Any?> {
    val list = source as MutableList<*>
    return list.subList(0, list.size) as MutableList<Any?>
}

private fun insertThroughProjectedParameter(list: MutableList<in String>) {
    list.add(0, "parameter")
}

private fun appendThroughProjectedParameter(list: MutableList<in String>): Boolean = list.add("parameter")

private fun <T> checkInvariantFailure(list: MutableList<T>, value: T, expected: System.Exception, operation: Int) {
    var receivers = 0
    var indices = 0
    var elements = 0
    fun receiver(): MutableList<T> { receivers++; return list }
    fun index(): Int { indices++; return 0 }
    fun element(): T { elements++; return value }
    var caught: System.Exception? = null
    try {
        when (operation) {
            0 -> receiver().set(index(), element())
            1 -> receiver().removeAt(index())
            2 -> receiver().add(index(), element())
            else -> receiver().add(element())
        }
    } catch (failure: System.Exception) { caught = failure }
    check(caught === expected && receivers == 1)
    check(indices == (if (operation == 3) 0 else 1))
    check(elements == (if (operation == 1) 0 else 1))
}

private fun checkProjectedIteratorWrites(source: Any, first: Any?, second: Any?) {
    val list = source as MutableList<*>
    val iterator = list.listIterator() as MutableListIterator<Any?>
    check(iterator.next() == first)
    iterator.set("iterator")
    iterator.add("added")
    check(list.size == 3 && list[0] == "iterator" && list[1] == "added" && list[2] == second)
    check(iterator.previous() == "added")
    iterator.remove()
    check(list.size == 2 && list[0] == "iterator" && list[1] == second)
}

private fun checkRemoval(source: Any, first: Any?, second: Any?) {
    val list = source as MutableList<*>
    check(list.removeAt(0) == first)
    check(list.size == 1 && list[0] == second)
    val iterator = list.listIterator()
    check(iterator.next() == second)
    iterator.remove()
    check(list.isEmpty())
}

private fun checkSliceWrites(source: Any, first: Any?, second: Any?) {
    val backing = source as MutableList<*>
    val view = writableSlice(source)
    check(view.set(0, "set") == first && backing[0] == "set")
    view.add(1, "insert")
    check(view.size == 3 && backing.size == 3 && backing[2] == second)
    val iterator = view.listIterator()
    check(iterator.next() == "set")
    iterator.set("iterator")
    iterator.add("added")
    check(view.size == 4 && backing[0] == "iterator" && backing[1] == "added")
    check(iterator.previous() == "added")
    iterator.remove()
    check(view.size == 3 && backing.size == 3 && backing[1] == "insert")
    val nested = view.subList(1, 3)
    check(nested.removeAt(0) == "insert")
    check(view.size == 2 && backing.size == 2 && nested.size == 1)
    check(backing[0] == "iterator" && backing[1] == second)
}

private class EvaluatedList(private val source: Any) {
    var receivers = 0
    var indices = 0
    fun receiver(): Any { receivers++; return source }
    fun index(): Int { indices++; return 0 }
}

private fun checkFailure(source: Any, expected: System.Exception) {
    val evaluation = EvaluatedList(source)
    var caught: System.Exception? = null
    try { (evaluation.receiver() as MutableList<*>).removeAt(evaluation.index()) }
    catch (failure: System.Exception) { caught = failure }
    check(caught === expected && evaluation.receivers == 1 && evaluation.indices == 1)
    val view = writableSlice(source)
    caught = null
    try { view.set(0, 7) } catch (failure: System.Exception) { caught = failure }
    check(caught === expected && view.size == 2)
    caught = null
    try { view.add(0, 7) } catch (failure: System.Exception) { caught = failure }
    check(caught === expected && view.size == 2)
}

private class AuthoredMutationList(
    private val backing: MutableList<Any?> = mutableListOf(7, 9),
) : MutableList<Any?> by backing {
    var sets = 0
    var inserts = 0
    var removals = 0
    override fun set(index: Int, element: Any?): Any? { sets++; return backing.set(index, element) }
    override fun add(index: Int, element: Any?) { inserts++; backing.add(index, element) }
    override fun removeAt(index: Int): Any? { removals++; return backing.removeAt(index) }
}

private fun checkAppendFailure(source: Any, expected: System.Exception, lowered: Boolean) {
    var receivers = 0
    var elements = 0
    fun receiver(): Any { receivers++; return source }
    fun element(): Int { elements++; return 7 }
    var caught: System.Exception? = null
    try {
        if (lowered) (receiver() as MutableList<in Int>).add(element())
        else clrProjectedListAdd<Int>(receiver(), null, element())
    } catch (failure: System.Exception) { caught = failure }
    check(caught === expected && receivers == 1 && elements == 1)
}

class MutableListMutationTests {
    @TestAttribute fun removeAtAndIteratorRemoveUseTheEligibleList() {
        checkRemoval(RawMutableList(), 7, 9)
        val dictionary = MutableListWithDictionaryStorage()
        dictionary.Add("map", 17)
        checkRemoval(dictionary, "x", "y")
        check(dictionary.Count == 1)
    }

    @TestAttribute fun liveSubListWritesReachRawAndIndependentLists() {
        checkSliceWrites(RawMutableList(), 7, 9)
        val dictionary = MutableListWithDictionaryStorage()
        dictionary.Add("map", 17)
        checkSliceWrites(dictionary, "x", "y")
        check(dictionary.Count == 1)
        checkProjectedIteratorWrites(RawMutableList(), 7, 9)
        checkProjectedIteratorWrites(MutableListWithDictionaryStorage(), "x", "y")
    }

    @TestAttribute fun projectedAndExactGenericWritesKeepTheirNativeContract() {
        val source: Any = MutableAndReadOnlyList()
        val projected = source as MutableList<in Int>
        check(projected.set(0, 11) == 7)
        projected.add(1, 13)
        check(projected.size == 3 && projected[0] == 11 && projected[1] == 13)
        val dictionary = MutableListWithDictionaryStorage()
        dictionary.Add("map", 17)
        val selected = (dictionary as Any) as MutableList<String>
        val independent: MutableList<in String> = selected
        check(independent.set(0, "set") == "x")
        independent.add(1, "inserted")
        check(independent.removeAt(2) == "y" && independent.size == 2)
        check(independent[0] == "set" && independent[1] == "inserted" && dictionary.Count == 1)
        insertThroughProjectedParameter(selected)
        check(selected.size == 3 && selected[0] == "parameter" && dictionary.Count == 1)
        val native = DualMutableList()
        val exact = (native as Any) as MutableList<Any?>
        check(exact.set(0, "exact") == "a")
        exact.add(1, "inserted")
        check(exact.removeAt(0) == "exact" && exact[0] == "inserted")
        check(exact.size == 3 && native.Count == 2)
    }

    @TestAttribute fun genuineAmbiguityNeverFallsBackToTheRawList() {
        val native = DualMutableList()
        val list = (native as Any) as MutableList<*>
        var rejected = false
        try { list.removeAt(0) } catch (_: IllegalStateException) { rejected = true }
        check(rejected && native.Count == 2)
        check(((native as Any) as MutableList<Any?>).size == 3)
    }

    @TestAttribute fun projectedInsertRetainsAnExactSourceWitness() {
        val native = DualMutableList()
        val exact = (native as Any) as MutableList<Any?>
        val projected: MutableList<in Any?> = exact
        projected.add(1, "selected")
        check(exact.size == 4 && exact[0] == "a" && exact[1] == "selected" && exact[2] == "b")
        val integers = (native as Any) as MutableList<Int>
        check(native.Count == 2 && integers[0] == 7 && integers[1] == 9)
    }

    @TestAttribute fun projectedAppendRetainsTheSelectedListAndItsCollectionParent() {
        val native = DualMutableList()
        val exact = (native as Any) as MutableList<Any?>
        val projected: MutableList<in Any?> = exact
        check(projected.add(17) && projected.add(null))
        check(exact.size == 5 && exact[3] == 17 && exact[4] == null)
        val integers = (native as Any) as MutableList<Int>
        check(native.Count == 2 && integers[0] == 7 && integers[1] == 9)

        val mixed = ListWithMutableDictionary()
        val mixedExact = (mixed as Any) as MutableList<Int>
        val mixedProjected: MutableList<in Int> = mixedExact
        check(mixedProjected.add(23) && mixed.Count == 1 && mixedExact[0] == 23)
        val dictionary = MutableListWithDictionaryStorage()
        dictionary.Add("map", 17)
        val independent = (dictionary as Any) as MutableList<String>
        val independentProjected: MutableList<in String> = independent
        check(independentProjected.add("list") && independent.size == 3 && dictionary.Count == 1)
        check(appendThroughProjectedParameter(independent) && independent.size == 4 && independent[3] == "parameter")
        check(dictionary.Count == 1)
        val raw = RawMutableList()
        check(clrProjectedListAdd<String>(raw, null, "raw") && raw.Count == 3)

        val ambiguous = DualMutableList()
        var rejected = false
        try { clrProjectedListAdd<String>(ambiguous, null, "must not choose from this value") }
        catch (_: IllegalStateException) { rejected = true }
        check(rejected && ambiguous.Count == 2)

        var calls = 0
        val backing = mutableListOf<Any?>()
        val authored = object : MutableList<Any?> by backing {
            override fun add(element: Any?): Boolean { calls++; return false }
        }
        val authoredExact = (authored as Any) as MutableList<Any?>
        val authoredProjected: MutableList<in Any?> = authoredExact
        check(!authoredProjected.add("unchanged") && calls == 1 && authoredExact.isEmpty())
        val rawFailure = RawFailureList()
        checkAppendFailure(rawFailure, rawFailure.Failure, false)
        check(rawFailure.Writes == 1 && rawFailure.Count == 2)
        val genericFailure = GenericFailureList()
        checkAppendFailure(genericFailure, genericFailure.Failure, true)
        check(genericFailure.Writes == 1 && genericFailure.Count == 2)
    }

    @TestAttribute fun nativeFailuresKeepIdentityAndEvaluateInputsOnce() {
        val raw = RawFailureList()
        checkFailure(raw, raw.Failure)
        check(raw.Writes == 3 && raw.Count == 2)
        val generic = GenericFailureList()
        checkFailure(generic, generic.Failure)
        check(generic.Writes == 3 && generic.Count == 2)
        val exact = GenericFailureList()
        val erased = GenericFailureList()
        for (operation in 0..3) {
            checkInvariantFailure<Int>((exact as Any) as MutableList<Int>, 7, exact.Failure, operation)
            checkInvariantFailure<Any?>((erased as Any) as MutableList<Any?>, 7, erased.Failure, operation)
        }
        check(exact.Writes == 4 && erased.Writes == 4 && exact.Count == 2 && erased.Count == 2)
        val nativeDynamic = DynamicStringList()
        val dynamic = (nativeDynamic as Any) as MutableList<Any?>
        nativeDynamic.InterfaceChecks = 0
        check(dynamic.size == 2 && dynamic[0] == "a")
        check(nativeDynamic.InterfaceChecks == 0)
    }

    @TestAttribute fun kotlinMutationOverridesRemainAuthoritative() {
        val authored = AuthoredMutationList()
        val source: Any = authored
        check((source as MutableList<*>).removeAt(0) == 7)
        val view = source as MutableList<Any?>
        check(view.set(0, "set") == 9)
        view.add(1, "inserted")
        check(view.removeAt(0) == "set")
        check(authored.sets == 1 && authored.inserts == 1 && authored.removals == 2)
        check(authored.size == 1 && authored[0] == "inserted")
    }
}
