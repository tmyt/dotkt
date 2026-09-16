import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

class CompositeCtorBox<T>(val value: T)
class CompositeCtorPair<A, B>(val first: A, val second: B)

fun <T> compositeCtorFactory(value: T): () -> CompositeCtorBox<T> = { CompositeCtorBox(value) }
fun <T> compositeCtorArraySize(values: Array<T>): Int = values.size
fun <T> compositeCtorArrayReference(): (Array<T>) -> Int = ::compositeCtorArraySize

class CompositeCtorOwner<T>(var value: T) {
    fun box(): CompositeCtorBox<T> = CompositeCtorBox(value)
    fun reference(): () -> CompositeCtorBox<T> = this::box
    fun setter(): (CompositeCtorBox<T>) -> Unit = { value = it.value }
    fun <U> pairWith(other: U): () -> CompositeCtorPair<T, U> = { CompositeCtorPair(value, other) }
}

class CompositeDelegateConstructionTests {
    @TestAttribute
    fun methodFrameCompositeResult() {
        assertEquals("text", compositeCtorFactory("text")().value)
        assertEquals(42, compositeCtorFactory(42)().value)
    }

    @TestAttribute
    fun boundReferenceCompositeResult() {
        val text = CompositeCtorOwner("before")
        val read = text.reference()
        text.value = "after"
        assertEquals("after", read().value)
        assertEquals(7, CompositeCtorOwner(7).reference()().value)
    }

    @TestAttribute
    fun staticReferenceArrayParameter() {
        assertEquals(2, compositeCtorArrayReference<String>()(arrayOf("a", "b")))
        assertEquals(3, compositeCtorArrayReference<Int>()(arrayOf(1, 2, 3)))
    }

    @TestAttribute
    fun actionCompositeParameter() {
        val text = CompositeCtorOwner("before")
        text.setter()(CompositeCtorBox("after"))
        assertEquals("after", text.value)
        val number = CompositeCtorOwner(1)
        number.setter()(CompositeCtorBox(9))
        assertEquals(9, number.value)
    }

    @TestAttribute
    fun ownerAndMethodFramesInOneDelegateResult() {
        val pair = CompositeCtorOwner("owner").pairWith(11)()
        assertEquals("owner", pair.first)
        assertEquals(11, pair.second)
        val reversed = CompositeCtorOwner(12).pairWith("method")()
        assertEquals(12, reversed.first)
        assertEquals("method", reversed.second)
    }
}
