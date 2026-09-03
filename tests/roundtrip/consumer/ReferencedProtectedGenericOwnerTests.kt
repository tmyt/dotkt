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

    fun referencedSnapshot(values: Array<String?>): Array<String?> {
        var result: Array<String?>? = null
        invoke {
            val reference: (Array<String?>) -> Array<String?> = this::snapshot
            result = reference(values)
        }
        return result!!
    }

    fun capturedOpenSnapshot(values: Array<String?>): Array<String?> {
        var result: Array<String?>? = null
        invoke { result = openSnapshot(values) }
        return result!!
    }
}

private class ReferencedProtectedGenericOwnerInt : ReferencedProtectedGenericOwnerBase<Int>() {
    private fun invoke(block: () -> Unit) = block()

    fun capturedSnapshot(values: Array<Int?>): Array<Int?> {
        var result: Array<Int?>? = null
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
        assertSame(values, ReferencedProtectedGenericOwnerText().capturedOpenSnapshot(values))
    }

    @TestAttribute
    fun callableReferenceCanAccessReferencedGenericOwner() {
        val values = arrayOf("left", null, "right")
        val result = ReferencedProtectedGenericOwnerText().referencedSnapshot(values)
        assertSame(values, result)
        assertEquals(null, result[1])
    }

    @TestAttribute
    fun liftedClosureCanAccessReferencedValueGenericOwner() {
        val values = arrayOf<Int?>(1, null, 3)
        val result = ReferencedProtectedGenericOwnerInt().capturedSnapshot(values)
        assertSame(values, result)
        assertEquals(null, result[1])
    }
}
