import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

private open class ProtectedNullableCallable<T> {
    protected fun echo(value: T?): T? = value
}

private class ProtectedNullableIntCallable : ProtectedNullableCallable<Int>() {
    fun reference(): (Int?) -> Int? = this::echo
}

class InheritedProtectedCallableTests {
    @TestAttribute
    fun boundReferenceUsesTheInheritedDeclarationOwner() {
        val reference = ProtectedNullableIntCallable().reference()
        assertEquals(42, reference(42))
        assertEquals(null, reference(null))
    }
}
