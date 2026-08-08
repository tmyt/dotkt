namespace ForeignStar
{
    public interface IRead<T>
    {
        T ReadView();
    }

    public sealed class DualRead : IRead<string>, IRead<int>
    {
        string IRead<string>.ReadView() => "string-view";
        int IRead<int>.ReadView() => 42;

        public IRead<string> StringView() => this;
        public IRead<int> IntView() => this;
    }

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
        public Holder<T> Nested() => new Holder<T>(_value);
    }

    public sealed class Holder<T>
    {
        public Holder(T value) => Value = value;
        public T Value { get; }
    }

    public sealed class Inner<T>
    {
        private readonly T _value;
        public Inner(T value) => _value = value;
        public T Read() => _value;
    }

    public sealed class Outer<T>
    {
        public Outer(T value) => Value = value;
        public T Value { get; }
    }

    public struct CounterCell<T>
    {
        private int _count;
        public int PublicCount;
        public CounterCell(int count) => _count = count;
        public void Increment() => _count++;
        public int ReadCount() => _count;
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

    public sealed class Duo<A, B>
    {
        public string Pick(A value) => "first:" + value;
        public string Pick(B value) => "second:" + value;
    }

    public class BaseReader
    {
        public string BaseValue() => "base";
    }

    public sealed class DerivedReader<T> : BaseReader { }

    public class GenericBase<T>
    {
        private readonly T _value;
        public GenericBase(T value) => _value = value;
        public T ReadInherited() => _value;
        public string PutInherited(T value) => "base:" + value;
    }

    public sealed class GenericDerived<T> : GenericBase<T>
    {
        public GenericDerived(T value) : base(value) { }
    }

    public sealed class ReorderedDerived<A, B> : GenericBase<B>
    {
        public ReorderedDerived(B value) : base(value) { }
    }

    public static class Factory
    {
        public static object StringBoxAsObject() => new Box<string>("foreign");
        public static IBox<string> StringBox() => new Box<string>("foreign");
        public static Pair<int, string> Pair() => new Pair<int, string>(7, "seven");
        public static CounterCell<string> CounterCell() => new CounterCell<string>(10);
        public static DualRead DualRead() => new DualRead();
        public static Duo<int, string> Duo() => new Duo<int, string>();
        public static DerivedReader<string> DerivedReader() => new DerivedReader<string>();
        public static object NestedOuterAsObject() =>
            new Outer<Inner<string>>(new Inner<string>("nested-foreign"));
        public static GenericDerived<string> GenericDerived() => new GenericDerived<string>("derived-view");
        public static ReorderedDerived<int, string> ReorderedDerived() =>
            new ReorderedDerived<int, string>("reordered-view");
    }
}
