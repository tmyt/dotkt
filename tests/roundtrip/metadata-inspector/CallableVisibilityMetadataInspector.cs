using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using DotKt.Klib.Metadata;

internal static class CallableVisibilityMetadataInspector
{
    const string Carrier = "DotKt.Runtime.CompilerServices.KotlinCallableVisibilityAttribute";
    const string Package = "roundtrip.publishedcallables";

    public static void Verify(string dllPath, string klibPath)
    {
        using var stream = File.OpenRead(dllPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        foreach (var (owner, methods, fields) in new[] {
            ("PublishedCallableVisibilityKt", new[] { "helper", "constrained", "prop_get<computed>",
                "prop_get<extension>", "suspendedHelper" }, new[] { "stored", "constant" }),
            ("Holder", new[] { ".ctor", "memberHelper", "prop_get<member>", "prop_set<member>",
                "prop_get<computedMember>" }, Array.Empty<string>()),
            ("GenericHolder", new[] { "memberHelper", "prop_get<member>" }, Array.Empty<string>()),
            ("StaticHost", new[] { "staticHelper", "prop_get<staticProperty>", "prop_set<staticProperty>" },
                Array.Empty<string>()),
        })
        {
            var type = md.GetTypeDefinition(md.TypeDefinitions.Single(handle =>
                TypeName(md, handle) == Package + "." + owner));
            foreach (var name in methods)
            {
                var handle = type.GetMethods().Single(handle => md.GetString(md.GetMethodDefinition(handle).Name) == name);
                Require((md.GetMethodDefinition(handle).Attributes & MethodAttributes.MemberAccessMask) ==
                    MethodAttributes.Public, owner + "." + name + ": physical inline access lost");
                CheckCarrier(md, handle);
            }
            foreach (var name in fields)
            {
                var handle = type.GetFields().Single(handle => md.GetString(md.GetFieldDefinition(handle).Name) == name);
                Require((md.GetFieldDefinition(handle).Attributes & FieldAttributes.FieldAccessMask) ==
                    FieldAttributes.Public, name + ": physical inline field access lost");
                CheckCarrier(md, handle);
            }
        }

        var fragment = Fragment(klibPath, Package);
        foreach (var name in new[] { "helper", "constrained", "suspendedHelper", "extensionHelper" })
            CheckInternal(fragment.Package.Function.Single(function => Name(fragment, function.Name) == name).Flags, name);
        foreach (var name in new[] { "stored", "constant", "computed", "extension", "extensionProperty" })
            CheckInternal(fragment.Package.Property.Single(property => Name(fragment, property.Name) == name).Flags, name);
        foreach (var name in new[] { "Holder", "GenericHolder", "StaticHost" })
        {
            var type = fragment.Class.Single(type => QualifiedName(fragment, type.FqName) == Package + "." + name);
            foreach (var function in type.Function) CheckInternal(function.Flags, Name(fragment, function.Name));
            foreach (var property in type.Property.Where(property => Name(fragment, property.Name) != "value"))
            {
                CheckInternal(property.Flags, Name(fragment, property.Name));
                if (property.HasGetterFlags) CheckInternal(property.GetterFlags, "getter");
                if (property.HasSetterFlags) CheckInternal(property.SetterFlags, "setter");
            }
            if (name == "Holder") CheckInternal(type.Constructor.Single().Flags, "primary constructor");
        }
        var secondary = fragment.Class.Single(type => QualifiedName(fragment, type.FqName) == Package + ".SecondaryHolder");
        CheckInternal(secondary.Constructor.Single(ctor => ctor.ValueParameter.Count == 1).Flags, "secondary constructor");
        Require((secondary.Constructor.Single(ctor => ctor.ValueParameter.Count == 0).Flags & 14) == 6,
            "public constructor control changed");
        Require((fragment.Package.Function.Single(function => Name(fragment, function.Name) == "publicEntry").Flags & 14) == 6,
            "public function control changed");
    }

    public static void VerifyReference(string dllPath, string klibPath)
    {
        using var stream = File.OpenRead(dllPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var type = md.GetTypeDefinition(md.TypeDefinitions.Single(handle =>
            TypeName(md, handle) == "roundtrip.callablemetadata.PublishedCallableMetadataKt"));
        var helper = type.GetMethods().Single(handle => md.GetString(md.GetMethodDefinition(handle).Name) == "metadataHelper");
        var method = md.GetMethodDefinition(helper);
        CheckCarrier(md, helper);
        Require((method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public,
            "reference helper lost public physical access");
        // The source parameter and its value-storage companion are distinct physical generic slots. Both value
        // positions use slot 1, while Kotlin's declaration and constraint remain on source slot 0.
        Require(md.GetBlobBytes(method.Signature).SequenceEqual(new byte[] { 0x10, 2, 1, 0x1e, 1, 0x1e, 1 }),
            "reference generic identity signature changed: " + Convert.ToHexString(md.GetBlobBytes(method.Signature)));
        Require(method.GetGenericParameters().Count == 2, "reference physical generic frame changed");
        var parameter = method.GetGenericParameters().Select(md.GetGenericParameter).Single(parameter => parameter.Index == 0);
        var bound = md.GetGenericParameterConstraint(parameter.GetConstraints().Single());
        Require(bound.Type.Kind == HandleKind.TypeDefinition &&
            TypeName(md, (TypeDefinitionHandle)bound.Type) == "roundtrip.callablemetadata.Constraint",
            "reference constraint disappeared");
        var body = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
        // The normal emitter may append its unreachable default-return epilogue after this unconditional throw.
        Require(body.Length >= 6 && body[0] == 0x73 && body[5] == 0x7a,
            "reference method did not undergo actual throw-body stripping: " + Convert.ToHexString(body));
        var constructor = MetadataTokens.EntityHandle(BitConverter.ToInt32(body, 1));
        Require(constructor.Kind == HandleKind.MemberReference, "reference throw has no external constructor");
        var member = md.GetMemberReference((MemberReferenceHandle)constructor);
        Require(md.GetString(member.Name) == ".ctor" && member.Parent.Kind == HandleKind.TypeReference,
            "reference throw constructor changed");
        var exception = md.GetTypeReference((TypeReferenceHandle)member.Parent);
        Require(md.GetString(exception.Namespace) == "System" && md.GetString(exception.Name) == "NotImplementedException",
            "reference throw still contains the original implementation");
        var inline = type.GetMethods().Single(handle => md.GetString(md.GetMethodDefinition(handle).Name) == "metadataInline");
        Require(HasCarrier(md, inline, "DotKt.Runtime.CompilerServices.KotlinInlineAttribute"),
            "reference body stripping lost inline metadata");
        var fragment = Fragment(klibPath, "roundtrip.callablemetadata");
        var function = fragment.Package.Function.Single(function => Name(fragment, function.Name) == "metadataHelper");
        CheckInternal(function.Flags, "reference helper");
        Require(function.TypeParameter.Count == 1 && function.TypeParameter[0].UpperBound.Count == 1 &&
            function.ValueParameter.Count == 1, "reference KLIB lost signature or constraints");
        CheckInternal(fragment.Package.Property.Single().Flags, "reference property");
        var constructed = fragment.Class.Single(type => QualifiedName(fragment, type.FqName) ==
            "roundtrip.callablemetadata.Constructed");
        CheckInternal(constructed.Constructor.Single().Flags, "reference constructor");
    }

    static void CheckCarrier(MetadataReader md, EntityHandle handle)
    {
        var attribute = md.GetCustomAttributes(handle).Select(md.GetCustomAttribute).Single(attribute => IsCarrier(md, attribute, Carrier));
        var blob = md.GetBlobReader(attribute.Value);
        Require(blob.ReadUInt16() == 1 && blob.ReadSerializedString() == "bir-json/1", "wrong visibility envelope");
        var length = blob.ReadInt32();
        Require(length >= 0 && length <= blob.RemainingBytes - 2, "invalid visibility payload length");
        using var document = JsonDocument.Parse(blob.ReadBytes(length));
        Require(document.RootElement.GetProperty("visibility").GetString() == "internal", "source visibility changed");
        Require(blob.ReadUInt16() == 0 && blob.RemainingBytes == 0, "unexpected visibility arguments");
    }

    static bool HasCarrier(MetadataReader md, EntityHandle handle, string name) =>
        md.GetCustomAttributes(handle).Select(md.GetCustomAttribute).Any(attribute => IsCarrier(md, attribute, name));

    static bool IsCarrier(MetadataReader md, CustomAttribute attribute, string name) =>
        attribute.Constructor.Kind == HandleKind.MethodDefinition &&
        TypeName(md, md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()) == name;

    static PackageFragment Fragment(string path, string package)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.Where(entry => entry.FullName.EndsWith(".knm", StringComparison.Ordinal))
            .Select(entry => { using var input = entry.Open(); return PackageFragment.Parser.ParseFrom(input); })
            .Single(fragment => QualifiedName(fragment, fragment.Package.PackageFqName) == package);
    }

    static string Name(PackageFragment fragment, int id) => fragment.Strings.String[id];
    static string TypeName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var type = md.GetTypeDefinition(handle);
        return md.GetString(type.Namespace) + "." + md.GetString(type.Name).Split('`')[0];
    }
    static string QualifiedName(PackageFragment fragment, int id)
    {
        var parts = new Stack<string>();
        while (id >= 0)
        {
            var name = fragment.QualifiedNames.QualifiedName[id];
            parts.Push(Name(fragment, name.ShortName));
            id = name.ParentQualifiedName;
        }
        return string.Join('.', parts);
    }
    static void CheckInternal(int flags, string name) => Require((flags & 14) == 0, name + ": KLIB source visibility changed");
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
