import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.AreSame as assertSame
import roundtrip.protectedgenericowner.ReferencedProtectedGenericOwnerBase

private class ReferencedProtectedGenericOwnerText : ReferencedProtectedGenericOwnerBase<String>() {
    private fun invoke(block: () -> Unit) = block()

    fun capturedSnapshot(values: Array<String?>): Array<String?> {
        var result: Array<String?>? = null
        invoke { result = snapshot(values) }
        return result!!
    }
}

class ReferencedProtectedGenericOwnerTests {
    @TestAttribute
    fun liftedClosureCanAccessReferencedGenericOwner() {
        val values = arrayOf("a", null, "c")
        val result = ReferencedProtectedGenericOwnerText().capturedSnapshot(values)
        assertSame(values, result)
        assertEquals("a", result[0])
        assertEquals(null, result[1])
        assertEquals("c", result[2])
    }
}
