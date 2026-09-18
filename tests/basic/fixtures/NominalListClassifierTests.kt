import NUnit.Framework.TestAttribute

private class NominalReadOnlyList<E>(private val elements: List<E>) : List<E> by elements
private fun nominalList(value: Any?): Boolean = value is List<*>
private fun nominalMutableList(value: Any?): Boolean = value is MutableList<*>
private inline fun <reified T> nominalIs(value: Any?): Boolean = value is T
private inline fun <reified T> nominalSafe(value: Any?): T? = value as? T
private inline fun <reified T> nominalChecked(value: Any?): T = value as T
private fun concreteNominalList(value: Any): List<Any> = value as List<Any>
private fun concreteNominalMutableList(value: Any): MutableList<Any> = value as MutableList<Any>
private fun safeConcreteNominalList(value: Any): List<Any>? = value as? List<Any>
private fun safeConcreteNominalMutableList(value: Any): MutableList<Any>? = value as? MutableList<Any>
private class NominalEvaluation(private val value: Any) {
    var count = 0
    fun next(): Any { count++; return value }
}

class NominalListClassifierTests {
    @TestAttribute fun mutableImplementationRetainsBothIdentities() {
        for (value in arrayOf<Any>(CollUserList<Int>(), CollUserList<String>())) {
            check(nominalList(value))
            check(nominalMutableList(value))
            check(value is Collection<*>)
        }
    }

    @TestAttribute fun checkedCastsPreserveIdentity() {
        val value: Any = CollUserList<Int>()
        check((value as List<*>) === value)
        check((value as MutableList<*>) === value)
    }

    @TestAttribute fun safeCastsPreserveIdentity() {
        val value: Any = CollUserList<Int>()
        check((value as? List<*>) === value)
        check((value as? MutableList<*>) === value)
    }

    @TestAttribute fun reifiedOperationsPreserveBothIdentities() {
        val value: Any = CollUserList<Int>()
        check(nominalIs<List<*>>(value))
        check(nominalIs<MutableList<*>>(value))
        check(nominalSafe<List<*>>(value) === value)
        check(nominalSafe<MutableList<*>>(value) === value)
        check(nominalChecked<List<*>>(value) === value)
        check(nominalChecked<MutableList<*>>(value) === value)
    }

    @TestAttribute fun checkedReadOnlyMembers() {
        val value: Any = CollUserList<Int>(mutableListOf(7, 9))
        val list = value as List<*>
        check(list.size == 2)
        check(list[0] == 7)
        check(list.iterator().next() == 7)
        check(list.indexOf(9) == 1)
        check(list.listIterator().next() == 7)
        check(list.subList(1, 2)[0] == 9)
    }

    @TestAttribute fun checkedMutableMembers() {
        val value: Any = CollUserList<Int>(mutableListOf(7, 9))
        val list = value as MutableList<*>
        check(list.removeAt(0) == 7)
        list.clear()
        check(list.isEmpty())
    }

    @TestAttribute fun smartCastMembers() {
        val value: Any = CollUserList<Int>(mutableListOf(7, 9))
        check(value is MutableList<*>)
        check(value[0] == 7)
        check(value.size == 2)
        check(value.removeAt(0) == 7)
        check(value.iterator().next() == 9)
    }

    @TestAttribute fun readOnlyImplementationDoesNotGainMutability() {
        val value: Any = NominalReadOnlyList(listOf(7, 9))
        check(nominalList(value))
        check(!nominalMutableList(value))
        check((value as List<*>)[0] == 7)
        check(value as? MutableList<*> == null)
        var rejected = false
        try { value as MutableList<*> } catch (_: ClassCastException) { rejected = true }
        check(rejected)
    }

    @TestAttribute fun nullabilityIsPreserved() {
        val value: Any? = null
        check(!nominalList(value) && !nominalMutableList(value))
        check(value is List<*>? && value is MutableList<*>?)
        check(value as List<*>? == null)
        check(value as MutableList<*>? == null)
        check(nominalIs<List<*>?>(value))
        check(nominalChecked<MutableList<*>?>(value) == null)
        var rejected = false
        try { value as List<*> } catch (_: ClassCastException) { rejected = true }
        check(rejected)
    }

    @TestAttribute fun unrelatedStorageIsNotAList() {
        for (value in arrayOf<Any>(arrayOf(1, 2), intArrayOf(1, 2), mapOf(1 to 2), setOf(1), "text")) {
            check(!nominalList(value)) { "List classifier accepted $value" }
            check(!nominalMutableList(value)) { "MutableList classifier accepted $value" }
            check(!nominalIs<List<*>>(value)) { "Reified List classifier accepted $value" }
            check(!nominalIs<MutableList<*>>(value)) { "Reified MutableList classifier accepted $value" }
            check(value as? List<*> == null) { "List safe cast accepted $value" }
            check(nominalSafe<MutableList<*>>(value) == null) { "Reified MutableList safe cast accepted $value" }
            var rejected = false
            try { value as List<*> } catch (_: ClassCastException) { rejected = true }
            check(rejected) { "List checked cast accepted $value" }
            rejected = false
            try { nominalChecked<MutableList<*>>(value) } catch (_: ClassCastException) { rejected = true }
            check(rejected) { "Reified MutableList checked cast accepted $value" }
        }
    }

    @TestAttribute fun concreteAnyReturnsKeepTheirClrInterfaces() {
        val value: Any = CollUserList<Any>(mutableListOf<Any>("entry"))
        check(concreteNominalList(value) === value)
        check(concreteNominalMutableList(value) === value)
        check(safeConcreteNominalList(value) === value)
        check(safeConcreteNominalMutableList(value) === value)
        check(concreteNominalList(value)[0] == "entry")
        concreteNominalMutableList(value).add(7)
        check(concreteNominalList(value).size == 2)
        check(safeConcreteNominalList(Any()) == null)
        check(safeConcreteNominalMutableList(Any()) == null)
    }

    @TestAttribute fun classifierOperandsAreEvaluatedOnce() {
        val value: Any = CollUserList<Int>(mutableListOf(7, 9))
        val source = NominalEvaluation(value)
        check(source.next() is List<*>)
        check(source.count == 1)
        check((source.next() as? MutableList<*>) === value)
        check(source.count == 2)
        check((source.next() as List<*>) === value)
        check(source.count == 3)
        check(nominalIs<MutableList<*>>(source.next()))
        check(source.count == 4)
        check(nominalSafe<List<*>>(source.next()) === value)
        check(source.count == 5)
        check(nominalChecked<MutableList<*>>(source.next()) === value)
        check(source.count == 6)
        check((source.next() as List<*>).size == 2)
        check(source.count == 7)
        check((source.next() as MutableList<*>)[0] == 7)
        check(source.count == 8)
    }
}
