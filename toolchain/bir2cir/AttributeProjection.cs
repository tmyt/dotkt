using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Cir;
using A = DotKt.Cir.AttributeArgument;

// Final physical metadata projection. This adapter disappears when the owning declaration passes
// construct typed CIR directly. It never walks executable children or saved source payloads.
static class AttributeProjection
{
    public static void ApplyAll(IReadOnlyList<JsonNode> roots, IEnumerable<string> typedDeclarations = null)
    {
        var emitted = roots.OfType<JsonObject>()
            .SelectMany(file => file["types"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>().Select(type => Str(type["name"])).Where(name => name != null)
            .Concat(typedDeclarations ?? Enumerable.Empty<string>())
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
        => Literal(BirLiteral.Read(literal), type);

    static A Literal(BirLiteral literal, TypeNode type)
    {
        while (type is TypeNode.Mod modifier) type = modifier.Of;
        var hint = type as TypeNode.Fqn;
        if (literal is BirLiteral.NullLiteral) return new A.NullValue();
        if (literal is BirLiteral.TextLiteral text) return Text(text.Value);
        if (literal is BirLiteral.BooleanLiteral boolean) return new A.BooleanValue(boolean.Value);
        if (literal is Float32Literal f32)
            return hint?.Name is "System.Double" or "double"
                ? new A.Float64Value(BitConverter.DoubleToInt64Bits(BitConverter.Int32BitsToSingle(f32.Bits)))
                : new A.Float32Value(f32.Bits);
        if (literal is Float64Literal f64)
            return hint?.Name is "System.Single" or "float"
                ? new A.Float32Value(BitConverter.SingleToInt32Bits((float)BitConverter.Int64BitsToDouble(f64.Bits)))
                : new A.Float64Value(f64.Bits);
        if (hint?.Name is "System.Single" or "float")
            return new A.Float32Value(BitConverter.SingleToInt32Bits((float)Real(literal)));
        if (hint?.Name is "System.Double" or "double")
            return new A.Float64Value(BitConverter.DoubleToInt64Bits(Real(literal)));
        var bits = literal switch {
            BirLiteral.IntegerLiteral value => unchecked((ulong)value.Value),
            BirLiteral.UnsignedIntegerLiteral value => value.Value,
            BirLiteral.RealLiteral value when double.IsInteger(value.Value) => value.Value >= 0
                ? checked((ulong)value.Value) : unchecked((ulong)checked((long)value.Value)),
            _ => throw new FormatException(),
        };
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

    static double Real(BirLiteral value) => value switch {
        BirLiteral.RealLiteral real => real.Value,
        BirLiteral.IntegerLiteral integer => integer.Value,
        BirLiteral.UnsignedIntegerLiteral integer => integer.Value,
        _ => throw new FormatException(),
    };

    static string Str(JsonNode node) => node?.GetValue<string>();

    public static void SelfTest()
    {
        foreach (var spelling in new[] { "-0", "-0.0", "-0e0", "0", "0.0", "0e0" })
        {
            var numeric = JsonNode.Parse(spelling);
            var negative = spelling.StartsWith('-');
            if (Literal(numeric, new TypeNode.Fqn("System.Single")) is not A.Float32Value { Bits: var single }
                || single != (negative ? int.MinValue : 0)
                || Literal(numeric, new TypeNode.Fqn("System.Double")) is not A.Float64Value { Bits: var dbl }
                || dbl != (negative ? long.MinValue : 0)
                || Literal(numeric, new TypeNode.Fqn("System.Int32")) is not A.Int32Value { Value: 0 })
                throw new Exception("Current native numeric zero lost its value or floating sign");
        }
        foreach (var malformed in new[] {
            """{"encoding":"f32","bits":null}""", """{"encoding":"f64"}""",
            """{"encoding":"f32","bits":2147483648}""", """{"encoding":"unknown","bits":0}"""
        })
        {
            try { BirLiteral.Read(JsonNode.Parse(malformed)); }
            catch (Exception error) when (error is FormatException or InvalidOperationException or OverflowException)
            { continue; }
            throw new Exception("Malformed current floating literal was accepted");
        }
        foreach (var integral in new JsonNode[] { JsonValue.Create(ulong.MaxValue), JsonValue.Create(-1L) })
            if (Literal(integral, new TypeNode.Fqn("System.UInt64")) is not A.UInt64Value { Value: ulong.MaxValue })
                throw new Exception("Semantic integral literal lost its full unsigned bit domain");
        foreach (var bits in new[] { 0, int.MinValue, 0x7f800000, unchecked((int)0xff800000),
                     0x7fa12345, unchecked((int)0xffc12345), 1 })
        {
            var literalSource = new Float32Literal(bits);
            var decoded = BirLiteral.Read(JsonNode.Parse(literalSource.ToDom().ToJsonString()));
            if (decoded is not Float32Literal { Bits: var actual } || actual != bits
                || Literal(decoded, new TypeNode.Fqn("System.Single")) is not A.Float32Value { Bits: var physical }
                || physical != bits
                || Literal(ReferenceMetadataIndex.LiteralValueNode(BitConverter.Int32BitsToSingle(bits)),
                    new TypeNode.Fqn("System.Single")) is not A.Float32Value { Bits: var referenced } || referenced != bits)
                throw new Exception("Typed source/reference Float literal lost its bit pattern");
        }
        foreach (var bits in new[] { 0L, long.MinValue, 0x7ff0000000000000L, unchecked((long)0xfff0000000000000),
                     0x7ff0123456789abcL, unchecked((long)0xfff8123456789abc), 1L })
        {
            var literalSource = new Float64Literal(bits);
            var decoded = BirLiteral.Read(JsonNode.Parse(literalSource.ToDom().ToJsonString()));
            if (decoded is not Float64Literal { Bits: var actual } || actual != bits
                || Literal(decoded, new TypeNode.Fqn("System.Double")) is not A.Float64Value { Bits: var physical }
                || physical != bits
                || Literal(ReferenceMetadataIndex.LiteralValueNode(BitConverter.Int64BitsToDouble(bits)),
                    new TypeNode.Fqn("System.Double")) is not A.Float64Value { Bits: var referenced } || referenced != bits)
                throw new Exception("Typed source/reference Double literal lost its bit pattern");
        }
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
