import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import kotlin.enums.EnumEntries

private enum class IdentityEntry { FIRST, SECOND }
private var entryTrace = ""
private fun entries(): EnumEntries<IdentityEntry> {
    entryTrace += "f"
    return IdentityEntry.entries
}
private val entries: EnumEntries<IdentityEntry>
    get() {
        entryTrace += "p"
        return IdentityEntry.entries
    }
private fun entries(index: Int): List<IdentityEntry> {
    entryTrace += "o"
    return listOf(IdentityEntry.entries[index])
}
private fun entryArgument(): Int {
    entryTrace += "a"
    return 1
}
private enum class CompanionIdentityEntry(val score: Int) {
    FIRST(2), SECOND(3);
    companion object {
        fun entries(): EnumEntries<CompanionIdentityEntry> {
            entryTrace += "c"
            return CompanionIdentityEntry.entries
        }
    }
}
private class EntryIdentitySource {
    fun entries(): EnumEntries<IdentityEntry> {
        entryTrace += "m"
        return IdentityEntry.entries
    }
}
private fun entrySource(): EntryIdentitySource {
    entryTrace += "r"
    return EntryIdentitySource()
}
private class EntryIdentityExtensionSource
private fun extensionEntrySource(): EntryIdentityExtensionSource {
    entryTrace += "r"
    return EntryIdentityExtensionSource()
}
private fun EntryIdentityExtensionSource.entries(): EnumEntries<IdentityEntry> {
    entryTrace += "x"
    return IdentityEntry.entries
}
private val EntryIdentityExtensionSource.entries: EnumEntries<IdentityEntry>
    get() {
        entryTrace += "y"
        return IdentityEntry.entries
    }

class EnumEntriesIdentityTests {
    @TestAttribute
    fun ordinaryEntriesFunctionRetainsItsCall() {
        entryTrace = ""
        val result = entries()
        assertEquals("f", entryTrace)
        assertTrue(result === IdentityEntry.entries)
        assertEquals(2, result.size)
    }

    @TestAttribute
    fun ordinaryEntriesPropertyRetainsItsGetter() {
        entryTrace = ""
        val result = entries
        assertEquals("p", entryTrace)
        assertTrue(result === IdentityEntry.entries)
    }

    @TestAttribute
    fun entriesOverloadRetainsArgumentsAndItsResult() {
        entryTrace = ""
        val result = entries(entryArgument())
        assertEquals("ao", entryTrace)
        assertEquals(1, result.size)
        assertEquals(IdentityEntry.SECOND, result[0])
    }

    @TestAttribute
    fun companionAndMemberEntriesRetainTheirReceivers() {
        entryTrace = ""
        assertTrue(CompanionIdentityEntry.entries() === CompanionIdentityEntry.entries)
        assertEquals("c", entryTrace)
        assertTrue(entrySource().entries() === IdentityEntry.entries)
        assertEquals("crm", entryTrace)
        assertTrue(extensionEntrySource().entries() === IdentityEntry.entries)
        assertEquals("crmrx", entryTrace)
        assertTrue(extensionEntrySource().entries === IdentityEntry.entries)
        assertEquals("crmrxry", entryTrace)
    }
}
