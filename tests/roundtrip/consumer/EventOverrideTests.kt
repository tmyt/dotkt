package roundtrip.eventoverride.consumer

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import roundtrip.eventoverride.EventBase
import roundtrip.eventoverride.EventMiddle
import roundtrip.eventoverride.PolicyEventBase
import roundtrip.eventoverride.RecordingEventPolicy
import roundtrip.eventoverride.localPolicyDispatch
import roundtrip.eventoverride.FunctionEventBase
import kotlin.clr.ClrEvent
import kotlin.clr.clrEvent

open class CrossDllEvent : EventMiddle<String>() {
    final override val Changed: ClrEvent<System.Action1<String>> by clrEvent()
    fun fire(value: String) { Changed(value) }
}

class CrossDllEventLeaf : CrossDllEvent()

private class CrossDllFunctionEvent : FunctionEventBase() {
    override val Changed by clrEvent()
    fun fire(value: Int) { Changed(value) }
}

private class CrossDllWideFunctionEvent : FunctionEventBase() {
    override val Changed: ClrEvent<(Any) -> Unit> by clrEvent()
    fun fire(value: Int) { Changed(value) }
}

class CrossDllPolicyEvent : PolicyEventBase() {
    val policy = RecordingEventPolicy()
    override val Changed: ClrEvent<System.Action1<Int>> by clrEvent(policy)
    fun fire(value: Int) { Changed(value) }
}

class EventOverrideTests {
    @TestAttribute
    fun genericClassEventReusesReferencedSlot() {
        val inferred = CrossDllFunctionEvent()
        var functionSum = 0
        val inferredToken = (inferred as FunctionEventBase).Changed.subscribe { functionSum += it }
        inferred.fireFromBase(10)
        inferred.fire(11)
        inferredToken.close()
        inferred.fireFromBase(100)
        val wide = CrossDllWideFunctionEvent()
        val wideToken = (wide as FunctionEventBase).Changed.subscribe { functionSum += it }
        wide.fireFromBase(20)
        wide.fire(21)
        wideToken.close()
        wide.fireFromBase(100)
        assertEquals(62, functionSum)
        val source = CrossDllEventLeaf()
        var received = ""
        val base: EventBase<Int, String> = source
        val token = base.Changed.subscribe { received += it }
        source.fire("a")
        source.fireFromBase("b")
        token.close()
        source.fire("ignored")
        assertEquals("ab", received)
        val type = (source as System.Object).GetType()
        for (name in arrayOf("add_Changed", "remove_Changed", "raise_Changed")) {
            val method = type.GetMethod(name)!!
            assertTrue(method.IsVirtual && method.IsFinal)
            assertEquals("roundtrip.eventoverride.EventBase`2",
                method.GetBaseDefinition().DeclaringType!!.GetGenericTypeDefinition().FullName)
        }
    }

    @TestAttribute
    fun customPolicyOverridesShareBaseDispatch() {
        assertEquals(42, localPolicyDispatch())
        val source = CrossDllPolicyEvent()
        var received = 0
        val token = (source as PolicyEventBase).Changed.subscribe { received += it }
        source.fire(20)
        source.fireFromBase(22)
        token.close()
        source.fire(100)
        assertEquals(42, received)
        assertEquals(0, source.basePolicy.adds)
        assertEquals(1, source.policy.adds)
        assertEquals(1, source.policy.removes)
    }
}
