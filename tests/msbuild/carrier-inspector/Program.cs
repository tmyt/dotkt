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
    if (name is not ("KotlinInlineAttribute" or "KotlinDefaultAttribute" or "KotlinTypeAliasAttribute")) continue;
    var blob = metadata.GetBlobReader(attribute.Value);
    if (blob.ReadUInt16() != 1) throw new Exception("Invalid attribute prolog");
    int? index = name == "KotlinDefaultAttribute" ? blob.ReadInt32() : null;
    var codec = blob.ReadSerializedString();
    var length = blob.ReadInt32();
    var content = blob.ReadBytes(length);
    if (blob.ReadUInt16() != 0 || blob.RemainingBytes != 0) throw new Exception("Invalid carrier attribute envelope");
    carriers.Add(new { name, index, codec, content });
}
Console.WriteLine(JsonSerializer.Serialize(carriers));
