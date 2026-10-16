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
