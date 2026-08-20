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
// Top-level names are family-prefixed (`suspendResultType`) — the cold-core lowering keys top-level suspend funs by simple
// name, so they must be unique across this assembly.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsNull as assertNull
import System.Activator
import System.Math
import System.Numerics.Vector3
import System.Text.StringBuilder
import System.Threading.Tasks.Task
import dotkt.support.blockOn

// A real suspension on every path — a defect that only shows once the outer call genuinely returns
// COROUTINE_SUSPENDED is invisible without one.
suspend fun suspendResultTypeTick(n: Int): Int {
    Task.Delay(1).await()
    return n + 1
}

// ---- 1. a safe call whose result slot used to be kotlin.Any ---------------------------------------------------
class SuspendResultTypeBox(val n: Int)

suspend fun SuspendResultTypeBox.suspendResultTypeNext(): Int = suspendResultTypeTick(n)

// `b?.suspendResultTypeNext()` — a `cond` with no type stamp, carrying a VALUE type (`Int?`) across a suspension.
suspend fun suspendResultTypeSafeInt(b: SuspendResultTypeBox?): Int? = b?.suspendResultTypeNext()

// The same through an elvis, so the conditional's value is CONSUMED as a bare Int rather than stored.
suspend fun suspendResultTypeSafeOrElse(b: SuspendResultTypeBox?): Int = b?.suspendResultTypeNext() ?: -7

// A reference-typed result, for the sibling arm of the same desugar.
suspend fun SuspendResultTypeBox.suspendResultTypeName(): String {
    Task.Delay(1).await()
    return "box" + n
}

suspend fun suspendResultTypeSafeString(b: SuspendResultTypeBox?): String? = b?.suspendResultTypeName()

// ---- 2. the same shape inside another call's operand list -----------------------------------------------------
fun suspendResultTypeSum(a: Int, b: Int): Int = a + b

// The safe call's value is bound by the enclosing node's evaluation plan and materialised ahead of the second
// operand's suspension, so its slot is typed by the plan rather than by the conditional's own temporary.
suspend fun suspendResultTypeSafeInOperands(b: SuspendResultTypeBox?): Int = suspendResultTypeSum(b?.suspendResultTypeNext() ?: 0, suspendResultTypeTick(100))

// ---- 3. a generic-owner suspend call: `sty` and `ret` disagree ------------------------------------------------
//
// The suspension in these two is a suspend CALL, not a `.await()` marker, because typing a generic call's resumed
// value is the behavior this section isolates. Generic await callback construction is covered independently by
// DynamicCaptureContextTests (#303).
class SuspendResultTypeCell<T>(val value: T) {
    // The declared return is the type parameter `T`; the call site below resolves it to `Int`, so the call node
    // carries `ret` = `T` and `sty` = `Int`.
    suspend fun unwrap(): T {
        suspendResultTypeTick(0)
        return value
    }
}

suspend fun suspendResultTypeGenericOwner(): Int {
    val cell = SuspendResultTypeCell(41)
    return cell.unwrap() + 1
}

// A generic suspend FUNCTION (rather than a generic owner), whose `ret` names its own type parameter.
suspend fun <T> suspendResultTypeIdentity(v: T): T {
    suspendResultTypeTick(0)
    return v
}

suspend fun suspendResultTypeGenericCallee(): Int = suspendResultTypeIdentity(20) + suspendResultTypeIdentity(22)

// ---- 4. a suspend functional value behind a `?.` --------------------------------------------------------------
class SuspendResultTypeHolder(val f: suspend (Int) -> Int)

suspend fun suspendResultTypeValueThroughSafeCall(h: SuspendResultTypeHolder?): Int = h?.f?.invoke(5) ?: -1

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
fun <T> suspendResultTypeNullBoxes(x: T): List<T?> = listOf(x, null)

