// Regression guard for the Monitor Wait/Pulse cross-thread DRAIN mechanism that `kotlin.clr.blockOn`
// relies on (its BlockOnSink is exactly this pattern: waiter does Enter/`while(!done) Wait`/Exit; the
// completer does Enter/set-value/`done=true`/Pulse/Exit under the SAME monitor). This exercises the
// four System.Threading.Monitor primitives with a GENUINE cross-thread resume: the main thread must
// BLOCK in Wait until a worker thread, after a delay, sets the value and Pulses. `99` can only be
// observed after that cross-thread hand-off — proving Wait actually blocks and Pulse actually wakes.
// (blockOn itself cannot yet be driven to a true suspension E2E — await's slow-path suspension is
// still landing — so this isolates + locks the drain primitives blockOn is built on.)
import System.Threading.Monitor
import System.Threading.Thread

class Sink {
    var done = false
    var value = 0
}

fun main() {
    val sink = Sink()
    // Explicit zero-arity lambda (`{ -> }`): the .NET `Thread` ctor is overloaded on `ThreadStart` (`() -> Unit`) and
    // `ParameterizedThreadStart` (`(Any?) -> Unit`, from a delegate whose Invoke takes `object` — #1). A bare no-arrow
    // `{ ... }` lambda has ambiguous arity (0, or 1 via implicit `it`) and matches BOTH → overload-resolution ambiguity;
    // `{ -> ... }` pins arity 0 → the `ThreadStart` overload. (docs/dotkt-semantics.md — .NET delegate overload arity.)
    val worker = Thread({ ->
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
    println(sink.value)   // 99 — only reachable via the cross-thread Pulse after Wait unblocks
}
