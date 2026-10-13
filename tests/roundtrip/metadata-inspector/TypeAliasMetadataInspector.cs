using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using DotKt.Klib.Metadata;

// Boundary assertions complement execution: mutually compatible producer/consumer mistakes must not hide a
// malformed saved PE, missing declaration information, or an accidentally materialized CLR alias class.
internal static class TypeAliasMetadataInspector
{
    const string Carrier = "DotKt.Runtime.CompilerServices.KotlinTypeAliasAttribute";

    public static void Verify(string dllPath, string klibPath)
    {
        using var stream = File.OpenRead(dllPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var declarations = new List<JsonDocument>();
        try
        {
            foreach (var handle in md.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = md.GetCustomAttribute(handle);
                if (attribute.Constructor.Kind != HandleKind.MethodDefinition) continue;
                var ctor = md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                var owner = md.GetTypeDefinition(ctor.GetDeclaringType());
                if (md.GetString(owner.Namespace) + "." + md.GetString(owner.Name) != Carrier) continue;
                Require(md.GetString(ctor.Name) == ".ctor", "carrier constructor is not a .ctor");
                var blob = md.GetBlobReader(attribute.Value);
                Require(blob.ReadUInt16() == 1 && blob.ReadSerializedString() == "bir-json/1", "wrong carrier envelope");
                var length = blob.ReadInt32();
                Require(length >= 0 && length <= blob.RemainingBytes - 2, "invalid carrier byte[] length");
                declarations.Add(JsonDocument.Parse(blob.ReadBytes(length)));
                Require(blob.ReadUInt16() == 0 && blob.RemainingBytes == 0, "unexpected carrier named arguments");
            }

            using var archive = ZipFile.OpenRead(klibPath);
            var fragments = archive.Entries.Where(entry => entry.FullName.EndsWith(".knm", StringComparison.Ordinal))
                .Select(entry => { using var input = entry.Open(); return PackageFragment.Parser.ParseFrom(input); })
                .ToArray();
            var aliases = declarations.Select(document => document.RootElement).ToArray();
            Require(aliases.Length > 0, "missing assembly typealias declarations");
            foreach (var declaration in aliases)
            {
                var package = declaration.GetProperty("packageName").GetString()!;
                var owners = declaration.GetProperty("ownerPath").EnumerateArray().Select(owner => owner.GetString()!).ToArray();
                var name = declaration.GetProperty("name").GetString()!;
                var fragment = fragments.Single(fragment => QualifiedName(fragment, fragment.Package.PackageFqName) == package);
                var rows = owners.Length == 0 ? fragment.Package.TypeAlias : fragment.Class.Single(klass =>
                    QualifiedName(fragment, klass.FqName) == string.Join('.', owners.Prepend(package))).TypeAlias;
                var alias = rows.Single(row => fragment.Strings.String[row.Name] == name);
                var visibility = declaration.GetProperty("visibility").GetString();
                Require((alias.Flags & 14) == (visibility switch { "public" => 6, "internal" => 0, "private" => 2,
                    _ => throw new InvalidDataException("unexpected fixture visibility") }), "alias visibility changed");
                Require(alias.TypeParameter.Count == declaration.GetProperty("typeParameters").GetArrayLength(),
                    "alias generic parameters were lost");
                var clrName = string.Join('.', owners.Prepend(package).Append(name));
                Require(!md.TypeDefinitions.Select(md.GetTypeDefinition).Any(type =>
                    md.GetString(type.Namespace) + "." + md.GetString(type.Name) == clrName), "alias acquired a CLR TypeDef");
                Require(!fragment.Class.Any(klass => QualifiedName(fragment, klass.FqName) == clrName),
                    "alias acquired a KLIB class declaration");
            }

            var regular = fragments.SingleOrDefault(fragment => QualifiedName(fragment, fragment.Package.PackageFqName) == "roundtrip.aliases");
            if (regular != null)
            {
                TypeAlias Alias(string name) => regular.Package.TypeAlias.Single(alias => regular.Strings.String[alias.Name] == name);
                Require(Alias("NullableText").ExpandedType.Nullable, "nullable expansion was lost");
                Require(Alias("TextChain").UnderlyingType.HasTypeAliasName &&
                    QualifiedName(regular, Alias("TextChain").UnderlyingType.TypeAliasName) == "roundtrip.aliases.Text",
                    "underlying alias chain was flattened");
                Require(Alias("Values").TypeParameter.Single().Id == 0 &&
                    Alias("Values").ExpandedType.Argument.Single().Type.TypeParameter == 0, "alias-owned parameter frame changed");
                Require(Alias("AnyValues").ExpandedType.Argument.Single().Projection ==
                    DotKt.Klib.Metadata.Type.Types.Argument.Types.Projection.Star, "star projection changed");
                Require(Alias("Suspended").ExpandedType.Flags == 1, "suspend function type flag was lost");
                Require(Alias("Callback").ExpandedType.TypeAnnotation.Any(annotation =>
                    QualifiedName(regular, annotation.Id) == "kotlin.ExtensionFunctionType"), "extension receiver metadata was lost");
                var deprecated = Alias("DeprecatedText").Annotation.Single(annotation =>
                    QualifiedName(regular, annotation.Id) == "kotlin.Deprecated");
                var message = deprecated.Argument.Single(argument => regular.Strings.String[argument.NameId] == "message");
                Require(regular.Strings.String[message.Value.StringValue] == "Use Text instead", "alias annotation argument changed");
                Require(!md.TypeDefinitions.Select(md.GetTypeDefinition).Any(type =>
                    md.GetString(type.Namespace) == "roundtrip.aliases" && md.GetString(type.Name) == "TypeAliasesKt"),
                    "alias-only file acquired a CLR facade");
                Require(regular.Class.Single(klass => QualifiedName(regular, klass.FqName) == "roundtrip.aliases.AliasOwner")
                    .TypeAlias.Count == 2, "nested-only file lost aliases");
            }
            var actual = fragments.SingleOrDefault(fragment => QualifiedName(fragment, fragment.Package.PackageFqName) == "roundtrip.actualalias");
            if (actual != null)
            {
                var alias = actual.Package.TypeAlias.Single();
                Require(QualifiedName(actual, alias.ExpandedType.ClassName) == "kotlin.coroutines.cancellation.CancellationException",
                    "actual alias no longer expands to the canonical exception");
            }
        }
        finally { foreach (var declaration in declarations) declaration.Dispose(); }
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
