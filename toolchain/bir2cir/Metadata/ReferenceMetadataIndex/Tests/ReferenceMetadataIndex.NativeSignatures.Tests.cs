using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotKt.Bir;

sealed partial class ReferenceMetadataIndex
{
    static void NativeSignatureLifetimeSelfTest()
    {
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Reference native signatures: " + message); }
        static bool GenericPointer(TypeNode type) => type is TypeNode.FnPtr {
            CallingConvention: 0, Ret: TypeNode.Tv { Scope: "method", I: 0 },
            Params: [TypeNode.Tv { Scope: "method", I: 0 }] };
        var path = Path.Combine(Path.GetTempPath(), "dotkt-native-signatures-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            File.WriteAllBytes(path, NativeSignatureImage());
            var inputs = new[] { typeof(object).Assembly.Location, path };
            using var first = Build(inputs);
            using var second = Build(inputs);
            var firstType = first.ResolveRefType("NativeSignatureSubject");
            var secondType = second.ResolveRefType("NativeSignatureSubject");
            var method = firstType.GetMethod("Echo");
            var secondMethod = secondType.GetMethod("Echo");
            var scanned = first.Assemblies.Single(assembly => assembly.Path == path)
                .DotKt.MemberBindings.Single(binding => binding.Name == "Echo");
            Check(GenericPointer(scanned.SignatureReturn)
                && GenericPointer(scanned.SignatureParameters.Single()), "scan lost the exact method frame");
            var signature = first.NativeMethodSignature(method);
            Check(GenericPointer(signature.Return) && GenericPointer(signature.Parameters.Single()),
                "method query lost the generic function-pointer frame");
            ((TypeNode.FnPtr)signature.Return).Params[0] = new TypeNode.Fqn("changed");
            ((TypeNode.FnPtr)signature.Parameters[0]).Params[0] = new TypeNode.Fqn("changed");
            var slot = first.DeclarationSignatureSlot(method.ReturnParameter);
            Check(GenericPointer(slot), "a method query exposed the cached return vector");
            ((TypeNode.FnPtr)slot).Params[0] = new TypeNode.Fqn("changed");
            var physical = ClrMemberResolution.DeclaredMethodReturn(first, method);
            Check(GenericPointer(physical), "slot mutation reached the physical CLR consumer");
            ((TypeNode.FnPtr)physical).Params[0] = new TypeNode.Fqn("changed");
            Check(GenericPointer(first.DeclarationSignatureSlot(method.GetParameters().Single()))
                && GenericPointer(first.NativeMethodSignature(method).Return)
                && GenericPointer(ClrMemberResolution.DeclaredMethodReturn(second, secondMethod))
                && GenericPointer(scanned.SignatureReturn)
                && GenericPointer(scanned.SignatureParameters.Single()),
                "query mutation leaked into a cache, scan row, or another compilation");
            var field = firstType.GetField("Pointer");
            var fieldType = (TypeNode.FnPtr)first.NativeFieldSignature(field);
            Check(fieldType.CallingConvention == 1 && fieldType.Ret == new TypeNode.Fqn("System.Int32")
                && fieldType.Params.Single() == new TypeNode.Fqn("System.Int32"),
                "field query lost the unmanaged calling convention");
            fieldType.Params[0] = new TypeNode.Fqn("changed");
            Check(((TypeNode.FnPtr)first.NativeFieldSignature(field)).Params.Single() == new TypeNode.Fqn("System.Int32"),
                "field query exposed its cached vector");
            var ordinary = first.ResolveRefType("System.Object").GetMethod("ToString", Type.EmptyTypes);
            Check(first.NativeMethodSignature(ordinary) == null
                && first.DeclarationSignatureSlot(ordinary.ReturnParameter) == new TypeNode.Fqn("System.String"),
                "ordinary declaration projection changed");
            first.Dispose();
            static bool Disposed(Action query)
            {
                try { query(); return false; }
                catch (ObjectDisposedException) { return true; }
            }
            Check(Disposed(() => first.NativeMethodSignature(method))
                && Disposed(() => first.NativeFieldSignature(field))
                && Disposed(() => first.DeclarationSignatureSlot(method.ReturnParameter)),
                "a disposed index retained an active native signature query");
            Check(GenericPointer(second.NativeMethodSignature(secondMethod).Return),
                "one index's disposal invalidated another compilation");
            Console.WriteLine("[reference native signatures] self-test OK (scan, CLR consumers, owned vectors and independent lifetimes)");
        }
        finally { File.Delete(path); }
    }

    // Real ECMA signatures: generic managed function pointers in an abstract
    // method, and a Cdecl function-pointer field. No executable bodies required.
    static byte[] NativeSignatureImage()
    {
        var metadata = new MetadataBuilder();
        StringHandle Text(string value) => metadata.GetOrAddString(value);
        BlobHandle Blob(params byte[] bytes) => metadata.GetOrAddBlob(bytes);
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
        metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static, Text("Pointer"), Blob(0x06, 0x1b, 1, 1, 8, 8));
        var method = metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Abstract
            | MethodAttributes.NewSlot | MethodAttributes.HideBySig, MethodImplAttributes.IL, Text("Echo"),
            Blob(0x30, 1, 1, 0x1b, 0, 1, 0x1e, 0, 0x1e, 0, 0x1b, 0, 1, 0x1e, 0, 0x1e, 0), -1,
            MetadataTokens.ParameterHandle(1));
        metadata.AddParameter(ParameterAttributes.None, Text("callback"), 1);
        metadata.AddGenericParameter(method, GenericParameterAttributes.None, Text("T"), 0);
        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), new BlobBuilder(), flags: CorFlags.ILOnly).Serialize(image);
        return image.ToArray();
    }
}
