using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotKt.Bir;

sealed partial class ReferenceMetadataIndex
{
    // Exercise the actual Build -> per-type scan -> carrier reader -> outer scan
    // boundaries. A direct decoder test cannot detect permissive scan catches.
    internal static void CarrierScanSelfTest()
    {
        var semantic = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.String"));
        void Scan(string codec, byte[] bytes, bool marked, bool generated, bool reject)
        {
            var file = Path.Combine(Path.GetTempPath(), "dotkt-carrier-scan-" + Guid.NewGuid().ToString("N") + ".dll");
            try
            {
                File.WriteAllBytes(file, CarrierScanImage(Path.GetFileNameWithoutExtension(file), codec, bytes, marked, generated));
                ReferenceMetadataIndex index;
                try { index = Build(new[] { typeof(object).Assembly.Location, file }); }
                catch (InvalidDataException error) when (reject && error.InnerException != null
                    && error.Message == "malformed trusted TypeNode carrier")
                {
                    if (codec == "unsupported-type-carrier" && error.InnerException is not NotSupportedException)
                        throw new InvalidOperationException("Carrier scan lost the shared unknown-codec failure", error);
                    return;
                }
                if (reject) throw new InvalidOperationException("Authenticated carrier failure became a warning or partial reference index");
                var metadata = index.Assemblies.Single(assembly => assembly.Path == file).DotKt;
                var method = metadata.MemberBindings.Single(method => method.Owner == "CarrierScanSubject" && method.Name == "Read");
                var trusted = marked && generated;
                if (metadata.Diagnostics.Count != 0 || (trusted
                        ? !TypeNodeDataEquality.Equal(method.KotlinReturnType, semantic)
                        : method.KotlinReturnType != null))
                    throw new InvalidOperationException("Complete carrier scan lost its current payload or trusted/foreign distinction");
                // Unlike initial MethodDef indexing, this real physical reader
                // calls KotlinTypeOf even when the assembly is not DotKt-owned.
                if (!index.TryResolveLiteralField(new TypeNode.Fqn("CarrierScanSubject"), "Literal", out var fieldType, out var value)
                    || (trusted ? !TypeNodeDataEquality.Equal(fieldType, semantic)
                        : fieldType != new TypeNode.Fqn("System.String"))
                    || value?.GetValue<string>() != "literal")
                    throw new InvalidOperationException("Physical field lookup accepted a foreign carrier or lost its CLR fallback");
            }
            finally { File.Delete(file); }
        }
        foreach (var codec in new[] { BirCarrier.JsonV1, BirCarrier.MessagePackV1 })
            Scan(codec, TypeNodeCarrier.Encode(codec, semantic), true, true, false);
        Scan("unsupported-type-carrier", TypeNodeCarrier.Encode(TypeNodeCarrier.Codec, semantic), true, true, true);
        Scan(BirCarrier.MessagePackV1, new byte[] { 0xc1 }, true, true, true);
        Scan(BirCarrier.JsonV1, new byte[] { (byte)'{' }, true, true, true);
        // These exact-name lookalikes do not authenticate: an undecodable body
        // is irrelevant and must not acquire authority through error handling.
        Scan("unsupported-type-carrier", new byte[] { 0xc1 }, false, true, false);
        Scan("unsupported-type-carrier", new byte[] { 0xc1 }, true, false, false);
        Console.WriteLine("[TypeNode carrier reference scan] self-test OK (declared codecs, complete failure boundary, cached provenance and foreign literal fields)");
    }

