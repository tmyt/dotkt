import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import roundtrip.inheritedsuspendcalls.*

private class CallPause {
    var pending: Continuation<Unit>? = null
    suspend fun pause() { suspendCoroutine<Unit> { pending = it } }
}
private class CallRecorder<T>(private val expected: T) : CallSink<T>, OtherCallSink<T> {
    var ordinary = 0
    var other = 0
    override fun emit(value: T) { check(value == expected); ordinary++ }
    override fun emitOther(value: T) { check(value == expected); other++ }
}
private class ConsumerCallStream(private val pause: CallPause) :
    ReorderedCallMarker<Long, Int>, OverloadedCallMarker<Int> {
    override suspend fun collect(sink: CallSink<Int>) { pause.pause(); sink.emit(42) }
    override suspend fun collect(sink: OtherCallSink<Int>) { pause.pause(); sink.emitOther(42) }
}
private interface LocalCallMarker<T> : CallStream<T>
private class LocalCallStream(private val pause: CallPause) : LocalCallMarker<Int> {
    override suspend fun collect(sink: CallSink<Int>) { pause.pause(); sink.emit(42) }
}

private suspend fun <T> viaMarker(stream: CallMarker<T>, sink: CallSink<T>) {
    stream.collect(sink)
}
private suspend fun <A, B> viaReordered(stream: ReorderedCallMarker<A, B>, sink: CallSink<B>) {
    stream.collect(sink)
}
private suspend fun <T> viaOverload(stream: OverloadedCallMarker<T>, sink: OtherCallSink<T>) {
    stream.collect(sink)
}
private fun <T> viaOrdinaryMarker(stream: OrdinaryCallMarker<T>, sink: CallSink<T>, other: OtherCallSink<T>) {
    stream.collect(sink)
    stream.collect(other)
}

private fun checkPausedCall(pause: CallPause, block: suspend () -> Unit) {
    var completed = false
    pause.pending = null
    block.startCoroutine(object : Continuation<Unit> {
        override val context: CoroutineContext = EmptyCoroutineContext
        override fun resumeWith(result: Result<Unit>) { result.getOrThrow(); completed = true }
    })
    check(!completed)
    val pending = pause.pending
    check(pending != null)
    pending.resume(Unit)
    check(completed)
}

class InheritedSuspendCallTests {
    @TestAttribute
    fun importedMarkerCallsResolveTheSelectedSuspendSlot() {
        val pause = CallPause()
        val sink = CallRecorder(42)
        val other: OtherCallSink<Int> = sink
        val ordinary: CallSink<Int> = sink
        val producer = ProducerCallStream<Int>(42) { pause.pause() }
        val consumer = ConsumerCallStream(pause)
        val local = LocalCallStream(pause)
        val calls: List<suspend () -> Unit> = listOf(
            { producer.collect(ordinary) },
            { (producer as CallStream<Int>).collect(ordinary) },
            { (producer as CallMarker<Int>).collect(ordinary) },
            { (producer as ReorderedCallMarker<String, Int>).collect(ordinary) },
            { (producer as OverloadedCallMarker<Int>).collect(ordinary) },
            { viaMarker(producer, ordinary) },
            { viaReordered(producer, ordinary) },
            { consumer.collect(ordinary) },
            { (consumer as CallMarker<Int>).collect(ordinary) },
            { viaReordered(consumer, ordinary) },
            { (local as LocalCallMarker<Int>).collect(ordinary) }
        )
        for (call in calls) {
            val before = sink.ordinary
            checkPausedCall(pause, call)
            check(sink.ordinary == before + 1)
            check(sink.other == 0)
        }
        checkPausedCall(pause) { (producer as OverloadedCallMarker<Int>).collect(other) }
        check(sink.other == 1)
        checkPausedCall(pause) { viaOverload(consumer, other) }
        check(sink.other == 2)
        check(sink.ordinary == calls.size)
        val ordinaryStream: OrdinaryCallMarker<Int> = ProducerOrdinaryCallStream(42)
        ordinaryStream.collect(ordinary)
        ordinaryStream.collect(other)
        viaOrdinaryMarker(ordinaryStream, ordinary, other)
        check(sink.ordinary == calls.size + 2)
        check(sink.other == 4)
    }
}
