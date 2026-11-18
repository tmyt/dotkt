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
}

public class SwappedBridge<A, B> : PairBase<B, A> { }

public class Outer<T> { public interface Contract<U> { int get_Offset(); } }
public class Outer { public interface Contract<T, U> { int Offset { get; } } }
public interface INestedOffsetView : Outer<int>.Contract<Outer<int>.Contract<string>> { }
public class NestedOffsetView : INestedOffsetView { public int get_Offset() => 37; }
public class OffsetBase : Outer.Contract<int, string> { public int Offset => 7; }
public static class OffsetCalls
{
    public static int Own(Outer<int>.Contract<string> value) => value.get_Offset();
    public static int Inherited(Outer.Contract<int, string> value) => value.Offset;
    public static bool SharesInheritedSlot(object first, object second) =>
        first.GetType().GetInterfaceMap(typeof(Outer.Contract<int, string>)).TargetMethods[0].DeclaringType ==
        second.GetType().GetInterfaceMap(typeof(Outer.Contract<int, string>)).TargetMethods[0].DeclaringType;
}
