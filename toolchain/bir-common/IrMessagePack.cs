#nullable enable
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using MessagePack;

namespace DotKt.Bir;

// MessagePack strings are UTF-8. Kotlin/CLR strings are UTF-16 code units and may
// contain lone surrogates: extension 1 stores those strings as little-endian units.
// This is a transport representation, not a new logical IR value or compatibility path.
public static class IrMessagePack
{
    public const sbyte Utf16Extension = 1;
    static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    public static void WriteString(ref MessagePackWriter writer, string value)
    {
        if (!LiteralText.RequiresCodeUnits(value)) { writer.Write(value); return; }
        var bytes = new byte[checked(value.Length * 2)];
        for (var i = 0; i < value.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), value[i]);
        writer.WriteExtensionFormat(new ExtensionResult(Utf16Extension, bytes));
    }

    public static string ReadString(ref MessagePackReader reader)
    {
        if (reader.NextMessagePackType == MessagePackType.String)
        {
            var text = reader.ReadStringSequence()!.Value;
            return text.IsSingleSegment ? StrictUtf8.GetString(text.FirstSpan) : StrictUtf8.GetString(text.ToArray());
        }
        var extension = reader.ReadExtensionFormat();
        if (extension.TypeCode != Utf16Extension || extension.Data.Length % 2 != 0) throw new FormatException();
        var bytes = extension.Data.ToArray();
        var units = new char[bytes.Length / 2];
        for (var i = 0; i < units.Length; i++) units[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2));
        return new string(units);
    }
}
