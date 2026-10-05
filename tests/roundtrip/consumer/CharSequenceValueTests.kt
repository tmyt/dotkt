import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

class CharSequenceValueTests {
    @TestAttribute
    fun polymorphicValuesCrossStringBackedSignatures() {
        roundtripCheckSnapshotBoundary()
    }

    @TestAttribute
    fun importedMembersAndSupertypesPreserveCharSequence() {
        assertEquals(roundtripOriginalString().length, roundtripStringAsSequence().length)
        assertEquals('c', roundtripCustomSequence()[0])
        assertEquals('u', roundtripCustomSequence().subSequence(1, 4)[0])
        val concrete = RoundtripCharSequence("x")
        val sequence: CharSequence = concrete
        // This consumer uses String-backed CharSequence slots: assignment takes a toString snapshot.
        assertEquals(concrete.toString().length, sequence.length)
        val sink: RoundtripSequenceSink<CharSequence> = RoundtripConcreteSequenceSink()
        assertTrue(sink != null)
        val bound: RoundtripSequenceBound<CharSequence>? = null
        assertTrue(bound == null)
    }

    @TestAttribute
    fun stringRepresentationSurvivesAnAssemblyBoundary() {
        assertTrue((roundtripStringAsSequence() as String) === roundtripOriginalString())
        assertTrue((roundtripNullSequence() as String?) == null)
        var rejected = false
        try { roundtripCustomSequence() as String }
        catch (ex: ClassCastException) { rejected = true }
        assertTrue(rejected)

        var evaluations = 0
        fun source(): CharSequence { evaluations++; return roundtripStringAsSequence() }
        assertTrue((source() as String) === roundtripOriginalString())
        assertEquals(1, evaluations)
    }

    @TestAttribute
    fun stringChecksRecognizeBothRepresentations() {
        assertTrue((roundtripStringAsSequence() as? String) === roundtripOriginalString())
        assertTrue((roundtripCustomSequence() as? String) == null)
        assertTrue((roundtripNullSequence() as? String) == null)
        assertTrue(roundtripStringAsSequence() is String)
        assertTrue(roundtripCustomSequence() !is String)
        assertTrue(roundtripNullSequence() is String?)
        val erased: Any = roundtripStringAsSequence()
        assertTrue(erased is String)
        assertTrue((erased as? String) === roundtripOriginalString())
        assertTrue((erased as String) === roundtripOriginalString())
        val number: Any = 42
        assertTrue(number !is String)
        assertTrue((number as? String) == null)

        var evaluations = 0
        fun source(): Any { evaluations++; return roundtripStringAsSequence() }
        assertTrue((source() as? String) === roundtripOriginalString())
        assertEquals(1, evaluations)
        assertTrue(source() is String)
        assertEquals(2, evaluations)
    }
}
