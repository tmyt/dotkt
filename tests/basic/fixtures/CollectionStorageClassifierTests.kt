import NUnit.Framework.TestAttribute

class CollectionStorageClassifierTests {
    private inline fun <reified T> matches(value: Any?): Boolean = value is T
    private inline fun <reified T> safe(value: Any?): T? = value as? T
    private inline fun <reified T> cast(value: Any?): T = value as T

    private inline fun <reified T> rejects(value: Any) {
        check(!matches<T>(value))
        check(safe<T>(value) == null)
        var failed = false
        try { cast<T>(value) } catch (_: ClassCastException) { failed = true }
        check(failed)
    }

    private fun rejectsStorage(value: Any) {
        check(value !is Collection<*>)
        check(value !is MutableCollection<*>)
        check(value !is List<*>)
        check(value !is MutableList<*>)
        check(value as? List<*> == null)
        check(value as? Collection<*> == null)
        check(value as? MutableList<*> == null)
        check(value as? MutableCollection<*> == null)
        rejects<Collection<*>>(value)
        rejects<MutableCollection<*>>(value)
        rejects<List<*>>(value)
        rejects<MutableList<*>>(value)
        rejects<Collection<*>?>(value)
        rejects<MutableCollection<*>?>(value)
        rejects<List<*>?>(value)
        rejects<MutableList<*>?>(value)
        var failed = false
        try { value as List<*> } catch (_: ClassCastException) { failed = true }
        check(failed)
    }

    @TestAttribute fun arraysDoNotAcquireCollectionClassifiers() {
        rejectsStorage(arrayOf(1))
        rejectsStorage(arrayOf("a"))
        rejectsStorage(intArrayOf(1))
        rejectsStorage(charArrayOf('a'))
    }

    @TestAttribute fun dictionaryStorageDoesNotAcquireCollectionClassifiers() {
        rejectsStorage(mapOf("a" to 1))
        rejectsStorage(System.Collections.Generic.Dictionary<String, Int>())
    }

    @TestAttribute fun orderedDictionaryStorageDoesNotAcquireCollectionClassifiers() {
        rejectsStorage(mutableMapOf("a" to 1))
        rejectsStorage(linkedMapOf("a" to 1))
        rejectsStorage(LinkedHashMap<String, Int>())
        rejectsStorage(buildMap { put("a", 1) })
    }

    @TestAttribute fun realListsRetainCollectionClassifiers() {
        for (value in arrayOf<Any>(mutableListOf(1), System.Collections.Generic.List<Int>())) {
            check(matches<Collection<*>>(value))
            check(matches<MutableCollection<*>>(value))
            check(matches<List<*>>(value))
            check(matches<MutableList<*>>(value))
            check(safe<List<*>>(value) === value)
            check(cast<MutableCollection<*>>(value) === value)
        }
    }

    @TestAttribute fun nullableClassifiersStillAcceptNull() {
        check(matches<List<*>?>(null))
        check(matches<MutableCollection<*>?>(null))
        check(!matches<List<*>>(null))
        check(!matches<MutableCollection<*>>(null))
        check(cast<List<*>?>(null) == null)
    }
}
