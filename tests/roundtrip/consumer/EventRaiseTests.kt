package roundtriptests.eventraise

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.eventraise.Publisher
import roundtrip.eventraise.PublisherOuter
import roundtrip.eventraise.PolicyPublisher
import roundtrip.eventraise.localInheritedRaises
import roundtrip.eventraise.SuperPublisher
import roundtrip.eventraise.RaiseCountingPolicy
import roundtrip.eventvisibility.EventVisibilitySource
import kotlin.clr.ClrEvent
import kotlin.clr.clrEvent

private class ImportedChild : Publisher<String, Int>()
private class ImportedPolicyChild : PolicyPublisher()

private class ImportedProtectedChild : EventVisibilitySource<Int>() {
    fun handle() = Family
    fun fireFamily(value: Int) { Family(value) }
    fun later(value: Int): () -> Unit = { Family(value) }
}

private class ImportedSuperChild : SuperPublisher() {
    val policy = RaiseCountingPolicy()
    override val Changed: ClrEvent<System.Action1<Int>> by clrEvent(policy)
    fun fireBase(value: Int) { super.Changed(value) }
    fun later(value: Int): () -> Unit = { super.Changed(value) }
}

private fun <A, B> raiseGeneric(source: Publisher<A, B>, value: B) { source.Changed(value) }

class EventRaiseTests {
    @TestAttribute
    fun inheritedLocalRaisesSelectTheDeclaringEvent() {
        assertEquals(42, localInheritedRaises())
    }

    @TestAttribute
    fun importedRaisesPreserveFramesAndEvaluateOperandsOnce() {
        val source = ImportedChild()
        var receiverCount = 0
        var argumentCount = 0
        var result = 0
        fun receiver(): ImportedChild { receiverCount++; return source }
        fun argument(): Int { argumentCount++; return 7 }
        val token = source.Changed.subscribe { result += it }
        receiver().Changed(argument())
        raiseGeneric(source, 13)
        token.close()
        receiver().Changed(argument())
        assertEquals(20, result)
        assertEquals(2, receiverCount)
        assertEquals(2, argumentCount)

        val nested = PublisherOuter<String>().NestedPublisher<Int>()
        var pair = ""
        val nestedToken = nested.Changed.subscribe { first, second -> pair = "$first:$second" }
        nested.Changed("payload", 42)
        nestedToken.close()
        nested.Changed("ignored", 100)
        assertEquals("payload:42", pair)

        val custom = ImportedPolicyChild()
        val customToken = custom.Changed.subscribe { result += it }
        custom.Changed(22)
        customToken.close()
        custom.Changed(100)
        assertEquals(42, result)
        assertEquals(1, custom.policy.adds)
        assertEquals(1, custom.policy.removes)

        val family = ImportedProtectedChild()
        var familyResult = 0
        val familyToken = family.handle().subscribe { familyResult += it }
        family.fireFamily(20)
        family.later(22)()
        familyToken.close()
        family.fireFamily(100)
        assertEquals(42, familyResult)

        val base = ImportedSuperChild()
        base.fireBase(20)
        base.later(22)()
        assertEquals(42, base.basePolicy.total)
        assertEquals(0, base.policy.total)
    }
}
