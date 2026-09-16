package roundtriptests.companioncovariant

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.covariantreference.*

private class StringFactory : ReferencedCompanionCovariantSlot<String> {
    override fun storage(): Array<String?> = arrayOf(null)
    override fun make(): ReferencedNarrowCovariantValue = ReferencedNarrowCovariantValue(41)
}

private class GenericFactory<A, T>(val seed: Array<T?>) : ReferencedCompanionCovariantSlot<T> {
    override fun storage(): Array<T?> = seed
    override fun make(): ReferencedNarrowCovariantValue = ReferencedNarrowCovariantValue(42)
}

class CompanionCovariantTests {
    @TestAttribute
    fun importedCovariantSlotsCloseTheDeclarationAndCallerFrames() {
        val strings: ReferencedCompanionCovariantSlot<String> = StringFactory()
        assertEquals(41, strings.make().value)
        assertEquals(null, strings.storage()[0])
        val integers: ReferencedCompanionCovariantSlot<Int> =
            GenericFactory<String, Int>(arrayOf<Int?>(7, null))
        assertEquals(42, integers.make().value)
        assertEquals(7, integers.storage()[0])
        assertEquals(null, integers.storage()[1])
    }
}
