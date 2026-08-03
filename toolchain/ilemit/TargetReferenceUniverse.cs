using System.Reflection;
using DotKt.Toolchain;

// The exact target metadata universe selected by the build. Compile references are the sole source of available
// types and members. Runtime references only disambiguate multiple genuine compile-time definitions by naming the
// implementation assembly MSBuild selected for deployment; they never add a type to this universe.
sealed class TargetReferenceUniverse : IDisposable
{
    readonly MetadataLoadContext _context;
    readonly Assembly[] _assemblies;
    readonly HashSet<Assembly> _assemblySet;
    readonly HashSet<string> _runtimeAssemblyIdentities;
    readonly Dictionary<string, Type> _types = new(StringComparer.Ordinal);
    readonly Dictionary<(string type, string assembly), Type> _ownedTypes = new();

    public Assembly CoreAssembly => _context.CoreAssembly;
    public IReadOnlyList<Assembly> Assemblies => _assemblies;
    public IReadOnlyList<string> ReferencePaths { get; }

    public TargetReferenceUniverse(IEnumerable<string> paths, IEnumerable<string> runtimePaths = null,
        string targetRid = null, string ridGraphPath = null)
    {
        var catalog = ManagedReferenceCatalog.Create(paths, "ilemit target");
        var runtimeCatalog = ManagedReferenceCatalog.Create(runtimePaths, "ilemit runtime", runtimeSelection: true,
            targetRid: targetRid, ridGraphPath: ridGraphPath);
        _runtimeAssemblyIdentities = runtimeCatalog.Entries
            .Select(e => e.Identity.FullName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _context = catalog.CreateMetadataLoadContext();
        ReferencePaths = catalog.Paths.ToArray();
        _assemblies = catalog.Paths.Select(LoadExact).ToArray();
        _assemblySet = new HashSet<Assembly>(_assemblies);

        // MetadataLoadContext accepts a core assembly name before any type is requested. Force the fundamental
        // identity now so a malformed/incomplete target set fails at the CLI boundary, not halfway through emit.
        _ = CoreAssembly.GetType("System.Object", throwOnError: true, ignoreCase: false)
            ?? throw new InvalidOperationException("ilemit target: core assembly does not define System.Object");
    }

    Assembly LoadExact(string path)
    {
        try { return _context.LoadFromAssemblyPath(path); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"ilemit target: could not load compile reference '{path}' into the target metadata context: " +
                $"{ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    // Resolve a target identity by exact full name. A forwarded type is de-duplicated by its defining Assembly;
    // two genuine definers are a malformed target set and must never degrade to first-match behavior.
    public Type ResolveType(string fullName)
    {
        if (_types.TryGetValue(fullName, out var cached)) return cached;
        var matches = new List<Type>();
        foreach (var assembly in _assemblies)
        {
            Type type;
            try { type = assembly.GetType(fullName, throwOnError: false, ignoreCase: false); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"ilemit target: could not resolve type '{fullName}' from compile reference " +
                    $"'{assembly.GetName().FullName}': {ex.GetType().Name}: {ex.Message}", ex);
            }
            if (type != null && matches.All(candidate => candidate.Assembly != type.Assembly)) matches.Add(type);
        }
        if (matches.Count == 0)
        {
            // CIR writes source-style dotted nested names while CLR reflection uses '+'. Walk one separator at a
            // time, matching the existing runtime resolver without adding any assembly or TPA fallback.
            var dot = fullName.LastIndexOf('.');
            if (dot > 0)
            {
                try
                {
                    var type = ResolveType(fullName[..dot] + "+" + fullName[(dot + 1)..]);
                    _types[fullName] = type;
                    return type;
                }
                catch (NotSupportedException) { }
            }
            throw new NotSupportedException(
                $"target type '{fullName}' is absent from the exact compile reference set");
        }
        if (matches.Count > 1)
        {
            // A compile surface may intentionally contain a metadata-only contract twin and its copy-local runtime
            // implementation under distinct assembly identities. CIR carries the resolved CLR type FQN, not a
            // deployment choice. If exactly one definer is the implementation assembly selected by MSBuild, use it.
            // This is a disambiguator over compile definitions, never a runtime lookup/fallback.
            var implementations = matches
                .Where(t => _runtimeAssemblyIdentities.Contains(t.Assembly.GetName().FullName!))
                .ToList();
            if (implementations.Count == 1)
            {
                _types[fullName] = implementations[0];
                return implementations[0];
            }
            throw new InvalidOperationException(
                $"target type '{fullName}' is defined by multiple compile references: " +
                string.Join(", ", matches.Select(t => t.Assembly.GetName().FullName)
                    .OrderBy(x => x, StringComparer.Ordinal)));
        }
        _types[fullName] = matches[0];
        return matches[0];
    }

    // Resolve a CIR-carried physical owner exactly. This is required for declarations such as NullableAttribute:
    // third-party assemblies commonly embed private compiler lookalikes with the same FQN, so a name-only search is
    // intentionally ambiguous. The producer owns the assembly decision; ilemit merely links that stated identity.
    public Type ResolveType(string fullName, string assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
            throw new InvalidOperationException($"target type '{fullName}' carries an empty assembly owner");
        var key = (fullName, assemblyName);
        if (_ownedTypes.TryGetValue(key, out var cached)) return cached;
        var assemblies = _assemblies
            .Where(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (assemblies.Length != 1)
            throw new InvalidOperationException(
                $"target assembly owner '{assemblyName}' for type '{fullName}' matched {assemblies.Length} compile references");
        Type type;
        try { type = assemblies[0].GetType(fullName, throwOnError: false, ignoreCase: false); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"target assembly '{assemblyName}' could not resolve owned type '{fullName}': {ex.Message}", ex);
        }
        if (type == null)
            throw new InvalidOperationException(
                $"target assembly '{assemblyName}' does not define owned type '{fullName}'");
        _ownedTypes[key] = type;
        return type;
    }

    // Guard used by the #336 migration: metadata-only target Type objects and host-runtime Type objects are not
    // interchangeable even when FullName is identical. Keep the ownership test beside the resolver rather than
    // teaching individual emitter paths how MetadataLoadContext represents composite types.
    public bool Owns(Type type)
    {
        if (type == null) return false;
        while (type.HasElementType) type = type.GetElementType()!;
        if (type.IsGenericParameter)
            type = type.DeclaringType ?? type.DeclaringMethod?.DeclaringType;
        return type != null && _assemblySet.Contains(type.Assembly);
    }

    public void AssertOwns(Type type, string context)
    {
        if (!Owns(type))
            throw new InvalidOperationException(
                $"ilemit target-universe invariant: {context} received non-target type '{type}' " +
                $"from '{type?.Assembly.GetName().Name ?? "<none>"}'");
    }

    public void Dispose() => _context.Dispose();
}
