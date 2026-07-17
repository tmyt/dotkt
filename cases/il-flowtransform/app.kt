// rc6 (#75 holistic) — the cold-SM NESTED-closure capture family that blocked the kotlinx.coroutines flow
// port. A self-contained mini cold Flow reproducing the exact `unsafeFlow`/`unsafeTransform`/`filter` shapes,
// driven end-to-end by blockOn (must print the exact sums). Exercises the whole unified fix:
//  - E1/E3/E4: the nested `newSuspendLambda`/SAM inside `unsafeFlow { collect { … } }` captures the
//    inline-renamed FlowCollector receiver (`$this$unsafeFlow` -> `__recvN` via selfSubst) AND `transform`
//    (a `{k:field}`/lambda-param capture). One-frame-one-name body shadow + the capValues one-value-channel,
//    resolved through the cold SM's field vocabulary (RewriteNoSpill) and descended by the NSL renamers.
//  - E2: `FlowCollector { … }` is a SUSPEND fun-interface SAM -> `mods.suspend`, so its `emit` body
//    cold-transforms (else its raw suspendCall body dangles in ilemit — the 33-SAM seam).
//  - E5/E6: `filter`'s crossinline `predicate` is INVOKED inside `transform`'s carrier then spliced away —
//    the inlineLambda capture-descriptor lockstep rename + the §4.4iii dead-capture prune.
//  - E7: `filterDivisibleBy`'s predicate CAPTURES a value (`box`) whose method it invokes — a spliced
//    carrier's OWN capture propagated to the host carrier so it does not dangle in the lifted SM.
import dotkt.support.blockOn

fun interface FlowCollector<T> {
    suspend fun emit(value: T)
}

interface Flow<T> {
    suspend fun collect(collector: FlowCollector<T>)
}

// unsafeFlow: an inline fn whose crossinline SUSPEND RECEIVER lambda is captured into an object literal (the
// receiver `$this$unsafeFlow` is inline-renamed to a fresh `__recvN` local via selfSubst).
inline fun <T> unsafeFlow(crossinline block: suspend FlowCollector<T>.() -> Unit): Flow<T> =
    object : Flow<T> {
        override suspend fun collect(collector: FlowCollector<T>) { collector.block() }
    }

// The suspend fun-interface SAM conversion (E2): `FlowCollector { value -> action(value) }` captures `action`.
suspend inline fun <T> Flow<T>.collect(crossinline action: suspend (T) -> Unit): Unit =
    collect(FlowCollector { value -> action(value) })

// The NESTED inline splice: unsafeFlow { collect { value -> transform(value) } }. The collect SAM captures
// `transform` ({k:field}) AND the unsafeFlow receiver (inline-renamed) — the A/B manifestation.
inline fun <T, R> Flow<T>.unsafeTransform(
    crossinline transform: suspend FlowCollector<R>.(T) -> Unit
): Flow<R> = unsafeFlow { collect { value -> transform(value) } }

inline fun <T, R> Flow<T>.transform(
    crossinline transform: suspend FlowCollector<R>.(T) -> Unit
): Flow<R> = unsafeTransform(transform)

// filter: crossinline `predicate` INVOKED inside transform's carrier (E5/E6 — desc rename + prune).
inline fun <T> Flow<T>.filter(crossinline predicate: suspend (T) -> Boolean): Flow<T> = transform { value ->
    if (predicate(value)) emit(value)
}

inline fun <T, R> Flow<T>.map(crossinline mapper: suspend (T) -> R): Flow<R> = transform { value ->
    emit(mapper(value))
}

// A captured VALUE whose method the predicate invokes (the filterIsInstance(klass) shape → E7).
class Divisor(private val by: Int) {
    fun accepts(value: Int): Boolean = value % by == 0
}

fun Flow<Int>.filterDivisibleBy(box: Divisor): Flow<Int> = filter { box.accepts(it) }

// Explicit emits (a loop var LIVE across the suspend `emit` is a separate cold-SM spill concern, orthogonal to
// this capture-family gate) — each captured a..f is a plain value capture the unsafeFlow SM spills.
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

fun main() {
    val base = flowOf(1, 2, 3, 4, 5, 6)
    // E1-E6: filter (crossinline predicate invoked+spliced) over transform over unsafeFlow
    println(blockOn { collectSum(base.filter { it % 2 == 0 }) })          // 2+4+6 = 12
    // map over the same cold-transform chain
    println(blockOn { collectSum(base.map { it * 10 }) })                 // 210
    // E7: filterDivisibleBy captures a VALUE box whose method the predicate invokes
    println(blockOn { collectSum(base.filterDivisibleBy(Divisor(3))) })   // 3+6 = 9
}
