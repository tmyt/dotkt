// OPERAND ORDER ACROSS A SUSPENSION — the acceptance battery for bir2cir's stage-0 operand plan
// (toolchain/bir2cir/SuspendOperandPlan.cs). Every shape here is a program the frontend accepts and Kotlin gives
// one unambiguous meaning; each one was wrong (or refused) until an operand carrying a suspension became a plan
// binding materialised ahead of the node that consumes it.
//
// What each section pins, and what it did before:
//
//   1. NESTED SUSPENSION IN A SUSPEND CALL'S OWN OPERANDS (#272) — `corAdd(x, corTick(1))` HUNG FOREVER. The
//      outer call wrote its resume label, the inner suspension then overwrote it, and the resume jumped back
//      into the inner state. Covered for every arm of the operand descriptor, because each is a different node
//      kind at the point the plan is made: same-module static and instance, a suspension in the RECEIVER rather
//      than an argument, a generic callee, and the four cross-module `clr*` forms — of which
//      `clrGenericInstance` was reachable only when the call WAS the suspension, never when it CONTAINED one.
//      The callees live in the referenced tests/support/coroutines assembly precisely to reach those forms.
//
//   2. THE SAME, THROUGH THE `.await()` MARKER — a .NET awaitable in a suspending call's argument list. The
//      marker is lowered by its own emitter (EmitAwaitPoint), so it takes a different path to the same label.
//
//   3. AN OPERAND THAT *CONTAINS* A SUSPENSION WITHOUT BEING ONE (#286) — `h(f()) + g()` traced F,G,H where
//      Kotlin requires F,H,G: the suspension's segments were appended and the residual `h(<awaited>)` was left
//      in the slot, so it ran after the NEXT operand's suspension. The siblings differ only in what the residual
//      is (a `toString`, a `length` read) and in which NODE consumes it — a `binOp` for the arithmetic form, a
//      `concat` for `wrap(f()) + g()` and for the string template that means the same thing, since every `+` with
//      a String on either side is a concatenation by the time the plan is made.
//
//   4. THE OPERANDS THE OLD PURITY PREDICATE NEVER COVERED — a .NET property read (`clrPropGet`), a delegate
//      invoke, an `Any`-slot method, an interface call on a type-parameter receiver. Each was judged "pure" and
//      left inline, so it observed the state the suspending callee had already mutated. They are covered now
//      because position, not kind, decides.
//
//   5. A TERMINAL OPERAND LEFT OF A SUSPENSION — `sum(run { throw … }, relay())` was a COMPILE-TIME REFUSAL
//      (tests/compile-fail/SuspendTerminalArgumentBeforeSuspension). The plan is made before any state machine
//      exists, so the unreachable remainder is simply dropped and the throw propagates, with nothing to the
//      right of it ever evaluated.
//
// Side effects are captured into `suspendOperandLog` and asserted POSITIONALLY — an order defect is only visible as an
// interleaving, and a value assertion alone would pass for several wrong orders. Every suspend callee that has
// to make the outer call actually return COROUTINE_SUSPENDED awaits a real `Task.Delay`; a defect that only
// shows on the genuine-suspension path is invisible without one.
//
// Top-level names are family-prefixed (`suspendOperand`) — the cold-core lowering keys top-level suspend funs by simple
// name, so they must be unique across this assembly.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import System.Text.StringBuilder
import System.Threading.Tasks.Task
import System.Threading.Tasks.TaskCompletionSource1
import dotkt.support.blockOn
import dotkt.support.SuspendOperandCrossModuleBox
import dotkt.support.suspendOperandCrossModuleAdd
import dotkt.support.suspendOperandCrossModuleFirst

val suspendOperandLog = mutableListOf<String>()

// ---- 1. a suspension inside a suspend call's own operand list (#272) -----------------------------------------
suspend fun suspendOperandTick(n: Int): Int {
    Task.Delay(1).await()
    suspendOperandLog.add("T")
    return n + 1
}

suspend fun suspendOperandAdd(a: Int, b: Int): Int {
    Task.Delay(1).await()
    suspendOperandLog.add("A")
    return a + b
}

// The issue's repro, verbatim.
suspend fun suspendOperandNestedInArgument(): Int {
    val x = 4
    return suspendOperandAdd(x, suspendOperandTick(1))
}

class SuspendOperandBox(val base: Int) {
    suspend fun add(a: Int, b: Int): Int {
        Task.Delay(1).await()
        suspendOperandLog.add("M")
        return base + a + b
    }
}

