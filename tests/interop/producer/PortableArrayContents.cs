#nullable enable

namespace PortableArrayContentInterop;

public static class Arrays
{
    public static string?[] Text() => ["first", null];
    public static sbyte[] Bytes() => [sbyte.MinValue, 0, sbyte.MaxValue];
    public static short[] Shorts() => [short.MinValue, 0, short.MaxValue];
    public static int[] Ints() => [int.MinValue, 0, int.MaxValue];
    public static long[] Longs() => [long.MinValue, 0, long.MaxValue];
    public static float[] Floats() => [float.NaN, -0.0f, 0.0f];
    public static double[] Doubles() => [double.NaN, -0.0, 0.0];
    public static bool[] Booleans() => [false, true];
    public static char[] Chars() => ['\0', 'x'];
    public static void Change(int[] values) => values[0] = 42;
}
