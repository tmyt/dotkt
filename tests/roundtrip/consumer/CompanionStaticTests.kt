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
import roundtrip.companionstatics.ConstrainedBox
import roundtrip.companionstatics.FBoundedBox
import roundtrip.companionstatics.FirstConstrainedBoxValue
import roundtrip.companionstatics.GenericOuter
import roundtrip.companionstatics.GenericShape
import roundtrip.companionstatics.GenericTag
import roundtrip.companionstatics.NamedConstants
import roundtrip.companionstatics.MutableTagContext
import roundtrip.companionstatics.OtherTag
import roundtrip.companionstatics.ReadOnlyTagContext
import roundtrip.companionstatics.SecondConstrainedBoxValue
import roundtrip.companionstatics.Shape
import roundtrip.companionstatics.Tag
import roundtrip.companionstatics.TagContext
import roundtrip.companionstatics.of
import roundtrip.companionstatics.keep
import roundtrip.companionstatics.formatTag
import roundtrip.companionstatics.suspended
import roundtrip.companionstatics.blank
import roundtrip.companionstatics.marker
import roundtrip.companionstatics.counter
import roundtrip.companionstatics.later
import roundtrip.companionstatics.withValue
import roundtrip.companionstatics.companionExtensionInlineDefault
import roundtrip.companionstatics.contextLabel
import roundtrip.companionstatics.contextState
import roundtrip.companionstatics.genericValue
import roundtrip.companionstatics.aliasValue
import roundtrip.companionstatics.localGenericCompanionExtensionValue
import roundtrip.companionstatics.constrainedBoxInitializations
import roundtrip.companionstatics.companionExtensionDefaults
import roundtrip.companionstatics.localCompanionSuspendReference
import roundtrip.companionstatics.localGenericCompanionInline
import roundtrip.companionstatics.TOP_TAG

const val importedCompanionTag: String = Counter.TAG
const val importedTopLevelTag: String = TOP_TAG
const val importedCompanionObjectTag: String = Counter.OBJECT_TAG
const val importedNamedObjectTag: String = NamedConstants.NAME
const val importedGenericBoxCode: String = Box.CODE

private fun companionExtensionInlineReturn(): Int {
    Tag.withValue { return 29 }
    return 0
}

@Suppress("DEPRECATION_ERROR")
class CompanionStaticRoundtripTests {
    @TestAttribute
    fun companionBlockMembersRoundTripAsStaticDeclarations() {
        assertEquals(42, Counter.twice(21))
        assertEquals("abab", Counter.twice("ab"))
        assertEquals(0, Counter.origin.n)
        assertEquals("counter", Counter.TAG)
        assertEquals("counter", importedCompanionTag)
        assertEquals("top-level-counter", TOP_TAG)
        assertEquals("top-level-counter", importedTopLevelTag)
        assertEquals("real-companion-const", Counter.OBJECT_TAG)
        assertEquals("real-companion-const", importedCompanionObjectTag)
        assertEquals("named-object-const", NamedConstants.NAME)
        assertEquals("named-object-const", importedNamedObjectTag)
        val companionObjectTag = Counter.Companion::OBJECT_TAG
        val namedObjectTag = NamedConstants::NAME
        assertEquals("real-companion-const", companionObjectTag.get())
        assertEquals("named-object-const", namedObjectTag.get())
        assertEquals(5, Counter(4).bump())

        val later = Counter::later
        var notInitialized = false
        try { later.get() } catch (e: UninitializedPropertyAccessException) { notInitialized = true }
        assertTrue(notInitialized)
        later.set("ready")
        assertEquals("ready", Counter.later)
        assertEquals("ready", later.get())

        assertEquals(0, constrainedBoxInitializations)
        ConstrainedBox(FirstConstrainedBoxValue())
        assertEquals(1, constrainedBoxInitializations)
        ConstrainedBox(SecondConstrainedBoxValue())
        assertEquals(1, constrainedBoxInitializations)
        assertEquals(17, ConstrainedBox.token)
        assertEquals("constrained", ConstrainedBox.label())
        assertEquals(19, FBoundedBox.value())
        Counter.seen = 7
        assertEquals(7, Counter.seen)
        Counter.seen = 1
    }

