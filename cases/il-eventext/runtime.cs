using System;
namespace Ev {
    // A NORMAL class with a STATIC event -> subscribed through the synthesized companion (`Station.Announced += h`).
    // facadegen's GetEvents was Public|Instance inside the non-static branch, so static events were absent (N6); now
    // surfaced as a companion `ClrEvent<T>` property whose add/remove accessor emits a STATIC Call (not Callvirt).
    public class Station {
        public static event Action<string> Announced;
        public static void Announce(string s) { Announced?.Invoke(s); }
    }
    // A STATIC class (-> Kotlin `object`) with a STATIC event — the `System.Console.CancelKeyPress` shape. The event is
    // a MEMBER of the object; `Beacon.Pinged += h` reads it (recv = the object value -> a static add/remove accessor).
    public static class Beacon {
        public static event Action<int> Pinged;
        public static void Ping(int n) { Pinged?.Invoke(n); }
    }
}
