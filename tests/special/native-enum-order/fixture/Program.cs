using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

// Start with csc's normal target-reference enum. Replace one same-width FieldDef name that C# cannot spell;
// no signatures, constants, constructor bodies or target-library identities are synthesized or changed.
var bytes = File.ReadAllBytes(typeof(MetadataOrder.Code).Assembly.Location);
using var pe = new PEReader(new MemoryStream(bytes));
var metadata = pe.GetMetadataReader();
var declaration = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition)
    .Single(type => metadata.GetString(type.Namespace) == "MetadataOrder" && metadata.GetString(type.Name) == "Code");
var field = declaration.GetFields().Select(metadata.GetFieldDefinition)
    .Single(field => metadata.GetString(field.Name) == "Hidden__");
var offset = pe.PEHeaders.MetadataStartOffset + metadata.GetHeapMetadataOffset(HeapIndex.String) + MetadataTokens.GetHeapOffset(field.Name);
Encoding.UTF8.GetBytes("<Hidden>").CopyTo(bytes, offset);
File.WriteAllBytes(args[0], bytes);
