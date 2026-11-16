#nullable enable
using System;

namespace NativeComparison;

public class NativeOrder : IComparable<NativeOrder>
{
    public int Value { get; }
    public NativeOrder(int value) => Value = value;
    public virtual int CompareTo(NativeOrder? other) => Value - (other?.Value ?? 0);
    public int CompareTo(int unrelated) => 1000 + unrelated;
    public int compareTo(int unrelated) => 4000 + unrelated;
}

public sealed class InheritedOrder : NativeOrder
{
    public InheritedOrder(int value) : base(value) { }
}

public sealed class OtherOrder
{
    public int Value { get; }
    public OtherOrder(int value) => Value = value;
}

public class ExplicitOrder : IComparable<OtherOrder>
{
    public int Value { get; }
    public ExplicitOrder(int value) => Value = value;
    int IComparable<OtherOrder>.CompareTo(OtherOrder? other) => Value - (other?.Value ?? 0);
    public int CompareTo(int unrelated) => 2000 + unrelated;
}

public sealed class InheritedExplicitOrder : ExplicitOrder
{
    public InheritedExplicitOrder(int value) : base(value) { }
}

public sealed class DualOrder : IComparable<string>, IComparable<int>
{
    int IComparable<string>.CompareTo(string? other) => 3 - (other?.Length ?? 0);
    int IComparable<int>.CompareTo(int other) => 7 - other;
    public int CompareTo(object? unrelated) => 3000;
}

public sealed class Box<T>
{
    public int Value { get; }
    public Box(int value) => Value = value;
}

public class PermutedOrder<A, B> : IComparable<Box<B>>
{
    public int Value { get; }
    public PermutedOrder(int value) => Value = value;
    public int CompareTo(Box<B>? other) => Value - (other?.Value ?? 0);
}

public sealed class NullableArgumentOrder<T> : IComparable<Box<T?>> where T : class
{
    public int Value { get; }
    public NullableArgumentOrder(int value) => Value = value;
    public int CompareTo(Box<T?>? other) => Value - (other?.Value ?? 0);
}

internal sealed class HiddenComparisonArgument { }
public sealed class HiddenComparisonContract : IComparable<HiddenComparisonArgument>
{
    int IComparable<HiddenComparisonArgument>.CompareTo(HiddenComparisonArgument? value) => 0;
}

public struct ComparisonToken
{
    public int Value;
    public ComparisonToken(int value) => Value = value;
}
public sealed class CrossAssemblyOrder : ComparableBase<ComparisonToken>
{
    public int Value { get; }
    public CrossAssemblyOrder(int value) => Value = value;
    public override int CompareTo(ComparisonToken value) => Value - value.Value;
}
public sealed class AnnotatedCrossAssemblyOrder : AnnotatedComparableBase<ComparisonToken>
{
    public int Value { get; }
    public AnnotatedCrossAssemblyOrder(int value) => Value = value;
    public override int CompareTo(ComparisonToken value) => Value - value.Value;
}
public sealed class NullableCrossAssemblyOrder : NullableComparableBase<ComparisonToken>
{
    public int Value { get; }
    public NullableCrossAssemblyOrder(int value) => Value = value;
    public override int CompareTo(ComparisonToken? value) => Value - (value?.Value ?? 0);
}
public sealed class LowercaseCollisionOrder : IComparable<int>
{
    public int CompareTo(int value) => value - 7;
    public int compareTo(int value) => 1000 + value;
}

public struct MutableOrder : IComparable<MutableOrder>
{
    public int Value;
    public int Calls;
    public MutableOrder(int value) { Value = value; Calls = 0; }
    public int CompareTo(MutableOrder other) { Calls++; return Value - other.Value; }
}
