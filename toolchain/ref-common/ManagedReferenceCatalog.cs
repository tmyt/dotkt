using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace DotKt.Toolchain;

/// <summary>
/// The already-resolved assembly set handed to a tool.  This deliberately models files, not search
/// directories: MSBuild/RAR (or scripts/lib.sh for direct runs) has already selected the references.
/// </summary>
sealed class ManagedReferenceCatalog
{
    readonly Dictionary<string, Entry> _bySimpleName = new(StringComparer.OrdinalIgnoreCase);

    // The ref/runtime stdlib split (docs/design-clr-stdlib-ref-runtime-split.md): the REFERENCE stdlib
    // (metadata twin, kept by ref-READERS bir2cir+facadegen) and the RUNTIME stdlib (shipped, loaded by
    // ilemit) define the SAME `kotlin.clr.*` type shapes; only the assembly name differs.
    const string RefStdlibName = "DotKt.Private.Stdlib";
    const string RuntimeStdlibName = "DotKt.Stdlib";

    public sealed record Entry(string Path, AssemblyName Identity);

    public IReadOnlyList<Entry> Entries { get; }
    public IReadOnlyList<string> Paths { get; }

    ManagedReferenceCatalog(List<Entry> entries, bool refStdlibAliasesRuntime)
    {
        Entries = entries;
        Paths = entries.Select(e => e.Path).ToArray();
        foreach (var entry in entries) _bySimpleName.Add(entry.Identity.Name!, entry);
        // Ref-READER alias: a consumed cross-module DotKt library references the RUNTIME stdlib (`DotKt.Stdlib`) in
        // its `[kotlin.clr.*]` round-trip metadata (and via any `kotlin.*` member type). A ref-reader carries only
        // the REFERENCE twin `DotKt.Private.Stdlib` (the runtime twin, if it was on the set, was dropped in Create —
        // see the twin-collapse below), so resolve a `DotKt.Stdlib` reference to it. A single, documented,
        // stdlib-specific mapping, NOT a fuzzy name match. ilemit does NOT set this flag: it loads the real runtime.
        if (refStdlibAliasesRuntime
            && _bySimpleName.TryGetValue(RefStdlibName, out var refStdlib)
            && !_bySimpleName.ContainsKey(RuntimeStdlibName))
            _bySimpleName.Add(RuntimeStdlibName, refStdlib);
    }

    /// <summary>
    /// Build a catalog from an already-resolved reference set.
    /// <para>
    /// <paramref name="runtimeSelection"/> distinguishes the two consumer classes:
    /// </para>
    /// <list type="bullet">
    /// <item><b>false (compile set, from <c>@(ReferencePath)</c>)</b> — bir2cir/facadegen/retarget. One
    /// entry per identity, no RID variants; a repeated simple name is a genuine conflict → throw. Strict.</item>
    /// <item><b>true (runtime set, from <c>@(ReferenceCopyLocalPaths)</c>)</b> — ilemit. Copy-local
    /// legitimately carries BOTH <c>lib/&lt;tfm&gt;/Foo.dll</c> and <c>runtimes/&lt;rid&gt;/lib/&lt;tfm&gt;/Foo.dll</c>
    /// for one identity (a RID-impl package: the <c>lib</c> asset is a PNSE placeholder, the real code is under
    /// <c>runtimes</c>). Dedup by FULL identity and select the asset the runtime host would load (host-RID chain,
    /// else the RID-neutral <c>lib</c> asset). Throw ONLY on same simple name + CONFLICTING identity — that
    /// preserves the duplicate-identity detection and the shared-dependency dedup win.</item>
    /// </list>
    /// <para><paramref name="refStdlibAliasesRuntime"/> (ref-readers bir2cir/facadegen ONLY): a consumed DotKt
    /// library is emitted by ilemit against the RUNTIME stdlib (<c>DotKt.Stdlib</c>), so its members / round-trip
    /// <c>[kotlin.clr.*]</c> attributes reference <c>kotlin.*</c> types scoped to that assembly. A ref-reader holds
    /// the REFERENCE twin (<c>DotKt.Private.Stdlib</c>), the correct pure-Kotlin-shape metadata source. When BOTH
    /// twins land on the set (copy-local), this DROPS the runtime twin (so <c>kotlin.*</c> resolves to one assembly,
    /// not two — see the twin-collapse in <see cref="Create"/>) and the constructor aliases a <c>DotKt.Stdlib</c>
    /// request to the ref twin — keeping the ref-reader ref.dll-only. NEVER set for ilemit (it loads the real
    /// runtime stdlib).</para>
    /// </summary>
    public static ManagedReferenceCatalog Create(IEnumerable<string> paths, string toolName,
        bool runtimeSelection = false, bool refStdlibAliasesRuntime = false)
    {
        // Phase 1: normalise the raw paths to (path, identity) candidates, dropping duplicates / non-managed /
        // satellite assemblies.  Identity-level conflict handling differs between the two sets, so it happens
        // in phase 2 where we can group by simple name.
        var candidates = new List<Entry>();
        var seenPaths = new HashSet<string>(PathComparer);
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
            candidates.Add(new Entry(full, identity));
        }

