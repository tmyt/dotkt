using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using DotKt.Klib.Metadata;

internal static class TypeVisibilityMetadataInspector
{
    const string Carrier = "DotKt.Runtime.CompilerServices.KotlinTypeVisibilityAttribute";
    const string Package = "roundtrip.publishedexistential";

    public static void Verify(string dllPath, string klibPath)
    {
        using var stream = File.OpenRead(dllPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        using var archive = ZipFile.OpenRead(klibPath);
        var fragments = archive.Entries.Where(entry => entry.FullName.EndsWith(".knm", StringComparison.Ordinal))
            .Select(entry => { using var input = entry.Open(); return PackageFragment.Parser.ParseFrom(input); }).ToArray();
        var fragment = fragments.Single(fragment => QualifiedName(fragment, fragment.Package.PackageFqName) == Package);
        foreach (var name in new[] { "PublishedCell", "PublishedContract", "PublishedImplementation",
            "PublishedObject", "PublicPublishedVisibilityControl.Nested", "PublishedEnum" })
        {
            var handle = md.TypeDefinitions.Single(handle => TypeName(md, handle) == Package + "." + name);
            var type = md.GetTypeDefinition(handle);
            Require((type.Attributes & TypeAttributes.VisibilityMask) ==
                (type.GetDeclaringType().IsNil ? TypeAttributes.Public : TypeAttributes.NestedPublic),
                name + ": inline ABI lost CLR public access");
            var attributes = type.GetCustomAttributes().Select(md.GetCustomAttribute).Where(attribute =>
                attribute.Constructor.Kind == HandleKind.MethodDefinition && TypeName(md,
                    md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()) == Carrier).ToArray();
            Require(attributes.Length == 1, name + ": expected one declaration visibility carrier");
            var blob = md.GetBlobReader(attributes[0].Value);
            Require(blob.ReadUInt16() == 1 && blob.ReadSerializedString() == "bir-json/1", "wrong carrier envelope");
            var length = blob.ReadInt32();
            Require(length >= 0 && length <= blob.RemainingBytes - 2, "invalid carrier length");
            using var declaration = JsonDocument.Parse(blob.ReadBytes(length));
            Require(declaration.RootElement.GetProperty("visibility").GetString() == "internal", "source fact changed");
            Require(blob.ReadUInt16() == 0 && blob.RemainingBytes == 0, "unexpected carrier arguments");
            var klass = fragment.Class.Single(klass => QualifiedName(fragment, klass.FqName) == Package + "." + name);
            Require((klass.Flags & 14) == 0, name + ": source visibility became public in KLIB");
        }
        var control = fragment.Class.Single(klass => QualifiedName(fragment, klass.FqName) ==
            Package + ".PublicPublishedVisibilityControl");
        Require((control.Flags & 14) == 6, "ordinary public declaration lost visibility");
    }

    static string TypeName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var type = md.GetTypeDefinition(handle);
        var name = md.GetString(type.Name).Split('`')[0];
        return type.GetDeclaringType().IsNil ? md.GetString(type.Namespace) + "." + name
            : TypeName(md, type.GetDeclaringType()) + "." + name;
    }

    static string QualifiedName(PackageFragment fragment, int id)
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
