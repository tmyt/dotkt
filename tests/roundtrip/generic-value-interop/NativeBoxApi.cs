using roundtrip.genericvalueinterop;

namespace GenericValueInterop;

public static class NativeBoxApi
{
    public static bool ReplaceAliased(ref Box<string> first, ref Box<string> second)
    {
        first = new Box<string>("first");
        bool observedFirstWrite = object.ReferenceEquals(first, second);
        second = new Box<string>("second");
        return observedFirstWrite && object.ReferenceEquals(first, second);
    }
}
