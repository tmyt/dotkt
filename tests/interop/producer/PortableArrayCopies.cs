namespace PortableArrayCopyInterop;

public static class NativeArrays
{
    public static string[] Text() => ["first", "last"];
    public static uint[] Unsigned() => [uint.MaxValue, 0, 42];
    public static string Component(object array) => array.GetType().GetElementType()!.FullName!;
    public static bool BoxedUInts(object array)
    {
        var values = (object[])array;
        return values.Length == 4 && values[0] is uint first && first == uint.MaxValue
            && values[1] is uint zero && zero == 0 && values[2] is uint last && last == 42
            && values[3] is null;
    }
}
