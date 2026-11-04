using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using AsmResolver;
using AsmResolver.IO;
using AsmResolver.PE.Win32Resources;
using AsmResolver.PE.Win32Resources.Builder;

// Adapt a resource directory to the BCL PE writer. AsmResolver owns the resource bytes and RVAs.
sealed class SdkNativeResources(ResourceDirectory root) : ResourceSectionBuilder
{
    public static ResourceSectionBuilder Create(SdkAssemblyOptions options)
    {
        using var stream = File.OpenRead(options.NativeResource);
        return new SdkNativeResources(SdkResReader.Read(options.NativeResourceSdkAssembly, options.NativeResource, stream));
    }

    protected override void Serialize(BlobBuilder builder, SectionLocation location)
    {
        // ResourceDirectoryBuffer preserves model order; supply named entries first and ascending keys.
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
