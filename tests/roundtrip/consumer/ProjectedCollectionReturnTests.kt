package roundtriptests.projectedcollectionreturns

import NUnit.Framework.TestAttribute
import roundtrip.projectedcollectionreturns.*

class ProjectedCollectionReturnTests {
    @TestAttribute
    fun importedOpaqueGenericReturnPreservesNestedProjectionAndSourceWidening() {
        check(collectionReturnConsume(collectionReturnIdentity<List<Comparable<*>>>(listOf(7, "x"))) == 2)
    }

    @TestAttribute
    fun importedWideningPreservesValueElementIdentityAndLiveMutations() {
        val integers = mutableListOf(7, 8)
        val widened = collectionReturnWiden(collectionReturnIdentity<List<Comparable<*>>>(integers))
        check(widened === integers)
        integers[0] = 9
        check(widened[0] == 9)
        integers.add(10)
        check(widened.size == 3)
        check(widened[2] == 10)
        integers.removeAt(0)
        check(widened.size == 2)
        integers.add(11)
        check(widened.size == 3)
        check(widened[2] == 11)
        integers.clear()
        check(widened.isEmpty())
    }

    @TestAttribute
    fun importedWideningAndGenericStoragePreserveReferenceElements() {
        val strings = mutableListOf("first", "second")
        val widened = collectionReturnWiden(collectionReturnIdentity<List<Comparable<*>>>(strings))
        val stored = collectionReturnFromBox(CollectionReturnBox<List<Comparable<*>>>(strings))
        check(widened === strings)
        check(stored === strings)
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
        check(nativeView === native)
        native.Add(8)
        check(nativeView.Count == 2)
        check(nativeView[1] == 8)

        val nativeOuter = System.Collections.Generic.List<System.Collections.Generic.IReadOnlyCollection<Int>>()
        nativeOuter.Add(native)
        val nestedNative = GenericValueInterop.CollectionStorageApi.NativeNestedIdentity<Int>(nativeOuter)
        check(nestedNative === nativeOuter)
        check(nestedNative[0] === native)
        native.Add(9)
        check(nestedNative[0].Count == 3)
    }
}
