import NUnit.Framework.TestAttribute

private inline fun <reified T> familyIs(value: Any?): Boolean = value is T
private inline fun <reified T> familySafe(value: Any?): T? = value as? T
private inline fun <reified T> familyCast(value: Any?): T = value as T
private inline fun <reified T> familyRejects(value: Any) {
    check(!familyIs<T>(value))
    check(familySafe<T>(value) == null)
    var rejected = false
    try { familyCast<T>(value) } catch (_: ClassCastException) { rejected = true }
    check(rejected)
}
private inline fun <reified T> familyAccepts(value: Any) {
    check(familyIs<T>(value))
    check(familySafe<T>(value) === value)
    check(familyCast<T>(value) === value)
}
private class FamilyReadOnlyCollection<E>(private val items: Collection<E>) : Collection<E> by items
private class FamilyReadOnlySet<E>(private val items: Set<E>) : Set<E> by items
private class FamilyMutableSet<E>(private val items: MutableSet<E>) : MutableSet<E> by items
private class FamilyEvaluation(private val value: Any) {
    var calls = 0
    fun next(): Any { calls++; return value }
}

class ReifiedCollectionClassifierTests {
    @TestAttribute fun unrelatedValuesDoNotBecomeSetsOrCollections() {
        for (value in arrayOf<Any>(Any(), "text", arrayOf(1), intArrayOf(1), mapOf(1 to 2))) {
            familyRejects<Collection<*>>(value)
            familyRejects<MutableCollection<*>>(value)
            familyRejects<Set<*>>(value)
            familyRejects<MutableSet<*>>(value)
            familyRejects<Collection<*>?>(value)
            familyRejects<MutableCollection<*>?>(value)
            familyRejects<Set<*>?>(value)
            familyRejects<MutableSet<*>?>(value)
        }
    }

    @TestAttribute fun listsDoNotBecomeSets() {
        for (value in arrayOf<Any>(mutableListOf(1), CollUserList<Int>())) {
            familyAccepts<Collection<*>>(value)
            familyAccepts<MutableCollection<*>>(value)
            familyRejects<Set<*>>(value)
            familyRejects<MutableSet<*>>(value)
        }
    }

    @TestAttribute fun setsRetainBothCollectionAndSetIdentity() {
        for (value in arrayOf<Any>(mutableSetOf(1), FamilyMutableSet(mutableSetOf(1)))) {
            familyAccepts<Collection<*>>(value)
            familyAccepts<MutableCollection<*>>(value)
            familyAccepts<Set<*>>(value)
            familyAccepts<MutableSet<*>>(value)
            check(value is Collection<*> && value is MutableCollection<*>)
            check(value is Set<*> && value is MutableSet<*>)
        }
    }

    @TestAttribute fun nominalReadOnlyCollectionsDoNotGainMutability() {
        val value: Any = FamilyReadOnlyCollection(listOf(1))
        familyAccepts<Collection<*>>(value)
        familyRejects<MutableCollection<*>>(value)
        familyRejects<Set<*>>(value)
        familyRejects<MutableSet<*>>(value)
    }

    @TestAttribute fun nominalReadOnlySetsDoNotGainMutability() {
        val value: Any = FamilyReadOnlySet(setOf(1))
        familyAccepts<Collection<*>>(value)
        familyAccepts<Set<*>>(value)
        familyRejects<MutableCollection<*>>(value)
        familyRejects<MutableSet<*>>(value)
    }

    @TestAttribute fun nullableClassifiersRetainNull() {
        check(familyIs<Collection<*>?>(null) && familyIs<MutableCollection<*>?>(null))
        check(familyIs<Set<*>?>(null) && familyIs<MutableSet<*>?>(null))
        check(!familyIs<Collection<*>>(null) && !familyIs<MutableCollection<*>>(null))
        check(!familyIs<Set<*>>(null) && !familyIs<MutableSet<*>>(null))
        check(familyCast<Collection<*>?>(null) == null)
        check(familyCast<MutableCollection<*>?>(null) == null)
        check(familyCast<Set<*>?>(null) == null)
        check(familyCast<MutableSet<*>?>(null) == null)
        check(familySafe<Collection<*>>(null) == null)
        check(familySafe<MutableCollection<*>>(null) == null)
        check(familySafe<Set<*>>(null) == null)
        check(familySafe<MutableSet<*>>(null) == null)
    }

    @TestAttribute fun classifierOperandsAreEvaluatedOnce() {
        val value: Any = mutableSetOf(7)
        val source = FamilyEvaluation(value)
        check(familyIs<Set<*>>(source.next()) && source.calls == 1)
        check(familySafe<MutableSet<*>>(source.next()) === value && source.calls == 2)
        check(familyCast<Collection<*>>(source.next()) === value && source.calls == 3)
        check(familyCast<MutableCollection<*>>(source.next()) === value && source.calls == 4)
        val invalid = FamilyEvaluation(Any())
        check(!familyIs<Set<*>>(invalid.next()) && invalid.calls == 1)
        check(familySafe<MutableSet<*>>(invalid.next()) == null && invalid.calls == 2)
        var rejected = false
        try { familyCast<Collection<*>>(invalid.next()) } catch (_: ClassCastException) { rejected = true }
        check(rejected && invalid.calls == 3)
    }
}
