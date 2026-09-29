package roundtrip.inheritedsuspendcalls

interface CallSink<in T> { fun emit(value: T) }
interface OtherCallSink<in T> { fun emitOther(value: T) }
interface CallStream<out T> { suspend fun collect(sink: CallSink<T>) }
interface CallMarker<out T> : CallStream<T>
interface ReorderedCallMarker<A, out B> : CallMarker<B>
interface OverloadedCallMarker<out T> : CallMarker<T> {
    suspend fun collect(sink: OtherCallSink<T>)
}

class ProducerCallStream<T>(private val value: T, private val pause: suspend () -> Unit) :
    ReorderedCallMarker<String, T>, OverloadedCallMarker<T> {
    override suspend fun collect(sink: CallSink<T>) {
        pause()
        sink.emit(value)
    }
    override suspend fun collect(sink: OtherCallSink<T>) {
        pause()
        sink.emitOther(value)
    }
}

interface OrdinaryCallStream<out T> {
    fun collect(sink: CallSink<T>)
    fun collect(sink: OtherCallSink<T>)
}
interface OrdinaryCallMarker<out T> : OrdinaryCallStream<T>
class ProducerOrdinaryCallStream<T>(private val value: T) : OrdinaryCallMarker<T> {
    override fun collect(sink: CallSink<T>) { sink.emit(value) }
    override fun collect(sink: OtherCallSink<T>) { sink.emitOther(value) }
}
