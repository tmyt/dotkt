using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using DotKt.Bir;

internal static class KotlinTypeMetadata
{
    internal static JsonElement Read(IEnumerable<CustomAttributeData> attributes,
        string carrier = "KotlinTypeAttribute")
    {
        var attribute = attributes.Single(value => value.AttributeType.FullName ==
            "DotKt.Runtime.CompilerServices." + carrier);
        var codec = (string)attribute.ConstructorArguments[0].Value!;
        Assert.That(codec, Is.EqualTo(TypeNodeCarrier.Codec));
        var bytes = ((IEnumerable<CustomAttributeTypedArgument>)attribute.ConstructorArguments[1].Value!)
            .Select(value => (byte)value.Value!).ToArray();
        using var document = JsonDocument.Parse(TypeNode.ToJson(TypeNodeCarrier.Decode(codec, bytes)));
        return document.RootElement.Clone();
    }

    internal static void Named(JsonElement type, string name)
    {
        Assert.That(type.GetProperty("t").GetString(), Is.EqualTo("fqn"));
        Assert.That(type.GetProperty("name").GetString(), Is.EqualTo(name));
    }

    internal static JsonElement Nullable(JsonElement type)
    {
        Assert.That(type.GetProperty("t").GetString(), Is.EqualTo("nullable"));
        return type.GetProperty("of");
    }
}
