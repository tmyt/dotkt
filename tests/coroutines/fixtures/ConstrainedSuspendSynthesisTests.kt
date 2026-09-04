import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import dotkt.support.blockOn
import kotlin.coroutines.resume
import kotlin.coroutines.suspendCoroutine

// Regression distilled from kotlinx.coroutines Flow.toCollection:
// a constrained generic suspend method moves its body into a synthesized CLR state-machine type, and that body
// constructs a generic SAM which captures C and calls MutableCollection<T>.add through the captured field.
// The SM must own the moved T/C variables and their C : MutableCollection<T> constraint; callee-relative `sig`
// variables inside the moved body remain method-scoped.
fun interface ConstrainedSuspendSink<T> {
    suspend fun emit(value: T)
}

suspend fun <T, C : MutableCollection<T>> constrainedSuspendCollectInto(value: T, destination: C): C {
    suspendCoroutine<Unit> { continuation -> continuation.resume(Unit) }
    val sink = ConstrainedSuspendSink<T> { item -> destination.add(item) }
    sink.emit(value)
    return destination
}

// Regression distilled from protected member reads in lifted coroutine state machines. Widening the base slot for
// the synthesized sibling must carry through local overrides, because CLR rejects a derived override that reduces
// the widened base member's accessibility.
open class ConstrainedSuspendProtectedBase {
    protected open fun protectedValue(): Int = 10

    suspend fun readProtected(): Int {
        suspendCoroutine<Unit> { continuation -> continuation.resume(Unit) }
        return protectedValue()
    }
}

class ConstrainedSuspendProtectedDerived : ConstrainedSuspendProtectedBase() {
    protected override fun protectedValue(): Int = 42
}

private fun <T> constrainedSuspendReadList(values: List<T>): T = values[0]

suspend fun constrainedSuspendMutableListAcrossSuspension(): String {
    val values = mutableListOf("list-view")
    suspendCoroutine<Unit> { continuation -> continuation.resume(Unit) }
    return constrainedSuspendReadList(values)
}

// A member called on a receiver whose STATIC TYPE IS THE TYPE PARAMETER, inside a GENERIC state machine. The
// suspend lowering spills the receiver into an SM field, so the receiver reaches the emitter as a field load
// rather than as the parameter it was written as — the dispatch is still `constrained. !!T ; callvirt`, because
// the rule is keyed on the receiver's static type and not on how the receiver is spelled. The siblings vary the
// spelling (the parameter, a local copy of it, a `T`-returning call result) and the constraint (a Kotlin
// interface, and `Comparable<T>` instantiated at a CLR VALUE type — which only dispatches without boxing
// through `constrained.`).
interface ConstrainedSuspendTagged {
    fun tag(): Int
}

class ConstrainedSuspendTag(private val n: Int) : ConstrainedSuspendTagged {
    override fun tag(): Int = n
}

class ConstrainedSuspendTagBox<T : ConstrainedSuspendTagged>(private val v: T) {
    fun get(): T = v
}

suspend fun <T : ConstrainedSuspendTagged> constrainedSuspendTagOfParam(t: T): Int {
    suspendCoroutine<Unit> { continuation -> continuation.resume(Unit) }
    return t.tag()
}

suspend fun <T : ConstrainedSuspendTagged> constrainedSuspendTagOfLocal(t: T): Int {
    val copy = t
    suspendCoroutine<Unit> { continuation -> continuation.resume(Unit) }
    return copy.tag()
}

suspend fun <T : ConstrainedSuspendTagged> constrainedSuspendTagOfCallResult(box: ConstrainedSuspendTagBox<T>): Int {
    suspendCoroutine<Unit> { continuation -> continuation.resume(Unit) }
    return box.get().tag()
}

suspend fun <T : Comparable<T>> constrainedSuspendCompareAcrossSuspension(a: T, b: T): Int {
    suspendCoroutine<Unit> { continuation -> continuation.resume(Unit) }
    return a.compareTo(b)
}

// A use-site projection in a generic constraint must survive kotc -> BIR so bir2cir can choose an existential
// physical representation. Erasing `in K`/`in T` to invariant arguments makes the moved state-machine constraint
// `M : MutableMap<K, T>`, which rejects a valid wider destination because IDictionary<K, T> is invariant.
suspend fun <T, K, M : MutableMap<in K, in T>> constrainedSuspendPutProjected(
    key: K,
    value: T,
    destination: M,
): M {
    suspendCoroutine<Unit> { continuation -> continuation.resume(Unit) }
    destination.put(key, value)
    return destination
}

suspend fun <T, C : MutableList<in T>> constrainedSuspendInsertProjected(
    value: T,
    destination: C,
): C {
    suspendCoroutine<Unit> { continuation -> continuation.resume(Unit) }
    destination.add(0, value)
    return destination
}

class ConstrainedSuspendSynthesisTests {
    @TestAttribute
    fun constrainedSamInsideGenericStateMachine() {
        val destination = ArrayList<Int>()
        val result = blockOn { constrainedSuspendCollectInto(42, destination) }
        assertEquals(1, result.size)
        assertEquals(42, result[0])
    }

    @TestAttribute
    fun protectedAccessFromStateMachineKeepsOverrideVisibility() {
        assertEquals(42, blockOn { ConstrainedSuspendProtectedDerived().readProtected() })
    }

    @TestAttribute
    fun mutableCollectionViewIsCoercedAfterSuspension() {
        assertEquals("list-view", blockOn { constrainedSuspendMutableListAcrossSuspension() })
    }

    @TestAttribute
    fun typeParameterReceiverDispatchesInsideStateMachine() {
        val tag = ConstrainedSuspendTag(4)
        assertEquals(4, blockOn { constrainedSuspendTagOfParam(tag) })
        assertEquals(4, blockOn { constrainedSuspendTagOfLocal(tag) })
        assertEquals(4, blockOn { constrainedSuspendTagOfCallResult(ConstrainedSuspendTagBox(tag)) })
    }

    @TestAttribute
    fun valueTypeTypeParameterReceiverDispatchesInsideStateMachine() {
        // T = Int is a CLR struct: `constrained. !!T ; callvirt IComparable<T>::CompareTo` dispatches to
        // Int32's own implementation with no box, and the same emitted method serves the reference case.
        assertEquals(1, blockOn { constrainedSuspendCompareAcrossSuspension(3, 1) })
        assertEquals(-1, blockOn { constrainedSuspendCompareAcrossSuspension(1, 3) })
        assertEquals(0, blockOn { constrainedSuspendCompareAcrossSuspension(2, 2) })
        // The reference instantiation asserts the SIGN only — Comparable's contract fixes that, not the magnitude.
        assertEquals(true, blockOn { constrainedSuspendCompareAcrossSuspension("b", "a") } > 0)
    }

    @TestAttribute
    fun useSiteProjectedConstraintSurvivesStateMachineSynthesis() {
        val destination = mutableMapOf<Any?, Any?>()
        val result = blockOn { constrainedSuspendPutProjected("projected", 23, destination) }
        assertEquals(true, result === destination)

        val list = mutableListOf<Any>("tail")
        val listResult = blockOn { constrainedSuspendInsertProjected("head", list) }
        assertEquals(true, listResult === list)
        assertEquals("head", list[0])
    }
}
