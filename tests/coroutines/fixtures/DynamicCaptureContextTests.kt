// DYNAMIC `captureContext` — `await(captureContext = <expression>)` where the expression is not a constant `true`
// (GitHub #64). dll2klib publishes TWO await bridges for an awaitable that exposes `ConfigureAwait(bool)` —
// `await()` and `await(captureContext: Boolean)` — so a runtime Boolean is an ordinary frontend-resolved call.
// bir2cir (SuspendColdLowering.EmitAwaitPoint) used to accept only a constant there and aborted the whole compile
// on anything else, so every function in this file failed to COMPILE before the fix.
//
// The lowering has TWO arms, chosen by the argument's SHAPE and never by its value:
//
//   omitted, or a constant `true`  ->  awaitable.GetAwaiter()
//   anything else                  ->  awaitable.ConfigureAwait(<the expression>).GetAwaiter()
//
// A constant `false` is not an arm of its own — it flows through the second one as the expression `false`.
// `ConfigureAwait(true)` and `ConfigureAwait(false)` return the SAME configured awaiter type, so a runtime Boolean
// selects no type and needs no branch between two state-machine field types: the arm is fixed at compile time and
// the value only reaches the .NET call. The FAST-PATH arm cannot be witnessed at run time (ConfigureAwait(true) is
// behaviorally identical to GetAwaiter()), so it is pinned by CIR shape in tests/ir/lowering/await-capture-*.
//
// The order sections assert POSITIONALLY through `corCcLog`: the captureContext expression is a value Kotlin
// evaluates exactly ONCE and AFTER the awaitable receiver. Three shapes, because each takes a different path
// through the lowering: an ordinary expression (evaluated in its own slot), one that SUSPENDS (which splits the
// marker's own operand list across a resume), and one that TRANSFERS CONTROL instead of producing a value —
// including from inside a closure capture, where the transfer sits one frame down but still runs in this one.
//
// `.await()` inside a GENERIC suspend fun is separately broken (open-generic delegate binding, GitHub #303), so
// every function here is non-generic — the same exclusion SuspendOperandOrderTests.kt carries.
//
// Top-level names are family-prefixed (`corCc`) — the cold-core lowering keys top-level suspend funs by simple name.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Threading.Tasks.Task
import System.Threading.Tasks.Task1
import System.Threading.Tasks.TaskCompletionSource1
import System.Threading.Thread
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine
import dotkt.support.blockOn

val corCcLog = mutableListOf<String>()

// ---- the awaited functions ------------------------------------------------------------------------------------

// A RUNTIME Boolean: the capture policy is a parameter, which is the shape the issue names.
suspend fun corCcAwait(task: Task1<Int>, capture: Boolean): Int = task.await(captureContext = capture)

// The same over a non-generic `Task` (a `void` GetResult -> Unit), so the dynamic arm is covered for both the
// generic and the non-generic configured awaitable.
suspend fun corCcAwaitUnit(task: Task, capture: Boolean): Int {
    task.await(captureContext = capture)
    return 8
}

// A constant `false` — the same arm as the dynamic one now, reaching it as the expression `false`.
suspend fun corCcAwaitConstFalse(task: Task1<Int>): Int = task.await(captureContext = false)

// The two fast-path shapes, kept HERE as well so the arm they take still executes correctly.
suspend fun corCcAwaitOmitted(task: Task1<Int>): Int = task.await()
suspend fun corCcAwaitConstTrue(task: Task1<Int>): Int = task.await(captureContext = true)

// ---- evaluation order: the receiver, then the argument, each exactly once ---------------------------------------

fun corCcReceiver(task: Task1<Int>): Task1<Int> {
    corCcLog.add("R")
    return task
}

fun corCcCapture(v: Boolean): Boolean {
    corCcLog.add("C")
    return v
}

suspend fun corCcOrdered(task: Task1<Int>): Int =
    corCcReceiver(task).await(captureContext = corCcCapture(false))

// The captureContext expression is itself SUSPENDING: the marker's operand list is split across a resume, so the
// receiver must already have been evaluated (and survive the suspension) before the argument runs.
suspend fun corCcSuspendingCapture(v: Boolean): Boolean {
    Task.Delay(1).await()
    corCcLog.add("C")
    return v
}

