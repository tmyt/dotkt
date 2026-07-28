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
// All top-level decls carry the `brd` case token under the shared `corB`/`CorB` prefix so their simple names are
// unique across this assembly (the cold-core lowering keys top-level suspend funs by simple name).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Span
import System.Threading.Tasks.Task
import kotlin.clr.await
import dotkt.support.blockOn

suspend fun corBrdTick(n: Int): Int {
    Task.Delay(1).await()          // a GENUINE suspension: a synchronously-completing relay never re-enters
    return n + 1                   // invokeSuspend, so it cannot observe a demoted local at all
}

fun corBrdPair(a: Int, b: Int): Int = a * 100 + b
fun corBrdBoom(): Int = throw IllegalStateException("boom")
fun corBrdMax(a: Int, b: Int): Int = if (a > b) a else b
class CorBrdBox(val a: Int, val b: Int) { val sum: Int get() = a * 100 + b }
fun interface CorBrdHandler { fun h(x: Int): Int }
fun corBrdUse(g: CorBrdHandler, n: Int): Int = g.h(n)

// A sibling ARGUMENT of a suspending argument. `x` appears before the suspension in the source and is read
// after it in the emitted code.
suspend fun corBrdSiblingArg(): Int {
    val x = corBrdPair(0, 7)
    return corBrdPair(x, corBrdTick(1))
}

// The same deferral one level deeper: the suspension is in the outer binOp's right operand, `y` two levels down.
suspend fun corBrdNestedOperand(): Int {
    val y = 5
    return 1 + (y + corBrdTick(1))
}

// A sibling ELEMENT of an array literal whose other element suspends.
suspend fun corBrdArrayElement(): Int {
    val x = 3
    val a = intArrayOf(x, corBrdTick(1))
    return a[0] * 10 + a[1]
}

// A conditional lowered to control flow because a branch ESCAPES, with no suspension of its own, standing left
// of a suspending operand — its result temp is deferred past the resume exactly like a plain local.
suspend fun corBrdEscapingCond(b: Boolean): Int =
    corBrdPair(if (b) 7 else return -1, corBrdTick(1))

// A write INSIDE a protected region must not kill the value the handler sees: the exception is raised before
// the store completes, so the pre-region value is what survives.
suspend fun corBrdWriteInTry(): Int {
    var x = 1
    corBrdTick(0)
    try { x = corBrdBoom() } catch (e: Exception) { }
    return x
}

// A protected body that performs no read or write of a tracked local at all — the handler's read still has to
// keep `x` alive across the suspension that precedes the region.
suspend fun corBrdHandlerRead(): Int {
    val x = 7
    corBrdTick(0)
    try { corBrdBoom() } catch (e: Exception) { return x }
    return 0
}

// The OTHER half of the deferral rule, and the reason it cannot be "re-read everything": an operand the emitter
// SPILLS before the suspension does not cross the resume, so the local feeding it is dead across it. Here the
// spilled value is an Int and the local is a byref-like Span — treating the operand as deferred would refuse
// these three with the CS4007 mirror even though the state machine only ever stores the Int. The three positions
// are the three the evaluation-order spill covers: a binary operand, a call argument and a constructor argument.
suspend fun corBrdSpilledBinOp(): Int {
    val s = Span<Int>(arrayOf(1, 2, 3))
    return s.ToArray().size + corBrdTick(1)
}

suspend fun corBrdSpilledCallArg(): Int {
    val s = Span<Int>(arrayOf(1, 2, 3, 4))
    return corBrdMax(s.ToArray().size, corBrdTick(1))
}

suspend fun corBrdSpilledCtorArg(): Int {
    val s = Span<Int>(arrayOf(1, 2, 3, 4, 5))
    return CorBrdBox(s.ToArray().size, corBrdTick(1)).sum
}

// A SAM's capture VALUE is evaluated in this frame and, when a sibling argument suspends, is read at the resume —
// so the capture list has to be walked on the re-read as well as on the first pass.
suspend fun corBrdSamCapture(): Int {
    val k = 3
    return corBrdUse(CorBrdHandler { it * k }, corBrdTick(1))
}

class CorBrdSuspendLocalDemotionTests {
    @TestAttribute
    fun operandsDeferredPastAResumeKeepTheirValue() {
        assertEquals(702, blockOn { corBrdSiblingArg() })        // 7 * 100 + tick(1)
        assertEquals(8, blockOn { corBrdNestedOperand() })       // 1 + (5 + tick(1))
        assertEquals(32, blockOn { corBrdArrayElement() })       // [3, tick(1)] -> 3 * 10 + 2
        assertEquals(702, blockOn { corBrdEscapingCond(true) })  // 7 * 100 + tick(1)
        assertEquals(-1, blockOn { corBrdEscapingCond(false) })  // the escaping branch still escapes
    }

    @TestAttribute
    fun aHandlersReadsSurviveTheSuspensionBeforeTheRegion() {
        assertEquals(1, blockOn { corBrdWriteInTry() })
        assertEquals(7, blockOn { corBrdHandlerRead() })
    }

    @TestAttribute
    fun aSpilledOperandDoesNotKeepItsSourceAlive() {
        // Each returns a value only reachable if the byref-like local stayed a local — i.e. if the analysis
        // agreed with the emitter that the spilled Int, not the Span, is what crosses the resume.
        assertEquals(5, blockOn { corBrdSpilledBinOp() })       // 3 + tick(1)
        assertEquals(4, blockOn { corBrdSpilledCallArg() })     // max(4, tick(1))
        assertEquals(502, blockOn { corBrdSpilledCtorArg() })   // 5 * 100 + tick(1)
    }

    @TestAttribute
    fun aSamCaptureDeferredPastAResumeKeepsItsValue() {
        assertEquals(6, blockOn { corBrdSamCapture() })         // (tick(1)) * 3
    }
}
