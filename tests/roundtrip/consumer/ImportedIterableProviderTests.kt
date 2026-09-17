package roundtriptests.iteratorproviders
import roundtrip.iteratorproviders.DefaultIterable
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import roundtrip.iterableidentity.TaggedIterable
import System.Collections.IEnumerable as RawEnumerable

private class DefaultCollection : DefaultIterable<Int>
private class ListCollection : System.Collections.Generic.List<Int>(), TaggedIterable<Int> {
    override fun iterator(): Iterator<Int> = listOf(42).iterator()
}
private abstract class AbstractCollection : System.Collections.ObjectModel.Collection<Int>(), TaggedIterable<Int> {
    abstract override fun iterator(): Iterator<Int>
}
private class ConcreteCollection : AbstractCollection() {
    override fun iterator(): Iterator<Int> = listOf(42).iterator()
}

@JvmInline private value class SlotValue(val value: Int)
private class ValueCollection : System.Collections.ObjectModel.Collection<SlotValue?>(), TaggedIterable<SlotValue?> {
    override fun iterator(): Iterator<SlotValue?> = listOf(SlotValue(42), null).iterator()
}

class ImportedIterableProviderTests {
    @TestAttribute fun importedDefault() {
        val value = DefaultCollection()
        val view: Iterable<Int> = value
        assertTrue(!view.iterator().hasNext())
        assertTrue(!(value as Any as RawEnumerable).GetEnumerator().MoveNext())
    }
    @TestAttribute fun nonvirtualListBase() {
        val value = ListCollection()
        value.Add(7)
        val view: Iterable<Int> = value
        assertTrue(view.iterator().next() == 42)
        val raw = (value as Any as RawEnumerable).GetEnumerator()
        assertTrue(raw.MoveNext())
        assertTrue(raw.Current as Int == 42)
    }
    @TestAttribute fun abstractProvider() {
        val value = ConcreteCollection()
        value.Add(7)
        val view: Iterable<Int> = value
        assertTrue(view.iterator().next() == 42)
        val raw = (value as Any as RawEnumerable).GetEnumerator()
        assertTrue(raw.MoveNext())
        assertTrue(raw.Current as Int == 42)
    }
    @TestAttribute fun localNullableValueElement() {
        val value = ValueCollection()
        value.Add(SlotValue(7))
        val view: Iterable<SlotValue?> = value
        val iterator = view.iterator()
        assertTrue(iterator.next()?.value == 42)
        assertTrue(iterator.next() == null)
        assertTrue(!iterator.hasNext())
    }
}
