using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DotKt.Klib.Metadata;

static class DataIdentityMetadataInspector
{
    const string Marker = "DotKt.Runtime.CompilerServices.KotlinDataAttribute";
    const string Package = "roundtrip.dataidentity.";
    static readonly HashSet<string> DataTypes = new(StringComparer.Ordinal) {
        "Point", "Box", "Hidden", "Secret", "FieldData", "Named", "Container.Nested", "Token",
    };

    public static void VerifyMarkers(string dllPath, bool native = false)
    {
        using var stream = File.OpenRead(dllPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        string TypeName(TypeDefinitionHandle handle)
        {
            var type = md.GetTypeDefinition(handle);
            var name = md.GetString(type.Name).Split('`')[0];
            return type.GetDeclaringType().IsNil ? name : TypeName(type.GetDeclaringType()) + "." + name;
        }
        var expected = native ? new[] { "NativeRecord", "NativeOrdinary" } : DataTypes.Concat(new[] { "Ordinary", "Container" }).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in md.TypeDefinitions)
        {
            var name = TypeName(handle);
            if (!expected.Contains(name)) continue;
            seen.Add(name);
            var markers = md.GetTypeDefinition(handle).GetCustomAttributes().Count(attributeHandle =>
            {
                var attribute = md.GetCustomAttribute(attributeHandle);
                if (attribute.Constructor.Kind != HandleKind.MethodDefinition) return false;
                var owner = md.GetTypeDefinition(md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType());
                return md.GetString(owner.Namespace) + "." + md.GetString(owner.Name) == Marker;
            });
            Require(markers == (!native && DataTypes.Contains(name) ? 1 : 0), $"{dllPath}: incorrect data marker on {name}");
        }
        Require(seen.SetEquals(expected), $"{dllPath}: missing expected data-identity fixture types");
    }

    public static void Verify(string dllPath, string klibPath, bool native)
    {
        VerifyMarkers(dllPath, native);
        using var archive = ZipFile.OpenRead(klibPath);
        var classes = archive.Entries.Where(entry => entry.FullName.EndsWith(".knm", StringComparison.Ordinal)).SelectMany(entry =>
        {
            using var input = entry.Open();
            var fragment = PackageFragment.Parser.ParseFrom(input);
            string Name(int index)
            {
                var row = fragment.QualifiedNames.QualifiedName[index];
                var shortName = fragment.Strings.String[row.ShortName];
                return row.ParentQualifiedName >= 0 ? Name(row.ParentQualifiedName) + "." + shortName : shortName;
            }
            return fragment.Class.Select(type => (Name: Name(type.FqName), Type: type)).ToArray();
        }).ToDictionary(row => row.Name, row => row.Type, StringComparer.Ordinal);
        foreach (var name in native ? new[] { "DataIdentityNative.NativeRecord", "DataIdentityNative.NativeOrdinary" }
            : DataTypes.Select(name => Package + name).Concat(new[] { Package + "Ordinary", Package + "Container" }))
        {
            Require(classes.TryGetValue(name, out var type), $"{klibPath}: missing {name}");
            Require(((type!.Flags & (1 << 10)) != 0) == (!native && DataTypes.Contains(name[Package.Length..])),
                $"{klibPath}: incorrect IS_DATA on {name}");
        }
        if (native) return;
        var point = classes[Package + "Point"];
        Require(point.Constructor.Count == 2 && point.Constructor.Count(ctor => (ctor.Flags & (1 << 4)) == 0) == 1,
            "data class lost primary/secondary constructor identity");
        Require(classes[Package + "Box"].TypeParameter.Count == 1, "generic data class lost its type parameter");
    }

    static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
