package roundtriptests.operatorresults

import NUnit.Framework.TestAttribute
import roundtrip.operatorresults.*

private class StringDiscardingList :
    MutationListBase<String>(mutableListOf("old"), "base-set", "base-remove") {
    override fun set(index: Int, element: String): String {
        super.set(index, element)
        return "derived-set"
    }
}

private class ReturningList<T>(initial: T, result: T) :
    MutationListBase<T>(mutableListOf(initial), result, result) {
    override fun set(index: Int, element: T): T = super.set(index, element)
}

private class DiscardingList<T>(initial: T, baseResult: T, private val result: T) :
    MutationListBase<T>(mutableListOf(initial), baseResult, baseResult) {
    override fun set(index: Int, element: T): T {
        super.set(index, element)
        return result
    }
}

private class ReturningIndex<T>(initial: T, result: T) : ResultIndex<T>(initial, result) {
    override fun set(index: Int, element: T): T = super.set(index, element)
}

private fun <T> checkImportedSet(initial: T, replacement: T, baseResult: T, derivedResult: T) {
    val returning = ReturningList(initial, baseResult)
    val returningView: MutableList<T> = returning
    check(returningView.set(0, replacement) == baseResult)
    check(returning.writes == 1 && returning[0] == replacement)
    val discarding = DiscardingList(initial, baseResult, derivedResult)
    val discardingView: MutableList<T> = discarding
    check(discardingView.set(0, replacement) == derivedResult)
    check(discarding.writes == 1 && discarding[0] == replacement)
    val ordinary = ResultIndex(initial, baseResult)
    check(ordinary.set(0, replacement) == baseResult)
    check(ordinary.writes == 1 && ordinary.value == replacement)
    val inherited = ReturningIndex(initial, baseResult)
    check(inherited.set(0, replacement) == baseResult)
    check(inherited.writes == 1 && inherited.value == replacement)
}

class ImportedSetResultTests {
    @TestAttribute
    fun importedValueReturningSetKeepsValuesAndBaseDispatch() {
        val original = StringDiscardingList()
        val list: MutableList<String> = original
        check(list.set(0, "new") == "derived-set" && original.writes == 1 && list[0] == "new")
        check(list.removeAt(0) == "base-remove" && list.isEmpty())
        check(list.addAll(0, listOf("again")) && list.size == 1 && list[0] == "again")
        checkImportedSet("old", "new", "base", "derived")
        checkImportedSet(1, 2, 41, 43)
        checkImportedSet<Int?>(null, 5, null, 7)
        val native = System.Collections.Generic.List<Int>()
        native.Add(1)
        val unit: Unit = native.set(0, 2)
        check(unit === Unit && native[0] == 2)
    }
}
