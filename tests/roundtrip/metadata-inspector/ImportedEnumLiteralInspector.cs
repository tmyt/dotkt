using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json.Nodes;
using DotKt.Klib.Metadata;

// Compare each boundary with the native producer, independently of runtime assertions: a constant that happens
// to equal its ordinal must not hide a dropped external identity or physical representation on another entry.
internal static class ImportedEnumLiteralInspector
{
    public static void Verify(string producer, string klib, string bir, string cir)
    {
        using var stream = File.OpenRead(producer);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var entries = new Dictionary<string, (string Underlying, string Value, int Ordinal)>();
        using var archive = ZipFile.OpenRead(klib);
        var fragments = archive.Entries.Where(entry => entry.FullName.EndsWith(".knm", StringComparison.Ordinal))
            .Select(entry => { using var input = entry.Open(); return PackageFragment.Parser.ParseFrom(input); }).ToArray();
        foreach (var handle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(handle);
            var owner = TypeName(md, handle);
            if (!owner.StartsWith("EnumLiteralInterop.", StringComparison.Ordinal) ||
                type.BaseType.Kind != HandleKind.TypeReference ||
                md.GetString(md.GetTypeReference((TypeReferenceHandle)type.BaseType).Name) != "Enum") continue;
            var fields = type.GetFields().Select(md.GetFieldDefinition)
                .Where(field => (field.Attributes & (FieldAttributes.Static | FieldAttributes.Literal)) ==
                    (FieldAttributes.Static | FieldAttributes.Literal)).ToArray();
            var matches = fragments.SelectMany(fragment => fragment.Class
                .Where(candidate => QualifiedName(fragment, candidate.FqName) == owner.Replace('+', '.'))
                .Select(candidate => (Fragment: fragment, Class: candidate))).ToArray();
            Require(matches.Length == 1, $"KLIB lost enum classifier {owner}");
            var (fragment, declaration) = matches.Single();
            var carrier = declaration.ClassAnnotation.Single(annotation =>
                QualifiedName(fragment, annotation.Id) == "kotlin.clr.ClrExternal");
            var physicalOwner = carrier.Argument.Single(argument => fragment.Strings.String[argument.NameId] == "physicalOwner");
            Require(fragment.Strings.String[physicalOwner.Value.StringValue] == owner,
                $"KLIB lost exact external owner {owner}");
            Require(declaration.EnumEntry.Select(entry => fragment.Strings.String[entry.Name])
                .SequenceEqual(fields.Select(field => md.GetString(field.Name))), $"KLIB changed entry order or aliases on {owner}");
            for (var ordinal = 0; ordinal < fields.Length; ordinal++)
            {
                var field = fields[ordinal];
                var constant = md.GetConstant(field.GetDefaultValue());
                var value = md.GetBlobReader(constant.Value).ReadConstant(constant.TypeCode);
                var underlying = constant.TypeCode switch
                {
                    ConstantTypeCode.SByte => "System.SByte", ConstantTypeCode.Byte => "System.Byte",
                    ConstantTypeCode.Int16 => "System.Int16", ConstantTypeCode.UInt16 => "System.UInt16",
                    ConstantTypeCode.Int32 => "System.Int32", ConstantTypeCode.UInt32 => "System.UInt32",
                    ConstantTypeCode.Int64 => "System.Int64", ConstantTypeCode.UInt64 => "System.UInt64",
                    _ => throw new InvalidDataException($"invalid native enum constant type {constant.TypeCode}"),
                };
                entries.Add(owner + ":" + md.GetString(field.Name),
                    (underlying, Convert.ToString(value, CultureInfo.InvariantCulture)!, ordinal));
            }
        }
        Require(entries.Count > 0, "native enum fixture contains no constants");
        foreach (var (path, physical) in new[] { (bir, false), (cir, true) })
        {
            var seen = new HashSet<string>();
            foreach (var node in Objects(JsonNode.Parse(File.ReadAllText(path))!))
            {
                if (node["k"]?.GetValue<string>() != "enumValue" ||
                    node["type"]?["name"]?.GetValue<string>() is not string owner ||
                    !owner.StartsWith("EnumLiteralInterop.", StringComparison.Ordinal)) continue;
                var key = owner + ":" + node["entry"]!.GetValue<string>();
                Require(entries.TryGetValue(key, out var expected), $"{path} dropped exact enum declaration identity: {key}");
                seen.Add(key);
                Require(node["ordinal"]!.GetValue<int>() == expected.Ordinal, $"{path} changed Kotlin entry ordinal: {key}");
                if (physical)
                    Require(node["underlying"]?.GetValue<string>() == expected.Underlying &&
                        node["physicalValue"]?.GetValue<string>() == expected.Value, $"CIR lost native literal bits: {key}");
                else
                    Require(node["underlying"] is null && node["physicalValue"] is null,
                        $"BIR selected CLR representation: {key}");
            }
            Require(seen.SetEquals(entries.Keys), $"{path} does not exercise every native enum literal and alias");
        }
    }

    static string TypeName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var type = md.GetTypeDefinition(handle);
        var parent = type.GetDeclaringType();
        if (!parent.IsNil) return TypeName(md, parent) + "+" + md.GetString(type.Name);
        var ns = md.GetString(type.Namespace);
        return (ns.Length == 0 ? "" : ns + ".") + md.GetString(type.Name);
    }

    static string QualifiedName(PackageFragment fragment, int id)
    {
        var parts = new Stack<string>();
        while (id >= 0)
        {
            var item = fragment.QualifiedNames.QualifiedName[id];
            parts.Push(fragment.Strings.String[item.ShortName]);
            id = item.ParentQualifiedName;
        }
        return string.Join('.', parts);
    }

    static IEnumerable<JsonObject> Objects(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            yield return obj;
            foreach (var child in obj.Select(pair => pair.Value).OfType<JsonNode>())
                foreach (var nested in Objects(child)) yield return nested;
        }
        else if (node is JsonArray array)
            foreach (var child in array.OfType<JsonNode>())
                foreach (var nested in Objects(child)) yield return nested;
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
