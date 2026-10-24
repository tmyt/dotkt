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
private fun checkedProjectedArray(value: Any?): Array<*> = value as Array<*>
private fun checkedProjectedNative(value: Any?): System.Collections.Generic.List<*> = value as System.Collections.Generic.List<*>
private fun checkedNamedParameter(__nn0: Any?): String = __nn0 as String
private fun <T> checkedGenericOperand(value: T): String = value as String
private fun checkedNullableValue(value: Int?): Any = value as Any
private fun checkedUnit(): Any = Unit as Any
private fun nullableString(value: Any?): String? = value as String?
private fun safeString(value: Any?): String? = value as? String
private fun nullableProjectedArray(value: Any?): Array<*>? = value as Array<*>?
private fun safeProjectedArray(value: Any?): Array<*>? = value as? Array<*>
private fun nullableProjectedNative(value: Any?): System.Collections.Generic.List<*>? = value as System.Collections.Generic.List<*>?
private fun safeProjectedNative(value: Any?): System.Collections.Generic.List<*>? = value as? System.Collections.Generic.List<*>
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
        expectNullCast { checkedProjectedArray(null) }
        expectNullCast { checkedProjectedNative(null) }
        expectNullCast { checkedNamedParameter(null) }
        expectNullCast { checkedGenericOperand<String?>(null) }
        expectNullCast { checkedNullableValue(null) }

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
        assertEquals(true, nullableProjectedArray(null) == null)
        assertEquals(true, safeProjectedArray(null) == null)
        assertEquals(true, safeProjectedArray(42) == null)
        assertEquals(true, nullableProjectedNative(null) == null)
        assertEquals(true, safeProjectedNative(null) == null)
        assertEquals(true, safeProjectedNative(42) == null)
        val marker = "retained"
        assertEquals(true, checkedString(marker) === marker)
        assertEquals(true, checkedNamedParameter(marker) === marker)
        assertEquals(true, checkedGenericOperand(marker) === marker)
        assertEquals(42, checkedNullableValue(42))
        assertEquals(true, checkedUnit() === Unit)

        val values: List<Int>? = listOf(1, 2)
        assertEquals("[1, 2]", "${values as Any}")
        assertEquals("[1, 2]", (values as Any).toString())
        val mapping: Map<String, Int>? = mapOf("one" to 1)
        assertEquals("{one=1}", "${mapping as Any}")
        assertEquals("{one=1}", (mapping as Any).toString())

        val deferred: Any? = unchecked<String>(null)
        assertEquals(true, deferred == null)
        val original = IllegalArgumentException("operand")
        var preserved = false
        try { checkedString(failingOperand(original)) }
        catch (error: IllegalArgumentException) { preserved = error === original }
        assertEquals(true, preserved)
    }
}
