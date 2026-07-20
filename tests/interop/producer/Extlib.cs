// ktproj-extlib (forward C#-library interop): a PLAIN C# type in a referenced (non-BCL, NON-NRT/oblivious) assembly,
// consumed FAÇADE-FREE by the DotKt consumer via import-scan + the AssemblyResolver. Exercises the injector's harder
// .NET-interop surfaces: a real .NET `event` (subscribed with a Kotlin lambda through the ClrEvent<T> `+=` operator),
// a nullable VALUE-type property (`bool?` == Nullable<bool>, facadegen maps Nullable<X> -> X?), and a reference-type
// property from an oblivious assembly surfaced as the Kotlin PLATFORM type `String!` (ConeFlexibleType). Unique
// simple name `Gadget` (NOT `Widget`, which the Inherit case already uses): a same-simple-name collision in this one
// shared producer assembly makes kotc's ClrTypeInjector process the wrong `Widget` during constructor resolution and
// crash (the type has only a parameterized ctor, no parameterless one, so the injector's no-arg delegating-ctor path
// throws). Unique names sidestep it — the case tests the event/bool?/String! interop, not the name.
namespace Ext {
    public class Gadget {
        public Gadget(string name) { Name = name; }
        public string Name { get; }
        public bool? Enabled { get; set; }             // a nullable VALUE type (bool?) — like WinUI CheckBox.IsChecked
        public int Add(int a, int b) => a + b;
        public event System.Action<int> Changed;      // a real .NET event
        public void Fire(int n) { Changed?.Invoke(n); }
    }
}
