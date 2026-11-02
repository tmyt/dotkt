using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

// Native PE resources are physical SDK output, not Kotlin semantics. The driver supplies the selected files;
// this writer encodes documented RES/ICO/VERSIONINFO structures without running a language compiler or mt.exe.
sealed class SdkNativeResources : ResourceSectionBuilder
{
    readonly record struct Key(string Text, int Id)
    {
        public static Key Number(int id) => new(null, id);
    }
    sealed record Resource(Key Type, Key Name, int Language, byte[] Data);
    readonly List<Resource> resources = [];

    public static ResourceSectionBuilder Create(SdkAssemblyOptions options, AssemblyVersions versions)
    {
        var result = new SdkNativeResources();
        if (!string.IsNullOrEmpty(options.Win32Resource))
        {
            if (!string.IsNullOrEmpty(options.Win32Manifest) || !string.IsNullOrEmpty(options.ApplicationIcon))
                throw new InvalidDataException("Win32Resource cannot be combined with ApplicationManifest or ApplicationIcon");
            using var stream = File.OpenRead(options.Win32Resource);
            Span<byte> signature = stackalloc byte[4];
            stream.ReadExactly(signature);
            if (BinaryPrimitives.ReadUInt32LittleEndian(signature) != 0)
                return CoffResources.Read(stream);
            result.ReadRes(options.Win32Resource);
            return result;
        }
        var isDll = string.Equals(options.OutputType, "Library", StringComparison.OrdinalIgnoreCase);
        result.Add(16, 1, VersionInfo(options, versions.Identity, isDll));
        if (!string.IsNullOrEmpty(options.Win32Manifest))
            result.Add(24, isDll ? 2 : 1, File.ReadAllBytes(options.Win32Manifest));
        else if (!isDll && !options.NoWin32Manifest)
            result.Add(24, 1, DefaultManifest(options.SdkResourceAssembly));
        if (!string.IsNullOrEmpty(options.ApplicationIcon)) result.ReadIcon(options.ApplicationIcon);
        return result;
    }

    void Add(int type, int name, byte[] data) => resources.Add(new(Key.Number(type), Key.Number(name), 0, data));

    static byte[] DefaultManifest(string sdkAssembly)
    {
        // The selected SDK owns its default manifest asset. Read it as data; never load or execute Roslyn, and
        // never reconstruct a resource from a previous compiler-produced application DLL.
        using var pe = new PEReader(File.OpenRead(sdkAssembly));
        var md = pe.GetMetadataReader();
        foreach (var handle in md.ManifestResources)
        {
            var resource = md.GetManifestResource(handle);
            if (md.GetString(resource.Name) != "Microsoft.CodeAnalysis.Resources.default.win32manifest") continue;
            if (!resource.Implementation.IsNil) throw new InvalidDataException("SDK default manifest is not embedded");
            var rva = checked(pe.PEHeaders.CorHeader.ResourcesDirectory.RelativeVirtualAddress + (int)resource.Offset);
            var data = pe.GetSectionData(rva).GetReader();
            return data.ReadBytes(data.ReadInt32());
        }
        throw new InvalidDataException($"SDK resource assembly contains no default Win32 manifest: {sdkAssembly}");
    }

