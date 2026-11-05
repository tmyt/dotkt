package roundtrip.consumer

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import kotlin.enums.EnumEntries
import kotlin.enums.enumEntries
import roundtrip.entries.BasicEntry
import roundtrip.entries.RichEntry
import roundtrip.entries.SuperRenderedEntry
import roundtrip.entries.basicEntries
import roundtrip.entries.richEntries
import roundtrip.entries.richName
import roundtrip.entries.richOrdinal
import roundtrip.entries.richCompare
import roundtrip.entries.sharedEntries
import roundtrip.entries.relayEntries

private fun <E : Enum<E>> importedEntryOrdinal(value: E): Int = value.ordinal

class EnumEntriesRoundtripTests {
    @TestAttribute
    fun importedBasicEntriesAreValuesAcrossDllBoundaries() {
        val entries: EnumEntries<BasicEntry> = relayEntries(basicEntries())
        assertTrue(entries === BasicEntry.entries)
        assertTrue(entries === enumEntries<BasicEntry>())
        assertTrue(entries === sharedEntries<BasicEntry>())
        assertEquals(2, entries.size)
        assertEquals(BasicEntry.SECOND, entries[1])
        assertEquals(1, entries.indexOf(BasicEntry.SECOND))
        assertEquals("FIRST|SECOND", entries.joinToString("|") { it.name })
        var count = 0
        for (entry in entries) { assertTrue(entries.contains(entry)); count++ }
        assertEquals(2, count)
    }

    @TestAttribute
    fun importedRichEntriesShareTheProducerCache() {
        val entries: EnumEntries<RichEntry> = relayEntries(sharedEntries<RichEntry>())
        assertTrue(entries === richEntries())
        assertTrue(entries === RichEntry.entries)
        assertTrue(entries === enumEntries<RichEntry>())
        assertTrue(entries === sharedEntries<RichEntry>())
        assertEquals(2, entries.size)
        assertEquals(RichEntry.SECOND, entries[1])
        assertEquals(1, entries.lastIndexOf(RichEntry.SECOND))
        assertEquals("7|9", entries.joinToString("|") { it.score.toString() })
        assertEquals("SECOND", richName(RichEntry.SECOND))
        assertEquals(1, richOrdinal(RichEntry.SECOND))
        assertEquals(-1, richCompare(RichEntry.FIRST, RichEntry.SECOND))
        assertEquals("SECOND", RichEntry.SECOND.name)
        assertEquals(1, RichEntry.SECOND.ordinal)
        assertEquals(-1, RichEntry.FIRST.compareTo(RichEntry.SECOND))
        val name: (RichEntry) -> String = RichEntry::name
        val ordinal: (RichEntry) -> Int = RichEntry::ordinal
        val compare: (RichEntry, RichEntry) -> Int = RichEntry::compareTo
        assertEquals("SECOND", name(RichEntry.SECOND))
        assertEquals(1, ordinal(RichEntry.SECOND))
        assertEquals(-1, compare(RichEntry.FIRST, RichEntry.SECOND))
        assertEquals("first:FIRST", SuperRenderedEntry.FIRST.toString())
        val render: (SuperRenderedEntry) -> String = SuperRenderedEntry::toString
        assertEquals("entry:second:SECOND", render(SuperRenderedEntry.SECOND))
        assertEquals(1, importedEntryOrdinal(SuperRenderedEntry.SECOND))
    }
}
