// retarget — makes an ilemit-produced assembly consumable via a COMPILE-TIME C# <Reference>.
//
// WHY: ilemit seeds PersistedAssemblyBuilder with typeof(object).Assembly (= System.Private.CoreLib), so every
// BCL TypeRef in the output (Object/String/List`1/Task…) resolves through the single System.Private.CoreLib
// AssemblyRef. That assembly is NOT in the reference pack a C# compiler sees, so `<Reference>`ing the dll fails
// with CS0012. Reflection LOAD at runtime works (the runtime forwards CoreLib types), which is why
// samples/il-revinterop runs but compile-time reference does not — this tool closes that gap (R-1).
//
// WHAT: repoint each System.Private.CoreLib-scoped TypeReference to the contract assembly the REFERENCE PACK
// says owns that type (Object/String/Task -> System.Runtime, List/Dictionary -> System.Collections, LINQ ->
// System.Linq, …). The map is derived from the same --compile-refs used for CLR reference binding.
// Pure metadata surgery via Mono.Cecil (no Reflection.Emit, so
// none of the TypeBuilder/MetadataLoadContext generic-instantiation limits that sank the two earlier attempts).
//
// USAGE: retarget <input.dll> [--out <path>] [--compile-refs "a.dll;b.dll;…"] [-v]
//   --out   defaults to in-place. --compile-refs is the resolved compile set (csc's @(ReferencePath)).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DotKt.Toolchain;
using Mono.Cecil;

static class Retarget
{
    // The single AssemblyRef ilemit emits for every BCL type. We repoint refs away from it.
    const string CoreLibName = "System.Private.CoreLib";

