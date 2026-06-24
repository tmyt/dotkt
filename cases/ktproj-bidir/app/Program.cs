using System;
using System.Collections.Generic;

// The C# host. REVERSE ProjectReference: it consumes the Kotlin library's types at COMPILE time — no reflection,
// full IntelliSense. `Greeter` is a Kotlin class; `roster` returns a Kotlin List<String> seen here as a real
// System.Collections.Generic.List<string>. This is what retarget unlocks (R-1).
class Program
{
    static void Main()
    {
        var g = new Greeter("Visual Studio");
        Console.WriteLine(g.greet());
        List<string> names = g.roster();
        Console.WriteLine(string.Join(", ", names));
    }
}
