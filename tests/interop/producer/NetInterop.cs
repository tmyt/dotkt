// Producer source for the migrated il-netinterop case. I4 remnants battery: .NET enum, generic delegates (BCL Func
// + a custom generic delegate), and nullable value types (int?/double?) in signatures. Own namespace (I4).
using System;
namespace I4 {
    public enum Color { Red = 1, Green = 2, Blue = 4 }
    public class Probe {
        // enum in signatures
        public Color First() { return Color.Green; }
        public string NameOf(Color c) { return c.ToString(); }
        public int Code(Color c) { return (int)c; }
        // generic delegates (constructed): a lambda binds via the func:[...] mapping
        public int Apply(Func<int, int> f, int x) { return f(x); }
        // nullable value types
        public int? MaybeVal(bool yes) { return yes ? 42 : (int?)null; }
        public int OrZero(int? v) { return v ?? 0; }
        public double? Half(double? d) { return d.HasValue ? d.Value / 2 : (double?)null; }
    }
    public delegate T Mapper<T>(T input);
    public class GenDel {
        public int Run(Mapper<int> m, int seed) { return m(m(seed)); }
    }
}
