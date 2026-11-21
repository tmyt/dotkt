using roundtrip.genericvalueinterop;
using roundtrip.invariantstorage;

namespace GenericValueInterop;

public interface IProjectedMark { }
public interface IMarkedTag<T> { }
public sealed class TwoMarkedTags : IMarkedTag<int>, IMarkedTag<string> { }
public sealed class ForeignFirstSelector : roundtrip.constrainedbound.MarkedSelector
{
    public override object selectMarkedProjected<A, Storage, Proof>(object left, object right) => left;
    public static object FromExact(roundtrip.constrainedbound.MarkedSelector selector,
        roundtrip.constrainedbound.ConcreteMarkedItem first, roundtrip.constrainedbound.ConcreteMarkedItem last) =>
        selector.selectMarked<roundtrip.constrainedbound.ConcreteMarkedItem, roundtrip.constrainedbound.ConcreteMarkedItem>(first, last);
}
public class MarkedStreamReceiver
{
    public virtual string Source<T>(T item) where T : System.IO.MemoryStream, IProjectedMark => "base";
    protected string Inherited<T>(T item) where T : System.IO.MemoryStream, IProjectedMark => "protected";
    public T Keep<T>(T item) where T : System.IO.MemoryStream, IProjectedMark => item;
    public T WriteAliased<T>(T item, ref int first, ref int second) where T : System.IO.MemoryStream, IProjectedMark
    {
        first = 8;
        if (second != 8) throw new System.InvalidOperationException("Expected the same managed location");
        second = 42;
        return item;
    }
}

public interface NativeBoxReceiver
{
    object Accept(Box<string, string> value);
}

public sealed class NativeBoxContainer<T>(T value) { public T Value { get; } = value; }
public delegate NativeBoxContainer<Box<string, string>> NativeBoxContainerFactory();
public interface NativeBoxCreator { Box<string, string> Create(); }
public delegate Box<string, string> NativeBoxFactory();
public delegate object NativeBoxConsumer(Box<string, string> value);

public static class NativeBoxApi
{
    public static B SecondMarkedStream<A, B>(A first, B second)
        where A : System.IO.MemoryStream, IProjectedMark where B : System.IO.MemoryStream, IProjectedMark => second;
    public static string FirstMarkedType<A, B>(A first, B second, IMarkedTag<int> tag)
        where A : System.IO.MemoryStream, IProjectedMark where B : System.IO.MemoryStream, IProjectedMark => typeof(A).Name;
    public static B DependentMarkedStream<A, B>(A first, B second)
        where A : System.IO.MemoryStream, IProjectedMark where B : A => second;
    static Box<string, string> live = new("initial");
    public static void ResetLive() => live = new("initial");
    public static ref Box<string, string> Live() => ref live;
    public static Box<string, string> LiveValue() => live;
    public static void SetLive(Box<string, string> value) => live = value;

    public static Box<string, string> Echo(Box<string, string> value) => value;
    public static Box<string, string>[] EchoBoxes(Box<string, string>[] values) => values;
    public static NativeBoxContainer<Box<string, string>> EchoContainer(NativeBoxContainer<Box<string, string>> value) => value;
    public static NativeBoxContainer<Box<string, string>> InvokeContainer(NativeBoxContainerFactory factory) => factory();
    public static Box<string, string> InvokeCreator(NativeBoxCreator factory) => factory.Create();
    public static Box<string, string> InvokeFactory(NativeBoxFactory factory) => factory();
    public static object InvokeConsumer(NativeBoxConsumer consumer, Box<string, string> value) => consumer(value);
    public static StoredVariant<int, int> CreateVariant() => new(42);
    public static int ReadVariant(StoredVariant<int, int> value) => (int)value.value;
    public static object Dispatch(NativeBoxReceiver receiver, Box<string, string> value) => receiver.Accept(value);

    public static bool ReplaceAliased(ref Box<string, string> first, ref Box<string, string> second)
    {
        first = new Box<string, string>("first");
        bool observedFirstWrite = object.ReferenceEquals(first, second);
        second = new Box<string, string>("second");
        return observedFirstWrite && object.ReferenceEquals(first, second);
    }
}
