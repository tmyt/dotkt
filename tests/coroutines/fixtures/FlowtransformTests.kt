// CorB batch — il-flowtransform: the cold-SM NESTED-closure capture family (rc6 #75 holistic, the kotlinx.coroutines
// `flow{}` port blocker). A self-contained mini cold Flow reproducing the EXACT `unsafeFlow`/`unsafeTransform`/`filter`
// shapes (E1-E7). Its declarations keep their ORIGINAL (unprefixed) names inside a DEDICATED PACKAGE `corb.flowtransform`:
// bir2cir's #82 inline-splice spill pass is sensitive to the synthesized-SM name ordering — renaming the inline chain
// (e.g. `filter`->`corBFtFilter`) trips a latent spill bug ("references unspilled local 'predicate'") that the original
// names do not, so this case is migrated VERBATIM (its very point is the named-shape regression guard) and isolated in
// its own package to avoid clashing with the assembly's other fixtures. Driven by the shared `dotkt.support.blockOn`
// harness; the former `main` + stdout-golden becomes one @TestAttribute method preserving every value 1:1.
package corb.flowtransform

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

fun interface FlowCollector<T> {
    suspend fun emit(value: T)
}

interface Flow<T> {
    suspend fun collect(collector: FlowCollector<T>)
}

inline fun <T> unsafeFlow(crossinline block: suspend FlowCollector<T>.() -> Unit): Flow<T> =
    object : Flow<T> {
        override suspend fun collect(collector: FlowCollector<T>) { collector.block() }
    }

suspend inline fun <T> Flow<T>.collect(crossinline action: suspend (T) -> Unit): Unit =
    collect(FlowCollector { value -> action(value) })

inline fun <T, R> Flow<T>.unsafeTransform(
    crossinline transform: suspend FlowCollector<R>.(T) -> Unit
): Flow<R> = unsafeFlow { collect { value -> transform(value) } }

inline fun <T, R> Flow<T>.transform(
    crossinline transform: suspend FlowCollector<R>.(T) -> Unit
): Flow<R> = unsafeTransform(transform)

inline fun <T> Flow<T>.filter(crossinline predicate: suspend (T) -> Boolean): Flow<T> = transform { value ->
    if (predicate(value)) emit(value)
}

inline fun <T, R> Flow<T>.map(crossinline mapper: suspend (T) -> R): Flow<R> = transform { value ->
    emit(mapper(value))
}

class Divisor(private val by: Int) {
    fun accepts(value: Int): Boolean = value % by == 0
}

fun Flow<Int>.filterDivisibleBy(box: Divisor): Flow<Int> = filter { box.accepts(it) }

fun flowOf(a: Int, b: Int, c: Int, d: Int, e: Int, f: Int): Flow<Int> = unsafeFlow {
    emit(a); emit(b); emit(c); emit(d); emit(e); emit(f)
}

suspend fun collectSum(flow: Flow<Int>): Int {
    val items = ArrayList<Int>()
    flow.collect { items.add(it) }
    var sum = 0
    for (x in items) sum += x
    return sum
}

class CorBFlowTransformTests {
    @TestAttribute
    fun flowtransform_nestedCrossinlineCaptureChain() {
        val base = flowOf(1, 2, 3, 4, 5, 6)
        assertEquals(12, blockOn { collectSum(base.filter { it % 2 == 0 }) })            // 2+4+6 = 12
        assertEquals(210, blockOn { collectSum(base.map { it * 10 }) })                  // 210
        assertEquals(9, blockOn { collectSum(base.filterDivisibleBy(Divisor(3))) })      // 3+6 = 9
    }
}
