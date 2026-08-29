using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal sealed record ReferenceTypeFact(
    string? AssemblyName,
    string MetadataName,
    string ArityKey);

internal sealed class ReferenceMetadataSnapshot : IDisposable
{
    private readonly MetadataReaderProvider _provider;

    private ReferenceMetadataSnapshot(
        string path,
        DateTime lastWriteTimeUtc,
        MetadataReaderProvider provider)
    {
        Path = path;
        LastWriteTimeUtc = lastWriteTimeUtc;
        _provider = provider;
        Reader = provider.GetMetadataReader();
        TypeReferences = DiscoverTypeReferences(Reader);
    }

    public string Path { get; }
    public DateTime LastWriteTimeUtc { get; }
    public MetadataReader Reader { get; }
    public ReferenceTypeFact[] TypeReferences { get; }

    public static ReferenceMetadataSnapshot Open(string input)
    {
        var path = System.IO.Path.GetFullPath(input);
        using var file = File.OpenRead(path);
        using var pe = new PEReader(file, PEStreamOptions.PrefetchMetadata);
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader is null)
            throw new InvalidDataException($"not a managed PE: {path}");

        // Copy only the metadata block. The input stream and PE image are released immediately; the compact immutable
        // snapshot remains valid while all batch catalogs derive their independent facts from the same reader.
        ImmutableArray<byte> metadata = pe.GetMetadata().GetContent();
        var provider = MetadataReaderProvider.FromMetadataImage(metadata);
        try
        {
            return new ReferenceMetadataSnapshot(path, File.GetLastWriteTimeUtc(path), provider);
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    public void Dispose() => _provider.Dispose();

    private static ReferenceTypeFact[] DiscoverTypeReferences(MetadataReader md)
    {
        string MetadataName(TypeReferenceHandle handle)
        {
            var reference = md.GetTypeReference(handle);
            var simple = md.GetString(reference.Name);
            if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
                return MetadataName((TypeReferenceHandle)reference.ResolutionScope) + "+" + simple;
            var ns = md.GetString(reference.Namespace);
            return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
        }
        string ArityScope(TypeReferenceHandle handle)
        {
            var reference = md.GetTypeReference(handle);
            var name = md.GetString(reference.Name);
            if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
                return ArityScope((TypeReferenceHandle)reference.ResolutionScope) + "." + name;
            var ns = md.GetString(reference.Namespace);
            return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        }
        string? AssemblyName(TypeReferenceHandle handle)
        {
            var scope = md.GetTypeReference(handle).ResolutionScope;
            return scope.Kind switch
            {
                HandleKind.AssemblyReference => md.GetString(
                    md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name),
                HandleKind.TypeReference => AssemblyName((TypeReferenceHandle)scope),
                HandleKind.ModuleDefinition when md.IsAssembly => md.GetString(md.GetAssemblyDefinition().Name),
                _ => null,
            };
        }

        return md.TypeReferences.Select(handle =>
            {
                var reference = md.GetTypeReference(handle);
                var name = md.GetString(reference.Name);
                var tick = name.IndexOf('`');
                var simple = tick < 0 ? name : name[..tick];
                var scope = reference.ResolutionScope.Kind == HandleKind.TypeReference
                    ? ArityScope((TypeReferenceHandle)reference.ResolutionScope)
                    : md.GetString(reference.Namespace);
                return new ReferenceTypeFact(
                    AssemblyName(handle),
                    MetadataName(handle),
                    string.IsNullOrEmpty(scope) ? simple : scope + "." + simple);
            })
            .ToArray();
    }
}

internal sealed class ReferenceMetadataSet : IDisposable
{
    private readonly ReferenceMetadataSnapshot[] _assemblies;

    private ReferenceMetadataSet(ReferenceMetadataSnapshot[] assemblies) => _assemblies = assemblies;

    public IReadOnlyList<ReferenceMetadataSnapshot> Assemblies => _assemblies;

    public static ReferenceMetadataSet Open(IEnumerable<string> inputs)
    {
        var assemblies = new List<ReferenceMetadataSnapshot>();
        try
        {
            foreach (var path in inputs.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal))
                assemblies.Add(ReferenceMetadataSnapshot.Open(path));
            return new ReferenceMetadataSet(assemblies.ToArray());
        }
        catch
        {
            foreach (var assembly in assemblies) assembly.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var assembly in _assemblies) assembly.Dispose();
    }
}