suspend fun suspendOperandMakeBox(): SuspendOperandBox {
    Task.Delay(1).await()
    suspendOperandLog.add("B")
    return SuspendOperandBox(10)
}

// The same nesting on an INSTANCE suspend member — a `callInstance` cold call, so the receiver joins the operands.
suspend fun suspendOperandNestedInMemberArgument(): Int = SuspendOperandBox(10).add(4, suspendOperandTick(1))

// The suspension in the RECEIVER operand of a suspend call, not in an argument.
suspend fun suspendOperandNestedInReceiver(): Int = suspendOperandMakeBox().add(4, 2)

// A same-module GENERIC suspend callee. It suspends through a non-generic cold call rather than a `.await()`
// MARKER: an await point inside a GENERIC suspend fun is separately broken today (its generic state machine builds
// the resume `Action` over a method it never instantiates, so the first suspension throws
// `InvalidOperationException: ... not fully instantiated`). That defect is reachable with no operand-order question
// anywhere in the program — `suspend fun <T> f(x: T): T { Task.Delay(1).await(); return x }` called as `f(7)` is
// enough — so it is a separate subject, and pinning it here would only hide what these fixtures do test.
suspend fun suspendOperandPause() {
    Task.Delay(1).await()
}

suspend fun <T> suspendOperandFirst(a: T, b: Int): T {
    suspendOperandPause()
    suspendOperandLog.add("P")
    return a
}

suspend fun suspendOperandNestedInGenericArgument(): Int = suspendOperandFirst(4, suspendOperandTick(1))

// The four CROSS-MODULE `clr*` forms: static, instance, generic-static, generic-instance.
suspend fun suspendOperandNestedCrossModuleStatic(): Int = suspendOperandCrossModuleAdd(4, suspendOperandTick(1))
suspend fun suspendOperandNestedCrossModuleInstance(): Int = SuspendOperandCrossModuleBox(10).add(4, suspendOperandTick(1))
suspend fun suspendOperandNestedCrossModuleGenericStatic(): Int = suspendOperandCrossModuleFirst(4, suspendOperandTick(1))
suspend fun suspendOperandNestedCrossModuleGenericInstance(): Int = SuspendOperandCrossModuleBox(10).first(4, suspendOperandTick(1))

// ---- 2. the same, reached through the `.await()` marker ------------------------------------------------------
suspend fun suspendOperandAwaitedInArgument(): Int {
    val tcs = TaskCompletionSource1<Int>()
    tcs.SetResult(3)
    return suspendOperandAdd(4, tcs.Task.await())
}

// ---- 3. an operand that CONTAINS a suspension without being one (#286) ---------------------------------------
suspend fun suspendOperandF(): Int {
    Task.Delay(1).await()
    suspendOperandLog.add("F")
    return 1
}

suspend fun suspendOperandG(): Int {
    Task.Delay(1).await()
    suspendOperandLog.add("G")
    return 2
}

fun suspendOperandH(x: Int): Int {
    suspendOperandLog.add("H")
    return x
}

fun suspendOperandWrap(x: Int): String {
    suspendOperandLog.add("W")
    return "w" + x
}

suspend fun suspendOperandContainedInLeftOperand(): Int = suspendOperandH(suspendOperandF()) + suspendOperandG()
suspend fun suspendOperandContainedThroughToString(): String = suspendOperandF().toString() + suspendOperandG()
suspend fun suspendOperandContainedThroughLength(): Int = suspendOperandWrap(suspendOperandF()).length + suspendOperandG()

// The same in a STRING CONCATENATION. By the time the plan is made, every `+` with a String on either side is a
// `concat` (PrimitiveOperatorLowering re-emits `kotlin.String.plus` as one) and so is a template, so these two are
// the same node kind — and it is a different arm of the operand descriptor from `binOp`.
suspend fun suspendOperandContainedThroughConcat(): String = suspendOperandWrap(suspendOperandF()) + suspendOperandG()
suspend fun suspendOperandContainedThroughTemplate(): String = "[${suspendOperandWrap(suspendOperandF())}:${suspendOperandG()}]"

// ---- 4. the operands the retired purity predicate never covered ----------------------------------------------
// A .NET property read (`clrPropGet`) left of a callee that mutates the same object.
suspend fun suspendOperandAppend(sb: StringBuilder): Int {
    sb.Append("xyz")
    return suspendOperandG()
}

