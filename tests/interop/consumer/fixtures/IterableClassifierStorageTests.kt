import NUnit.Framework.TestAttribute

private class IterableClassifierGenericDictionary : IterableClassifierStorage.GenericDictionary()
private class IterableClassifierReadOnlyDictionary : IterableClassifierStorage.ReadOnlyDictionary()
private class IterableClassifierMixedSet : IterableClassifierStorage.SetAndDictionary()
private class IterableClassifierMixedList : IterableClassifierStorage.ListAndDictionary()

class IterableClassifierStorageTests {
    @TestAttribute fun unrelatedGenericMapSafeCastKeepsItsExistingView() {
        val value: Any = IterableClassifierStorage.GenericDictionary()
        check(value as? Map<*, *> === value)
    }

    private inline fun <reified T> matches(value: Any): Boolean = value is T
    private fun rejects(value: Any) {
        check(value !is Iterable<*>)
        check(value !is MutableIterable<*>)
        check(value as? Iterable<*> == null)
        check(!matches<Iterable<*>>(value))
        check(!matches<MutableIterable<*>>(value))
        var failed = false
        try { value as Iterable<*> } catch (_: ClassCastException) { failed = true }
        check(failed)
    }

    @TestAttribute fun genericOnlyDictionaryDoesNotGrantIterableIdentity() {
        rejects(IterableClassifierStorage.GenericDictionary())
        rejects(IterableClassifierGenericDictionary())
    }

    @TestAttribute fun readOnlyDictionaryDoesNotGrantIterableIdentity() {
        rejects(IterableClassifierStorage.ReadOnlyDictionary())
        rejects(IterableClassifierReadOnlyDictionary())
    }

    @TestAttribute fun multipleDictionaryConstructionsDoNotThrow() {
        rejects(IterableClassifierStorage.MultipleReadOnlyDictionaries())
    }

    @TestAttribute fun setIdentitySurvivesDictionaryStorage() {
        for (value in arrayOf<Any>(IterableClassifierStorage.SetAndDictionary(), IterableClassifierMixedSet())) {
            check(value is Iterable<*>)
            check(value is MutableIterable<*>)
            check(matches<Iterable<*>>(value))
            check(value as? Iterable<*> === value)
            check(value is MutableSet<*>)
        }
    }

    @TestAttribute fun listIdentitySurvivesDictionaryStorage() {
        for (value in arrayOf<Any>(IterableClassifierStorage.ListAndDictionary(), IterableClassifierMixedList())) {
            check(value is Iterable<*>)
            check(value is MutableIterable<*>)
            check(matches<Iterable<*>>(value))
            check(value as? Iterable<*> === value)
            check(value is MutableList<*>)
        }
    }
}
