using System;
using System.Globalization;

namespace EnumLiteralInterop;

public static class Entries
{
    public enum Code { Sparse = 73, Negative = -19, Zero = 0, Alias = Sparse }
    public enum I8 : sbyte { Sparse = 17, High = sbyte.MinValue }
    public enum U8 : byte { Sparse = 17, High = byte.MaxValue }
    public enum I16 : short { Sparse = 317, High = short.MinValue }
    public enum U16 : ushort { Sparse = 317, High = ushort.MaxValue }
    public enum I32 : int { Sparse = 70001, High = int.MinValue }
    public enum U32 : uint { Sparse = 70001, High = uint.MaxValue }
    public enum I64 : long { Sparse = 9000000000, High = long.MinValue }
    public enum U64 : ulong { Sparse = 9000000000, High = ulong.MaxValue }
    [Flags] public enum Mask : byte { High = 0x80, Low = 2, AliasLow = Low }
}

public enum TopCode { Sparse = 103, Negative = -7, Alias = Sparse }
public enum OrderedU32 : uint { High = uint.MaxValue, Zero = 0, Sparse = 70001, Alias = High }
public enum OrderedU64 : ulong { High = ulong.MaxValue, Zero = 0, Sparse = 9000000000, Alias = High }
public enum OrderedI64 : long { High = long.MinValue, Zero = 0, Sparse = long.MaxValue }

public static class EnumOrderApi
{
    public static TopCode Negative() => TopCode.Negative;
    public static TopCode Sparse() => TopCode.Sparse;
    public static TopCode Alias() => TopCode.Alias;
    public static OrderedU32 U32High() => OrderedU32.High;
    public static OrderedU64 U64High() => OrderedU64.High;
    public static OrderedI64 I64High() => OrderedI64.High;
    public static string U32Bits(OrderedU32 value) => ((uint)value).ToString(CultureInfo.InvariantCulture);
    public static string U64Bits(OrderedU64 value) => ((ulong)value).ToString(CultureInfo.InvariantCulture);
    public static string I64Bits(OrderedI64 value) => ((long)value).ToString(CultureInfo.InvariantCulture);
}

public static class EnumLiteralApi
{
    public static int Receive(Entries.Code value) => (int)value;
    public static int ReceiveTop(TopCode value) => (int)value;
    public static int ReceiveMask(Entries.Mask value) => (byte)value;
    public static int ReceiveProfile(Environment.SpecialFolder value) => (int)value;
    public static string ProfileControl() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string Widths(
        Entries.I8 i8, Entries.U8 u8, Entries.I16 i16, Entries.U16 u16,
        Entries.I32 i32, Entries.U32 u32, Entries.I64 i64, Entries.U64 u64) =>
        string.Join(":", ((sbyte)i8).ToString(CultureInfo.InvariantCulture),
            ((byte)u8).ToString(CultureInfo.InvariantCulture), ((short)i16).ToString(CultureInfo.InvariantCulture),
            ((ushort)u16).ToString(CultureInfo.InvariantCulture), ((int)i32).ToString(CultureInfo.InvariantCulture),
            ((uint)u32).ToString(CultureInfo.InvariantCulture), ((long)i64).ToString(CultureInfo.InvariantCulture),
            ((ulong)u64).ToString(CultureInfo.InvariantCulture));
}