        // Phase 2: resolve to at most one entry per simple name, preserving first-encounter order.
        var entries = new List<Entry>();
        var order = new List<string>();
        var groups = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            if (!groups.TryGetValue(c.Identity.Name!, out var list))
            {
                groups[c.Identity.Name!] = list = new List<Entry>();
                order.Add(c.Identity.Name!);
            }
            list.Add(c);
        }
        foreach (var name in order)
        {
            var list = groups[name];
            if (list.Count == 1) { entries.Add(list[0]); continue; }
            // Multiple physical files share a simple name.  In the COMPILE set that is always a conflict
            // (@(ReferencePath) has one entry per identity).  In the RUNTIME set it is legal iff every file has
            // the SAME full identity (a RID-impl package's lib + runtimes/<rid>/lib builds); differing identities
            // are still a conflict (preserves #35's duplicate-identity detection + the shared-dependency dedup win).
            var distinctIdentities = list
                .Select(e => e.Identity.FullName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!runtimeSelection || distinctIdentities.Count > 1)
                throw new ArgumentException(
                    $"{toolName}: conflicting references with assembly name '{name}': " +
                    string.Join(" and ", list.Select(e => e.Path)));
            entries.Add(SelectRuntimeAsset(name, list, toolName));
        }

        // Ref-READER twin collapse (#73): a consumed cross-module DotKt library is emitted by ilemit against the
        // RUNTIME stdlib, so a copy-local build puts BOTH stdlib twins on a ref-reader's compile set — the REFERENCE
        // twin `DotKt.Private.Stdlib` (which bir2cir/facadegen are meant to read) AND the RUNTIME twin `DotKt.Stdlib`.
        // The runtime twin is the SUBSTITUTE build (its @Clr-bound types are dropped/BCL-substituted and its
        // `[Kotlin*]`/`[Clr]` metadata stripped — docs/design-clr-stdlib-ref-runtime-split.md), so for a ref-reader it
        // is not just redundant but an actively WRONG metadata source. Worse, loading BOTH into one MetadataLoadContext
        // makes every `kotlin.*` type (e.g. `kotlin.reflect.KProperty`, `kotlin.concurrent.atomics.AtomicReference`)
        // resolve to TWO defining assemblies, so a ref-reader's use-site duplicate-definition check throws and a
        // consumed type whose members reference `kotlin.*` is silently skipped (the atomicfu AtomicInt/Long/Boolean/Ref
        // regression). So DROP the runtime twin here (the constructor's alias then resolves a `DotKt.Stdlib` reference
        // to the ref twin) — realizing the invariant "ref-readers function with the REFERENCE stdlib ALONE". Done AFTER
        // phase 2 so a genuine same-name conflict (two physical `DotKt.Stdlib` files) still throws first. ilemit never
        // sets the flag; its runtime set carries only `DotKt.Stdlib` (the ref twin is compile-only) so this is inert.
        if (refStdlibAliasesRuntime
            && entries.Any(e => string.Equals(e.Identity.Name, RefStdlibName, StringComparison.OrdinalIgnoreCase)))
        {
            var runtimeTwin = entries.FirstOrDefault(
                e => string.Equals(e.Identity.Name, RuntimeStdlibName, StringComparison.OrdinalIgnoreCase));
            if (runtimeTwin != null)
            {
                Console.Error.WriteLine(
                    $"{toolName}: ref-reader collapse — dropping runtime stdlib twin {RuntimeStdlibName} " +
                    $"({runtimeTwin.Path}); a {RuntimeStdlibName} reference resolves to the reference twin {RefStdlibName}");
                entries.Remove(runtimeTwin);
            }
        }
        return new ManagedReferenceCatalog(entries, refStdlibAliasesRuntime);
    }

    // Given several physical files that all share ONE full identity (the lib + runtimes/<rid>/lib builds of a
    // RID-impl package), pick the one the runtime host would actually load.  Priority = the host RID's ordered
    // fallback chain (exact > os > unix-family > any > base), then the RID-neutral `lib` asset as a last resort.
    // keep-first is wrong: for RID-impl packages the `lib` asset is a PlatformNotSupported placeholder.
    static Entry SelectRuntimeAsset(string name, List<Entry> candidates, string toolName)
    {
        var chain = RidFallbackChain();
        // rank: lower is better.  A RID-neutral lib asset (no runtimes/<rid>/lib segment) ranks after the whole
        // chain; a RID asset outside the chain (incompatible with the host) is excluded.
        int Rank(Entry e)
        {
            var rid = ExtractRid(e.Path);
            if (rid == null) return chain.Count;                       // RID-neutral `lib` asset
            var idx = chain.FindIndex(r => string.Equals(r, rid, StringComparison.OrdinalIgnoreCase));
            return idx < 0 ? int.MaxValue : idx;                       // incompatible RID -> excluded
        }
        var ranked = candidates
            .Select(e => (entry: e, rank: Rank(e)))
            .Where(x => x.rank != int.MaxValue)
            .OrderBy(x => x.rank)
            .ToList();
        if (ranked.Count > 0)
        {
            var chosen = ranked[0].entry;
            var rid = ExtractRid(chosen.Path);
            Console.Error.WriteLine(
                $"{toolName}: '{name}' has {candidates.Count} RID builds; selected {(rid == null ? "RID-neutral lib" : "runtimes/" + rid + "/lib")} asset for host {RuntimeInformation.RuntimeIdentifier}");
            return chosen;
        }
        // No RID-compatible asset and no neutral lib.  This should not happen for a host-appropriate copy-local set
        // (RAR only copies compatible RID assets), and a hand-written family chain does not model specialised RIDs
        // (e.g. linux-musl-x64).  Fall back to the first candidate rather than hard-fail, and say so.
        Console.Error.WriteLine(
            $"{toolName}: WARNING '{name}' has no RID-compatible asset for host {RuntimeInformation.RuntimeIdentifier} " +
            $"(candidates: {string.Join(", ", candidates.Select(e => ExtractRid(e.Path) ?? "lib"))}); using {candidates[0].Path}");
        return candidates[0];
    }

    // The RID segment of a `.../runtimes/<rid>/lib/<tfm>/Foo.dll` copy-local path, or null for a RID-neutral
    // `.../lib/<tfm>/Foo.dll` asset.  Pure path shape — no denylist.
    static string ExtractRid(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i + 2 < parts.Length; i++)
            if (parts[i].Equals("runtimes", StringComparison.OrdinalIgnoreCase)
                && parts[i + 2].Equals("lib", StringComparison.OrdinalIgnoreCase))
                return parts[i + 1];
        return null;
    }

    // The host RID's ordered RID fallback chain (the transitive #import closure of the portable RID graph, most
    // specific first).  The BCL exposes neither the chain nor a compatibility predicate, so reproduce the portable
    // families directly: linux-<arch> > linux > unix-<arch> > unix > any > base (osx analogous; win/browser have no
    // unix tier).  Specialised RIDs (linux-musl-*) fall back through their os tier only — good enough for managed
    // RID assets, which authors key on os-level RIDs (win/unix/linux/osx).
    static List<string> RidFallbackChain()
    {
        var host = RuntimeInformation.RuntimeIdentifier;              // e.g. "linux-x64"
        var segs = host.Split('-');
        var os = segs[0];
        var arch = segs.Length > 1 ? segs[^1] : null;
        var chain = new List<string>();
        void Add(string r) { if (!chain.Any(x => string.Equals(x, r, StringComparison.OrdinalIgnoreCase))) chain.Add(r); }
        Add(host);
        // A multi-part RID (e.g. linux-musl-x64) inherits both its qualifier-stripped (linux-musl) and its
        // os-arch (linux-x64) tiers before the bare os — so a package that ships only runtimes/linux-x64 still
        // matches on a musl host, instead of falling through to keep-first (the PNSE lib placeholder).
        if (segs.Length > 2)
        {
            Add(string.Join('-', segs[..^1]));
            if (arch != null) Add(os + "-" + arch);
        }
        Add(os);
        foreach (var fam in os.ToLowerInvariant() switch
                 {
                     "linux" or "osx" or "freebsd" or "illumos" or "solaris" => new[] { "unix" },
                     _ => Array.Empty<string>(),
                 })
        {
            if (arch != null) Add(fam + "-" + arch);
            Add(fam);
        }
        Add("any");
        Add("base");
        return chain;
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
