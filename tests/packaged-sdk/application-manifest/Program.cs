using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Xml.Linq;

try
{
    uint resourceId = 1;
    if (args[0] == "--library") { resourceId = 2; args = args.Skip(1).ToArray(); }
    if (args[0] == "--managed-match")
    {
        var expected = Versions(args[1]);
        foreach (var file in args.Skip(2))
            if (!expected.SequenceEqual(Versions(file)))
                throw new InvalidDataException("Managed versions differ from " + args[1] + ": " + file);
    }
    else if (args[0] == "--managed")
    {
        var expected = args.Skip(1).Take(3).Select(v => v == "<absent>" ? null : v).Append(null).ToArray();
        foreach (var file in args.Skip(4))
            if (!expected.SequenceEqual(Versions(file)))
                throw new InvalidDataException("Unexpected managed versions: " + file + ": " + string.Join(" / ", Versions(file)));
    }
    else if (args[0] == "--wildcard")
    {
        var version = new Version(Versions(args[3])[0]);
        var start = long.Parse(args[1]); var end = long.Parse(args[2]);
        var matched = false;
        for (var second = start; second <= end; second++)
        {
            var time = DateTimeOffset.FromUnixTimeSeconds(second).LocalDateTime;
            var expected = new Version(2, 3, (time.Date - new DateTime(2000, 1, 1)).Days, (int)time.TimeOfDay.TotalSeconds / 2);
            matched |= version == expected;
        }
        if (!matched) throw new InvalidDataException($"Assembly wildcard version {version} is outside its emission interval");
    }
    else if (args[0] == "--reference-version")
    {
        foreach (var file in args.Skip(3))
        {
            using var pe = new PEReader(File.OpenRead(file));
            var metadata = pe.GetMetadataReader();
            var reference = metadata.AssemblyReferences.Select(metadata.GetAssemblyReference)
                .Single(r => metadata.GetString(r.Name) == args[1]);
            if (reference.Version.ToString() != args[2]) throw new InvalidDataException("Wrong AssemblyRef version: " + file);
        }
    }
    else if (args[0] == "--write-icon")
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
    else if (args[0] is "--write-res" or "--write-res-reversed-languages" or "--write-res-high-ids")
    {
        // Deliberately unordered RES records: named/numeric types and names, plus languages out of order.
        // Both compiler controls consume the same bytes; the PE oracle checks ordering as well as payloads.
        using var stream = File.Create(args[1]);
        using var writer = new BinaryWriter(stream, Encoding.Unicode);
        writer.Write(new byte[] { 0, 0, 0, 0, 32, 0, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0 });
        writer.Write(new byte[16]); // mandatory null resource header
        var data = Encoding.UTF8.GetBytes(args[2]);
        var reversedLanguages = args[0] == "--write-res-reversed-languages";
        var resources = args[0] == "--write-res-high-ids" ? new (object, object, ushort)[] { (60000, 50000, 1033) }
            : new (object, object, ushort)[] {
            ("NativeTest", "Payload", 1033), (10, 7, reversedLanguages ? (ushort)1041 : (ushort)1033), ("Zulu", "item", 1033), ("Alpha", "item", 1033),
            (10, "zeta", 1033), (10, "alpha", 1033), (10, 7, reversedLanguages ? (ushort)1033 : (ushort)1041), (10, 3, 1033) };
        foreach (var (type, name, language) in resources)
        {
            using var identifiers = new MemoryStream();
            using var namesWriter = new BinaryWriter(identifiers, Encoding.Unicode, leaveOpen: true);
            foreach (var key in new[] { type, name })
                if (key is int id) { namesWriter.Write(ushort.MaxValue); namesWriter.Write((ushort)id); }
                else { namesWriter.Write(((string)key).ToCharArray()); namesWriter.Write((ushort)0); }
            var names = identifiers.ToArray();
            var nameSize = (names.Length + 3) & ~3;
            writer.Write(data.Length); writer.Write(8 + nameSize + 16);
            writer.Write(names); writer.Write(new byte[nameSize - names.Length]);
            writer.Write(0); writer.Write((ushort)0x30); writer.Write(language);
            writer.Write(0); writer.Write(0); writer.Write(data);
            writer.Write(new byte[((data.Length + 3) & ~3) - data.Length]);
        }
    }
    else if (args[0] == "--write-coff")
    {
        // Produce a COFF resource object from the oracle's resource section, not from the implementation under
        // test. Preserve named IDs, language IDs, code pages and bytes, and express each payload RVA as a symbol.
        using var pe = new PEReader(File.OpenRead(args[1]));
        var section = pe.PEHeaders.PEHeader.ResourceTableDirectory;
        var content = pe.GetSectionData(section.RelativeVirtualAddress).GetContent(0, section.Size).ToArray();
        uint Read32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(at, 4));
        ushort Read16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(at, 2));
        var entries = new List<int>();
        void Visit(int directory)
        {
            var count = Read16(directory + 12) + Read16(directory + 14);
            for (var i = 0; i < count; i++)
            {
                var target = Read32(directory + 20 + i * 8);
                if ((target & 0x80000000u) != 0) Visit(checked((int)(target & 0x7fffffffu)));
                else entries.Add(checked((int)target));
            }
        }
        Visit(0);
        // Roslyn may interleave data-entry records and payloads. Keep the entire directory image in $01 and an
        // independent payload image in $02 rather than incorrectly splitting at the first payload's offset.
        var payloads = (byte[])content.Clone();
        var split = content.Length;
        var payloadOffsets = entries.Select(at => checked(Read32(at) - (uint)section.RelativeVirtualAddress)).ToArray();
        foreach (var at in entries) BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(at, 4), 0);
        using var stream = File.Create(args[2]);
        using var writer = new BinaryWriter(stream);
        const int headers = 20 + 3 * 40;
        var relocationsAt = headers + content.Length + payloads.Length;
        var symbolsAt = relocationsAt + entries.Count * 10;
        writer.Write((ushort)0x14c); writer.Write((ushort)3); writer.Write(0);
        writer.Write(symbolsAt); writer.Write(entries.Count); writer.Write((ushort)0); writer.Write((ushort)0);
        void Header(string name, int size, int start, int relocs, ushort count)
        {
            writer.Write(Encoding.ASCII.GetBytes(name.PadRight(8, '\0')));
            writer.Write(0); writer.Write(0); writer.Write(size); writer.Write(start); writer.Write(relocs);
            writer.Write(0); writer.Write(count); writer.Write((ushort)0); writer.Write(0x40000040u);
        }
        Header(".drectve", 0, 0, 0, 0);
        Header(".rsrc$01", split, headers, relocationsAt, checked((ushort)entries.Count));
        Header(".rsrc$02", payloads.Length, headers + split, 0, 0);
        writer.Write(content); writer.Write(payloads);
        for (var i = 0; i < entries.Count; i++)
        { writer.Write(entries[i]); writer.Write(i); writer.Write((ushort)7); } // IMAGE_REL_I386_DIR32NB
        for (var i = 0; i < entries.Count; i++)
        {
            writer.Write(Encoding.ASCII.GetBytes($"r{i:D7}")); writer.Write(payloadOffsets[i]);
            writer.Write((short)3); writer.Write((ushort)0); writer.Write((byte)2); writer.Write((byte)0);
        }
        writer.Write(4); // empty COFF string table
    }
    else if (args[0] == "--resource-payload")
    {
        foreach (var file in args.Skip(3))
        {
            var entry = Resources(file).GetValueOrDefault(args[1]);
            if (entry == null || !entry.Data.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(args[2])))
                throw new InvalidDataException($"Wrong native resource {args[1]} payload: {file}");
        }
    }
    else if (args[0] == "--resources")
    {
        // Compare the control's payloads, but independently enforce valid ordering on DotKt's outputs.
        // C# preserves RES language insertion order rather than sorting it, so it is not an ordering oracle.
        var expected = Resources(args[1], requireOrdering: false);
        foreach (var file in args.Skip(2))
        {
            var actual = Resources(file);
            if (expected.Count != actual.Count)
                throw new InvalidDataException($"Native resource count differs: {expected.Count} vs {actual.Count}: {file}");
            foreach (var (key, resource) in expected)
            {
                if (!actual.TryGetValue(key, out var value)) throw new InvalidDataException($"Missing native resource {key}: {file}");
                if (resource.CodePage != value.CodePage) throw new InvalidDataException($"Native resource {key} code page differs: {file}");
                if (!resource.Data.AsSpan().SequenceEqual(value.Data))
                {
                    var at = 0;
                    while (at < Math.Min(resource.Data.Length, value.Data.Length) && resource.Data[at] == value.Data[at]) at++;
                    throw new InvalidDataException($"Native resource {key} differs at byte {at}, lengths {resource.Data.Length}/{value.Data.Length}: {file}");
                }
            }
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

static string[] Versions(string file)
{
    using var pe = new PEReader(File.OpenRead(file));
    var metadata = pe.GetMetadataReader();
    var assembly = metadata.GetAssemblyDefinition();
    var attributes = new Dictionary<string, string>();
    foreach (var handle in assembly.GetCustomAttributes())
    {
        var attribute = metadata.GetCustomAttribute(handle);
        if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
        var ctor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
        if (ctor.Parent.Kind != HandleKind.TypeReference) continue;
        var type = metadata.GetTypeReference((TypeReferenceHandle)ctor.Parent);
        var name = metadata.GetString(type.Name);
        if (metadata.GetString(type.Namespace) != "System.Reflection" ||
            name is not ("AssemblyFileVersionAttribute" or "AssemblyInformationalVersionAttribute" or "AssemblyVersionAttribute")) continue;
        var value = metadata.GetBlobReader(attribute.Value);
        if (value.ReadUInt16() != 1) throw new InvalidDataException("Invalid custom attribute prolog");
        attributes.Add(name, value.ReadSerializedString()); // duplicate attributes fail rather than hiding one
    }
    return new[] { assembly.Version.ToString(), attributes.GetValueOrDefault("AssemblyFileVersionAttribute"),
        attributes.GetValueOrDefault("AssemblyInformationalVersionAttribute"), attributes.GetValueOrDefault("AssemblyVersionAttribute") };
}

static Dictionary<string, NativeResource> Resources(string file, bool requireOrdering = true)
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
        var namedCount = U16(directory + 12);
        var count = namedCount + U16(directory + 14);
        string previousName = null;
        uint previousId = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = checked(directory + 16 + i * 8);
            var id = U32(entry);
            var offset = checked((int)(id & 0x7fffffff));
            var name = (id & 0x80000000) == 0 ? "#" + id : Encoding.Unicode.GetString(bytes, offset + 2, U16(offset) * 2);
            var named = (id & 0x80000000) != 0;
            if (requireOrdering && (named != (i < namedCount) ||
                (named && previousName != null && StringComparer.Ordinal.Compare(previousName, name) >= 0) ||
                (!named && i > namedCount && id <= previousId)))
                throw new InvalidDataException($"Unsorted native resource directory '{prefix}' in {file}");
            previousName = named ? name : previousName;
            previousId = id;
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
