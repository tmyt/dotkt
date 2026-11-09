package roundtrip.nullablefunctionevent.consumer

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.nullablefunctionevent.NullableFunctionEvent
import roundtrip.nullablefunctionevent.localNullableFunctionEvent
import kotlin.clr.ClrEvent

class NullableFunctionEventTests {
    @TestAttribute
    fun nullableFunctionHandlersPreserveSubscriptionIdentityLocallyAndAcrossDlls() {
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
    }
}
