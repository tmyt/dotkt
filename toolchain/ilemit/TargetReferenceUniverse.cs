using System.Reflection;
using DotKt.Toolchain;

// The exact target metadata universe selected by the build.  This is deliberately separate from
// RuntimeReferences: the latter loads implementation assemblies so current Reflection.Emit paths can execute,
// whereas this context contains the contract/reference assemblies whose identities must eventually be written to
// output metadata.  #335 establishes and validates the boundary without changing emission; #336 switches every
// emitted type/member to this universe atomically.
sealed class TargetReferenceUniverse : IDisposable
{
    readonly MetadataLoadContext _context;
    readonly Assembly[] _assemblies;
    readonly HashSet<Assembly> _assemblySet;
    readonly Dictionary<string, Type> _types = new(StringComparer.Ordinal);

    public Assembly CoreAssembly => _context.CoreAssembly;
    public IReadOnlyList<Assembly> Assemblies => _assemblies;
    public IReadOnlyList<string> ReferencePaths { get; }

    public TargetReferenceUniverse(IEnumerable<string> paths)
    {
        var catalog = ManagedReferenceCatalog.Create(paths, "ilemit target");
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
            throw new InvalidOperationException(
                $"target type '{fullName}' is defined by multiple compile references: " +
                string.Join(", ", matches.Select(t => t.Assembly.GetName().FullName)
                    .OrderBy(x => x, StringComparer.Ordinal)));
        _types[fullName] = matches[0];
        return matches[0];
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
