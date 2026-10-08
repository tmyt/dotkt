package roundtrip.eventframes

import kotlin.clr.ClrEvent
import kotlin.clr.clrEvent

class ImportedEventSource {
    val pulse: ClrEvent<(Int) -> Unit> by clrEvent()
    var observed = 0
    fun fire(value: Int) { pulse.invoke(value) }
}

inline fun inlineSubscribe(source: ImportedEventSource, noinline handler: (Int) -> Unit): AutoCloseable =
    source.pulse.subscribe(handler)

fun subscribeDefault(source: ImportedEventSource,
    token: AutoCloseable = source.pulse.subscribe { source.observed += it }): AutoCloseable = token
