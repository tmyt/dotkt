import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.delegateprovider.Cell

var referencedTopLevelDelegate: Int by Cell(7)

class ReferencedDelegateHost {
    var value: String by Cell("seed")
}

class ReferencedDelegateTests {
    @TestAttribute
    fun providerFromReferencedAssembly() {
        assertEquals(7, referencedTopLevelDelegate)
        referencedTopLevelDelegate = 42
        assertEquals(42, referencedTopLevelDelegate)

        val host = ReferencedDelegateHost()
        assertEquals("seed", host.value)
        host.value = "changed"
        assertEquals("changed", host.value)

        var local: Long by Cell(9L)
        assertEquals(9L, local)
        local = 12L
        assertEquals(12L, local)
    }
}
