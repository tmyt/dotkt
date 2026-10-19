package roundtrip.consumer

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.basicenum.BasicEnumHolder
import roundtrip.basicenum.producerBasicOrdinal
import roundtrip.basicenum.producerGenericOrdinal
import roundtrip.entries.BasicEntry

private fun <E : Enum<E>> consumerGenericOrdinal(value: E): Int = value.ordinal
private inline fun <reified E : Enum<E>> consumerReifiedOrdinal(value: E): Int = value.ordinal

class BasicEnumOrdinalRoundtripTests {
    @TestAttribute
    fun importedBasicOrdinalUsesTheProducerDeclarationMap() {
        assertEquals(0, BasicEntry.FIRST.ordinal)
        assertEquals(1, BasicEntry.SECOND.ordinal)
        assertEquals(1, producerBasicOrdinal(BasicEntry.SECOND))
        assertEquals(1, BasicEntry.entries[1].ordinal)
        assertEquals(1, enumValues<BasicEntry>()[1].ordinal)
    }

    @TestAttribute
    fun genericAndReifiedCallersKeepImportedBasicOrdinals() {
        assertEquals(0, consumerGenericOrdinal(BasicEntry.FIRST))
        assertEquals(1, consumerGenericOrdinal(BasicEntry.SECOND))
        assertEquals(1, producerGenericOrdinal(BasicEntry.SECOND))
        assertEquals(1, consumerReifiedOrdinal(BasicEntry.SECOND))
    }

    @TestAttribute
    fun nestedImportedBasicEnumKeepsItsOwnOrdinalMap() {
        assertEquals(0, BasicEnumHolder.Nested.FIRST.ordinal)
        assertEquals(2, BasicEnumHolder.Nested.LAST.ordinal)
        assertEquals(2, consumerGenericOrdinal(BasicEnumHolder.Nested.LAST))
        assertEquals(2, producerGenericOrdinal(BasicEnumHolder.Nested.LAST))
        assertEquals(2, consumerReifiedOrdinal(BasicEnumHolder.Nested.LAST))
    }
}
