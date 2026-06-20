// Phase 1 — .NET event `+=`/`-=` in pure IL (façade-free, injected type).
// ObservableCollection<T> raises CollectionChanged SYNCHRONOUSLY on the calling thread when Add() runs,
// so the Kotlin handler (a lambda bound as the event's own delegate type) fires deterministically with
// no UI loop. `add_<E>`/`remove_<E>` are the injector-synthesized accessors the backend rewrites to
// the event's add/remove method (see ClrEventRegistry).
import System.Collections.ObjectModel.ObservableCollection

fun main() {
    val c = ObservableCollection<Int>()

    // (1) A direct lambda literal bound straight into the event delegate — the `button.Click += { }` case.
    c.add_CollectionChanged { sender, e -> println("changed") }
    c.Add(10)
    c.Add(20)
    println(c.Count)            // 2

    // (2) A stored handler reference so it can later be removed (`-=` needs delegate equality).
    val h: (Any?, Any?) -> Unit = { sender, e -> println("h fired") }
    c.add_CollectionChanged(h)
    c.Add(30)                   // literal + h both fire -> "changed", "h fired"
    c.remove_CollectionChanged(h)
    c.Add(40)                   // only the literal fires -> "changed"
    println(c.Count)            // 4
}
