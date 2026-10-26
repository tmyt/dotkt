using roundtrip.genericvalueinterop;
using roundtrip.invariantstorage;

namespace GenericValueInterop;

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
    public static int ReadVariant(StoredVariant<int, int> value) => value.value;
    public static object Dispatch(NativeBoxReceiver receiver, Box<string, string> value) => receiver.Accept(value);

    public static bool ReplaceAliased(ref Box<string, string> first, ref Box<string, string> second)
    {
        first = new Box<string, string>("first");
        bool observedFirstWrite = object.ReferenceEquals(first, second);
        second = new Box<string, string>("second");
        return observedFirstWrite && object.ReferenceEquals(first, second);
    }
}
