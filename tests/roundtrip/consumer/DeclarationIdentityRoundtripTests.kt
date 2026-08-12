import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.identity.roundtripIdentityFunction
import roundtrip.identity.roundtripIdentityProperty
import roundtrip.identity.roundtripNullableFunction
import roundtrip.identity.roundtripNullableProperty
import roundtrip.identity.roundtripMutableIdentityProperty
import roundtrip.identity.roundtripInlineIdentity
import roundtrip.identity.roundtripDefaultIdentity
import roundtrip.identity.roundtripDefaultNamedProperty
import roundtrip.identity.roundtripCrossFileIdentity
import roundtrip.identity.RoundtripMemberIdentity
import roundtrip.identity.RoundtripStaticMemberIdentity
import roundtrip.identity.RoundtripRegularCompanionIdentity
import roundtrip.identity.RoundtripNullableGenericIdentity
import roundtrip.identity.RoundtripCompanionExtensionIdentity
import roundtrip.identity.erasedCompanionCallable
import roundtrip.identity.erasedSuspendCompanionCallable

class DeclarationIdentityRoundtripTests {
    @TestAttribute
    fun dllToKlibCallsKeepProducerDeclarationIdentity() {
        val readOnly: Map<Int, Int> = mapOf(1 to 1)
        val mutable: MutableMap<Int, Int> = mutableMapOf(1 to 1)
        val present: String = "x"
        val optional: String? = null
        assertEquals(301, readOnly.roundtripIdentityProperty)
        assertEquals(302, mutable.roundtripIdentityProperty)
        assertEquals(303, present.roundtripNullableProperty)
        assertEquals(304, optional.roundtripNullableProperty)
        readOnly.roundtripMutableIdentityProperty = 13
        mutable.roundtripMutableIdentityProperty = 14
        assertEquals(613, readOnly.roundtripMutableIdentityProperty)
        assertEquals(714, mutable.roundtripMutableIdentityProperty)
        assertEquals(305, readOnly.roundtripIdentityFunction())
        assertEquals(306, mutable.roundtripIdentityFunction())
        assertEquals(307, present.roundtripNullableFunction())
        assertEquals(308, optional.roundtripNullableFunction())
        assertEquals(309, readOnly.roundtripInlineIdentity())
        assertEquals(310, mutable.roundtripInlineIdentity())
        assertEquals(311, readOnly.roundtripDefaultIdentity())
        assertEquals(312, mutable.roundtripDefaultIdentity())
        assertEquals(333, roundtripDefaultNamedProperty)
        assertEquals(325, readOnly.roundtripCrossFileIdentity())
        assertEquals(326, mutable.roundtripCrossFileIdentity())
        val crossFileReadRef: (Map<Int, Int>) -> Int = Map<Int, Int>::roundtripCrossFileIdentity
        val crossFileMutableRef: (MutableMap<Int, Int>) -> Int =
            MutableMap<Int, Int>::roundtripCrossFileIdentity
        assertEquals(325, crossFileReadRef(readOnly))
        assertEquals(326, crossFileMutableRef(mutable))
        val memberOwner = RoundtripMemberIdentity()
        assertEquals(313, memberOwner.read(readOnly))
        assertEquals(314, memberOwner.readMutable(mutable))
        with(memberOwner) {
            assertEquals(313, readOnly.erasedMember())
            assertEquals(314, mutable.erasedMember())
        }
        assertEquals(316, memberOwner.erasedCallable(readOnly))
        assertEquals(317, memberOwner.erasedCallable(mutable))
        val memberReadRef: (Map<Int, Int>) -> Int = memberOwner::erasedCallable
        val memberMutableRef: (MutableMap<Int, Int>) -> Int = memberOwner::erasedCallable
        assertEquals(316, memberReadRef(readOnly))
        assertEquals(317, memberMutableRef(mutable))
        assertEquals(318, RoundtripStaticMemberIdentity.erasedCallable(readOnly))
        assertEquals(319, RoundtripStaticMemberIdentity.erasedCallable(mutable))
        val staticReadRef: (Map<Int, Int>) -> Int = RoundtripStaticMemberIdentity::erasedCallable
        val staticMutableRef: (MutableMap<Int, Int>) -> Int = RoundtripStaticMemberIdentity::erasedCallable
        assertEquals(318, staticReadRef(readOnly))
        assertEquals(319, staticMutableRef(mutable))
        val staticReadSuspendRef: suspend (Map<Int, Int>) -> Int =
            RoundtripStaticMemberIdentity::erasedSuspendCallable
        val staticMutableSuspendRef: suspend (MutableMap<Int, Int>) -> Int =
            RoundtripStaticMemberIdentity::erasedSuspendCallable
        assertEquals(320, runCrossModuleSuspend { staticReadSuspendRef(readOnly) })
        assertEquals(321, runCrossModuleSuspend { staticMutableSuspendRef(mutable) })
        assertEquals(327, RoundtripRegularCompanionIdentity.erasedCallable(readOnly))
        assertEquals(328, RoundtripRegularCompanionIdentity.erasedCallable(mutable))
        val companionReadRef: (Map<Int, Int>) -> Int =
            RoundtripRegularCompanionIdentity::erasedCallable
        val companionMutableRef: (MutableMap<Int, Int>) -> Int =
            RoundtripRegularCompanionIdentity::erasedCallable
        assertEquals(327, companionReadRef(readOnly))
        assertEquals(328, companionMutableRef(mutable))
        val nullableGeneric = RoundtripNullableGenericIdentity<Int>()
        assertEquals(329, nullableGeneric.selected(1))
        assertEquals(330, nullableGeneric.selected("s"))
        val nullableGenericStar: RoundtripNullableGenericIdentity<*> = nullableGeneric
        assertEquals(null, nullableGenericStar.selectedProperty)
        val selectedPropertyRef = nullableGenericStar::selectedProperty
        assertEquals(null, selectedPropertyRef.get())
        assertEquals(330, nullableGenericStar.selected("s"))
        assertEquals(331, nullableGeneric.selectedMap(readOnly))
        assertEquals(332, nullableGeneric.selectedMap(mutable))
        assertEquals(331, nullableGenericStar.selectedMap(readOnly))
        assertEquals(332, nullableGenericStar.selectedMap(mutable))
        val starReadRef: (Map<Int, Int>) -> Int = nullableGenericStar::selectedMap
        val starMutableRef: (MutableMap<Int, Int>) -> Int = nullableGenericStar::selectedMap
        assertEquals(331, starReadRef(readOnly))
        assertEquals(332, starMutableRef(mutable))
    }

