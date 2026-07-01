using System;
using System.Collections.Generic;

// The C# host. REVERSE ProjectReference: it consumes the Kotlin library's types at COMPILE time — no reflection,
// full IntelliSense. `Greeter` is a Kotlin class; `roster` returns a Kotlin read-only List<String>, which the
// @ClrTypeAlias maps to System.Collections.Generic.IReadOnlyList<string> (the runtime instance is a real
// System.Collections.Generic.List<string>, exposed under the read-only interface contract). This is what retarget
// unlocks (R-1).
class Program
{
    static void Main()
    {
        var g = new Greeter("Visual Studio");
        Console.WriteLine(g.greet());
        IReadOnlyList<string> names = g.roster();
        Console.WriteLine(string.Join(", ", names));
    }
}
