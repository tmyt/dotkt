import AuthoredMapInterop.NativeMaps
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue

private class NativeReadOnlyMap(private val storage: Map<Int, String>) : Map<Int, String> by storage
private class NativeMutableMap(private val storage: MutableMap<Int, Int>) : MutableMap<Int, Int> by storage

class AuthoredMapInteropTests {
    @TestAttribute
    fun readOnlyImplementationSuppliesNativeDictionaryContracts() {
        assertTrue(NativeMaps.ReadOnly(NativeReadOnlyMap(mapOf(7 to "seven"))))
        assertTrue(NativeMaps.CopyArguments(NativeReadOnlyMap(emptyMap())))
        assertTrue(NativeMaps.CopyArguments(NativeReadOnlyMap(mapOf(7 to "seven"))))
    }

    @TestAttribute
    fun mutableImplementationSuppliesNativeDictionaryContractsWithoutCopying() {
        val source = NativeMutableMap(mutableMapOf(1 to 0))
        assertTrue(NativeMaps.Echo(source) === source)
        assertTrue(NativeMaps.Mutable(source))
        assertTrue(source.isEmpty())
    }
}
