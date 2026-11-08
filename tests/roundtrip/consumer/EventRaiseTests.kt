package roundtriptests.eventraise

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.eventraise.Publisher
import roundtrip.eventraise.PublisherOuter
import roundtrip.eventraise.PolicyPublisher
import roundtrip.eventraise.localInheritedRaises

private class ImportedChild : Publisher<String, Int>()
private class ImportedPolicyChild : PolicyPublisher()

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
    }
}
