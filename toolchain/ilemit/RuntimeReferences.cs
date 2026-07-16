using System.Reflection;
using System.Runtime.Loader;
using DotKt.Toolchain;

static class RuntimeReferences
{
    static ExactRuntimeLoadContext _context;
    public static IReadOnlyList<Assembly> Assemblies { get; private set; } = Array.Empty<Assembly>();

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

    public static void Load(IEnumerable<string> paths)
    {
        var catalog = ManagedReferenceCatalog.Create(paths, "ilemit");
        _context = new ExactRuntimeLoadContext(catalog);
        Assemblies = _context.LoadReferences();
    }

    sealed class ExactRuntimeLoadContext : AssemblyLoadContext
    {
        readonly ManagedReferenceCatalog _catalog;
        readonly HashSet<string> _platformNames;

        public ExactRuntimeLoadContext(ManagedReferenceCatalog catalog) : base("dotkt-ilemit-runtime-refs")
        {
            _catalog = catalog;
            _platformNames = TrustedPlatformAssemblies()
                .Select(Path.GetFileNameWithoutExtension)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<Assembly> LoadReferences()
        {
            var loaded = new List<Assembly>();
            foreach (var entry in _catalog.Entries)
            {
                if (_platformNames.Contains(entry.Identity.Name!))
                {
                    loaded.Add(AssemblyLoadContext.Default.LoadFromAssemblyName(entry.Identity));
                    continue;
                }
                loaded.Add(LoadFromAssemblyPath(entry.Path));
            }
            return loaded;
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            if (_platformNames.Contains(assemblyName.Name ?? ""))
                return AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName);
            if (_catalog.TryGet(assemblyName, out var entry))
                return LoadFromAssemblyPath(entry.Path);
            throw new FileNotFoundException(
                $"ilemit: runtime dependency '{assemblyName.FullName}' is absent from --runtime-refs");
        }

        static IEnumerable<string> TrustedPlatformAssemblies() =>
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
    }
}
