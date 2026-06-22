// T3(c) — ContinuationInterceptor / intercepted(): a dispatcher in the continuation's context wraps it so resume
// is dispatched. intercepted() finds the interceptor by key and applies it; resume then goes through the wrapper.
import clr.SinkI
import clr.Recorder
import kotlin.coroutines.Continuation
import kotlin.coroutines.intrinsics.intercepted
import kotlin.coroutines.resume

fun main() {
    val sink = SinkI()                          // Continuation<Int> with a RecordingDispatcher in its context
    val c: Continuation<Int> = sink
    val ic = c.intercepted()                    // wrap via the context's interceptor (the dispatcher)
    ic.resume(7)                                // dispatched: Recorder bumps, then the sink receives 7
    println(Recorder.count())                   // 1  (interception happened)
    println(sink.value)                         // 7
}
