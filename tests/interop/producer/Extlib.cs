// ktproj-extlib (forward C#-library interop): a PLAIN C# type in a referenced (non-BCL, NON-NRT/oblivious) assembly,
// consumed FAÇADE-FREE by the DotKt consumer via import-scan + the AssemblyResolver. Exercises the injector's harder
// .NET-interop surfaces: a real .NET `event` (subscribed with a Kotlin lambda through the ClrEvent<T> `+=` operator),
// a nullable VALUE-type property (`bool?` == Nullable<bool>, facadegen maps Nullable<X> -> X?), and a reference-type
// property from an oblivious assembly surfaced as the Kotlin PLATFORM type `String!` (ConeFlexibleType). Its simple
// name `Widget` DELIBERATELY collides with `Inherit.Widget` in another namespace of this SAME producer assembly — the
// #199-② regression: `Ext.Widget` has ONLY a parameterized ctor (no parameterless one), and before the fix facadegen
// emitted a subclass's base / a same-name reference as a BARE simple name, so kotc's ClrTypeInjector resolved the
// wrong `Widget` (by-simple-name last-wins) and CRASHED synthesizing `super()` against this no-arg-ctor-less type
// (`No arguments constructor for class Ext/Widget not found`). facadegen now emits namespace-qualified references, so
// `Inherit.Widget` and `Ext.Widget` stay distinct; this case guards that (and tests event/bool?/String! interop).
namespace Ext {
    public class Widget {
        public Widget(string name) { Name = name; }
        public string Name { get; }
        public bool? Enabled { get; set; }             // a nullable VALUE type (bool?) — like WinUI CheckBox.IsChecked
        public int Add(int a, int b) => a + b;
        public event System.Action<int> Changed;      // a real .NET event
        public void Fire(int n) { Changed?.Invoke(n); }
    }
}
