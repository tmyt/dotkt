import NUnit.Framework.TestAttribute
import kotlin.clr.ClrEvent
import kotlin.clr.clrEvent
import roundtrip.eventframes.*

class LocalSubscriptionEventSource {
    val pulse: ClrEvent<(Int) -> Unit> by clrEvent()
    fun fire(value: Int) { pulse.invoke(value) }
}

class EventSubscriptionFrameTests {
    @TestAttribute
    fun importedInlineSubscriptionRetainsItsCallbackDeclaration() {
        val source = ImportedEventSource()
        var seen = 0
        val token = inlineSubscribe(source) { seen += it }
        source.fire(7)
        check(seen == 7)
        token.Dispose()
        source.fire(11)
        check(seen == 7)
    }

    @TestAttribute
    fun sourceAndImportedDefaultSubscriptionsRemoveTheirOwnHandlers() {
        val early = LocalSubscriptionEventSource()
        val late = ImportedEventSource()
        var seen = 0
        val first = early.pulse.subscribe { seen += it }
        val second = subscribeDefault(late)
        early.fire(3)
        late.fire(5)
        check(seen == 3)
        check(late.observed == 5)
        first.close()
        second.Dispose()
        early.fire(7)
        late.fire(11)
        check(seen == 3)
        check(late.observed == 5)
    }
}
