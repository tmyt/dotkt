package roundtriptests.collectionarrayidentity

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import roundtrip.collectionarrayidentity.*
import GenericValueInterop.CollectionStorageApi

class CollectionArrayIdentityTests {
    @TestAttribute
    fun inheritedArrayMethodUsesTheAccessedOwnersGenericFrame() {
        val sink = ArraySinkChild<String, Int>()
        assertEquals(2, sink.acceptValues(arrayOf<Int?>(null, 23)))
    }

    @TestAttribute
    fun arrayAndVarargMetadataRetainReadOnlyElementTypes() {
        val values: Array<List<String>> = arrayOf(listOf("first"))
        val returned: Array<List<String>> = collectionArrayIdentity(values)
        assertTrue(returned === values)
        assertEquals("first", returned[0][0])
        assertEquals(1, collectionVarargSize(listOf("second")))
        val holder = CollectionArrays(values)
        assertTrue(holder.values === values)
        val replacement: Array<List<String>> = arrayOf(listOf("replacement"))
        holder.values = replacement
        assertTrue(holder.values === replacement)
    }

    @TestAttribute
    fun readOnlyOnlyElementsKeepIdentityAcrossGenericArrayCalls() {
        val element: Collection<ArrayElementBase> = ReadOnlyElements(listOf(ArrayElementBase(17)))
        val values: Array<Collection<ArrayElementBase>> = arrayOf(element)
        val returned = genericArrayIdentity(values)
        assertTrue(returned === values)
        assertTrue(returned[0] === element)
        assertEquals(17, returned[0].first().value)
        val iterator = collectionIteratorReference<ArrayElementBase>()(element)
        assertEquals(17, iterator.next().value)
        val nested: Collection<Collection<ArrayElementBase>> = ReadOnlyElements(listOf(element))
        assertTrue(nested.containsAll(listOf(element)))
        val physical = ReadOnlyElements(listOf(2, 3))
        val copied = CollectionStorageApi.Copy<Int>(physical)
        assertEquals(2, copied[0])
        assertEquals(3, copied[1])
        assertTrue(CollectionStorageApi.Contains<Int>(physical, 3))
        assertTrue(!CollectionStorageApi.Contains<Int>(physical, 5))
        assertEquals(1, CollectionStorageApi.IndexOf<Int>(ReadOnlyListElements(listOf(2, 3)), 3))
    }

    @TestAttribute
    fun readOnlyCollectionCheckedCastsThrowKotlinClassCastException() {
        val value: Any = ReadOnlyElements(listOf(17))
        assertTrue(!collectionIsInstance<MutableCollection<Int>>(value))
        assertTrue(!nullableCollectionIsInstance<MutableCollection<Int>>(value))
        assertTrue(nullableCollectionIsInstance<MutableCollection<Int>>(null))
        assertTrue(!capturedCollectionIsInstance<MutableCollection<Int>>()(value))
        assertTrue(collectionSafeCast<MutableCollection<Int>>(value) == null)
        var rejected = false
        try {
            collectionCheckedCast<MutableCollection<Int>>(value)
        } catch (_: ClassCastException) {
            rejected = true
        }
        assertTrue(rejected)
        assertTrue(collectionCheckedCast<Collection<Int>>(value) === value)
        assertTrue(collectionCheckedCast<MutableCollection<Int>?>(null) == null)
        val mutable: Any = mutableListOf(19)
        assertTrue(collectionCheckedCast<MutableCollection<Int>>(mutable) === mutable)
    }

    @TestAttribute
    fun projectedCollectionArraysRetainReferenceCovariance() {
        val values: Array<Collection<ArrayElementDerived>> = arrayOf(listOf(ArrayElementDerived()))
        assertEquals(23, readProjectedCollections(values))
    }

    @TestAttribute
    fun collectionArrayWritesRetainElementCovariance() {
        val values: Array<Collection<ArrayElementBase>> = arrayOf(listOf(ArrayElementBase(1)))
        values[0] = listOf(ArrayElementDerived())
        assertEquals(23, values[0].first().value)
    }

    @TestAttribute
    fun arrayAndInvariantMapShareTheSourceTypeParameterWithoutCopying() {
        val element: Collection<ArrayElementBase> = ReadOnlyElements(listOf(ArrayElementBase(17)))
        val values: Array<Collection<ArrayElementBase>> = arrayOf(element)
        val stored: Collection<ArrayElementBase> = listOf(ArrayElementBase(19))
        val map: Map<String, Collection<ArrayElementBase>> = mapOf("value" to stored)
        assertTrue(selectArrayOrMap(values, map, true) === element)
        assertTrue(selectArrayOrMap(values, map, false) === stored)
        val keyed: Map<Collection<ArrayElementBase>, Int> = mapOf(element to 23)
        assertTrue(importedMapKeys(keyed).first() === element)
        val list = mutableListOf<Collection<ArrayElementBase>>(element)
        assertTrue(importedListReplace(list, stored) === element)
        assertTrue(importedListRemove(list) === stored)
        assertEquals("append", importedCollectionAppend(list, element, "append"))
        assertTrue(list[0] === element)
        val authored = MutableElements<Collection<ArrayElementBase>>(mutableListOf(element))
        assertEquals("authored", importedCollectionAppend(authored, stored, "authored"))
        assertEquals(2, authored.size)
        assertTrue(authored.last() === stored)
        assertTrue(authored.contains(""))
        assertTrue(CollectionStorageApi.ContainsStoredCollection<ArrayElementBase>(authored, stored))
        assertTrue(!CollectionStorageApi.ContainsStoredCollection<ArrayElementBase>(authored, listOf(ArrayElementBase(31))))
        val concreteList = arrayListOf<Collection<ArrayElementBase>>(element)
        assertTrue(importedArrayListReplace(concreteList, stored) === element)
        assertTrue(concreteList[0] === stored)
        val concreteMap = hashMapOf("value" to element)
        assertTrue(importedHashMapReplace(concreteMap, stored) === element)
        assertTrue(concreteMap["value"] === stored)
        val authoredList = MutableListElements<Collection<ArrayElementBase>>(mutableListOf(element, stored))
        assertTrue(authoredList[0] === element)
        assertEquals(1, authoredList.indexOf(stored))
        assertEquals(0, authoredList.lastIndexOf(element))
        assertTrue(authoredList.subList(1, 2)[0] === stored)
        val listIterator = authoredList.listIterator(1)
        assertTrue(listIterator.next() === stored)
        assertTrue(listIterator.previous() === stored)
        val numbers = arrayOf(3, 1, 2)
        assertEquals("sort", importedArraySort(numbers, Comparator<Int> { left, right -> left.compareTo(right) }, "sort"))
        assertEquals(1, numbers[0])
        assertEquals(2, numbers[1])
        assertEquals(3, numbers[2])
    }
}
