// Producer source for the migrated il-eventext case (N6). STATIC .NET events on a NORMAL class (subscribed through the
// synthesized companion) and on a `static class` -> Kotlin `object` (the Console.CancelKeyPress shape) — surfaced by
// facadegen as `kotlin.clr.ClrEvent<T>` properties whose add/remove accessors emit a STATIC Call. Own namespace.
using System;
namespace Eventext {
    public class Station {
        public static event Action<string> Announced;
        public static void Announce(string s) { Announced?.Invoke(s); }
    }
    public static class Beacon {
        public static event Action<int> Pinged;
        public static void Ping(int n) { Pinged?.Invoke(n); }
    }
}
