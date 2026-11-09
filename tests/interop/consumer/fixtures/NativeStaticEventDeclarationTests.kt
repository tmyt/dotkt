import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import CustomEvents.StaticEventEvidence
import CustomEvents.GenericForeignEvents
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

        var other = 0
        val plainSource = NativeStaticSource.StaticPulse.subscribe { other += it }
        val policySource = NativeStaticSource.StaticPolicyPulse.subscribe { other += it }
        NativeStaticSource.StaticPulse(2)
        NativeStaticSource.StaticPolicyPulse(3)
        plainSource.close()
        policySource.close()
        NativeStaticSource.StaticPulse(100)
        NativeStaticSource.StaticPolicyPulse(100)
        assertEquals(5, other)

        val enumPlain = NativeStaticEnum.EnumPulse.subscribe { other += it }
        val enumPolicy = NativeStaticEnum.EnumPolicyPulse.subscribe { other += it }
        NativeStaticEnum.EnumPulse(7)
        NativeStaticEnum.EnumPolicyPulse(11)
        enumPlain.close()
        enumPolicy.close()
        NativeStaticEnum.EnumPulse(100)
        NativeStaticEnum.EnumPolicyPulse(100)
        assertEquals(23, other)

        val foreign = GenericForeignEvents.ForeignPulse.subscribe { other += it }
        GenericForeignEvents.Fire(13)
        foreign.close()
        GenericForeignEvents.Fire(100)
        assertEquals(36, other)

        for (name in listOf("StaticPulse", "StaticPolicyPulse", "GenericPulse", "GenericPolicyPulse", "EnumPulse", "EnumPolicyPulse"))
            assertTrue(StaticEventEvidence.HasExactStaticShape(owner, name))
    }
}
