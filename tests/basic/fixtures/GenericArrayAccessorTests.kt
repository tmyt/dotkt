import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

private open class AccessorArrayOwner<T>(protected val slots: Array<T>) {
    protected fun touch() { check(slots.size > 0) }
}

private class AccessorStringOwner : AccessorArrayOwner<String>(arrayOf("first")) {
    fun read(): String {
        val touchOwner = { touch() }
        touchOwner()
        var observed: Array<String>? = null
        val capture = { observed = slots }
        capture()
        return observed!![0]
    }
}

class GenericArrayAccessorTests {
    @TestAttribute
    fun inheritedGenericArrayIsProjectedIntoCapturedStorage() {
        assertEquals("first", AccessorStringOwner().read())
    }
}
