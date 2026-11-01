namespace UnsignedArrayMemberInterop;

public static class NativeArrays
{
    public static byte[] Bytes() => [0, 128, byte.MaxValue];
    public static ushort[] Shorts() => [0, 32768, ushort.MaxValue];
    public static uint[] Ints() => [0, 2147483648u, uint.MaxValue];
    public static ulong[] Longs() => [0, 9223372036854775808uL, ulong.MaxValue];

    public static void Touch(byte[] bytes, ushort[] shorts, uint[] ints, ulong[] longs)
    {
        bytes[0] = byte.MaxValue;
        shorts[0] = ushort.MaxValue;
        ints[0] = uint.MaxValue;
        longs[0] = ulong.MaxValue;
    }

    public static void Replace(ref byte[] bytes, ref ushort[] shorts, ref uint[] ints, out ulong[] longs)
    {
        bytes = [7];
        shorts = [7];
        ints = [7];
        longs = [7];
    }
}
