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
        assertTrue(NativeMaps.ReadOnly(NativeReadOnlyMap(mapOf(7 to "seven"))))
        assertTrue(NativeMaps.CopyArguments(NativeReadOnlyMap(emptyMap())))
        assertTrue(NativeMaps.CopyArguments(NativeReadOnlyMap(mapOf(7 to "seven"))))
        val abstractMap = NativeAbstractMap()
        assertTrue(NativeMaps.AbstractMap(abstractMap))
        assertTrue(mapOf("a" to 1) == abstractMap)
        assertTrue(mapOf("a" to 1).hashCode() == abstractMap.hashCode())
        assertTrue(NativeMaps.AbstractMap(NativeAbstractMutableMap()))
        val raw = NativeMaps.RawDictionary()
        assertTrue(raw is Map<*, *> && (raw as Map<*, *>)["a"] == 1)
    }

    @TestAttribute
    fun mutableImplementationSuppliesNativeDictionaryContractsWithoutCopying() {
        val source = NativeMutableMap(mutableMapOf(1 to 0))
        assertTrue(NativeMaps.Echo(source) === source)
        assertTrue(NativeMaps.Mutable(source))
        assertTrue(source.isEmpty())
    }
}
