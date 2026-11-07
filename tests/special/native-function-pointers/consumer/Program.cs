using NativePointers;

// A separately compiled C# caller forces both native generic frames to agree
// with the Kotlin-produced declaration and with its external MemberRef.
static unsafe class Program
{
    static int Add(string value, int number) => value.Length + number;
    static string Identity(string value) => value;
    static void Main()
    {
        var callback = ConsumerKt.generic<string, int>(new GenericApi<string>(), &Add);
        if (callback("abc", 39) != 42) throw new Exception("Native generic method/owner frames changed");
        var field = ConsumerKt.genericField<string>(new GenericApi<string>(), &Identity);
        if (field("frame") != "frame") throw new Exception("Native generic field/property frames changed");
        Console.WriteLine("Independent C# native generic-frame invocation: PASS");
    }
}
