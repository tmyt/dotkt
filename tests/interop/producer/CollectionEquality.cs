using System;
using System.Collections.Generic;

namespace EqualityInterop;

public sealed class OverriddenList : List<int>
{
    public int Calls { get; private set; }
    public override bool Equals(object? other) { Calls++; return false; }
    public override int GetHashCode() => 0;
}

public sealed class HiddenList : List<int>
{
    public HiddenList() { Add(7); Add(9); }
    public new bool Equals(object? other) => throw new InvalidOperationException("not the Object slot");
}

public sealed class LookupKey(int id)
{
    public static int Calls { get; set; }
    public override bool Equals(object? other) { Calls++; return other is LookupKey key && id == key.Id; }
    private int Id => id;
    public override int GetHashCode() => id;
}

public static class NativeEqualityContract
{
    public static bool EqualHashAndLookup(object left, object right)
    {
        if (!left.Equals(right) || left.GetHashCode() != right.GetHashCode()) return false;
        var map = new Dictionary<object, int> { [left] = 7 };
        return map.TryGetValue(right, out var value) && value == 7;
    }
}
