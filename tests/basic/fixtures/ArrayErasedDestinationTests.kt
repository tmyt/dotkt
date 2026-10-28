import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.IsInstanceOf as assertInstanceOf

private class ArrayErasedDestination<T>(private val values: List<T>) : AbstractCollection<T>() {
    override val size: Int get() = values.size
    override fun iterator(): Iterator<T> = values.iterator()
    fun copy(destination: Array<T>): Array<T> = toArray(destination)
}

@Suppress("UNCHECKED_CAST")
private fun <T> checkErasedArrayDestination(values: List<T>, supplied: Array<Any?>, reuse: Boolean) {
    val view = supplied as Array<T>
    val result = ArrayErasedDestination(values).copy(view)
    check((result === supplied) == reuse && result.size >= values.size)
    assertInstanceOf<Array<Any?>>(result)
    for (index in values.indices) check(result[index] == values[index])
    if (reuse) check(supplied[values.size] == "tail")
}

class ArrayErasedDestinationTests {
    @TestAttribute
    fun genericViewsRetainTheSuppliedRuntimeComponentWhenGrowingAndReusing() {
        checkErasedArrayDestination(listOf("first", "second"), emptyArray<Any?>(), false)
        checkErasedArrayDestination(listOf("first", "second"), arrayOf<Any?>(null, null, "tail"), true)
        checkErasedArrayDestination(listOf(7, 9), emptyArray<Any?>(), false)
        checkErasedArrayDestination(listOf<Int?>(null, 7), emptyArray<Any?>(), false)
    }
}
