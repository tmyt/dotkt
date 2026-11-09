import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import CustomEvents.StaticEventEvidence
import dotkt.support.events.*

class NativeStaticEventDeclarationTests {
    @TestAttribute
    fun staticDeclarationsSurviveDllProjectionWithOneStoreAndExactTokenIdentity() {
        assertEquals(42, exerciseLocalStaticEvents())
        val owner = GenericNativeStaticSource<Int>()
        GenericNativeStaticSource<String>()
        assertEquals(1, StaticEventInitialization.count)
        var sum = 0
        val plain = GenericNativeStaticSource.GenericPulse.subscribe { sum += it }
        val custom = GenericNativeStaticSource.GenericPolicyPulse.subscribe { sum += it }
        GenericNativeStaticSource.GenericPulse(20)
        GenericNativeStaticSource.GenericPolicyPulse(22)
        plain.close()
        custom.close()
        custom.close()
        GenericNativeStaticSource.GenericPulse(100)
        GenericNativeStaticSource.GenericPolicyPulse(100)
        assertEquals(42, sum)
        assertTrue(GenericNativeStaticSource.policy.added === GenericNativeStaticSource.policy.removed)

        for (name in listOf("StaticPulse", "StaticPolicyPulse", "GenericPulse", "GenericPolicyPulse"))
            assertTrue(StaticEventEvidence.HasExactStaticShape(owner, name))
    }
}