    static byte[] CarrierScanImage(string name, string codec, byte[] payload, bool marked, bool generated)
    {
        var metadata = new MetadataBuilder();
        var il = new BlobBuilder();
        var bodies = new MethodBodyStreamEncoder(il);
        StringHandle Text(string value) => metadata.GetOrAddString(value);
        BlobHandle Signature(params byte[] bytes) => metadata.GetOrAddBlob(bytes);
        int Body(Action<InstructionEncoder> emit)
        {
            var encoder = new InstructionEncoder(new BlobBuilder());
            emit(encoder);
            return bodies.AddMethodBody(encoder, maxStack: 8, localVariablesSignature: default,
                attributes: MethodBodyAttributes.None);
        }
        metadata.AddModule(0, Text(name + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        var assembly = metadata.AddAssembly(Text(name), new Version(1, 0), default, default, default, AssemblyHashAlgorithm.None);
        var coreName = typeof(object).Assembly.GetName();
        var core = metadata.AddAssemblyReference(Text(coreName.Name), coreName.Version, default,
            metadata.GetOrAddBlob(coreName.GetPublicKeyToken()), default, default);
        TypeReferenceHandle CoreType(string ns, string type) => metadata.AddTypeReference(core, Text(ns), Text(type));
        var objectType = CoreType("System", "Object");
        var attributeType = CoreType("System", "Attribute");
        var generatedType = CoreType("System.Runtime.CompilerServices", "CompilerGeneratedAttribute");
        var markerType = CoreType("System.Reflection", "AssemblyMetadataAttribute");
        var attributeCtor = metadata.AddMemberReference(attributeType, Text(".ctor"), Signature(0x20, 0, 1));
        var generatedCtor = metadata.AddMemberReference(generatedType, Text(".ctor"), Signature(0x20, 0, 1));
        var markerCtor = metadata.AddMemberReference(markerType, Text(".ctor"), Signature(0x20, 2, 1, 0x0e, 0x0e));
        BlobHandle AttributeBody(Action<BlobBuilder> write)
        {
            var blob = new BlobBuilder(); blob.WriteUInt16(1); write(blob); blob.WriteUInt16(0);
            return metadata.GetOrAddBlob(blob);
        }
        var generatedBody = AttributeBody(_ => { });
        if (marked) metadata.AddCustomAttribute(assembly, markerCtor, AttributeBody(blob => {
            blob.WriteSerializedString(DotKtAssemblyMarkerKey); blob.WriteSerializedString(DotKtAssemblyMarkerValue);
        }));
        metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, Text("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var fileClass = metadata.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Sealed,
            Text("DotKt.Runtime.CompilerServices"), Text("KotlinFileClassAttribute"), attributeType,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        const MethodAttributes ctorFlags = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
        int AttributeConstructor() => Body(encoder => {
            encoder.LoadArgument(0); encoder.Call(attributeCtor); encoder.OpCode(ILOpCode.Ret);
        });
        metadata.AddMethodDefinition(ctorFlags, MethodImplAttributes.IL, Text(".ctor"), Signature(0x20, 0, 1),
            AttributeConstructor(), MetadataTokens.ParameterHandle(1));
        metadata.AddCustomAttribute(fileClass, generatedCtor, generatedBody);
        var carrier = metadata.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Sealed,
            Text("DotKt.Runtime.CompilerServices"), Text("KotlinTypeAttribute"), attributeType,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(2));
        var carrierCtor = metadata.AddMethodDefinition(ctorFlags, MethodImplAttributes.IL, Text(".ctor"),
            Signature(0x20, 2, 1, 0x0e, 0x1d, 0x05), AttributeConstructor(), MetadataTokens.ParameterHandle(1));
        metadata.AddParameter(ParameterAttributes.None, Text("codec"), 1);
        metadata.AddParameter(ParameterAttributes.None, Text("content"), 2);
        if (generated) metadata.AddCustomAttribute(carrier, generatedCtor, generatedBody);
        metadata.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
            default, Text("CarrierScanSubject"), objectType,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(3));
        var field = metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault,
            Text("Literal"), Signature(0x06, 0x0e));
        metadata.AddConstant(field, "literal");
        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            Text("Read"), Signature(0, 0, 0x1c), Body(encoder => {
                encoder.OpCode(ILOpCode.Ldnull); encoder.OpCode(ILOpCode.Ret);
            }), MetadataTokens.ParameterHandle(3));
        var result = metadata.AddParameter(ParameterAttributes.None, default, 0);
        var carried = AttributeBody(blob => {
            blob.WriteSerializedString(codec); blob.WriteInt32(payload.Length); blob.WriteBytes(payload);
        });
        metadata.AddCustomAttribute(result, carrierCtor, carried);
        metadata.AddCustomAttribute(field, carrierCtor, carried);
        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), il, flags: CorFlags.ILOnly).Serialize(image);
        return image.ToArray();
    }
}
