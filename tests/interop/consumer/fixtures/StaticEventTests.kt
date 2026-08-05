// C#-producer roundtrip consumer battery (batch A — static .NET events). N6: STATIC .NET events use closeable
// subscriptions. dll2klib surfaces them as direct static `kotlin.clr.ClrEvent<T>` properties that bir2cir's
// ClrEventSubscriptionBinding binds to the event's
// STATIC add/remove accessor. Side-effect prints are captured into an ordered value and asserted 1:1.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import Eventext.Station
import Eventext.Beacon

class StaticEventTests {
    @TestAttribute
    fun eventext() {
        var log = ""
        // (1) static event on a `static class` (Kotlin `object`) — the `Console.CancelKeyPress` shape.
        val ping = Beacon.Pinged.subscribe { n -> log += "ping: $n\n" }
        Beacon.Ping(3)            // ping: 3
        Beacon.Ping(7)            // ping: 7
        // (2) static event on a normal class, reached directly on its declaring type.
        val announce = Station.Announced.subscribe { s -> log += "announce: $s\n" }
        Station.Announce("hi")    // announce: hi
        // (3) close removes the exact stored handler from a static event.
        val extra = Station.Announced.subscribe { s -> log += "h: $s\n" }
        Station.Announce("yo")    // announce: yo  +  h: yo
        extra.close()
        Station.Announce("bye")   // announce: bye
        assertEquals("ping: 3\nping: 7\nannounce: hi\nannounce: yo\nh: yo\nannounce: bye\n", log)
        announce.close()
        ping.close()
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
