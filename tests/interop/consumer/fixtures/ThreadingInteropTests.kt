// CLR synchronous-threading battery (batch IntropC). Exercises genuine cross-thread monitor hand-off with a
// blocking Thread.Join; these are plain synchronous tests, not coroutine cold-core tests.
//
// Coverage preserved (old case -> method):
//   il-atomicarraytry -> atomicarraytry_boundsThrowReleasesMonitorCrossThread   #129 an AtomicIntArray element op whose
//                        bounds check THROWS mid-critical-section must still release the monitor (try/finally). A worker
//                        thread then acquires the same instance's monitor (loadAt); pre-fix the leaked lock made
//                        worker.Join(2000) time out -> "DEADLOCK". Also proves the ctor's defensive copy (20, not 999).
//   il-monitordrain   -> monitordrain_waitPulseCrossThreadDrain                 the System.Threading.Monitor Wait/Pulse
//                        cross-thread DRAIN the harness blockOn's BlockOnSink is built on (waiter Enter/`while(!done)
//                        Wait`/Exit; completer Enter/set/`done=true`/Pulse/Exit on the same monitor). `99` is only
//                        observable after a genuine cross-thread hand-off, proving Wait blocks + Pulse wakes.
//
// Top-level names are family-prefixed with `IntropC` (one assembly = one namespace).
@file:OptIn(ExperimentalAtomicApi::class)

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import System.Threading.Thread
import System.Threading.Monitor
import kotlin.concurrent.atomics.AtomicIntArray
import kotlin.concurrent.atomics.ExperimentalAtomicApi

// il-monitordrain: the shared monitor object — the waiter blocks on `while(!done) Wait`, the completer sets the value
// and Pulses under the same monitor.
class IntropCSink {
    var done = false
    var value = 0
}

class ThreadingInteropTests {
    // il-atomicarraytry (#129): the bounds-throw releases the monitor cross-thread; the defensive ctor copy holds.
    @TestAttribute
    fun boundsThrowReleasesMonitorCrossThread() {
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
        assertTrue(caught)                                // True

        // Cross-thread proof the monitor was released despite the throw.
        var observed = -1
        val worker = Thread({ observed = arr.loadAt(1) })
        worker.Start()
        val finished = worker.Join(2000)
        assertTrue(finished)                              // not DEADLOCK — the lock was released cross-thread
        assertEquals(20, observed)                        // 20 (not 999 -> defensive copy held AND lock released)

        // The array is still fully usable from the main thread.
        arr.storeAt(0, 100)
        assertEquals(100, arr.loadAt(0))                  // 100
    }

    // il-monitordrain: the main thread BLOCKS in Wait until a worker thread, after a delay, sets the value and Pulses.
    @TestAttribute
    fun waitPulseCrossThreadDrain() {
        val sink = IntropCSink()
        // A bare lambda `{ ... }` binds the .NET `Thread` ctor's preferred `ThreadStart` (`() -> Unit`) overload; the
        // Pareto-dominated `ParameterizedThreadStart` sibling is `@LowPriorityInOverloadResolution` (#19), so no arity pin.
        val worker = Thread({
            Thread.Sleep(100)
            Monitor.Enter(sink)
            try {
                sink.value = 99
                sink.done = true
                Monitor.Pulse(sink)
            } finally {
                Monitor.Exit(sink)
            }
        })
        worker.Start()
        Monitor.Enter(sink)
        try {
            while (!sink.done) Monitor.Wait(sink)
        } finally {
            Monitor.Exit(sink)
        }
        assertEquals(99, sink.value)   // 99 — only reachable via the cross-thread Pulse after Wait unblocks
    }
}
