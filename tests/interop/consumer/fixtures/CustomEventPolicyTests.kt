import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import System.ComponentModel.INotifyPropertyChanged
import System.ComponentModel.PropertyChangedEventArgs
import CustomEvents.PolicyEvidence
import dotkt.support.events.CustomNotifyingSource
import dotkt.support.events.CustomGenericSource
import dotkt.support.events.CustomResultSource
import dotkt.support.events.CustomCompanionSource
import dotkt.support.events.ConcreteFunctionSource
import dotkt.support.events.NullablePayloadSource
import dotkt.support.events.WeakPolicySource

class CustomEventPolicyTests {
    @TestAttribute
    fun interfaceEventUsesPolicyAndExactSubscriptionIdentity() {
        val first = CustomNotifyingSource()
        val second = CustomNotifyingSource()
        assertEquals("1234", first.initializationOrder)
        assertTrue(PolicyEvidence.IsNativeEvent(first, "PropertyChanged"))
        var received = ""
        val token = (first as INotifyPropertyChanged).PropertyChanged.subscribe { _, args ->
            received += args.PropertyName
        }
        first.fire("one")
        second.fire("wrong")
        assertEquals("one", received)
        token.close()
        token.close()
        assertEquals(1, first.policy.adds)
        assertEquals(1, first.policy.removes)
        assertTrue(first.policy.added === first.policy.removed)
        assertEquals(0, second.policy.adds)
        first.fire("closed")
        assertEquals("one", received)
    }

    @TestAttribute
    fun ordinaryPolicyDelegateIsNotAnEvent() {
        val source = CustomNotifyingSource()
        assertTrue(PolicyEvidence.IsOrdinaryProperty(source, "ordinary"))
        var received = ""
        val token = source.ordinary.subscribe { _, args -> received += args.PropertyName }
        source.fire("ordinary")
        token.close()
        source.fire("closed")
        assertEquals("ordinary", received)
    }

    @TestAttribute
    fun genericFunctionPolicySurvivesDllBoundary() {
        val source = CustomGenericSource<Int, String?>()
        var received = ""
        val token = source.handle().subscribe { received += it ?: "null" }
        source.fire("text")
        source.fire(null)
        token.close()
        token.close()
        source.fire("closed")
        assertEquals("textnull", received)
        assertEquals(1, source.policy.removes)
        assertTrue(source.policy.added === source.policy.removed)

        val numbers = CustomGenericSource<String, Int>()
        var sum = 0
        val numberToken = numbers.handle().subscribe { sum += it }
        numbers.fire(42)
        numberToken.close()
        numbers.fire(100)
        assertEquals(42, sum)

        val resultToken = CustomResultSource.Requested.subscribe { number, text -> number + text.length }
        CustomResultSource.fire(40, "ok")
        assertEquals(42, CustomResultSource.result)
        resultToken.close()
        CustomResultSource.fire(100, "closed")
        assertEquals(42, CustomResultSource.result)

        var companionCalls = 0
        val companionToken = CustomCompanionSource.Changed.subscribe { companionCalls++ }
        CustomCompanionSource.fire()
        companionToken.close()
        CustomCompanionSource.fire()
        assertEquals(1, companionCalls)

        val concrete = ConcreteFunctionSource()
        var concreteValue = 0
        val concreteToken = concrete.handle().subscribe { concreteValue = it }
        concrete.fire(42)
        concreteToken.close()
        concrete.fire(100)
        assertEquals(42, concreteValue)

        val nullable = NullablePayloadSource()
        var nullableValue = "initial"
        val nullableToken = nullable.Changed.subscribe { nullableValue = it?.toString() ?: "null" }
        nullable.fire(7)
        assertEquals("7", nullableValue)
        nullable.fire(null)
        nullableToken.close()
        nullable.fire(100)
        assertEquals("null", nullableValue)
    }

    @TestAttribute
    fun weakPolicyPrunesDeadSubscriberAndPreservesRegistrationOrder() {
        val source = WeakPolicySource()
        var order = ""
        val first = source.Changed.subscribe { order += "a" }
        val dead = PolicyEvidence.RegisterCollectible { handler -> source.Changed.subscribe(handler) }
        val last = source.Changed.subscribe { order += "b" }
        assertEquals(3, source.policy.count)
        assertTrue(PolicyEvidence.Collect(dead))
        source.fire("payload")
        assertEquals("ab", order)
        assertEquals(2, source.policy.count)
        first.close()
        source.fire("again")
        assertEquals("abb", order)
        last.close()
        assertEquals(0, source.policy.count)
    }
}
