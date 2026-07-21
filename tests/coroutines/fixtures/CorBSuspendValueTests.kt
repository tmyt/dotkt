// CorB batch — the suspend functional-VALUE / callable-reference / suspend-lambda / interface-member family.
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
// Top-level names carry a per-case token (`sv2`/`sref`/`lam2`/`ifc`) under the shared `corB`/`CorB` prefix so
// they can't clash with sibling coroutine fixtures or the stdlib within this single assembly. (lam1 has no
// top-level decls.)
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

// ---- il-suspendval2 ------------------------------------------------------------------------------------------
suspend fun corBSv2AddA(a: Int, b: Int): Int = a + b

suspend fun corBSv2Run2(b: suspend (Int, Int) -> Int, x: Int, y: Int): Int = b(x, y)   // invoke an arity-2 PARAM value

suspend fun corBSv2Run3(b: suspend (Int, Int, Int) -> Int): Int = b(10, 20, 12)        // arity-3 value invoke

suspend fun corBSv2Local2(): Int {                                                     // an arity-2 value in a LOCAL
    val f: suspend (Int, Int) -> Int = { p, q -> corBSv2AddA(p, q) }
    return f(30, 12)
}

// ---- il-suspendref -------------------------------------------------------------------------------------------
suspend fun corBSrefWork(x: Int): Int = x + 1

class CorBSrefDoubler(val base: Int) {
    suspend fun apply(x: Int): Int = base * x
}

fun corBSrefRunRef(f: suspend (Int) -> Int, arg: Int): Int = blockOn { f(arg) }

// ---- il-lam2 -------------------------------------------------------------------------------------------------
suspend fun corBLam2H(): Int = 5

// ---- il-ifacesuspend -----------------------------------------------------------------------------------------
interface CorBIfcFetcher { suspend fun fetch(): Int }
class CorBIfcFetcher42(val n: Int) : CorBIfcFetcher { override suspend fun fetch(): Int = n + 1 }

class CorBSuspendValueTests {
    @TestAttribute
    fun suspendval2_storedSuspendValueArityN() {
        assertEquals(42, blockOn { corBSv2Run2({ p, q -> corBSv2AddA(p, q) }, 37, 5) })   // 42
        assertEquals(42, blockOn { corBSv2Local2() })                                     // 42
        val base = 0
        assertEquals(42, blockOn { corBSv2Run3 { a, b, c -> corBSv2AddA(a, b) + c + base } }) // 42 (captures base)
    }

    @TestAttribute
    fun suspendref_callableReferenceToSuspendFn() {
        assertEquals(6, corBSrefRunRef(::corBSrefWork, 5))   // 6   (top-level suspend fn ref)
        val d = CorBSrefDoubler(10)
        assertEquals(40, corBSrefRunRef(d::apply, 4))        // 40  (bound member suspend fn ref, receiver captured)
    }

    @TestAttribute
    fun lam1_bareSuspendLambda() {
        assertEquals(42, blockOn { 42 })   // 42
    }

    @TestAttribute
    fun lam2_capturingSuspendLambda() {
        val n = 10
        assertEquals(15, blockOn { corBLam2H() + n })   // 15
    }

    @TestAttribute
    fun ifacesuspend_virtualInterfaceSuspendMember() {
        val f: CorBIfcFetcher = CorBIfcFetcher42(41)
        assertEquals(42, blockOn { f.fetch() })   // 42 — virtual dispatch through the interface cold entry
    }
}
