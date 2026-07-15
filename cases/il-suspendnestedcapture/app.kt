// #22 — a `suspend inline fun` with a `crossinline` block whose body NESTS a lambda that captures an
// enclosing binding. This is the `suspendCancellableCoroutine { cont -> cont.invokeOnCancellation { … } }`
// shape that every real cancellable-coroutine block uses. bir2cir's §4.4ii inline-splice materializes the
// crossinline `block` into a real `newClosure` (it is captured into the suspend SM, a non-invoke position);
// before the fix MaterializeCarrier refused any carrier containing a nested closure and FAILED LOUD. The
// carrier's nested closure captures the block's own param `cont` (an invoke param of the materialized
// closure) and, in cap1, ALSO an enclosing-suspend-fn local `h` (a carrier capture -> rewritten to a field
// of the materialized closure). `register` invokes the nested closure synchronously, resuming `cont`.
import kotlin.coroutines.*
import kotlin.coroutines.intrinsics.*
import dotkt.support.blockOn

suspend inline fun <T> mySuspend(crossinline block: (Continuation<T>) -> Unit): T =
    suspendCoroutineUninterceptedOrReturn { uCont -> block(uCont); COROUTINE_SUSPENDED }

fun register(action: () -> Unit) { action() }

// case a: the nested closure captures ONLY `cont` (the block's own param) — the exact minimal issue repro.
suspend fun cap0(): Int = mySuspend { cont -> register { cont.resume(5) } }

// case a + b: the nested closure captures `cont` (invoke param) AND `h` (an enclosing-suspend-fn local that
// the carrier itself captures -> a field of the materialized closure, rewritten by RewriteCapturesToFields).
suspend fun cap1(h: Int): Int = mySuspend { cont -> register { cont.resume(h + 1) } }

// case a + c, GENERIC: `capG<T>` — the nested closure captures `cont: Continuation<T>` (invoke param, case a)
// AND `local: T` (a carrier-DECLARED local, case c). The materialized closure is generic over `T` and the
// nested closure carries `T` on its own typeArgs; exercises CollectTvKeys/RenumberTvs descending the nested
// closure's captures/typeArgs while skipping its own `synthClass` frame.
suspend fun <T> capG(v: T): T = mySuspend { cont ->
    val local = v
    register { cont.resume(local) }
}

// #22 RESIDUAL — the nested closure capturing `cont` reaches the block through an inner inline-EXTENSION
// iterator (`forEach`/`map`/`forEachIndexed`, receiver `Array<T>`). That iterator splices to a `forArray`
// loop whose element binder rides the node's `"var"` field (not a `{k:var}` stmt) and flows into the
// lambda-param temp; MaterializeCarrier's declared-locals scan missed the loop binder, so the temp's
// element ref read as an unlisted stray capture and the carrier failed §4.4ii. Single-element arrays ->
// each resumes `cont` exactly once. (Real `awaitAll` uses `nodes.forEach { … cont … }` + `Array(n){ … }`.)
suspend fun capFE(): Int = mySuspend { cont ->
    arrayOf(7).forEach { register { cont.resume(it) } }
}
suspend fun capMap(): Int = mySuspend { cont ->
    arrayOf(50).map { register { cont.resume(it) }; it }
}
suspend fun capFEI(): Int = mySuspend { cont ->
    arrayOf(100).forEachIndexed { idx, v -> register { cont.resume(idx + v) } }
}
// forIn variant: a NON-array receiver (`List`) — the inline `forEach` splices to a `forIn` (iterator) loop
// whose element binds in the node's `"var"` field, the sibling binder kind to `forArray`.
suspend fun capList(): Int = mySuspend { cont ->
    listOf(70).forEach { register { cont.resume(it) } }
}
// try-catch binder variant: the carrier's own `catch (e: …)` binds `e` in the try node's `"var"` field (not a
// `{k:var}`); referencing it inside the nested closure was equally an unlisted-stray before the fix.
suspend fun capTry(): Int = mySuspend { cont ->
    try { throw RuntimeException("80") } catch (e: RuntimeException) { register { cont.resume(e.message!!.toInt()) } }
}

fun main() {
    println(blockOn { cap0() })          // 5
    println(blockOn { cap1(41) })        // 42
    println(blockOn { capG("hi") })      // hi
    println(blockOn { capG(7) })         // 7
    println(blockOn { capFE() })         // 7
    println(blockOn { capMap() })        // 50
    println(blockOn { capFEI() })        // 100
    println(blockOn { capList() })       // 70
    println(blockOn { capTry() })        // 80
}
