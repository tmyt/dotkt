// N6 — STATIC .NET events subscribe with the idiomatic `+=` / `-=`. facadegen emitted event metadata only for
// INSTANCE events of NON-static classes (`GetEvents(Public|Instance)` inside the non-static branch), so a static
// event on a normal class (`TaskScheduler.UnobservedTaskException`) or on a `static class`/`object`
// (`System.Console.CancelKeyPress`) had no member to resolve. They are now surfaced as `kotlin.clr.ClrEvent<T>`
// properties (a companion property for a normal class; an object member for a static class) that bir2cir's
// ClrEventOperatorBinding binds to the event's STATIC add/remove accessor. See docs/dotkt-semantics.md §8d.
//
// (Interface events are deferred — surfacing them destabilizes a Kotlin subclass of a .NET class implementing the
// interface, which needs a downstream ClrEvent fake-override elision; see facadegen's interface-branch note.)
import Ev.Station
import Ev.Beacon

fun main() {
    // (1) static event on a `static class` (Kotlin `object`) — the `Console.CancelKeyPress` shape.
    Beacon.Pinged += { n -> println("ping: $n") }
    Beacon.Ping(3)            // ping: 3
    Beacon.Ping(7)            // ping: 7

    // (2) static event on a NORMAL class, reached through the companion.
    Station.Announced += { s -> println("announce: $s") }
    Station.Announce("hi")    // announce: hi

    // (3) `-=` on a static event (a stored handler, removed by delegate equality).
    val h: (String) -> Unit = { s -> println("h: $s") }
    Station.Announced += h
    Station.Announce("yo")    // announce: yo  +  h: yo
    Station.Announced -= h
    Station.Announce("bye")   // announce: bye
}