// Three consumers, one per way the erased value can be RECEIVED, because carrier-argument erasure (#86) gives all
// three the same physical parameter: `List<Any?>`, the directly-written substituted `List<Int?>` and a generic
// `List<A?>` are each an `IReadOnlyList<object>`. The last two were deliberately kept out while `List<Int?>` was an
// `IReadOnlyList<Nullable<int32>>` — the erased value did not inhabit either slot, and the generic one additionally
// went through an `Enumerable.Cast<object>` wrap whose `IEnumerable<object>` result inhabited neither. Both are in
// now, and each is driven through BOTH suspension compositions below.
fun suspendResultTypeCountNulls(l: List<Any?>, extra: Int): Int {
    var n = 0
    for (v in l) if (v == null) n++
    return n + extra
}

fun suspendResultTypeCountNullsTyped(l: List<Int?>, extra: Int): Int {
    var n = 0
    for (v in l) if (v == null) n++
    return n + extra
}

fun <A> suspendResultTypeCountNullsGeneric(l: List<A?>, extra: Int): Int {
    var n = 0
    for (v in l) if (v == null) n++
    return n + extra
}

suspend fun suspendResultTypeErasedLeftOfSuspension(): Int = suspendResultTypeCountNulls(suspendResultTypeNullBoxes(7), suspendResultTypeTick(0))

suspend fun suspendResultTypeErasedLeftOfSuspensionTyped(): Int = suspendResultTypeCountNullsTyped(suspendResultTypeNullBoxes(7), suspendResultTypeTick(0))

suspend fun suspendResultTypeErasedLeftOfSuspensionGeneric(): Int = suspendResultTypeCountNullsGeneric(suspendResultTypeNullBoxes(7), suspendResultTypeTick(0))

suspend fun <T> suspendResultTypeNullBoxesSuspend(x: T): List<T?> {
    suspendResultTypeTick(0)
    return listOf(x, null)
}

suspend fun suspendResultTypeErasedAwaited(): Int {
    val l = suspendResultTypeNullBoxesSuspend(7)
    var n = 0
    for (v in l) if (v == null) n++
    return n
}

// The awaited value carried across the suspension as a DECLARED `List<Int?>` local, then handed to each consumer:
// the state-machine field, the local's slot and the parameter are one physical type or none of them are.
suspend fun suspendResultTypeErasedAwaitedTyped(): Int {
    val l: List<Int?> = suspendResultTypeNullBoxesSuspend(7)
    return suspendResultTypeCountNullsTyped(l, suspendResultTypeTick(0)) + suspendResultTypeCountNullsGeneric(l, 0)
}

// ---- 6. the suspend `fun interface` SAM shim's result type ----------------------------------------------------
//
// The shim kotc lifts for a suspend `fun interface` lambda carries `mods.suspend`; `suspendRet` rides alongside
// it, and bir2cir's cold registry reads THAT slot. A shim with the modifier but no slot had its awaited values
// typed `kotlin.Any` — boxed into the state machine and unboxed out of it. These pin the two shapes whose result
// type is least likely to survive by accident: a GENERIC interface with a NON-Unit result (so the slot is a type
// parameter's instantiation rather than `Unit`), and a suspend member with an EXTENSION receiver (so the shim's
// parameter list is shifted by the receiver).
fun interface SuspendResultTypeSuspendMapper<T, R> {
    suspend fun map(v: T): R
}

fun interface SuspendResultTypeSuspendOnInt {
    suspend fun Int.transform(): Int
}

suspend fun <T, R> suspendResultTypeApplyMapper(v: T, m: SuspendResultTypeSuspendMapper<T, R>): R = m.map(v)

suspend fun suspendResultTypeApplyOnInt(v: Int, f: SuspendResultTypeSuspendOnInt): Int = with(f) { v.transform() }

