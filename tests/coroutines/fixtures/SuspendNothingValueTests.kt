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
// companion-static producer. Top-level names carry the `corNv` prefix.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

suspend fun corNvRelay(x: Int): Int = x
fun corNvFail(msg: String): Nothing = throw IllegalStateException(msg)
suspend fun corNvSuspendFail(msg: String): Nothing = throw IllegalStateException(msg)
class CorNvBoom { companion object { fun boom(): Nothing = throw IllegalStateException("boom") } }

val corNvLog = mutableListOf<String>()

suspend fun corNvBothArms(n: Int): String {
    val a = corNvRelay(n)
    return if (a >= 0) corNvFail("a") else corNvFail("b")
}
suspend fun corNvSuspendingArm(n: Int): String {
    val a = corNvRelay(n)
    return if (a >= 0) "kept" else corNvSuspendFail("s")
}
suspend fun corNvElvis(s: String?): String {
    val a = corNvRelay(1)
    return if (a >= 0) (s ?: corNvFail("elvis")) else "x"
}
suspend fun corNvBlockTail(n: Int): String {
    val a = corNvRelay(n)
    return if (a >= 0) "ok" else { corNvLog.add("side"); corNvFail("tail") }
}
suspend fun corNvSubjectWhen(n: Int): String {
    val a = corNvRelay(n)
    return when (a) { 0 -> "zero"; 1 -> corNvFail("one"); 2 -> CorNvBoom.boom(); else -> "many" }
}
suspend fun corNvValueSlot(n: Int): Int {
    val a = corNvRelay(n)
    return if (a >= 0) 7 else corNvFail("int")
}
suspend fun corNvWholeBody(n: Int): String { corNvRelay(n); return corNvFail("body") }
suspend fun corNvNestedArgument(n: Int): String {
    val a = corNvRelay(n)
    return "p".plus(if (a >= 0) "q" else corNvFail("arg"))
}
suspend fun corNvTryArm(n: Int): String {
    val a = corNvRelay(n)
    return try { if (a >= 0) "t" else corNvFail("try") } catch (e: IllegalStateException) { "caught" }
}

class SuspendNothingValueTests {
    private fun thrown(block: () -> Unit): String =
        try { block(); "NO THROW" } catch (e: IllegalStateException) { e.message ?: "no message" }

    @TestAttribute
    fun nothingReturningCallInSuspendValuePosition() {
        corNvLog.clear()
        // The surviving arms.
        assertEquals("kept", blockOn { corNvSuspendingArm(1) })   // kept
        assertEquals("e", blockOn { corNvElvis("e") })            // e
        assertEquals("ok", blockOn { corNvBlockTail(1) })         // ok
        assertEquals("zero", blockOn { corNvSubjectWhen(0) })     // zero   `when` WITH a subject
        assertEquals("many", blockOn { corNvSubjectWhen(9) })     // many
        assertEquals(7, blockOn { corNvValueSlot(1) })            // 7      value-typed merge
        assertEquals("pq", blockOn { corNvNestedArgument(1) })    // pq     terminated arm nested in an argument
        assertEquals("t", blockOn { corNvTryArm(1) })             // t
        assertEquals(0, corNvLog.size)                            // the untaken block arm did not run
        // The terminated arms: each still throws ITS OWN exception, out through the state machine.
        assertEquals("a", thrown { blockOn { corNvBothArms(1) } })          // BOTH arms Nothing
        assertEquals("b", thrown { blockOn { corNvBothArms(-1) } })
        assertEquals("s", thrown { blockOn { corNvSuspendingArm(-1) } })    // the Nothing callee itself suspends
        assertEquals("elvis", thrown { blockOn { corNvElvis(null) } })
        assertEquals("tail", thrown { blockOn { corNvBlockTail(-1) } })
        assertEquals(1, corNvLog.size)                            // ...and the taken block arm ran its statements
        assertEquals("one", thrown { blockOn { corNvSubjectWhen(1) } })
        assertEquals("boom", thrown { blockOn { corNvSubjectWhen(2) } })    // companion-static producer
        assertEquals("int", thrown { blockOn { corNvValueSlot(-1) } })
        assertEquals("body", thrown { blockOn { corNvWholeBody(1) } })
        assertEquals("arg", thrown { blockOn { corNvNestedArgument(-1) } })
        assertEquals("caught", blockOn { corNvTryArm(-1) })       // the arm throws INSIDE the try, so the catch wins
    }
}
