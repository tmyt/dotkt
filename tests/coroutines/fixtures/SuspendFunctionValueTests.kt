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
// Top-level names carry a per-case token (`sv2`/`sref`/`lam2`/`ifc`) under the shared `suspendFunctionValue`/`SuspendFunctionValue` prefix so
// they can't clash with sibling coroutine fixtures or the stdlib within this single assembly. (lam1 has no
// top-level decls.)
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Threading.Tasks.Task
import dotkt.support.blockOn

// ---- il-suspendval2 ------------------------------------------------------------------------------------------
suspend fun suspendFunctionValueSv2AddA(a: Int, b: Int): Int = a + b

suspend fun suspendFunctionValueSv2Run2(b: suspend (Int, Int) -> Int, x: Int, y: Int): Int = b(x, y)   // invoke an arity-2 PARAM value

suspend fun suspendFunctionValueSv2Run3(b: suspend (Int, Int, Int) -> Int): Int = b(10, 20, 12)        // arity-3 value invoke

suspend fun suspendFunctionValueSv2Local2(): Int {                                                     // an arity-2 value in a LOCAL
    val f: suspend (Int, Int) -> Int = { p, q -> suspendFunctionValueSv2AddA(p, q) }
    return f(30, 12)
}

// ---- il-suspendref -------------------------------------------------------------------------------------------
suspend fun suspendFunctionValueSrefWork(x: Int): Int = x + 1

class SuspendFunctionValueSrefDoubler(val base: Int) {
    suspend fun apply(x: Int): Int = base * x
}

suspend fun String.suspendFunctionValueSrefAddLength(x: Int): Int = length + x

fun suspendFunctionValueSrefRunRef(f: suspend (Int) -> Int, arg: Int): Int = blockOn { f(arg) }

fun suspendFunctionValueSrefRunExtRef(f: suspend (String, Int) -> Int, recv: String, arg: Int): Int =
    blockOn { f(recv, arg) }

// ---- il-lam2 -------------------------------------------------------------------------------------------------
suspend fun suspendFunctionValueLam2H(): Int = 5

suspend fun suspendFunctionValueLocalSuspendHost(base: Int): Int {
    suspend fun addAfterResume(delta: Int): Int {
        Task.Delay(10).await()
        return base + delta
    }
    return addAfterResume(2)
}

class SuspendFunctionValueSvReceiverFactory(private val delta: Int) {
    fun make(): suspend Int.() -> Int = {
        Task.Delay(10).await()
        this + delta
    }
}

// ---- il-ifacesuspend -----------------------------------------------------------------------------------------
interface SuspendFunctionValueIfcFetcher { suspend fun fetch(): Int }
class SuspendFunctionValueIfcFetcher42(val n: Int) : SuspendFunctionValueIfcFetcher { override suspend fun fetch(): Int = n + 1 }

class SuspendFunctionValueTests {
    @TestAttribute
    fun storedSuspendValueArityN() {
        assertEquals(42, blockOn { suspendFunctionValueSv2Run2({ p, q -> suspendFunctionValueSv2AddA(p, q) }, 37, 5) })   // 42
        assertEquals(42, blockOn { suspendFunctionValueSv2Local2() })                                     // 42
        val base = 0
        assertEquals(42, blockOn { suspendFunctionValueSv2Run3 { a, b, c -> suspendFunctionValueSv2AddA(a, b) + c + base } }) // 42 (captures base)
    }

    @TestAttribute
    fun callableReferenceToSuspendFn() {
        assertEquals(6, suspendFunctionValueSrefRunRef(::suspendFunctionValueSrefWork, 5))   // 6   (top-level suspend fn ref)
        val d = SuspendFunctionValueSrefDoubler(10)
        assertEquals(40, suspendFunctionValueSrefRunRef(d::apply, 4))        // 40  (bound member suspend fn ref, receiver captured)

        // #67 residual: extension receivers use the same newSuspendLambda adapter as dispatch receivers.
        assertEquals(42, suspendFunctionValueSrefRunExtRef(String::suspendFunctionValueSrefAddLength, "abcd", 38))
        var receiverReads = 0
        fun receiver(): String {
            receiverReads += 1
            return "abc"
        }
        val bound: suspend (Int) -> Int = receiver()::suspendFunctionValueSrefAddLength
        assertEquals(42, suspendFunctionValueSrefRunRef(bound, 39))
        assertEquals(1, receiverReads)                        // a bound extension receiver is captured exactly once
    }

    @TestAttribute
    fun storedSuspendReferenceInvokesDirectly() {
        val r = ::suspendFunctionValueSrefWork
        assertEquals(42, blockOn { r(41) })
    }

    @TestAttribute
    fun capturingSuspendLambda() {
        val n = 10
        assertEquals(15, blockOn { suspendFunctionValueLam2H() + n })   // 15
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
        assertEquals(42, blockOn(40, SuspendFunctionValueSvReceiverFactory(2).make()))
    }

    @TestAttribute
    fun virtualInterfaceSuspendMember() {
        val f: SuspendFunctionValueIfcFetcher = SuspendFunctionValueIfcFetcher42(41)
        assertEquals(42, blockOn { f.fetch() })   // 42 — virtual dispatch through the interface cold entry
    }
}
