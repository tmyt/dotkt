package roundtriptests.authoredmap

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import roundtrip.authoredmap.*

private class LocalDerivedMap : ImportedReadOnlyMap<Int, String>(mutableMapOf(7 to "seven")) {
    override fun get(key: Int): String? = "local"
    fun get(key: String): String = "unrelated overload"
}

class AuthoredMapTests {
    @TestAttribute
    fun importedImplementationRetainsConstructedGenericFrame() {
        val map = importedMap()
        assertTrue(map[7] == "seven" && readImportedMap(map) == "seven")
        assertTrue(map.keys.contains(7) && map.values.contains("seven"))
        val erased: Any = map
        assertTrue(erased is Map<*, *> && (erased as Map<*, *>)[7] == "seven")
    }

    @TestAttribute
    fun externalBaseOverrideKeepsSemanticSlotSelection() {
        val source = LocalDerivedMap()
        val map: Map<Int, String> = source
        assertTrue(map[7] == "local" && readImportedMap(map) == "local")
        assertTrue(source.get("seven") == "unrelated overload")
        assertTrue(map.size == 1 && map.containsKey(7))
    }
}
