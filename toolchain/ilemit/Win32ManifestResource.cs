using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// RT_MANIFEST / SDK-selected application(1) or isolation-aware library(2) ID / neutral language.
// The SDK resolves the file and output kind; this builder only serializes those physical inputs.
sealed class Win32ManifestResource(byte[] content, uint resourceId) : ResourceSectionBuilder
{
    protected override void Serialize(BlobBuilder builder, SectionLocation location)
    {
        const int directorySize = 24; // IMAGE_RESOURCE_DIRECTORY + one IMAGE_RESOURCE_DIRECTORY_ENTRY
        const int dataEntryOffset = 3 * directorySize;
        const int contentOffset = dataEntryOffset + 16;
        void Directory(uint id, uint target)
        {
            builder.WriteUInt32(0); // characteristics
            builder.WriteUInt32(0); // timestamp
            builder.WriteUInt32(0); // major/minor versions
            builder.WriteUInt16(0); // named entries
            builder.WriteUInt16(1); // numeric entries
            builder.WriteUInt32(id);
            builder.WriteUInt32(target);
        }
        Directory(24, 0x80000000u | directorySize);
        Directory(resourceId, 0x80000000u | (2 * directorySize));
        Directory(0, dataEntryOffset);
        builder.WriteUInt32(checked((uint)(location.RelativeVirtualAddress + contentOffset)));
        builder.WriteUInt32(checked((uint)content.Length));
        builder.WriteUInt32(0); // code page
        builder.WriteUInt32(0); // reserved
        builder.WriteBytes(content);
    }
}
