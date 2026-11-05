using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

sealed partial class Emitter
{
    readonly bool _deterministic;

    BlobContentId DeterministicSymbolsId(ImmutableArray<int> rows, MethodDefinitionHandle entryPoint, BlobBuilder il)
    {
        // These are the complete inputs to WritePortablePdb: row counts and entry point, ordered source
        // points, and IL bodies (including their final code sizes and local-signature tokens).
        var identity = new BlobBuilder();
        identity.WriteInt32(rows.Length);
        foreach (var count in rows) identity.WriteInt32(count);
        identity.WriteInt32(MetadataTokens.GetToken(entryPoint));
        identity.WriteInt32(il.Count);
        foreach (var pair in _sourcePoints.OrderBy(pair => pair.Key.MetadataToken))
        {
            identity.WriteInt32(pair.Key.MetadataToken);
            identity.WriteInt32(pair.Value.Count);
            foreach (var point in pair.Value)
            {
                identity.WriteInt32(point.Offset);
                identity.WriteSerializedString(point.File);
                identity.WriteInt32(point.Line);
                identity.WriteInt32(point.Column);
                identity.WriteInt32(point.EndLine);
                identity.WriteInt32(point.EndColumn);
            }
        }
        return ContentId(identity.GetBlobs().Concat(il.GetBlobs()));
    }

    static BlobContentId ContentId(IEnumerable<Blob> content)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var blob in content) hash.AppendData(blob.GetBytes().AsSpan());
        return BlobContentId.FromHash(hash.GetHashAndReset());
    }

    static BlobContentId DeterministicPeId(IEnumerable<Blob> content)
    {
        var parts = content.ToArray();
        var bytes = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            var data = part.GetBytes();
            data.AsSpan().CopyTo(bytes.AsSpan(offset));
            offset += data.Count;
        }
        // PersistedAssemblyBuilder supplies a random MVID and no public setter. Exclude that slot
        // from the hash, then fix it to the resulting content GUID after PEBuilder has serialized.
        bytes.AsSpan(ModuleVersionIdOffset(bytes), 16).Clear();
        return BlobContentId.FromHash(SHA256.HashData(bytes));
    }

    static int ModuleVersionIdOffset(byte[] bytes)
    {
        // Locate the actual Module row's GUID through the framework reader, never by byte-pattern search.
        using var pe = new PEReader(new MemoryStream(bytes, writable: false));
        var metadata = pe.GetMetadataReader();
        var index = MetadataTokens.GetHeapOffset(metadata.GetModuleDefinition().Mvid);
        return checked(pe.PEHeaders.MetadataStartOffset
            + metadata.GetHeapMetadataOffset(HeapIndex.Guid) + (index - 1) * 16);
    }
}
