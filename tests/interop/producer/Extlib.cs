// A plain C# type in a referenced non-NRT assembly. Exercises a real .NET event,
// a nullable VALUE-type property (`bool?` == Nullable<bool>, dll2klib maps Nullable<X> -> X?), and a reference-type
// property from an oblivious assembly surfaced as the Kotlin PLATFORM type `String!` (ConeFlexibleType). Its simple
// name `Widget` DELIBERATELY collides with `Inherit.Widget` in another namespace of this SAME producer assembly — the
// #199-② regression: `Ext.Widget` has only a parameterized ctor. dll2klib must preserve qualified identities so
// `Inherit.Widget` and `Ext.Widget` stay distinct.
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
