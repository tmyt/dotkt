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

// A member called on a receiver whose STATIC TYPE IS THE TYPE PARAMETER, inside a GENERIC state machine. The
// suspend lowering spills the receiver into an SM field, so the receiver reaches the emitter as a field load
// rather than as the parameter it was written as — the dispatch is still `constrained. !!T ; callvirt`, because
// the rule is keyed on the receiver's static type and not on how the receiver is spelled. The siblings vary the
// spelling (the parameter, a local copy of it, a `T`-returning call result) and the constraint (a Kotlin
// interface, and `Comparable<T>` instantiated at a CLR VALUE type — which only dispatches without boxing
// through `constrained.`).
interface CorBTagged {
    fun tag(): Int
}

class CorBTag(private val n: Int) : CorBTagged {
    override fun tag(): Int = n
}

class CorBTagBox<T : CorBTagged>(private val v: T) {
    fun get(): T = v
}

suspend fun <T : CorBTagged> corBTagOfParam(t: T): Int {
    suspendCoroutine<Unit> { continuation -> continuation.resume(Unit) }
    return t.tag()
}

suspend fun <T : CorBTagged> corBTagOfLocal(t: T): Int {
    val copy = t
    suspendCoroutine<Unit> { continuation -> continuation.resume(Unit) }
    return copy.tag()
}

suspend fun <T : CorBTagged> corBTagOfCallResult(box: CorBTagBox<T>): Int {
    suspendCoroutine<Unit> { continuation -> continuation.resume(Unit) }
    return box.get().tag()
}

suspend fun <T : Comparable<T>> corBCompareAcrossSuspension(a: T, b: T): Int {
    suspendCoroutine<Unit> { continuation -> continuation.resume(Unit) }
    return a.compareTo(b)
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

    @TestAttribute
    fun typeParameterReceiverDispatchesInsideStateMachine() {
        val tag = CorBTag(4)
        assertEquals(4, blockOn { corBTagOfParam(tag) })
        assertEquals(4, blockOn { corBTagOfLocal(tag) })
        assertEquals(4, blockOn { corBTagOfCallResult(CorBTagBox(tag)) })
    }

    @TestAttribute
    fun valueTypeTypeParameterReceiverDispatchesInsideStateMachine() {
        // T = Int is a CLR struct: `constrained. !!T ; callvirt IComparable<T>::CompareTo` dispatches to
        // Int32's own implementation with no box, and the same emitted method serves the reference case.
        assertEquals(1, blockOn { corBCompareAcrossSuspension(3, 1) })
        assertEquals(-1, blockOn { corBCompareAcrossSuspension(1, 3) })
        assertEquals(0, blockOn { corBCompareAcrossSuspension(2, 2) })
        // The reference instantiation asserts the SIGN only — Comparable's contract fixes that, not the magnitude.
        assertEquals(true, blockOn { corBCompareAcrossSuspension("b", "a") } > 0)
    }
}
