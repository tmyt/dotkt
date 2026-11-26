using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DotKt.Toolchain;

// Owns the lazy reflection resources and type cache for ONE fixed compile set.
// Returned Assembly/Type handles are borrowed until Dispose. No source graph,
// local declaration precedence, metadata binding policy or pipeline backreference
// belongs here. The caller applies changing local-owner policy before querying.
sealed partial class ReferenceMetadataUniverse : IDisposable
{
    readonly ManagedReferenceCatalog catalog;
    readonly Dictionary<(string Name, int Arity), Type> types = new();
    readonly Dictionary<Module, (PEReader Image, MetadataReader Reader)> metadataImages = new();
    MetadataLoadContext referencesContext;
    IReadOnlyList<Assembly> references = Array.Empty<Assembly>();
    bool referencesInitialized;
    MetadataLoadContext physicalContext;
    Assembly physicalStdlib;
    bool physicalInitialized;
    bool disposed;

    internal ReferenceMetadataUniverse(ManagedReferenceCatalog catalog) =>
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    internal MetadataReader ModuleMetadata(Module module)
    {
        RequireActive();
        if (metadataImages.TryGetValue(module, out var cached)) return cached.Reader;
        using var file = File.OpenRead(module.FullyQualifiedName);
        var image = new PEReader(file, PEStreamOptions.PrefetchMetadata);
        try
        {
            var reader = image.GetMetadataReader();
            metadataImages.Add(module, (image, reader));
            return reader;
        }
        catch { image.Dispose(); throw; }
    }

    internal IReadOnlyList<Assembly> Assemblies
    {
        get { EnsureReferences(); return references; }
    }

    internal Type ResolveType(string name, int arity, bool throwOnAmbiguity = true)
    {
        RequireActive();
        var key = (name, arity);
        if (types.TryGetValue(key, out var cached)) return cached;
        EnsureReferences();
        Type found = null;
        foreach (var candidate in TypeCandidates(name, arity))
        {
            var matches = new Dictionary<string, Type>(StringComparer.Ordinal);
            foreach (var assembly in references)
            {
                var match = TryType(assembly, candidate);
                if (match != null)
                    // Forwarders collapse to one defining assembly; distinct
                    // definitions never become a first-reference winner.
                    matches.TryAdd(match.Assembly.GetName().FullName ?? match.Assembly.GetName().Name!, match);
            }
            if (matches.Count > 1)
            {
                if (!throwOnAmbiguity) return null; // Optional proofs must not poison the exact lookup's cache.
                throw new InvalidOperationException(
                    $"bir2cir: type '{candidate}' is defined by multiple compile references: " +
                    string.Join(", ", matches.Keys.OrderBy(value => value, StringComparer.Ordinal)));
            }
            found = matches.Values.SingleOrDefault();
            if (found != null) break;
        }
        types[key] = found;
        return found;
    }

    // Existing best-effort hierarchy readers ask for the first declaration,
    // unlike exact binding's ambiguity-checked query above. Keep that policy
    // explicit rather than making it an incidental cache result.
    internal Type FirstType(string name, int arity)
    {
        EnsureReferences();
        foreach (var candidate in TypeCandidates(name, arity))
        foreach (var assembly in references)
            if (TryType(assembly, candidate) is { } found) return found;
        return null;
    }

    internal Type ResolveTypeIn(string name, string assemblySimpleName)
    {
        EnsureReferences();
        Type found = null;
        foreach (var assembly in references)
        {
            if (!string.Equals(assembly.GetName().Name, assemblySimpleName, StringComparison.OrdinalIgnoreCase)) continue;
            var match = TryType(assembly, name);
            if (match == null) continue;
            if (found != null && found != match)
                throw new InvalidOperationException(
                    $"bir2cir: type '{name}' is defined by more than one reference named '{assemblySimpleName}'");
            found = match;
        }
        return found;
    }

    internal Type CoreType(string name)
    {
        EnsureReferences();
        return referencesContext?.CoreAssembly is { } core ? TryType(core, name) : null;
    }

    internal bool SameDefinition(string name, string left, string right)
    {
        EnsureReferences();
        Type Bound(string scope)
        {
            if (scope.StartsWith("module:", StringComparison.Ordinal)) return null;
            var requested = new AssemblyName(scope);
            if (!catalog.TryGet(requested, out var entry)) return null;
            // The compile set selects the version. A simple-name hit alone must not
            // impersonate the requested public key, culture or content type.
            requested.Version = entry.Identity.Version;
            return string.Equals(requested.FullName, entry.Identity.FullName, StringComparison.OrdinalIgnoreCase)
                ? TryType(referencesContext.LoadFromAssemblyPath(entry.Path), name) : null;
        }
        return Bound(left) is { } type && type == Bound(right);
    }

    internal Assembly PhysicalStdlib
    {
        get
        {
            RequireActive();
            if (physicalInitialized) return physicalStdlib;
            physicalInitialized = true;
            var path = catalog.PhysicalStdlibPath;
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                physicalContext = catalog.CreatePhysicalStdlibMetadataLoadContext();
                physicalStdlib = physicalContext?.LoadFromAssemblyPath(path);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"bir2cir: warning: could not read the shipped stdlib twin {path} — {error.GetType().Name}: {error.Message}");
                Release(physicalContext);
                physicalContext = null;
            }
            return physicalStdlib;
        }
    }

    void EnsureReferences()
    {
        RequireActive();
        if (referencesInitialized) return;
        referencesInitialized = true;
        try
        {
            referencesContext = catalog.CreateMetadataLoadContext();
            var loaded = new List<Assembly>();
            foreach (var entry in catalog.Entries)
            {
                try { loaded.Add(referencesContext.LoadFromAssemblyPath(entry.Path)); }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"bir2cir: warning: could not load reference into the metadata context: {entry.Path} — {error.GetType().Name}: {error.Message}");
                }
            }
            references = loaded.AsReadOnly();
        }
        catch
        {
            Release(referencesContext);
            referencesContext = null;
        }
    }

    static IEnumerable<string> TypeCandidates(string name, int arity)
    {
        // Preserve the current query policy: supplied arity first, plain name,
        // then the existing bounded generic-declaration probe range.
        if (arity > 0) yield return name + "`" + arity;
        yield return name;
        for (var candidate = 1; candidate <= 8; candidate++)
            if (candidate != arity) yield return name + "`" + candidate;
    }

    static Type TryType(Assembly assembly, string name)
    {
        try { return assembly.GetType(name, throwOnError: false); }
        catch { return null; }
    }

    void RequireActive() => ObjectDisposedException.ThrowIf(disposed, this);

    static void Release(MetadataLoadContext context)
    {
        try { context?.Dispose(); }
        catch { } // Cleanup must not mask the compilation failure being unwound.
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var cached in metadataImages.Values) cached.Image.Dispose();
        metadataImages.Clear();
        types.Clear(); references = Array.Empty<Assembly>(); physicalStdlib = null;
        Release(physicalContext); physicalContext = null;
        Release(referencesContext); referencesContext = null;
    }
}
