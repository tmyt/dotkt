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
        VerifyOverrides(md, klibPath);
    }

    static void VerifyOverrides(MetadataReader md, string klibPath)
    {
        const string package = "roundtrip.publishedoverrides";
        var fragment = Fragment(klibPath, package);
        foreach (var owner in new[] { "Base", "Derived", "Leaf", "AbstractBase", "Concrete" })
        {
            var type = md.GetTypeDefinition(md.TypeDefinitions.Single(handle => TypeName(md, handle) == package + "." + owner));
            var methods = type.GetMethods().Select(handle => (Handle: handle, Method: md.GetMethodDefinition(handle)))
                .Where(row => md.GetString(row.Method.Name) is "hook" or "select" or "suspended" or "operation"
                    or "prop_get<readOnly>" or "prop_set<readOnly>" or "prop_get<mutable>" or "prop_set<mutable>")
                .ToArray();
            var expectedCount = owner switch { "Base" => 8, "Derived" => 9, "Leaf" => 7, _ => 1 };
            Require(methods.Length == expectedCount, owner + ": expected override/accessor declarations missing");
            foreach (var (handle, method) in methods)
            {
                var name = md.GetString(method.Name);
                // The String overload is not published. A setter introduced over a val has no public base setter.
                var unpromoted = name == "prop_set<readOnly>" || name == "hook" &&
                    md.GetBlobBytes(method.Signature).SequenceEqual(new byte[] { 0x20, 1, 8, 14 });
                Require((method.Attributes & MethodAttributes.MemberAccessMask) ==
                    (unpromoted ? MethodAttributes.Assembly : MethodAttributes.Public),
                    owner + "." + name + ": selected CLR slot accessibility changed");
                CheckCarrier(md, handle);
            }

            var source = fragment.Class.Single(type => QualifiedName(fragment, type.FqName) == package + "." + owner);
            foreach (var function in source.Function.Where(function => Name(fragment, function.Name) is
                         "hook" or "select" or "suspended" or "operation"))
                CheckInternal(function.Flags, owner + "." + Name(fragment, function.Name));
            foreach (var property in source.Property.Where(property => Name(fragment, property.Name) is "readOnly" or "mutable"))
            {
                CheckInternal(property.Flags, owner + "." + Name(fragment, property.Name));
                if (property.HasGetterFlags) CheckInternal(property.GetterFlags, owner + " getter");
                if (property.HasSetterFlags) CheckInternal(property.SetterFlags, owner + " setter");
            }
        }

        var publicType = md.GetTypeDefinition(md.TypeDefinitions.Single(handle =>
            TypeName(md, handle) == package + ".PublicDerived"));
        var publicHook = publicType.GetMethods().Single(handle => md.GetString(md.GetMethodDefinition(handle).Name) == "hook");
        Require((md.GetMethodDefinition(publicHook).Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public
            && !HasCarrier(md, publicHook, Carrier), "explicit public override was made source-internal");
        var publicSource = fragment.Class.Single(type => QualifiedName(fragment, type.FqName) == package + ".PublicDerived");
        Require((publicSource.Function.Single(function => Name(fragment, function.Name) == "hook" &&
            function.ValueParameter.Count == 0).Flags & 14) == 6, "public override KLIB visibility changed");
    }

    public static void VerifyReferencedOverride(string dllPath, string birPath, string cirPath)
    {
        using var bir = JsonDocument.Parse(File.ReadAllText(birPath));
        var source = bir.RootElement.GetProperty("types").EnumerateArray().Single();
        var declaration = source.GetProperty("methods").EnumerateArray().Single();
        Require(declaration.GetProperty("vis").GetString() == "internal", "friend override lost source visibility");
        var selectedId = declaration.GetProperty("overrides")[0].GetProperty("declarationId").GetString();
        Require(!string.IsNullOrEmpty(selectedId) &&
            !bir.RootElement.GetProperty("methods").EnumerateArray().Concat(source.GetProperty("methods").EnumerateArray())
                .Any(method => method.GetProperty("declarationId").GetString() == selectedId),
            "friend fixture must select an external declaration, not a local public slot");
        using var cir = JsonDocument.Parse(File.ReadAllText(cirPath));
        var physical = cir.RootElement.GetProperty("types").EnumerateArray().Single(type =>
            type.GetProperty("name").GetString() == "roundtrip.referenceoverride.FriendDerived");
        Require(physical.GetProperty("methods").EnumerateArray().Single().GetProperty("vis").GetString() == "public",
            "exact referenced slot did not widen the override");
        using var stream = File.OpenRead(dllPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var type = md.GetTypeDefinition(md.TypeDefinitions.Single(handle =>
            TypeName(md, handle) == "roundtrip.referenceoverride.FriendDerived"));
        var hook = type.GetMethods().Single(handle => md.GetString(md.GetMethodDefinition(handle).Name) == "hook");
        Require((md.GetMethodDefinition(hook).Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public,
            "referenced override lost public physical access");
        CheckCarrier(md, hook);
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

    public static void VerifyInherited(string klibPath)
    {
        var fragment = Fragment(klibPath, "InheritedCallableAdapter");
        var type = fragment.Class.Single(type => QualifiedName(fragment, type.FqName) == "InheritedCallableAdapter.Derived");
        foreach (var name in new[] { "secret", "secretField", "publicValue", "publicField" })
        {
            var properties = type.Property.Where(property => Name(fragment, property.Name) == name).ToArray();
            Require(properties.Length == 2, name + ": inherited declaration and hidden interface slot must remain distinct");
            var declaration = properties.Single(property => !property.PropertyAnnotation.Any(annotation =>
                QualifiedName(fragment, annotation.Id) == "kotlin.Deprecated"));
            var expectedVisibility = name.StartsWith("secret", StringComparison.Ordinal) ? 0 : 6;
            Require((declaration.Flags & 14) == expectedVisibility, name + ": declaring assembly visibility lost");
            if (declaration.HasGetterFlags)
                Require((declaration.GetterFlags & 14) == expectedVisibility, name + ": getter visibility lost");
            if (name.EndsWith("Field", StringComparison.Ordinal))
                Require((declaration.Flags & (1 << 8)) != 0 && declaration.SetterValueParameter is not null &&
                    declaration.PropertyAnnotation.Any(annotation => QualifiedName(fragment, annotation.Id) == "kotlin.clr.ClrField"),
                    name + ": source field mutability or representation lost");
            var slot = properties.Single(property => !ReferenceEquals(property, declaration));
            Require((slot.Flags & 14) == 6, name + ": explicit interface slot visibility changed");
        }
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
