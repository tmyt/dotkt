// A `fun f(): Nothing` in a VALUE position INSIDE a suspend function (#197) — the state-machine twin of
// tests/basic ExceptionTests.nothingReturningCallInValuePosition and of the cross-module round-trip case.
//
// `Nothing` has no CLR analog, so such a call returns `object`, and bir2cir's NothingValueTermination rewrites a
// `Nothing`-stamped value position into `throw <call>` so the erased `object` never reaches the slot that reads it.
// In a suspend body that rewrite lands in a DIFFERENT lowering: SuspendColdLowering turns a conditional into a
// `__cond$` result slot plus explicit control flow, and its `EmitCondBranch` emits a `throwExpr` arm as a bare
// statement with NO store — so one rule serves the plain merge and the state machine. It also WIDENS what that
// lowering handles: `EscapesExpression` is a whole-subtree walk, so a terminated arm anywhere under a conditional
// (or a `valueBlock`) now routes the whole node through the control-flow path that mints and types the `__cond$`
// slot. A slot that lowering cannot type is a hard refusal, so these shapes are exactly where such a refusal
// would surface — an abort on frontend-accepted IR, which is a compiler bug, not a limitation.
//
// The formal half (ilverify over the emitted assembly, tests/run-ilverify.sh) is the one that regresses first:
// every assert here also passed BEFORE the fix, because the terminated arm always throws before the merge is
// reached. So read a green run together with a clean ilverify, not on its own.
//
// Shapes: both arms Nothing (the conditional itself produces nothing), a SUSPENDING Nothing callee, elvis, a block
// arm ENDING in the call, a `when` WITH a subject (a different node path from the subject-less one the basic
// battery covers), a value-typed (`Int`) merge, a whole expression body, a nested argument, a `try` arm, and a
// companion-static producer. Top-level names carry the `suspendNothing` prefix.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

suspend fun suspendNothingRelay(x: Int): Int = x
fun suspendNothingFail(msg: String): Nothing = throw IllegalStateException(msg)
suspend fun suspendNothingSuspendFail(msg: String): Nothing = throw IllegalStateException(msg)
class SuspendNothingBoom { companion object { fun boom(): Nothing = throw IllegalStateException("boom") } }

val suspendNothingLog = mutableListOf<String>()

suspend fun suspendNothingBothArms(n: Int): String {
    val a = suspendNothingRelay(n)
    return if (a >= 0) suspendNothingFail("a") else suspendNothingFail("b")
}
suspend fun suspendNothingSuspendingArm(n: Int): String {
    val a = suspendNothingRelay(n)
    return if (a >= 0) "kept" else suspendNothingSuspendFail("s")
}
suspend fun suspendNothingElvis(s: String?): String {
    val a = suspendNothingRelay(1)
    return if (a >= 0) (s ?: suspendNothingFail("elvis")) else "x"
}
suspend fun suspendNothingBlockTail(n: Int): String {
    val a = suspendNothingRelay(n)
    return if (a >= 0) "ok" else { suspendNothingLog.add("side"); suspendNothingFail("tail") }
}
suspend fun suspendNothingSubjectWhen(n: Int): String {
    val a = suspendNothingRelay(n)
    return when (a) { 0 -> "zero"; 1 -> suspendNothingFail("one"); 2 -> SuspendNothingBoom.boom(); else -> "many" }
}
suspend fun suspendNothingValueSlot(n: Int): Int {
    val a = suspendNothingRelay(n)
    return if (a >= 0) 7 else suspendNothingFail("int")
}
suspend fun suspendNothingWholeBody(n: Int): String { suspendNothingRelay(n); return suspendNothingFail("body") }
suspend fun suspendNothingNestedArgument(n: Int): String {
    val a = suspendNothingRelay(n)
    return "p".plus(if (a >= 0) "q" else suspendNothingFail("arg"))
}
suspend fun suspendNothingTryArm(n: Int): String {
    val a = suspendNothingRelay(n)
    return try { if (a >= 0) "t" else suspendNothingFail("try") } catch (e: IllegalStateException) { "caught" }
}

class SuspendNothingValueTests {
    private fun thrown(block: () -> Unit): String =
        try { block(); "NO THROW" } catch (e: IllegalStateException) { e.message ?: "no message" }

    @TestAttribute
    fun nothingReturningCallInSuspendValuePosition() {
        suspendNothingLog.clear()
        // The surviving arms.
        assertEquals("kept", blockOn { suspendNothingSuspendingArm(1) })   // kept
        assertEquals("e", blockOn { suspendNothingElvis("e") })            // e
        assertEquals("ok", blockOn { suspendNothingBlockTail(1) })         // ok
        assertEquals("zero", blockOn { suspendNothingSubjectWhen(0) })     // zero   `when` WITH a subject
        assertEquals("many", blockOn { suspendNothingSubjectWhen(9) })     // many
        assertEquals(7, blockOn { suspendNothingValueSlot(1) })            // 7      value-typed merge
        assertEquals("pq", blockOn { suspendNothingNestedArgument(1) })    // pq     terminated arm nested in an argument
        assertEquals("t", blockOn { suspendNothingTryArm(1) })             // t
        assertEquals(0, suspendNothingLog.size)                            // the untaken block arm did not run
        // The terminated arms: each still throws ITS OWN exception, out through the state machine.
        assertEquals("a", thrown { blockOn { suspendNothingBothArms(1) } })          // BOTH arms Nothing
        assertEquals("b", thrown { blockOn { suspendNothingBothArms(-1) } })
        assertEquals("s", thrown { blockOn { suspendNothingSuspendingArm(-1) } })    // the Nothing callee itself suspends
        assertEquals("elvis", thrown { blockOn { suspendNothingElvis(null) } })
        assertEquals("tail", thrown { blockOn { suspendNothingBlockTail(-1) } })
        assertEquals(1, suspendNothingLog.size)                            // ...and the taken block arm ran its statements
        assertEquals("one", thrown { blockOn { suspendNothingSubjectWhen(1) } })
        assertEquals("boom", thrown { blockOn { suspendNothingSubjectWhen(2) } })    // companion-static producer
        assertEquals("int", thrown { blockOn { suspendNothingValueSlot(-1) } })
        assertEquals("body", thrown { blockOn { suspendNothingWholeBody(1) } })
        assertEquals("arg", thrown { blockOn { suspendNothingNestedArgument(-1) } })
        assertEquals("caught", blockOn { suspendNothingTryArm(-1) })       // the arm throws INSIDE the try, so the catch wins
    }
}
