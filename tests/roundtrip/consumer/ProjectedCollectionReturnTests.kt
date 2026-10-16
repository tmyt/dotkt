package roundtriptests.projectedcollectionreturns

import NUnit.Framework.TestAttribute
import roundtrip.projectedcollectionreturns.*

private fun collectionReturnSame(left: Any, right: Any): Boolean = left === right
private class ImportedCollectionReturnSink : CollectionReturnBase<Int>(), CollectionReturnSink<Int>, CollectionReturnOtherSink<Int> {
    override fun pick(items: List<Int>): String = "imported-list"
    override fun pick(value: Any): String = "imported-any"
}
private fun pickBaseList(target: CollectionReturnBase<Int>): String = target.pick(listOf(7))
private fun pickBaseAny(target: CollectionReturnBase<Int>): String = target.pick(7 as Any)
private fun pickSinkList(target: CollectionReturnSink<Int>): String = target.pick(listOf(7))
private fun pickSinkAny(target: CollectionReturnSink<Int>): String = target.pick(7 as Any)
private fun pickOtherList(target: CollectionReturnOtherSink<Int>): String = target.pick(listOf(7))
private fun pickOtherAny(target: CollectionReturnOtherSink<Int>): String = target.pick(7 as Any)
private class ImportedCollectionConstructor(items: List<Int>) : CollectionConstructorReturn<Int>(items)
private class NativeCollectionOverload : GenericValueInterop.CollectionOverloadBase(), GenericValueInterop.ICollectionOverloadSlot {
    override fun Pick(value: Any): String = "native-any"
    fun Pick(items: List<Int>): String = "native-list"
}
private fun pickNativeBase(target: GenericValueInterop.CollectionOverloadBase): String = target.Pick(7)
private fun pickNativeInterface(target: GenericValueInterop.ICollectionOverloadSlot): String = target.Pick(7)

class ProjectedCollectionReturnTests {
    @TestAttribute
    fun importedOpaqueGenericReturnPreservesNestedProjectionAndSourceWidening() {
        check(collectionReturnConsume(collectionReturnIdentity<List<Comparable<*>>>(listOf(7, "x"))) == 2)
        val source = mutableListOf(7, 8) as ClrEnumerable<Int>
        val iterator = CollectionEnumeratorReturn<Int>(source).enumerator()
        check(iterator.MoveNext())
        check(iterator.current() == 7)
        val produced = CollectionReturnSinkImpl()
        check(pickBaseList(produced) == "list")
        check(pickBaseAny(produced) == "any")
        check(pickSinkList(produced) == "list")
        check(pickSinkAny(produced) == "any")
        check(pickOtherList(produced) == "list")
        check(pickOtherAny(produced) == "any")
        val imported = ImportedCollectionReturnSink()
        check(pickBaseList(imported) == "imported-list")
        check(pickBaseAny(imported) == "imported-any")
        check(pickSinkList(imported) == "imported-list")
        check(pickSinkAny(imported) == "imported-any")
        check(pickOtherList(imported) == "imported-list")
        check(pickOtherAny(imported) == "imported-any")
        check(CollectionConstructorReturn<Int>(listOf(7)).result == "list:1")
        check(CollectionConstructorReturn<Int>(7 as Any).result == "any:7")
        check(ImportedCollectionConstructor(listOf(7)).result == "list:1")
        check(CollectionArrayConstructorReturn(arrayOf(7)).result == 1)
        check(CollectionArrayConstructorReturn(intArrayOf(8)).result == 2)
        check(collectionArrayConstructorLocal() == 12)
        val nativeOverload = NativeCollectionOverload()
        check(nativeOverload.Pick(listOf(7)) == "native-list")
        check(pickNativeBase(nativeOverload) == "native-any")
        check(pickNativeInterface(nativeOverload) == "native-any")
    }

    @TestAttribute
    fun importedWideningPreservesValueElementIdentityAndLiveMutations() {
        val integers = mutableListOf(7, 8)
        val widened = collectionReturnWiden(collectionReturnIdentity<List<Comparable<*>>>(integers))
        val stored = collectionReturnFromBox(CollectionReturnBox<List<Comparable<*>>>(integers))
        check(collectionReturnSame(widened, integers))
        check(collectionReturnSame(stored, integers))
        integers[0] = 9
        check(widened[0] == 9)
        check(stored[0] == 9)
        integers.add(10)
        check(widened.size == 3)
        check(stored.size == 3)
        check(widened[2] == 10)
        integers.removeAt(0)
        check(widened.size == 2)
        check(stored.size == 2)
        integers.add(11)
        check(widened.size == 3)
        check(widened[2] == 11)
        integers.clear()
        check(widened.isEmpty())
        check(stored.isEmpty())
    }

    @TestAttribute
    fun importedWideningAndGenericStoragePreserveReferenceElements() {
        val strings = mutableListOf("first", "second")
        val widened = collectionReturnWiden(collectionReturnIdentity<List<Comparable<*>>>(strings))
        val stored = collectionReturnFromBox(CollectionReturnBox<List<Comparable<*>>>(strings))
        check(collectionReturnSame(widened, strings))
        check(collectionReturnSame(stored, strings))
        strings[0] = "changed"
        check(widened[0] == "changed")
        check(stored[0] == "changed")
        strings.add("third")
        check(widened.size == 3)
        check(stored.size == 3)
        strings.removeAt(0)
        check(widened.size == 2)
        check(stored[0] == "second")
        strings.clear()
        check(widened.isEmpty())
        check(stored.isEmpty())

        val native = System.Collections.Generic.List<Int>()
        native.Add(7)
        val nativeView = collectionReturnNative(native)
        check(collectionReturnSame(nativeView, native))
        native.Add(8)
        check(nativeView.Count == 2)
        check(nativeView[1] == 8)

        val nativeOuter = System.Collections.Generic.List<System.Collections.Generic.IReadOnlyCollection<Int>>()
        nativeOuter.Add(native)
        val nestedNative = GenericValueInterop.CollectionStorageApi.NativeNestedIdentity<Int>(nativeOuter)
        check(collectionReturnSame(nestedNative, nativeOuter))
        check(collectionReturnSame(nestedNative[0], native))
        native.Add(9)
        check(nestedNative[0].Count == 3)
    }
}
