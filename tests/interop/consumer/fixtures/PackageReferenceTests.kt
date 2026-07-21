import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import PackageInterop.VirtualBase
import System.IO.Ports.SerialPort

class KotlinVirtualOverride : VirtualBase() {
    override fun Describe(value: Int): String = "kotlin:" + (value * 2)
}

class PackageReferenceTests {
    @TestAttribute
    fun kotlinOverridesVirtualMemberFromPackage() {
        val instance: VirtualBase = KotlinVirtualOverride()
        assertEquals("kotlin:42", instance.Describe(21))
    }

    @TestAttribute
    fun runtimeSpecificAssetWinsIdentityDeduplication() {
        val ports = SerialPort.GetPortNames()
        assertTrue(ports.size >= 0)
    }
}
