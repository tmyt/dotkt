#nullable disable
namespace InheritedGenericOwners;

public class Base<T>
{
    public T Echo(T value) => value;
    public V Convert<V>(T first, V second) => second;
}

public class IntBridge : Base<int> { }
public class GenericBridge<T> : Base<T> { }

public class Nested<T>
{
    public class Base<U>
    {
        public T Outer(T value) => value;
        public U Inner(U value) => value;
    }
}

public class NestedBridge : Nested<string>.Base<int> { }

public class PairBase<X, Y>
{
    public X First(X value) => value;
    public Y Second(Y value) => value;
    public R Convert<R>(X first, Y second, R result) => result;
    public X[] Repeat(X value) => new[] { value, value };
    public System.Tuple<X, System.Tuple<Y, X>> Nested(X first, Y second)
        => new(first, new(second, first));
    public System.Tuple<X, R> Mix<R>(X first, R result) => new(first, result);
}

public class SwappedBridge<A, B> : PairBase<B, A> { }

public class NativeReferenceBox<T>
{
    public T Value;
    public NativeReferenceBox(T value) => Value = value;
    public ref T Read() => ref Value;
}
