object value = new();
if (!EqualityKt.opaqueEquals(value, value) || EqualityKt.opaqueEquals(value, new object())
    || !EqualityKt.opaqueEquals(null, null) || EqualityKt.opaqueHash(value) != value.GetHashCode())
    throw new Exception("Ordinary Object slots");

var overridden = new InheritsEquals();
if (EqualityKt.opaqueEquals(overridden, overridden) || overridden.Calls != 1
    || EqualityKt.opaqueHash(new HashOnly()) != 73)
    throw new Exception("Collection override dispatch");
Console.WriteLine("Kotlin equality and hashing passed");

class Overrides : List<int>
{
    public int Calls;
    public override bool Equals(object? other) { Calls++; return false; }
    public override int GetHashCode() => 41;
}
sealed class InheritsEquals : Overrides {}
sealed class HashOnly : List<int> { public override int GetHashCode() => 73; }
