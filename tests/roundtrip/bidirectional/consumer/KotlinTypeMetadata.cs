using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;

internal static class KotlinTypeMetadata
{
    internal static JsonElement Read(IEnumerable<CustomAttributeData> attributes,
        string carrier = "KotlinTypeAttribute")
    {
        var attribute = attributes.Single(value => value.AttributeType.FullName ==
            "DotKt.Runtime.CompilerServices." + carrier);
        Assert.That(attribute.ConstructorArguments[0].Value, Is.EqualTo("bir-json/1"));
        var bytes = ((IEnumerable<CustomAttributeTypedArgument>)attribute.ConstructorArguments[1].Value!)
            .Select(value => (byte)value.Value!).ToArray();
        using var document = JsonDocument.Parse(bytes);
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
