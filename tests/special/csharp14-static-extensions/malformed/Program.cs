using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

if (args.Length != 2)
    throw new ArgumentException("usage: StaticExtensionMalformedGenerator <output.dll> <mode>");

var output = Path.GetFullPath(args[0]);
var mode = args[1];
if (mode is not ("valid" or "missing-marker" or "duplicate-implementation" or
    "signature-mismatch" or "callable-declaration" or "callable-marker"))
    throw new ArgumentException($"unknown malformed-fixture mode '{mode}'");

var md = new MetadataBuilder();
var il = new BlobBuilder();
var bodies = new MethodBodyStreamEncoder(il);
var assemblyName = "CSharp14Malformed_" + mode.Replace('-', '_');
md.AddModule(0, S(assemblyName + ".dll"), md.GetOrAddGuid(Guid.NewGuid()), default, default);
md.AddAssembly(S(assemblyName), new Version(1, 0, 0, 0), default, default,
    (AssemblyFlags)0, AssemblyHashAlgorithm.None);

var runtime = md.AddAssemblyReference(
    S("System.Runtime"), new Version(10, 0, 0, 0), default, default, (AssemblyFlags)0, default);
var objectType = md.AddTypeReference(runtime, S("System"), S("Object"));
var extensionType = md.AddTypeReference(
    runtime, S("System.Runtime.CompilerServices"), S("ExtensionAttribute"));
var markerAttributeType = md.AddTypeReference(
    runtime, S("System.Runtime.CompilerServices"), S("ExtensionMarkerAttribute"));
var generatedType = md.AddTypeReference(
    runtime, S("System.Runtime.CompilerServices"), S("CompilerGeneratedAttribute"));
var extensionCtor = md.AddMemberReference(extensionType, S(".ctor"), Sig(0x20, 0x00, 0x01));
var markerAttributeCtor = md.AddMemberReference(markerAttributeType, S(".ctor"), Sig(0x20, 0x01, 0x01, 0x0e));
var generatedCtor = md.AddMemberReference(generatedType, S(".ctor"), Sig(0x20, 0x00, 0x01));

var moduleType = MetadataTokens.TypeDefinitionHandle(1);
var receiverType = MetadataTokens.TypeDefinitionHandle(2);
var containerType = MetadataTokens.TypeDefinitionHandle(3);
var groupType = MetadataTokens.TypeDefinitionHandle(4);
var markerType = MetadataTokens.TypeDefinitionHandle(5);
var firstField = MetadataTokens.FieldDefinitionHandle(1);
var implementationCount = mode == "duplicate-implementation" ? 2 : 1;
var implementationMethod = MetadataTokens.MethodDefinitionHandle(1);
var declarationMethod = MetadataTokens.MethodDefinitionHandle(1 + implementationCount);
var markerMethod = MetadataTokens.MethodDefinitionHandle(2 + implementationCount);
var firstParameter = MetadataTokens.ParameterHandle(1);

md.AddTypeDefinition(TypeAttributes.NotPublic, default, S("<Module>"), default, firstField, implementationMethod);
md.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
    S("Fixture"), S("Receiver"), objectType, firstField, implementationMethod);
md.AddTypeDefinition(
    TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
    S("Fixture"), S("Extensions"), objectType, firstField, implementationMethod);
md.AddTypeDefinition(TypeAttributes.NestedPublic | TypeAttributes.Sealed | TypeAttributes.SpecialName,
    default, S("<G>$fixture"), objectType, firstField, declarationMethod);
md.AddTypeDefinition(
    TypeAttributes.NestedPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.SpecialName,
    default, S("<M>$fixture"), objectType, firstField, markerMethod);
md.AddNestedType(groupType, containerType);
md.AddNestedType(markerType, groupType);

var implementationSignature = mode == "signature-mismatch"
    ? Sig(0x00, 0x01, 0x08, 0x0e) // static int Ping(string)
    : Sig(0x00, 0x00, 0x08);      // static int Ping()
for (var index = 0; index < implementationCount; index++)
    md.AddMethodDefinition(
        MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
        MethodImplAttributes.IL,
        S("Ping"),
        implementationSignature,
        Body(code => { code.LoadConstantI4(42); code.OpCode(ILOpCode.Ret); }),
        firstParameter);

md.AddMethodDefinition(
    MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
    MethodImplAttributes.IL,
    S("Ping"),
    Sig(0x00, 0x00, 0x08),
    mode == "callable-declaration"
        ? Body(code => { code.LoadConstantI4(7); code.OpCode(ILOpCode.Ret); })
        : Body(code => { code.OpCode(ILOpCode.Ldnull); code.OpCode(ILOpCode.Throw); }),
    firstParameter);
md.AddMethodDefinition(
    MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
    MethodImplAttributes.IL,
    S("<Extension>$"),
    // static void <Extension>$(class Fixture.Receiver); TypeDefOrRef coded index for TypeDef row 2 is 8.
    Sig(0x00, 0x01, 0x01, 0x12, 0x08),
    Body(code => { if (mode == "callable-marker") code.OpCode(ILOpCode.Nop); code.OpCode(ILOpCode.Ret); }),
    firstParameter);

md.AddCustomAttribute(containerType, extensionCtor, Attr());
md.AddCustomAttribute(groupType, extensionCtor, Attr());
md.AddCustomAttribute(declarationMethod, markerAttributeCtor,
    Attr(mode == "missing-marker" ? "<M>$missing" : "<M>$fixture"));
md.AddCustomAttribute(markerMethod, generatedCtor, Attr());

var pe = new ManagedPEBuilder(
    new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
    new MetadataRootBuilder(md),
    il,
    flags: CorFlags.ILOnly);
var image = new BlobBuilder();
pe.Serialize(image);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllBytes(output, image.ToArray());

StringHandle S(string value) => md.GetOrAddString(value);
BlobHandle Sig(params byte[] bytes) => md.GetOrAddBlob(bytes);
int Body(Action<InstructionEncoder> emit)
{
    var code = new BlobBuilder();
    var encoder = new InstructionEncoder(code);
    emit(encoder);
    return bodies.AddMethodBody(encoder, maxStack: 8, localVariablesSignature: default,
        attributes: MethodBodyAttributes.None);
}
BlobHandle Attr(string? value = null)
{
    var blob = new BlobBuilder();
    blob.WriteUInt16(1);
    if (value is not null)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        if (bytes.Length >= 0x80) throw new ArgumentOutOfRangeException(nameof(value));
        blob.WriteByte((byte)bytes.Length);
        blob.WriteBytes(bytes);
    }
    blob.WriteUInt16(0);
    return md.GetOrAddBlob(blob);
}
