#nullable disable
using System.Globalization;
using System.Reflection.Metadata;
using System.Text.Json;

// These are physical build-driver facts, independent of Kotlin declarations and the CIR vocabulary.
sealed class SdkAssemblyOptions
{
    public string OutputFileName { get; set; }
    public string OutputType { get; set; }
    public bool Deterministic { get; set; }
    public bool NoWin32Manifest { get; set; }
    public string Win32Manifest { get; set; }
    public string Win32Resource { get; set; }
    public string ApplicationIcon { get; set; }
    public string NativeResource { get; set; }
    public List<AttributeValue> Attributes { get; set; } = [];
    public sealed record AttributeValue(string Type, string Value, string IsLiteral, string TypeName);

    public static SdkAssemblyOptions Read(string path) => JsonSerializer.Deserialize<SdkAssemblyOptions>(
        File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException($"Empty SDK assembly options: {path}");

    public string Attribute(string name)
    {
        var matches = Attributes.Where(a => a.Type == "System.Reflection." + name ||
            a.Type == "System.Reflection." + name + "Attribute").ToArray();
        if (matches.Length > 1) throw new InvalidDataException($"Duplicate assembly attribute: {name}");
        if (matches.Length == 0) return null;
        var value = matches[0];
        if (string.Equals(value.IsLiteral, "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{name}: language source fragments (_Parameter1_IsLiteral) are not supported in SDK assembly values; provide the evaluated string value");
        if (value.TypeName is not (null or "" or "System.String"))
            throw new InvalidDataException($"{name} requires a string parameter");
        return value.Value;
    }

    public AssemblyVersions Versions()
    {
        var identity = ParseIdentity(Attribute("AssemblyVersion"), Deterministic);
        var attributes = new List<(string, byte[])>();
        foreach (var name in new[] { "AssemblyFileVersion", "AssemblyInformationalVersion" })
        {
            var value = Attribute(name);
            if (value == null) continue;
            var blob = new BlobBuilder();
            blob.WriteUInt16(1);
            blob.WriteSerializedString(value);
            blob.WriteUInt16(0);
            attributes.Add(("System.Reflection." + name + "Attribute", blob.ToArray()));
        }
        return new AssemblyVersions(identity, attributes);
    }

    static Version ParseIdentity(string text, bool deterministic)
    {
        if (text == null) return new Version(0, 0, 0, 0);
        var parts = text.Split('.');
        var wildcard = parts[^1] == "*";
        if (parts.Length > 4 || (wildcard && (parts.Length < 3 || deterministic)))
            throw new InvalidDataException($"Invalid assembly version '{text}' (wildcards require Deterministic=false)");
        var values = new int[4];
        for (var i = 0; i < parts.Length - (wildcard ? 1 : 0); i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]) || values[i] >= ushort.MaxValue)
                throw new InvalidDataException($"Invalid assembly version '{text}'");
        if (wildcard)
        {
            var now = DateTime.Now;
            if (parts.Length == 3) values[2] = Math.Min(ushort.MaxValue, (now.Date - new DateTime(2000, 1, 1)).Days);
            values[3] = (int)now.TimeOfDay.TotalSeconds / 2;
        }
        return new Version(values[0], values[1], values[2], values[3]);
    }
}