suspend fun corCcOrderedAcrossSuspension(task: Task1<Int>): Int =
    corCcReceiver(task).await(captureContext = corCcSuspendingCapture(true))

// The captureContext expression ESCAPES instead of producing a value (a `throw` in expression position). That is the
// other shape whose lowering emits statements, so it takes the same receiver-binding path as a suspending argument:
// the receiver has already been evaluated when the argument transfers control, and nothing is awaited.
suspend fun corCcEscapingCapture(task: Task1<Int>, bail: Boolean): Int =
    corCcReceiver(task).await(captureContext = if (bail) throw IllegalStateException("bail") else false)

// The same control transfer, one frame down: a BOUND CALLABLE REFERENCE puts an arbitrary expression into a
// closure's capture list, and that expression is evaluated where the closure is CONSTRUCTED — here, inside the
// captureContext argument. The two "whose frame is this" predicates stop at every lambda kind, so a receiver bound
// on their answer was not bound here, and the throwing path evaluated `corCcReceiver` ZERO times.
fun String.corCcMark() {}

fun corCcPolicy(f: () -> Unit): Boolean = false

suspend fun corCcEscapingCaptureUnderClosure(task: Task1<Int>, bail: Boolean): Int =
    corCcReceiver(task).await(
        captureContext = corCcPolicy((if (bail) throw IllegalStateException("bail") else "s")::corCcMark))

// A LOOP-BORNE suspension in the argument: an inline collection loop is not a separate frame — its suspension is
// flattened into this state machine — so it crosses the receiver's binding and that binding must be a
// state-machine field, not a MoveNext local the resume no longer sees. This is the arm on the other side of the
// storage question from the two control-transfer shapes above, reached without a suspending call in the argument's
// own operand list.
fun corCcAnyOdd(values: List<Int>): Boolean = values.any { it % 2 == 1 }

suspend fun corCcTick(v: Int): Int {
    Task.Delay(1).await()
    corCcLog.add("C")
    return v
}

suspend fun corCcLoopBorneCapture(task: Task1<Int>): Int =
    corCcReceiver(task).await(captureContext = corCcAnyOdd(listOf(2, 4).map { corCcTick(it) }))

// ---- the drive ---------------------------------------------------------------------------------------------------

// A terminal sink + bounded drain (the CoroutineContextInterceptionTests pattern): a GENUINE suspension is needed
// to exercise the OnCompleted registration on the configured awaiter, and its resume may land on the threadpool.
class CorCcSink : Continuation<Any?> {
    var done: Boolean = false
    var value: Any? = null
    var error: Throwable? = null
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<Any?>) {
        value = result.getOrNull()
        error = result.exceptionOrNull()
        done = true
    }
}

fun corCcDrain(sink: CorCcSink) {
    var tries = 0
    while (!sink.done && tries < 3000) { Thread.Sleep(1); tries += 1 }
}

fun corCcCompleted(v: Int): Task1<Int> {
    val tcs = TaskCompletionSource1<Int>()
    tcs.SetResult(v)
    return tcs.Task
}

class DynamicCaptureContextTests {
    private fun order(): String = corCcLog.joinToString(",")

    // A runtime `true`: the capturing policy, requested through a value the compiler cannot read.
    @TestAttribute
    fun runtimeTrueSuspends() {
        val tcs = TaskCompletionSource1<Int>()
        val sink = CorCcSink()
        val block: suspend () -> Int = { corCcAwait(tcs.Task, true) + 1 }
        block.startCoroutine(sink)   // suspends: the task is not complete yet
        tcs.SetResult(41)
        corCcDrain(sink)
        assertEquals(true, sink.done)
        assertEquals(42, sink.value)
    }

    // A runtime `false`: the opt-out, same call site, same awaiter type, different value.
    @TestAttribute
    fun runtimeFalseSuspends() {
        val tcs = TaskCompletionSource1<Int>()
        val sink = CorCcSink()
        val block: suspend () -> Int = { corCcAwait(tcs.Task, false) + 2 }
        block.startCoroutine(sink)
        tcs.SetResult(5)
        corCcDrain(sink)
        assertEquals(true, sink.done)
        assertEquals(7, sink.value)
    }

