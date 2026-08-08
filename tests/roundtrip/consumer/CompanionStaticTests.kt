// Cross-module half of the Kotlin 2.4 static-declaration round trip (#382): DLL -> KLIB -> this second module.
//
// Everything referenced here is resolved from the PRODUCER'S BUILT ASSEMBLY (a ProjectReference re-imported through
// dll2klib), not from its source, so these assertions fail if the emitted metadata loses either shape:
//   - a `companion { }` member must arrive as a standard static class declaration (IS_STATIC_FUNCTION /
//     IS_STATIC_PROPERTY), which is the same shape an ordinary CLR static is projected into;
//   - a `companion fun C.f()` must arrive as a static declaration WITH a receiver type, restored from the trusted
//     [KotlinCompanionExtension] carrier — the association has no physical trace to infer it from.
// A companion extension is an extension, so it is imported by name exactly as a top-level one would be.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse
import roundtrip.companionstatics.Box
import roundtrip.companionstatics.Counter
import roundtrip.companionstatics.Shape
import roundtrip.companionstatics.Tag
import roundtrip.companionstatics.of
import roundtrip.companionstatics.blank
import roundtrip.companionstatics.marker
import roundtrip.companionstatics.counter

class CompanionStaticRoundtripTests {
    @TestAttribute
    fun companionBlockMembersRoundTripAsStaticDeclarations() {
        assertEquals(42, Counter.twice(21))
        assertEquals("abab", Counter.twice("ab"))
        assertEquals(0, Counter.origin.n)
        assertEquals("counter", Counter.TAG)
        assertEquals(5, Counter(4).bump())

        Counter.seen = 7
        assertEquals(7, Counter.seen)
        Counter.seen = 1
    }

    @TestAttribute
    fun companionBlockStorageStaysOneLogicalMemberOnAGenericOwner() {
        assertEquals("box", Box.make())
        Box.count = 3
        assertEquals(3, Box.count)
        assertEquals("x", Box("x").v)
        assertEquals(3, Box.count)
        Box.count = 0
    }

    @TestAttribute
    fun companionBlockOnAnInterfaceRoundTrips() {
        assertEquals(1, Shape.unitArea())
        assertEquals("shape", Shape.kind)
    }

    @TestAttribute
    fun realCompanionObjectStaysDistinctAcrossTheRoundTrip() {
        assertEquals("obj:real-companion", Counter.describe())
        assertEquals("real-companion", Counter.label)
        assertTrue(Counter.Companion === Counter.Companion)
        assertFalse(Counter.label == Counter.TAG)
    }

    @TestAttribute
    fun companionExtensionsAreReferenceableAcrossTheModuleBoundary() {
        // A reference resolves the declaration through metadata, so it exercises the restored shape a second way:
        // the receiver is not a parameter, and the physical host is the PRODUCER's facade, not this module's.
        val p = Tag::marker
        assertEquals("m", p.get())
        val f: (String) -> Tag = Tag::of
        assertEquals("z", f("z").label)
    }

    @TestAttribute
    fun companionExtensionsRoundTripWithTheirAssociatedType() {
        assertEquals("hi", Tag.of("hi").label)
        assertEquals("", Tag.blank.label)
        assertEquals("m", Tag.marker)
        Tag.counter = 4
        assertEquals(4, Tag.counter)
        Tag.counter = 0
    }
}
