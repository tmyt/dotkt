package constructedvaluearguments

import NUnit.Framework.TestAttribute

private interface Entry<K, V> { val key: K; val value: V }
private class ConcreteEntry<K, V>(override val key: K, override val value: V) : Entry<K, V>
private class Holder<T>(val value: T)
private class BoundedHolder<T : Entry<Int, String>>(val entry: T)

private fun <K, V> emptyEntries(): MutableSet<Entry<K, V>> = LinkedHashSet<Entry<K, V>>()
private fun <K, V> populatedEntries(key: K, value: V): MutableSet<Entry<K, V>> {
    val entries = LinkedHashSet<Entry<K, V>>()
    entries.add(ConcreteEntry(key, value))
    return entries
}
private fun <T> nestedHolder(value: T): Holder<Holder<T>> = Holder(Holder(value))
private inline fun <reified T> matches(value: Any): Boolean = value is T

class ConstructedValueArgumentTests {
    @TestAttribute fun collectionConstructionUsesItsElementValueRepresentation() {
        check(emptyEntries<Int, String>().isEmpty())
        val entries = populatedEntries(7, "seven")
        val entry = entries.single()
        check(entry.key == 7 && entry.value == "seven")
        check(!entries.add(entry))
        check(entries.remove(entry))
        check(entries.isEmpty())
    }

    @TestAttribute fun nestedConstructionKeepsValuesAndReifiedIdentity() {
        val nested = nestedHolder(42)
        check(nested.value.value == 42)
        check(nestedHolder("nested").value.value == "nested")
        check(matches<Holder<Holder<Int>>>(nested))
        check(matches<Holder<*>>(nested))
    }

    @TestAttribute fun constrainedAndNativeConstructionsKeepTheirContracts() {
        val entry = ConcreteEntry(3, "three")
        val bounded = BoundedHolder(entry)
        check(bounded.entry === entry && bounded.entry.key == 3)
        val native = System.Collections.Generic.List<ConcreteEntry<Int, String>>()
        native.Add(entry)
        check(native[0] === entry && native[0].value == "three")
    }
}