suspend fun suspendOperandClrPropertyBeforeSuspension(): Int {
    val sb = StringBuilder()
    sb.Append("a")
    return sb.Length + suspendOperandAppend(sb)      // 1 + 2, NOT 4 + 2
}

// A delegate invoke (`delegateInvoke`) left of a suspension.
val suspendOperandDelegate: () -> Int = { suspendOperandLog.add("D"); 3 }
suspend fun suspendOperandDelegateBeforeSuspension(): Int = suspendOperandDelegate() + suspendOperandG()

// An `Any`-slot method (`objMethod`) left of a suspension.
class SuspendOperandNamed {
    override fun toString(): String {
        suspendOperandLog.add("S")
        return "named"
    }
}

val suspendOperandAny: Any = SuspendOperandNamed()
suspend fun suspendOperandObjMethodBeforeSuspension(): Int = suspendOperandAny.toString().length + suspendOperandG()

// An interface call on a TYPE-PARAMETER receiver (`constrainedCall`) left of a suspension.
interface SuspendOperandTagged {
    fun tag(): Int
}

class SuspendOperandTag : SuspendOperandTagged {
    override fun tag(): Int {
        suspendOperandLog.add("C")
        return 4
    }
}

suspend fun <T : SuspendOperandTagged> suspendOperandConstrainedBeforeSuspension(t: T): Int = t.tag() + suspendOperandG()

// ---- 5. a terminal operand left of a suspension --------------------------------------------------------------
suspend fun suspendOperandRelay(): Int {
    suspendOperandLog.add("R")
    return 5
}

suspend fun suspendOperandSum(a: Int, b: Int): Int = a + b
suspend fun suspendOperandSum3(a: Int, b: Int, c: Int): Int = a + b + c

fun suspendOperandSide(): Int {
    suspendOperandLog.add("E")
    return 1
}

// Was a compile-time refusal; now runs, throws, and never evaluates the operand to the right of the throw.
suspend fun suspendOperandTerminalBeforeSuspension(): Int =
    suspendOperandSum(run<Int> { suspendOperandLog.add("L"); throw IllegalStateException("boom") }, suspendOperandRelay())

// An operand to the LEFT of the terminal one still runs: Kotlin evaluates it before reaching the throw. The
// retired rewrite truncated the operand list at the terminal and then returned only the terminal, so everything
// left of it was rewritten into a value nothing read — and its side effect vanished.
suspend fun suspendOperandSideBeforeTerminal(): Int =
    suspendOperandSum3(suspendOperandSide(), run<Int> { suspendOperandLog.add("L"); throw IllegalStateException("boom") }, suspendOperandRelay())

class SuspendOperandOrderTests {
    private fun order(): String = suspendOperandLog.joinToString(",")

    // ---- 1 ----
    @TestAttribute
    fun nestedSuspensionInArgumentTerminates() {
        suspendOperandLog.clear()
        assertEquals(6, blockOn { suspendOperandNestedInArgument() })    // 4 + (1 + 1)
        assertEquals("T,A", order())                            // the argument's suspension, then the outer call
    }

    @TestAttribute
    fun nestedSuspensionInMemberArgument() {
        suspendOperandLog.clear()
        assertEquals(16, blockOn { suspendOperandNestedInMemberArgument() })   // 10 + 4 + 2
        assertEquals("T,M", order())
    }

    @TestAttribute
    fun nestedSuspensionInReceiver() {
        suspendOperandLog.clear()
        assertEquals(16, blockOn { suspendOperandNestedInReceiver() })    // 10 + 4 + 2
        assertEquals("B,M", order())                            // receiver first, then the member call
    }

    @TestAttribute
    fun nestedSuspensionInGenericArgument() {
        suspendOperandLog.clear()
        assertEquals(4, blockOn { suspendOperandNestedInGenericArgument() })
        assertEquals("T,P", order())
    }

    @TestAttribute
    fun nestedSuspensionCrossModuleStatic() {
        suspendOperandLog.clear()
        assertEquals(6, blockOn { suspendOperandNestedCrossModuleStatic() })   // 4 + 2
        assertEquals("T", order())
    }

    @TestAttribute
    fun nestedSuspensionCrossModuleInstance() {
        suspendOperandLog.clear()
        assertEquals(16, blockOn { suspendOperandNestedCrossModuleInstance() })   // 10 + 4 + 2
        assertEquals("T", order())
    }