// ---- 7. a `.NET`-bound operand's stamp surviving the `clr*` reshape (#304) -------------------------------------
//
// bir2cir's NetInteropBinding takes the plain call/read kotc emits by the .NET owner's IDENTITY and reshapes it
// into the CLR vocabulary — `clrStatic`/`clrGenericStatic` for a call on a referenced owner, `clrPropGet` for a
// `.NET` FIELD read. The reshape changes the node's SHAPE and not what it produces, so its result-type stamps stay
// true and must travel with it: `bir-common/NodeType.cs` has no derivation arm for ANY `clr*` kind, which makes
// those stamps the whole of the reshaped node's static type. Where a reshape dropped them, the node became
// untypable — and an operand with no static type standing LEFT of a suspension is a stage-0 REFUSAL
// (SuspendOperandPlan: "carries no static type"), so `v.X + susp()` was a compile-time rejection of source the
// frontend accepts, while the same expression without the suspension compiled and ran.
//
// Each shape below is therefore paired with its NON-suspend twin asserting the identical value: the twin is what
// says the refusal was about the suspension and not about the expression. The suspending operand is the SECOND one,
// so the operand plan really does have to spill the first into a typed local across the suspension.
//
//   A `.NET` FIELD read (`Vector3.X` -> ReshapeField -> `clrPropGet`) — the reshape that dropped the stamp
//   outright, and the shape this fixture is RED on without the fix.
//   A `.NET` static call (`Math.Max` -> `clrStatic`) and a GENERIC one (`Activator.CreateInstance<T>()` ->
//   `clrGenericStatic`) — the kinds the issue names. Both already carried a `sty` the reshape forwarded, so they
//   pin the shape rather than reproduce the drop.
//
// The owner has to be a GENUINE .NET type. A referenced DotKt assembly is deliberately NOT one: NetInteropBinding
// leaves a Kotlin-emitted owner on the Kotlin ABI path (only the enum and Comparable seams are admitted), so a
// cross-module Kotlin callee stays a plain `callStatic` and never reaches the reshape at all — it would look like
// coverage while asserting nothing about it.
//
// The `ret` half of the carry has no Kotlin witness in either direction (kotc's generic .NET-call emitter writes
// only `sty`, and its `field` emitter writes `ret` only for a generic owner), so it is pinned structurally instead:
// tests/ir/lowering/net-interop-reshape-result-stamp.
//
// The consumers take the operand's OWN type, so the reshaped node is the operand ITSELF: a conversion around it
// (`v.X.toInt()`) would be a `conv`, which the deriver types from its own `to` slot whatever its operand is, and the
// composition would no longer reach the drop at all.
fun suspendResultTypeJoinF(a: Float, b: Int): String = "" + a.toInt() + "," + b
fun suspendResultTypeJoinI(a: Int, b: Int): String = "" + a + "," + b
fun suspendResultTypeJoinB(a: StringBuilder, b: Int): String = "" + a.Length + "," + b

suspend fun suspendResultTypeNetInstanceField(): String {
    val v = Vector3(4.0f, 2.0f, 3.0f)
    return suspendResultTypeJoinF(v.X, suspendResultTypeTick(1))
}

fun suspendResultTypeNetInstanceFieldPlain(): String {
    val v = Vector3(4.0f, 2.0f, 3.0f)
    return suspendResultTypeJoinF(v.X, 2)
}

suspend fun suspendResultTypeNetStaticCall(): String = suspendResultTypeJoinI(Math.Max(7, 1), suspendResultTypeTick(1))

fun suspendResultTypeNetStaticCallPlain(): String = suspendResultTypeJoinI(Math.Max(7, 1), 2)

suspend fun suspendResultTypeNetGenericCall(): String = suspendResultTypeJoinB(Activator.CreateInstance<StringBuilder>(), suspendResultTypeTick(1))

fun suspendResultTypeNetGenericCallPlain(): String = suspendResultTypeJoinB(Activator.CreateInstance<StringBuilder>(), 2)

class SuspendResultTypePrecedenceTests {
    @TestAttribute
    fun safeCallValueTypeResultIsNotBoxed() {
        assertEquals(8, blockOn { suspendResultTypeSafeInt(SuspendResultTypeBox(7)) })
        assertNull(blockOn { suspendResultTypeSafeInt(null) })
    }

    @TestAttribute
    fun safeCallThroughElvis() {
        assertEquals(4, blockOn { suspendResultTypeSafeOrElse(SuspendResultTypeBox(3)) })
        assertEquals(-7, blockOn { suspendResultTypeSafeOrElse(null) })
    }

