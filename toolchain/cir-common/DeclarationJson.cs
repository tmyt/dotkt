#nullable enable
using System.IO;
using System.Text.Json;

namespace DotKt.Cir;

public static class DeclarationJson
{
    static readonly JsonSerializerOptions IndentedOptions = new(AttributeJson.Options) { WriteIndented = true };
    // AttributeJson's options already own the shared TypeNode codec and strict current-record reading.
    // OptionalField and executable-adapter converters are attached to their concrete structural types.
    public static CirFileDeclaration ReadFile(string path) => Read(File.ReadAllBytes(path));
    public static CirFileDeclaration Read(byte[] utf8)
        => JsonSerializer.Deserialize<CirFileDeclaration>(utf8, AttributeJson.Options) ?? throw new JsonException();
    public static string Write(CirFileDeclaration file, bool indented = false)
        => JsonSerializer.Serialize(file, indented ? IndentedOptions : AttributeJson.Options);
}
