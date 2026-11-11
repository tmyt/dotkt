using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Source declarations are read directly into their semantic model. Remaining BIR
// domains still enter their existing owning DOM passes until those domains migrate.
static class BirInputJson
{
    public static (JsonObject Root, SourceTypeAlias[] Aliases) Read(byte[] content)
    {
        var reader = new Utf8JsonReader(content, new JsonReaderOptions {
            MaxDepth = BirJson.DocOptions.MaxDepth, CommentHandling = BirJson.DocOptions.CommentHandling,
            AllowTrailingCommas = BirJson.DocOptions.AllowTrailingCommas,
        });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new FormatException();
        var root = new JsonObject();
        SourceTypeAlias[] aliases = [];
        var seenAliases = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new FormatException();
            var name = reader.GetString()!;
            if (!reader.Read()) throw new FormatException();
            if (name == "typeAliases")
            {
                if (seenAliases) throw new FormatException();
                seenAliases = true;
                aliases = SourceMetadataJson.ReadAliases(ref reader);
            }
            else root.Add(name, JsonNode.Parse(ref reader));
        }
        if (reader.TokenType != JsonTokenType.EndObject || reader.Read()) throw new FormatException();
        return (root, aliases);
    }
}
