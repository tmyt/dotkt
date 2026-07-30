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

// ---- 5. an object-ERASED nullable-generic return crossing a suspension ----------------------------------------
//
// `fun <T> f(x: T): List<T?>` has its `Nullable(T)` erased to `object` on the DECLARATION side — `object` is the
// only uniform CLR storage carrying a real null for both a reference and a value instantiation of an
// unconstrained `T` — so the emitted method returns `List<object>`. The CALL site is emitted with `T` already
// substituted, so kotc stamps `List<Nullable<Int>>`; NullableTvErasureCallRealign realigns the call's result to
// the erased form, and must realign the frontend `sty` stamp with it. The two are UNRELATED invariant reified
// generics (no cast reconciles them), so a slot declared from the pre-erasure stamp is invalid IL, not a
// diagnosable drop — and a SUSPENSION is what makes such a slot exist. Both compositions:
//
//   (a) the erased call is an operand evaluated LEFT of a suspending one, so stage 0 declares the plan's spill
//       local from the stamp;
//   (b) the erased call IS the suspension, so the awaited state-machine field is declared from it.
fun <T> corRtNullBoxes(x: T): List<T?> = listOf(x, null)

// The consumer's parameter is `List<Any?>`, whose CLR slot is the same `IReadOnlyList<object>` the erased return
// produces, so the argument position agrees with the value by construction and this fixture pins result TYPING and
// nothing else. Two neighbouring subjects are deliberately kept out: a parameter written as the substituted
// `List<Int?>` would hit the erasure family's documented STORE/pass-side gap (reconciling an erased value with a
// directly-written target), and a GENERIC consumer is reshaped to `clrGenericStatic`, whose stamps the reshape
// drops — stage 0 refuses that operand outright, at HEAD and at origin/main alike.
fun corRtCountNulls(l: List<Any?>, extra: Int): Int {
    var n = 0
    for (v in l) if (v == null) n++
    return n + extra
}

suspend fun corRtErasedLeftOfSuspension(): Int = corRtCountNulls(corRtNullBoxes(7), corRtTick(0))

suspend fun <T> corRtNullBoxesSuspend(x: T): List<T?> {
    corRtTick(0)
    return listOf(x, null)
}

suspend fun corRtErasedAwaited(): Int {
    val l = corRtNullBoxesSuspend(7)
    var n = 0
    for (v in l) if (v == null) n++
    return n
}

// ---- 6. the suspend `fun interface` SAM shim's result type ----------------------------------------------------
//
// The shim kotc lifts for a suspend `fun interface` lambda carries `mods.suspend`; `suspendRet` rides alongside
// it, and bir2cir's cold registry reads THAT slot. A shim with the modifier but no slot had its awaited values
// typed `kotlin.Any` — boxed into the state machine and unboxed out of it. These pin the two shapes whose result
// type is least likely to survive by accident: a GENERIC interface with a NON-Unit result (so the slot is a type
// parameter's instantiation rather than `Unit`), and a suspend member with an EXTENSION receiver (so the shim's
// parameter list is shifted by the receiver).
fun interface CorRtSuspendMapper<T, R> {
    suspend fun map(v: T): R
}

fun interface CorRtSuspendOnInt {
    suspend fun Int.transform(): Int
}

suspend fun <T, R> corRtApplyMapper(v: T, m: CorRtSuspendMapper<T, R>): R = m.map(v)

suspend fun corRtApplyOnInt(v: Int, f: CorRtSuspendOnInt): Int = with(f) { v.transform() }

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

    @TestAttribute
    fun erasedNullableGenericReturnLeftOfSuspension() {
        assertEquals(2, blockOn { corRtErasedLeftOfSuspension() })   // 1 null in [7, null] + tick(0) = 1
    }

    @TestAttribute
    fun erasedNullableGenericReturnIsTheSuspension() {
        assertEquals(1, blockOn { corRtErasedAwaited() })            // 1 null in [7, null]
    }

    @TestAttribute
    fun suspendFunInterfaceGenericNonUnitResult() {
        assertEquals(15, blockOn { corRtApplyMapper(7) { v -> corRtTick(v) + 7 } })
        assertEquals("v8", blockOn { corRtApplyMapper(7) { v -> "v" + corRtTick(v) } })
    }

    @TestAttribute
    fun suspendFunInterfaceExtensionReceiver() {
        assertEquals(11, blockOn { corRtApplyOnInt(10) { corRtTick(this) } })
    }
}
