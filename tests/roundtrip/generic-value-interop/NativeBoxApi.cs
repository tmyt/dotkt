using roundtrip.genericvalueinterop;

namespace GenericValueInterop;

public interface NativeBoxReceiver
{
    object Accept(Box<string, string> value);
}

public static class NativeBoxApi
{
    public static Box<string, string> Echo(Box<string, string> value) => value;
    public static object Dispatch(NativeBoxReceiver receiver, Box<string, string> value) => receiver.Accept(value);

    public static bool ReplaceAliased(ref Box<string, string> first, ref Box<string, string> second)
    {
        first = new Box<string, string>("first");
        bool observedFirstWrite = object.ReferenceEquals(first, second);
        second = new Box<string, string>("second");
        return observedFirstWrite && object.ReferenceEquals(first, second);
    }
}
