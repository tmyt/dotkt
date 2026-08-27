using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

if (args.Length != 1) throw new ArgumentException("expected output DLL path");

var md = new MetadataBuilder();
md.AddModule(0, S("FlagsLookalike.dll"), md.GetOrAddGuid(Guid.NewGuid()), default, default);
md.AddAssembly(S("FlagsLookalike"), new Version(1, 0, 0, 0), default, default,
    (AssemblyFlags)0, AssemblyHashAlgorithm.None);
var runtime = md.AddAssemblyReference(S("System.Runtime"), new Version(10, 0, 0, 0), default, default,
    (AssemblyFlags)0, default);
var valueType = md.AddTypeReference(runtime, S("System"), S("ValueType"));
var attributeType = md.AddTypeReference(runtime, S("System"), S("Attribute"));

var firstField = MetadataTokens.FieldDefinitionHandle(1);
var firstMethod = MetadataTokens.MethodDefinitionHandle(1);
md.AddTypeDefinition(TypeAttributes.NotPublic, default, S("<Module>"), default, firstField, firstMethod);
var fakeEnum = md.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Abstract,
    S("System"), S("Enum"), valueType, firstField, firstMethod);
md.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Sealed,
    S("System"), S("FlagsAttribute"), attributeType, firstField, firstMethod);
var flagsCtor = md.AddMethodDefinition(
    MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
    MethodImplAttributes.Runtime, S(".ctor"), Sig(0x20, 0x00, 0x01), 0, MetadataTokens.ParameterHandle(1));
var lookalike = md.AddTypeDefinition(
    TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
    S("Lookalike"), S("FakeFlags"), fakeEnum, firstField, MetadataTokens.MethodDefinitionHandle(2));
md.AddFieldDefinition(
    FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName,
    S("value__"), Sig(0x06, 0x08));
md.AddCustomAttribute(lookalike, flagsCtor, Attr());

var pe = new ManagedPEBuilder(
    new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
    new MetadataRootBuilder(md), new BlobBuilder(), flags: CorFlags.ILOnly);
var output = new BlobBuilder();
pe.Serialize(output);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
File.WriteAllBytes(args[0], output.ToArray());

StringHandle S(string value) => md.GetOrAddString(value);
BlobHandle Sig(params byte[] bytes) => md.GetOrAddBlob(bytes);
BlobHandle Attr()
{
    var blob = new BlobBuilder();
    blob.WriteUInt16(1);
    blob.WriteUInt16(0);
    return md.GetOrAddBlob(blob);
}
