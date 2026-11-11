#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;
using A = DotKt.Cir.AttributeArgument;

namespace DotKt.Cir;

public static class AttributeSelfTest
{
    public static void Run(JsonSerializerOptions outerWriterOptions)
    {
        A[] arguments = [
            new A.NullValue(), new A.BooleanValue(false), new A.CharValue(65535),
            new A.Int8Value(sbyte.MinValue), new A.UInt8Value(byte.MaxValue),
            new A.Int16Value(short.MinValue), new A.UInt16Value(ushort.MaxValue),
            new A.Int32Value(int.MinValue), new A.UInt32Value(uint.MaxValue),
            new A.Int64Value(long.MinValue), new A.UInt64Value(ulong.MaxValue),
            new A.Float32Value(unchecked((int)0x80000000)), new A.Float32Value(0x7fc12345),
            new A.Float64Value(unchecked((long)0x8000000000000000)), new A.Float64Value(0x7ff8123456789abc),
            new A.TextValue("metadata \"\\\nκ😀"), new A.Utf16Value([0xd800, 0x0000, 0xdfff]),
            new A.BytesValue([0, 1, 127, 128, 255]),
            new A.TypeValue(new TypeNode.Fqn("Generic", [new TypeNode.Tv("method", 2)])),
            new A.ArrayValue([new A.NullValue(), new A.BooleanValue(true)], new TypeNode.Fqn("System.Object")),
            new A.EnumValue("System.UInt64", "18446744073709551615", 1, new TypeNode.Fqn("Physical.Enum")),
            new A.UnencodableValue(),
        ];
        var parameterTypes = arguments.Select(_ => (TypeNode)new TypeNode.Fqn("System.Object")).ToArray();
        var member = new MemberRefNode("ctor", "Physical", new TypeNode.Fqn("Physical.Attribute"), ".ctor", 0,
            MemberRefNode.Void, MemberRefNode.Instance, parameterTypes);
        var application = new AttributeApplication(new TypeNode.Fqn("Physical.Attribute"),
            parameterTypes, arguments, true, "Physical", member,
            [new NamedAttributeArgument("field", "Payload", new TypeNode.Array(new TypeNode.Fqn("System.Byte")),
                new A.BytesValue([42]))]);
        var first = JsonSerializer.Serialize(application, AttributeJson.Options);
        using var document = JsonDocument.Parse(first, BirJson.DocOptions);
        var parsed = document.RootElement.Deserialize<AttributeApplication>(AttributeJson.Options)!;
        var second = JsonSerializer.Serialize(parsed, AttributeJson.Options);
        if (first != second) throw new Exception("Physical attribute roundtrip changed a value");
        var container = new JsonObject { ["attribute"] = JsonValue.Create(application),
            ["default"] = JsonValue.Create<A>(arguments[18]) };
        using var boundary = JsonDocument.Parse(container.ToJsonString(outerWriterOptions));
        if (JsonSerializer.Serialize(boundary.RootElement.GetProperty("attribute").Deserialize<AttributeApplication>(AttributeJson.Options), AttributeJson.Options) != first
            || boundary.RootElement.GetProperty("default").Deserialize<A>(AttributeJson.Options) is not A.TypeValue)
            throw new Exception("Typed metadata changed at the current output-container boundary");
        var reordered = new JsonObject();
        foreach (var pair in ((JsonObject)JsonNode.Parse(first)!).Reverse())
            reordered[pair.Key] = pair.Value?.DeepClone();
        using var reorderedDocument = JsonDocument.Parse(reordered.ToJsonString());
        if (JsonSerializer.Serialize(reorderedDocument.RootElement.Deserialize<AttributeApplication>(AttributeJson.Options), AttributeJson.Options) != first)
            throw new Exception("Attribute decoding depends on root property order");
        var seen = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Remember(string tag, JsonObject value)
        {
            if (!seen.TryGetValue(tag, out var fields)) seen[tag] = fields = new(StringComparer.Ordinal);
            fields.UnionWith(value.Select(pair => pair.Key));
        }
        void Walk(JsonNode? value)
        {
            if (value is JsonObject obj)
            {
                if (obj["a"] is JsonValue tag) Remember(tag.GetValue<string>(), obj);
                foreach (var pair in obj) Walk(pair.Value);
            }
            else if (value is JsonArray array) foreach (var child in array) Walk(child);
        }
        var root = (JsonObject)JsonNode.Parse(first)!;
        Remember("AttributeApplication", root);
        Remember("NamedAttributeArgument", (JsonObject)root["namedArgs"]![0]!);
        Walk(root);
        foreach (var (tag, fields) in AttributeWireContract.Properties)
            if (!seen.TryGetValue(tag, out var actual) || !actual.SetEquals(fields))
                throw new Exception($"Physical attribute schema field coverage mismatch: {tag}");
        Console.WriteLine($"[CIR attributes] self-test OK ({arguments.Length} values, IEEE/UTF16/bytes, schema field coverage)");
    }
}
