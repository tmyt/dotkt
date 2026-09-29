package roundtrip.suspendbottomslots

interface BottomSink<in T> { fun emit(value: T) }
interface BottomStream<out T> { suspend fun collect(sink: BottomSink<T>) }
interface NeverStream<out T> : BottomStream<T> {
    override suspend fun collect(sink: BottomSink<T>): Nothing
}
interface MarkedStream<out T> : BottomStream<T>

class ProducerBottomStream<T>(private val pause: suspend () -> Unit, private val failure: Any) :
    NeverStream<T>, MarkedStream<T> {
    override suspend fun collect(sink: BottomSink<T>): Nothing {
        pause()
        throw (failure as Throwable)
    }
}

class ProducerForwardingStream<T>(stream: NeverStream<T>) : NeverStream<T> by stream, MarkedStream<T>

interface BottomValue<T> { suspend fun read(): T }
interface BottomUnit { suspend fun read() }
open class NestedSlotResult(val value: String)
interface NestedReferenceSlot<T> { fun put(value: T): NestedSlotResult }
interface NestedSuspendReferenceSlot<T> { suspend fun put(value: T): NestedSlotResult }
class ProducerClosedUnit(private val failure: Any) : BottomValue<Unit>, BottomUnit {
    override suspend fun read(): Nothing = throw (failure as Throwable)
}
class ProducerBottomValue<T>(private val failure: Any) : BottomValue<T> {
    override suspend fun read(): Nothing = throw (failure as Throwable)
}
