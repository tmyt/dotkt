using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Klib.Metadata;

internal static class CharSequenceSupertypeInspector
{
    const string Package = "roundtrip.charsequencesupertypes";
    const string Carrier = "KotlinSupertypesAttribute";

    public static void Verify(string dllPath, string klibPath)
    {
        using var stream = File.OpenRead(dllPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        using var archive = ZipFile.OpenRead(klibPath);
        var fragments = archive.Entries.Where(entry => entry.FullName.EndsWith(".knm", StringComparison.Ordinal))
            .Select(entry => { using var input = entry.Open(); return PackageFragment.Parser.ParseFrom(input); }).ToArray();
        var fragment = fragments.Single(fragment => Name(fragment, fragment.Package.PackageFqName) == Package);
        var character = Fqn("kotlin.CharSequence");
        var nullableCharacter = new JsonObject { ["t"] = "nullable", ["of"] = character.DeepClone() };
        foreach (var (typeName, edge, isBase) in new[] {
            ("Text", Fqn(Package + ".Slot", character.DeepClone()), false),
            ("NullableText", Fqn(Package + ".Slot", nullableCharacter), false),
            ("NestedText", Fqn(Package + ".Slot", Fqn(Package + ".Slot", character.DeepClone())), false),
            ("DerivedText", Fqn(Package + ".Base", character.DeepClone()), true),
            ("MixedText", Fqn(Package + ".PairSlot", character.DeepClone(),
                new JsonObject { ["t"] = "nullable", ["of"] = Fqn("kotlin.Int") }), false) })
        {
            var definition = md.TypeDefinitions.Select(md.GetTypeDefinition).Single(type =>
                md.GetString(type.Namespace) == Package && md.GetString(type.Name) == typeName);
            var carriers = definition.GetCustomAttributes().Select(md.GetCustomAttribute).Where(attribute =>
                attribute.Constructor.Kind == HandleKind.MethodDefinition &&
                md.GetString(md.GetTypeDefinition(md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor)
                    .GetDeclaringType()).Name) == Carrier).ToArray();
            Require(carriers.Length == 1, typeName + ": expected one merged supertype carrier");
            var blob = md.GetBlobReader(carriers[0].Value);
            Require(blob.ReadUInt16() == 1 && blob.ReadSerializedString() == "bir-json/1", "wrong carrier envelope");
            var length = blob.ReadInt32();
            Require(length >= 0 && length <= blob.RemainingBytes - 2, "invalid carrier length");
            using var document = JsonDocument.Parse(blob.ReadBytes(length));
            Require(blob.ReadUInt16() == 0 && blob.RemainingBytes == 0, "unexpected carrier arguments");
            var source = isBase ? document.RootElement.GetProperty("base")
                : document.RootElement.GetProperty("interfaces").EnumerateArray().Single();
            Require(JsonNode.DeepEquals(JsonNode.Parse(source.GetRawText()), edge), typeName + ": source edge changed");
            var klass = fragment.Class.Single(klass => Name(fragment, klass.FqName) == Package + "." + typeName);
            Require((klass.Flags & 14) == 6, typeName + ": public visibility changed");
            var projected = klass.Supertype.Single(type => type.HasClassName &&
                Name(fragment, type.ClassName) == edge["name"]!.GetValue<string>());
            VerifyType(fragment, projected, edge);
        }
        var control = fragment.Class.Single(klass => Name(fragment, klass.FqName) == Package + ".UnchangedText");
        // These untouched edges keep the normal physical-signature projection; only corrected source edges
        // above are reconstructed from an exact Kotlin TypeNode and must have no platform bounds.
        VerifyType(fragment, control.Supertype.Single(type => type.HasClassName &&
            Name(fragment, type.ClassName) == Package + ".Slot"), Fqn(Package + ".Slot", Fqn("kotlin.String")), false);
        var unchanged = md.TypeDefinitions.Select(md.GetTypeDefinition).Single(type =>
            md.GetString(type.Namespace) == Package && md.GetString(type.Name) == "UnchangedText");
        Require(!unchanged.GetCustomAttributes().Select(md.GetCustomAttribute).Any(attribute =>
            attribute.Constructor.Kind == HandleKind.MethodDefinition &&
            md.GetString(md.GetTypeDefinition(md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor)
                .GetDeclaringType()).Name) == Carrier), "unchanged edge acquired a correction carrier");
        var mixed = fragment.Class.Single(klass => Name(fragment, klass.FqName) == Package + ".MixedText");
        VerifyType(fragment, mixed.Supertype.Single(type => type.HasClassName &&
            Name(fragment, type.ClassName) == Package + ".Marker"), Fqn(Package + ".Marker", Fqn("kotlin.String")), false);
    }

    static void VerifyType(PackageFragment fragment, DotKt.Klib.Metadata.Type actual, JsonNode expected,
        bool sourceCorrection = true)
    {
        var nullable = expected["t"]!.GetValue<string>() == "nullable";
        if (nullable) expected = expected["of"]!;
        Require(actual.Nullable == nullable, "KLIB nullability changed");
        if (sourceCorrection)
            Require(!actual.HasFlexibleTypeCapabilitiesId && actual.FlexibleUpperBound == null,
                "Kotlin source correction acquired a platform bound");
        Require(actual.HasClassName && Name(fragment, actual.ClassName) == expected["name"]!.GetValue<string>(),
            "KLIB Kotlin classifier identity changed");
        var arguments = expected["args"] as JsonArray;
        Require(actual.Argument.Count == (arguments?.Count ?? 0), "KLIB argument count changed");
        for (var index = 0; index < actual.Argument.Count; index++)
        {
            Require(actual.Argument[index].Projection == DotKt.Klib.Metadata.Type.Types.Argument.Types.Projection.Inv,
                "KLIB projection changed");
            VerifyType(fragment, actual.Argument[index].Type, arguments![index]!, sourceCorrection);
        }
    }

    static JsonObject Fqn(string name, params JsonNode[] arguments)
    {
        var type = new JsonObject { ["t"] = "fqn", ["name"] = name };
        if (arguments.Length > 0) type["args"] = new JsonArray(arguments);
        return type;
    }

    static string Name(PackageFragment fragment, int id)
    {
        var parts = new Stack<string>();
        while (id >= 0)
        {
            var name = fragment.QualifiedNames.QualifiedName[id];
            parts.Push(fragment.Strings.String[name.ShortName]);
            id = name.ParentQualifiedName;
        }
        return string.Join('.', parts);
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
