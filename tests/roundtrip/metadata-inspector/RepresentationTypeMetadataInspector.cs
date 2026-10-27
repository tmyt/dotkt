using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json.Nodes;
using DotKt.Klib.Metadata;

internal static class RepresentationTypeMetadataInspector
{
    const string Package = "roundtrip.representationtypes";
    const string Association = "DotKt.Runtime.CompilerServices.KotlinTypeAttribute";
    const string Source = "kotlin.CharSequence";

    internal static void Verify(string referencePath, string runtimePath, string producerPath, string klibPath, string birPath)
    {
        var runtimeIdentity = System.Reflection.AssemblyName.GetAssemblyName(runtimePath);
        foreach (var path in new[] { referencePath, runtimePath })
        {
            using var pe = new PEReader(File.OpenRead(path));
            var metadata = pe.GetMetadataReader();
            var referenceSignature = path == referencePath;
            // The test names the physical declaration being checked; production import uses its exact TypeDef,
            // never this implementation spelling, to recover the trusted association.
            var declaration = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition).Single(type =>
                TypeName(metadata, type) == "dotkt$CharSequence");
            var attributes = declaration.GetCustomAttributes().Select(metadata.GetCustomAttribute).Where(attribute =>
                AttributeName(metadata, attribute) == Association).ToArray();
            Require(attributes.Length == 1, path + ": representation declaration lost its unique association");
            var definition = metadata.GetMethodDefinition((MethodDefinitionHandle)attributes[0].Constructor)
                .GetDeclaringType();
            Require(metadata.GetTypeDefinition(definition).GetCustomAttributes().Select(metadata.GetCustomAttribute)
                .Any(attribute => AttributeName(metadata, attribute) ==
                    "System.Runtime.CompilerServices.CompilerGeneratedAttribute"), "association definition is not trusted");
            var blob = metadata.GetBlobReader(attributes[0].Value);
            Require(blob.ReadUInt16() == 1 && blob.ReadSerializedString() == "bir-json/1", "association envelope changed");
            var length = blob.ReadInt32();
            Require(length >= 0 && length <= blob.RemainingBytes - 2, "association payload length changed");
            var source = JsonNode.Parse(blob.ReadBytes(length));
            Require(JsonNode.DeepEquals(source, new JsonObject { ["t"] = "fqn", ["name"] = Source }),
                "association does not preserve the exact Kotlin classifier");
            Require(blob.ReadUInt16() == 0 && blob.RemainingBytes == 0, "unexpected association arguments");
            var methods = declaration.GetMethods().Select(metadata.GetMethodDefinition).ToArray();
            Require(methods.Length == 3, "representation declaration's physical slot count changed");
            foreach (var method in methods)
            {
                var signature = metadata.GetBlobReader(method.Signature);
                Require(signature.ReadSignatureHeader().IsInstance, "representation slot became static");
                var count = signature.ReadCompressedInteger();
                var name = metadata.GetString(method.Name);
                if (name == "get")
                    Require(count == 1 && Primitive(metadata, ref signature, referenceSignature, "kotlin.Char", SignatureTypeCode.Char)
                        && Primitive(metadata, ref signature, referenceSignature, "kotlin.Int", SignatureTypeCode.Int32), "indexer signature changed");
                else if (name == "prop_get<length>")
                    Require(count == 0 && Primitive(metadata, ref signature, referenceSignature, "kotlin.Int", SignatureTypeCode.Int32),
                        "length signature changed");
                else
                    Require(name == "subSequence" && count == 2
                        && signature.ReadSignatureTypeCode() == SignatureTypeCode.TypeHandle
                        && Name(metadata, signature.ReadTypeHandle()) == "dotkt$CharSequence"
                        && Primitive(metadata, ref signature, referenceSignature, "kotlin.Int", SignatureTypeCode.Int32)
                        && Primitive(metadata, ref signature, referenceSignature, "kotlin.Int", SignatureTypeCode.Int32), "subsequence signature changed");
                Require(signature.RemainingBytes == 0, "unexpected physical slot signature tail");
            }
        }

