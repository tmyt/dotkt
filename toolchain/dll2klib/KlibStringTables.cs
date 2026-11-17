using DotKt.Klib.Metadata;

// The manifest selects the indexed table codec; no source string spelling is a discriminator.
internal static class KlibStringTables
{
    internal const string ManifestProperty = "dotkt_string_table_codec";
    internal const string Codec = "utf16le-base64-indexed";

    internal static bool EncodeIfNeeded(IReadOnlyList<Fragment> fragments)
    {
        if (!fragments.Any(fragment => fragment.Message.Strings.String.Any(HasUnpairedSurrogate)))
            return false;
        foreach (var fragment in fragments)
        {
            var encoded = new List<int>();
            for (int index = 0; index < fragment.Message.Strings.String.Count; index++)
                if (HasUnpairedSurrogate(fragment.Message.Strings.String[index]))
                {
                    encoded.Add(index);
                    fragment.Message.Strings.String[index] = Encode(fragment.Message.Strings.String[index]);
                }
            // This codec reserves the final row for explicit indexes; existing source indexes never move.
            fragment.Message.Strings.String.Add(string.Join(',', encoded));
        }
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
