using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

using var input = File.OpenRead(args[0]);
using var pe = new PEReader(input);
var metadata = pe.GetMetadataReader();
var carriers = new List<object>();
foreach (var handle in metadata.CustomAttributes)
{
    var attribute = metadata.GetCustomAttribute(handle);
    // The tested producer defines its own compiler metadata carrier classes.
    if (attribute.Constructor.Kind != HandleKind.MethodDefinition) continue;
    var owner = metadata.GetTypeDefinition(metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType());
    if (metadata.GetString(owner.Namespace) != "DotKt.Runtime.CompilerServices") continue;
    var name = metadata.GetString(owner.Name);
    // Inspect every embedded record declared by the compiler, including newly
    // added carriers. Skip native scalar attributes based on their actual ctor.
    var signature = metadata.GetBlobReader(metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).Signature);
    signature.ReadSignatureHeader();
    var arity = signature.ReadCompressedInteger();
    if (signature.ReadSignatureTypeCode() != SignatureTypeCode.Void || arity is not (2 or 3)) continue;
    if (arity == 3 && signature.ReadSignatureTypeCode() != SignatureTypeCode.Int32) continue;
    if (signature.ReadSignatureTypeCode() != SignatureTypeCode.String ||
        signature.ReadSignatureTypeCode() != SignatureTypeCode.SZArray ||
        signature.ReadSignatureTypeCode() != SignatureTypeCode.Byte || signature.RemainingBytes != 0) continue;
    var blob = metadata.GetBlobReader(attribute.Value);
    if (blob.ReadUInt16() != 1) throw new Exception("Invalid attribute prolog");
    int? index = arity == 3 ? blob.ReadInt32() : null;
    var codec = blob.ReadSerializedString();
    var length = blob.ReadInt32();
    var content = blob.ReadBytes(length);
    if (blob.ReadUInt16() != 0 || blob.RemainingBytes != 0) throw new Exception("Invalid carrier attribute envelope");
    carriers.Add(new { name, index, codec, content });
}
Console.WriteLine(JsonSerializer.Serialize(carriers));
