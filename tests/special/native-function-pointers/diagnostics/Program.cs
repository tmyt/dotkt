using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

// Current external ECMA signatures, not old compiler artifacts. Use raw metadata
// because C# cannot spell a generic/vararg function pointer or arbitrary modreq.
Directory.CreateDirectory(args[0]);
var core = AssemblyName.GetAssemblyName(args[1]);
foreach (var (name, bytes) in new (string, byte[])[] {
    ("NativeVararg", [0x06, 0x1b, 0x05, 0x01, 0x08, 0x08]),
    ("NativeGeneric", [0x06, 0x1b, 0x10, 0x01, 0x00, 0x1e, 0x00]),
    // fnptr managed () -> modopt(Opt) modreq(Req) int32.
    // Opt/Req are TypeDef rows 3/4: TypeDefOrRef coded indexes 12/16.
    ("NativeModifiers", [0x06, 0x1b, 0x00, 0x00, 0x20, 0x0c, 0x1f, 0x10, 0x08]),
})
{
    var metadata = new MetadataBuilder();
    metadata.AddModule(0, metadata.GetOrAddString(name + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
    metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 0, 0, 0), default, default, 0, 0);
    var scope = metadata.AddAssemblyReference(metadata.GetOrAddString(core.Name!), core.Version!, default,
        metadata.GetOrAddBlob(core.GetPublicKeyToken()!), 0, default);
    var objectType = metadata.AddTypeReference(scope, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));
    var firstField = MetadataTokens.FieldDefinitionHandle(1);
    var firstMethod = MetadataTokens.MethodDefinitionHandle(1);
    metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default, firstField, firstMethod);
    metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("NativeDiagnostics"),
        metadata.GetOrAddString("Shape"), objectType, firstField, firstMethod);
    metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("NativeDiagnostics"),
        metadata.GetOrAddString("Opt"), objectType, MetadataTokens.FieldDefinitionHandle(2), firstMethod);
    metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("NativeDiagnostics"),
        metadata.GetOrAddString("Req"), objectType, MetadataTokens.FieldDefinitionHandle(2), firstMethod);
    metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static,
        metadata.GetOrAddString("Handle"), metadata.GetOrAddBlob(bytes));
    var builder = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll | Characteristics.ExecutableImage),
        new MetadataRootBuilder(metadata), new BlobBuilder(), flags: CorFlags.ILOnly);
    var image = new BlobBuilder();
    builder.Serialize(image);
    File.WriteAllBytes(Path.Combine(args[0], name + ".dll"), image.ToArray());
}
