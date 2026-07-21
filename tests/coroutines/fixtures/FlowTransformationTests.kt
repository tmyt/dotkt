// CorB batch — il-flowtransform: the cold-SM NESTED-closure capture family (rc6 #75 holistic, the kotlinx.coroutines
// `flow{}` port blocker). A self-contained mini cold Flow reproducing the EXACT `unsafeFlow`/`unsafeTransform`/`filter`
// shapes (E1-E7). It is isolated in a dedicated package because this fixture deliberately exercises a deep chain of
// same-module inline carriers and suspend state machines. The parameter below uses a non-original name to guard against
// the former name-sensitive spill bug (#200): spill ownership must follow declarations, never incidental local names.
// Driven by the shared `dotkt.support.blockOn`
// harness; the former `main` + stdout-golden becomes one @TestAttribute method preserving every value 1:1.
package corb.flowtransform

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

fun interface CorBFtFlowCollector<T> {
    suspend fun emit(value: T)
}

interface CorBFtFlow<T> {
    suspend fun collect(collector: CorBFtFlowCollector<T>)
}

inline fun <T> corBFtUnsafeFlow(crossinline block: suspend CorBFtFlowCollector<T>.() -> Unit): CorBFtFlow<T> =
    object : CorBFtFlow<T> {
        override suspend fun collect(collector: CorBFtFlowCollector<T>) { collector.block() }
    }

suspend inline fun <T> CorBFtFlow<T>.corBFtCollect(crossinline action: suspend (T) -> Unit): Unit =
    collect(CorBFtFlowCollector { value -> action(value) })

inline fun <T, R> CorBFtFlow<T>.corBFtUnsafeTransform(
    crossinline transform: suspend CorBFtFlowCollector<R>.(T) -> Unit
): CorBFtFlow<R> = corBFtUnsafeFlow { corBFtCollect { value -> transform(value) } }

inline fun <T, R> CorBFtFlow<T>.corBFtTransform(
    crossinline transform: suspend CorBFtFlowCollector<R>.(T) -> Unit
): CorBFtFlow<R> = corBFtUnsafeTransform(transform)

inline fun <T> CorBFtFlow<T>.corBFtFilter(crossinline predicate: suspend (T) -> Boolean): CorBFtFlow<T> = corBFtTransform { value ->
    if (predicate(value)) emit(value)
}

inline fun <T, R> CorBFtFlow<T>.corBFtMap(crossinline mapper: suspend (T) -> R): CorBFtFlow<R> = corBFtTransform { value ->
    emit(mapper(value))
}

class CorBFtDivisor(private val by: Int) {
    fun accepts(value: Int): Boolean = value % by == 0
}

fun CorBFtFlow<Int>.corBFtFilterDivisibleBy(box: CorBFtDivisor): CorBFtFlow<Int> = corBFtFilter { box.accepts(it) }

fun corBFtFlowOf(a: Int, b: Int, c: Int, d: Int, e: Int, f: Int): CorBFtFlow<Int> = corBFtUnsafeFlow {
    emit(a); emit(b); emit(c); emit(d); emit(e); emit(f)
}

suspend fun corBFtCollectSum(flow: CorBFtFlow<Int>): Int {
    val items = ArrayList<Int>()
    flow.corBFtCollect { items.add(it) }
    var sum = 0
    for (x in items) sum += x
    return sum
}

class FlowTransformationTests {
    @TestAttribute
    fun nestedCrossinlineCaptureChain() {
        val base = corBFtFlowOf(1, 2, 3, 4, 5, 6)
        assertEquals(12, blockOn { corBFtCollectSum(base.corBFtFilter { it % 2 == 0 }) })                    // 2+4+6 = 12
        assertEquals(210, blockOn { corBFtCollectSum(base.corBFtMap { it * 10 }) })                         // 210
        assertEquals(9, blockOn { corBFtCollectSum(base.corBFtFilterDivisibleBy(CorBFtDivisor(3))) })        // 3+6 = 9
    }
}
