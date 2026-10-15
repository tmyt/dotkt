package roundtrip.uncheckedtop.consumer

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.uncheckedtop.read as referencedRead

@Suppress("UNCHECKED_CAST")
private fun <T> read(seed: T, raw: Any?): T = raw as T

private fun <T> local(seed: T, raw: Any?): Int {
    val value = read(seed, raw)
    return value.hashCode()
}

private fun <T> referenced(seed: T, raw: Any?): Int {
    val value = referencedRead(seed, raw)
    return value.hashCode()
}

class UncheckedTopLevelIdentityTests {
    @TestAttribute
    fun referencedDeclarationIsNotReboundToTheSameNamedLocalProducer() {
        assertEquals(0, local(42, null))
        assertEquals("wrong".hashCode(), local(42, "wrong"))
        assertEquals(42, referenced(42, null))
        assertEquals(42, referenced(42, "wrong"))
    }
}
