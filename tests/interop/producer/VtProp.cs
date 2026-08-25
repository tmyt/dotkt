// Producer source for the migrated il-vtprop case. A .NET value type (struct) with a MUTABLE auto-property and a
// mutable public field. Mutating either from Kotlin exercises the clrPropSet value-type-receiver path: the
// setter/stfld must run against the struct's ADDRESS (ldloca), not a spilled copy, or the mutation is lost. Own
// namespace (Probe).
namespace Probe {
    public struct Box {
        public int V { get; set; }   // property setter (set_V) -> clrPropSet property branch
        public int F;                // public field        -> clrPropSet field-store branch
        public Box(int v) { V = v; F = v; }
        public int Sum() => V + F;
        public void SetBoth(int v) { V = v; F = v; }
    }

    public interface IMutableBox {
        int Value { get; }
        void SetValue(int value);
    }

    public struct GenericMutableBox : IMutableBox {
        public int Value { get; private set; }
        public GenericMutableBox(int value) { Value = value; }
        public void SetValue(int value) { Value = value; }
    }

    public struct BoxNest {
        public Box Value;
        public BoxNest(int value) { Value = new Box(value); }
    }

    public sealed class BoxHolder {
        public Box Direct;
        public BoxNest Nested;
        public Box[] Items;

        public BoxHolder(int direct, int nested, int item) {
            Direct = new Box(direct);
            Nested = new BoxNest(nested);
            Items = new[] { new Box(item) };
        }
    }
}
