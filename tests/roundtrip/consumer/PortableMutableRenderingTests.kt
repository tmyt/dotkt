import NUnit.Framework.TestAttribute
import roundtrip.mutablepolicy.PortableMutableList
import roundtrip.mutablepolicy.PortableMutableSet
import roundtrip.mutablepolicy.PortableMutableCollection
import roundtrip.mutablepolicy.PortableRenderingElement
import roundtrip.mutablepolicy.PortableAuthoredRenderList
import roundtrip.mutablepolicy.renderMutableThroughAny
import roundtrip.mutablepolicy.renderMutableGeneric

private fun checkPortableMutableRendering(value: Any, expected: String) {
    val actual = value.toString()
    check(actual == expected) { "Expected $expected; actual $actual" }
}

class CrossModulePortableMutableRenderingTests {
    @TestAttribute fun opaqueDefaultsRenderEmptyNullableAndValueElements() {
        checkPortableMutableRendering(PortableMutableCollection(mutableListOf<Int>()), "[]")
        checkPortableMutableRendering(PortableMutableList(mutableListOf<Int>()), "[]")
        checkPortableMutableRendering(PortableMutableSet(mutableListOf<Int>()), "[]")
        checkPortableMutableRendering(PortableMutableCollection(mutableListOf<Int?>(1, null, 2)), "[1, null, 2]")
        checkPortableMutableRendering(PortableMutableList(mutableListOf<Int?>(1, null, 2)), "[1, null, 2]")
        checkPortableMutableRendering(PortableMutableSet(mutableListOf<Int?>(1, null, 2)), "[1, null, 2]")
        checkPortableMutableRendering(PortableMutableList(mutableListOf("first", "last")), "[first, last]")
    }

    @TestAttribute fun authoredElementAndCollectionOverridesRemainVirtual() {
        val element = PortableRenderingElement(7)
        checkPortableMutableRendering(PortableMutableList(mutableListOf(element)), "[value:7]")
        check(element.calls == 1)
        checkPortableMutableRendering(PortableMutableCollection(mutableListOf(element)), "[value:7]")
        check(element.calls == 2)
        checkPortableMutableRendering(PortableMutableSet(mutableListOf(element)), "[value:7]")
        check(element.calls == 3)
        val authored = PortableAuthoredRenderList(mutableListOf(1, 2))
        checkPortableMutableRendering(authored, "authored")
        check(renderMutableThroughAny(authored) == "authored")
        check(renderMutableGeneric(authored) == "authored")
    }

    @TestAttribute fun directSelfReferencesUseIdentityWithoutRecursion() {
        val listValues = mutableListOf<Any?>()
        val list = PortableMutableList(listValues)
        listValues.add(list)
        checkPortableMutableRendering(list, "[(this Collection)]")
        val collectionValues = mutableListOf<Any?>()
        val collection = PortableMutableCollection(collectionValues)
        collectionValues.add(collection)
        checkPortableMutableRendering(collection, "[(this Collection)]")
        val setValues = mutableListOf<Any?>()
        val set = PortableMutableSet(setValues)
        setValues.add(set)
        checkPortableMutableRendering(set, "[(this Collection)]")
    }

    @TestAttribute fun authoredIterationAndItsFailureArePreserved() {
        val collection = PortableMutableCollection(mutableListOf(1, 2))
        check(collection.iterations == 0)
        check(renderMutableThroughAny(collection) == "[1, 2]")
        check(collection.iterations == 1)
        collection.failIteration = true
        try {
            renderMutableThroughAny(collection)
            error("must throw")
        } catch (failure: IllegalStateException) {
            check(failure.message == "iteration")
        }
        check(collection.iterations == 2)
    }

    @TestAttribute fun staticNativeAndNestedViewsDoNotRegress() {
        val native = System.Collections.Generic.List<Int>()
        native.Add(1); native.Add(2)
        val nativeView = native as List<Int>
        check(nativeView.toString() == "[1, 2]") { nativeView.toString() }
        val values = mutableListOf<Any?>(native)
        val list: List<*> = PortableMutableList(values)
        check(list.toString() == "[[1, 2]]") { list.toString() }
        val collection: Collection<*> = PortableMutableCollection(values)
        check(collection.toString() == "[[1, 2]]") { collection.toString() }
        val set: Set<*> = PortableMutableSet(values)
        check(set.toString() == "[[1, 2]]") { set.toString() }
    }

    @TestAttribute fun callableAndProducerGenericCallsUseDefaultPolicy() {
        val value: Any = PortableMutableList(mutableListOf<Int?>(1, null, 2))
        val render: () -> String = value::toString
        check(render() == "[1, null, 2]")
        check(renderMutableThroughAny(value) == "[1, null, 2]")
        check(renderMutableGeneric(value) == "[1, null, 2]")
        val collection: Any = PortableMutableCollection(mutableListOf(3, 4))
        val set: Any = PortableMutableSet(mutableListOf(5, 6))
        check(renderMutableThroughAny(collection) == "[3, 4]")
        check(renderMutableGeneric(set) == "[5, 6]")
    }
}
