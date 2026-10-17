using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// The SDK selects the resource provider PE. Preserve its native resource tree and payloads;
// only IMAGE_RESOURCE_DATA_ENTRY RVAs change when the section moves to the emitted PE.
sealed class Win32Resources(byte[] content, int sourceRva, int[] dataEntries) : ResourceSectionBuilder
{
    public static Win32Resources FromPE(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        var directory = pe.PEHeaders.PEHeader.ResourceTableDirectory;
        if (directory.Size == 0) return null;
        var content = pe.GetSectionData(directory.RelativeVirtualAddress).GetContent(0, directory.Size).ToArray();
        var entries = new List<int>();
        var visited = new HashSet<int>();
        uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(offset, 4));
        ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(offset, 2));
        void Visit(int offset)
        {
            if (!visited.Add(offset)) throw new BadImageFormatException("Cyclic or shared native resource directory");
            var count = checked(U16(offset + 12) + U16(offset + 14));
            for (var i = 0; i < count; i++)
            {
                var target = U32(checked(offset + 16 + i * 8 + 4));
                var at = checked((int)(target & 0x7fffffff));
                if ((target & 0x80000000) != 0) Visit(at);
                else
                {
                    var dataOffset = checked((int)U32(at) - directory.RelativeVirtualAddress);
                    var size = checked((int)U32(at + 4));
                    _ = content.AsSpan(dataOffset, size); // payload must belong to the supplied resource tree
                    _ = content.AsSpan(at, 16);
                    entries.Add(at);
                }
            }
        }
        Visit(0);
        return new Win32Resources(content, directory.RelativeVirtualAddress, entries.Distinct().ToArray());
    }

    protected override void Serialize(BlobBuilder builder, SectionLocation location)
    {
        var relocated = (byte[])content.Clone();
        foreach (var at in dataEntries)
        {
            var source = BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(at, 4));
            var target = checked((uint)(source - sourceRva + location.RelativeVirtualAddress));
            BinaryPrimitives.WriteUInt32LittleEndian(relocated.AsSpan(at, 4), target);
        }
        builder.WriteBytes(relocated);
    }
}
