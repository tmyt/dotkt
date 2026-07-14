// #7 Part B TEST HARNESS — NOT stdlib. A ContinuationInterceptor that COUNTS resumes routed through it,
// plus a terminal Sink completion carrying a caller-supplied coroutine context. Single-threaded and
// deterministic: the interceptor's wrapper resumes INLINE (flips a counter, forwards to the delegate), so
// there is no wall-clock / thread-race flakiness. Proves the await-point resume precedence:
//   interceptor present  -> the resume routes THROUGH the interceptor (SuspendColdLowering routes the
//                           OnCompleted callback through ContinuationImpl.intercepted()).
@file:Suppress("UNCHECKED_CAST")

package dotkt.support

import kotlin.coroutines.Continuation
import kotlin.coroutines.ContinuationInterceptor
import kotlin.coroutines.CoroutineContext

/**
 * A [ContinuationInterceptor] that intercepts every resumption: [interceptContinuation] returns a [Wrapped]
 * continuation whose [Continuation.resumeWith] increments [resumes] before forwarding to the real delegate.
 * `resumes > 0` after an await resume proves the interceptor OWNED that resume (took precedence over the raw
 * SynchronizationContext capture).
 */
public class CountingInterceptor : ContinuationInterceptor {
    public var resumes: Int = 0

    override val key: CoroutineContext.Key<*> get() = ContinuationInterceptor

    // Override get() concretely: `context[ContinuationInterceptor]` must return this interceptor. This
    // sidesteps ContinuationInterceptor's polymorphic default get() (its `AbstractCoroutineContextKey<*,*>`
    // star projection is blocked on the SEPARATE GitHub #2 generic-DIM lowering) — orthogonal to the #7
    // await-resume precedence this case exercises.
    @Suppress("UNCHECKED_CAST")
    override fun <E : CoroutineContext.Element> get(key: CoroutineContext.Key<E>): E? =
        if (key === ContinuationInterceptor) this as E else null

    override fun <T> interceptContinuation(continuation: Continuation<T>): Continuation<T> = Wrapped(continuation)

    private inner class Wrapped<T>(val delegate: Continuation<T>) : Continuation<T> {
        override val context: CoroutineContext get() = delegate.context
        override fun resumeWith(result: Result<T>) {
            resumes += 1
            delegate.resumeWith(result)
        }
    }
}

/** A terminal completion [Continuation] with a caller-supplied [context] that captures the coroutine outcome. */
public class Sink<T>(override val context: CoroutineContext) : Continuation<T> {
    public var done: Boolean = false
    public var value: Any? = null
    public var error: Throwable? = null
    override fun resumeWith(result: Result<T>) {
        value = result.getOrNull()
        error = result.exceptionOrNull()
        done = true
    }
}
