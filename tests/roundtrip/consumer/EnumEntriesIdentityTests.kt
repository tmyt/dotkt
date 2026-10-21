package roundtrip.consumer

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import roundtrip.entriesidentity.CompanionIdentityEntry
import roundtrip.entriesidentity.IdentityEntry
import roundtrip.entriesidentity.entries
import roundtrip.entriesidentity.entryArgument
import roundtrip.entriesidentity.entrySource
import roundtrip.entriesidentity.readEntryTrace
import roundtrip.entriesidentity.resetEntryTrace

class EnumEntriesIdentityRoundtripTests {
    @TestAttribute
    fun importedEntriesDeclarationsRetainEvaluation() {
        resetEntryTrace()
        val functionResult = entries()
        assertEquals("f", readEntryTrace())
        val propertyResult = entries
        assertEquals("fp", readEntryTrace())
        assertTrue(functionResult === IdentityEntry.entries)
        assertTrue(propertyResult === functionResult)
        val overloadResult = entries(entryArgument())
        assertEquals("fpao", readEntryTrace())
        assertEquals(1, overloadResult.size)
        assertEquals(IdentityEntry.SECOND, overloadResult[0])
    }

    @TestAttribute
    fun importedCompanionAndMemberEntriesRetainReceivers() {
        resetEntryTrace()
        assertTrue(CompanionIdentityEntry.entries() === CompanionIdentityEntry.entries)
        assertEquals("c", readEntryTrace())
        assertTrue(entrySource().entries() === IdentityEntry.entries)
        assertEquals("crm", readEntryTrace())
    }
}
