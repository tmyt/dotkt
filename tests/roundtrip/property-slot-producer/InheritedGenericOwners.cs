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
