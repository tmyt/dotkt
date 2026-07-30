// RESULT-TYPE PRECEDENCE ACROSS A SUSPENSION — the acceptance battery for the ONE stamp order the toolchain now
// states in bir-common/NodeType.cs: `sty` (the frontend's INSTANTIATED static type, stamped per call site) before
// `ret` (emitted only for a generic callee/owner, where it may name the UNinstantiated declared type) before
// `dynRet`. Every slot the suspend lowering declares — a state-machine field, a MoveNext local, a resumed value —
// is typed through that order, so a wrong order (or a slot with NO derivable type at all, which used to become
// `kotlin.Any`) shows up as a boxed value that the CLR then has to unbox at every read.
//
// What each section pins:
//
//   1. A SAFE CALL WHOSE RESULT SLOT USED TO BE `kotlin.Any`. `b?.susp()` is a `cond` that kotc stamps NO type on
//      (the type lives on the two arms — a `nullableWrap`/`nullableNull` pair for a value type). The suspend
//      lowering carries a conditional's value in a temporary, and typed that temporary `kotlin.Any` whenever the
//      stamp was absent — so every value-type safe call across a suspension went through a box and an unbox, and a
//      nullable value type lost the distinction between "absent" and "boxed zero". The temporary is now derived
//      from the LIVE arm, and a conditional whose arms cannot be typed either is a REFUSAL rather than a box
//      (witnessed by tests/ir/lowering/reject-untyped-cond-slot.bir.json — no Kotlin source can produce it).
//
//   2. THE SAME SHAPE NESTED IN AN OPERAND LIST, so the value crosses a suspension in a plan binding rather than
//      in the conditional's own temporary — the two type their slots through the same deriver and must agree.
//
//   3. A GENERIC-OWNER SUSPEND CALL, where `sty` and `ret` genuinely DISAGREE: the callee declares `T` and the
//      call site resolves it to `Int`. Reading `ret` first typed the resumed value by the DECLARATION, so the
//      awaited slot held the erased type parameter; `sty` types it by the USE. Asserted through arithmetic on the
//      resumed value, which is what an erased slot cannot do without a boxed round trip.
//
//   4. A SUSPEND FUNCTIONAL VALUE reached through a `?.` chain — the receiver's suspend-`fn` type is recognized
//      through the same shared deriver rather than through a private restatement of the stamp order.
//
// Top-level names are family-prefixed (`corRt`) — the cold-core lowering keys top-level suspend funs by simple
// name, so they must be unique across this assembly.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsNull as assertNull
import System.Threading.Tasks.Task
import dotkt.support.blockOn

// A real suspension on every path — a defect that only shows once the outer call genuinely returns
// COROUTINE_SUSPENDED is invisible without one.
suspend fun corRtTick(n: Int): Int {
    Task.Delay(1).await()
    return n + 1
}

// ---- 1. a safe call whose result slot used to be kotlin.Any ---------------------------------------------------
class CorRtBox(val n: Int)

suspend fun CorRtBox.corRtNext(): Int = corRtTick(n)

// `b?.corRtNext()` — a `cond` with no type stamp, carrying a VALUE type (`Int?`) across a suspension.
suspend fun corRtSafeInt(b: CorRtBox?): Int? = b?.corRtNext()

// The same through an elvis, so the conditional's value is CONSUMED as a bare Int rather than stored.
suspend fun corRtSafeOrElse(b: CorRtBox?): Int = b?.corRtNext() ?: -7

// A reference-typed result, for the sibling arm of the same desugar.
suspend fun CorRtBox.corRtName(): String {
    Task.Delay(1).await()
    return "box" + n
}

suspend fun corRtSafeString(b: CorRtBox?): String? = b?.corRtName()

// ---- 2. the same shape inside another call's operand list -----------------------------------------------------
fun corRtSum(a: Int, b: Int): Int = a + b

// The safe call's value is bound by the enclosing node's evaluation plan and materialised ahead of the second
// operand's suspension, so its slot is typed by the plan rather than by the conditional's own temporary.
suspend fun corRtSafeInOperands(b: CorRtBox?): Int = corRtSum(b?.corRtNext() ?: 0, corRtTick(100))

// ---- 3. a generic-owner suspend call: `sty` and `ret` disagree ------------------------------------------------
//
// The suspension in these two is a suspend CALL, not a `.await()` marker, and deliberately so: a `.await()` inside
// a GENERIC cold state machine binds its completion `Action` to a method on the still-open `…$sm`1` type, which
// ilverify rejects (`DelegateCtor`: unrecognized arguments for delegate .ctor) and which faults at runtime with
// "the method itself or the containing type is not fully instantiated". That gap is INDEPENDENT of result typing —
// it reproduces identically with the previous bir2cir — and no existing fixture reached it; typing a generic call's
// resumed value is what this section is for, so it uses the shape that isolates it.
class CorRtCell<T>(val value: T) {
    // The declared return is the type parameter `T`; the call site below resolves it to `Int`, so the call node
    // carries `ret` = `T` and `sty` = `Int`.
    suspend fun unwrap(): T {
        corRtTick(0)
        return value
    }
}

suspend fun corRtGenericOwner(): Int {
    val cell = CorRtCell(41)
    return cell.unwrap() + 1
}

// A generic suspend FUNCTION (rather than a generic owner), whose `ret` names its own type parameter.
suspend fun <T> corRtIdentity(v: T): T {
    corRtTick(0)
    return v
}

suspend fun corRtGenericCallee(): Int = corRtIdentity(20) + corRtIdentity(22)

// ---- 4. a suspend functional value behind a `?.` --------------------------------------------------------------
class CorRtHolder(val f: suspend (Int) -> Int)

suspend fun corRtValueThroughSafeCall(h: CorRtHolder?): Int = h?.f?.invoke(5) ?: -1

class SuspendResultTypePrecedenceTests {
    @TestAttribute
    fun safeCallValueTypeResultIsNotBoxed() {
        assertEquals(8, blockOn { corRtSafeInt(CorRtBox(7)) })
        assertNull(blockOn { corRtSafeInt(null) })
    }

    @TestAttribute
    fun safeCallThroughElvis() {
        assertEquals(4, blockOn { corRtSafeOrElse(CorRtBox(3)) })
        assertEquals(-7, blockOn { corRtSafeOrElse(null) })
    }

    @TestAttribute
    fun safeCallReferenceTypeResult() {
        assertEquals("box5", blockOn { corRtSafeString(CorRtBox(5)) })
        assertNull(blockOn { corRtSafeString(null) })
    }

    @TestAttribute
    fun safeCallInsideOperandList() {
        assertEquals(104, blockOn { corRtSafeInOperands(CorRtBox(2)) })   // (2+1) + (100+1)
        assertEquals(101, blockOn { corRtSafeInOperands(null) })          //     0 + (100+1)
    }

    @TestAttribute
    fun genericOwnerCallResumesInstantiatedType() {
        assertEquals(42, blockOn { corRtGenericOwner() })
    }

    @TestAttribute
    fun genericCalleeResumesInstantiatedType() {
        assertEquals(42, blockOn { corRtGenericCallee() })
    }

    @TestAttribute
    fun suspendFunctionValueBehindSafeCall() {
        assertEquals(6, blockOn { corRtValueThroughSafeCall(CorRtHolder { v -> corRtTick(v) }) })
        assertEquals(-1, blockOn { corRtValueThroughSafeCall(null) })
    }
}
