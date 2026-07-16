// BATCH B (#75 holistic) — the `withLock { launch { …local… } }`-shaped cell (L518 carrier-side retirement).
// A coroutine-builder SUSPEND lambda built INSIDE an inline-call lambda arg, capturing that arg's OWN local. The
// inline fn `withGuard` takes a normal (non-crossinline) lambda `body`; the caller passes `{ launchLike { local + t } }`
// where `local` is a local declared inside that lambda and `launchLike` is a non-inline fn taking a suspend lambda.
// The inner suspend lambda `{ local + t }` captures the ENCLOSING inline-call lambda's own local — before Batch B
// BuildLambdaSplice's carrier-side guard (SuspendDescriptorIn) fired: a newSuspendLambda whose descriptor names a
// carrier-declared local was refused. Now the newSuspendLambda is a joint-hygiene citizen — its descriptor is renamed
// in lockstep with the carrier's local prefixing, so the SM ctor field and the invokeSuspend body ref stay aligned.
import dotkt.support.blockOn

suspend fun addA(a: Int, b: Int): Int = a + b

// non-inline "launch"-like driver: runs the suspend lambda to completion, returns its Int result.
fun launchLike(block: suspend () -> Int): Int = blockOn(block)

// inline fn taking a NORMAL lambda; the lambda body builds a capturing suspend lambda over its OWN local.
inline fun withGuard(body: () -> Int): Int = body()

fun main() {
    val r = withGuard {
        val local = 30            // a local of the inline-call lambda arg
        launchLike { addA(local, 12) }   // suspend lambda capturing that local
    }
    println(r)                    // 42
    val r2 = withGuard {
        val base = 5
        launchLike { addA(base, base) }  // captures the local twice
    }
    println(r2)                   // 10
}
