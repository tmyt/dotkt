// BATCH B (#75 holistic, 2A) — the GENERIC + RECEIVER + suspend-MEMBER inline-splice path (the whole
// kotlinx.coroutines `flow{}` family, 51 sites). A generic `inline fun <T>` whose `crossinline` SUSPEND
// RECEIVER lambda (`suspend Sink<T>.() -> Unit`) is captured into an `object : Src<T>` literal, and whose
// carrier body invokes a suspend MEMBER (`emit`) on the generic receiver. That member call carries the
// interface's own decl param `tv{type,0}` in its signature; the carrier ALSO references the enclosing
// method/type param — so MaterializeSuspendCarrier's CollectTvKeys yields a MULTI-scope key set
// `{(method,0),(type,0)}`. The former single-scope-0..N-1-prefix guard fail-loud'd on exactly this. 2A
// renumbers the enclosing tvs to a dense SM param space and passes the ORIGINALS as a construction-typeArgs
// channel (mirroring the non-suspend newClosure arm), so `new SM<origTvs…>(…)` instantiates correctly; the
// member-sig `tv{type,0}` resolves to `object` at the non-generic construction site but is never consulted
// (ilemit re-resolves `s.emit` against the field's static receiver type). Drives end-to-end via blockOn;
// the summed values MUST be correct.
import dotkt.support.blockOn

interface Sink<T> {
    suspend fun emit(value: T)
}

interface Src<T> {
    suspend fun drain(s: Sink<T>)
}

class ListSink<T> : Sink<T> {
    val items = ArrayList<T>()
    override suspend fun emit(value: T) { items.add(value) }
}

// THE path: crossinline SUSPEND RECEIVER lambda captured into an object literal; carrier body invokes the
// generic receiver's suspend member `emit`.
inline fun <T> mkFlow(crossinline block: suspend Sink<T>.() -> Unit): Src<T> = object : Src<T> {
    override suspend fun drain(s: Sink<T>) { s.block() }
}

// (b, method-scope) a GENERIC METHOD enclosing the splice: its `R` survives to the construction site as a
// genuine `tv{method,0}` free var (captured `x`/`y`), so the key set is a REAL multi-scope {(method,0),(type,0)}
// — the shape the old guard rejected, pinning the construction-typeArgs channel (SM instantiated with `<R>`).
fun <R> makeSrc(x: R, y: R): Src<R> = mkFlow<R> { emit(x); emit(y) }

// (b, type-scope) a GENERIC CLASS member: its `E` (`tv{type,0}`) flows through as the receiver element type,
// exercising the construction-typeArgs channel with a type-scope free var. (E and the receiver element coincide
// — the residual conflation is the DIFFERING case, which none of the 52 hit; see the filed #46 follow-up.)
class Box<E>(val a: E, val b: E) {
    fun make(): Src<E> = mkFlow<E> { emit(a); emit(b) }
}

suspend fun runSrc(src: Src<Int>): Int {
    val sink = ListSink<Int>()
    src.drain(sink)
    var sum = 0
    for (x in sink.items) sum += x
    return sum
}

fun main() {
    // (a) top-level use
    val s1 = mkFlow<Int> { emit(20); emit(22) }
    println(blockOn { runSrc(s1) })          // 42

    // (b, method-scope) generic-method enclosing free var -> construction-typeArgs channel
    val s2 = makeSrc(30, 12)
    println(blockOn { runSrc(s2) })          // 42

    // (b, type-scope) generic-class member enclosing free var
    val s3 = Box(40, 2).make()
    println(blockOn { runSrc(s3) })          // 42
}