    // The synchronous fast path (IsCompleted true at the await point) for both values.
    @TestAttribute
    fun runtimeBooleanOnCompletedTask() {
        assertEquals(3, blockOn { corCcAwait(corCcCompleted(3), true) })
        assertEquals(4, blockOn { corCcAwait(corCcCompleted(4), false) })
    }

    // The non-generic awaitable (void GetResult) through the dynamic arm.
    @TestAttribute
    fun runtimeBooleanNonGenericAwaitable() {
        assertEquals(8, blockOn { corCcAwaitUnit(Task.CompletedTask, true) })
        assertEquals(8, blockOn { corCcAwaitUnit(Task.Delay(1), false) })
    }

    // The constant shapes keep working, and the constant `false` now shares the dynamic arm.
    @TestAttribute
    fun constantShapes() {
        assertEquals(11, blockOn { corCcAwaitConstFalse(corCcCompleted(11)) })
        assertEquals(12, blockOn { corCcAwaitOmitted(corCcCompleted(12)) })
        assertEquals(13, blockOn { corCcAwaitConstTrue(corCcCompleted(13)) })
    }

    // The awaitable receiver is evaluated BEFORE the captureContext argument, and each exactly once.
    @TestAttribute
    fun receiverThenArgumentExactlyOnce() {
        corCcLog.clear()
        assertEquals(21, blockOn { corCcOrdered(corCcCompleted(21)) })
        assertEquals("R,C", order())
    }

    // The same when the argument SUSPENDS: the receiver was evaluated before the suspension and is still the
    // awaitable the configured call is made on after the resume.
    @TestAttribute
    fun receiverSurvivesASuspendingArgument() {
        corCcLog.clear()
        val sink = CorCcSink()
        val block: suspend () -> Int = { corCcOrderedAcrossSuspension(corCcCompleted(22)) }
        block.startCoroutine(sink)
        corCcDrain(sink)
        assertEquals(true, sink.done)
        assertEquals(22, sink.value)
        assertEquals("R,C", order())
    }

    // An argument that TRANSFERS CONTROL rather than producing a value: the receiver was evaluated (Kotlin evaluates
    // it first), the throw leaves through the call that was going to consume it, and nothing is awaited.
    @TestAttribute
    fun escapingCaptureArgument() {
        corCcLog.clear()
        var message: String? = null
        try {
            blockOn { corCcEscapingCapture(corCcCompleted(31), true) }
        } catch (e: IllegalStateException) {
            message = e.message
        }
        assertEquals("bail", message)
        assertEquals("R", order())   // the receiver ran; the argument never yielded a value

        corCcLog.clear()
        assertEquals(32, blockOn { corCcEscapingCapture(corCcCompleted(32), false) })
        assertEquals("R", order())
    }

    // The transfer one frame down, inside a closure capture. Same assertion, and it is the one that was wrong:
    // the receiver was evaluated ZERO times on the throwing path.
    @TestAttribute
    fun escapingCaptureUnderAClosureCapture() {
        corCcLog.clear()
        var message: String? = null
        try {
            blockOn { corCcEscapingCaptureUnderClosure(corCcCompleted(41), true) }
        } catch (e: IllegalStateException) {
            message = e.message
        }
        assertEquals("bail", message)
        assertEquals("R", order())

        corCcLog.clear()
        assertEquals(42, blockOn { corCcEscapingCaptureUnderClosure(corCcCompleted(42), false) })
        assertEquals("R", order())
    }

    // A suspension the argument reaches through an inline collection loop rather than through its own operand
    // list: it still crosses the receiver's binding, so the binding is a state-machine field and the awaitable is
    // intact after both resumes.
    @TestAttribute
    fun receiverSurvivesALoopBorneSuspensionInTheArgument() {
        corCcLog.clear()
        val sink = CorCcSink()
        val block: suspend () -> Int = { corCcLoopBorneCapture(corCcCompleted(51)) }
        block.startCoroutine(sink)
        corCcDrain(sink)
        assertEquals(true, sink.done)
        assertEquals(51, sink.value)
        assertEquals("R,C,C", order())   // the receiver, then one resume per element
    }
}
