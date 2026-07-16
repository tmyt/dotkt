using System.Reflection;
using System.Runtime.Loader;
using DotKt.Toolchain;

// The app's resolved runtime reference set (from @(ReferenceCopyLocalPaths)) plus a fallback onto ilemit's own
// host framework.  Precedence is CATALOG-FIRST, TPA-FALLBACK:
//   * the app catalog is AUTHORITATIVE — a copy-local assembly wins even when its simple name is also one of
//     ilemit's Trusted-Platform-Assemblies (so an app that pins a different version emits against ITS version), and
//   * ilemit's host framework (TPA) is only a FALLBACK for framework/inbox types the app does NOT copy-local
//     (System.Text.Json, System.Net.Http, …) — those are absent from the catalog yet must still resolve.
static class RuntimeReferences
{
    static ExactRuntimeLoadContext _context;
    public static IReadOnlyList<Assembly> Assemblies { get; private set; } = Array.Empty<Assembly>();

    // Simple names of ilemit's own Trusted Platform Assemblies (the host framework), used for the fallback probe.
    static readonly string[] _hostFrameworkNames = TrustedPlatformAssemblies()
        .Select(Path.GetFileNameWithoutExtension)
        .Where(n => !string.IsNullOrEmpty(n))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray()!;

    static readonly HashSet<string> _hostFrameworkSet = new(_hostFrameworkNames, StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, Type> _hostTypeCache = new();

    // Probe ONLY the app's resolved runtime set (the authoritative catalog). Returns null when the catalog does not
    // define the type — the caller then falls back to ilemit's host framework via ResolveFromHostFramework.
    public static Type ResolveType(string fullName)
    {
        var matches = new List<Type>();
        foreach (var assembly in Assemblies)
        {
            Type type;
            try { type = assembly.GetType(fullName, throwOnError: false); }
            catch { continue; }
            if (type != null && matches.All(t => t.Assembly != type.Assembly)) matches.Add(type);
        }
        if (matches.Count > 1)
            throw new InvalidOperationException(
                $"ilemit: type '{fullName}' is defined by multiple runtime references: " +
                string.Join(", ", matches.Select(t => t.Assembly.GetName().FullName).OrderBy(x => x, StringComparer.Ordinal)));
        return matches.SingleOrDefault();
    }

    // Fallback for a framework/inbox type the app does NOT copy-local: resolve it from ilemit's OWN host framework
    // (TPA).  Try the assemblies whose simple name is a namespace-prefix of the type first (most specific first) so
    // the common case — System.Text.Json.JsonSerializer -> System.Text.Json — is one probe, then scan the rest as a
    // safety net.  Assembly-qualified Type.GetType loads into the Default ALC = ilemit's host framework.
    public static Type ResolveFromHostFramework(string fullName)
    {
        if (_hostTypeCache.TryGetValue(fullName, out var cached)) return cached;
        Type Probe(string simpleName)
        {
            try { return Type.GetType(fullName + ", " + simpleName, throwOnError: false); }
            catch { return null; }
        }
        Type found = null;
        foreach (var name in _hostFrameworkNames
                     .Where(n => fullName.StartsWith(n + ".", StringComparison.Ordinal))
                     .OrderByDescending(n => n.Length))
            if ((found = Probe(name)) != null) break;
        if (found == null)
            foreach (var name in _hostFrameworkNames)
                if ((found = Probe(name)) != null) break;
        _hostTypeCache[fullName] = found;
        return found;
    }

    public static void Load(IEnumerable<string> paths)
    {
        // runtimeSelection: true — the runtime set legitimately carries lib + runtimes/<rid>/lib RID builds of one
        // identity; the catalog dedups them by identity and picks the host-RID asset (see ManagedReferenceCatalog).
        var catalog = ManagedReferenceCatalog.Create(paths, "ilemit", runtimeSelection: true);
        _context = new ExactRuntimeLoadContext(catalog);
        Assemblies = _context.LoadReferences();
    }

    static IEnumerable<string> TrustedPlatformAssemblies() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

    // The core identity assemblies that MUST come from the Default ALC: System.Private.CoreLib literally cannot be
    // loaded into a non-default ALC, and mscorlib/netstandard are type-forwarder facades whose identity must unify
    // with the Default CoreLib so the Emitter's typeof()-based checks stay sound.  A self-contained publish puts
    // these in the copy-local set (@(RuntimePackAsset)); framework-dependent builds never do — so this is inert
    // today and simply keeps path-loading from hard-failing on a runtime-pack CoreLib.
    static readonly HashSet<string> _coreIdentityNames = new(StringComparer.OrdinalIgnoreCase)
        { "System.Private.CoreLib", "mscorlib", "netstandard" };

    sealed class ExactRuntimeLoadContext : AssemblyLoadContext
    {
        readonly ManagedReferenceCatalog _catalog;

        public ExactRuntimeLoadContext(ManagedReferenceCatalog catalog) : base("dotkt-ilemit-runtime-refs")
            => _catalog = catalog;

        // The catalog is authoritative: load every entry from ITS path — even a simple name that is also in ilemit's
        // TPA — so an app that copy-locals a different version emits against the app's version, not ilemit's.  The
        // sole exception is the core identity trio, which must come from the Default ALC (see _coreIdentityNames).
        public IReadOnlyList<Assembly> LoadReferences() =>
            _catalog.Entries.Select(e => _coreIdentityNames.Contains(e.Identity.Name!)
                ? AssemblyLoadContext.Default.LoadFromAssemblyName(e.Identity)
                : LoadFromAssemblyPath(e.Path)).ToArray();

        // Dependency resolution during type derivation: catalog first, then ilemit's host framework (TPA) for a
        // framework dependency the app does not copy-local (e.g. an app assembly that references System.Text.Json).
        protected override Assembly Load(AssemblyName assemblyName)
        {
            // Core identity always from Default (it must unify with ilemit's own CoreLib), regardless of catalog.
            if (_coreIdentityNames.Contains(assemblyName.Name ?? ""))
                return AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName);
            // Catalog is authoritative — the app's copy-local version wins over ilemit's TPA.
            if (_catalog.TryGet(assemblyName, out var entry))
                return LoadFromAssemblyPath(entry.Path);
            // A framework/inbox dependency the app does not copy-local: fall back to ilemit's host framework.
            if (_hostFrameworkSet.Contains(assemblyName.Name ?? ""))
                return AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName);
            throw new FileNotFoundException(
                $"ilemit: runtime dependency '{assemblyName.FullName}' is absent from --runtime-refs and ilemit's host framework");
        }
    }
}
