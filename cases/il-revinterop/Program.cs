// A .NET host consuming the IL-emitted Kotlin assembly. Loads it and calls a Kotlin class + top-level fun.
// Reflection load works for any .NET consumer at runtime. (Compile-time `<Reference>` from C# additionally
// needs the emitted assembly to reference contract assemblies — System.Runtime/System.Collections/… — per
// type instead of the single System.Private.CoreLib ref ilemit currently emits; that per-type retargeting is
// blocked by a Reflection.Emit limitation, see docs/csharp-retirement-design.md 5.2.)
using System;
using System.Reflection;
class Program {
    static void Main(string[] args) {
        var asm = Assembly.LoadFrom(args[0]);
        var greeter = asm.GetType("Greeter");
        var g = Activator.CreateInstance(greeter, "World");                 // a Kotlin class
        Console.WriteLine(greeter.GetMethod("greet").Invoke(g, null));      // -> Hi, World
        var libkt = asm.GetType("LibKt");                                   // Kotlin top-level fun's file class
        Console.WriteLine(libkt.GetMethod("add").Invoke(null, new object[] { 2, 3 }));  // -> 5
    }
}
