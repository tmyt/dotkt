using System;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

if (args.Length is < 1 or > 2) throw new ArgumentException("expected output DLL path and optional 'static' carrier kind");
var staticCarrier = args.Length == 2 && args[1] == "static";

var md = new MetadataBuilder();
md.AddModule(0, S("UnsupportedCompanionExtensionTarget.dll"), md.GetOrAddGuid(Guid.NewGuid()), default, default);
md.AddAssembly(S("UnsupportedCompanionExtensionTarget"), new Version(1, 0, 0, 0), default, default,
    (AssemblyFlags)0, AssemblyHashAlgorithm.None);
var runtime = md.AddAssemblyReference(S("System.Runtime"), new Version(10, 0, 0, 0), default, default,
    (AssemblyFlags)0, default);
var objectType = md.AddTypeReference(runtime, S("System"), S("Object"));
var attributeType = md.AddTypeReference(runtime, S("System"), S("Attribute"));
var compilerGeneratedType = md.AddTypeReference(runtime, S("System.Runtime.CompilerServices"), S("CompilerGeneratedAttribute"));
var assemblyMetadataType = md.AddTypeReference(runtime, S("System.Reflection"), S("AssemblyMetadataAttribute"));

var noArgCtor = md.AddMemberReference(compilerGeneratedType, S(".ctor"), Sig(0x20, 0x00, 0x01));
var assemblyMetadataCtor = md.AddMemberReference(assemblyMetadataType, S(".ctor"), Sig(0x20, 0x02, 0x01, 0x0e, 0x0e));

var firstField = MetadataTokens.FieldDefinitionHandle(1);
var firstMethod = MetadataTokens.MethodDefinitionHandle(1);
md.AddTypeDefinition(TypeAttributes.NotPublic, default, S("<Module>"), default, firstField, firstMethod);
var fileAttr = md.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
    S("DotKt.Runtime.CompilerServices"), S("KotlinFileClassAttribute"), attributeType, firstField, firstMethod);
var fileCtor = md.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
    MethodImplAttributes.Runtime, S(".ctor"), Sig(0x20, 0x00, 0x01), 0, MetadataTokens.ParameterHandle(1));
var companionAttr = md.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
    S("DotKt.Runtime.CompilerServices"),
    S(staticCarrier ? "KotlinStaticCarrierAttribute" : "KotlinCompanionExtensionAttribute"), attributeType,
    firstField, MetadataTokens.MethodDefinitionHandle(2));
var companionCtor = md.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
    MethodImplAttributes.Runtime, S(".ctor"), Sig(0x20, 0x02, 0x01, 0x0e, 0x1d, 0x05), 0,
    MetadataTokens.ParameterHandle(1));
var iface = md.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract,
    S("Fixture"), S("I"), default, firstField, MetadataTokens.MethodDefinitionHandle(3));
var implementation = md.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
    S("Fixture"), S("Implementation"), objectType, firstField, MetadataTokens.MethodDefinitionHandle(3));
var interfaceImpl = md.AddInterfaceImplementation(implementation, iface);

md.AddCustomAttribute(MetadataTokens.EntityHandle(TableIndex.Assembly, 1), assemblyMetadataCtor,
    Attr(blob => { SerString(blob, "DotKt.Compiler"); SerString(blob, "metadata-v1"); }));
md.AddCustomAttribute(fileAttr, noArgCtor, Attr(_ => { }));
md.AddCustomAttribute(companionAttr, noArgCtor, Attr(_ => { }));
md.AddCustomAttribute(interfaceImpl, companionCtor, Attr(blob => {
    SerString(blob, "bir-json/1");
    var payload = System.Text.Encoding.UTF8.GetBytes(
        staticCarrier
            ? "{\"owner\":\"Fixture.Implementation\"}"
            : "{\"receiver\":{\"t\":\"fqn\",\"name\":\"Fixture.Implementation\"},\"name\":\"bad\",\"kind\":\"function\"}");
    blob.WriteInt32(payload.Length);
    blob.WriteBytes(payload);
}));

var pe = new ManagedPEBuilder(
    new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
    new MetadataRootBuilder(md), new BlobBuilder(), flags: CorFlags.ILOnly);
var output = new BlobBuilder();
pe.Serialize(output);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
File.WriteAllBytes(args[0], output.ToArray());

StringHandle S(string value) => md.GetOrAddString(value);
BlobHandle Sig(params byte[] bytes) => md.GetOrAddBlob(bytes);
BlobHandle Attr(Action<BlobBuilder> fixedArgs)
{
    var blob = new BlobBuilder();
    blob.WriteUInt16(1);
    fixedArgs(blob);
    blob.WriteUInt16(0);
    return md.GetOrAddBlob(blob);
}
static void SerString(BlobBuilder blob, string value)
{
    var bytes = System.Text.Encoding.UTF8.GetBytes(value);
    if (bytes.Length >= 0x80) throw new ArgumentOutOfRangeException(nameof(value));
    blob.WriteByte((byte)bytes.Length);
    blob.WriteBytes(bytes);
}
