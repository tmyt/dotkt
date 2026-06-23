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
// System.Linq, …). The map is derived by reflecting the same --refs the forward path (facadegen) already uses —
// this is the mirror image of forward injection. Pure metadata surgery via Mono.Cecil (no Reflection.Emit, so
// none of the TypeBuilder/MetadataLoadContext generic-instantiation limits that sank the two earlier attempts).
//
// USAGE: retarget <input.dll> [--out <path>] [--refs "a.dll;b.dll;…"] [--core <name>] [-v]
//   --out   defaults to in-place. --refs is the reference-assembly set (csc's @(ReferencePath)).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;

static class Retarget
{
    // The single AssemblyRef ilemit emits for every BCL type. We repoint refs away from it.
    const string CoreLibName = "System.Private.CoreLib";

    static int Main(string[] argv)
    {
        if (argv.Length < 1)
        {
            Console.Error.WriteLine("usage: retarget <input.dll> [--out <path>] [--refs \"a.dll;b.dll\"] [-v]");
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
                case "--refs": refPaths.AddRange(SplitRefs(argv[++i])); break;
                case "-v": case "--verbose": verbose = true; break;
                default: Console.Error.WriteLine($"retarget: unknown arg '{argv[i]}'"); return 2;
            }
        }
        if (!File.Exists(input)) { Console.Error.WriteLine($"retarget: not found: {input}"); return 2; }

        // type FullName (e.g. "System.Collections.Generic.List`1") -> the ref-pack contract assembly that DEFINES it.
        var typeToContract = BuildContractMap(refPaths, verbose);
        if (typeToContract.Count == 0)
            Console.Error.WriteLine("retarget: WARNING no types resolved from --refs; only the System.Runtime fallback will apply.");

        // Read fully into memory so we can overwrite the same path. The backing stream must stay open until
        // after Write() — Cecil re-reads method bodies during serialization — so we hold the MemoryStream.
        var bytes = File.ReadAllBytes(input);
        var backing = new MemoryStream(bytes, writable: false);
        // A resolver over the --refs dirs (+ the input's own dir) so Cecil can resolve referenced types it must
        // inspect — notably an ENUM custom-attribute argument (e.g. DotKt's [KotlinFunction(KotlinFunctionFlags)]),
        // whose underlying type Cecil reads from the defining assembly to (de)serialize the blob.
        var resolver = new DefaultAssemblyResolver();
        foreach (var d in refPaths.Select(p => Path.GetDirectoryName(Path.GetFullPath(p))).Append(Path.GetDirectoryName(Path.GetFullPath(input))).Where(d => !string.IsNullOrEmpty(d)).Distinct())
            resolver.AddSearchDirectory(d);
        var asm = AssemblyDefinition.ReadAssembly(backing,
            new ReaderParameters { InMemory = true, ReadingMode = ReadingMode.Immediate, AssemblyResolver = resolver });
        var module = asm.MainModule;

        var coreRefs = module.AssemblyReferences.Where(a => a.Name == CoreLibName).ToList();
        if (coreRefs.Count == 0)
        {
            if (verbose) Console.WriteLine("retarget: no System.Private.CoreLib ref — already clean, copying through.");
            if (outPath != input) asm.Write(outPath); else asm.Write(input);
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
        // its identity from whatever contract the map produced, else synthesize the well-known ECMA identity.
        AssemblyName fallbackRuntime =
            typeToContract.Values.FirstOrDefault(a => a.Name == "System.Runtime")
            ?? new AssemblyName("System.Runtime") { Version = new Version(coreRefs[0].Version.Major, 0, 0, 0) };

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

        asm.Write(outPath);

        if (verbose || missing > 0)
        {
            Console.WriteLine($"retarget: {Path.GetFileName(input)} — repointed {remapped} TypeRef(s) across " +
                              $"{contractRefs.Count} contract assemblies" + (missing > 0 ? $", {missing} via System.Runtime fallback" : "") + ".");
            if (missing > 0)
                Console.WriteLine("  fallback (not found in --refs): " + string.Join(", ", unresolved));
        }
        return 0;
    }

    static IEnumerable<string> SplitRefs(string s) =>
        s.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
         .Select(x => x.Trim()).Where(x => x.Length > 0);

    // Reflect the reference pack via MetadataLoadContext (the forward path's machinery) and record, for every
    // DEFINED top-level type, which contract assembly owns it. Forwarders are skipped — we want the real definer
    // so the consuming compiler resolves the type from an assembly its own reference set actually contains.
    static Dictionary<string, AssemblyName> BuildContractMap(List<string> refPaths, bool verbose)
    {
        var map = new Dictionary<string, AssemblyName>(StringComparer.Ordinal);
        var paths = refPaths.Where(File.Exists).Distinct().ToList();
        if (paths.Count == 0) return map;

        // MLC needs a core assembly present in the path set.
        string core = new[] { "System.Runtime", "System.Private.CoreLib", "mscorlib", "netstandard" }
            .Select(n => paths.FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p), n, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(p => p != null);
        core ??= paths[0];

        var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths), Path.GetFileNameWithoutExtension(core));
        foreach (var p in paths)
        {
            Assembly a;
            try { a = mlc.LoadFromAssemblyPath(p); } catch { continue; }
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
        if (verbose) Console.WriteLine($"retarget: contract map = {map.Count} types from {paths.Count} ref assemblies (core={Path.GetFileNameWithoutExtension(core)}).");
        return map;
    }
}
