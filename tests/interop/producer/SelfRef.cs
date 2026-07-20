// Producer source for the migrated il-selfref case. The everyday BCL value-type shape: a type that implements a
// generic interface parameterized by ITSELF (int/string/DateTime/Guid all do IComparable<Self>/IEquatable<Self>).
// Own namespace (SelfRef) — the case's original `namespace P` collided with netattr/outref in this assembly.
using System;
namespace SelfRef {
    public class Money : IComparable<Money> {
        public int Amount;
        public Money(int a) { Amount = a; }
        public int CompareTo(Money other) => Amount - other.Amount;
    }
    public class Cmp { public int Test(IComparable<Money> x, Money y) => x.CompareTo(y); }
}
