using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotKt.Klib.Metadata;

internal static class NumberMetadataInspector
{
    const string Package = "roundtrip.numbers";

    internal static void Verify(string referencePath, string producerPath, string klibPath)
    {
        // The pure Kotlin reference surface is metadata, not a loadable execution universe.
        using var reference = new PEReader(File.OpenRead(referencePath));
        var md = reference.GetMetadataReader();
        var number = md.TypeDefinitions.Select(md.GetTypeDefinition)
            .Single(type => md.GetString(type.Namespace) == "kotlin" && md.GetString(type.Name) == "Number");
        Require(HasAttribute(md, number.GetCustomAttributes(), "kotlin.clr.ClrValueRepresentation"),
            "reference body stripping lost the superclass value-representation contract");
        var members = number.GetMethods().Select(md.GetMethodDefinition).ToArray();
        foreach (var name in new[] { "toByte", "toShort", "toInt", "toLong", "toFloat", "toDouble", "toChar" })
        {
            var method = members.Single(method => md.GetString(method.Name) == name);
            Require(HasAttribute(md, method.GetCustomAttributes(), "kotlin.clr.ClrValueMember"),
                "reference body stripping lost a member adapter: " + name);
            var signature = md.GetBlobReader(method.Signature);
            Require(signature.ReadSignatureHeader().IsInstance && signature.ReadCompressedInteger() == 0
                && signature.ReadSignatureTypeCode() == SignatureTypeCode.TypeHandle,
                "reference member no longer declares its Kotlin numeric return type: " + name);
            Require(TypeName(md, signature.ReadTypeHandle()).StartsWith("kotlin.", StringComparison.Ordinal)
                && signature.RemainingBytes == 0, "reference member signature was stripped or physically substituted");
            if (name == "toChar")
            {
                var body = reference.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
                // The emitter may append an unreachable default return after the throwing body.
                Require(body.Length >= 6 && body[0] == 0x73 && body[5] == 0x7a,
                    "reference body stripping did not retain the ordinary throwing stub: " + Convert.ToHexString(body));
                var constructor = md.GetMemberReference((MemberReferenceHandle)MetadataTokens.EntityHandle(BitConverter.ToInt32(body, 1)));
                Require(md.GetString(constructor.Name) == ".ctor" && TypeName(md, constructor.Parent) == "System.NotImplementedException",
                    "reference stub no longer throws the reference-only exception");
            }
        }

        using var producer = new PEReader(File.OpenRead(producerPath));
        md = producer.GetMetadataReader();
        var value = md.MethodDefinitions.Select(md.GetMethodDefinition)
            .Single(method => md.GetString(method.Name) == "numberValue");
        var physical = md.GetBlobReader(value.Signature);
        Require(!physical.ReadSignatureHeader().IsGeneric && physical.ReadCompressedInteger() == 1
            && physical.ReadSignatureTypeCode() == SignatureTypeCode.Int32
            && physical.ReadSignatureTypeCode() == SignatureTypeCode.Object && physical.RemainingBytes == 0,
            "Number parameter does not use the native boxing ABI");
        foreach (var type in md.TypeDefinitions.Select(md.GetTypeDefinition))
            foreach (var parameter in type.GetGenericParameters().Select(md.GetGenericParameter))
                foreach (var constraint in parameter.GetConstraints().Select(md.GetGenericParameterConstraint))
                    Require(TypeName(md, constraint.Type) != "kotlin.Number", "impossible CLR Number class constraint survived");
        foreach (var method in md.MethodDefinitions.Select(md.GetMethodDefinition))
            foreach (var parameter in method.GetGenericParameters().Select(md.GetGenericParameter))
                foreach (var constraint in parameter.GetConstraints().Select(md.GetGenericParameterConstraint))
                    Require(TypeName(md, constraint.Type) != "kotlin.Number", "impossible method Number constraint survived");

        using var archive = ZipFile.OpenRead(klibPath);
        var fragment = archive.Entries.Where(entry => entry.FullName.EndsWith(".knm", StringComparison.Ordinal))
            .Select(entry => { using var input = entry.Open(); return PackageFragment.Parser.ParseFrom(input); })
            .Single(fragment => Name(fragment, fragment.Package.PackageFqName) == Package);
        Function Function(string name) => fragment.Package.Function.Single(function => fragment.Strings.String[function.Name] == name);
        bool IsNumber(DotKt.Klib.Metadata.Type type) => type.HasClassName && Name(fragment, type.ClassName) == "kotlin.Number";
        Require(IsNumber(Function("numberValue").ValueParameter.Single().Type), "DLL import lost a source Number parameter");
        var bounded = Function("boundedNumber");
        Require(bounded.TypeParameter.Count == 1 && bounded.TypeParameter[0].UpperBound.Count == 1
            && IsNumber(bounded.TypeParameter[0].UpperBound[0]), "DLL import lost a method's source Number bound");
        var bound = fragment.Class.Single(type => Name(fragment, type.FqName) == Package + ".NumberBound");
        Require(bound.TypeParameter.Count == 1 && bound.TypeParameter[0].UpperBound.Count == 1
            && IsNumber(bound.TypeParameter[0].UpperBound[0]), "DLL import lost a class's source Number bound");
        var slots = fragment.Class.Single(type => Name(fragment, type.FqName) == Package + ".NumberSlots");
        foreach (var property in slots.Property)
            Require(IsNumber(property.ReturnType) && property.ReturnType.Nullable ==
                (fragment.Strings.String[property.Name] == "nullable"), "DLL import lost Number property type or nullability");
    }

    static bool HasAttribute(MetadataReader metadata, CustomAttributeHandleCollection attributes, string name) =>
        attributes.Select(metadata.GetCustomAttribute).Any(attribute =>
            TypeName(metadata, attribute.Constructor.Kind == HandleKind.MethodDefinition
                ? metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()
                : metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent) == name);

    static string TypeName(MetadataReader metadata, EntityHandle handle) => handle.Kind switch {
        HandleKind.TypeDefinition => metadata.GetString(metadata.GetTypeDefinition((TypeDefinitionHandle)handle).Namespace)
            + "." + metadata.GetString(metadata.GetTypeDefinition((TypeDefinitionHandle)handle).Name),
        HandleKind.TypeReference => metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)handle).Namespace)
            + "." + metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)handle).Name),
        _ => "",
    };

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
