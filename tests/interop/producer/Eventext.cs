// Producer source for the migrated il-eventext case (N6). STATIC .NET events on a NORMAL class and on a `static
// class` (the Console.CancelKeyPress shape) remain direct KLIB static declarations — surfaced by
// dll2klib as `kotlin.clr.ClrEvent<T>` properties whose add/remove accessors emit a STATIC Call. Own namespace.
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