    @TestAttribute
    fun dllToKlibCallableReferencesKeepProducerDeclarationIdentity() {
        val readOnly: Map<Int, Int> = mapOf(1 to 1)
        val mutable: MutableMap<Int, Int> = mutableMapOf(1 to 1)
        val readFunction: (Map<Int, Int>) -> Int = Map<Int, Int>::roundtripIdentityFunction
        val mutableFunction: (MutableMap<Int, Int>) -> Int = MutableMap<Int, Int>::roundtripIdentityFunction
        val readProperty = Map<Int, Int>::roundtripIdentityProperty
        val mutableProperty = MutableMap<Int, Int>::roundtripIdentityProperty
        val readMutableProperty = Map<Int, Int>::roundtripMutableIdentityProperty
        val mutableMutableProperty = MutableMap<Int, Int>::roundtripMutableIdentityProperty
        val defaultNamedProperty = ::roundtripDefaultNamedProperty
        assertEquals(305, readFunction(readOnly))
        assertEquals(306, mutableFunction(mutable))
        assertEquals(301, readProperty.get(readOnly))
        assertEquals(302, mutableProperty.get(mutable))
        assertEquals(333, defaultNamedProperty.get())
        readMutableProperty.set(readOnly, 15)
        mutableMutableProperty.set(mutable, 16)
        assertEquals(615, readMutableProperty.get(readOnly))
        assertEquals(716, mutableMutableProperty.get(mutable))
    }

    @TestAttribute
    fun dllToKlibCompanionExtensionReferencesKeepProducerDeclarationIdentity() {
        val readOnly: Map<Int, Int> = mapOf(1 to 1)
        val mutable: MutableMap<Int, Int> = mutableMapOf(1 to 1)
        assertEquals(322, RoundtripCompanionExtensionIdentity.erasedCompanionCallable(readOnly))
        assertEquals(323, RoundtripCompanionExtensionIdentity.erasedCompanionCallable(mutable))
        val readRef: (Map<Int, Int>) -> Int =
            RoundtripCompanionExtensionIdentity::erasedCompanionCallable
        val mutableRef: (MutableMap<Int, Int>) -> Int =
            RoundtripCompanionExtensionIdentity::erasedCompanionCallable
        assertEquals(322, readRef(readOnly))
        assertEquals(323, mutableRef(mutable))
        val readSuspendRef: suspend (Map<Int, Int>) -> Int =
            RoundtripCompanionExtensionIdentity::erasedSuspendCompanionCallable
        val mutableSuspendRef: suspend (MutableMap<Int, Int>) -> Int =
            RoundtripCompanionExtensionIdentity::erasedSuspendCompanionCallable
        assertEquals(324, runCrossModuleSuspend { readSuspendRef(readOnly) })
        assertEquals(325, runCrossModuleSuspend { mutableSuspendRef(mutable) })
    }
}
