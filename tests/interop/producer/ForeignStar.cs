namespace ForeignStar
{
    public interface IBox<T>
    {
        T Read();
        string Describe(int value);
        string Describe(string value);
    }

    public sealed class Box<T> : IBox<T>
    {
        private readonly T _value;

        public Box(T value) => _value = value;

        public T Read() => _value;
        public string Describe(int value) => "int:" + value;
        public string Describe(string value) => "string:" + value;
        public string EchoType<X>(X value) => typeof(X).Name + ":" + value;
        public string Throwing() => throw new System.InvalidOperationException("foreign-boom");
    }

    public sealed class Pair<A, B>
    {
        public Pair(A first, B second)
        {
            First = first;
            Second = second;
            FirstField = first;
            SecondField = second;
        }

        public A First { get; }
        public B Second { get; set; }
        public A FirstField;
        public B SecondField;
    }

    public static class Factory
    {
        public static object StringBoxAsObject() => new Box<string>("foreign");
        public static IBox<string> StringBox() => new Box<string>("foreign");
        public static Pair<int, string> Pair() => new Pair<int, string>(7, "seven");
    }
}
