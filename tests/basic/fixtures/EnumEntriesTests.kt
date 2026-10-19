import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import kotlin.enums.EnumEntries
import kotlin.enums.enumEntries

private enum class NoBasicEntries { ; }

private fun <E : Enum<E>> relayEnumEntries(entries: EnumEntries<E>): EnumEntries<E> = entries
private inline fun <reified E : Enum<E>> inlineEnumEntries(): EnumEntries<E> = enumEntries<E>()

class EnumEntriesTests {
    @TestAttribute
    fun basicEntriesAreCachedListValues() {
        val entries: EnumEntries<EnumIntrColor> = relayEnumEntries(EnumIntrColor.entries)
        assertTrue(entries === EnumIntrColor.entries)
        assertTrue(entries === enumEntries<EnumIntrColor>())
        assertTrue(entries === inlineEnumEntries<EnumIntrColor>())
        val list: List<EnumIntrColor> = entries
        assertEquals(3, list.size)
        assertEquals(EnumIntrColor.GREEN, entries[1])
        assertTrue(entries.contains(EnumIntrColor.BLUE))
        assertEquals(1, entries.indexOf(EnumIntrColor.GREEN))
        assertEquals(1, entries.lastIndexOf(EnumIntrColor.GREEN))
        val names = mutableListOf<String>()
        for (entry in entries) names.add(entry.name)
        assertEquals("RED|GREEN|BLUE", names.joinToString("|"))
        assertEquals("GREEN|BLUE", entries.subList(1, 3).joinToString("|") { it.name })
        val outside = try { entries[3]; false } catch (e: IndexOutOfBoundsException) { true }
        assertTrue(outside)
        assertTrue((entries as Any) !is MutableList<*>)
    }

    @TestAttribute
    fun richEntriesUseTheSameListContract() {
        val factory: () -> EnumEntries<EnumPlanet> = ::inlineEnumEntries
        val entries = relayEnumEntries(factory())
        assertTrue(entries === EnumPlanet.entries)
        assertTrue(entries === enumEntries<EnumPlanet>())
        assertTrue(entries === inlineEnumEntries<EnumPlanet>())
        assertEquals(3, entries.size)
        assertEquals(EnumPlanet.MARS, entries[1])
        assertTrue(entries.contains(EnumPlanet.JUPITER))
        assertEquals(2, entries.indexOf(EnumPlanet.JUPITER))
        assertEquals(2, entries.lastIndexOf(EnumPlanet.JUPITER))
        assertEquals("5|1|9", entries.joinToString("|") { it.mass.toString() })
        val bodyEntries = EnumMixedEntryBody.entries
        assertTrue(bodyEntries === enumEntries<EnumMixedEntryBody>())
        assertEquals(EnumMixedEntryBody.values().toList(), bodyEntries)
    }

    @TestAttribute
    fun emptyEnumsStillHaveCachedEntries() {
        val basic = relayEnumEntries(NoBasicEntries.entries)
        val rich = relayEnumEntries(EnumEmptyRich.entries)
        assertTrue(basic === enumEntries<NoBasicEntries>())
        assertTrue(rich === enumEntries<EnumEmptyRich>())
        assertEquals(0, basic.size)
        assertEquals(0, rich.size)
        assertTrue(basic.isEmpty())
        assertTrue(rich.isEmpty())
        assertEquals(0, basic.toList().size)
        val outside = try { rich[-1]; false } catch (e: IndexOutOfBoundsException) { true }
        assertTrue(outside)
    }
}
