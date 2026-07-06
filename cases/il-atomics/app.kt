// COV2 (kcc review §2B): kotlin.concurrent.atomics coverage. The genuinely-atomic ops route through the
// @ClrRefArgument Interlocked byref binding (bir2cir reads it from the ref.dll and passes the backing field
// by managed pointer to System.Threading.Interlocked.*), which had ZERO gate coverage. Single-threaded so
// the output is deterministic and JVM-oracle-comparable (kotlin.concurrent.atomics is plain common Kotlin;
// on the JVM the actual is java.util.concurrent.atomic.*).
//
// Restricted to the API surface present in the RELEASED kotlin-stdlib-2.2.0 (the differential JVM oracle):
// the CAS-loop extensions update/fetchAndUpdate/updateAndFetch exist in our newer stdlib snapshot but not in
// 2.2.0, so they are exercised in verify-il only via the class primitives below (which cover the same byref
// binding). compareAndExchange stands in for the update-family readback.
@file:OptIn(ExperimentalAtomicApi::class)

import kotlin.concurrent.atomics.AtomicInt
import kotlin.concurrent.atomics.AtomicLong
import kotlin.concurrent.atomics.ExperimentalAtomicApi
import kotlin.concurrent.atomics.incrementAndFetch

fun main() {
    val a = AtomicInt(10)
    println(a.incrementAndFetch())            // 11
    println(a.fetchAndAdd(5))                 // 11  (old value; now 16)
    println(a.load())                         // 16
    println(a.compareAndSet(16, 20))          // true  (now 20)
    println(a.compareAndSet(99, 0))           // false (unchanged, still 20)
    println(a.addAndFetch(-4))                // 16
    println(a.exchange(100))                  // 16  (old value; now 100)
    println(a.compareAndExchange(100, 55))    // 100 (old value; now 55)
    println(a.load())                         // 55

    val b = AtomicLong(1000L)
    println(b.incrementAndFetch())            // 1001
    println(b.fetchAndAdd(9L))                // 1001 (old value; now 1010)
    println(b.addAndFetch(-10L))              // 1000
    println(b.compareAndExchange(1000L, 42L)) // 1000 (old value; now 42)
    println(b.load())                         // 42
}
