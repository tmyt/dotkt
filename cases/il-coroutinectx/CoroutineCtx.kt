// bir2cir SuspendColdLowering #79 — the top-level `suspend inline val coroutineContext` read. Its stdlib getter
// is intentionally `throw NotImplementedError("Implemented as intrinsic")`, so a real binding is required and lives
// in bir2cir (the only layer that knows the current-continuation identity). kotc emits the read as a top-level
// `callStatic get_coroutineContext` (owner mis-resolved to the enclosing file class, NOT stamped suspendCall);
// SuspendColdLowering rewrites it to `<current continuation>.get_context()` — the SM ITSELF in an SM body, the
// `completion` param in a no-SM body-direct cold entry (mirroring JVM's `<cont>.getContext`). Exercises all three
// current-continuation shapes: SM (a fun with another suspension), body-direct (no other suspension), and a
// suspending instance MEMBER (the SM's `this` is the SM, distinct from the `$this` enclosing-instance field).

import kotlin.coroutines.coroutineContext

suspend fun echo(x: Int): Int = x

suspend fun smRead(): String {                       // SM path: coroutineContext -> this(SM).get_context()
    val c = coroutineContext
    val y = echo(1)
    return c.toString() + y
}

suspend fun directRead(): String = coroutineContext.toString()   // no-SM body-direct: completion.get_context()

class Holder(val tag: Int) {
    suspend fun member(): String {                   // member SM: `this`=SM (get_context), `$this`=Holder (tag)
        val c = coroutineContext
        return c.toString() + (tag + echo(1))
    }
}

suspend fun main() {
    println(smRead())            // EmptyCoroutineContext1
    println(directRead())        // EmptyCoroutineContext
    println(Holder(1).member())  // EmptyCoroutineContext2
}
