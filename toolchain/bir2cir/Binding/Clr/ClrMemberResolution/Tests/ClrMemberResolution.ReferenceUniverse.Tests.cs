using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotKt.Bir;

static partial class ClrMemberResolution
{
    internal static void ReferenceUniverseIsolationSelfTest()
    {
        var previous = _refs;
        var first = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var second = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        try
        {
            foreach (var references in new[] { first, second, first })
            {
                _refs = references;
                var integer = references.ResolveRefType("System.Int32");
                var nullable = references.ResolveRefType("System.Nullable", 1).MakeGenericType(integer);
                if (!ClrDeclarationMatching.DeclaredConstructorSlotMatches(_refs, new TypeNode.Nullable(new TypeNode.Fqn("System.Int32")), nullable))
                    throw new InvalidOperationException("Native nullable selection retained another reference universe's Type handle");
                if (ClrDeclarationMatching.SystemArrayMlc(_refs) != references.ResolveRefType("System.Array"))
                    throw new InvalidOperationException("Native array selection retained another reference universe's Type handle");
            }
        }
        finally
        {
            first.Dispose(); second.Dispose();
            _refs = previous;
        }
        Console.WriteLine("[reference-universe native selection] self-test OK (interleaved nullable and array declaration identities)");
    }

    internal static void NativeSignatureLifetimeSelfTest()
    {
        var path = Path.Combine(Path.GetTempPath(), "dotkt-native-signatures-" + Guid.NewGuid().ToString("N") + ".dll");
        var previous = _refs;
        try
        {
            File.WriteAllBytes(path, NativeSignatureImage());
            var inputs = new[] { typeof(object).Assembly.Location, path };
            using var first = ReferenceMetadataIndex.Build(inputs);
            using var second = ReferenceMetadataIndex.Build(inputs);
            var method = first.ResolveRefType("NativeSignatureSubject").GetMethod("Pointer");
            var secondMethod = second.ResolveRefType("NativeSignatureSubject").GetMethod("Pointer");
            var signature = first.NativeMethodSignature(method);
            var independent = second.NativeMethodSignature(secondMethod);
            var scanned = first.Assemblies.Single(assembly => assembly.Path == path)
                .DotKt.MemberBindings.Single(binding => binding.Name == "Pointer");
            if (signature == null || independent == null || ReferenceEquals(signature, independent)
                || !ReferenceEquals(signature.Return, scanned.SignatureReturn)
                || !ReferenceEquals(signature.Return, first.DeclarationSignatureSlot(method.ReturnParameter))
                || !ReferenceEquals(signature.Return, DeclaredMethodReturn(first, method)))
                throw new InvalidOperationException("Scan and CLR binding must share their compilation's native decoder");
            first.Dispose();
            if (!ReferenceEquals(independent, second.NativeMethodSignature(secondMethod)))
                throw new InvalidOperationException("Disposing one reference index changed another compilation's decoder");
            Console.WriteLine("[reference native signatures] self-test OK (shared scan/binding decoder and independent compilations)");
        }
        finally { _refs = previous; File.Delete(path); }
    }

    // A real function-pointer MethodDef exercises the raw decoder rather than
    // reflection's ordinary type path. Existing native E2E tests cover the ABI.
    static byte[] NativeSignatureImage()
    {
        var metadata = new MetadataBuilder();
        StringHandle Text(string value) => metadata.GetOrAddString(value);
        metadata.AddModule(0, Text("native-signatures.dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(Text("NativeSignatureOwner"), new Version(1, 0), default, default, default, AssemblyHashAlgorithm.None);
        var core = typeof(object).Assembly.GetName();
        var coreReference = metadata.AddAssemblyReference(Text(core.Name), core.Version, default,
            metadata.GetOrAddBlob(core.GetPublicKeyToken()), default, default);
        var objectType = metadata.AddTypeReference(coreReference, Text("System"), Text("Object"));
        metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, Text("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Abstract, default, Text("NativeSignatureSubject"), objectType,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Abstract
            | MethodAttributes.NewSlot | MethodAttributes.HideBySig, MethodImplAttributes.IL, Text("Pointer"),
            metadata.GetOrAddBlob(new byte[] { 0x20, 0, 0x1b, 0, 0, 1 }), -1, MetadataTokens.ParameterHandle(1));
        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), new BlobBuilder(), flags: CorFlags.ILOnly).Serialize(image);
        return image.ToArray();
    }
}
