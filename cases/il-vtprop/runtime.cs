namespace Probe {
    // A .NET value type (struct) with a MUTABLE auto-property and a mutable public field. Mutating either
    // from Kotlin exercises the clrPropSet value-type-receiver path: the setter/`stfld` must run against the
    // struct's ADDRESS (ldloca), not a spilled copy, or the mutation is lost.
    public struct Box {
        public int V { get; set; }   // property setter (set_V) -> clrPropSet property branch
        public int F;                // public field        -> clrPropSet field-store branch
        public Box(int v) { V = v; F = v; }
        public int Sum() => V + F;
    }
}
