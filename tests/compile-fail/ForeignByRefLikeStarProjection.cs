namespace ForeignByRefLikeStarProjectionFixture
{
    public ref struct Cell<T>
    {
        public Cell(T value) => Value = value;
        public T Value;
    }

    public static class Factory
    {
        public static Cell<string> Create() => new Cell<string>("value");
    }
}
