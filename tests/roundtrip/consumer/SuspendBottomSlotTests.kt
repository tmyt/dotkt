import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import roundtrip.suspendbottomslots.*

private class ImportedBottomStream<T>(private val pause: suspend () -> Unit, private val failure: Throwable) :
    NeverStream<T>, MarkedStream<T> {
    override suspend fun collect(sink: BottomSink<T>): Nothing {
        pause()
        throw failure
    }
}
private class ImportedForwardingStream<T>(stream: NeverStream<T>) : NeverStream<T> by stream, MarkedStream<T>

private interface LocalBottomStream<T> { suspend fun collect(sink: BottomSink<T>) }
private interface LocalNeverStream<T> : LocalBottomStream<T> {
    override suspend fun collect(sink: BottomSink<T>): Nothing
}
private interface LocalMarkedStream<T> : LocalBottomStream<T>
private class LocalBottomBody<T>(private val pause: suspend () -> Unit, private val failure: Throwable) :
    LocalNeverStream<T>, LocalMarkedStream<T> {
    override suspend fun collect(sink: BottomSink<T>): Nothing {
        pause()
        throw failure
    }
}
private class LocalForwardingStream<T>(stream: LocalNeverStream<T>) : LocalNeverStream<T> by stream, LocalMarkedStream<T>

private class ImportedBottomValue<T>(private val failure: Throwable) : BottomValue<T> {
    override suspend fun read(): Nothing = throw failure
}
private class ImportedClosedUnit(private val failure: Throwable) : BottomValue<Unit>, BottomUnit {
    override suspend fun read(): Nothing = throw failure
}
private interface LocalBottomValue<T> { suspend fun read(): T }
private interface LocalBottomUnit { suspend fun read() }
private class LocalClosedUnit(private val failure: Throwable) : LocalBottomValue<Unit>, LocalBottomUnit {
    override suspend fun read(): Nothing = throw failure
}
private class SlotOuter { class Nested(val text: String) }
private class NestedSlotDerived(text: String) : NestedSlotResult(text)
private class NestedReferenceBody : NestedReferenceSlot<SlotOuter.Nested> {
    override fun put(value: SlotOuter.Nested): NestedSlotDerived = NestedSlotDerived(value.text)
}
private class NestedSuspendReferenceBody : NestedSuspendReferenceSlot<SlotOuter.Nested> {
    override suspend fun put(value: SlotOuter.Nested): NestedSlotDerived = NestedSlotDerived(value.text)
}

private fun checkBottomFailure(failure: Throwable, block: suspend () -> Unit) {
    var completed = false
    var caught: Throwable? = null
    block.startCoroutine(object : Continuation<Unit> {
        override val context: CoroutineContext = EmptyCoroutineContext
        override fun resumeWith(result: Result<Unit>) {
            completed = true
            caught = result.exceptionOrNull()
        }
    })
    check(completed)
    check(caught === failure)
}

class SuspendBottomSlotTests {
    @TestAttribute
    fun bottomOverridesOwnEachSuspendSlotOnce() {
        val failure = IllegalStateException("bottom slot")
        var pending: Continuation<Unit>? = null
        val pause: suspend () -> Unit = { suspendCoroutine<Unit> { pending = it } }
        val sink = object : BottomSink<Int> { override fun emit(value: Int) {} }
        val producer = ProducerBottomStream<Int>(pause, failure)
        val imported = ImportedBottomStream<Int>(pause, failure)
        val producerForwarder = ProducerForwardingStream<Int>(producer)
        val importedForwarder = ImportedForwardingStream<Int>(imported)
        val local = LocalBottomBody<Int>(pause, failure)
        val localForwarder = LocalForwardingStream<Int>(local)
        val calls: List<suspend () -> Unit> = listOf(
            { producer.collect(sink) }, { imported.collect(sink) },
            { producerForwarder.collect(sink) }, { importedForwarder.collect(sink) },
            { (producer as BottomStream<Int>).collect(sink) },
            { (imported as BottomStream<Int>).collect(sink) },
            { (producerForwarder as BottomStream<Int>).collect(sink) },
            { (importedForwarder as BottomStream<Int>).collect(sink) },
            { local.collect(sink) }, { localForwarder.collect(sink) },
            { (local as LocalMarkedStream<Int>).collect(sink) },
            { (localForwarder as LocalBottomStream<Int>).collect(sink) }
        )
        for (call in calls) {
            var completed = false
            var caught: Throwable? = null
            pending = null
            call.startCoroutine(object : Continuation<Unit> {
                override val context: CoroutineContext = EmptyCoroutineContext
                override fun resumeWith(result: Result<Unit>) {
                    completed = true
                    caught = result.exceptionOrNull()
                }
            })
            check(!completed)
            val continuation = pending
            check(continuation != null)
            continuation.resume(Unit)
            check(completed)
            check(caught === failure)
        }
        val producerValue: BottomValue<Int> = ProducerBottomValue<Int>(failure)
        val importedValue: BottomValue<String> = ImportedBottomValue<String>(failure)
        checkBottomFailure(failure) { producerValue.read() }
        checkBottomFailure(failure) { importedValue.read() }
        val producerUnit = ProducerClosedUnit(failure)
        val importedUnit = ImportedClosedUnit(failure)
        val localUnit = LocalClosedUnit(failure)
        checkBottomFailure(failure) { (producerUnit as BottomValue<Unit>).read() }
        checkBottomFailure(failure) { (producerUnit as BottomUnit).read() }
        checkBottomFailure(failure) { (importedUnit as BottomValue<Unit>).read() }
        checkBottomFailure(failure) { (importedUnit as BottomUnit).read() }
        checkBottomFailure(failure) { (localUnit as LocalBottomValue<Unit>).read() }
        checkBottomFailure(failure) { (localUnit as LocalBottomUnit).read() }
        val nested = SlotOuter.Nested("nested slot")
        val nestedBody = NestedReferenceBody()
        val nestedSlot: NestedReferenceSlot<SlotOuter.Nested> = nestedBody
        check(nestedBody.put(nested).value == "nested slot")
        check(nestedSlot.put(nested).value == "nested slot")
        var nestedCompleted = false
        val nestedCall: suspend () -> Unit = {
            val body = NestedSuspendReferenceBody()
            val slot: NestedSuspendReferenceSlot<SlotOuter.Nested> = body
            check(body.put(nested).value == "nested slot")
            check(slot.put(nested).value == "nested slot")
        }
        nestedCall.startCoroutine(object : Continuation<Unit> {
            override val context: CoroutineContext = EmptyCoroutineContext
            override fun resumeWith(result: Result<Unit>) {
                result.getOrThrow()
                nestedCompleted = true
            }
        })
        check(nestedCompleted)
    }
}
