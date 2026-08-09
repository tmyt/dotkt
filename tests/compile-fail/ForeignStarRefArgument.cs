namespace ForeignStarReflectionSignatureFixture
{
    public sealed class Box<T>
    {
        private int _value;

        public void Replace(ref int value) => value++;
        public ref int RefValue() => ref _value;
        public System.Span<int> SpanValue() => default;
    }

    public static class Factory
    {
        public static Box<string> Create() => new Box<string>();
    }
}
