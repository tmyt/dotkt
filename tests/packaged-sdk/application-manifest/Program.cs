using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Xml.Linq;

try
{
    uint resourceId = 1;
    if (args[0] == "--library") { resourceId = 2; args = args.Skip(1).ToArray(); }
    if (args[0] == "--write-icon")
    {
        using var stream = File.Create(args[1]);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1); // ICONDIR
        writer.Write(new byte[] { 1, 1, 0, 0 }); // 1x1, true color
        writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(48); writer.Write(22);
        writer.Write(40); writer.Write(1); writer.Write(2); // BITMAPINFOHEADER, doubled height includes mask
        writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(0); writer.Write(8);
        writer.Write(new byte[16]);
        writer.Write(uint.Parse(args[2])); writer.Write(0); // BGRA pixel + padded AND mask
    }
    else if (args[0] == "--write-res")
    {
        // rc.exe-style .res with one named resource and a non-neutral language. Csc owns conversion to PE.
        using var stream = File.Create(args[1]);
        using var writer = new BinaryWriter(stream, Encoding.Unicode);
        writer.Write(new byte[] { 0, 0, 0, 0, 32, 0, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0 });
        writer.Write(new byte[16]); // mandatory null resource header
        var names = Encoding.Unicode.GetBytes("NativeTest\0Payload\0");
        var nameSize = (names.Length + 3) & ~3;
        var data = Encoding.UTF8.GetBytes(args[2]);
        writer.Write(data.Length); writer.Write(8 + nameSize + 16);
        writer.Write(names); writer.Write(new byte[nameSize - names.Length]);
        writer.Write(0); writer.Write((ushort)0x30); writer.Write((ushort)1033);
        writer.Write(0); writer.Write(0); writer.Write(data);
        writer.Write(new byte[((data.Length + 3) & ~3) - data.Length]);
    }
    else if (args[0] == "--resources")
    {
        var expected = Resources(args[1]);
        foreach (var file in args.Skip(2))
        {
            var actual = Resources(file);
            if (expected.Count != actual.Count || expected.Any(e =>
                !actual.TryGetValue(e.Key, out var value) || e.Value.CodePage != value.CodePage ||
                !e.Value.Data.AsSpan().SequenceEqual(value.Data)))
                throw new InvalidDataException("Native resource payloads differ from " + args[1] + ": " + file);
        }
    }
    else if (args[0] == "--default")
    {
        foreach (var file in args.Skip(1))
        {
            var manifest = ManifestBytes(file, 1) ?? throw new InvalidDataException("Missing default manifest: " + file);
            var xml = XDocument.Parse(Encoding.UTF8.GetString(manifest).Trim('\uFEFF', '\0'));
            var level = xml.Descendants().Single(e => e.Name.LocalName == "requestedExecutionLevel");
            if ((string)level.Attribute("level") != "asInvoker" || (string)level.Attribute("uiAccess") != "false")
                throw new InvalidDataException("Incorrect default execution level: " + file);
        }
    }
    else if (args[0] == "--version")
    {
        foreach (var file in args.Skip(3))
        {
            var resource = Resources(file).Single(e => e.Key.StartsWith("#16/#1/")).Value.Data;
            var strings = new Dictionary<string, string>();
            void Read(int at)
            {
                ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(resource.AsSpan(offset, 2));
                int Align(int offset) => (offset + 3) & ~3;
                var end = at + U16(at); var length = U16(at + 2); var text = U16(at + 4) == 1;
                var keyEnd = at + 6;
                while (U16(keyEnd) != 0) keyEnd += 2;
                var key = Encoding.Unicode.GetString(resource, at + 6, keyEnd - at - 6);
                var value = Align(keyEnd + 2);
                if (text && length > 0) strings.Add(key, Encoding.Unicode.GetString(resource, value, length * 2).TrimEnd('\0'));
                for (var child = Align(value + length * (text ? 2 : 1)); child < end;)
                {
                    var childLength = U16(child);
                    if (childLength == 0) throw new InvalidDataException("Zero-sized version node");
                    Read(child); child = Align(child + childLength);
                }
            }
            Read(0);
            if (strings["FileVersion"] != args[1] || strings["ProductVersion"] != args[2] ||
                strings["CompanyName"] != "ResourceCompany" || strings["ProductName"] != "ResourceProduct")
                throw new InvalidDataException("Unexpected native version information: " + file);
        }
    }
    else if (args[0] == "--gui")
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
    => Resources(file).GetValueOrDefault($"#24/#{resourceId}/#0")?.Data;

static Dictionary<string, NativeResource> Resources(string file)
{
    var result = new Dictionary<string, NativeResource>();
    using var pe = new PEReader(File.OpenRead(file));
    var resource = pe.PEHeaders.PEHeader.ResourceTableDirectory;
    if (resource.Size == 0) return result;
    var bytes = pe.GetSectionData(resource.RelativeVirtualAddress).GetContent(0, resource.Size).ToArray();
    uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
    ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at, 2));
    void Read(int directory, string prefix)
    {
        var count = U16(directory + 12) + U16(directory + 14);
        for (var i = 0; i < count; i++)
        {
            var entry = checked(directory + 16 + i * 8);
            var id = U32(entry);
            var offset = checked((int)(id & 0x7fffffff));
            var name = (id & 0x80000000) == 0 ? "#" + id : Encoding.Unicode.GetString(bytes, offset + 2, U16(offset) * 2);
            var key = prefix + name;
            var target = U32(entry + 4);
            var data = checked((int)(target & 0x7fffffff));
            if ((target & 0x80000000) != 0) Read(data, key + "/");
            else
            {
                result.Add(key, new NativeResource(U32(data + 8),
                    pe.GetSectionData(checked((int)U32(data))).GetContent(0, checked((int)U32(data + 4))).ToArray()));
            }
        }
    }
    Read(0, "");
    return result;
}

record NativeResource(uint CodePage, byte[] Data);
