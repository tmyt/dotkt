// feature fixture — il-flowtransform: the cold-SM NESTED-closure capture family (rc6 #75 holistic, the kotlinx.coroutines
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

fun interface FlowTransformationFtFlowCollector<T> {
    suspend fun emit(value: T)
}

interface FlowTransformationFtFlow<T> {
    suspend fun collect(collector: FlowTransformationFtFlowCollector<T>)
}

inline fun <T> flowTransformationFtUnsafeFlow(crossinline block: suspend FlowTransformationFtFlowCollector<T>.() -> Unit): FlowTransformationFtFlow<T> =
    object : FlowTransformationFtFlow<T> {
        override suspend fun collect(collector: FlowTransformationFtFlowCollector<T>) { collector.block() }
    }

suspend inline fun <T> FlowTransformationFtFlow<T>.flowTransformationFtCollect(crossinline action: suspend (T) -> Unit): Unit =
    collect(FlowTransformationFtFlowCollector { value -> action(value) })

inline fun <T, R> FlowTransformationFtFlow<T>.flowTransformationFtUnsafeTransform(
    crossinline transform: suspend FlowTransformationFtFlowCollector<R>.(T) -> Unit
): FlowTransformationFtFlow<R> = flowTransformationFtUnsafeFlow { flowTransformationFtCollect { value -> transform(value) } }

inline fun <T, R> FlowTransformationFtFlow<T>.flowTransformationFtTransform(
    crossinline transform: suspend FlowTransformationFtFlowCollector<R>.(T) -> Unit
): FlowTransformationFtFlow<R> = flowTransformationFtUnsafeTransform(transform)

inline fun <T> FlowTransformationFtFlow<T>.flowTransformationFtFilter(crossinline predicate: suspend (T) -> Boolean): FlowTransformationFtFlow<T> = flowTransformationFtTransform { value ->
    if (predicate(value)) emit(value)
}

inline fun <T, R> FlowTransformationFtFlow<T>.flowTransformationFtMap(crossinline mapper: suspend (T) -> R): FlowTransformationFtFlow<R> = flowTransformationFtTransform { value ->
    emit(mapper(value))
}

class FlowTransformationFtDivisor(private val by: Int) {
    fun accepts(value: Int): Boolean = value % by == 0
}

fun FlowTransformationFtFlow<Int>.flowTransformationFtFilterDivisibleBy(box: FlowTransformationFtDivisor): FlowTransformationFtFlow<Int> = flowTransformationFtFilter { box.accepts(it) }

fun flowTransformationFtFlowOf(a: Int, b: Int, c: Int, d: Int, e: Int, f: Int): FlowTransformationFtFlow<Int> = flowTransformationFtUnsafeFlow {
    emit(a); emit(b); emit(c); emit(d); emit(e); emit(f)
}

suspend fun flowTransformationFtCollectSum(flow: FlowTransformationFtFlow<Int>): Int {
    val items = ArrayList<Int>()
    flow.flowTransformationFtCollect { items.add(it) }
    var sum = 0
    for (x in items) sum += x
    return sum
}

class FlowTransformationTests {
    @TestAttribute
    fun nestedCrossinlineCaptureChain() {
        val base = flowTransformationFtFlowOf(1, 2, 3, 4, 5, 6)
        assertEquals(12, blockOn { flowTransformationFtCollectSum(base.flowTransformationFtFilter { it % 2 == 0 }) })                    // 2+4+6 = 12
        assertEquals(210, blockOn { flowTransformationFtCollectSum(base.flowTransformationFtMap { it * 10 }) })                         // 210
        assertEquals(9, blockOn { flowTransformationFtCollectSum(base.flowTransformationFtFilterDivisibleBy(FlowTransformationFtDivisor(3))) })        // 3+6 = 9
    }
}
