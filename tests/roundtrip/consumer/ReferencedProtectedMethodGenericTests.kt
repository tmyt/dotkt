import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.AreSame as assertSame
import roundtrip.protectedmethodgeneric.ReferencedProtectedMethodGenericBase

private class ReferencedProtectedMethodGenericText : ReferencedProtectedMethodGenericBase() {
    private fun invoke(block: () -> Unit) = block()

    fun capturedSnapshot(values: Array<String?>): Array<String?> {
        var result: Array<String?>? = null
        invoke { result = snapshot<String>(values) }
        return result!!
    }
}

class ReferencedProtectedMethodGenericTests {
    @TestAttribute
    fun liftedClosureCanCallReferencedProtectedMethodGenericMember() {
        val values = arrayOf("a", null, "c")
        val result = ReferencedProtectedMethodGenericText().capturedSnapshot(values)
        assertSame(values, result)
        assertEquals("a", result[0])
        assertEquals(null, result[1])
        assertEquals("c", result[2])
    }
}
