import NUnit.Framework.TestAttribute

private class IterableClassifierGenericDictionary : IterableClassifierStorage.GenericDictionary()
private class IterableClassifierReadOnlyDictionary : IterableClassifierStorage.ReadOnlyDictionary()

class IterableClassifierStorageTests {
    private inline fun <reified T> matches(value: Any): Boolean = value is T
    private fun rejects(value: Any) {
        check(value !is Iterable<*>)
        check(value !is MutableIterable<*>)
        check(value as? Iterable<*> == null)
        check(!matches<Iterable<*>>(value))
        check(!matches<MutableIterable<*>>(value))
    }

    @TestAttribute fun genericOnlyDictionaryDoesNotGrantIterableIdentity() {
        rejects(IterableClassifierStorage.GenericDictionary())
        rejects(IterableClassifierGenericDictionary())
    }

    @TestAttribute fun readOnlyDictionaryDoesNotGrantIterableIdentity() {
        rejects(IterableClassifierStorage.ReadOnlyDictionary())
        rejects(IterableClassifierReadOnlyDictionary())
    }
}
