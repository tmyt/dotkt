// Cold-core suspend battery — migrates the `suspendCoroutine{}`-shaped and Unit-Task-bridge coroutine cases
// onto the in-process NUnit suite. These exercise the CLR cold Continuation state machine directly (no genuine
// .NET Task await — the sync-completion path), driven by the shared `dotkt.support.blockOn` harness. Each old
// case's `main` + stdout-golden becomes one @TestAttribute method preserving every asserted value 1:1 (see the
// `// <expected>` comments).
//
// Coverage preserved (old case -> method):
//   il-suspendco -> suspendco_syncResume / suspendco_syncResumeWithException
//                   cross-module `suspendCoroutine{}` -> caller cold SM; SafeContinuation UNDECIDED/RESUMED
//                   identity cache (F1) so a SYNC `it.resume(42)` holds and `it.resumeWithException` rethrows.
//   il-counit    -> counit_unitReturningSuspendTaskBridge
//                   a PUBLIC Unit-returning suspend fun -> a NON-generic public `Task` bridge (coroutine-abi.md
//                   §1: `suspend fun f(): Unit` maps to `Task`, not `Task<Unit>`). greet stays Unit-returning
//                   (the bridge point); its former println side effect is captured into `unitContinuationLog` and asserted.
//   regression   -> unitContinuationList
//                   compiling a generic List<T>-returning public suspend declaration synthesizes one coherent
//                   readonly CLR result slot across Task<T>, TCS<T>, RootContinuation<T>, and TrySetResult(T).
//
// Top-level names use the descriptive `synchronousContinuation` and `unitContinuation` stems so they remain
// readable and cannot clash with sibling coroutine fixtures or the stdlib.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import kotlin.coroutines.suspendCoroutine
import kotlin.coroutines.Continuation
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED
import kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import System.Threading.Tasks.Task
import dotkt.support.blockOn

// il-suspendco: a synchronous resume — the block resumes immediately with 42 (never actually suspends).
suspend fun synchronousContinuationResume(): Int = suspendCoroutine { it.resume(42) }

// il-suspendco: a synchronous resumeWithException — getOrThrow() rethrows the failure at the (sync) point.
suspend fun synchronousContinuationResumeWithException(): Int = suspendCoroutine { it.resumeWithException(IllegalStateException("boom")) }

suspend fun storedContinuationBlockResume(): Int {
    val value = 43
    val block: (Continuation<Int>) -> Unit = { continuation -> continuation.resume(value) }
    return suspendCoroutine(block)
}

fun resumeStoredContinuation(continuation: Continuation<Int>): Unit = continuation.resume(44)

suspend fun storedContinuationReferenceResume(): Int {
    val block: (Continuation<Int>) -> Unit = ::resumeStoredContinuation
    return suspendCoroutine(block)
}

suspend fun <T> continuationParameterResume(block: (Continuation<T>) -> Unit): T = suspendCoroutine(block)

suspend fun storedUninterceptedResult(): Int {
    val value = 45
    val block: (Continuation<Int>) -> Any? = { value }
    return suspendCoroutineUninterceptedOrReturn(block)
}

suspend fun storedUninterceptedResume(): Int {
    val value = 47
    val block: (Continuation<Int>) -> Any? = { continuation ->
        continuation.resume(value)
        COROUTINE_SUSPENDED
    }
    return suspendCoroutineUninterceptedOrReturn(block)
}

suspend fun makeStoredUninterceptedBlock(): (Continuation<Int>) -> Any? {
    Task.Delay(1).await()
    return { continuation ->
        continuation.resume(48)
        COROUTINE_SUSPENDED
    }
}

suspend fun suspendingBlockExpressionResume(): Int =
    suspendCoroutineUninterceptedOrReturn(makeStoredUninterceptedBlock())

class ContinuationBlockHolder(private val value: Int) {
    private val block: (Continuation<Int>) -> Unit = { continuation -> continuation.resume(value) }
    suspend fun resume(): Int = suspendCoroutine(block)
}

fun continuationBlock(value: Int): (Continuation<Int>) -> Unit = { continuation -> continuation.resume(value) }

fun starContinuationDeliverFailure(continuation: Continuation<*>, failure: Throwable) {
    continuation.resumeWith(Result.failure(failure))
}

inline fun starContinuationDeliverFailureInline(
    continuation: Continuation<*>,
    failure: Throwable,
    beforeResume: () -> Unit,
) {
    beforeResume()
    continuation.resumeWith(Result.failure(failure))
}

