#nullable enable
using System;
using System.Buffers;
using System.Buffers.Binary;
using MessagePack;

namespace DotKt.Bir;

// MessagePack strings are UTF-8. Kotlin/CLR strings are UTF-16 code units and may
// contain lone surrogates: extension 1 stores those strings as little-endian units.
// This is a transport representation, not a new logical IR value or compatibility path.
public static class IrMessagePack
{
    public const sbyte Utf16Extension = 1;

    public static void WriteString(ref MessagePackWriter writer, string value)
    {
        var hasLoneSurrogate = false;
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) i++;
            else { hasLoneSurrogate = true; break; }
        }
        if (!hasLoneSurrogate) { writer.Write(value); return; }
        var bytes = new byte[checked(value.Length * 2)];
        for (var i = 0; i < value.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), value[i]);
        writer.WriteExtensionFormat(new ExtensionResult(Utf16Extension, bytes));
    }

    public static string ReadString(ref MessagePackReader reader)
    {
        if (reader.NextMessagePackType == MessagePackType.String) return reader.ReadString()!;
        var extension = reader.ReadExtensionFormat();
        if (extension.TypeCode != Utf16Extension || extension.Data.Length % 2 != 0) throw new FormatException();
        var bytes = extension.Data.ToArray();
        var units = new char[bytes.Length / 2];
        for (var i = 0; i < units.Length; i++) units[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2));
        return new string(units);
    }
}
