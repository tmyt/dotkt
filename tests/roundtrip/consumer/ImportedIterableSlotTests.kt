package roundtriptests.iterableslots

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import System.Collections.IEnumerable as RawEnumerable
import roundtrip.iterableidentity.TaggedIterable
import roundtrip.iterableidentity.ForeignCollectionWithIterable

private class ImportedCollection<T>(private val payload: T) :
    System.Collections.ObjectModel.Collection<T>(), TaggedIterable<T> {
    override fun iterator(): Iterator<T> = listOf(payload).iterator()
}

private interface LocalTagged<T> : Iterable<T>
private class LocalCollection : System.Collections.ObjectModel.Collection<Int>(), LocalTagged<Int> {
    override fun iterator(): Iterator<Int> = listOf(42).iterator()
}

private class DerivedCollection : ForeignCollectionWithIterable<Int>() {
    override fun iterator(): Iterator<Int> = listOf(42).iterator()
}

class ImportedIterableSlotTests {
    @TestAttribute
    fun importedInterfaceUsesKotlinIteratorForValueAndReferenceElements() {
        val ints = ImportedCollection(42)
        ints.Add(7)
        val taggedInts: TaggedIterable<Int> = ints
        assertEquals(42, taggedInts.iterator().next())
        val iterableInts: Iterable<Int> = ints
        assertEquals(42, iterableInts.iterator().next())

        val words = ImportedCollection("iterator")
        words.Add("base")
        val taggedWords: TaggedIterable<String> = words
        assertEquals("iterator", taggedWords.iterator().next())
        val iterableWords: Iterable<String> = words
        assertEquals("iterator", iterableWords.iterator().next())
    }

    @TestAttribute
    fun nongenericClrSlotUsesTheSameKotlinIterator() {
        val ints = ImportedCollection(42)
        ints.Add(7)
        val cursor = (ints as Any as RawEnumerable).GetEnumerator()
        assertTrue(cursor.MoveNext())
        assertEquals(42, cursor.Current as Int)
        assertTrue(!cursor.MoveNext())
        val words = ImportedCollection("iterator")
        words.Add("base")
        val wordCursor = (words as Any as RawEnumerable).GetEnumerator()
        assertTrue(wordCursor.MoveNext())
        assertEquals("iterator", wordCursor.Current as String)
        assertTrue(!wordCursor.MoveNext())
    }

    @TestAttribute
    fun localInterfaceAndImportedBasePreserveIteratorOverrides() {
        val local = LocalCollection()
        local.Add(7)
        val localView: Iterable<Int> = local
        assertEquals(42, localView.iterator().next())
        val derived = DerivedCollection()
        derived.Add(7)
        val derivedView: Iterable<Int> = derived
        assertEquals(42, derivedView.iterator().next())
    }

    @TestAttribute
    fun foreignCollectionWithoutKotlinIteratorKeepsItsOwnSlots() {
        val values = System.Collections.ObjectModel.Collection<Int>()
        values.Add(7)
        val cursor = (values as Any as RawEnumerable).GetEnumerator()
        assertTrue(cursor.MoveNext())
        assertEquals(7, cursor.Current as Int)
        assertTrue(!cursor.MoveNext())
    }
}
