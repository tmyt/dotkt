object value = new();
if (!EqualityKt.opaqueEquals(value, value) || EqualityKt.opaqueEquals(value, new object())
    || !EqualityKt.opaqueEquals(null, null) || EqualityKt.opaqueHash(value) != value.GetHashCode())
    throw new Exception("Ordinary Object slots");

var overridden = new InheritsEquals();
if (EqualityKt.opaqueEquals(overridden, overridden) || overridden.Calls != 1
    || EqualityKt.opaqueHash(new HashOnly()) != 73)
    throw new Exception("Collection override dispatch");
Console.WriteLine("Kotlin equality and hashing passed");

if (EqualityKt.projectedSize(new List<int> { 11, 12 }) != 2 ||
    !EqualityKt.opaqueEquals(new List<int> { 2, 3 }, new List<int> { 2, 3 }) ||
    EqualityKt.opaqueEquals(new List<int> { 2, 3 }, new List<int> { 3, 2 }))
    throw new Exception("Projected list size and structural equality");
var split = new ExplicitCounts();
if (EqualityKt.projectedSize(split) != 17 || EqualityKt.projectedMutableSize(split) != 23)
    throw new Exception("Read-only and mutable explicit Count slots were conflated");
if (!EqualityKt.opaqueEquals(new ExplicitSequence { 0 }, new List<int> { 42 }))
    throw new Exception("Structural equality did not use the selected generic enumerator");
Console.WriteLine("Projected CLR collection slots passed");

class Overrides : List<int>
{
    public int Calls;
    public override bool Equals(object? other) { Calls++; return false; }
    public override int GetHashCode() => 41;
}
sealed class InheritsEquals : Overrides {}
sealed class HashOnly : List<int> { public override int GetHashCode() => 73; }
sealed class ExplicitSequence : List<int>, IEnumerable<int>
{
    IEnumerator<int> IEnumerable<int>.GetEnumerator() { yield return 42; }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw new Exception("Wrong enumerator");
}
sealed class ExplicitCounts : List<int>, IReadOnlyList<int>, IList<int>
{
    int IReadOnlyCollection<int>.Count => 17;
    int ICollection<int>.Count => 23;
}
