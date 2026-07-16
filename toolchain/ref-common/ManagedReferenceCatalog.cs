using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.PortableExecutable;

namespace DotKt.Toolchain;

/// <summary>
/// The already-resolved assembly set handed to a tool.  This deliberately models files, not search
/// directories: MSBuild/RAR (or scripts/lib.sh for direct runs) has already selected the references.
/// </summary>
sealed class ManagedReferenceCatalog
{
    readonly Dictionary<string, Entry> _bySimpleName = new(StringComparer.OrdinalIgnoreCase);

    public sealed record Entry(string Path, AssemblyName Identity);

    public IReadOnlyList<Entry> Entries { get; }
    public IReadOnlyList<string> Paths { get; }

    ManagedReferenceCatalog(List<Entry> entries)
    {
        Entries = entries;
        Paths = entries.Select(e => e.Path).ToArray();
        foreach (var entry in entries) _bySimpleName.Add(entry.Identity.Name!, entry);
    }

    public static ManagedReferenceCatalog Create(IEnumerable<string> paths, string toolName)
    {
        var entries = new List<Entry>();
        var seenPaths = new HashSet<string>(PathComparer);
        var byName = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var full = Path.GetFullPath(raw.Trim());
            if (!seenPaths.Add(full)) continue;
            if (!File.Exists(full)) throw new ArgumentException($"{toolName}: reference not found: {full}");
            if (!IsManagedAssembly(full))
            {
                Console.Error.WriteLine($"{toolName}: skipping non-managed reference {Path.GetFileName(full)}");
                continue;
            }

            var identity = AssemblyName.GetAssemblyName(full);
            if (string.IsNullOrEmpty(identity.Name))
                throw new ArgumentException($"{toolName}: reference has no assembly name: {full}");
            // ReferenceCopyLocalPaths may contain satellite resource assemblies.  They are data for
            // ResourceManager, never candidates for metadata/type resolution, and the same simple name occurs once
            // per culture.  Diagnose and omit them here just like native DLLs instead of rebuilding MSBuild's asset
            // classification rules in every target.
            if (!string.IsNullOrEmpty(identity.CultureName))
            {
                Console.Error.WriteLine($"{toolName}: skipping satellite reference {Path.GetFileName(full)} ({identity.CultureName})");
                continue;
            }
            var entry = new Entry(full, identity);
            if (byName.TryGetValue(identity.Name, out var prior))
                throw new ArgumentException(
                    $"{toolName}: conflicting references with assembly name '{identity.Name}': " +
                    $"{prior.Path} and {full}");
            byName.Add(identity.Name, entry);
            entries.Add(entry);
        }
        return new ManagedReferenceCatalog(entries);
    }

    public bool TryGet(AssemblyName requested, out Entry entry)
    {
        entry = null;
        return requested != null && requested.Name != null && _bySimpleName.TryGetValue(requested.Name, out entry);
    }

    public MetadataLoadContext CreateMetadataLoadContext()
    {
        if (Entries.Count == 0)
            throw new InvalidOperationException("the compile reference set is empty");
        var core = new[] { "System.Runtime", "System.Private.CoreLib", "mscorlib", "netstandard" }
            .FirstOrDefault(n => _bySimpleName.ContainsKey(n));
        if (core == null)
            throw new InvalidOperationException(
                "the compile reference set has no core assembly (expected System.Runtime or System.Private.CoreLib)");
        return new MetadataLoadContext(new ExactPathAssemblyResolver(this), core);
    }

    public static string[] Split(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static bool IsManagedAssembly(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            return pe.HasMetadata && pe.PEHeaders.CorHeader != null;
        }
        catch { return false; }
    }

    static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    sealed class ExactPathAssemblyResolver : MetadataAssemblyResolver
    {
        readonly ManagedReferenceCatalog _catalog;
        public ExactPathAssemblyResolver(ManagedReferenceCatalog catalog) => _catalog = catalog;

        public override Assembly Resolve(MetadataLoadContext context, AssemblyName assemblyName) =>
            _catalog.TryGet(assemblyName, out var entry)
                ? context.LoadFromAssemblyPath(entry.Path)
                : null;
    }
}
