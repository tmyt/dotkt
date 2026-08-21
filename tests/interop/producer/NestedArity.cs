#nullable enable

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

public sealed class NullableSlot<T> where T : struct
{
    public T? Value { get; set; }
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

    public static EventValueItem EventValue() => new();
}