    void ReadRes(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.Unicode);
        if (stream.Length < 32 || reader.ReadUInt32() != 0)
            throw new InvalidDataException($"Not a Win32 RES file: {path}");
        stream.Position = 0;
        Key Identifier()
        {
            var first = reader.ReadUInt16();
            if (first == ushort.MaxValue) return Key.Number(reader.ReadUInt16());
            var value = new StringBuilder();
            for (var ch = first; ch != 0; ch = reader.ReadUInt16()) value.Append((char)ch);
            return new Key(value.ToString(), 0);
        }
        while (stream.Position < stream.Length)
        {
            var start = stream.Position;
            var size = checked((int)reader.ReadUInt32());
            var headerSize = reader.ReadUInt32();
            var endHeader = checked(start + headerSize);
            if (headerSize < 8 || endHeader > stream.Length || size > stream.Length - endHeader)
                throw new InvalidDataException($"Malformed Win32 resource in {path}");
            if (size == 0) { stream.Position = endHeader; continue; }
            var type = Identifier();
            var name = Identifier();
            stream.Position = Align(stream.Position);
            _ = reader.ReadUInt32(); // data version
            _ = reader.ReadUInt16(); // memory flags
            var language = reader.ReadUInt16();
            _ = reader.ReadUInt32(); // version
            _ = reader.ReadUInt32(); // characteristics
            if (stream.Position > endHeader) throw new InvalidDataException($"Malformed Win32 resource header in {path}");
            stream.Position = endHeader;
            var data = reader.ReadBytes(size);
            if (type != Key.Number(17)) resources.Add(new(type, name, language, data)); // RT_DLGINCLUDE is RC source metadata
            stream.Position = Align(stream.Position);
        }
    }

    void ReadIcon(string path)
    {
        var content = File.ReadAllBytes(path);
        using var stream = new MemoryStream(content);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1)
            throw new InvalidDataException($"Not an ICO file: {path}");
        var count = reader.ReadUInt16();
        if (count == 0) throw new InvalidDataException($"Empty ICO file: {path}");
        using var group = new MemoryStream();
        using var writer = new BinaryWriter(group);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write(count);
        for (var i = 0; i < count; i++)
        {
            var dimensions = reader.ReadBytes(4);
            var planes = reader.ReadUInt16();
            var bits = reader.ReadUInt16();
            var size = checked((int)reader.ReadUInt32());
            var offset = checked((int)reader.ReadUInt32());
            var image = content.AsSpan(offset, size);
            // BITMAPINFOHEADER is authoritative for uncompressed images. PNG entries retain the ICO directory's
            // format fields, matching native resource tools without decoding/re-encoding either image format.
            if (BinaryPrimitives.ReadUInt32LittleEndian(image) == 40)
            {
                planes = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(12));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(14));
            }
            Add(3, i + 1, image.ToArray());
            writer.Write(dimensions); writer.Write(planes); writer.Write(bits); writer.Write(size); writer.Write((ushort)(i + 1));
        }
        Add(14, 0x7f00, group.ToArray()); // IDI_APPLICATION, referring to RT_ICON IDs starting at 1
    }

    static long Align(long value) => checked((value + 3) & ~3L);

    // Resource compilers may supply a COFF object instead of RES. Its directory and payload sections are already
    // encoded; resolve the explicit symbol relocations, then add only the final section RVA during serialization.
    sealed class CoffResources(byte[] content, int[] relocations) : ResourceSectionBuilder
    {
        public static CoffResources Read(Stream stream)
        {
            stream.Position = 0;
            var headers = new PEHeaders(stream);
            // member-lookup-residual: named COFF resource sections, not managed members or Kotlin operations
            var tree = headers.SectionHeaders.Single(s => s.Name == ".rsrc$01");
            var data = headers.SectionHeaders.Single(s => s.Name == ".rsrc$02");
            var dataIndex = headers.SectionHeaders.IndexOf(data);
            var bytes = new byte[checked(tree.SizeOfRawData + data.SizeOfRawData)];
            stream.Position = tree.PointerToRawData; stream.ReadExactly(bytes.AsSpan(0, tree.SizeOfRawData));
            stream.Position = data.PointerToRawData; stream.ReadExactly(bytes.AsSpan(tree.SizeOfRawData));
            var offsets = new int[tree.NumberOfRelocations];
            using var reader = new BinaryReader(stream, Encoding.Unicode, leaveOpen: true);
            for (var i = 0; i < offsets.Length; i++)
            {
                stream.Position = checked((long)tree.PointerToRelocations + i * 10);
                var offset = checked((int)reader.ReadUInt32());
                var symbol = reader.ReadUInt32();
                _ = reader.ReadUInt16(); // COFF relocation type, independent of resource payload format
                if (offset > tree.SizeOfRawData - 4 || symbol >= headers.CoffHeader.NumberOfSymbols)
                    throw new InvalidDataException("Invalid COFF resource relocation");
                stream.Position = checked((long)headers.CoffHeader.PointerToSymbolTable + symbol * 18L + 8);
                var value = reader.ReadUInt32();
                if (reader.ReadInt16() != dataIndex + 1 || reader.ReadUInt16() != 0 || value >= data.SizeOfRawData)
                    throw new InvalidDataException("Invalid COFF resource symbol");
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), checked(value + (uint)tree.SizeOfRawData));
                offsets[i] = offset;
            }
            return new CoffResources(bytes, offsets);
        }

        protected override void Serialize(BlobBuilder builder, SectionLocation location)
        {
            var bytes = (byte[])content.Clone();
            foreach (var offset in relocations)
            {
                var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), checked(value + (uint)location.RelativeVirtualAddress));
            }
            builder.WriteBytes(bytes);
        }
    }

    static byte[] VersionInfo(SdkAssemblyOptions options, Version identity, bool isDll)
    {
        var fileVersion = options.Attribute("AssemblyFileVersion") ?? identity.ToString();
        var productVersion = options.Attribute("AssemblyInformationalVersion") ?? fileVersion;
        var strings = new (string Name, string Value)[]
        {
            ("Comments", options.Attribute("AssemblyDescription")),
            ("CompanyName", options.Attribute("AssemblyCompany")),
            ("FileDescription", options.Attribute("AssemblyTitle") ?? " "),
            ("FileVersion", fileVersion),
            ("InternalName", options.OutputFileName),
            ("LegalCopyright", options.Attribute("AssemblyCopyright") ?? " "),
            ("LegalTrademarks", options.Attribute("AssemblyTrademark")),
            ("OriginalFilename", options.OutputFileName),
            ("ProductName", options.Attribute("AssemblyProduct")),
            ("ProductVersion", productVersion),
            ("Assembly Version", identity.ToString())
        };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode);
        void Pad() { while ((stream.Position & 3) != 0) writer.Write((byte)0); }
        long Begin(string key, ushort valueSize, ushort kind)
        {
            Pad();
            var start = stream.Position;
            writer.Write((ushort)0); writer.Write(valueSize); writer.Write(kind);
            writer.Write(key.ToCharArray()); writer.Write((ushort)0); Pad();
            return start;
        }
        void End(long start)
        {
            var end = stream.Position;
            stream.Position = start; writer.Write(checked((ushort)(end - start))); stream.Position = end;
        }
        var root = Begin("VS_VERSION_INFO", 52, 0);
        writer.Write(0xfeef04bdu); writer.Write(0x10000u);
        foreach (var text in new[] { fileVersion, productVersion })
        {
            var parts = NativeVersion(text);
            writer.Write(((uint)parts[0] << 16) | parts[1]);
            writer.Write(((uint)parts[2] << 16) | parts[3]);
        }
        writer.Write(0x3fu); writer.Write(0u); writer.Write(4u); writer.Write(isDll ? 2u : 1u);
        writer.Write(0u); writer.Write(0u); writer.Write(0u);
        var variable = Begin("VarFileInfo", 0, 1);
        var translation = Begin("Translation", 4, 0);
        writer.Write((ushort)0); writer.Write((ushort)1200);
        End(translation); End(variable);
        var stringInfo = Begin("StringFileInfo", 0, 1);
        var table = Begin("000004b0", 0, 1);
        foreach (var (name, value) in strings)
        {
            if (value == null) continue;
            var entry = Begin(name, checked((ushort)(value.Length + 1)), 1);
            writer.Write(value.ToCharArray()); writer.Write((ushort)0); End(entry);
        }
        End(table); End(stringInfo); End(root);
        return stream.ToArray();
    }

    static ushort[] NativeVersion(string text)
    {
        var result = new ushort[4];
        var parts = text.Split('.');
        for (var i = 0; i < Math.Min(4, parts.Length); i++)
        {
            var part = parts[i];
            var length = 0;
            while (length < part.Length && part[length] is >= '0' and <= '9') length++;
            if (length == 0) break;
            result[i] = (ushort)(BigInteger.Parse(part[..length], CultureInfo.InvariantCulture) % 65536);
            if (length != part.Length) break;
        }
        return result;
    }

    sealed class Directory
    {
        public int Offset;
        public List<(Key Key, Directory Child, Resource Resource, int Target)> Entries = [];
    }

    protected override void Serialize(BlobBuilder builder, SectionLocation location)
    {
        static IEnumerable<IGrouping<Key, Resource>> Groups(IEnumerable<Resource> values, Func<Resource, Key> key) =>
            values.GroupBy(key).OrderBy(g => g.Key.Text == null ? 1 : 0)
                .ThenBy(g => g.Key.Text, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Key.Id);
        var root = new Directory();
        foreach (var types in Groups(resources, r => r.Type))
        {
            var type = new Directory(); root.Entries.Add((types.Key, type, null, 0));
            foreach (var names in Groups(types, r => r.Name))
            {
                var name = new Directory(); type.Entries.Add((names.Key, name, null, 0));
                foreach (var languages in names.GroupBy(r => r.Language).OrderBy(g => g.Key))
                {
                    if (languages.Count() != 1) throw new InvalidDataException("Duplicate Win32 resource type/name/language");
                    name.Entries.Add((Key.Number(languages.Key), null, languages.Single(), 0));
                }
            }
        }
        var directories = new List<Directory>();
        var size = 0;
        void Allocate(Directory node)
        {
            node.Offset = size; size = checked(size + 16 + node.Entries.Count * 8); directories.Add(node);
            foreach (var e in node.Entries) if (e.Child != null) Allocate(e.Child);
        }
        Allocate(root);
        var namesAt = new Dictionary<Key, int>();
        foreach (var node in directories)
            foreach (var e in node.Entries)
                if (e.Key.Text != null && !namesAt.ContainsKey(e.Key))
                { namesAt.Add(e.Key, size); size = checked(size + 2 + e.Key.Text.Length * 2); }
        size = checked((int)Align(size));
        foreach (var node in directories)
            for (var i = 0; i < node.Entries.Count; i++)
            {
                var e = node.Entries[i];
                if (e.Resource == null) continue;
                node.Entries[i] = (e.Key, null, e.Resource, size); size = checked(size + 16);
            }
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode);
        stream.SetLength(size);
        foreach (var (key, at) in namesAt)
        { stream.Position = at; writer.Write(checked((ushort)key.Text.Length)); writer.Write(key.Text.ToCharArray()); }
        foreach (var node in directories)
        {
            stream.Position = node.Offset + 12;
            var named = node.Entries.Count(e => e.Key.Text != null);
            writer.Write(checked((ushort)named)); writer.Write(checked((ushort)(node.Entries.Count - named)));
            foreach (var e in node.Entries)
            {
                writer.Write(e.Key.Text == null ? (uint)e.Key.Id : 0x80000000u | (uint)namesAt[e.Key]);
                writer.Write(e.Child == null ? (uint)e.Target : 0x80000000u | (uint)e.Child.Offset);
                if (e.Resource == null) continue;
                var next = stream.Position;
                var payload = checked((int)Align(stream.Length));
                stream.Position = e.Target;
                writer.Write(checked((uint)(payload + location.RelativeVirtualAddress)));
                writer.Write(e.Resource.Data.Length); writer.Write(0u); writer.Write(0u); // RES payloads have no code page
                stream.Position = payload; writer.Write(e.Resource.Data); stream.Position = next;
            }
        }
        builder.WriteBytes(stream.ToArray());
    }
}