    @TestAttribute
    fun nestedSuspensionCrossModuleGenericStatic() {
        suspendOperandLog.clear()
        assertEquals(4, blockOn { suspendOperandNestedCrossModuleGenericStatic() })
        assertEquals("T", order())
    }

    @TestAttribute
    fun nestedSuspensionCrossModuleGenericInstance() {
        suspendOperandLog.clear()
        assertEquals(4, blockOn { suspendOperandNestedCrossModuleGenericInstance() })
        assertEquals("T", order())
    }

    // ---- 2 ----
    @TestAttribute
    fun awaitedValueInSuspendingCallArgument() {
        suspendOperandLog.clear()
        assertEquals(7, blockOn { suspendOperandAwaitedInArgument() })    // 4 + 3
        assertEquals("A", order())
    }

    // ---- 3 ----
    @TestAttribute
    fun containedSuspensionInLeftOperand() {
        suspendOperandLog.clear()
        assertEquals(3, blockOn { suspendOperandContainedInLeftOperand() })   // 1 + 2
        assertEquals("F,H,G", order())                               // Kotlin's order (was F,G,H)
    }

    @TestAttribute
    fun containedSuspensionThroughToString() {
        suspendOperandLog.clear()
        assertEquals("12", blockOn { suspendOperandContainedThroughToString() })
        assertEquals("F,G", order())
    }

    @TestAttribute
    fun containedSuspensionThroughLength() {
        suspendOperandLog.clear()
        assertEquals(4, blockOn { suspendOperandContainedThroughLength() })   // "w1".length + 2
        assertEquals("F,W,G", order())
    }

    @TestAttribute
    fun containedSuspensionThroughConcat() {
        suspendOperandLog.clear()
        assertEquals("w12", blockOn { suspendOperandContainedThroughConcat() })
        assertEquals("F,W,G", order())                               // was F,G,W
    }

    @TestAttribute
    fun containedSuspensionThroughTemplate() {
        suspendOperandLog.clear()
        assertEquals("[w1:2]", blockOn { suspendOperandContainedThroughTemplate() })
        assertEquals("F,W,G", order())                               // was F,G,W
    }

    // ---- 4 ----
    @TestAttribute
    fun clrPropertyReadBeforeSuspension() {
        suspendOperandLog.clear()
        assertEquals(3, blockOn { suspendOperandClrPropertyBeforeSuspension() })   // the PRE-append length, 1, plus 2
    }

    @TestAttribute
    fun delegateInvokeBeforeSuspension() {
        suspendOperandLog.clear()
        assertEquals(5, blockOn { suspendOperandDelegateBeforeSuspension() })   // 3 + 2
        assertEquals("D,G", order())
    }

    @TestAttribute
    fun objMethodBeforeSuspension() {
        suspendOperandLog.clear()
        assertEquals(7, blockOn { suspendOperandObjMethodBeforeSuspension() })   // "named".length + 2
        assertEquals("S,G", order())
    }

    @TestAttribute
    fun constrainedCallBeforeSuspension() {
        suspendOperandLog.clear()
        assertEquals(6, blockOn { suspendOperandConstrainedBeforeSuspension(SuspendOperandTag()) })   // 4 + 2
        assertEquals("C,G", order())
    }

    // ---- 5 ----
    @TestAttribute
    fun terminalOperandBeforeSuspensionThrows() {
        suspendOperandLog.clear()
        var message: String? = null
        try {
            blockOn { suspendOperandTerminalBeforeSuspension() }
        } catch (e: IllegalStateException) {
            message = e.message
        }
        assertEquals("boom", message)
        // The throw is the whole expression's value: the operand to its right is never evaluated, so `suspendOperandRelay`
        // never runs and the enclosing suspending call is never made.
        assertEquals("L", order())
        assertTrue(!suspendOperandLog.contains("R"))
    }

    @TestAttribute
    fun sideEffectBeforeATerminalOperandStillRuns() {
        suspendOperandLog.clear()
        var message: String? = null
        try {
            blockOn { suspendOperandSideBeforeTerminal() }
        } catch (e: IllegalStateException) {
            message = e.message
        }
        assertEquals("boom", message)
        // The left operand ran, the terminal one then left, and nothing to its right was evaluated.
        assertEquals("E,L", order())
        assertTrue(!suspendOperandLog.contains("R"))
    }
}
