using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using AsmResolver;
using AsmResolver.IO;
using AsmResolver.PE.Win32Resources;
using AsmResolver.PE.Win32Resources.Builder;

// Adapt Roslyn/SDK RES data to the BCL PE writer. ResourceDirectoryBuffer owns PE directory layout;
// the adapter does not synthesize VERSIONINFO, manifests, icon groups or resource directory bytes.
sealed class SdkNativeResources : ResourceSectionBuilder
{
    readonly ResourceDirectory root = new(0u);

    public static ResourceSectionBuilder Create(SdkAssemblyOptions options)
    {
        using var stream = File.OpenRead(options.NativeResource);
        Span<byte> signature = stackalloc byte[4];
        stream.ReadExactly(signature);
        if (BinaryPrimitives.ReadUInt32LittleEndian(signature) != 0)
            return CoffResources.Read(stream);
        var result = new SdkNativeResources();
        result.ReadRes(options.NativeResource);
        return result;
    }

    void ReadRes(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.Unicode);
        if (stream.Length < 32 || reader.ReadUInt32() != 0)
            throw new InvalidDataException($"Not a Win32 RES file: {path}");
        stream.Position = 0;
        (string Text, uint Id) Identifier()
        {
            var first = reader.ReadUInt16();
            if (first == ushort.MaxValue) return (null, reader.ReadUInt16());
            var value = new StringBuilder();
            for (var ch = first; ch != 0; ch = reader.ReadUInt16()) value.Append((char)ch);
            return (value.ToString(), 0);
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
            if (type != (null, 17u)) // RT_DLGINCLUDE is RC source metadata
            {
                ResourceDirectory Directory(ResourceDirectory parent, (string Text, uint Id) key)
                {
                    var found = parent.Entries.OfType<ResourceDirectory>()
                        // member-lookup-residual: native resource directory keys, not CLR members or Kotlin semantics
                        .SingleOrDefault(e => e.Name == key.Text && (key.Text != null || e.Id == key.Id));
                    if (found != null) return found;
                    var created = key.Text == null ? new ResourceDirectory(key.Id) : new ResourceDirectory(key.Text);
                    parent.Entries.Add(created);
                    return created;
                }
                var directory = Directory(Directory(root, type), name);
                if (directory.Entries.Any(e => e.Id == language))
                    throw new InvalidDataException("Duplicate Win32 resource type/name/language");
                directory.Entries.Add(new ResourceData(language, new DataSegment(data)));
            }
            stream.Position = Align(stream.Position);
        }
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

    protected override void Serialize(BlobBuilder builder, SectionLocation location)
    {
        // RES is a sequence, while PE directories require named entries first and ascending keys at every level.
        // ResourceDirectoryBuffer preserves model order, so establish its precondition before delegating layout.
        static void Order(ResourceDirectory node)
        {
            // member-lookup-residual: ordering native resource keys, not selecting managed members
            var entries = node.Entries.OrderBy(e => e.Name == null ? 1 : 0)
                .ThenBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Id).ToArray();
            node.Entries.Clear();
            foreach (var entry in entries)
            {
                if (entry is ResourceDirectory child) Order(child);
                node.Entries.Add(entry);
            }
        }
        Order(root);
        var directory = new ResourceDirectoryBuffer();
        directory.AddDirectory(root);
        directory.UpdateOffsets(new RelocationParameters(0, checked((uint)location.RelativeVirtualAddress)));
        using var stream = new MemoryStream();
        directory.Write(new BinaryStreamWriter(stream));
        builder.WriteBytes(stream.ToArray());
    }
}
