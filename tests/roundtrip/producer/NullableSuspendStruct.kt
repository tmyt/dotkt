package roundtrip.nullablesuspendstruct

import System.ArraySegment
import kotlin.coroutines.*

suspend fun nullableSegment(present: Boolean): ArraySegment<String?>? =
    if (present) ArraySegment<String?>(arrayOf(null, "value")) else null

class SegmentGate {
    private var pending: Continuation<ArraySegment<String?>?>? = null
    suspend fun await(): ArraySegment<String?>? = suspendCoroutine { pending = it }
    fun complete(present: Boolean) {
        pending!!.resume(if (present) ArraySegment<String?>(arrayOf(null, "resumed")) else null)
    }
}

suspend fun delayedSegment(gate: SegmentGate): ArraySegment<String?>? = gate.await()

interface SegmentSource { suspend fun read(present: Boolean): ArraySegment<String?>? }
class SegmentSourceImpl : SegmentSource {
    override suspend fun read(present: Boolean): ArraySegment<String?>? = nullableSegment(present)
}
