// C#-producer roundtrip consumer battery (batch A — static .NET events). N6: STATIC .NET events subscribe with the
// idiomatic `+=` / `-=`. facadegen surfaces them as `kotlin.clr.ClrEvent<T>` properties (a companion property for a
// normal class; an object member for a `static class`) that bir2cir's ClrEventOperatorBinding binds to the event's
// STATIC add/remove accessor. Side-effect prints are captured into an ordered value and asserted 1:1.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import Eventext.Station
import Eventext.Beacon

class StaticEventTests {
    @TestAttribute
    fun eventext() {
        var log = ""
        // (1) static event on a `static class` (Kotlin `object`) — the `Console.CancelKeyPress` shape.
        Beacon.Pinged += { n -> log += "ping: $n\n" }
        Beacon.Ping(3)            // ping: 3
        Beacon.Ping(7)            // ping: 7
        // (2) static event on a NORMAL class, reached through the companion.
        Station.Announced += { s -> log += "announce: $s\n" }
        Station.Announce("hi")    // announce: hi
        // (3) `-=` on a static event (a stored handler, removed by delegate equality).
        val h: (String) -> Unit = { s -> log += "h: $s\n" }
        Station.Announced += h
        Station.Announce("yo")    // announce: yo  +  h: yo
        Station.Announced -= h
        Station.Announce("bye")   // announce: bye
        assertEquals("ping: 3\nping: 7\nannounce: hi\nannounce: yo\nh: yo\nannounce: bye\n", log)
    }

    @TestAttribute
    fun staticEventSubscriptionCloses() {
        var log = ""
        val subscription = Station.Announced.subscribe { s -> log += "$s\n" }
        Station.Announce("before")
        subscription.close()
        Station.Announce("after")
        assertEquals("before\n", log)
    }
}
