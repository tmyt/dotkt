import NUnit.Framework.TestAttribute

private class IterableClassifierDictionary : System.Collections.Generic.Dictionary<String, Int>()

private class IterableClassifierExplicitDictionary : System.Collections.Generic.Dictionary<String, Int>(),
    Iterable<System.Collections.Generic.KeyValuePair2<String, Int>> {
    override fun iterator(): Iterator<System.Collections.Generic.KeyValuePair2<String, Int>> = emptyList<System.Collections.Generic.KeyValuePair2<String, Int>>().iterator()
}

class IterableClassifierTests {
    private inline fun <reified T> matches(value: Any?): Boolean = value is T
    private inline fun <reified T> safe(value: Any?): T? = value as? T
    private inline fun <reified T> cast(value: Any?): T = value as T

    private fun rejects(value: Any) {
        check(value !is Iterable<*>)
        check(value !is MutableIterable<*>)
        check(value as? Iterable<*> == null)
        check(value as? MutableIterable<*> == null)
        check(!matches<Iterable<*>>(value))
        check(!matches<Iterable<*>?>(value))
        check(!matches<MutableIterable<*>>(value))
        check(safe<Iterable<*>>(value) == null)
        check(safe<MutableIterable<*>>(value) == null)
        var failed = false
        try { value as Iterable<*> } catch (_: ClassCastException) { failed = true }
        check(failed)
        failed = false
        try { cast<Iterable<*>>(value) } catch (_: ClassCastException) { failed = true }
        check(failed)
    }

    @TestAttribute fun stringsHaveNoIterableIdentity() { rejects("abc") }

    @TestAttribute fun arraysHaveNoIterableIdentity() {
        rejects(arrayOf("a"))
        rejects(intArrayOf(1))
        rejects(charArrayOf('a'))
        rejects(emptyArray<String>())
    }

    @TestAttribute fun dictionariesHaveNoImplicitIterableIdentity() {
        rejects(mapOf("a" to 1))
        rejects(System.Collections.Generic.Dictionary<String, Int>())
        rejects(IterableClassifierDictionary())
    }

    @TestAttribute fun explicitIterableIdentityWinsOverDictionaryStorage() {
        val dictionary = IterableClassifierExplicitDictionary()
        dictionary.Add("base", 1)
        val value: Any = dictionary
        check(value is Iterable<*>)
        check(matches<Iterable<*>>(value))
        check(value !is MutableIterable<*>)
        check(!matches<MutableIterable<*>>(value))
        check(!(value as Iterable<*>).iterator().hasNext())
        check(!cast<Iterable<*>>(value).iterator().hasNext())
    }

    @TestAttribute fun foreignListsRetainIterableIdentity() {
        val value: Any = System.Collections.Generic.List<Int>()
        check(value is Iterable<*>)
        check(value is MutableIterable<*>)
        check(matches<Iterable<*>>(value))
        check(matches<MutableIterable<*>>(value))
        check(safe<Iterable<*>>(value) === value)
        check(cast<Iterable<*>>(value) === value)
    }

    @TestAttribute fun nullableIterableTestsPreserveNullability() {
        check(!matches<Iterable<*>>(null))
        check(matches<Iterable<*>?>(null))
        check(matches<MutableIterable<*>?>(null))
        check(safe<Iterable<*>>(null) == null)
        check(cast<Iterable<*>?>(null) == null)
    }
}
