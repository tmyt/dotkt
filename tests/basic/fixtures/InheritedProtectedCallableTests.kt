import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

private open class ProtectedNullableCallable<T> {
    protected fun echo(value: T?): T? = value
}

private class ProtectedNullableIntCallable : ProtectedNullableCallable<Int>() {
    fun reference(): (Int?) -> Int? = this::echo
}

private open class PublicNullableCallable<T> {
    fun echoPublic(value: T?): T? = value
}

private class PublicNullableIntCallable : PublicNullableCallable<Int>() {
    fun reference(): (Int?) -> Int? = this::echoPublic
}

private open class ProtectedErasedOverloadCallable {
    protected fun select(value: Map<Int, Int>): Int = value.size + 100

    @kotlin.clr.ClrName("selectMutable")
    protected fun select(value: MutableMap<Int, Int>): Int = value.size + 200
}

private class ProtectedErasedOverloadReferences : ProtectedErasedOverloadCallable() {
    fun readOnly(): (Map<Int, Int>) -> Int = this::select
    fun mutable(): (MutableMap<Int, Int>) -> Int = this::select
}

class InheritedProtectedCallableTests {
    @TestAttribute
    fun boundReferenceUsesTheInheritedDeclarationOwner() {
        val reference = ProtectedNullableIntCallable().reference()
        assertEquals(42, reference(42))
        assertEquals(null, reference(null))
    }

    @TestAttribute
    fun publicBoundReferenceUsesTheInheritedDeclarationAbi() {
        val reference = PublicNullableIntCallable().reference()
        assertEquals(42, reference(42))
        assertEquals(null, reference(null))
    }

    @TestAttribute
    fun boundReferencesUseSelectedDeclarationsAcrossClrErasure() {
        val references = ProtectedErasedOverloadReferences()
        assertEquals(101, references.readOnly()(mapOf(1 to 1)))
        assertEquals(201, references.mutable()(mutableMapOf(1 to 1)))
    }
}