class StarContinuationProbe : Continuation<Any?> {
    override val context = EmptyCoroutineContext
    var outcome: String = "pending"

    override fun resumeWith(result: Result<Any?>) {
        outcome = result.exceptionOrNull()?.message ?: "success"
    }
}

suspend fun callResultContinuationResume(): Int = suspendCoroutine(continuationBlock(50))

suspend fun suspendCoroutine(value: Int): Int = value + 1

suspend fun sameNameSuspendCoroutineCall(): Int = suspendCoroutine(50)

suspend fun receiverContinuationBlockResume(): Int {
    val block: Continuation<Int>.() -> Unit = { resume(52) }
    return suspendCoroutine(block)
}

@Suppress("UNCHECKED_CAST")
suspend fun contravariantContinuationBlockResume(): Int {
    val block: (Any?) -> Unit = { value -> (value as Continuation<Int>).resume(53) }
    return suspendCoroutine(block)
}

// il-counit: a PUBLIC Unit-returning suspend fun -> a non-generic `Task` bridge. `unitContinuationStep` completes
// synchronously, so `unitContinuationGreet` genuinely suspends then resumes on the sync path, exercising the full
// state-machine + Unit Task-bridge emit. The former println("hello 42") is captured into unitContinuationLog.
suspend fun unitContinuationStep(): Int = 21
var unitContinuationLog: String = ""
suspend fun unitContinuationGreet(): Unit {
    val x = unitContinuationStep()
    unitContinuationLog = "hello " + (x * 2)
}

suspend fun <T> unitContinuationList(value: T): List<T> = listOf(value)

class ContinuationBridgeTests {
    @TestAttribute
    fun syncResume() {
        assertEquals(42, blockOn { synchronousContinuationResume() })   // 42
    }

    @TestAttribute
    fun syncResumeWithException() {
        var msg: String? = null
        try {
            blockOn { synchronousContinuationResumeWithException() }
        } catch (e: IllegalStateException) {
            msg = e.message
        }
        assertEquals("boom", msg)                // former golden: "caught:boom"
    }

    @TestAttribute
    fun storedBlockResume() {
        assertEquals(43, blockOn { storedContinuationBlockResume() })
    }

    @TestAttribute
    fun storedReferenceResume() {
        assertEquals(44, blockOn { storedContinuationReferenceResume() })
    }

    @TestAttribute
    fun parameterBlockResume() {
        assertEquals(46, blockOn { continuationParameterResume { it.resume(46) } })
    }

    @TestAttribute
    fun uninterceptedBlockResult() {
        assertEquals(45, blockOn { storedUninterceptedResult() })
    }

    @TestAttribute
    fun uninterceptedBlockResume() {
        assertEquals(47, blockOn { storedUninterceptedResume() })
    }

    @TestAttribute
    fun suspendingBlockExpression() {
        assertEquals(48, blockOn { suspendingBlockExpressionResume() })
    }

    @TestAttribute
    fun fieldBlockResume() {
        assertEquals(49, blockOn { ContinuationBlockHolder(49).resume() })
    }

    @TestAttribute
    fun callResultBlockResume() {
        assertEquals(50, blockOn { callResultContinuationResume() })
    }

    @TestAttribute
    fun sameNameUserFunction() {
        assertEquals(51, blockOn { sameNameSuspendCoroutineCall() })
    }

    @TestAttribute
    fun receiverBlockResume() {
        assertEquals(52, blockOn { receiverContinuationBlockResume() })
    }

    @TestAttribute
    fun contravariantBlockResume() {
        assertEquals(53, blockOn { contravariantContinuationBlockResume() })
    }

    @TestAttribute
    fun starProjectedMemberCallUsesExistentialSlot() {
        val direct = StarContinuationProbe()
        starContinuationDeliverFailure(direct, IllegalStateException("direct"))
        assertEquals("direct", direct.outcome)

        val inlined = StarContinuationProbe()
        starContinuationDeliverFailureInline(inlined, IllegalStateException("inline")) {}
        assertEquals("inline", inlined.outcome)
    }

    @TestAttribute
    fun unitReturningSuspendTaskBridge() {
        unitContinuationLog = ""
        blockOn { unitContinuationGreet() }
        assertEquals("hello 42", unitContinuationLog)          // former golden: "hello 42" (then main printed "done")
    }
}
