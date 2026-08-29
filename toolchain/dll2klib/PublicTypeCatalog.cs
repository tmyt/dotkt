using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal sealed record PublicTypeCatalogEntry(
    string AssemblyName,
    string MetadataName,
    bool IsPublic,
    bool IsInterface,
    string AssemblyPath);

internal sealed record ResolvedTypeDefinition(
    MetadataReader Reader,
    TypeDefinitionHandle Handle,
    string? DefinitionPath = null);

internal readonly record struct PublicTypeSurface(
    bool IsPublic,
    bool IsInterface,
    ImmutableArray<bool> TypeArguments);

// InterfaceImpl rows may name a TypeRef, or a TypeSpec whose generic arguments name TypeRefs.
// Resolve those references against the complete MSBuild reference universe before publishing a
// Kotlin supertype: Kotlin has no surface spelling for a CLR interface edge containing an
// inaccessible classifier.
internal sealed class PublicTypeCatalog : IDisposable
{
    private readonly Dictionary<string, PublicTypeCatalogEntry> _entries;
    private readonly ConcurrentDictionary<string, Lazy<LoadedAssembly>> _loaded =
        new(StringComparer.Ordinal);
    private readonly Lazy<IReadOnlyDictionary<string, string[]>> _directDependencies;

    private PublicTypeCatalog(IEnumerable<PublicTypeCatalogEntry> entries)
    {
        _entries = new Dictionary<string, PublicTypeCatalogEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var key = Key(entry.AssemblyName, entry.MetadataName);
            if (_entries.TryGetValue(key, out var existing) &&
                (existing.IsPublic != entry.IsPublic || existing.IsInterface != entry.IsInterface))
                throw new InvalidOperationException(
                    $"type '{entry.MetadataName}' has conflicting shapes in assembly '{entry.AssemblyName}'");
            _entries[key] = entry;
        }
        _directDependencies = new Lazy<IReadOnlyDictionary<string, string[]>>(
            BuildDirectDependencies,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static PublicTypeCatalog Discover(IEnumerable<string> inputs)
    {
        var entries = new List<PublicTypeCatalogEntry>();
        var forwarders = new List<(string ForwardingAssembly, string TargetAssembly, string MetadataName)>();
        foreach (var path in inputs.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal))
        {
            using var file = File.OpenRead(path);
            using var pe = new PEReader(file, PEStreamOptions.PrefetchMetadata);
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) continue;
            var assemblyName = AssemblyName(md);
            foreach (var handle in md.TypeDefinitions)
            {
                var definition = md.GetTypeDefinition(handle);
                entries.Add(new PublicTypeCatalogEntry(
                    assemblyName,
                    DefinitionName(md, handle),
                    IsPublic(md, handle),
                    (definition.Attributes & TypeAttributes.Interface) != 0,
                    path));
            }
            foreach (var handle in md.ExportedTypes)
            {
                var exported = md.GetExportedType(handle);
                const TypeAttributes Forwarder = (TypeAttributes)0x00200000;
                // A type forwarder is exported by definition. Real facade rows carry tdForwarder
                // without a TypeAttributes visibility bit (for example netstandard and mscorlib),
                // so requiring Public here makes the entire forwarder graph unreachable.
                if ((exported.Attributes & Forwarder) == 0)
                    continue;
                var targetAssembly = ExportedAssemblyName(md, handle);
                if (targetAssembly is not null)
                    forwarders.Add((assemblyName, targetAssembly, ExportedName(md, handle)));
            }
        }

