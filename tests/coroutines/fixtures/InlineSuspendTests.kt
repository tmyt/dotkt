// CorB batch — il-inline-suspend: a SUSPEND call inside a NON-suspend-typed inline-arg lambda (#75 S4a §8.7 arm-c).
// `1.let { tick() }` holds a `tick()` suspend call while `let`'s lambda is NOT suspend-typed — legal only because
// inline expansion puts the call in work()'s suspend frame. All top-level decls carry the `insp` case token under the
// shared `corB`/`CorB` prefix so their (top-level suspend fun) simple names are UNIQUE across this single assembly —
// bir2cir's cold-core suspend lowering keys top-level suspend funs by simple name, so a shared name across cases
// collides ("reached codegen un-lowered"); the pilots prefix for the same reason. Driven by the shared
// `dotkt.support.blockOn` harness; the former `main` + golden -> one @TestAttribute method (value 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Threading.Tasks.Task
import dotkt.support.blockOn

suspend fun corBInspTick() { Task.Delay(1).await() }   // a real .NET-async suspension point

suspend fun corBInspWork(): Int {
    var n = 0
    for (i in 1..2) {
        corBInspTick()
        n += 10
    }
    1.let {
        corBInspTick()
        n += it
    }
    return n
}

class InlineSuspendTests {
    @TestAttribute
    fun suspendCallInNonSuspendInlineArgLambda() {
        assertEquals(21, blockOn { corBInspWork() })   // 10 + 10 + 1 = 21
    }
}
