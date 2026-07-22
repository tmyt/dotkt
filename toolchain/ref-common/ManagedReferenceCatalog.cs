using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DotKt.Toolchain;

/// <summary>
/// The already-resolved assembly set handed to a tool.  This deliberately models files, not search
/// directories: MSBuild/RAR (or scripts/lib.sh for direct runs) has already selected the references.
/// </summary>
sealed class ManagedReferenceCatalog
{
    readonly Dictionary<string, Entry> _bySimpleName = new(StringComparer.OrdinalIgnoreCase);

    // The ref/runtime stdlib split (docs/architecture.md): the REFERENCE stdlib
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
    /// <para><paramref name="targetRid"/> / <paramref name="ridGraphPath"/> (runtime set only, #51): the RID the app
    /// is being compiled FOR, and the .NET/NuGet portable RID fallback graph used to rank RID-impl assets. When
    /// <paramref name="targetRid"/> is null OR EMPTY the HOST RID is assumed — correct for a host-targeted or
    /// direct-script run, and for the framework-dependent MSBuild case where <c>$(RuntimeIdentifier)</c> is empty — but
    /// a cross-target build (Linux host → <c>win-x64</c>) MUST pass its <c>$(RuntimeIdentifier)</c> or asset selection
    /// picks the host's asset (a RID-neutral PlatformNotSupported placeholder). <paramref name="ridGraphPath"/> is
    /// MSBuild's <c>$(RuntimeIdentifierGraphPath)</c> (the portable <c>PortableRuntimeIdentifierGraph.json</c> or, under
    /// <c>UseRidGraph</c>, the full <c>RuntimeIdentifierGraph.json</c> — same schema); when null/empty it is
    /// auto-discovered from the running SDK, and if no graph is found a minimal built-in family chain is the last
    /// resort. The fallback chain is the graph's transitive <c>#import</c> closure (most specific first), NOT a
    /// hand-rolled family table.</para>
    /// </summary>
    public static ManagedReferenceCatalog Create(IEnumerable<string> paths, string toolName,
        bool runtimeSelection = false, bool refStdlibAliasesRuntime = false,
        string targetRid = null, string ridGraphPath = null)
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
            // #52 — a load failure is CLASSIFIED, never swallowed as "native". A genuinely native PE (valid image, no
            // CLI/COR directory) is always a legitimate silent skip. A broken/truncated PE or an I/O error is FATAL for
            // the COMPILE set (facadegen/bir2cir/retarget consume `@(ReferencePath)` — pure managed reference assemblies,
            // so a corrupt one is a real error that must name the file + stage instead of silently dropping a type). The
            // RUNTIME copy-local set (ilemit, runtimeSelection) legitimately mixes managed + foreign-OS native assets
            // named `*.dll`, so there a non-loadable image is a loud WARN + skip, not a hard fail.
            switch (ClassifyReference(full, out var detail))
            {
                case RefLoadKind.Managed:
                    break;
                case RefLoadKind.Native:
                    Console.Error.WriteLine($"{toolName}: skipping non-managed reference {Path.GetFileName(full)} ({detail})");
                    continue;
                case RefLoadKind.Broken when runtimeSelection:
                    Console.Error.WriteLine($"{toolName}: skipping unreadable copy-local reference {Path.GetFileName(full)} ({detail})");
                    continue;
                case RefLoadKind.Broken:
                    throw new ArgumentException(
                        $"{toolName}: reference is not a readable managed assembly (corrupt/truncated PE): {full} — {detail}. " +
                        "A supplied reference that fails to load is fatal; it is NOT silently reclassified as non-managed.");
                case RefLoadKind.IoError when runtimeSelection:
                    Console.Error.WriteLine($"{toolName}: skipping unreadable copy-local reference {Path.GetFileName(full)} (I/O error: {detail})");
                    continue;
                default: // RefLoadKind.IoError
                    throw new ArgumentException(
                        $"{toolName}: reference could not be read (I/O error): {full} — {detail}.");
            }