    @TestAttribute
    fun companionBlockStorageStaysOneLogicalMemberOnAGenericOwner() {
        assertEquals("box", Box.make())
        assertEquals(42, Box.lambdaFactory()())
        assertEquals(42, Box.capturingLambdaFactory(41)())
        assertEquals(7, Box.__lambda0())
        assertEquals(43, Box.localClassValue())
        assertEquals("generic-box", importedGenericBoxCode)
        assertEquals(41, Box.runInline { 40 })
        assertEquals(21, localGenericCompanionInline())
        assertEquals("box-default", Box.withDefault())
        assertEquals("nested-generic", GenericOuter.Nested.label())
        assertEquals(7, Box("x").collidingAccessor)
        assertEquals(9, Box.get_collidingAccessor())
        assertEquals("echo", Box.echoNullable("echo"))
        assertEquals(null, Box.echoNullable<String>(null))
        val echoNullable: (String?) -> String? = Box<String>::echoNullable
        assertEquals("reference", echoNullable("reference"))
        assertEquals(null, echoNullable(null))
        Box.count = 3
        assertEquals(3, Box.count)
        assertEquals("x", Box("x").v)
        assertEquals(11, Box("x").revealPrivateStatics())
        val volatileBox = Box("volatile")
        assertEquals(7, volatileBox.readPrivateVolatile())
        volatileBox.writePrivateVolatile(8)
        assertEquals(8, volatileBox.readPrivateVolatile())
        volatileBox.writePrivateVolatile(7)
        assertEquals(3, Box.count)
        Box.count = 0

        val code = Box<String>::CODE
        assertEquals("generic-box", code.get())
        val nan = Box<String>::NAN
        assertTrue(nan.get().isNaN())
        val big = Box<String>::BIG
        assertEquals(4_000_000_000u, big.get())
        val later = Box<String>::later
        var notInitialized = false
        try { later.get() } catch (e: UninitializedPropertyAccessException) { notInitialized = true }
        assertTrue(notInitialized)
        later.set("ready")
        assertEquals("ready", Box.later)
        assertEquals("ready", later.get())

        assertEquals(10, runCrossModuleSuspend { Box.suspendedMake(7) })
        val suspendedMake: suspend (Int) -> Int = Box<Int>::suspendedMake
        assertEquals(12, runCrossModuleSuspend { suspendedMake(9) })

        Counter.volatileLater = "volatile-ready"
        assertEquals("volatile-ready", Counter.volatileLater)
    }

    @TestAttribute
    fun companionBlockOnAnInterfaceRoundTrips() {
        assertEquals(1, Shape.unitArea())
        assertEquals("shape", Shape.kind)
        assertEquals(2, GenericShape.unitArea())
        assertEquals("generic-shape", GenericShape.kind)
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
        val otherF: (String) -> OtherTag = OtherTag::of
        val otherP = OtherTag::marker
        assertEquals("other-z", otherF("other-z").label)
        assertEquals("other-m", otherP.get())
        val s: suspend (String) -> Tag = Tag::suspended
        assertTrue(s === s)
        assertEquals(2, runCrossModuleSuspend { s("ok").label.length })
        val twice: (Int) -> Int = Counter::twice
        assertEquals(12, twice(6))
        val suspendedTwice: suspend (Int) -> Int = Counter::suspendedTwice
        assertEquals(14, runCrossModuleSuspend { suspendedTwice(7) })
        // The reference itself was created in the producer, so this also witnesses the same-module owner path.
        assertEquals(16, runCrossModuleSuspend { localCompanionSuspendReference()(8) })
        val seen = Counter::seen
        assertEquals(1, seen.get())
    }

    @TestAttribute
    fun companionExtensionsRoundTripWithTheirAssociatedType() {
        assertEquals("hi", Tag.of("hi").label)
        assertEquals("n:8", Tag.of(8).label)
        assertEquals(7, Tag.keep(7))
        assertEquals("tag:v", Tag.formatTag(value = "v"))
        assertEquals("", Tag.blank.label)
        assertEquals("m", Tag.marker)
        Tag.counter = 4
        assertEquals(4, Tag.counter)
        Tag.counter = 0
        Tag.counter += 2
        Tag.counter++
        assertEquals(3, Tag.counter)
        Tag.counter = 0
        val counterRef = Tag::counter
        counterRef.set(5)
        assertEquals(5, counterRef.get())
        counterRef.set(0)
        var directLateinitFailed = false
        try { Tag.later } catch (e: UninitializedPropertyAccessException) {
            directLateinitFailed = e.message == "lateinit property later has not been initialized"
        }
        assertTrue(directLateinitFailed)
        val laterRef = Tag::later
        var referencedLateinitFailed = false
        try { laterRef.get() } catch (e: UninitializedPropertyAccessException) {
            referencedLateinitFailed = e.message == "lateinit property later has not been initialized"
        }
        assertTrue(referencedLateinitFailed)
        laterRef.set("ready")
        assertEquals("ready", Tag.later)
        assertEquals(23, Tag.withValue { 23 })
        assertEquals(29, companionExtensionInlineReturn())
        assertEquals(38, companionExtensionInlineDefault(block = { it + 1 }))
        with(TagContext("ctx")) { assertEquals("ctx", Tag.contextLabel) }
        val mutableContext = MutableTagContext(31)
        with(mutableContext) {
            assertEquals(31, Tag.contextState)
            Tag.contextState = 32
            assertEquals(32, Tag.contextState)
        }
        with(ReadOnlyTagContext(33)) { assertEquals(33, Tag.contextState) }
        assertEquals("generic", GenericTag.genericValue())
        assertEquals("alias", GenericTag.aliasValue())
        assertEquals("generic/alias", localGenericCompanionExtensionValue())
        assertEquals("other-hi", OtherTag.of("other-hi").label)
        assertEquals("kept", OtherTag.keep("kept"))
        assertEquals("other", OtherTag.blank.label)
        assertEquals("other-m", OtherTag.marker)
        OtherTag.counter = 14
        assertEquals(14, OtherTag.counter)
        OtherTag.counter = 10
        assertEquals("m:default", companionExtensionDefaults())
        assertEquals("top:x", of("x"))
        assertEquals("top-m", marker)
    }
}
