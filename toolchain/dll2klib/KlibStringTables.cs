using DotKt.Klib.Metadata;

// The manifest selects this whole-table codec; no string spelling is a discriminator.
internal static class KlibStringTables
{
    internal const string ManifestProperty = "dotkt_string_table_codec";
    internal const string Codec = "utf16le-base64";

    internal static bool EncodeIfNeeded(IReadOnlyList<Fragment> fragments)
    {
        if (!fragments.Any(fragment => fragment.Message.Strings.String.Any(HasUnpairedSurrogate)))
            return false;
        foreach (var fragment in fragments)
            for (int index = 0; index < fragment.Message.Strings.String.Count; index++)
                fragment.Message.Strings.String[index] = Encode(fragment.Message.Strings.String[index]);
        return true;
    }

    internal static string Encode(string text)
    {
        var bytes = new byte[checked(text.Length * 2)];
        for (int index = 0; index < text.Length; index++)
        {
            bytes[index * 2] = (byte)text[index];
            bytes[index * 2 + 1] = (byte)(text[index] >> 8);
        }
        return Convert.ToBase64String(bytes);
    }

    static bool HasUnpairedSurrogate(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (!char.IsSurrogate(text[index])) continue;
            if (!char.IsSurrogatePair(text, index)) return true;
            index++;
        }
        return false;
    }
}
