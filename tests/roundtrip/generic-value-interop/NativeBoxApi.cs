using roundtrip.genericvalueinterop;

namespace GenericValueInterop;

public static class NativeBoxApi
{
    public static bool ReplaceAliased(ref Box<string, string> first, ref Box<string, string> second)
    {
        first = new Box<string, string>("first");
        bool observedFirstWrite = object.ReferenceEquals(first, second);
        second = new Box<string, string>("second");
        return observedFirstWrite && object.ReferenceEquals(first, second);
    }
}
