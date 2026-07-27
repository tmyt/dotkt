// Suspend + CONTEXT-PARAMETER battery. A context parameter is projected as an ordinary POSITIONAL value parameter
// (after the `__self` extension receiver, before the regular parameters), and the suspend lowering has to carry
// that parameter into the state machine's frame like any other: before the rule was applied on both sides of a
// call, a `suspend` context function's call site emitted a short argument list and the emitted program failed to
// verify. The genuinely-suspending case additionally pins the context value ACROSS a suspension point (it is
// spilled into the SM like any other parameter, not re-read from a scope that no longer exists).
//
// Driven by the shared `dotkt.support.blockOn` harness; top-level names are family-prefixed (`sctx`).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Threading.Tasks.Task
import kotlin.clr.await
import dotkt.support.blockOn

class SctxScale(val factor: Int)

context(s: SctxScale)
suspend fun sctxScaled(a: Int): Int = a * s.factor

// The context parameter is read AFTER a real suspension point, so it must survive the state-machine spill.
context(s: SctxScale)
suspend fun sctxAcrossSuspension(a: Int): Int {
    Task.Delay(1).await()
    return a * s.factor
}

// A suspend context function calling another one: the callee's context argument comes from the caller's own
// context parameter, inside a state machine.
context(s: SctxScale)
suspend fun sctxChained(a: Int): Int = sctxScaled(a) + s.factor

class SctxHolder(val base: Int) {
    context(s: SctxScale)
    suspend fun combine(a: Int): Int = base + a * s.factor
}

class SuspendContextParameterTests {
    @TestAttribute
    fun suspendContextParameters() {
        assertEquals(50, blockOn { with(SctxScale(10)) { sctxScaled(5) } })          // 50
        assertEquals(60, blockOn { with(SctxScale(10)) { sctxChained(5) } })         // 60  50 + s.factor
        assertEquals(70, blockOn { with(SctxScale(10)) { SctxHolder(20).combine(5) } }) // 70  a suspend MEMBER
    }

    @TestAttribute
    fun suspendContextParameterSurvivesSuspension() {
        assertEquals(50, blockOn { with(SctxScale(10)) { sctxAcrossSuspension(5) } })   // 50
    }
}