    @TestAttribute
    fun safeCallReferenceTypeResult() {
        assertEquals("box5", blockOn { suspendResultTypeSafeString(SuspendResultTypeBox(5)) })
        assertNull(blockOn { suspendResultTypeSafeString(null) })
    }

    @TestAttribute
    fun safeCallInsideOperandList() {
        assertEquals(104, blockOn { suspendResultTypeSafeInOperands(SuspendResultTypeBox(2)) })   // (2+1) + (100+1)
        assertEquals(101, blockOn { suspendResultTypeSafeInOperands(null) })          //     0 + (100+1)
    }

    @TestAttribute
    fun genericOwnerCallResumesInstantiatedType() {
        assertEquals(42, blockOn { suspendResultTypeGenericOwner() })
    }

    @TestAttribute
    fun genericCalleeResumesInstantiatedType() {
        assertEquals(42, blockOn { suspendResultTypeGenericCallee() })
    }

    @TestAttribute
    fun suspendFunctionValueBehindSafeCall() {
        assertEquals(6, blockOn { suspendResultTypeValueThroughSafeCall(SuspendResultTypeHolder { v -> suspendResultTypeTick(v) }) })
        assertEquals(-1, blockOn { suspendResultTypeValueThroughSafeCall(null) })
    }

    @TestAttribute
    fun erasedNullableGenericReturnLeftOfSuspension() {
        assertEquals(2, blockOn { suspendResultTypeErasedLeftOfSuspension() })   // 1 null in [7, null] + tick(0) = 1
    }

    @TestAttribute
    fun erasedNullableGenericReturnIsTheSuspension() {
        assertEquals(1, blockOn { suspendResultTypeErasedAwaited() })            // 1 null in [7, null]
    }

    // #86 — the same two compositions received at a DIRECTLY-WRITTEN `List<Int?>` and at a GENERIC `List<A?>`.
    // Carrier-argument erasure makes all three consumer slots one `IReadOnlyList<object>`, so an erased value now
    // inhabits each of them; before it, neither of these compiled to a program that ran.
    @TestAttribute
    fun erasedNullableGenericReachesATypedAndAGenericConsumer() {
        assertEquals(2, blockOn { suspendResultTypeErasedLeftOfSuspensionTyped() })    // 1 null in [7, null] + tick(0) = 1
        assertEquals(2, blockOn { suspendResultTypeErasedLeftOfSuspensionGeneric() })  // same, through List<A?>
        assertEquals(3, blockOn { suspendResultTypeErasedAwaitedTyped() })             // (1 + 1) + 1 across the suspension
    }

    @TestAttribute
    fun suspendFunInterfaceGenericNonUnitResult() {
        assertEquals(15, blockOn { suspendResultTypeApplyMapper(7) { v -> suspendResultTypeTick(v) + 7 } })
        assertEquals("v8", blockOn { suspendResultTypeApplyMapper(7) { v -> "v" + suspendResultTypeTick(v) } })
    }

    @TestAttribute
    fun suspendFunInterfaceExtensionReceiver() {
        assertEquals(11, blockOn { suspendResultTypeApplyOnInt(10) { suspendResultTypeTick(this) } })
    }

    // ---- 7 ----
    // Each pair is the same expression with and without the suspension: without it the program always compiled,
    // with it the reshaped operand had no static type and bir2cir refused the compilation outright (#304).
    @TestAttribute
    fun netInstanceFieldLeftOfSuspension() {
        assertEquals("4,2", suspendResultTypeNetInstanceFieldPlain())
        assertEquals("4,2", blockOn { suspendResultTypeNetInstanceField() })
    }

    @TestAttribute
    fun netStaticCallLeftOfSuspension() {
        assertEquals("7,2", suspendResultTypeNetStaticCallPlain())
        assertEquals("7,2", blockOn { suspendResultTypeNetStaticCall() })
    }

    @TestAttribute
    fun netGenericCallLeftOfSuspension() {
        assertEquals("0,2", suspendResultTypeNetGenericCallPlain())
        assertEquals("0,2", blockOn { suspendResultTypeNetGenericCall() })
    }
}