    static int Main(string[] argv)
    {
        try { return Run(argv); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"retarget: {ex.Message}");
            return 1;
        }
    }

    static int Run(string[] argv)
    {
        if (argv.Length < 1)
        {
            Console.Error.WriteLine("usage: retarget <input.dll> [--out <path>] [--compile-refs \"a.dll;b.dll\"] [-v]");
            return 2;
        }
        string input = argv[0];
        string outPath = input;
        var refPaths = new List<string>();
        bool verbose = false;
        for (int i = 1; i < argv.Length; i++)
        {
            switch (argv[i])
            {
                case "--out": outPath = argv[++i]; break;
                case "--compile-refs": refPaths.AddRange(ManagedReferenceCatalog.Split(argv[++i])); break;
                case "-v": case "--verbose": verbose = true; break;
                default: Console.Error.WriteLine($"retarget: unknown arg '{argv[i]}'"); return 2;
            }
        }
        if (!File.Exists(input)) { Console.Error.WriteLine($"retarget: not found: {input}"); return 2; }

        // type FullName (e.g. "System.Collections.Generic.List`1") -> the ref-pack contract assembly that DEFINES it.
        var catalog = ManagedReferenceCatalog.Create(refPaths, "retarget");
        var typeToContract = BuildContractMap(catalog, verbose);
        if (typeToContract.Count == 0)
            Console.Error.WriteLine("retarget: WARNING no types resolved from --compile-refs; only the System.Runtime fallback will apply.");

        // Read fully into memory so we can overwrite the same path. The backing stream must stay open until
        // after Write() — Cecil re-reads method bodies during serialization — so we hold the MemoryStream.
        var bytes = File.ReadAllBytes(input);
        var backing = new MemoryStream(bytes, writable: false);
        // An exact resolver over --compile-refs so Cecil can resolve referenced types it must
        // inspect — notably an ENUM custom-attribute argument (e.g. DotKt's [KotlinFunction(KotlinFunctionFlags)]),
        // whose underlying type Cecil reads from the defining assembly to (de)serialize the blob.
        using var resolver = new ExactCecilResolver(catalog);
        var asm = AssemblyDefinition.ReadAssembly(backing,
            new ReaderParameters { InMemory = true, ReadingMode = ReadingMode.Immediate, AssemblyResolver = resolver });
        var module = asm.MainModule;

        var coreRefs = module.AssemblyReferences.Where(a => a.Name == CoreLibName).ToList();
        if (coreRefs.Count == 0)
        {
            if (verbose) Console.WriteLine("retarget: no System.Private.CoreLib ref — already clean, copying through.");
            AtomicFile.Write(outPath, fs => asm.Write(fs));   // #52 — temp+rename so an in-place rewrite is never read torn
            return 0;
        }

        // Dedupe contract AssemblyNameReferences we add (one row per contract assembly).
        var contractRefs = new Dictionary<string, AssemblyNameReference>(StringComparer.Ordinal);
        AssemblyNameReference ContractRef(AssemblyName name)
        {
            if (contractRefs.TryGetValue(name.Name, out var r)) return r;
            r = new AssemblyNameReference(name.Name, name.Version);
            var tok = name.GetPublicKeyToken();
            if (tok != null && tok.Length > 0) r.PublicKeyToken = tok;
            contractRefs[name.Name] = r;
            module.AssemblyReferences.Add(r);
            return r;
        }

        // Fallback for types not found in the ref pack: System.Runtime (covers the object graph's core). We copy
        // its identity from whatever contract the map produced, else synthesize the well-known ECMA identity —
        // including the ECMA PublicKeyToken `b03f5f7f11d50a3a`. Without a PKT the synthesized reference is
        // partial (PKT=null), so a C# project `<Reference>`-ing the retargeted dll fails to bind System.Runtime.
        AssemblyName fallbackRuntime =
            typeToContract.Values.FirstOrDefault(a => a.Name == "System.Runtime")
            ?? new AssemblyName("System.Runtime") { Version = new Version(coreRefs[0].Version.Major, 0, 0, 0) };
        if (fallbackRuntime.GetPublicKeyToken() is not { Length: > 0 })
            fallbackRuntime.SetPublicKeyToken(new byte[] { 0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a });

        int remapped = 0, missing = 0;
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var tr in module.GetTypeReferences())
        {
            if (!(tr.Scope is AssemblyNameReference anr) || anr.Name != CoreLibName) continue;
            string key = tr.FullName; // namespace.name with `arity — matches the reflection FullName
            if (typeToContract.TryGetValue(key, out var contract))
            {
                tr.Scope = ContractRef(contract);
            }
            else
            {
                // Synthesize System.Runtime identity if it wasn't otherwise in the map yet.
                tr.Scope = ContractRef(fallbackRuntime);
                missing++; unresolved.Add(key);
            }
            remapped++;
        }

        // Drop any CoreLib AssemblyRef no longer referenced by a TypeRef scope.
        foreach (var core in coreRefs)
        {
            bool stillUsed = module.GetTypeReferences().Any(tr => ReferenceEquals(tr.Scope, core));
            if (!stillUsed) module.AssemblyReferences.Remove(core);
        }

        AtomicFile.Write(outPath, fs => asm.Write(fs));   // #52 — temp+rename so an in-place rewrite is never read torn

        if (verbose || missing > 0)
        {
            Console.WriteLine($"retarget: {Path.GetFileName(input)} — repointed {remapped} TypeRef(s) across " +
                              $"{contractRefs.Count} contract assemblies" + (missing > 0 ? $", {missing} via System.Runtime fallback" : "") + ".");
            if (missing > 0)
                Console.WriteLine("  fallback (not found in --compile-refs): " + string.Join(", ", unresolved));
        }
        return 0;
    }

    // Reflect the reference pack via MetadataLoadContext (the forward path's machinery) and record, for every
    // DEFINED top-level type, which contract assembly owns it. Forwarders are skipped — we want the real definer
    // so the consuming compiler resolves the type from an assembly its own reference set actually contains.
    static Dictionary<string, AssemblyName> BuildContractMap(ManagedReferenceCatalog catalog, bool verbose)
    {
        var map = new Dictionary<string, AssemblyName>(StringComparer.Ordinal);
        if (catalog.Entries.Count == 0) return map;
        using var mlc = catalog.CreateMetadataLoadContext();
        foreach (var p in catalog.Paths)
        {
            Assembly a;
            // The catalog already classified each path as a readable managed PE (#52); a load failure here means a
            // transitive dependency the MLC resolver could not satisfy — surface it naming the file rather than
            // silently dropping the assembly's types (which would send its TypeRefs to the System.Runtime fallback
            // with no hint of the cause). Non-fatal: the contract map is best-effort over the assemblies that loaded.
            try { a = mlc.LoadFromAssemblyPath(p); }
            catch (Exception ex) { Console.Error.WriteLine($"retarget: warning: could not load reference into the metadata context: {p} — {ex.GetType().Name}: {ex.Message}"); continue; }
            var an = a.GetName();
            Type[] types;
            try { types = a.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
            catch { continue; }
            foreach (var t in types)
            {
                if (t.IsNested || !(t.IsPublic)) continue;       // nested refs follow their declarer's scope
                var name = t.FullName;                            // includes `arity for generics
                if (name == null) continue;
                if (!map.ContainsKey(name)) map[name] = an;       // first definer wins
            }
        }
        if (verbose) Console.WriteLine($"retarget: contract map = {map.Count} types from {catalog.Entries.Count} compile-reference assemblies.");
        return map;
    }

    sealed class ExactCecilResolver : IAssemblyResolver
    {
        readonly ManagedReferenceCatalog _catalog;
        readonly Dictionary<string, AssemblyDefinition> _loaded = new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);
        public ExactCecilResolver(ManagedReferenceCatalog catalog) { _catalog = catalog; }

        public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters());

        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            if (_loaded.TryGetValue(name.Name, out var loaded)) return loaded;
            if (!_catalog.TryGet(new AssemblyName(name.FullName), out var entry))
                throw new AssemblyResolutionException(name);
            var rp = new ReaderParameters
            {
                AssemblyResolver = this,
                InMemory = true,
                ReadingMode = ReadingMode.Immediate,
            };
            var assembly = AssemblyDefinition.ReadAssembly(entry.Path, rp);
            _loaded[name.Name] = assembly;
            return assembly;
        }

        public void Dispose()
        {
            foreach (var assembly in _loaded.Values) assembly.Dispose();
            _loaded.Clear();
        }
    }
}
