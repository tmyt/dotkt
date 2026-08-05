// CoroutineContext.Key star-projection + await-interceptor-precedence battery (feature fixture). These exercise the
// `CoroutineContext.Key<E : Element>` self-ref-bounded key surface (bir2cir StarProjectionBoundLowering repoints the
// `Key<*>` projection to `Key<Element>`) and the #7 await-point resume precedence (interceptor > SyncContext > inline).
//
// The old per-case battery baselined formal-only #12 findings for these self-ref-bounded Key shapes. Existential
// star-projection metadata and bir2cir lowering now preserve the signatures without producing those findings, so
// both the runtime assertions and whole-assembly ILVerify gate are expected to remain green.
//
// Coverage preserved (old case -> method):
//   il-coctxkey       -> coCtxKey_abstractElementCompanionKey     (#12: AbstractCoroutineContextElement subtype)
//   il-cointercept    -> coIntercept_interceptorKeyProjection      (#12: ContinuationInterceptor key<*> projection)
//   il-awaitintercept -> awaitIntercept_scenario{A,B,C}            (#7 Part B: interceptor-owned await resume + controls)
//
// Top-level names are family-prefixed (`ContextInterceptionContextKey`/`ContextInterceptionInterceptor`/`ContextInterceptionAwait`/`contextInterceptionAwait`).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import System.Threading.Tasks.Task1
import System.Threading.Tasks.TaskCompletionSource1
import System.Threading.Thread
import kotlin.coroutines.AbstractCoroutineContextElement
import kotlin.coroutines.Continuation
import kotlin.coroutines.ContinuationInterceptor
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine

// ---- il-coctxkey: subclassing AbstractCoroutineContextElement with a self-typed companion Key ----------------
class ContextInterceptionContextKeyElement : AbstractCoroutineContextElement(Key) {
    companion object Key : CoroutineContext.Key<ContextInterceptionContextKeyElement>
}

// ---- il-cointercept: a ContinuationInterceptor impl exposing `key: Key<*>` -----------------------------------
class ContextInterceptionContinuationInterceptor : ContinuationInterceptor {
    override val key: CoroutineContext.Key<*> get() = ContinuationInterceptor
    override fun <T> interceptContinuation(continuation: Continuation<T>): Continuation<T> = continuation
}

// A plain intermediate interface reproduces kotlinx.coroutines' `Job : CoroutineContext.Element` shape. The frontend
// materializes Element's inherited members on ContextInterceptionTransitiveElement as abstract slots; the class-side CLR MethodImpls
// therefore have to forward those distinct slots to Element's referenced stdlib DIMs.
interface ContextInterceptionTransitiveElement : CoroutineContext.Element

class ContextInterceptionTransitiveElementImplementation : ContextInterceptionTransitiveElement {
    companion object Key : CoroutineContext.Key<ContextInterceptionTransitiveElementImplementation>
    override val key: CoroutineContext.Key<*> get() = Key
}

// BIR intentionally keeps the LOCAL intermediate receiver owner; bir2cir must bind this generic
// call to the nearest referenced declaration (Element, not the transitive CoroutineContext slot).
fun <E : CoroutineContext.Element> contextInterceptionTransitiveGet(
    delegate: ContextInterceptionTransitiveElement,
    key: CoroutineContext.Key<E>,
): E? = delegate[key]

// ---- il-awaitintercept: an interceptor that COUNTS resumes routed through it (#7 Part B, harness inlined) -----
class ContextInterceptionAwaitCountingInterceptor : ContinuationInterceptor {
    var resumes: Int = 0

