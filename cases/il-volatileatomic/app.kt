// #130 regression guard. Scalar atomics load()/store() must be VOLATILE (acquire/release), not plain field access.
// AtomicInt/AtomicLong/AtomicBoolean bind System.Threading.Volatile.Read/Write on the backing field (byref); the long
// path additionally guarantees non-tearing (Volatile.Read(ref long) is atomic on every platform). AtomicReference uses
// a real `@Volatile` field. This exercises a store->load round-trip on each so the volatile binding compiles and
// carries the value (a true visibility race is not deterministically gate-testable; this locks the API path).
@file:OptIn(ExperimentalAtomicApi::class)

import kotlin.concurrent.atomics.AtomicInt
import kotlin.concurrent.atomics.AtomicLong
import kotlin.concurrent.atomics.AtomicBoolean
import kotlin.concurrent.atomics.AtomicReference
import kotlin.concurrent.atomics.ExperimentalAtomicApi

fun main() {
    val i = AtomicInt(0)
    i.store(42)
    println(i.load())                    // 42

    val l = AtomicLong(0L)
    l.store(9_000_000_000L)              // > 2^32, would tear on a plain 32-bit access
    println(l.load())                    // 9000000000

    val b = AtomicBoolean(false)
    b.store(true)
    println(b.load())                    // true

    val r = AtomicReference("a")
    r.store("b")
    println(r.load())                    // b
}
