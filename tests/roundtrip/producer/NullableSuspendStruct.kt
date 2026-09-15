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

suspend fun nullableNumber(present: Boolean): Int? = if (present) 42 else null
suspend fun nullableDay(present: Boolean): System.DayOfWeek? = if (present) System.DayOfWeek.Monday else null
suspend fun <T> genericNullableSegment(value: ArraySegment<T>, present: Boolean): ArraySegment<T>? =
    if (present) value else null
suspend fun segmentThroughFinally(gate: SegmentGate): ArraySegment<String?>? {
    try { return null } finally { gate.await() }
}

interface SegmentSource { suspend fun read(present: Boolean): ArraySegment<String?>? }
class SegmentSourceImpl : SegmentSource {
    override suspend fun read(present: Boolean): ArraySegment<String?>? = nullableSegment(present)
}
