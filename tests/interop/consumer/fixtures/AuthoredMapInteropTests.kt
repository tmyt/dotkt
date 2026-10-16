import AuthoredMapInterop.NativeMaps
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue

private class NativeReadOnlyMap(private val storage: Map<Int, String>) : Map<Int, String> by storage
private class NativeMutableMap(private val storage: MutableMap<Int, Int>) : MutableMap<Int, Int> by storage

private interface NativeNamedMap<K, V> : Map<K, V> { val name: String }
private class NativeAbstractMap : AbstractMap<String, Int>(), NativeNamedMap<String, Int> {
    override val name: String = "map"
    override val entries: Set<Map.Entry<String, Int>> = mapOf("a" to 1).entries
}

private class NativeAbstractMutableMap : AbstractMutableMap<String, Int>() {
    private val storage = mutableMapOf("a" to 1)
    override val entries: MutableSet<MutableMap.MutableEntry<String, Int>> get() = storage.entries
    override fun put(key: String, value: Int): Int? = storage.put(key, value)
}

class AuthoredMapInteropTests {
    @TestAttribute
    fun readOnlyImplementationSuppliesNativeDictionaryContracts() {
        assertTrue(NativeMaps.ReadOnly(NativeReadOnlyMap(mapOf(7 to "seven"))), "Native read-only dictionary slots")
        assertTrue(NativeMaps.CopyArguments(NativeReadOnlyMap(emptyMap())), "Empty native CopyTo argument checks")
        assertTrue(NativeMaps.CopyArguments(NativeReadOnlyMap(mapOf(7 to "seven"))), "Nonempty native CopyTo argument checks")
        val abstractMap = NativeAbstractMap()
        assertTrue(NativeMaps.AbstractMap(abstractMap), "AbstractMap native slots")
        assertTrue(mapOf("a" to 1) == abstractMap, "Map structural equality")
        assertTrue(abstractMap.entries.hashCode() == abstractMap.hashCode(), "AbstractMap entry-set hash contract")
        assertTrue(NativeMaps.AbstractMap(NativeAbstractMutableMap()), "AbstractMutableMap native slots")
        val raw = NativeMaps.RawDictionary()
        assertTrue(raw is Map<*, *> && (raw as Map<*, *>)["a"] == 1, "Non-generic dictionary recognition and get")
    }

    @TestAttribute
    fun mutableImplementationSuppliesNativeDictionaryContractsWithoutCopying() {
        val source = NativeMutableMap(mutableMapOf(1 to 0))
        assertTrue(NativeMaps.Echo(source) === source)
        assertTrue(NativeMaps.Mutable(source))
        assertTrue(source.isEmpty())
    }
}
