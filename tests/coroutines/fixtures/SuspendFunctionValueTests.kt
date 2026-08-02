// feature fixture — the suspend functional-VALUE / callable-reference / suspend-lambda / interface-member family.
// Invoking a stored `suspend (...) -> R` value (arity >= 2, the general N-arg cold protocol), a callable
// reference to a suspend fn, a bare/capturing suspend lambda, and a virtual suspend interface member. Driven by
// the shared `dotkt.support.blockOn` harness; each former case's `main` + stdout-golden becomes one @TestAttribute
// method preserving every value 1:1 (`// <expected>`).
//
// Coverage preserved (old case -> method):
//   il-suspendval2  -> suspendval2_storedSuspendValueArityN   (#38: N-arg startSuspendUninterceptedOrReturnN)
//   il-suspendref   -> suspendref_callableReferenceToSuspendFn (#67: ::work / d::apply -> newSuspendLambda adapter)
//   il-lam1         -> lam1_bareSuspendLambda                  (blockOn { 42 } -> SuspendLambda SM)
//   il-lam2         -> lam2_capturingSuspendLambda             (capture + real suspend call)
//   il-ifacesuspend -> ifacesuspend_virtualInterfaceSuspendMember (interface `suspend fun` cold-entry dispatch)
//
// Top-level names distinguish stored values, callable references, capturing lambdas, and interface members under
// descriptive `suspendFunctionValue`/`SuspendFunctionValue` stems. (The bare lambda case has no
// top-level decls.)
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Threading.Tasks.Task
import dotkt.support.blockOn

// ---- il-suspendval2 ------------------------------------------------------------------------------------------
suspend fun suspendFunctionValueStoredAdd(a: Int, b: Int): Int = a + b

suspend fun suspendFunctionValueStoredInvokeTwoArguments(b: suspend (Int, Int) -> Int, x: Int, y: Int): Int = b(x, y)   // invoke an arity-2 PARAM value

suspend fun suspendFunctionValueStoredInvokeThreeArguments(b: suspend (Int, Int, Int) -> Int): Int = b(10, 20, 12)        // arity-3 value invoke

suspend fun suspendFunctionValueStoredLocalTwoArguments(): Int {                                                     // an arity-2 value in a LOCAL
    val f: suspend (Int, Int) -> Int = { p, q -> suspendFunctionValueStoredAdd(p, q) }
    return f(30, 12)
}

// ---- il-suspendref -------------------------------------------------------------------------------------------
suspend fun suspendFunctionValueReferenceWork(x: Int): Int = x + 1

class SuspendFunctionValueReferenceDoubler(val base: Int) {
    suspend fun apply(x: Int): Int = base * x
}

suspend fun String.suspendFunctionValueReferenceAddLength(x: Int): Int = length + x

fun suspendFunctionValueReferenceRunRef(f: suspend (Int) -> Int, arg: Int): Int = blockOn { f(arg) }

fun suspendFunctionValueReferenceRunExtRef(f: suspend (String, Int) -> Int, recv: String, arg: Int): Int =
    blockOn { f(recv, arg) }

// ---- il-lam2 -------------------------------------------------------------------------------------------------
suspend fun suspendFunctionValueCapturingLambdaH(): Int = 5

suspend fun suspendFunctionValueLocalSuspendHost(base: Int): Int {
    suspend fun addAfterResume(delta: Int): Int {
        Task.Delay(10).await()
        return base + delta
    }
    return addAfterResume(2)
}

class SuspendFunctionValueReceiverFactory(private val delta: Int) {
    fun make(): suspend Int.() -> Int = {
        Task.Delay(10).await()
        this + delta
    }
}

// ---- il-ifacesuspend -----------------------------------------------------------------------------------------
interface SuspendFunctionValueInterfaceFetcher { suspend fun fetch(): Int }
class SuspendFunctionValueInterfaceFetcher42(val n: Int) : SuspendFunctionValueInterfaceFetcher { override suspend fun fetch(): Int = n + 1 }

class SuspendFunctionValueTests {
    @TestAttribute
    fun storedSuspendValueArityN() {
        assertEquals(42, blockOn { suspendFunctionValueStoredInvokeTwoArguments({ p, q -> suspendFunctionValueStoredAdd(p, q) }, 37, 5) })   // 42
        assertEquals(42, blockOn { suspendFunctionValueStoredLocalTwoArguments() })                                     // 42
        val base = 0
        assertEquals(42, blockOn { suspendFunctionValueStoredInvokeThreeArguments { a, b, c -> suspendFunctionValueStoredAdd(a, b) + c + base } }) // 42 (captures base)
    }

    @TestAttribute
    fun callableReferenceToSuspendFn() {
        assertEquals(6, suspendFunctionValueReferenceRunRef(::suspendFunctionValueReferenceWork, 5))   // 6   (top-level suspend fn ref)
        val d = SuspendFunctionValueReferenceDoubler(10)
        assertEquals(40, suspendFunctionValueReferenceRunRef(d::apply, 4))        // 40  (bound member suspend fn ref, receiver captured)

        // #67 residual: extension receivers use the same newSuspendLambda adapter as dispatch receivers.
        assertEquals(42, suspendFunctionValueReferenceRunExtRef(String::suspendFunctionValueReferenceAddLength, "abcd", 38))
        var receiverReads = 0
        fun receiver(): String {
            receiverReads += 1
            return "abc"
        }
        val bound: suspend (Int) -> Int = receiver()::suspendFunctionValueReferenceAddLength
        assertEquals(42, suspendFunctionValueReferenceRunRef(bound, 39))
        assertEquals(1, receiverReads)                        // a bound extension receiver is captured exactly once
    }

    @TestAttribute
    fun storedSuspendReferenceInvokesDirectly() {
        val r = ::suspendFunctionValueReferenceWork
        assertEquals(42, blockOn { r(41) })
    }

    @TestAttribute
    fun capturingSuspendLambda() {
        val n = 10
        assertEquals(15, blockOn { suspendFunctionValueCapturingLambdaH() + n })   // 15
    }

    @TestAttribute
    fun liftedLocalSuspendFunction() {
        assertEquals(42, blockOn { suspendFunctionValueLocalSuspendHost(40) })
    }

    @TestAttribute
    fun receiverSuspendValueResumesAsynchronously() {
        val f: suspend Int.() -> Int = {
            Task.Delay(10).await()
            this + 1
        }
        assertEquals(42, blockOn(41, f))
        // The lambda has both an extension receiver (`this`) and a captured dispatch receiver (`delta`).
        // They must remain distinct after the lambda becomes a state machine.
        assertEquals(42, blockOn(40, SuspendFunctionValueReceiverFactory(2).make()))
    }

    @TestAttribute
    fun virtualInterfaceSuspendMember() {
        val f: SuspendFunctionValueInterfaceFetcher = SuspendFunctionValueInterfaceFetcher42(41)
        assertEquals(42, blockOn { f.fetch() })   // 42 — virtual dispatch through the interface cold entry
    }
}
