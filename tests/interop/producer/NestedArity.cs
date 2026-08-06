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
}
