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
// Top-level names are family-prefixed (`ContextInterceptionCtxk`/`ContextInterceptionIcept`/`ContextInterceptionAwi`/`contextInterceptionAwi`).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
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
class ContextInterceptionCtxkElem : AbstractCoroutineContextElement(Key) {
    companion object Key : CoroutineContext.Key<ContextInterceptionCtxkElem>
}

// ---- il-cointercept: a ContinuationInterceptor impl exposing `key: Key<*>` -----------------------------------
class ContextInterceptionIceptInterceptor : ContinuationInterceptor {
    override val key: CoroutineContext.Key<*> get() = ContinuationInterceptor
    override fun <T> interceptContinuation(continuation: Continuation<T>): Continuation<T> = continuation
}

// A plain intermediate interface reproduces kotlinx.coroutines' `Job : CoroutineContext.Element` shape. The frontend
// materializes Element's inherited members on ContextInterceptionTransitiveElement as abstract slots; the class-side CLR MethodImpls
// therefore have to forward those distinct slots to Element's referenced stdlib DIMs.
interface ContextInterceptionTransitiveElement : CoroutineContext.Element

class ContextInterceptionTransitiveElementImpl : ContextInterceptionTransitiveElement {
    companion object Key : CoroutineContext.Key<ContextInterceptionTransitiveElementImpl>
    override val key: CoroutineContext.Key<*> get() = Key
}

// BIR intentionally keeps the LOCAL intermediate receiver owner; bir2cir must bind this generic
// call to the nearest referenced declaration (Element, not the transitive CoroutineContext slot).
fun <E : CoroutineContext.Element> contextInterceptionTransitiveGet(
    delegate: ContextInterceptionTransitiveElement,
    key: CoroutineContext.Key<E>,
): E? = delegate[key]

// ---- il-awaitintercept: an interceptor that COUNTS resumes routed through it (#7 Part B, harness inlined) -----
class ContextInterceptionAwiCountingInterceptor : ContinuationInterceptor {
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
class ContextInterceptionAwiSink<T>(override val context: CoroutineContext) : Continuation<T> {
    var done: Boolean = false
    var value: Any? = null
    var error: Throwable? = null
    override fun resumeWith(result: Result<T>) {
        value = result.getOrNull()
        error = result.exceptionOrNull()
        done = true
    }
}

suspend fun contextInterceptionAwiAwaitCapturing(tcs: TaskCompletionSource1<Int>): Int = tcs.Task.await()
suspend fun contextInterceptionAwiAwaitNoCapture(tcs: TaskCompletionSource1<Int>): Int = tcs.Task.await(captureContext = false)

// Deterministic drain: the captureContext=false / no-SyncContext resume MAY land on the threadpool (async), so the
// completion is not necessarily set synchronously by SetResult. Bounded-wait for the sink to complete (the analog of
// blockOn's Monitor.Wait drain) so the assertion is race-free — the asserted values are unchanged.
fun contextInterceptionAwiDrain(sink: ContextInterceptionAwiSink<Int>) {
    var tries = 0
    while (!sink.done && tries < 3000) { Thread.Sleep(1); tries += 1 }
}

class CoroutineContextInterceptionTests {
    @TestAttribute
    fun abstractElementCompanionKey() {
        val e = ContextInterceptionCtxkElem()
        assertEquals(true, e.key === ContextInterceptionCtxkElem.Key)   // True
        assertEquals(true, e[ContextInterceptionCtxkElem.Key] === e)    // True (get<E : Element>)
    }

    @TestAttribute
    fun interceptorKeyProjection() {
        val i = ContextInterceptionIceptInterceptor()
        assertEquals(true, i.key === ContinuationInterceptor)   // True
        assertEquals(true, (i as CoroutineContext)[ContinuationInterceptor] === i)
    }

    @TestAttribute
    fun transitiveReferencedElementDefaults() {
        val e: ContextInterceptionTransitiveElement = ContextInterceptionTransitiveElementImpl()
        assertEquals(true, e[ContextInterceptionTransitiveElementImpl.Key] === e)
        assertEquals(e, e.fold<Any?>(null) { _, element -> element })
        assertEquals(EmptyCoroutineContext, e.minusKey(ContextInterceptionTransitiveElementImpl.Key))

        assertEquals(true, contextInterceptionTransitiveGet(e, ContextInterceptionTransitiveElementImpl.Key) === e)
    }

    @TestAttribute
    fun interceptorPrecedence() {
        val icept = ContextInterceptionAwiCountingInterceptor()
        val sink = ContextInterceptionAwiSink<Int>(icept)
        val tcs = TaskCompletionSource1<Int>()
        val block: suspend () -> Int = { contextInterceptionAwiAwaitCapturing(tcs) }
        block.startCoroutine(sink)   // runs to the await, which SUSPENDS (tcs not completed)
        icept.resumes = 0            // reset: isolate the await-point resume from the start resume
        tcs.SetResult(42)            // resume -> OnCompleted -> this.intercepted() -> wrapper (resumes++)
        contextInterceptionAwiDrain(sink)
        assertEquals(1, icept.resumes)   // A:resumes=1
        assertEquals(true, sink.done)    // done=True
        assertEquals(42, sink.value)     // value=42
    }

    @TestAttribute
    fun defaultCapturingPath() {
        val sink = ContextInterceptionAwiSink<Int>(EmptyCoroutineContext)
        val tcs = TaskCompletionSource1<Int>()
        val block: suspend () -> Int = { contextInterceptionAwiAwaitCapturing(tcs) + 5 }
        block.startCoroutine(sink)
        tcs.SetResult(7)
        contextInterceptionAwiDrain(sink)
        assertEquals(true, sink.done)   // B:done=True
        assertEquals(12, sink.value)    // value=12
    }

    @TestAttribute
    fun configureAwaitFalsePath() {
        val sink = ContextInterceptionAwiSink<Int>(EmptyCoroutineContext)
        val tcs = TaskCompletionSource1<Int>()
        val block: suspend () -> Int = { contextInterceptionAwiAwaitNoCapture(tcs) + 2 }
        block.startCoroutine(sink)
        tcs.SetResult(9)
        contextInterceptionAwiDrain(sink)
        assertEquals(true, sink.done)   // C:done=True
        assertEquals(11, sink.value)    // value=11
    }
}
