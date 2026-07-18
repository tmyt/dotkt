// #129 regression guard. An AtomicIntArray element op does `array[index]`, whose bounds check THROWS mid-critical-
// section. Before the fix (bare monitorEnter/…/monitorExit, no try/finally) the monitor stayed LOCKED on the throw;
// the SAME thread never notices (Monitor is reentrant) but ANY OTHER thread on that instance blocks forever. This test
// proves the lock is released cross-thread: after catching the out-of-bounds throw, a worker thread must be able to
// acquire the same instance's monitor (loadAt). With the pre-fix bug worker.Join(2000) times out -> "DEADLOCK".
@file:OptIn(ExperimentalAtomicApi::class)

import System.Threading.Thread
import kotlin.concurrent.atomics.AtomicIntArray
import kotlin.concurrent.atomics.ExperimentalAtomicApi

fun main() {
    val src = intArrayOf(10, 20, 30)
    val arr = AtomicIntArray(src)
    src[1] = 999                                      // defensive-copy check: the ctor copies, so this must NOT leak in

    // Throw INSIDE the monitor critical section (index 99 is out of bounds).
    var caught = false
    try {
        arr.exchangeAt(99, 7)
    } catch (e: Throwable) {
        caught = true
    }
    println(caught)                                   // true

    // Cross-thread proof the monitor was released despite the throw.
    var observed = -1
    val worker = Thread({ observed = arr.loadAt(1) })
    worker.Start()
    val finished = worker.Join(2000)
    println(if (finished) observed.toString() else "DEADLOCK")   // 20 (not 999 -> defensive copy held AND lock released)

    // The array is still fully usable from the main thread.
    arr.storeAt(0, 100)
    println(arr.loadAt(0))                             // 100
}
