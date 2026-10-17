using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Xml.Linq;

try
{
    uint resourceId = 1;
    if (args[0] == "--library") { resourceId = 2; args = args.Skip(1).ToArray(); }
    if (args[0] == "--gui")
    {
        foreach (var file in args.Skip(1))
        {
            using var pe = new PEReader(File.OpenRead(file));
            if (pe.PEHeaders.PEHeader.Subsystem != Subsystem.WindowsGui)
                throw new InvalidDataException("Expected Windows GUI apphost: " + file);
        }
    }
    else if (args[0] == "--kotlin")
    {
        using var pe = new PEReader(File.OpenRead(args[1]));
        var metadata = pe.GetMetadataReader();
        var names = metadata.TypeDefinitions.Select(h => metadata.GetString(metadata.GetTypeDefinition(h).Name)).ToArray();
        if (!names.Contains("ProgramKt") || names.Contains("_DotKtPlaceholder"))
            throw new InvalidDataException("Final managed output is not the Kotlin program: " + args[1]);
    }
    else if (args[0] == "--bytes")
    {
        var expected = File.ReadAllBytes(args[1]);
        foreach (var file in args.Skip(2))
            if (!expected.AsSpan().SequenceEqual(ManifestBytes(file, resourceId)))
                throw new InvalidDataException("Native manifest bytes differ from " + args[1] + ": " + file);
    }
    else
    {
        var expected = args[0] == "--absent" ? null : XDocument.Load(args[0]).Root;
        foreach (var file in args.Skip(1))
        {
            var payload = ManifestBytes(file, resourceId);
            var actual = payload == null ? null : XDocument.Parse(Encoding.UTF8.GetString(payload).Trim('\uFEFF', '\0')).Root;
            if (!XNode.DeepEquals(expected, actual))
                throw new InvalidDataException("Native manifest differs from " + args[0] + ": " + file);
        }
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("manifest-inspector: " + error.Message);
    return 1;
}

static byte[] ManifestBytes(string file, uint resourceId)
{
    using var pe = new PEReader(File.OpenRead(file));
    var resource = pe.PEHeaders.PEHeader.ResourceTableDirectory;
    if (resource.Size == 0) return null;
    var bytes = pe.GetSectionData(resource.RelativeVirtualAddress).GetContent(0, resource.Size).ToArray();
    uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
    ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at, 2));
    uint? Find(int directory, uint id)
    {
        var count = U16(directory + 12) + U16(directory + 14);
        for (var i = 0; i < count; i++)
        {
            var entry = checked(directory + 16 + i * 8);
            if (U32(entry) == id) return U32(entry + 4);
        }
        return null;
    }
    int Directory(uint entry) => (entry & 0x80000000) != 0
        ? checked((int)(entry & 0x7fffffff)) : throw new InvalidDataException("Expected resource directory: " + file);
    var type = Find(0, 24); // RT_MANIFEST
    if (type == null) return null;
    var name = Find(Directory(type.Value), resourceId);
    if (name == null) return null;
    var language = Find(Directory(name.Value), 0); // neutral, matching the same-target C# control
    if (language == null || (language.Value & 0x80000000) != 0)
        throw new InvalidDataException("Missing neutral manifest data: " + file);
    var data = checked((int)language.Value);
    return pe.GetSectionData(checked((int)U32(data))).GetContent(0, checked((int)U32(data + 4))).ToArray();
}
