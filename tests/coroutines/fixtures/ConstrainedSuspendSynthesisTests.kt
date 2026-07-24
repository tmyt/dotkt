import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn
import kotlin.coroutines.resume
import kotlin.coroutines.suspendCoroutine

// Regression distilled from kotlinx.coroutines Flow.toCollection:
// a constrained generic suspend method moves its body into a synthesized CLR state-machine type, and that body
// constructs a generic SAM which captures C and calls MutableCollection<T>.add through the captured field.
// The SM must own the moved T/C variables and their C : MutableCollection<T> constraint; callee-relative `sig`
// variables inside the moved body remain method-scoped.
fun interface CorBConstrainedSink<T> {
    suspend fun emit(value: T)
}

suspend fun <T, C : MutableCollection<T>> corBCollectInto(value: T, destination: C): C {
    suspendCoroutine<Unit> { continuation -> continuation.resume(Unit) }
    val sink = CorBConstrainedSink<T> { item -> destination.add(item) }
    sink.emit(value)
    return destination
}

// Regression distilled from protected member reads in lifted coroutine state machines. Widening the base slot for
// the synthesized sibling must carry through local overrides, because CLR rejects a derived override that reduces
// the widened base member's accessibility.
open class CorBProtectedBase {
    protected open fun protectedValue(): Int = 10

    suspend fun readProtected(): Int {
        suspendCoroutine<Unit> { continuation -> continuation.resume(Unit) }
        return protectedValue()
    }
}

class CorBProtectedDerived : CorBProtectedBase() {
    protected override fun protectedValue(): Int = 42
}

private fun <T> corBReadList(values: List<T>): T = values[0]

suspend fun corBMutableListAcrossSuspension(): String {
    val values = mutableListOf("list-view")
    suspendCoroutine<Unit> { continuation -> continuation.resume(Unit) }
    return corBReadList(values)
}

class ConstrainedSuspendSynthesisTests {
    @TestAttribute
    fun constrainedSamInsideGenericStateMachine() {
        val destination = ArrayList<Int>()
        val result = blockOn { corBCollectInto(42, destination) }
        assertEquals(1, result.size)
        assertEquals(42, result[0])
    }

    @TestAttribute
    fun protectedAccessFromStateMachineKeepsOverrideVisibility() {
        assertEquals(42, blockOn { CorBProtectedDerived().readProtected() })
    }

    @TestAttribute
    fun mutableCollectionViewIsCoercedAfterSuspension() {
        assertEquals("list-view", blockOn { corBMutableListAcrossSuspension() })
    }
}
