// CoroutineContext.Key star-projection + await-interceptor-precedence battery (CorA batch). These exercise the
// `CoroutineContext.Key<E : Element>` self-ref-bounded key surface (bir2cir StarProjectionBoundLowering repoints the
// `Key<*>` projection to `Key<Element>`) and the #7 await-point resume precedence (interceptor > SyncContext > inline).
//
// ILVERIFY NOTE: these three carry the SAME runtime-safe formal-only finding as the old verify-compiler-tests.sh XFAIL_ILVERIFY
// entries — the invariant `Key<Element>` slot filled by a `Key<Self>` companion (GitHub #12, formal-only follow-up of
// the closed #2). The RUN lane is green; the finding is baselined for DotKt.Tests.Coroutines.dll in tests/run-ilverify.sh.
//
// Coverage preserved (old case -> method):
//   il-coctxkey       -> coCtxKey_abstractElementCompanionKey     (#12: AbstractCoroutineContextElement subtype)
//   il-cointercept    -> coIntercept_interceptorKeyProjection      (#12: ContinuationInterceptor key<*> projection)
//   il-awaitintercept -> awaitIntercept_scenario{A,B,C}            (#7 Part B: interceptor-owned await resume + controls)
//
// Top-level names are family-prefixed (`CorACtxk`/`CorAIcept`/`CorAAwi`/`corAAwi`).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Threading.Tasks.Task1
import System.Threading.Tasks.TaskCompletionSource1
import System.Threading.Thread
import kotlin.clr.await
import kotlin.coroutines.AbstractCoroutineContextElement
import kotlin.coroutines.Continuation
import kotlin.coroutines.ContinuationInterceptor
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine

// ---- il-coctxkey: subclassing AbstractCoroutineContextElement with a self-typed companion Key ----------------
class CorACtxkElem : AbstractCoroutineContextElement(Key) {
    companion object Key : CoroutineContext.Key<CorACtxkElem>
}

// ---- il-cointercept: a ContinuationInterceptor impl exposing `key: Key<*>` -----------------------------------
class CorAIceptInterceptor : ContinuationInterceptor {
    override val key: CoroutineContext.Key<*> get() = ContinuationInterceptor
    override fun <T> interceptContinuation(continuation: Continuation<T>): Continuation<T> = continuation
}

// A plain intermediate interface reproduces kotlinx.coroutines' `Job : CoroutineContext.Element` shape. The frontend
// materializes Element's inherited members on CorATransitiveElement as abstract slots; the class-side CLR MethodImpls
// therefore have to forward those distinct slots to Element's referenced stdlib DIMs.
interface CorATransitiveElement : CoroutineContext.Element

class CorATransitiveElementImpl : CorATransitiveElement {
    companion object Key : CoroutineContext.Key<CorATransitiveElementImpl>
    override val key: CoroutineContext.Key<*> get() = Key
}

// BIR intentionally keeps the LOCAL intermediate receiver owner; bir2cir must bind this generic
// call to the nearest referenced declaration (Element, not the transitive CoroutineContext slot).
fun <E : CoroutineContext.Element> corATransitiveGet(
    delegate: CorATransitiveElement,
    key: CoroutineContext.Key<E>,
): E? = delegate[key]

// ---- il-awaitintercept: an interceptor that COUNTS resumes routed through it (#7 Part B, harness inlined) -----
class CorAAwiCountingInterceptor : ContinuationInterceptor {
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
class CorAAwiSink<T>(override val context: CoroutineContext) : Continuation<T> {
    var done: Boolean = false
    var value: Any? = null
    var error: Throwable? = null
    override fun resumeWith(result: Result<T>) {
        value = result.getOrNull()
        error = result.exceptionOrNull()
        done = true
    }
}

suspend fun corAAwiAwaitCapturing(tcs: TaskCompletionSource1<Int>): Int = tcs.Task.await()
suspend fun corAAwiAwaitNoCapture(tcs: TaskCompletionSource1<Int>): Int = tcs.Task.await(captureContext = false)

// Deterministic drain: the captureContext=false / no-SyncContext resume MAY land on the threadpool (async), so the
// completion is not necessarily set synchronously by SetResult. Bounded-wait for the sink to complete (the analog of
// blockOn's Monitor.Wait drain) so the assertion is race-free — the asserted values are unchanged.
fun corAAwiDrain(sink: CorAAwiSink<Int>) {
    var tries = 0
    while (!sink.done && tries < 3000) { Thread.Sleep(1); tries += 1 }
}

class CoroutineContextInterceptionTests {
    @TestAttribute
    fun abstractElementCompanionKey() {
        val e = CorACtxkElem()
        assertEquals(true, e.key === CorACtxkElem.Key)   // True
        assertEquals(true, e[CorACtxkElem.Key] === e)    // True (get<E : Element>)
    }

    @TestAttribute
    fun interceptorKeyProjection() {
        val i = CorAIceptInterceptor()
        assertEquals(true, i.key === ContinuationInterceptor)   // True
        assertEquals(true, (i as CoroutineContext)[ContinuationInterceptor] === i)
    }

    @TestAttribute
    fun transitiveReferencedElementDefaults() {
        val e: CorATransitiveElement = CorATransitiveElementImpl()
        assertEquals(true, e[CorATransitiveElementImpl.Key] === e)
        assertEquals(e, e.fold<Any?>(null) { _, element -> element })
        assertEquals(EmptyCoroutineContext, e.minusKey(CorATransitiveElementImpl.Key))

        assertEquals(true, corATransitiveGet(e, CorATransitiveElementImpl.Key) === e)
    }

    @TestAttribute
    fun interceptorPrecedence() {
        val icept = CorAAwiCountingInterceptor()
        val sink = CorAAwiSink<Int>(icept)
        val tcs = TaskCompletionSource1<Int>()
        val block: suspend () -> Int = { corAAwiAwaitCapturing(tcs) }
        block.startCoroutine(sink)   // runs to the await, which SUSPENDS (tcs not completed)
        icept.resumes = 0            // reset: isolate the await-point resume from the start resume
        tcs.SetResult(42)            // resume -> OnCompleted -> this.intercepted() -> wrapper (resumes++)
        corAAwiDrain(sink)
        assertEquals(1, icept.resumes)   // A:resumes=1
        assertEquals(true, sink.done)    // done=True
        assertEquals(42, sink.value)     // value=42
    }

    @TestAttribute
    fun defaultCapturingPath() {
        val sink = CorAAwiSink<Int>(EmptyCoroutineContext)
        val tcs = TaskCompletionSource1<Int>()
        val block: suspend () -> Int = { corAAwiAwaitCapturing(tcs) + 5 }
        block.startCoroutine(sink)
        tcs.SetResult(7)
        corAAwiDrain(sink)
        assertEquals(true, sink.done)   // B:done=True
        assertEquals(12, sink.value)    // value=12
    }

    @TestAttribute
    fun configureAwaitFalsePath() {
        val sink = CorAAwiSink<Int>(EmptyCoroutineContext)
        val tcs = TaskCompletionSource1<Int>()
        val block: suspend () -> Int = { corAAwiAwaitNoCapture(tcs) + 2 }
        block.startCoroutine(sink)
        tcs.SetResult(9)
        corAAwiDrain(sink)
        assertEquals(true, sink.done)   // C:done=True
        assertEquals(11, sink.value)    // value=11
    }
}