        using (var pe = new PEReader(File.OpenRead(producerPath)))
        {
            var metadata = pe.GetMetadataReader();
            foreach (var name in new[] { "echoRepresentation", "nativeRepresentation" })
            {
                var method = metadata.MethodDefinitions.Select(metadata.GetMethodDefinition)
                    .Single(method => metadata.GetString(method.Name) == name);
                var signature = metadata.GetBlobReader(method.Signature);
                Require(!signature.ReadSignatureHeader().IsGeneric && signature.ReadCompressedInteger() == 1,
                    "ordinary method acquired a generic physical signature");
                var expected = name == "echoRepresentation" ? "dotkt$CharSequence" : "System.Version";
                for (var slot = 0; slot < 2; slot++)
                {
                    Require(signature.ReadSignatureTypeCode() == SignatureTypeCode.TypeHandle,
                        "source metadata changed native parameter/return kind: " + name);
                    var type = signature.ReadTypeHandle();
                    Require(Name(metadata, type) == expected, "native parameter/return identity changed: " + name);
                    if (name == "echoRepresentation") RequireRuntimeTypeReference(metadata, type, runtimeIdentity);
                }
                Require(signature.RemainingBytes == 0, "ordinary physical signature acquired extra slots");
            }
            var authoredPhysical = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition)
                .Single(type => TypeName(metadata, type) == Package + ".RepresentationText");
            var implemented = authoredPhysical.GetInterfaceImplementations().Select(metadata.GetInterfaceImplementation).ToArray();
            Require(implemented.Length == 1, "authored representation acquired an unexpected physical interface");
            RequireRuntimeTypeReference(metadata, implemented[0].Interface, runtimeIdentity);
        }

        using var archive = ZipFile.OpenRead(klibPath);
        var fragment = archive.Entries.Where(entry => entry.FullName.EndsWith(".knm", StringComparison.Ordinal))
            .Select(entry => { using var input = entry.Open(); return PackageFragment.Parser.ParseFrom(input); })
            .Single(fragment => QualifiedName(fragment, fragment.Package.PackageFqName) == Package);
        Function Function(string name) => fragment.Package.Function.Single(function => fragment.Strings.String[function.Name] == name);
        bool IsSource(DotKt.Klib.Metadata.Type type) => type.HasClassName && QualifiedName(fragment, type.ClassName) == Source;
        var authored = fragment.Class.Single(type => QualifiedName(fragment, type.FqName) == Package + ".RepresentationText");
        Require(authored.Supertype.Count(IsSource) == 1 && !authored.Supertype.Any(type => type.HasClassName
            && QualifiedName(fragment, type.ClassName).Contains("dotkt$", StringComparison.Ordinal)),
            "authored Kotlin class retained a physical synthetic supertype");
        foreach (var name in new[] { "representationAt", "echoRepresentation", "nullableRepresentation" })
            Require(IsSource(Function(name).ValueParameter[0].Type), "ordinary source parameter was not restored: " + name);
        Require(IsSource(Function("echoRepresentation").ReturnType)
            && IsSource(Function("nullableRepresentation").ReturnType)
            && Function("nullableRepresentation").ReturnType.Nullable
            && Function("nullableRepresentation").ValueParameter[0].Type.Nullable,
            "source result classifier/nullability was not restored");
        var generic = Function("genericRepresentation");
        Require(generic.TypeParameter.Count == 1 && generic.ReturnType.HasTypeParameter
            && generic.ReturnType.TypeParameter == generic.TypeParameter[0].Id
            && generic.ValueParameter[0].Type.HasTypeParameter
            && generic.ValueParameter[0].Type.TypeParameter == generic.TypeParameter[0].Id
            && IsSource(generic.ValueParameter[1].Type), "source association shifted an ordinary generic frame");
        var bounded = Function("boundedRepresentation");
        Require(bounded.TypeParameter.Count == 1 && bounded.TypeParameter[0].UpperBound.Any(IsSource),
            "method source bound was not restored");
        var box = fragment.Class.Single(type => QualifiedName(fragment, type.FqName) == Package + ".RepresentationBox");
        Require(box.TypeParameter.Count == 1 && box.TypeParameter[0].UpperBound.Any(IsSource),
            "class source bound was not restored");
        var boxed = Function("boxedRepresentation").ValueParameter[0].Type;
        Require(boxed.HasClassName && QualifiedName(fragment, boxed.ClassName) == Package + ".RepresentationBox"
            && boxed.Argument.Count == 1 && boxed.Argument[0].Projection ==
                DotKt.Klib.Metadata.Type.Types.Argument.Types.Projection.Star, "existential source association changed");
        var native = Function("nativeRepresentation");
        Require(new[] { native.ValueParameter[0].Type, native.ReturnType }.All(type => type.HasClassName
            && QualifiedName(fragment, type.ClassName) == "System.Version"), "unannotated nominal classifier changed");

        var nodes = Objects(JsonNode.Parse(File.ReadAllText(birPath))).ToArray();
        Require(!nodes.Any(node => node["t"]?.GetValue<string>() == "fqn"
            && node["name"]?.GetValue<string>() == "dotkt$CharSequence"),
            "consumer BIR leaked the physical representation classifier");
        var calls = nodes.Where(node => node["k"]?.GetValue<string>() == "callStatic"
            && node["ownerType"]?["name"]?.GetValue<string>() == Package + ".RepresentationTypesKt").ToArray();
        foreach (var name in new[] { "representationAt", "echoRepresentation", "nullableRepresentation",
                     "genericRepresentation", "boundedRepresentation", "boxedRepresentation", "nativeRepresentation" })
            Require(calls.Any(call => call["method"]?.GetValue<string>() == name
                && !string.IsNullOrEmpty(call["declarationId"]?.GetValue<string>())),
                "consumer did not bind the ordinary imported declaration: " + name);
        var genericCalls = calls.Where(call => call["method"]?.GetValue<string>() == "genericRepresentation").ToArray();
        Require(genericCalls.Length == 2 && genericCalls.All(call => call["shapeTypes"] is JsonArray { Count: 2 } shape
            && shape[0]?["t"]?.GetValue<string>() == "tv" && shape[0]?["scope"]?.GetValue<string>() == "method"
            && shape[0]?["i"]?.GetValue<int>() == 0 && shape[1]?["name"]?.GetValue<string>() == Source),
            "consumer generic calls lost their source-relative selected declaration frame");
    }

    static IEnumerable<JsonObject> Objects(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            yield return obj;
            foreach (var child in obj.Select(pair => pair.Value))
                foreach (var nested in Objects(child)) yield return nested;
        }
        else if (node is JsonArray array)
            foreach (var child in array)
                foreach (var nested in Objects(child)) yield return nested;
    }

    static string AttributeName(MetadataReader metadata, CustomAttribute attribute) => Name(metadata,
        attribute.Constructor.Kind == HandleKind.MethodDefinition
            ? metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()
            : metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent);

    static void RequireRuntimeTypeReference(MetadataReader metadata, EntityHandle handle,
        System.Reflection.AssemblyName expected)
    {
        Require(handle.Kind == HandleKind.TypeReference, "canonical representation was emitted as a local TypeDef");
        var type = metadata.GetTypeReference((TypeReferenceHandle)handle);
        Require(Name(metadata, handle) == "dotkt$CharSequence" && type.ResolutionScope.Kind == HandleKind.AssemblyReference,
            "canonical representation has no direct runtime AssemblyRef");
        var assembly = metadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope);
        var key = (assembly.Flags & System.Reflection.AssemblyFlags.PublicKey) != 0
            ? expected.GetPublicKey() : expected.GetPublicKeyToken();
        Require(metadata.GetString(assembly.Name) == expected.Name && assembly.Version == expected.Version
            && metadata.GetString(assembly.Culture) == (expected.CultureName ?? "")
            && metadata.GetBlobBytes(assembly.PublicKeyOrToken).SequenceEqual(key ?? Array.Empty<byte>()),
            "canonical representation references a different assembly than the supplied runtime twin");
    }

    static bool Primitive(MetadataReader metadata, ref BlobReader signature, bool reference, string source,
        SignatureTypeCode runtime) => reference
            ? signature.ReadSignatureTypeCode() == SignatureTypeCode.TypeHandle && Name(metadata, signature.ReadTypeHandle()) == source
            : signature.ReadSignatureTypeCode() == runtime;

    static string TypeName(MetadataReader metadata, TypeDefinition type) =>
        Join(metadata.GetString(type.Namespace), metadata.GetString(type.Name));

    static string Name(MetadataReader metadata, EntityHandle handle) => handle.Kind switch {
        HandleKind.TypeDefinition => TypeName(metadata, metadata.GetTypeDefinition((TypeDefinitionHandle)handle)),
        HandleKind.TypeReference => Join(metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)handle).Namespace),
            metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)handle).Name)),
        _ => throw new InvalidDataException("unexpected nominal handle: " + handle.Kind),
    };

    static string Join(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;

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
