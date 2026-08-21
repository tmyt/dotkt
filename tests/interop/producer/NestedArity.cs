#nullable enable

using System.Diagnostics.CodeAnalysis;

namespace NestedArityInterop;

public sealed class Outer
{
    public sealed class Item
    {
        public int Value => 1;
    }

    public sealed class Item<T>
    {
        public Item(T value) => Value = value;
        public T Value { get; }
    }

    public readonly struct ValueItem<T>
    {
        public ValueItem(T value) => Value = value;
        public T Value { get; }
    }

    public sealed class Kind
    {
        public int Value => 3;
    }

    public readonly struct Kind<T>
    {
        public Kind(T value) => Value = value;
        public T Value { get; }
    }
}

public sealed class GenericOuter<T>
{
    public readonly struct Leaf<U>
    {
        public Leaf(U value) => Value = value;
        public U Value { get; }
    }
}

// Both nested declarations have flattened arity two, but the generic slots belong to different TypeDef segments.
// Their exact CLR identities are SegmentCollisionOuter`1+Leaf`1 and SegmentCollisionOuter+Leaf`2.
public sealed class SegmentCollisionOuter<T>
{
    public interface Contract<U>
    {
        int Offset => 5;
        string Describe(T outer, U inner) => $"outer-contract:{outer}:{inner}";
    }

    public sealed class Leaf<U>
    {
        public Leaf(T outer, U inner, int marker = 43) => (Outer, Inner, Marker) = (outer, inner, marker);
        public T Outer { get; }
        public U Inner { get; }
        public int Marker { get; }
        public string Describe(string prefix = "outer") => $"{prefix}:{Outer}:{Inner}";
        public event System.Action<int>? Changed;
        public void Raise(int value) => Changed?.Invoke(value);
    }
}

public sealed class SegmentCollisionOuter
{
    public interface Contract<T, U>
    {
        int Offset => 7;
        string Describe(T outer, U inner) => $"inner-contract:{outer}:{inner}";
    }

    public sealed class Leaf<T, U>
    {
        public Leaf(T outer, U inner, int marker = 47) => (Outer, Inner, Marker) = (outer, inner, marker);
        public T Outer { get; }
        public U Inner { get; }
        public int Marker { get; }
        public string Describe(string prefix = "inner") => $"{prefix}:{Outer}:{Inner}";
        public event System.Action<int>? Changed;
        public void Raise(int value) => Changed?.Invoke(value);
    }
}

public sealed class NullableSlot<T> where T : struct
{
    public T? Value { get; set; }
}

public sealed class ConcreteNullableSlot
{
    public Outer.ValueItem<string>? Value { get; set; }
}

public readonly struct EventValueItem
{
    // Custom accessors keep the struct immutable while still exposing a real instance event declaration.
    public event System.Action<int>? Changed
    {
        add { }
        remove { }
    }
}

public static class Oracle
{
    public static Outer.ValueItem<string>? NestedValue() => new("nested value");
    [return: MaybeNull]
    public static Outer.ValueItem<string> PlatformNestedValue() => new("platform nested value");
    public static bool HasNestedValue(Outer.ValueItem<string>? value) =>
        value.HasValue && value.Value.Value == "nested value";

    public static Outer.Kind? ReferenceKind() => new();
    public static bool HasReferenceKind(Outer.Kind? value) => value is not null && value.Value == 3;

    public static Outer.Kind<int>? ValueKind() => new(7);
    public static bool HasValueKind(Outer.Kind<int>? value) =>
        value.HasValue && value.Value.Value == 7;

    public static GenericOuter<int>.Leaf<string>? FlattenedNestedValue() => new("flattened nested value");
    public static bool HasFlattenedNestedValue(GenericOuter<int>.Leaf<string>? value) =>
        value.HasValue && value.Value.Value == "flattened nested value";

    public static SegmentCollisionOuter<int>.Leaf<string> OuterGenericLeaf() => new(17, "outer generic");
    public static bool HasOuterGenericLeaf(SegmentCollisionOuter<int>.Leaf<string> value) =>
        value.Outer == 17 && value.Inner == "outer generic";

    public static SegmentCollisionOuter.Leaf<int, string> InnerGenericLeaf() => new(23, "inner generic");
    public static bool HasInnerGenericLeaf(SegmentCollisionOuter.Leaf<int, string> value) =>
        value.Outer == 23 && value.Inner == "inner generic";

    public static EventValueItem EventValue() => new();
}
