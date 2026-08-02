// kotlin.concurrent.atomics CAS battery (feature fixture) — migrates the pure-Kotlin il-atomics case. The genuinely-atomic
// ops route through the @ClrRefArgument Interlocked byref binding (bir2cir reads it from the ref.dll and passes the
// backing field by managed pointer to System.Threading.Interlocked.*). This case is PLAIN Kotlin (no `import System.*`),
// single-threaded so the result is deterministic. It COMPLEMENTS PropertyAndAtomicTests.atomicVolatile (store/load) by
// covering the CAS family (compareAndSet/compareAndExchange/exchange/fetchAndAdd/addAndFetch/incrementAndFetch).
//
// API surface restricted to the released kotlin-stdlib-2.2.0 shape (the differential JVM oracle): the CAS-loop
// extensions update/fetchAndUpdate/updateAndFetch exist in our newer snapshot but not in 2.2.0, so the class primitives
// below stand in — they cover the same byref binding. Each old `println` value is preserved 1:1 (see `// <expected>`).
@file:OptIn(ExperimentalAtomicApi::class)

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsFalse as assertFalse
import kotlin.concurrent.atomics.AtomicInt
import kotlin.concurrent.atomics.AtomicLong
import kotlin.concurrent.atomics.ExperimentalAtomicApi
import kotlin.concurrent.atomics.incrementAndFetch

class AtomicInteropTests {
    // il-atomics: the AtomicInt/AtomicLong CAS family over the @ClrRefArgument Interlocked byref binding. The ops mutate
    // in sequence, so the order (and each returned old/new value) is load-bearing.
    @TestAttribute
    fun interlockedByrefBinding() {
        val a = AtomicInt(10)
        assertEquals(11, a.incrementAndFetch())          // 11
        assertEquals(11, a.fetchAndAdd(5))               // 11  (old value; now 16)
        assertEquals(16, a.load())                       // 16
        assertTrue(a.compareAndSet(16, 20))              // true  (now 20)
        assertFalse(a.compareAndSet(99, 0))              // false (unchanged, still 20)
        assertEquals(16, a.addAndFetch(-4))              // 16
        assertEquals(16, a.exchange(100))                // 16  (old value; now 100)
        assertEquals(100, a.compareAndExchange(100, 55)) // 100 (old value; now 55)
        assertEquals(55, a.load())                       // 55

        val b = AtomicLong(1000L)
        assertEquals(1001L, b.incrementAndFetch())       // 1001
        assertEquals(1001L, b.fetchAndAdd(9L))           // 1001 (old value; now 1010)
        assertEquals(1000L, b.addAndFetch(-10L))         // 1000
        assertEquals(1000L, b.compareAndExchange(1000L, 42L)) // 1000 (old value; now 42)
        assertEquals(42L, b.load())                      // 42
    }
}
