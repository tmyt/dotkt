// Phase 1 — .NET event `+=`/`-=` in pure IL (façade-free, injected type).
// ObservableCollection<T> raises CollectionChanged SYNCHRONOUSLY on the calling thread when Add() runs, so the Kotlin
// handler (a lambda bound as the event's own delegate type) fires deterministically with no UI loop. A .NET event is
// surfaced as a `kotlin.clr.ClrEvent<T>` property (a compile-time handle — a .NET event is not a first-class value):
// subscribe with the idiomatic Kotlin `+=` and unsubscribe with `-=`. bir2cir binds these operators to the event's
// add/remove accessor (ClrEventOperatorBinding); the emitted IL is the same add/remove accessor call.
import System.Collections.ObjectModel.ObservableCollection

fun main() {
    val c = ObservableCollection<Int>()

    // (1) A direct lambda literal bound straight into the event delegate — the `button.Click += { }` case.
    c.CollectionChanged += { sender, e -> println("changed") }
    c.Add(10)
    c.Add(20)
    println(c.Count)            // 2

    // (2) A stored handler reference so it can later be removed (`-=` needs delegate equality).
    val h: (Any?, Any?) -> Unit = { sender, e -> println("h fired") }
    c.CollectionChanged += h
    c.Add(30)                   // literal + h both fire -> "changed", "h fired"
    c.CollectionChanged -= h
    c.Add(40)                   // only the literal fires -> "changed"
    println(c.Count)            // 4
}
