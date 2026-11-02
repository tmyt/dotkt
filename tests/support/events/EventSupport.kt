package dotkt.support.events

import kotlin.clr.ClrEvent
import kotlin.clr.EventSubscription
import kotlin.clr.clrEvent

/** Ordinary properties must not acquire event accessors merely because their type is ClrEvent. */
class EventBox<H>(var event: ClrEvent<H>)

fun <H> passEvent(event: ClrEvent<H>): ClrEvent<H> = event

fun <H> ClrEvent<H>.subscribeLater(handler: H): () -> EventSubscription<H> = { subscribe(handler) }

class LocalSource {
    val Changed: ClrEvent<(Int) -> Unit> by clrEvent()
    fun fire(value: Int) { Changed(value) }
    fun handle(): ClrEvent<(Int) -> Unit> = Changed
}

class GenericSource<A, B> {
    val Changed: ClrEvent<(B) -> Unit> by clrEvent()
    fun fire(value: B) { Changed(value) }
}

class ReorderedSource<A, B>(private val source: GenericSource<B, A>) {
    fun handle(): ClrEvent<(A) -> Unit> = source.Changed
}
