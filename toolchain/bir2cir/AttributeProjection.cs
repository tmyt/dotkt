using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Cir;
using A = DotKt.Cir.AttributeArgument;

// Final physical metadata projection. This adapter disappears when the owning declaration passes
// construct typed CIR directly. It never walks executable children or saved source payloads.
static class AttributeProjection
{
    public static void ApplyAll(IReadOnlyList<JsonNode> roots)
    {
        var emitted = roots.OfType<JsonObject>()
            .SelectMany(file => file["types"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>().Select(type => Str(type["name"])).Where(name => name != null)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var file in roots.OfType<JsonObject>())
            Apply(file, emitted);
    }

    static void Apply(JsonObject file, IReadOnlySet<string> emitted)
    {
        Attributes(file, "assemblyAttrs", emitted);
        Container(file, emitted);
        if (file["types"] is JsonArray types)
            foreach (var type in types.OfType<JsonObject>()) Container(type, emitted);
    }

    static void Container(JsonObject container, IReadOnlySet<string> emitted)
    {
        Attributes(container, "attrs", emitted);
        if (Str(container["kind"]) == "delegate")
        {
            Attributes(container, "retAttrs", emitted);
            Parameters(container, emitted);
        }
        if (Str(container["kind"]) == "enum" && container["entries"] is JsonArray entries)
            foreach (var entry in entries.OfType<JsonObject>()) Attributes(entry, "attrs", emitted);
        foreach (var key in new[] { "methods", "ctors" })
            if (container[key] is JsonArray methods)
                foreach (var method in methods.OfType<JsonObject>())
                {
                    Attributes(method, "attrs", emitted);
                    Attributes(method, "retAttrs", emitted);
                    Parameters(method, emitted);
                }
        foreach (var key in new[] { "fields", "properties" })
            if (container[key] is JsonArray members)
                foreach (var member in members.OfType<JsonObject>()) Attributes(member, "attrs", emitted);
    }

    static void Parameters(JsonObject signature, IReadOnlySet<string> emitted)
    {
        if (signature["params"] is not JsonArray parameters) return;
        foreach (var parameter in parameters.OfType<JsonObject>())
        {
            Attributes(parameter, "attrs", emitted);
            if (parameter.ContainsKey("default"))
                parameter["default"] = JsonValue.Create<A>(Argument(parameter["default"]));
        }
    }

    static void Attributes(JsonObject declaration, string key, IReadOnlySet<string> emitted)
    {
        if (declaration[key] is not JsonArray attributes) return;
        for (int i = 0; i < attributes.Count; i++)
        {
            var source = (JsonObject)attributes[i];
            // This is the existing physical annotation-admission rule, applied by its owning layer.
            // Do not reconstruct a missing constructor signature from argument values.
            if (source["attrExternal"]?.GetValue<bool>() != true
                && !emitted.Contains(TypeJson.OwnerName(source["attr"])))
            {
                attributes.RemoveAt(i--);
                continue;
            }
            var model = new AttributeApplication(
                TypeJson.Read(source["attr"]),
                ((JsonArray)source["argTypes"]).Select(TypeJson.Read).ToArray(),
                ((JsonArray)source["args"]).Select(Argument).ToArray(),
                source["attrExternal"]?.GetValue<bool>(), Str(source["attrAssembly"]),
                source["memberRef"] is { } member ? MemberRefNode.Read(member) : null,
                source["namedArgs"] is JsonArray named ? named.Select(item => new NamedAttributeArgument(
                    Str(item["kind"]), Str(item["name"]), TypeJson.Read(item["type"]), Argument(item["value"]))).ToArray() : null);
            // Only the enclosing output container is still DOM-backed. The value itself is the typed CIR
            // record and its converter writes directly at the file boundary; no text/reparse bridge.
            attributes[i] = JsonValue.Create(model);
        }
    }

    static A Argument(JsonNode source)
    {
        if (source is not JsonObject value) throw new InvalidOperationException("Malformed annotation argument");
        if (value["bytes"] is { } bytes) return new A.BytesValue(Convert.FromBase64String(Str(bytes)));
        switch (Str(value["k"]))
        {
            case "classRef": return new A.TypeValue(TypeJson.Read(value["type"]));
            case "newArray":
                return new A.ArrayValue(((JsonArray)value["elems"]).Select(Argument).ToArray(),
                    value["elem"] is { } elem ? TypeJson.Read(elem) : null);
            case "enumValue":
                return new A.EnumValue(Str(value["underlying"]), Str(value["physicalValue"]),
                    value["ordinal"]?.GetValue<int>(), value["type"] is { } type ? TypeJson.Read(type) : null);
        }
        if (!value.ContainsKey("value")) return new A.UnencodableValue();
        if (value["value"] is null) return new A.NullValue();
        return Literal(value["value"], TypeJson.Read(value["type"]));
    }

    public static A Literal(JsonNode literal, TypeNode type)
    {
        if (literal is null) return new A.NullValue();
        while (type is TypeNode.Mod modifier) type = modifier.Of;
        var hint = type as TypeNode.Fqn;
        if (literal is JsonObject || hint?.Name is "System.String" or "string")
            return Text(LiteralText.Read(literal));
        if (literal.GetValueKind() == JsonValueKind.True || literal.GetValueKind() == JsonValueKind.False)
            return new A.BooleanValue(literal.GetValue<bool>());
        if (hint?.Name is "System.Single" or "float")
            return new A.Float32Value(BitConverter.SingleToInt32Bits((float)Real(literal)));
        if (hint?.Name is "System.Double" or "double")
            return new A.Float64Value(BitConverter.DoubleToInt64Bits(Real(literal)));
        if (literal.GetValueKind() == JsonValueKind.String) return Text(literal.GetValue<string>());
        var bits = Integral(literal);
        return hint?.Name switch {
            "System.Char" or "char" => new A.CharValue(unchecked((ushort)bits)),
            "System.SByte" or "sbyte" => new A.Int8Value(unchecked((sbyte)bits)),
            "System.Byte" or "byte" => new A.UInt8Value(unchecked((byte)bits)),
            "System.Int16" or "short" => new A.Int16Value(unchecked((short)bits)),
            "System.UInt16" or "ushort" => new A.UInt16Value(unchecked((ushort)bits)),
            "System.Int32" or "int" => new A.Int32Value(unchecked((int)bits)),
            "System.UInt32" or "uint" => new A.UInt32Value(unchecked((uint)bits)),
            "System.Int64" or "long" => new A.Int64Value(unchecked((long)bits)),
            "System.UInt64" or "ulong" => new A.UInt64Value(bits),
            _ when unchecked((long)bits) is >= int.MinValue and <= int.MaxValue =>
                new A.Int32Value(unchecked((int)bits)),
            _ => new A.Int64Value(unchecked((long)bits)),
        };
    }

    static A Text(string value) => LiteralText.RequiresCodeUnits(value)
        ? new A.Utf16Value(value.Select(c => (ushort)c).ToArray()) : new A.TextValue(value);

    static ulong Integral(JsonNode node)
    {
        var value = (JsonValue)node;
        if (value.TryGetValue<JsonElement>(out var element))
            return element.TryGetInt64(out var signed) ? unchecked((ulong)signed) : element.GetUInt64();
        if (value.TryGetValue<int>(out var i)) return unchecked((ulong)i);
        if (value.TryGetValue<long>(out var l)) return unchecked((ulong)l);
        if (value.TryGetValue<uint>(out var u)) return u;
        if (value.TryGetValue<ulong>(out var ul)) return ul;
        if (value.TryGetValue<byte>(out var b)) return b;
        if (value.TryGetValue<sbyte>(out var sb)) return unchecked((ulong)sb);
        if (value.TryGetValue<short>(out var s)) return unchecked((ulong)s);
        if (value.TryGetValue<ushort>(out var us)) return us;
        throw new InvalidOperationException("Malformed integral annotation constant");
    }

    static double Real(JsonNode node)
    {
        var value = (JsonValue)node;
        if (value.TryGetValue<string>(out var text)) return double.Parse(text, CultureInfo.InvariantCulture);
        if (value.TryGetValue<JsonElement>(out var element)) return element.GetDouble();
        if (value.TryGetValue<double>(out var d)) return d;
        if (value.TryGetValue<float>(out var f)) return f;
        return unchecked((long)Integral(node));
    }

    static string Str(JsonNode node) => node?.GetValue<string>();

    public static void SelfTest()
    {
        JsonObject Applied() => new() {
            ["attr"] = TypeJson.Fqn("Probe.Attribute"),
            ["argTypes"] = new JsonArray(TypeJson.Fqn("System.String")),
            ["args"] = new JsonArray(new JsonObject {
                ["type"] = TypeJson.Fqn("System.String"), ["value"] = LiteralText.Encode("\ud800x\udfff") }),
        };
        JsonArray Attrs() => new(Applied());
        JsonObject Parameter() => new() {
            ["name"] = "p", ["type"] = TypeJson.Fqn("System.Int32"), ["attrs"] = Attrs(),
            ["default"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("System.Int32"), ["value"] = 42 },
        };
        JsonObject Method() => new() {
            ["attrs"] = Attrs(), ["retAttrs"] = Attrs(), ["params"] = new JsonArray(Parameter()),
        };
        var source = new JsonObject {
            ["fileClass"] = "Probe.File", ["attrs"] = Attrs(), ["assemblyAttrs"] = Attrs(),
            ["methods"] = new JsonArray(Method()), ["ctors"] = new JsonArray(Method()),
            ["fields"] = new JsonArray(new JsonObject { ["attrs"] = Attrs() }),
            ["properties"] = new JsonArray(new JsonObject { ["attrs"] = Attrs() }),
            ["types"] = new JsonArray(
                new JsonObject { ["name"] = "Probe.Enum", ["kind"] = "enum",
                    ["entries"] = new JsonArray(new JsonObject { ["attrs"] = Attrs() }) },
                new JsonObject { ["name"] = "Probe.Delegate", ["kind"] = "delegate",
                    ["retAttrs"] = Attrs(), ["params"] = new JsonArray(Parameter()) }),
        };
        var ignored = new JsonObject { ["attr"] = TypeJson.Fqn("Probe.NotEmitted"), ["args"] = new JsonArray() };
        ((JsonArray)source["attrs"]).Add(ignored);
        var declaring = new JsonObject { ["types"] = new JsonArray(
            new JsonObject { ["name"] = "Probe.Attribute", ["kind"] = "class" }) };
        ApplyAll(new JsonNode[] { source, declaring });
        using var doc = JsonDocument.Parse(source.ToJsonString(JsonOptions.Indented));
        int attributes = 0, defaults = 0;
        void Check(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("attr", out _))
                {
                    var metadata = value.Deserialize<AttributeApplication>(AttributeJson.Options);
                    if (metadata.Args.Single() is not A.Utf16Value units
                        || !units.Units.SequenceEqual(new ushort[] { 0xd800, 'x', 0xdfff }))
                        throw new Exception("Metadata projection lost UTF16 or cross-file declaration ownership");
                    attributes++;
                }
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Name == "default")
                    {
                        if (property.Value.Deserialize<A>(AttributeJson.Options) is not A.Int32Value { Value: 42 })
                            throw new Exception("Metadata projection lost the parameter default");
                        defaults++;
                    }
                    else Check(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var child in value.EnumerateArray()) Check(child);
        }
        Check(doc.RootElement);
        if (attributes != 13 || defaults != 3)
            throw new Exception($"Metadata declaration-site coverage changed: {attributes} attrs, {defaults} defaults");
        Console.WriteLine("[CIR metadata projection] self-test OK (declaration sites, enum entries, delegate signature, cross-file ownership)");
    }
}
