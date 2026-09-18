import NUnit.Framework.TestAttribute

private class MixedCollectionList : IterableClassifierStorage.ListAndDictionary()
private class MixedCollectionSet : IterableClassifierStorage.SetAndDictionary()

class MixedCollectionClassifierTests {
    private fun values(): Array<Any> = arrayOf(
        IterableClassifierStorage.ListAndDictionary(),
        IterableClassifierStorage.SetAndDictionary(),
    )
    private fun matches(value: Any?): Boolean = value is Collection<*>
    private fun nullableMatches(value: Any?): Boolean = value is Collection<*>?
    private fun safe(value: Any?): Collection<*>? = value as? Collection<*>
    private fun checked(value: Any): Collection<*> = value as Collection<*>
    private fun nullableChecked(value: Any?): Collection<*>? = value as Collection<*>?
    private inline fun <reified T> reifiedMatches(value: Any?): Boolean = value is T
    private inline fun <reified T> reifiedSafe(value: Any?): T? = value as? T
    private inline fun <reified T> reifiedChecked(value: Any?): T = value as T

    @TestAttribute fun independentListAndSetRetainCollectionClassifier() {
        for (value in values()) check(matches(value))
    }

    @TestAttribute fun safeCollectionCastPreservesForeignIdentity() {
        for (value in values()) check(safe(value) === value)
    }

    @TestAttribute fun checkedCollectionCastPreservesForeignIdentity() {
        for (value in values()) check(checked(value) === value)
    }

    @TestAttribute fun reifiedCollectionClassifiersPreserveForeignIdentity() {
        for (value in values()) {
            check(reifiedMatches<Collection<*>>(value))
            check(reifiedSafe<Collection<*>>(value) === value)
            check(reifiedChecked<Collection<*>>(value) === value)
        }
    }

    @TestAttribute fun nullableCollectionClassifiersPreserveNullability() {
        check(nullableMatches(null))
        check(!matches(null))
        check(safe(null) == null)
        check(nullableChecked(null) == null)
        check(reifiedMatches<Collection<*>?>(null))
        for (value in values()) {
            check(nullableMatches(value))
            check(nullableChecked(value) === value)
            check(reifiedMatches<Collection<*>?>(value))
        }
    }

    @TestAttribute fun kotlinSubclassesRetainTheForeignCollectionContract() {
        for (value in arrayOf<Any>(MixedCollectionList(), MixedCollectionSet())) {
            check(matches(value))
            check(safe(value) === value)
            check(checked(value) === value)
        }
    }

    @TestAttribute fun storageWithoutAnIndependentContractStillFails() {
        for (value in arrayOf<Any>(arrayOf(1), intArrayOf(1), mapOf("a" to 1),
            mutableMapOf("a" to 1), IterableClassifierStorage.GenericDictionary(),
            IterableClassifierStorage.ReadOnlyDictionary())) {
            check(!matches(value))
            check(safe(value) == null)
            var failed = false
            try { checked(value) } catch (_: ClassCastException) { failed = true }
            check(failed)
        }
    }

    private fun checkIteration(value: Any) {
        check(value is Collection<*>)
        check(!value.iterator().hasNext())
    }

    @TestAttribute fun smartCastIterationKeepsTheEligibleReceiver() {
        for (value in values()) checkIteration(value)
    }
}
