package roundtriptests.eventraise

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.eventraise.Publisher
import roundtrip.eventraise.PublisherOuter
import roundtrip.eventraise.PolicyPublisher
import roundtrip.eventraise.localInheritedRaises
import roundtrip.eventraise.raiseProjected
import roundtrip.eventraise.SuperPublisher
import roundtrip.eventraise.WidePublisher
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
    fun later(value: Int): () -> Unit = { super.Changed(*arrayOf<Any?>(value)) }
}

private fun <A, B> raiseGeneric(source: Publisher<A, B>, value: B) { source.Changed(value) }

class EventRaiseTests {
    @TestAttribute
    fun projectedRaisesUseTheActualHandlerTypeAcrossModules() {
        fun imported(source: Publisher<String, in Int>, value: Any?) { source.Changed(value) }
        val broad = Publisher<String, Any?>()
        var received = ""
        val broadToken = broad.Changed.subscribe { received += "$it," }
        raiseProjected(broad, "local")
        imported(broad, "imported")
        imported(broad, 42)
        broadToken.close()
        assertEquals("local,imported,42,", received)

        val narrow = Publisher<String, Int>()
        var total = 0
        val narrowToken = narrow.Changed.subscribe { total += it }
        imported(narrow, 7)
        try {
            imported(narrow, "not an integer")
            error("a projected raise must still check the concrete handler's argument")
        } catch (_: ClassCastException) {}
        narrowToken.close()
        assertEquals(7, total)
    }

    @TestAttribute
    fun wideFunctionEventsUseCallableDelegatesAcrossModules() {
        val source = WidePublisher()
        assertEquals(18, source.localRaiseTotal())
        var total = 0
        val token = source.Query.subscribe { first, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, last ->
            total += last - first
            -1
        }
        fun fire() { source.Query(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17) }
        fire()
        token.close()
        fire()
        assertEquals(16, total)
    }

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

        var trace = ""
        val spreadToken = source.Changed.subscribe { trace += it.toString() }
        source.fireWithTail(3) { trace += "e"; emptyArray<Any?>() }
        fun selected(): Publisher<String, Int> { trace += "r"; return source }
        fun part(label: String, vararg args: Any?): Array<out Any?> { trace += label; return args }
        selected().Changed(*part("a"), *part("b", 4))
        try {
            selected().Changed(*part("x", 5), *part("y", 6))
            error("a dynamic raise must not discard extra arguments")
        } catch (_: IllegalArgumentException) {}
        spreadToken.close()
        assertEquals("e3rab4rxy", trace)

        val nested = PublisherOuter<String>().NestedPublisher<Int>()
        var pair = ""
        val nestedToken = nested.Changed.subscribe { first, second -> pair = "$first:$second" }
        nested.Changed("payload", 42)
        assertEquals("payload:42", pair)
        nested.Changed("spread", *arrayOf<Any?>(43))
        nestedToken.close()
        nested.Changed("ignored", 100)
        assertEquals("spread:43", pair)

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
