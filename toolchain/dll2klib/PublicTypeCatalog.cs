using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

internal sealed record PublicTypeCatalogEntry(
    string AssemblyIdentity,
    string MetadataName,
    bool IsPublic,
    bool IsInterface);

// InterfaceImpl rows may name a TypeRef, or a TypeSpec whose generic arguments name TypeRefs.
// Resolve those references against the complete MSBuild reference universe before publishing a
// Kotlin supertype: Kotlin has no surface spelling for a CLR interface edge containing an
// inaccessible classifier.
internal sealed class PublicTypeCatalog
{
    private readonly Dictionary<string, PublicTypeCatalogEntry> _entries;

    private PublicTypeCatalog(IEnumerable<PublicTypeCatalogEntry> entries)
    {
        _entries = new Dictionary<string, PublicTypeCatalogEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var key = Key(entry.AssemblyIdentity, entry.MetadataName);
            if (_entries.TryGetValue(key, out var existing) &&
                (existing.IsPublic != entry.IsPublic || existing.IsInterface != entry.IsInterface))
                throw new InvalidOperationException(
                    $"type '{entry.MetadataName}' has conflicting shapes in assembly '{entry.AssemblyIdentity}'");
            _entries[key] = entry;
        }
    }

    public static PublicTypeCatalog Empty { get; } = new(Array.Empty<PublicTypeCatalogEntry>());

    public static PublicTypeCatalog Discover(IEnumerable<string> inputs)
    {
        var entries = new List<PublicTypeCatalogEntry>();
        var forwarders = new List<(string ForwardingIdentity, string TargetIdentity, string MetadataName)>();
        foreach (var path in inputs.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal))
        {
            using var file = File.OpenRead(path);
            using var pe = new PEReader(file, PEStreamOptions.PrefetchMetadata);
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) continue;
            var assemblyIdentity = AssemblyIdentity(md);
            foreach (var handle in md.TypeDefinitions)
            {
                var definition = md.GetTypeDefinition(handle);
                entries.Add(new PublicTypeCatalogEntry(
                    assemblyIdentity,
                    DefinitionName(md, handle),
                    IsPublic(md, handle),
                    (definition.Attributes & TypeAttributes.Interface) != 0));
            }
            foreach (var handle in md.ExportedTypes)
            {
                var exported = md.GetExportedType(handle);
                const TypeAttributes Forwarder = (TypeAttributes)0x00200000;
                if ((exported.Attributes & Forwarder) == 0 ||
                    (exported.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public)
                    continue;
                var targetIdentity = ExportedAssemblyIdentity(md, handle);
                if (targetIdentity is not null)
                    forwarders.Add((assemblyIdentity, targetIdentity, ExportedName(md, handle)));
            }
        }

        var all = new PublicTypeCatalog(entries);
        var changed = true;
        while (changed)
        {
            changed = false;
            var additions = new List<PublicTypeCatalogEntry>();
            foreach (var forwarder in forwarders)
                foreach (var target in all._entries.Values.Where(entry =>
                    StringComparer.Ordinal.Equals(entry.AssemblyIdentity, forwarder.TargetIdentity) &&
                    (StringComparer.Ordinal.Equals(entry.MetadataName, forwarder.MetadataName) ||
                     entry.MetadataName.StartsWith(forwarder.MetadataName + "+", StringComparison.Ordinal))))
                {
                    var alias = target with { AssemblyIdentity = forwarder.ForwardingIdentity };
                    if (!all._entries.ContainsKey(Key(alias.AssemblyIdentity, alias.MetadataName)))
                    {
                        additions.Add(alias);
                        changed = true;
                    }
                }
            if (changed) all = new PublicTypeCatalog(all._entries.Values.Concat(additions));
        }
        return all;
    }

    public static PublicTypeCatalog Load(string? path)
    {
        if (string.IsNullOrEmpty(path)) return Empty;
        var entries = JsonSerializer.Deserialize<List<PublicTypeCatalogEntry>>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"invalid public-type catalog: {path}");
        return new PublicTypeCatalog(entries);
    }

    public string Serialize() => JsonSerializer.Serialize(
        _entries.Values.OrderBy(x => x.AssemblyIdentity, StringComparer.Ordinal)
            .ThenBy(x => x.MetadataName, StringComparer.Ordinal).ToArray());

    public bool IsPublicInterface(MetadataReader reader, EntityHandle handle)
    {
        var provider = new SurfaceProvider(this);
        var surface = handle.Kind switch
        {
            HandleKind.TypeDefinition => provider.GetTypeFromDefinition(
                reader, (TypeDefinitionHandle)handle, 0),
            HandleKind.TypeReference => provider.GetTypeFromReference(
                reader, (TypeReferenceHandle)handle, 0),
            HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle)
                .DecodeSignature(provider, genericContext: null),
            _ => default,
        };
        return surface.IsPublic && surface.IsInterface;
    }

    private TypeSurface Resolve(MetadataReader reader, TypeReferenceHandle handle)
    {
        var identity = ReferenceAssemblyIdentity(reader, handle);
        return identity is not null &&
            _entries.TryGetValue(Key(identity, ReferenceName(reader, handle)), out var entry)
            ? new TypeSurface(entry.IsPublic, entry.IsInterface)
            : default;
    }

    private readonly record struct TypeSurface(bool IsPublic, bool IsInterface);

    private sealed class SurfaceProvider : ISignatureTypeProvider<TypeSurface, object?>
    {
        private readonly PublicTypeCatalog _catalog;

        internal SurfaceProvider(PublicTypeCatalog catalog) => _catalog = catalog;

        public TypeSurface GetArrayType(TypeSurface elementType, ArrayShape shape) =>
            new(elementType.IsPublic, false);
        public TypeSurface GetByReferenceType(TypeSurface elementType) => default;
        public TypeSurface GetFunctionPointerType(MethodSignature<TypeSurface> signature) => default;
        public TypeSurface GetGenericInstantiation(
            TypeSurface genericType,
            ImmutableArray<TypeSurface> typeArguments) =>
            new(genericType.IsPublic && typeArguments.All(x => x.IsPublic), genericType.IsInterface);
        public TypeSurface GetGenericMethodParameter(object? genericContext, int index) => new(true, false);
        public TypeSurface GetGenericTypeParameter(object? genericContext, int index) => new(true, false);
        public TypeSurface GetModifiedType(
            TypeSurface modifier,
            TypeSurface unmodifiedType,
            bool isRequired) => unmodifiedType;
        public TypeSurface GetPinnedType(TypeSurface elementType) => elementType;
        public TypeSurface GetPointerType(TypeSurface elementType) => default;
        public TypeSurface GetPrimitiveType(PrimitiveTypeCode typeCode) => new(true, false);
        public TypeSurface GetSZArrayType(TypeSurface elementType) => new(elementType.IsPublic, false);
        public TypeSurface GetTypeFromDefinition(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            byte rawTypeKind)
        {
            var definition = reader.GetTypeDefinition(handle);
            return new TypeSurface(
                IsPublic(reader, handle),
                (definition.Attributes & TypeAttributes.Interface) != 0);
        }
        public TypeSurface GetTypeFromReference(
            MetadataReader reader,
            TypeReferenceHandle handle,
            byte rawTypeKind) => _catalog.Resolve(reader, handle);
        public TypeSurface GetTypeFromSpecification(
            MetadataReader reader,
            object? genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind) => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }

    private static bool IsPublic(MetadataReader md, TypeDefinitionHandle handle)
    {
        var definition = md.GetTypeDefinition(handle);
        var visibility = definition.Attributes & TypeAttributes.VisibilityMask;
        var parent = definition.GetDeclaringType();
        return parent.IsNil
            ? visibility == TypeAttributes.Public
            : visibility == TypeAttributes.NestedPublic && IsPublic(md, parent);
    }

    private static string DefinitionName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var definition = md.GetTypeDefinition(handle);
        var simple = md.GetString(definition.Name);
        var parent = definition.GetDeclaringType();
        if (!parent.IsNil) return DefinitionName(md, parent) + "+" + simple;
        var ns = md.GetString(definition.Namespace);
        return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
    }

    private static string ReferenceName(MetadataReader md, TypeReferenceHandle handle)
    {
        var reference = md.GetTypeReference(handle);
        var simple = md.GetString(reference.Name);
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
            return ReferenceName(md, (TypeReferenceHandle)reference.ResolutionScope) + "+" + simple;
        var ns = md.GetString(reference.Namespace);
        return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
    }

    private static string AssemblyIdentity(MetadataReader md)
    {
        var assembly = md.GetAssemblyDefinition();
        return AssemblyIdentity(
            md.GetString(assembly.Name),
            assembly.Version,
            md.GetString(assembly.Culture),
            md.GetBlobBytes(assembly.PublicKey),
            publicKey: true);
    }

    private static string? ReferenceAssemblyIdentity(MetadataReader md, TypeReferenceHandle handle)
    {
        var scope = md.GetTypeReference(handle).ResolutionScope;
        return scope.Kind switch
        {
            HandleKind.AssemblyReference => AssemblyIdentity(md, (AssemblyReferenceHandle)scope),
            HandleKind.TypeReference => ReferenceAssemblyIdentity(md, (TypeReferenceHandle)scope),
            HandleKind.ModuleDefinition when md.IsAssembly => AssemblyIdentity(md),
            _ => null,
        };
    }

    private static string AssemblyIdentity(MetadataReader md, AssemblyReferenceHandle handle)
    {
        var assembly = md.GetAssemblyReference(handle);
        return AssemblyIdentity(
            md.GetString(assembly.Name),
            assembly.Version,
            md.GetString(assembly.Culture),
            md.GetBlobBytes(assembly.PublicKeyOrToken),
            publicKey: (assembly.Flags & AssemblyFlags.PublicKey) != 0);
    }

    private static string AssemblyIdentity(
        string name,
        Version version,
        string culture,
        byte[] key,
        bool publicKey)
    {
        var assembly = new AssemblyName(name)
        {
            Version = version,
            CultureName = string.IsNullOrEmpty(culture) ? null : culture,
        };
        if (key.Length != 0)
        {
            if (publicKey) assembly.SetPublicKey(key);
            else assembly.SetPublicKeyToken(key);
        }
        return assembly.FullName
            ?? throw new InvalidDataException($"could not form assembly identity for '{name}'");
    }

    private static string ExportedName(MetadataReader md, ExportedTypeHandle handle)
    {
        var exported = md.GetExportedType(handle);
        var simple = md.GetString(exported.Name);
        if (exported.Implementation.Kind == HandleKind.ExportedType)
            return ExportedName(md, (ExportedTypeHandle)exported.Implementation) + "+" + simple;
        var ns = md.GetString(exported.Namespace);
        return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
    }

    private static string? ExportedAssemblyIdentity(MetadataReader md, ExportedTypeHandle handle)
    {
        var implementation = md.GetExportedType(handle).Implementation;
        return implementation.Kind switch
        {
            HandleKind.AssemblyReference => AssemblyIdentity(md, (AssemblyReferenceHandle)implementation),
            HandleKind.ExportedType => ExportedAssemblyIdentity(md, (ExportedTypeHandle)implementation),
            _ => null,
        };
    }

    private static string Key(string assemblyIdentity, string metadataName) =>
        assemblyIdentity + "\0" + metadataName;
}
