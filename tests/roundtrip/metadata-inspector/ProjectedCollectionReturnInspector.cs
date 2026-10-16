using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using DotKt.Klib.Metadata;

internal static class ProjectedCollectionReturnInspector
{
    const string Package = "roundtrip.projectedcollectionreturns";

    internal static void Verify(string dllPath, string klibPath)
    {
        using var pe = new PEReader(File.OpenRead(dllPath));
        var metadata = pe.GetMetadataReader();
        MethodDefinition Method(string name) => metadata.MethodDefinitions.Select(metadata.GetMethodDefinition)
            .Single(method => metadata.GetString(method.Name) == name);

        var widened = Method("collectionReturnWiden");
        var signature = metadata.GetBlobReader(widened.Signature);
        Require(!signature.ReadSignatureHeader().IsGeneric && signature.ReadCompressedInteger() == 1
            && signature.ReadSignatureTypeCode() == SignatureTypeCode.Object
            && signature.ReadSignatureTypeCode() == SignatureTypeCode.Object && signature.RemainingBytes == 0,
            "Kotlin collection widening does not retain an opaque identity-preserving value ABI");
        foreach (var sequence in new[] { 0, 1 })
        {
            var parameter = widened.GetParameters().Select(metadata.GetParameter)
                .Single(parameter => parameter.SequenceNumber == sequence);
            using var carried = Payload(metadata, parameter.GetCustomAttributes());
            var source = carried.RootElement;
            Require(source.GetProperty("t").GetString() == "fqn"
                && source.GetProperty("name").GetString() == "kotlin.collections.List"
                && source.GetProperty("args").GetArrayLength() == 1
                && source.GetProperty("args")[0].GetProperty("name").GetString() == "kotlin.Any",
                "opaque collection parameter/return lost its complete source type carrier");
        }

        signature = metadata.GetBlobReader(Method("collectionReturnNative").Signature);
        Require(!signature.ReadSignatureHeader().IsGeneric && signature.ReadCompressedInteger() == 1,
            "native collection declaration acquired a compiler value frame");
        for (var position = 0; position < 2; position++)
        {
            Require(signature.ReadSignatureTypeCode() == SignatureTypeCode.GenericTypeInstance
                && signature.ReadSignatureTypeCode() == SignatureTypeCode.TypeHandle,
                "native collection declaration was erased or wrapped");
            var handle = signature.ReadTypeHandle();
            Require(handle.Kind == HandleKind.TypeReference, "native collection type is not a CLR reference");
            var type = metadata.GetTypeReference((TypeReferenceHandle)handle);
            Require(metadata.GetString(type.Namespace) == "System.Collections.Generic"
                && metadata.GetString(type.Name) == "IReadOnlyList`1"
                && signature.ReadCompressedInteger() == 1
                && signature.ReadSignatureTypeCode() == SignatureTypeCode.Int32,
                "native collection parameter/return lost its exact reified element type");
        }
        Require(signature.RemainingBytes == 0, "unexpected native collection signature suffix");

        using var archive = ZipFile.OpenRead(klibPath);
        var fragments = archive.Entries.Where(entry => entry.FullName.EndsWith(".knm", StringComparison.Ordinal))
            .Select(entry => { using var stream = entry.Open(); return PackageFragment.Parser.ParseFrom(stream); })
            .Where(fragment => Name(fragment, fragment.Package.PackageFqName) == Package).ToArray();
        Require(fragments.Length == 1, "collection return source package was lost or duplicated");
        var fragment = fragments.Single();
        Function Function(string name) => fragment.Package.Function.Single(function => fragment.Strings.String[function.Name] == name);
        void ListOfAny(DotKt.Klib.Metadata.Type type)
        {
            Require(type.HasClassName && Name(fragment, type.ClassName) == "kotlin.collections.List"
                && type.Argument.Count == 1 && type.Argument[0].Type.HasClassName
                && Name(fragment, type.Argument[0].Type.ClassName) == "kotlin.Any",
                "DLL import replaced the source List<Any> with its physical opaque carrier");
        }
        var restored = Function("collectionReturnWiden");
        Require(restored.TypeParameter.Count == 0 && restored.ValueParameter.Count == 1,
            "collection widening changed source arity");
        ListOfAny(restored.ReturnType);
        ListOfAny(restored.ValueParameter[0].Type);
        var identity = Function("collectionReturnIdentity");
        Require(identity.TypeParameter.Count == 1 && identity.ValueParameter.Count == 1
            && identity.ReturnType.HasTypeParameter && identity.ValueParameter[0].Type.HasTypeParameter
            && identity.ReturnType.TypeParameter == identity.TypeParameter[0].Id
            && identity.ValueParameter[0].Type.TypeParameter == identity.TypeParameter[0].Id,
            "generic collection return lost its source-owned parameter frame");
        var box = Function("collectionReturnFromBox").ValueParameter.Single().Type;
        Require(Name(fragment, box.ClassName) == Package + ".CollectionReturnBox" && box.Argument.Count == 1,
            "generic collection storage lost its source application");
        var list = box.Argument[0].Type;
        Require(Name(fragment, list.ClassName) == "kotlin.collections.List" && list.Argument.Count == 1,
            "nested source List projection was erased in the KLIB");
        var comparable = list.Argument[0].Type;
        Require(Name(fragment, comparable.ClassName) == "kotlin.Comparable" && comparable.Argument.Count == 1
            && comparable.Argument[0].Projection == DotKt.Klib.Metadata.Type.Types.Argument.Types.Projection.Star,
            "nested Comparable<*> did not survive DLL import");
    }

    static JsonDocument Payload(MetadataReader metadata, CustomAttributeHandleCollection handles)
    {
        var attribute = handles.Select(metadata.GetCustomAttribute).Single(attribute =>
        {
            if (attribute.Constructor.Kind == HandleKind.MethodDefinition)
            {
                var type = metadata.GetTypeDefinition(metadata.GetMethodDefinition(
                    (MethodDefinitionHandle)attribute.Constructor).GetDeclaringType());
                return metadata.GetString(type.Namespace) == "DotKt.Runtime.CompilerServices"
                    && metadata.GetString(type.Name) == "KotlinTypeAttribute";
            }
            if (attribute.Constructor.Kind == HandleKind.MemberReference)
            {
                var parent = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                if (parent.Kind != HandleKind.TypeReference) return false;
                var type = metadata.GetTypeReference((TypeReferenceHandle)parent);
                return metadata.GetString(type.Namespace) == "DotKt.Runtime.CompilerServices"
                    && metadata.GetString(type.Name) == "KotlinTypeAttribute";
            }
            return false;
        });
        var blob = metadata.GetBlobReader(attribute.Value);
        Require(blob.ReadUInt16() == 1 && blob.ReadSerializedString() == "bir-json/1", "invalid collection source carrier");
        var length = blob.ReadInt32();
        Require(length >= 0 && length <= blob.RemainingBytes - 2, "invalid collection source carrier length");
        var document = JsonDocument.Parse(blob.ReadBytes(length));
        Require(blob.ReadUInt16() == 0 && blob.RemainingBytes == 0, "invalid collection source carrier suffix");
        return document;
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
        if (!condition) throw new InvalidOperationException(message);
    }
}
