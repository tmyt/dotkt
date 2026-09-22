namespace InheritedStatics;

public class Base
{
    public static string Field = "base";
    public static string Property { get; set; } = "base property";
    public static string Method() => "base method";
    public static readonly int Readonly = 59;
    public static event System.Action<int> Changed;
    public static void Raise(int value) => Changed?.Invoke(value);
}
public class Leaf : Base { }
public class Middle : Base
{
    public new static int Field = 41;
    public new static int Property { get; set; } = 43;
    public new static string Method() => "middle method";
}
public class DeepLeaf : Middle { }

public class GenericBase<T>
{
    public static T Field;
    public static T Property { get; set; }
    public static T Store(T value) { Field = value; return value; }
}
public class StringLeaf : GenericBase<string> { }
public class IntLeaf : GenericBase<int> { }

public class PairBase<A, B>
{
    public static A First;
    public static B Second { get; set; }
    public static U Store<U>(A first, B second, U result) where U : class
    {
        First = first;
        Second = second;
        return result;
    }
}
public class Swap<X, Y> : PairBase<Y, X> { }
public class PairLeaf : Swap<int, string> { }

// Reading from independently compiled C# proves the Kotlin calls use the same
// constructed owner, not a mutually consistent but incorrect erased storage.
public static class Storage
{
    public static string StringField() => GenericBase<string>.Field;
    public static string StringProperty() => GenericBase<string>.Property;
    public static int IntField() => GenericBase<int>.Field;
    public static int IntProperty() => GenericBase<int>.Property;
    public static string PairFirst() => PairBase<string, int>.First;
    public static int PairSecond() => PairBase<string, int>.Second;
    public static bool ObjectUntouched() => GenericBase<object>.Field is null && GenericBase<object>.Property is null;
}
