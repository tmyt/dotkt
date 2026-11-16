package roundtrip.nullablefunctionevent.consumer

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.nullablefunctionevent.NullableFunctionEvent
import roundtrip.nullablefunctionevent.localNullableFunctionEvent
import roundtrip.nullablefunctionevent.ReceiverFunctionEvent
import roundtrip.nullablefunctionevent.localReceiverFunctionEvent
import kotlin.clr.ClrEvent

class NullableFunctionEventTests {
    @TestAttribute
    fun functionHandlersPreserveShapeAndSubscriptionIdentityLocallyAndAcrossDlls() {
        assertEquals(42, localNullableFunctionEvent())
        val source = NullableFunctionEvent<String>()
        val handle: ClrEvent<((String) -> Unit)?> = source.Changed
        val absent: ((String) -> Unit)? = null
        val emptyToken = source.Changed.subscribe(absent)
        source.fire("ignored")
        emptyToken.close()
        var received = ""
        val handler: ((String) -> Unit)? = { received += it }
        val token = handle.subscribe(handler)
        source.fire("a")
        source.fire("b")
        token.close()
        source.fire("ignored")
        assertEquals("ab", received)

        val nullableValues = NullableFunctionEvent<String?>()
        var calls = 0
        val nullableToken = nullableValues.Changed.subscribe { value ->
            assertEquals(null, value)
            calls++
        }
        nullableValues.fire(null)
        nullableToken.close()
        nullableValues.fire(null)
        assertEquals(1, calls)

        assertEquals(42, localReceiverFunctionEvent())
        val receivers = ReceiverFunctionEvent<String>()
        val receiverHandle: ClrEvent<String.(Int) -> Unit> = receivers.Changed
        var combined = ""
        val receiverHandler: String.(Int) -> Unit = { value -> combined += this + value.toString() }
        val first = receiverHandle.subscribe(receiverHandler)
        val second = receiverHandle.subscribe(receiverHandler)
        var inferred = ""
        val inferredToken = receivers.Changed.subscribe { value -> inferred += this + value.toString() }
        receivers.fire("a", 2)
        first.close()
        receivers.fire("b", 3)
        second.close()
        inferredToken.close()
        receivers.fire("ignored", 100)
        assertEquals("a2a2b3", combined)
        assertEquals("a2b3", inferred)
    }
}
