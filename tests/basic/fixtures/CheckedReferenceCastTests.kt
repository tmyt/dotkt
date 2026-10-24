package checkedreferencecast

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

private class CastTarget
private interface CastFace

private fun checkedString(value: Any?): String = value as String
private fun checkedAny(value: Any?): Any = value as Any
private fun checkedClass(value: Any?): CastTarget = value as CastTarget
private fun checkedInterface(value: Any?): CastFace = value as CastFace
private fun checkedNativeInterface(value: Any?): System.IDisposable = value as System.IDisposable
private fun checkedArray(value: Any?): Array<String> = value as Array<String>
private fun checkedNamedParameter(__nn0: Any?): String = __nn0 as String
private fun nullableString(value: Any?): String? = value as String?
private fun safeString(value: Any?): String? = value as? String
private fun failingOperand(error: IllegalArgumentException): Any? = throw error

@Suppress("UNCHECKED_CAST")
private fun <T> unchecked(value: Any?): T = value as T

private fun expectNullCast(block: () -> Any?) {
    var rejected = false
    try { block() } catch (_: NullPointerException) { rejected = true }
    assertEquals(true, rejected)
}

class CheckedReferenceCastTests {
    @TestAttribute
    fun explicitReferenceCastsRejectNullWithoutChangingOtherConversions() {
        expectNullCast { checkedString(null) }
        expectNullCast { checkedAny(null) }
        expectNullCast { checkedClass(null) }
        expectNullCast { checkedInterface(null) }
        expectNullCast { checkedNativeInterface(null) }
        expectNullCast { checkedArray(null) }
        expectNullCast { checkedNamedParameter(null) }

        var evaluations = 0
        expectNullCast { checkedString(run { evaluations++; null }) }
        assertEquals(1, evaluations)
        expectNullCast { run { evaluations++; null } as String; "unreachable" }
        assertEquals(2, evaluations)

        var mismatch = false
        try { checkedString(run { evaluations++; 42 }) }
        catch (_: ClassCastException) { mismatch = true }
        assertEquals(true, mismatch)
        assertEquals(3, evaluations)

        assertEquals(true, nullableString(null) == null)
        assertEquals(true, safeString(null) == null)
        assertEquals(true, safeString(42) == null)
        val marker = "retained"
        assertEquals(true, checkedString(marker) === marker)
        assertEquals(true, checkedNamedParameter(marker) === marker)

        val deferred: Any? = unchecked<String>(null)
        assertEquals(true, deferred == null)
        val original = IllegalArgumentException("operand")
        var preserved = false
        try { checkedString(failingOperand(original)) }
        catch (error: IllegalArgumentException) { preserved = error === original }
        assertEquals(true, preserved)
    }
}
