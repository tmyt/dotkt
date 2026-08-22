// feature fixture — il-flowtransform: the cold-SM NESTED-closure capture family (rc6 #75 holistic, the kotlinx.coroutines
// `flow{}` port blocker). A self-contained mini cold Flow reproducing the EXACT `unsafeFlow`/`unsafeTransform`/`filter`
// shapes (E1-E7). It is isolated in a dedicated package because this fixture deliberately exercises a deep chain of
// same-module inline carriers and suspend state machines. The parameter below uses a non-original name to guard against
// the former name-sensitive spill bug (#200): spill ownership must follow declarations, never incidental local names.
// Driven by the shared `dotkt.support.blockOn`
// harness; the former `main` + stdout-golden becomes one @TestAttribute method preserving every value 1:1.
package corb.flowtransform

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import dotkt.support.blockOn

fun interface FlowTransformationFlowCollector<T> {
    suspend fun emit(value: T)
}

interface FlowTransformationFlow<T> {
    suspend fun collect(collector: FlowTransformationFlowCollector<T>)
}

inline fun <T> flowTransformationUnsafeFlow(crossinline block: suspend FlowTransformationFlowCollector<T>.() -> Unit): FlowTransformationFlow<T> =
    object : FlowTransformationFlow<T> {
        override suspend fun collect(collector: FlowTransformationFlowCollector<T>) { collector.block() }
    }

suspend inline fun <T> FlowTransformationFlow<T>.flowTransformationCollect(crossinline action: suspend (T) -> Unit): Unit =
    collect(FlowTransformationFlowCollector { value -> action(value) })

inline fun <T, R> FlowTransformationFlow<T>.flowTransformationUnsafeTransform(
    crossinline transform: suspend FlowTransformationFlowCollector<R>.(T) -> Unit
): FlowTransformationFlow<R> = flowTransformationUnsafeFlow { flowTransformationCollect { value -> transform(value) } }

inline fun <T, R> FlowTransformationFlow<T>.flowTransformationTransform(
    crossinline transform: suspend FlowTransformationFlowCollector<R>.(T) -> Unit
): FlowTransformationFlow<R> = flowTransformationUnsafeTransform(transform)

inline fun <T> FlowTransformationFlow<T>.flowTransformationFilter(crossinline predicate: suspend (T) -> Boolean): FlowTransformationFlow<T> = flowTransformationTransform { value ->
    if (predicate(value)) emit(value)
}

inline fun <reified R> FlowTransformationFlow<Any?>.flowTransformationFilterIsInstance(): FlowTransformationFlow<R> =
    flowTransformationTransform<Any?, R> { value -> if (value is R) emit(value) }

inline fun <A, B, C, reified R> FlowTransformationFlow<Any?>.flowTransformationSparseFilterIsInstance(
    unusedA: A, unusedB: B, unusedC: C
): FlowTransformationFlow<R> =
    flowTransformationTransform<Any?, R> { value -> if (value is R) emit(value) }

inline fun <T, R> FlowTransformationFlow<T>.flowTransformationMap(crossinline mapper: suspend (T) -> R): FlowTransformationFlow<R> = flowTransformationTransform { value ->
    emit(mapper(value))
}

class FlowTransformationDivisor(private val by: Int) {
    fun accepts(value: Int): Boolean = value % by == 0
}

fun FlowTransformationFlow<Int>.flowTransformationFilterDivisibleBy(box: FlowTransformationDivisor): FlowTransformationFlow<Int> = flowTransformationFilter { box.accepts(it) }

fun flowTransformationFlowOf(a: Int, b: Int, c: Int, d: Int, e: Int, f: Int): FlowTransformationFlow<Int> = flowTransformationUnsafeFlow {
    emit(a); emit(b); emit(c); emit(d); emit(e); emit(f)
}

fun <T> flowTransformationFlowOf3(a: T, b: T, c: T): FlowTransformationFlow<T> = flowTransformationUnsafeFlow {
    emit(a); emit(b); emit(c)
}

suspend fun flowTransformationCollectSum(flow: FlowTransformationFlow<Int>): Int {
    val items = ArrayList<Int>()
    flow.flowTransformationCollect { items.add(it) }
    var sum = 0
    for (x in items) sum += x
    return sum
}

suspend fun <T> flowTransformationCollectCount(flow: FlowTransformationFlow<T>): Int {
    var count = 0
    flow.flowTransformationCollect { count++ }
    return count
}

class FlowTransformationTests {
    @TestAttribute
    fun nestedCrossinlineCaptureChain() {
        val base = flowTransformationFlowOf(1, 2, 3, 4, 5, 6)
        assertEquals(12, blockOn { flowTransformationCollectSum(base.flowTransformationFilter { it % 2 == 0 }) })                    // 2+4+6 = 12
        assertEquals(210, blockOn { flowTransformationCollectSum(base.flowTransformationMap { it * 10 }) })                         // 210
        assertEquals(9, blockOn { flowTransformationCollectSum(base.flowTransformationFilterDivisibleBy(FlowTransformationDivisor(3))) })        // 3+6 = 9
        val mixed: FlowTransformationFlow<Any?> = flowTransformationFlowOf3(1, "skip", 2)
        assertEquals(3, blockOn { flowTransformationCollectSum(mixed.flowTransformationFilterIsInstance<Int>()) })
        val nullableMixed: FlowTransformationFlow<Any?> = flowTransformationFlowOf3(null, "keep", 1)
        assertEquals(2, blockOn { flowTransformationCollectCount(nullableMixed.flowTransformationFilterIsInstance<String?>()) })
        val nullAndInt: FlowTransformationFlow<Any?> = flowTransformationFlowOf3(null, 1, "skip")
        assertEquals(1, blockOn { flowTransformationCollectCount(nullAndInt.flowTransformationFilterIsInstance<Int>()) })
        assertEquals(1, blockOn {
            flowTransformationCollectCount(
                nullAndInt.flowTransformationSparseFilterIsInstance<Int, String, Boolean, Int>(0, "", false)
            )
        })
    }
}