        var all = new PublicTypeCatalog(entries);
        var changed = true;
        while (changed)
        {
            changed = false;
            var additions = new List<PublicTypeCatalogEntry>();
            // A forwarder can expose one top-level type together with all of its nested definitions. Index that
            // narrow family once per fixed-point round instead of scanning every TypeDef in every resolved assembly
            // for every ExportedType row (netstandard alone carries thousands of forwarders).
            var targetsByRoot = all._entries.Values
                .GroupBy(entry => (entry.AssemblyName, RootName(entry.MetadataName)))
                .ToDictionary(group => group.Key, group => group.ToArray());
            foreach (var forwarder in forwarders)
            {
                if (!targetsByRoot.TryGetValue(
                        (forwarder.TargetAssembly, RootName(forwarder.MetadataName)),
                        out var candidates))
                    continue;
                foreach (var target in candidates.Where(entry =>
                    StringComparer.Ordinal.Equals(entry.MetadataName, forwarder.MetadataName) ||
                    entry.MetadataName.StartsWith(forwarder.MetadataName + "+", StringComparison.Ordinal)))
                {
                    var alias = target with { AssemblyName = forwarder.ForwardingAssembly };
                    if (!all._entries.ContainsKey(Key(alias.AssemblyName, alias.MetadataName)))
                    {
                        additions.Add(alias);
                        changed = true;
                    }
                }
            }
            if (changed) all = new PublicTypeCatalog(all._entries.Values.Concat(additions));
        }
        return all;
    }

    public IReadOnlyList<string> DirectDependenciesOf(string input)
    {
        input = Path.GetFullPath(input);
        return _directDependencies.Value.TryGetValue(input, out var dependencies)
            ? dependencies
            : Array.Empty<string>();
    }

    private IReadOnlyDictionary<string, string[]> BuildDirectDependencies()
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var path in _entries.Values.Select(entry => entry.AssemblyPath)
                     .Distinct(StringComparer.Ordinal))
        {
            using var file = File.OpenRead(path);
            using var pe = new PEReader(file, PEStreamOptions.PrefetchMetadata);
            if (!pe.HasMetadata)
            {
                result[path] = [];
                continue;
            }
            var md = pe.GetMetadataReader();
            result[path] = md.TypeReferences
                .Select(handle => ResolveEntry(md, handle)?.AssemblyPath)
                .Where(dependency => dependency is not null &&
                    !StringComparer.Ordinal.Equals(dependency, path))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .OrderBy(dependency => dependency, StringComparer.Ordinal)
                .ToArray();
        }
        return result;
    }

    public PublicTypeSurface Surface(
        MetadataReader reader,
        EntityHandle handle,
        ImmutableArray<bool> genericTypeArguments = default)
    {
        var provider = new SurfaceProvider(this);
        var surface = handle.Kind switch
        {
            HandleKind.TypeDefinition => provider.GetTypeFromDefinition(
                reader, (TypeDefinitionHandle)handle, 0),
            HandleKind.TypeReference => provider.GetTypeFromReference(
                reader, (TypeReferenceHandle)handle, 0),
            HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle)
                .DecodeSignature(provider, genericTypeArguments),
            _ => default,
        };
        return surface;
    }

    public bool IsPublicInterface(MetadataReader reader, EntityHandle handle)
    {
        var surface = Surface(reader, handle);
        return surface.IsPublic && surface.IsInterface;
    }

    public bool TryResolveDefinition(
        MetadataReader reader,
        EntityHandle handle,
        out ResolvedTypeDefinition resolved,
        string? definitionPath = null)
    {
        ResolvedTypeDefinition? value = handle.Kind switch
        {
            HandleKind.TypeDefinition => new(reader, (TypeDefinitionHandle)handle, definitionPath),
            HandleKind.TypeReference => ResolveDefinition(reader, (TypeReferenceHandle)handle),
            HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle)
                .DecodeSignature(new DefinitionProvider(this, definitionPath), genericContext: null),
            _ => null,
        };
        resolved = value!;
        return value is not null;
    }

    public void Dispose()
    {
        foreach (var assembly in _loaded.Values)
            if (assembly.IsValueCreated)
                assembly.Value.Dispose();
        _loaded.Clear();
    }

    private PublicTypeSurface Resolve(MetadataReader reader, TypeReferenceHandle handle)
    {
        var entry = ResolveEntry(reader, handle);
        if (entry is null)
            throw new InvalidDataException(
                $"public-type catalog cannot resolve '{ReferenceName(reader, handle)}' from assembly " +
                $"'{ReferenceAssemblyName(reader, handle) ?? "<unknown>"}'; pass the complete resolved reference set");
        return new PublicTypeSurface(entry.IsPublic, entry.IsInterface, []);
    }

    private PublicTypeCatalogEntry? ResolveEntry(MetadataReader reader, TypeReferenceHandle handle)
    {
        var assemblyName = ReferenceAssemblyName(reader, handle);
        return assemblyName is not null &&
            _entries.TryGetValue(Key(assemblyName, ReferenceName(reader, handle)), out var entry)
            ? entry
            : null;
    }

    private ResolvedTypeDefinition? ResolveDefinition(MetadataReader reader, TypeReferenceHandle handle)
    {
        var entry = ResolveEntry(reader, handle);
        if (entry is null) return null;
        var assembly = _loaded.GetOrAdd(
            entry.AssemblyPath,
            static path => new Lazy<LoadedAssembly>(
                () => new LoadedAssembly(path),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        return assembly.Definitions.TryGetValue(entry.MetadataName, out var definition)
            ? new ResolvedTypeDefinition(assembly.Reader, definition, entry.AssemblyPath)
            : null;
    }

    private sealed class SurfaceProvider : ISignatureTypeProvider<PublicTypeSurface, ImmutableArray<bool>>
    {
        private readonly PublicTypeCatalog _catalog;

        internal SurfaceProvider(PublicTypeCatalog catalog) => _catalog = catalog;

        public PublicTypeSurface GetArrayType(PublicTypeSurface elementType, ArrayShape shape) =>
            new(elementType.IsPublic, false, []);
        public PublicTypeSurface GetByReferenceType(PublicTypeSurface elementType) => default;
        public PublicTypeSurface GetFunctionPointerType(MethodSignature<PublicTypeSurface> signature) => default;
        public PublicTypeSurface GetGenericInstantiation(
            PublicTypeSurface genericType,
            ImmutableArray<PublicTypeSurface> typeArguments) =>
            new(
                genericType.IsPublic && typeArguments.All(x => x.IsPublic),
                genericType.IsInterface,
                typeArguments.Select(argument => argument.IsPublic).ToImmutableArray());
        public PublicTypeSurface GetGenericMethodParameter(ImmutableArray<bool> genericContext, int index) =>
            new(true, false, []);
        public PublicTypeSurface GetGenericTypeParameter(ImmutableArray<bool> genericContext, int index) =>
            new(genericContext.IsDefault || index >= genericContext.Length || genericContext[index], false, []);
        public PublicTypeSurface GetModifiedType(
            PublicTypeSurface modifier,
            PublicTypeSurface unmodifiedType,
            bool isRequired) => unmodifiedType;
        public PublicTypeSurface GetPinnedType(PublicTypeSurface elementType) => elementType;
        public PublicTypeSurface GetPointerType(PublicTypeSurface elementType) => default;
        public PublicTypeSurface GetPrimitiveType(PrimitiveTypeCode typeCode) => new(true, false, []);
        public PublicTypeSurface GetSZArrayType(PublicTypeSurface elementType) =>
            new(elementType.IsPublic, false, []);
        public PublicTypeSurface GetTypeFromDefinition(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            byte rawTypeKind)
        {
            var definition = reader.GetTypeDefinition(handle);
            return new PublicTypeSurface(
                IsPublic(reader, handle),
                (definition.Attributes & TypeAttributes.Interface) != 0,
                []);
        }
        public PublicTypeSurface GetTypeFromReference(
            MetadataReader reader,
            TypeReferenceHandle handle,
            byte rawTypeKind) => _catalog.Resolve(reader, handle);
        public PublicTypeSurface GetTypeFromSpecification(
            MetadataReader reader,
            ImmutableArray<bool> genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind) => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }

    private sealed class DefinitionProvider : ISignatureTypeProvider<ResolvedTypeDefinition?, object?>
    {
        private readonly PublicTypeCatalog _catalog;
        private readonly string? _definitionPath;

        internal DefinitionProvider(PublicTypeCatalog catalog, string? definitionPath)
        {
            _catalog = catalog;
            _definitionPath = definitionPath;
        }

        public ResolvedTypeDefinition? GetArrayType(ResolvedTypeDefinition? elementType, ArrayShape shape) => null;
        public ResolvedTypeDefinition? GetByReferenceType(ResolvedTypeDefinition? elementType) => null;
        public ResolvedTypeDefinition? GetFunctionPointerType(MethodSignature<ResolvedTypeDefinition?> signature) => null;
        public ResolvedTypeDefinition? GetGenericInstantiation(
            ResolvedTypeDefinition? genericType,
            ImmutableArray<ResolvedTypeDefinition?> typeArguments) => genericType;
        public ResolvedTypeDefinition? GetGenericMethodParameter(object? genericContext, int index) => null;
        public ResolvedTypeDefinition? GetGenericTypeParameter(object? genericContext, int index) => null;
        public ResolvedTypeDefinition? GetModifiedType(
            ResolvedTypeDefinition? modifier,
            ResolvedTypeDefinition? unmodifiedType,
            bool isRequired) => unmodifiedType;
        public ResolvedTypeDefinition? GetPinnedType(ResolvedTypeDefinition? elementType) => elementType;
        public ResolvedTypeDefinition? GetPointerType(ResolvedTypeDefinition? elementType) => null;
        public ResolvedTypeDefinition? GetPrimitiveType(PrimitiveTypeCode typeCode) => null;
        public ResolvedTypeDefinition? GetSZArrayType(ResolvedTypeDefinition? elementType) => null;
        public ResolvedTypeDefinition? GetTypeFromDefinition(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            byte rawTypeKind) => new(reader, handle, _definitionPath);
        public ResolvedTypeDefinition? GetTypeFromReference(
            MetadataReader reader,
            TypeReferenceHandle handle,
            byte rawTypeKind) => _catalog.ResolveDefinition(reader, handle);
        public ResolvedTypeDefinition? GetTypeFromSpecification(
            MetadataReader reader,
            object? genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind) => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }

    private sealed class LoadedAssembly : IDisposable
    {
        private readonly FileStream _file;
        private readonly PEReader _pe;

        internal LoadedAssembly(string path)
        {
            _file = File.OpenRead(path);
            _pe = new PEReader(_file, PEStreamOptions.PrefetchMetadata);
            if (!_pe.HasMetadata)
                throw new InvalidDataException($"not a managed PE: {path}");
            Reader = _pe.GetMetadataReader();
            Definitions = Reader.TypeDefinitions.ToDictionary(
                handle => DefinitionName(Reader, handle),
                handle => handle,
                StringComparer.Ordinal);
        }

        internal MetadataReader Reader { get; }
        internal IReadOnlyDictionary<string, TypeDefinitionHandle> Definitions { get; }

        public void Dispose()
        {
            _pe.Dispose();
            _file.Dispose();
        }
    }

    private static bool IsPublic(MetadataReader md, TypeDefinitionHandle handle)
    {
        var definition = md.GetTypeDefinition(handle);
        var visibility = definition.Attributes & TypeAttributes.VisibilityMask;
        var parent = definition.GetDeclaringType();
        return parent.IsNil
            ? visibility == TypeAttributes.Public
            : visibility is TypeAttributes.NestedPublic or
                TypeAttributes.NestedFamily or
                TypeAttributes.NestedFamORAssem && IsPublic(md, parent);
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

    private static string RootName(string metadataName)
    {
        var separator = metadataName.IndexOf('+');
        return separator < 0 ? metadataName : metadataName[..separator];
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

    private static string AssemblyName(MetadataReader md) =>
        md.GetString(md.GetAssemblyDefinition().Name);

    private static string? ReferenceAssemblyName(MetadataReader md, TypeReferenceHandle handle)
    {
        var scope = md.GetTypeReference(handle).ResolutionScope;
        return scope.Kind switch
        {
            HandleKind.AssemblyReference => AssemblyName(md, (AssemblyReferenceHandle)scope),
            HandleKind.TypeReference => ReferenceAssemblyName(md, (TypeReferenceHandle)scope),
            HandleKind.ModuleDefinition when md.IsAssembly => AssemblyName(md),
            _ => null,
        };
    }

    private static string AssemblyName(MetadataReader md, AssemblyReferenceHandle handle) =>
        md.GetString(md.GetAssemblyReference(handle).Name);

    private static string ExportedName(MetadataReader md, ExportedTypeHandle handle)
    {
        var exported = md.GetExportedType(handle);
        var simple = md.GetString(exported.Name);
        if (exported.Implementation.Kind == HandleKind.ExportedType)
            return ExportedName(md, (ExportedTypeHandle)exported.Implementation) + "+" + simple;
        var ns = md.GetString(exported.Namespace);
        return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
    }

    private static string? ExportedAssemblyName(MetadataReader md, ExportedTypeHandle handle)
    {
        var implementation = md.GetExportedType(handle).Implementation;
        return implementation.Kind switch
        {
            HandleKind.AssemblyReference => AssemblyName(md, (AssemblyReferenceHandle)implementation),
            HandleKind.ExportedType => ExportedAssemblyName(md, (ExportedTypeHandle)implementation),
            _ => null,
        };
    }

    private static string Key(string assemblyName, string metadataName) =>
        assemblyName + "\0" + metadataName;
}
