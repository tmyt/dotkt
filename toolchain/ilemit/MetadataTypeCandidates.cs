#nullable disable
using System.Reflection;
using System.Reflection.Metadata;

// An index of physical namespaces, not a type resolver. The pinned metadata context still
// resolves every candidate, including forwarding, and TargetReferenceUniverse checks all definers.
sealed class MetadataTypeCandidates
{
    static readonly PropertyInfo ReaderProperty = typeof(MetadataLoadContext).Assembly
        .GetType("System.Reflection.TypeLoading.Ecma.EcmaAssembly", throwOnError: true)
        .GetProperty("Reader", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMemberException("MetadataLoadContext EcmaAssembly.Reader");
    readonly Assembly[] _assemblies;
    readonly Dictionary<string, List<Assembly>> _byNamespace = new(StringComparer.Ordinal);
    readonly List<Assembly> _multiModule = new();

    public MetadataTypeCandidates(Assembly[] assemblies)
    {
        if (ReaderProperty.PropertyType != typeof(MetadataReader))
            throw new InvalidOperationException("MetadataLoadContext metadata-reader contract changed");
        _assemblies = assemblies;
        foreach (var assembly in assemblies)
        {
            var reader = (MetadataReader)ReaderProperty.GetValue(assembly);
            // Non-public definitions in additional modules need not have ExportedType rows.
            // Let the context search those modules itself rather than assuming manifest completeness.
            if (reader.AssemblyFiles.Any(handle => reader.GetAssemblyFile(handle).ContainsMetadata))
            {
                _multiModule.Add(assembly);
                foreach (var candidates in _byNamespace.Values) candidates.Add(assembly);
                continue;
            }
            var names = new HashSet<StringHandle>();
            foreach (var handle in reader.TypeDefinitions)
            {
                var definition = reader.GetTypeDefinition(handle);
                if (!definition.IsNested) names.Add(definition.Namespace);
            }
            foreach (var handle in reader.ExportedTypes)
            {
                var exported = reader.GetExportedType(handle);
                if (exported.Implementation.Kind != HandleKind.ExportedType)
                    names.Add(exported.Namespace);
            }
            foreach (var handle in names)
            {
                var name = reader.GetString(handle);
                if (!_byNamespace.TryGetValue(name, out var candidates))
                    _byNamespace[name] = candidates = new(_multiModule);
                if (!candidates.Contains(assembly)) candidates.Add(assembly);
            }
        }
    }

    public IEnumerable<Assembly> For(string fullName)
    {
        // Constructed/escaped reflection expressions retain the context's complete parser path.
        if (fullName.IndexOfAny(['\\', '[', ']', '*', '&', ',']) >= 0) return _assemblies;
        var nested = fullName.IndexOf('+');
        var root = nested < 0 ? fullName : fullName[..nested];
        var dot = root.LastIndexOf('.');
        var space = dot < 0 ? "" : root[..dot];
        return _byNamespace.TryGetValue(space, out var candidates) ? candidates : _multiModule;
    }
}
