using System;

namespace FlagsInterop;

[Flags]
public enum AccessFlags
{
    None = 0,
    Read = 1,
    AliasRead = Read,
    Write = 2,
    ReadWrite = Read | Write,
    Execute = 4,
}

[Flags] public enum SByteFlags : sbyte { None = 0, Low = 1, High = sbyte.MinValue, NotLow = -2 }
[Flags] public enum ByteFlags : byte { None = 0, Low = 1, High = 0x80, NotLow = 0xfe }
[Flags] public enum Int16Flags : short { None = 0, Low = 1, High = short.MinValue, NotLow = -2 }
[Flags] public enum UInt16Flags : ushort { None = 0, Low = 1, High = 0x8000, NotLow = 0xfffe }
[Flags] public enum Int32Flags : int { None = 0, Low = 1, High = int.MinValue, NotLow = -2 }
[Flags] public enum UInt32Flags : uint { None = 0, Low = 1, High = 0x80000000u, NotLow = 0xfffffffeu }
[Flags] public enum Int64Flags : long { None = 0, Low = 1, High = long.MinValue, NotLow = -2 }
[Flags] public enum UInt64Flags : ulong { None = 0, Low = 1, High = 0x8000000000000000ul, NotLow = 0xfffffffffffffffeul }

public enum PlainEnum
{
    None = 0,
    First = 1,
    Second = 2,
}

[Flags]
public enum OtherFlags
{
    None = 0,
    First = 1,
}

public static class FlagsApi
{
    public static AccessFlags Unknown() => (AccessFlags)8;
    public static AccessFlags RoundTrip(AccessFlags value) => value;
    public static int Bits(AccessFlags value) => (int)value;
}
