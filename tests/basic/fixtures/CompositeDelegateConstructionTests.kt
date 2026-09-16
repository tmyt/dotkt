import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.AreSame as assertSame

class CompositeCtorBox<T>(val value: T)
class CompositeCtorPair<A, B>(val first: A, val second: B)

fun <T> compositeCtorFactory(value: T): () -> CompositeCtorBox<T> = { CompositeCtorBox(value) }
fun <T> compositeCtorArraySize(values: Array<T>): Int = values.size
fun <T> compositeCtorArrayReference(): (Array<T>) -> Int = ::compositeCtorArraySize
fun <T> compositeCtorExternalList(values: Array<T>): Array<T> {
    val list = System.Collections.Generic.List<Array<T>>()
    val append: (Array<T>) -> Unit = list::Add
    append(values)
    return list[0]
}

class CompositeCtorOwner<T>(var value: T) {
    fun box(): CompositeCtorBox<T> = CompositeCtorBox(value)
    fun reference(): () -> CompositeCtorBox<T> = this::box
    fun setter(): (CompositeCtorBox<T>) -> Unit = { value = it.value }
    fun <U> pairWith(other: U): () -> CompositeCtorPair<T, U> = { CompositeCtorPair(value, other) }
    fun nestedArraySize(values: Array<Array<T>>): Int = values.size + values[0].size
    fun nestedArrayReference(): (Array<Array<T>>) -> Int = this::nestedArraySize
}

class CompositeDelegateConstructionTests {
    @TestAttribute
    fun externalGenericOwnerAndBoundClrDelegate() {
        val strings = arrayOf("a", "b")
        val numbers = arrayOf(1, 2, 3)
        assertSame(strings, compositeCtorExternalList(strings))
        assertSame(numbers, compositeCtorExternalList(numbers))
    }

    @TestAttribute
    fun ownerFrameNestedArrayParameter() {
        assertEquals(3, CompositeCtorOwner("owner").nestedArrayReference()(arrayOf(arrayOf("a", "b"))))
        assertEquals(4, CompositeCtorOwner(0).nestedArrayReference()(arrayOf(arrayOf(1, 2, 3))))
    }

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
