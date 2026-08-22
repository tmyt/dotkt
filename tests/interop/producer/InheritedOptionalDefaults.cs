namespace InheritedOptionalDefaults;

public class BaseWriter
{
    public string Save(string path, int? quality = null) =>
        $"{path}:{(quality.HasValue ? quality.Value.ToString() : "default")}";
}

public sealed class DerivedWriter : BaseWriter
{
    public string Save(int id, int? quality = 99) =>
        $"{id}:{(quality.HasValue ? quality.Value.ToString() : "default")}";
}

public class GenericBaseWriter<T>
{
    public string Save(T value, int quality = 1) => $"{value}:{quality}";
}

public sealed class GenericDerivedWriter<T> : GenericBaseWriter<T>
{
    public string Save(string value, int quality = 99) => $"{value}:{quality}";
}

public class ValueBaseWriter
{
    public string Save(string value, int quality = 5) => $"{value}:{quality}";
}

public sealed class HidingDerivedWriter : ValueBaseWriter
{
    public new string Save(string value, int quality = 7) => $"{value}:{quality}";
}

public interface IBaseWriter
{
    string Save(string value, int quality = 1);
}

public interface IDerivedWriter : IBaseWriter
{
    string Save(int value, int quality = 99);
}

public sealed class InterfaceWriter : IDerivedWriter
{
    public string Save(string value, int quality = 1) => $"{value}:{quality}";
    public string Save(int value, int quality = 99) => $"{value}:{quality}";
}

public enum NavigationMethod
{
    Unspecified = 0,
    Directional = 2,
}

[System.Flags]
public enum KeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
}

public sealed class EnumDefaults
{
    public string Focus(
        NavigationMethod method = NavigationMethod.Unspecified,
        KeyModifiers modifiers = KeyModifiers.None) => $"{(int)method}:{(int)modifiers}";

    public string Move(
        NavigationMethod method = NavigationMethod.Directional,
        KeyModifiers modifiers = KeyModifiers.Alt | KeyModifiers.Shift) => $"{(int)method}:{(int)modifiers}";
}

public static class StaticEnumDefaults
{
    public static string Move(
        NavigationMethod method = NavigationMethod.Directional,
        KeyModifiers modifiers = KeyModifiers.Alt | KeyModifiers.Shift) => $"{(int)method}:{(int)modifiers}";
}

public enum SByteDefault : sbyte { Value = sbyte.MinValue }
public enum ByteDefault : byte { Value = byte.MaxValue }
public enum Int16Default : short { Value = short.MinValue }
public enum UInt16Default : ushort { Value = ushort.MaxValue }
public enum Int32Default : int { Value = int.MinValue }
public enum UInt32Default : uint { Value = uint.MaxValue }
public enum Int64Default : long { Value = long.MinValue }
public enum UInt64Default : ulong { Value = ulong.MaxValue }

public sealed class EnumWidthDefaults
{
    public string Read(
        SByteDefault i8 = SByteDefault.Value,
        ByteDefault u8 = ByteDefault.Value,
        Int16Default i16 = Int16Default.Value,
        UInt16Default u16 = UInt16Default.Value,
        Int32Default i32 = Int32Default.Value,
        UInt32Default u32 = UInt32Default.Value,
        Int64Default i64 = Int64Default.Value,
        UInt64Default u64 = UInt64Default.Value) =>
        $"{(sbyte)i8}:{(byte)u8}:{(short)i16}:{(ushort)u16}:{(int)i32}:{(uint)u32}:{(long)i64}:{(ulong)u64}";
}

public class ValueTypeDefaults
{
    const decimal ExpectedDecimal = -12345678901234567890.1234m;

    public bool Instance(decimal value = ExpectedDecimal, System.DateTime when = default) =>
        value == ExpectedDecimal && when == default;

    public static bool Static(decimal value = ExpectedDecimal, System.DateTime when = default) =>
        value == ExpectedDecimal && when == default;

    public static T GenericDefault<T>(T value = default) => value;

    public long DateTimeConstant(
        [System.Runtime.InteropServices.Optional]
        [System.Runtime.CompilerServices.DateTimeConstant(638000000000000000)] System.DateTime when) => when.Ticks;
}

public sealed class DerivedValueTypeDefaults : ValueTypeDefaults { }

public sealed class NullableValueDefaults
{
    public string Instance(int? count = -7, NavigationMethod? method = NavigationMethod.Directional) =>
        $"{count}:{(int?)method}";

    public static string Static(int? count = 42, NavigationMethod? method = NavigationMethod.Unspecified) =>
        $"{count}:{(int?)method}";
}

public sealed class ValueTypeDefaultConstructor
{
    const decimal ExpectedDecimal = -12345678901234567890.1234m;

    public ValueTypeDefaultConstructor(decimal value = ExpectedDecimal, System.DateTime when = default) =>
        ValuesMatch = value == ExpectedDecimal && when == default;

    public bool ValuesMatch { get; }
}