    override val key: CoroutineContext.Key<*> get() = ContinuationInterceptor

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

// A terminal completion Continuation with a caller-supplied context that captures the coroutine outcome.
class ContextInterceptionAwaitSink<T>(override val context: CoroutineContext) : Continuation<T> {
    var done: Boolean = false
    var value: Any? = null
    var error: Throwable? = null
    override fun resumeWith(result: Result<T>) {
        value = result.getOrNull()
        error = result.exceptionOrNull()
        done = true
    }
}

suspend fun contextInterceptionAwaitCapturing(tcs: TaskCompletionSource1<Int>): Int = tcs.Task.await()
suspend fun contextInterceptionAwaitWithoutCapture(tcs: TaskCompletionSource1<Int>): Int = tcs.Task.await(captureContext = false)

// Deterministic drain: the captureContext=false / no-SyncContext resume MAY land on the threadpool (async), so the
// completion is not necessarily set synchronously by SetResult. Bounded-wait for the sink to complete (the analog of
// blockOn's Monitor.Wait drain) so the assertion is race-free — the asserted values are unchanged.
fun contextInterceptionAwaitDrain(sink: ContextInterceptionAwaitSink<Int>) {
    var tries = 0
    while (!sink.done && tries < 3000) { Thread.Sleep(1); tries += 1 }
}

class CoroutineContextInterceptionTests {
    @TestAttribute
    fun abstractElementCompanionKey() {
        val e = ContextInterceptionContextKeyElement()
        assertEquals(true, e.key === ContextInterceptionContextKeyElement.Key)   // True
        assertEquals(true, e[ContextInterceptionContextKeyElement.Key] === e)    // True (get<E : Element>)
    }

    @TestAttribute
    fun interceptorKeyProjection() {
        val i = ContextInterceptionContinuationInterceptor()
        assertEquals(true, i.key === ContinuationInterceptor)   // True
        assertEquals(true, (i as CoroutineContext)[ContinuationInterceptor] === i)
    }

    @TestAttribute
    fun transitiveReferencedElementDefaults() {
        val e: ContextInterceptionTransitiveElement = ContextInterceptionTransitiveElementImplementation()
        assertEquals(true, e[ContextInterceptionTransitiveElementImplementation.Key] === e)
        assertEquals(e, e.fold<Any?>(null) { _, element -> element })
        assertEquals(EmptyCoroutineContext, e.minusKey(ContextInterceptionTransitiveElementImplementation.Key))

        assertEquals(true, contextInterceptionTransitiveGet(e, ContextInterceptionTransitiveElementImplementation.Key) === e)
    }

    @TestAttribute
    fun interceptorPrecedence() {
        val icept = ContextInterceptionAwaitCountingInterceptor()
        val sink = ContextInterceptionAwaitSink<Int>(icept)
        val tcs = TaskCompletionSource1<Int>()
        val block: suspend () -> Int = { contextInterceptionAwaitCapturing(tcs) }
        block.startCoroutine(sink)   // runs to the await, which SUSPENDS (tcs not completed)
        icept.resumes = 0            // reset: isolate the await-point resume from the start resume
        tcs.SetResult(42)            // resume -> OnCompleted -> this.intercepted() -> wrapper (resumes++)
        contextInterceptionAwaitDrain(sink)
        assertEquals(1, icept.resumes)   // A:resumes=1
        assertEquals(true, sink.done)    // done=True
        assertEquals(42, sink.value)     // value=42
    }

    @TestAttribute
    fun defaultCapturingPath() {
        val sink = ContextInterceptionAwaitSink<Int>(EmptyCoroutineContext)
        val tcs = TaskCompletionSource1<Int>()
        val block: suspend () -> Int = { contextInterceptionAwaitCapturing(tcs) + 5 }
        block.startCoroutine(sink)
        tcs.SetResult(7)
        contextInterceptionAwaitDrain(sink)
        assertEquals(true, sink.done)   // B:done=True
        assertEquals(12, sink.value)    // value=12
    }

    @TestAttribute
    fun configureAwaitFalsePath() {
        val sink = ContextInterceptionAwaitSink<Int>(EmptyCoroutineContext)
        val tcs = TaskCompletionSource1<Int>()
        val block: suspend () -> Int = { contextInterceptionAwaitWithoutCapture(tcs) + 2 }
        block.startCoroutine(sink)
        tcs.SetResult(9)
        contextInterceptionAwaitDrain(sink)
        assertEquals(true, sink.done)   // C:done=True
        assertEquals(11, sink.value)    // value=11
    }
}
