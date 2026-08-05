// The VALUE side of the state machine's storage decision: a local that the machine leaves as a MoveNext local
// must be one that no path reads after a resume — otherwise it reads back as the resume-fresh zero and the
// function silently computes the wrong answer. Its byref-like twin (which locals may be demoted at all) is
// ByRefLikeStorageTests.kt; this file is about getting the LIVENESS right, and every case here is a shape where
// a plausible-looking analysis says "dead" and the emitter says otherwise.
//
// Two emitter facts drive them. First, a suspension's segments are appended to the statement list as the
// statement is rewritten, and the statement itself — assembled from its rewritten operands — is emitted AFTER
// them: an operand the emitter leaves inline is therefore read at the RESUME point, however early it appears in
// the source. Second, an exception can be raised part-way through a protected region, so a handler's reads must
// keep a value alive throughout the region and a write inside the region must not kill what the handler sees.
//
// Every assertion here is a value, not a shape: each returns a number that is only reachable when the local
// survived. Under a source-order-only analysis they return 2 / 3 / 2 / 2 / 0 / 0 instead.
//
// All top-level declarations use the descriptive `suspendLocal`/`SuspendLocal` stem so their simple names are
// unique across this assembly (the cold-core lowering keys top-level suspend funs by simple name).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import System.Span
import System.Threading.Tasks.Task
import dotkt.support.blockOn

suspend fun suspendLocalTick(n: Int): Int {
    Task.Delay(1).await()          // a GENUINE suspension: a synchronously-completing relay never re-enters
    return n + 1                   // invokeSuspend, so it cannot observe a demoted local at all
}

fun suspendLocalPair(a: Int, b: Int): Int = a * 100 + b
fun suspendLocalBoom(): Int = throw IllegalStateException("boom")
fun suspendLocalMax(a: Int, b: Int): Int = if (a > b) a else b
class SuspendLocalBox(val a: Int, val b: Int) { val sum: Int get() = a * 100 + b }
fun interface SuspendLocalHandler { fun h(x: Int): Int }
fun suspendLocalUse(g: SuspendLocalHandler, n: Int): Int = g.h(n)

// A sibling ARGUMENT of a suspending argument. `x` appears before the suspension in the source and is read
// after it in the emitted code.
suspend fun suspendLocalSiblingArg(): Int {
    val x = suspendLocalPair(0, 7)
    return suspendLocalPair(x, suspendLocalTick(1))
}

// The same deferral one level deeper: the suspension is in the outer binOp's right operand, `y` two levels down.
suspend fun suspendLocalNestedOperand(): Int {
    val y = 5
    return 1 + (y + suspendLocalTick(1))
}

// A sibling ELEMENT of an array literal whose other element suspends.
suspend fun suspendLocalArrayElement(): Int {
    val x = 3
    val a = intArrayOf(x, suspendLocalTick(1))
    return a[0] * 10 + a[1]
}

// A conditional lowered to control flow because a branch ESCAPES, with no suspension of its own, standing left
// of a suspending operand — its result temp is deferred past the resume exactly like a plain local.
suspend fun suspendLocalEscapingCond(b: Boolean): Int =
    suspendLocalPair(if (b) 7 else return -1, suspendLocalTick(1))

// A write INSIDE a protected region must not kill the value the handler sees: the exception is raised before
// the store completes, so the pre-region value is what survives.
suspend fun suspendLocalWriteInTry(): Int {
    var x = 1
    suspendLocalTick(0)
    try { x = suspendLocalBoom() } catch (e: Exception) { }
    return x
}

// A protected body that performs no read or write of a tracked local at all — the handler's read still has to
// keep `x` alive across the suspension that precedes the region.
suspend fun suspendLocalHandlerRead(): Int {
    val x = 7
    suspendLocalTick(0)
    try { suspendLocalBoom() } catch (e: Exception) { return x }
    return 0
}

// The OTHER half of the deferral rule, and the reason it cannot be "re-read everything": an operand the emitter
// SPILLS before the suspension does not cross the resume, so the local feeding it is dead across it. Here the
// spilled value is an Int and the local is a byref-like Span — treating the operand as deferred would refuse
// these three with the CS4007 mirror even though the state machine only ever stores the Int. The three positions
// are the three the evaluation-order spill covers: a binary operand, a call argument and a constructor argument.
suspend fun suspendLocalSpilledBinOp(): Int {
    val s = Span<Int>(arrayOf(1, 2, 3))
    return s.ToArray().size + suspendLocalTick(1)
}

suspend fun suspendLocalSpilledCallArg(): Int {
    val s = Span<Int>(arrayOf(1, 2, 3, 4))
    return suspendLocalMax(s.ToArray().size, suspendLocalTick(1))
}

suspend fun suspendLocalSpilledCtorArg(): Int {
    val s = Span<Int>(arrayOf(1, 2, 3, 4, 5))
    return SuspendLocalBox(s.ToArray().size, suspendLocalTick(1)).sum
}

// A SAM's capture VALUE is evaluated in this frame and, when a sibling argument suspends, is read at the resume —
// so the capture list has to be walked on the re-read as well as on the first pass.
suspend fun suspendLocalSamCapture(): Int {
    val k = 3
    return suspendLocalUse(SuspendLocalHandler { it * k }, suspendLocalTick(1))
}

class SuspendLocalDemotionTests {
    @TestAttribute
    fun operandsDeferredPastAResumeKeepTheirValue() {
        assertEquals(702, blockOn { suspendLocalSiblingArg() })        // 7 * 100 + tick(1)
        assertEquals(8, blockOn { suspendLocalNestedOperand() })       // 1 + (5 + tick(1))
        assertEquals(32, blockOn { suspendLocalArrayElement() })       // [3, tick(1)] -> 3 * 10 + 2
        assertEquals(702, blockOn { suspendLocalEscapingCond(true) })  // 7 * 100 + tick(1)
        assertEquals(-1, blockOn { suspendLocalEscapingCond(false) })  // the escaping branch still escapes
    }

    @TestAttribute
    fun aHandlersReadsSurviveTheSuspensionBeforeTheRegion() {
        assertEquals(1, blockOn { suspendLocalWriteInTry() })
        assertEquals(7, blockOn { suspendLocalHandlerRead() })
    }

    @TestAttribute
    fun aSpilledOperandDoesNotKeepItsSourceAlive() {
        // Each returns a value only reachable if the byref-like local stayed a local — i.e. if the analysis
        // agreed with the emitter that the spilled Int, not the Span, is what crosses the resume.
        assertEquals(5, blockOn { suspendLocalSpilledBinOp() })       // 3 + tick(1)
        assertEquals(4, blockOn { suspendLocalSpilledCallArg() })     // max(4, tick(1))
        assertEquals(502, blockOn { suspendLocalSpilledCtorArg() })   // 5 * 100 + tick(1)
    }

    @TestAttribute
    fun aSamCaptureDeferredPastAResumeKeepsItsValue() {
        assertEquals(6, blockOn { suspendLocalSamCapture() })         // (tick(1)) * 3
    }
}