            AssemblyName identity;
            try { identity = AssemblyName.GetAssemblyName(full); }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or FileLoadException)
            {
                // The PE opened + carried a COR header (else ClassifyReference would have diverted above), yet the
                // CLI metadata / assembly-identity blob is unreadable — a distinct, still-fatal corruption class.
                throw new ArgumentException(
                    $"{toolName}: reference has unreadable CLI metadata / assembly identity: {full} — {ex.Message}.");
            }
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

        // The TARGET RID (#51): the RID being COMPILED FOR. Defaults to the HOST RID when the caller passed none (a
        // host-targeted or direct-script run). Its ordered fallback chain is computed LAZILY — only a runtime set that
        // actually carries a RID-impl duplicate group reaches SelectRuntimeAsset. An EMPTY string counts as "none":
        // MSBuild hands the tool `--target-rid $(RuntimeIdentifier)` and $(RuntimeIdentifier) is empty for a
        // framework-dependent (no-RID) build, so an empty value degrades to the host RID rather than selecting on "".
        var effectiveRid = string.IsNullOrWhiteSpace(targetRid)
            ? RuntimeInformation.RuntimeIdentifier
            : targetRid.Trim();
        var ridChain = new Lazy<IReadOnlyList<string>>(() => RidFallbackChain(effectiveRid, ridGraphPath));

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
            entries.Add(SelectRuntimeAsset(name, list, toolName, effectiveRid, ridChain.Value));
        }

        // Ref-READER twin collapse (#73): a consumed cross-module DotKt library is emitted by ilemit against the
        // RUNTIME stdlib, so a copy-local build puts BOTH stdlib twins on a ref-reader's compile set — the REFERENCE
        // twin `DotKt.Private.Stdlib` (which bir2cir/facadegen are meant to read) AND the RUNTIME twin `DotKt.Stdlib`.
        // The runtime twin is the SUBSTITUTE build (its @Clr-bound types are dropped/BCL-substituted and its
        // `[Kotlin*]`/`[Clr]` metadata stripped — docs/architecture.md), so for a ref-reader it
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
    // RID-impl package), pick the one the TARGET runtime would actually load (#51).  Priority = the TARGET RID's
    // ordered fallback chain (the portable RID graph's transitive #import closure, most specific first), then the
    // RID-neutral `lib` asset as a last resort.  keep-first is wrong: for RID-impl packages the `lib` asset is a
    // PlatformNotSupported placeholder.  `targetRid`/`chain` are the RID being COMPILED FOR (host RID when the caller
    // passed none) — NOT the machine ilemit happens to run on, so a cross-target build selects the right asset.
    static Entry SelectRuntimeAsset(string name, List<Entry> candidates, string toolName,
        string targetRid, IReadOnlyList<string> chain)
    {
        // rank: lower is better.  A RID-neutral lib asset (no runtimes/<rid>/lib segment) ranks after the whole
        // chain; a RID asset outside the chain (incompatible with the target) is excluded.
        int Rank(Entry e)
        {
            var rid = ExtractRid(e.Path);
            if (rid == null) return chain.Count;                       // RID-neutral `lib` asset
            var idx = IndexOf(chain, rid);
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
                $"{toolName}: '{name}' has {candidates.Count} RID builds; selected {(rid == null ? "RID-neutral lib" : "runtimes/" + rid + "/lib")} asset for target {targetRid}");
            return chosen;
        }
        // No RID-compatible asset and no neutral lib.  This should not happen for a target-appropriate copy-local set
        // (RAR only copies compatible RID assets).  Fall back to the first candidate rather than hard-fail, and say so.
        Console.Error.WriteLine(
            $"{toolName}: WARNING '{name}' has no RID-compatible asset for target {targetRid} " +
            $"(candidates: {string.Join(", ", candidates.Select(e => ExtractRid(e.Path) ?? "lib"))}); using {candidates[0].Path}");
        return candidates[0];
    }

    static int IndexOf(IReadOnlyList<string> chain, string rid)
    {
        for (var i = 0; i < chain.Count; i++)
            if (string.Equals(chain[i], rid, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
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

    // The TARGET RID's ordered RID fallback chain (#51): the transitive #import closure of the .NET/NuGet portable
    // RID graph, most specific first — the SAME data (`PortableRuntimeIdentifierGraph.json`) and BFS expansion that
    // NuGet's RuntimeGraph.ExpandRuntime uses, so `runtimes/<rid>/lib` asset selection matches what the SDK's asset
    // resolution would pick for that target. When the graph can't be found we fall back to a minimal built-in family
    // chain (below) rather than hard-fail — enough for the os-level RIDs (win/unix/linux/osx) managed assets key on.
    // ridGraphPath is MSBuild's $(RuntimeIdentifierGraphPath); it may name EITHER the portable graph
    // (PortableRuntimeIdentifierGraph.json, the .NET 8+ default) or the full RuntimeIdentifierGraph.json (UseRidGraph),
    // which share the `{ runtimes: { <rid>: { #import: [...] } } }` schema LoadRidGraph parses — so both work. An empty
    // path (MSBuild passed an unset property) falls through to auto-discovery, NOT to the built-in chain.
    static IReadOnlyList<string> RidFallbackChain(string targetRid, string ridGraphPath)
    {
        var path = string.IsNullOrWhiteSpace(ridGraphPath) ? DiscoverRidGraphPath() : ridGraphPath.Trim();
        var graph = LoadRidGraph(path);
        return graph != null ? ExpandRuntime(graph, targetRid) : BuiltinRidChain(targetRid);
    }

    // BFS expansion over the portable RID graph, mirroring NuGet's RuntimeGraph.ExpandRuntime: the RID itself first,
    // then a breadth-first walk of its #import edges, de-duplicated — yielding the compatible RIDs in priority order.
    static IReadOnlyList<string> ExpandRuntime(IReadOnlyDictionary<string, string[]> graph, string targetRid)
    {
        var chain = new List<string> { targetRid };
        for (var i = 0; i < chain.Count; i++)
            if (graph.TryGetValue(chain[i], out var imports))
                foreach (var import in imports)
                    if (IndexOf(chain, import) < 0) chain.Add(import);
        return chain;
    }

    // Parse `PortableRuntimeIdentifierGraph.json` -> { rid : [imported rids] } (its `runtimes.<rid>.#import` array).
    // Returns null on any absence/parse failure so the caller degrades to the built-in chain.
    static IReadOnlyDictionary<string, string[]> LoadRidGraph(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("runtimes", out var runtimes)) return null;
            var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var rid in runtimes.EnumerateObject())
            {
                var imports = rid.Value.TryGetProperty("#import", out var imp) && imp.ValueKind == JsonValueKind.Array
                    ? imp.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrEmpty(s)).ToArray()!
                    : Array.Empty<string>();
                map[rid.Name] = imports;
            }
            return map;
        }
        catch { return null; }
    }

    // Auto-discover the running SDK's portable RID graph when MSBuild did not pass $(RuntimeIdentifierGraphPath).
    // The tools run under `dotnet`, so the SDK is on disk; probe DOTNET_ROOT, the host executable's dir, and the
    // well-known install roots for `sdk/<version>/PortableRuntimeIdentifierGraph.json` (highest version wins). The
    // graph sits one level under `sdk/`, so probe the immediate version dirs — no recursive SDK-tree walk.
    static string DiscoverRidGraphPath()
    {
        var roots = new List<string>();
        var dr = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dr)) roots.Add(dr);
        try
        {
            var host = Environment.ProcessPath;                        // the `dotnet` muxer, e.g. /usr/share/dotnet/dotnet
            var dir = host != null ? Path.GetDirectoryName(host) : null;
            if (!string.IsNullOrEmpty(dir)) roots.Add(dir);
        }
        catch { /* ProcessPath may throw on some hosts */ }
        roots.Add("/usr/share/dotnet");
        roots.Add("/usr/lib/dotnet");
        foreach (var root in roots)
        {
            var sdk = Path.Combine(root, "sdk");
            if (!Directory.Exists(sdk)) continue;
            string best = null;
            try
            {
                foreach (var versionDir in Directory.EnumerateDirectories(sdk))
                {
                    var f = Path.Combine(versionDir, "PortableRuntimeIdentifierGraph.json");
                    if (File.Exists(f) && (best == null || string.CompareOrdinal(f, best) > 0)) best = f;   // path sort ~ version order
                }
            }
            catch { /* unreadable sdk dir */ }
            if (best != null) return best;
        }
        return null;
    }

    // Last-resort chain when no portable RID graph is available: the portable families reproduced directly —
    // <rid> > qualifier-stripped > os-arch > os > unix[-arch] > any > base (osx/freebsd/illumos/solaris share the
    // unix tier; win/browser do not). Enough for the os-level RIDs (win/unix/linux/osx) managed RID assets key on.
    static IReadOnlyList<string> BuiltinRidChain(string targetRid)
    {
        var segs = targetRid.Split('-');
        var os = segs[0];
        var arch = segs.Length > 1 ? segs[^1] : null;
        var chain = new List<string>();
        void Add(string r) { if (IndexOf(chain, r) < 0) chain.Add(r); }
        Add(targetRid);
        // A multi-part RID (e.g. linux-musl-x64) inherits both its qualifier-stripped (linux-musl) and its
        // os-arch (linux-x64) tiers before the bare os — so a package that ships only runtimes/linux-x64 still
        // matches a musl target, instead of falling through to keep-first (the PNSE lib placeholder).
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

    // #52 — how a reference file's open+parse turned out. Only `Native` (a fully-parsed PE that simply carries no
    // CLI/COR data directory) is a legitimate silent skip; `Broken` (a PE-header parse FAILURE — corrupt/truncated
    // image, e.g. a partial write) and `IoError` (open/read failure — sharing violation, unreadable) are FATAL and
    // must name the offending file + the consumer stage rather than being bucketed as "non-managed".
    enum RefLoadKind { Managed, Native, Broken, IoError }

    // Classify a reference file structurally (the Codex-confirmed native-vs-corrupt discriminator): fully parse the
    // PE headers, then decide on the RAW CLR data-directory entry — NOT on `CorHeader == null` alone, which conflates
    // a genuinely native image with one whose CLR directory is advertised but unresolvable (a nonzero-but-invalid RVA
    // leaves CorHeader null WITHOUT throwing). Only a PE whose CorHeaderTableDirectory is truly empty (RVA==0 && Size==0)
    // is native (a legitimate silent skip). A PE-header parse failure (BadImageFormatException — a truncated/corrupt
    // image, e.g. a partial concurrent write) is Broken; an open/read failure (sharing violation / EndOfStream) is IoError.
    static RefLoadKind ClassifyReference(string path, out string detail)
    {
        detail = null;
        FileStream stream;
        try { stream = File.OpenRead(path); }
        catch (IOException ex) { detail = ex.Message; return RefLoadKind.IoError; }
        catch (UnauthorizedAccessException ex) { detail = ex.Message; return RefLoadKind.IoError; }
        using (stream)
        {
            try
            {
                using var pe = new PEReader(stream);
                var headers = pe.PEHeaders;               // lazily parses the PE headers; throws BadImageFormatException on a corrupt/truncated image
                if (headers.IsCoffOnly || headers.PEHeader == null)
                {
                    detail = "not a PE image (COFF-only / no optional header)";
                    return RefLoadKind.Broken;
                }
                var clr = headers.PEHeader.CorHeaderTableDirectory;
                if (clr.RelativeVirtualAddress == 0 && clr.Size == 0)
                {
                    detail = "valid PE, no CLI/COR directory (native image)";
                    return RefLoadKind.Native;
                }
                if (headers.CorHeader == null)
                {
                    detail = "CLI/COR directory advertised but unresolvable";
                    return RefLoadKind.Broken;
                }
                return RefLoadKind.Managed;
            }
            catch (BadImageFormatException ex) { detail = ex.Message; return RefLoadKind.Broken; }
            catch (IOException ex) { detail = ex.Message; return RefLoadKind.IoError; }
        }
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
