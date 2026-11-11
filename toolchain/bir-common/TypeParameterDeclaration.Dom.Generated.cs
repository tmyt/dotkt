// Generated from docs/bir-cir.schema.json; run scripts/generate-ir-model.py.
#nullable enable
using System;
using System.Linq;
using System.Text.Json.Nodes;
namespace DotKt.Bir;

// Temporary boundary to not-yet-migrated enclosing declarations.
public sealed partial record TypeParameterDeclaration
{
    public static TypeParameterDeclaration Read(JsonNode node)
    {
        var source = (JsonObject)node;
        foreach (var field in source)
            switch (field.Key)
            {
                case "name": break;
                case "reified": break;
                case "variance": break;
                case "constraints": break;
                case "erasedInnerConstraints": break;
                case "specialConstraints": break;
                default: throw new FormatException();
            }
        return new TypeParameterDeclaration(
            source["name"]!.GetValue<string>(),
            source.ContainsKey("reified") ? source["reified"]!.GetValue<bool>() : null,
            source.ContainsKey("variance") ? source["variance"]!.GetValue<string>() : null,
            source.ContainsKey("constraints") ? ((JsonArray)source["constraints"]!).Select(item => TypeNode.Read(item!) ?? throw new FormatException()).ToArray() : null,
            source.ContainsKey("erasedInnerConstraints") ? ((JsonArray)source["erasedInnerConstraints"]!).Select(item => TypeNode.Read(item!) ?? throw new FormatException()).ToArray() : null,
            source.ContainsKey("specialConstraints") ? ((JsonArray)source["specialConstraints"]!).Select(item => item!.GetValue<string>()).ToArray() : null);
    }

    public JsonObject ToDom()
    {
        var result = new JsonObject();
        result["name"] = Name;
        if (Reified is { } reified)
        {
            result["reified"] = reified;
        }
        if (Variance is { } variance)
        {
            result["variance"] = variance;
        }
        if (Constraints is { } constraints)
        {
            result["constraints"] = new JsonArray(constraints.Select(item => TypeNode.Write(item)).ToArray());
        }
        if (ErasedInnerConstraints is { } erasedInnerConstraints)
        {
            result["erasedInnerConstraints"] = new JsonArray(erasedInnerConstraints.Select(item => TypeNode.Write(item)).ToArray());
        }
        if (SpecialConstraints is { } specialConstraints)
        {
            result["specialConstraints"] = new JsonArray(specialConstraints.Select(item => (JsonNode)JsonValue.Create(item)!).ToArray());
        }
        return result;
    }
}
