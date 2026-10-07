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

private class InheritedCollection : ForeignCollectionWithIterable<Int>()

private fun <T> appendImportedCollection(values: ImportedCollection<T>, item: T): T {
    values.Add(item)
    return values[values.Count - 1]
}

class ImportedIterableSlotTests {
    @TestAttribute
    fun inheritedNativeSlotsUseTheKotlinReceiversStorageArguments() {
        val first = listOf("first")
        val replacement = listOf("replacement")
        val values = ImportedCollection<List<String>>(first)
        values.Add(first)
        check(values[0] === first)
        values[0] = replacement
        check(values[0] === replacement)
        val add = values::Add
        add(first)
        check(values[1] === first)
        check(appendImportedCollection(values, replacement) === replacement)

        val imported = ForeignCollectionWithIterable<List<String>>()
        imported.Add(first)
        check(imported[0] === first)
        imported[0] = replacement
        check(imported[0] === replacement)
    }

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

        val nullable = ImportedCollection<Int?>(null)
        nullable.Add(7)
        val nullableView: TaggedIterable<Int?> = nullable
        assertTrue(nullableView.iterator().next() == null)
        val nested = ImportedCollection<List<String>>(listOf("iterator"))
        nested.Add(listOf("base"))
        val nestedView: Iterable<List<String>> = nested
        assertEquals("iterator", nestedView.iterator().next()[0])
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
        val nullable = ImportedCollection<Int?>(null)
        nullable.Add(7)
        val nullableCursor = (nullable as Any as RawEnumerable).GetEnumerator()
        assertTrue(nullableCursor.MoveNext())
        assertTrue(nullableCursor.Current == null)
        assertTrue(!nullableCursor.MoveNext())
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
        val inherited = InheritedCollection()
        inherited.Add(7)
        val inheritedView: Iterable<Int> = inherited
        assertTrue(!inheritedView.iterator().hasNext())
        assertTrue(!(inherited as Any as RawEnumerable).GetEnumerator().MoveNext())
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
