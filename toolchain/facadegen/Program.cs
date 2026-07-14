// facadegen — reads .NET type metadata via reflection and emits FIR-injection metadata, so a kotlin/clr program can
// call those .NET types FAÇADE-FREE (`import System.X` resolves directly; the compiler's FIR injector consumes the
// metadata).
//
//   facadegen --meta <outFile> [--refs a.dll;b.dll;...] <Type.Full.Name>... [--import-list <file>]
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TN = DotKt.Bir.TypeNode;

static class FacadeGen
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: facadegen --meta <outFile> [--refs a.dll;...] <TypeFullName>... [--import-list <file>]");
            return 1;
        }
        // S5 generalization: emit a compact metadata file consumed by the compiler's FIR injector,
        // so .NET types resolve façade-free. Same reflection as the .kt path, different sink.
        // I2: `--refs <a.dll;b.dll;...>` lets types resolve from arbitrary referenced assemblies
        // (Avalonia/WPF/NuGet), not just the BCL — fed by MSBuild's @(ReferencePath).
        if (args[0] == "--meta")
        {
            var rest = args.Skip(2).ToList();
            if (rest.Count >= 2 && rest[0] == "--refs")
            {
                LoadRefs(rest[1].Split(';', StringSplitOptions.RemoveEmptyEntries));
                rest = rest.Skip(2).ToList();
            }
            // C-2: explicit type names, then optionally `--import-list <file>` — the type list produced by the
            // compiler's `kotc --scan-imports` PSI pass (a real parser, not a regex: handles aliases, `.*`,
            // multi-line, comments, backtick identifiers — interop feedback item 5). Merge both; EmitMeta warns
            // on any .NET-looking name that resolves to nothing (no silent drop).
            var listAt = rest.IndexOf("--import-list");
            var explicitTypes = listAt < 0 ? rest : rest.Take(listAt).ToList();
            var imported = listAt < 0 || listAt + 1 >= rest.Count ? Enumerable.Empty<string>() : ReadImportList(rest[listAt + 1]);
            return EmitMeta(args[1], explicitTypes.Concat(imported).Distinct());
        }
        // .NET interop is façade-free via `--meta` (the FIR injector consumes the metadata directly). There is no other mode.
        Console.Error.WriteLine("facadegen: only `--meta` mode is supported (façade-free .NET injection); see usage above.");
        return 1;
    }

    static readonly string[] PROBE_ASSEMBLIES =
        { "System.Runtime", "System.Collections", "System.ObjectModel", "System.Private.CoreLib", "System.Console", "System.Runtime.Extensions", "System.Linq", "mscorlib" };

    // I2: referenced assemblies (Avalonia/WPF/NuGet) reflected via MetadataLoadContext — it reads
    // metadata WITHOUT executing, so it handles reference assemblies (ref/ folder) that LoadFrom rejects.
    static System.Reflection.MetadataLoadContext Mlc;

    static void LoadRefs(string[] paths)
    {
        if (paths.Length == 0) return;
        // The core assembly (System.Private.CoreLib / System.Runtime) must be in the path set. Chosen from the CALLER's
        // paths (before the CoreLib backfill below), so the ref-pack's System.Runtime stays the core when present.
        var core = new[] { "System.Private.CoreLib", "System.Runtime", "mscorlib", "netstandard" }
            .FirstOrDefault(n => paths.Any(p => Path.GetFileNameWithoutExtension(p).Equals(n, StringComparison.OrdinalIgnoreCase)))
            ?? "System.Runtime";
        // Bug ⑥ (wiring): a DotKt assembly emitted against the runtime (e.g. an un-retargeted DotKt.Stdlib.dll) references
        // System.Private.CoreLib — and the ref-pack's System.Runtime carries TYPE FORWARDERS to it. If that CoreLib is
        // absent from the resolver's path set, reflecting ANY member whose signature touches a stdlib type throws
        // FileNotFoundException, and EmitMeta's per-type guard then SKIPS the whole owning type — so a user-library
        // function with a stdlib-typed signature (e.g. `fun makePair(): Pair<Int,Int>`) SILENTLY vanishes from the meta.
        // Backfill the running runtime's System.Private.CoreLib so those refs (and the forwarders to them) resolve.
        // Types are compared by FullName throughout facadegen (not identity), so a ref-pack System.Object and this
        // runtime System.Object coexisting is already the anticipated case (see Map's I2 note). Harmless when the caller
        // already retargeted everything to System.Runtime (the path is just unused).
        var pathList = paths.ToList();
        if (!pathList.Any(p => Path.GetFileNameWithoutExtension(p).Equals("System.Private.CoreLib", StringComparison.OrdinalIgnoreCase)))
        {
            var coreLib = typeof(object).Assembly.Location;
            if (!string.IsNullOrEmpty(coreLib) && File.Exists(coreLib)) pathList.Add(coreLib);
        }
        Mlc = new System.Reflection.MetadataLoadContext(new System.Reflection.PathAssemblyResolver(pathList), core);
        foreach (var p in pathList)
            try { Mlc.LoadFromAssemblyPath(p); } catch { /* skip unloadable */ }
    }

    static Type Resolve(string name)
    {
        if (Mlc != null)
        {
            foreach (var asm in Mlc.GetAssemblies())
                try { var mt = asm.GetType(name); if (mt != null) return mt; } catch { }
            return null;
        }
        var t = Type.GetType(name);
        if (t != null) return t;
        foreach (var asm in PROBE_ASSEMBLIES)
            if ((t = Type.GetType($"{name}, {asm}")) != null) return t;
        return null;
    }

    // Compact line-based metadata for the FIR injector. Each injected type keeps its real .NET namespace (the second
    // token is the .NET FullName, from which the injector derives the Kotlin ClassId) — there is NO fixed `clrgen`
    // package (that was an early-prototype convention; the meta grammar carries the true namespace now). Line shapes:
    //   object <SimpleName> <DotNetFullName>      | class <SimpleName> <DotNetFullName>
    //   fun <Name> <RetKotlinType> [pName:pType]* | ctor [pName:pType]*
    // Kotlin member names = .NET names verbatim (no per-member mapping needed in the backend).
    // A static .NET class (abstract+sealed, e.g. System.Math) -> Kotlin `object` (static call site);
    // an instance class -> Kotlin `class` with constructors + instance methods.
    // C-2: read the .NET import list produced by `kotc --scan-imports` (the compiler's PSI-based pre-pass — see
    // ImportScan.kt). Each line is a fully-qualified type name, or `Namespace.*` for a wildcard import, expanded
    // here to every public type directly in that namespace across the loaded reference assemblies.
    static IEnumerable<string> ReadImportList(string file)
    {
        if (!File.Exists(file)) yield break;
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.EndsWith(".*"))
                foreach (var tn in TypesInNamespace(line.Substring(0, line.Length - 2))) yield return tn;
            else
                yield return line;
        }
    }

    // Every public, non-nested type directly in `ns` (no sub-namespaces), across loaded reference assemblies.
    // Generic definitions are yielded without their arity suffix (`...List`, not `...List`1`) so Resolve's
    // arity probing picks them up consistently with explicitly-imported names.
    static IEnumerable<string> TypesInNamespace(string ns)
    {
        var dns = ns;   // a Kotlin package maps 1:1 to its .NET namespace (no projection)
        var asms = Mlc != null ? Mlc.GetAssemblies() : Enumerable.Empty<Assembly>();
        var seen = new HashSet<string>();
        foreach (var asm in asms)
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch { continue; }
            foreach (var t in types)
            {
                if (t.Namespace != dns || !t.IsPublic || t.IsNested || t.FullName == null || IsCompilerGenerated(t)) continue;   // #68: skip generated types by attribute
                var name = t.FullName.Contains('`') ? t.FullName.Substring(0, t.FullName.IndexOf('`')) : t.FullName;
                if (seen.Add(name)) yield return name;
            }
        }
    }

    // A top-level function import (`import geom.greet`) -> the [KotlinFileClass] facade class in that package that holds a
    // matching static method, so the FIR injector restores `greet` as a top-level function. Null if none.
    static Type ResolveTopLevelFacade(string fqn)
    {
        if (Mlc == null) return null;
        int dot = fqn.LastIndexOf('.');
        var ns = dot < 0 ? "" : fqn.Substring(0, dot);
        var fn = dot < 0 ? fqn : fqn.Substring(dot + 1);
        foreach (var asm in Mlc.GetAssemblies())
        {
            Type[] types; try { types = asm.GetTypes(); } catch { continue; }
            foreach (var t in types)
            {
                if ((t.Namespace ?? "") != ns || !HasKotlinFileClass(t)) continue;
                // `import pkg.foo` matches a top-level function `foo` OR an extension property whose getter is `get_foo`.
                if (t.GetMethods(BindingFlags.Public | BindingFlags.Static).Any(mm => mm.Name == fn || mm.Name == "get_" + fn)) return t;
            }
        }
        return null;
    }

    static int EmitMeta(string outFile, IEnumerable<string> typeNames)
    {
        MetaMode = true;   // enable array/cross-type member support for the FIR-injection path
        // Structured injection document (spec §5b): `{ "types": [...], "files": [...] }` — decls reusing the
        // BIR TypeNode / mods vocabulary. The line grammar is retired.
        var types = new JsonArray();
        var files = new JsonArray();
        // (6) Reachable-closure auto-injection: the imported types are SEEDS; we BFS out to every type their API
        // surface references (base class chain, implemented interfaces, member return/param/element/generic-arg
        // types) and inject the whole reachable closure. This is what makes chained access (`panel.Children.Add`)
        // and cross-type assignability work without the user importing every intermediate type — see
        // docs/dotkt-interop-feedback.md (6). Bounded by resolvability + a hard cap (runaway backstop).
        const int CAP = 5000;
        var queue = new Queue<Type>();
        var enqueued = new HashSet<string>();   // by FullName — guards the queue
        var done = new HashSet<string>();        // by FullName — already emitted
        void Enqueue(Type ty) { var fn = ty?.FullName; if (fn != null && enqueued.Add(fn)) queue.Enqueue(ty); }
        var seeds = 0;
        // An imported name may denote a whole ARITY FAMILY (Task AND Task`1..`n share one source name): seed EVERY
        // member, so `import System.Threading.Tasks.Task` surfaces BOTH the non-generic Task and Task1<TResult>.
        static List<Type> ResolveFamily(string nm)
        {
            var fam = new List<Type>();
            if (Resolve(nm) is { } plain) fam.Add(plain);
            for (var n = 1; n <= 17; n++) if (Resolve(nm + "`" + n) is { } g) fam.Add(g);
            // An ARITY-QUALIFIED Kotlin name (`import System.Threading.Tasks.Task1` — our own naming for Task`1):
            // when nothing resolves plainly, re-probe the trailing digits as the CLR backtick arity. Fires only on a
            // total miss, so real digit-suffixed .NET names (Vector2, Int32) — which resolve plainly — are unaffected.
            if (fam.Count == 0)
            {
                var i = nm.Length; while (i > 0 && char.IsDigit(nm[i - 1])) i--;
                if (i > 0 && i < nm.Length && nm[i - 1] != '.' && Resolve(nm.Substring(0, i) + "`" + nm.Substring(i)) is { } q) fam.Add(q);
            }
            return fam;
        }
        foreach (var typeName in typeNames)
        {
            var dn = typeName;   // a Kotlin type name IS its .NET name (no namespace projection)
            // Resolve the full arity family: the plain type and/or generic definitions (Collection`1, Func`1..`17, …).
            var fam = ResolveFamily(dn);
            // A top-level function import (`import geom.greet`) isn't a type — resolve it to the [KotlinFileClass] facade
            // class that holds it (EmitOneType then emits `file`/`tlfun`). `import geom.*` already yields the facade
            // type directly via TypesInNamespace, so this only covers the single-function form.
            if (fam.Count == 0 && ResolveTopLevelFacade(dn) is { } facade) fam.Add(facade);
            // A MEMBER import (`import Probe.Ext.tripled` — a member of an object/static class, e.g. a C#-origin
            // extension fun) doesn't resolve to a type; inject its CONTAINING type (the prefix) so the member comes into
            // scope. Walk successive prefixes (handles a member of a nested type) and stop at the first that resolves.
            for (var prefix = dn; fam.Count == 0 && prefix.Contains('.'); )
            {
                prefix = prefix.Substring(0, prefix.LastIndexOf('.'));
                fam = ResolveFamily(prefix);
            }
            if (fam.Count == 0) { Console.Error.WriteLine($"warning: .NET import resolved to no type (injected nothing): {typeName}"); continue; }
            // BINDING INVARIANT: a resolved seed that lands in `kotlin.*` is dropped — the stdlib comes from the
            // frontend JAR, never facadegen (see IsKotlinStdlibSymbol). The `kotlin.clr.await` bridge is exempt: it
            // resolves to no type here and is surfaced textually by EmitAwaitables. Defense-in-depth / output-neutral.
            fam.RemoveAll(s => IsKotlinStdlibSymbol(s));
            if (fam.Count == 0) { Console.Error.WriteLine($"warning: .NET import is a kotlin.* stdlib symbol (owned by the JAR, not facadegen; injected nothing): {typeName}"); continue; }
            seeds++; foreach (var seed in fam) Enqueue(seed);
        }
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            if (!done.Add(t.FullName!)) continue;
            // Scan one type (emit + closure traversal) under a guard: a single malformed/unreflectable type — e.g. a
            // stdlib member whose signature references a type the MetadataLoadContext can't resolve — is SKIPPED with a
            // warning rather than aborting the whole façade scan. Emit into a local buffer committed only on success.
            try
            {
                EmitOneType(t, types, files);
                foreach (var r in ReferencedTypes(t))
                    foreach (var u in Unwrap(r))
                        if (ShouldInject(u)) Enqueue(u);
            }
            catch (Exception ex) { Console.Error.WriteLine($"warning: skipped type {t.FullName}: {ex.GetType().Name}: {ex.Message}"); }
            if (done.Count >= CAP) { Console.Error.WriteLine($"warning: injection closure hit cap {CAP}; truncating reachable set"); break; }
        }
        Console.WriteLine($"closure: {seeds} seed(s) -> {done.Count} injected type(s)");
        // #10 — the `.await()` CLR platform extensions. For EVERY surfaced .NET type that matches the AWAITABLE PATTERN
        // (a conforming GetAwaiter — member, or a referenced [Extension]), inject the Kotlin-facing `suspend fun X.await()`
        // in package `kotlin.clr` — the sole frontend surfacing of the CLR async boundary (deliberately EXCLUDED from the
        // frontend stdlib jar, design-coroutine-cold-core-task-bridge.md §5/§12). facadegen only SURFACES the symbol so
        // kotc resolves `x.await()` in a suspend context and emits it as a suspend call; the BODY is bir2cir-lowered at
        // the call site to the awaiter + Continuation bridge — facadegen binds NO intrinsic here.
        EmitAwaitables(done, files);
        var doc = new JsonObject { ["types"] = types, ["files"] = files };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        File.WriteAllText(outFile, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        return 0;
    }

    // #10 — inject a `.await()` suspend extension for EVERY surfaced .NET type matching the AWAITABLE PATTERN, as one
    // top-level [KotlinFile] section. The `file`'s .NET class token is `kotlin.clr.CoroutinesKt` — where the real
    // (bir2cir-lowered / TODO-bodied) declaration lives in `libraries/stdlib/clr/taskinterop/kotlin/clr/Coroutines.kt`;
    // it is the marker bir2cir keys on (owner == "kotlin.clr.CoroutinesKt", method == "await") to lower the call site.
    // A type is awaitable iff it has a conforming GetAwaiter — a MEMBER (Task/ValueTask) or a referenced [Extension]
    // (WinRT IAsyncOperation<T> via WindowsRuntimeSystemExtensions, or any custom extension awaitable). Receiver tokens
    // use the facadegen-surfaced Kotlin name (arity-qualified `Task1`/`ValueTask1`, plain `Task`), so
    // `import …; x.await()` resolves on the ONE facadegen-surfaced awaitable.
    static void EmitAwaitables(HashSet<string> done, JsonArray files)
    {
        var funs = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);   // by receiver Kotlin name — de-dup overloaded await
        // #3: `captureContext: Boolean = true` opt-out param — injected ONLY for a Task-LIKE awaitable that exposes a
        // `ConfigureAwait(bool)` member. The default `true` IS the current runtime behavior (OnCompleted captures the
        // SynchronizationContext); passing `false` makes bir2cir emit the ConfigureAwait(false) awaiter. The const bool
        // default rides the DefaultObj path; the marker is bir2cir-lowered before ilemit, so the operative consumer is
        // kotc's metaDefaults fill (an omitted call gets a real const `true` arg) — args[1] at the await site.
        JsonObject CaptureCtxParam() => new JsonObject
        {
            ["name"] = "captureContext",
            ["type"] = Ty(new TN.Fqn("Boolean")),
            ["default"] = new JsonObject { ["valueType"] = "Boolean", ["value"] = "true" },
        };
        // DETERMINISTIC order: `done` is a HashSet, so iterate sorted — the injected await set (and, on a simple-name
        // collision across namespaces, WHICH type wins the receiver token) is then reproducible build-to-build.
        foreach (var fn in done.OrderBy(x => x, StringComparer.Ordinal))
        {
            // Per-type guard (mirrors the EmitOneType closure guard): one awaitable whose GetAwaiter/awaiter signature
            // references an MLC-unresolvable type must not abort the whole facadegen run — skip it with a warning.
            try
            {
            var t = Resolve(fn);
            if (t == null) continue;
            var awaiterRet = AwaitableAwaiterReturn(t);
            if (awaiterRet == null || !AwaiterConforms(awaiterRet)) continue;
            var kname = KotlinName(t);
            if (!seen.Add(kname)) continue;
            var voidResult = GetResultReturnType(awaiterRet)?.FullName == "System.Void";
            var wantCfg = HasConfigureAwaitBool(t);
            // The result type the lowering emits is dictated by the marker: non-generic → Unit (void GetResult),
            // generic arity-1 → the receiver's type arg T. GATE injection to awaitables whose awaiter genuinely matches
            // that shape (else bir2cir would type-confuse the resume value): a non-generic awaitable must have a
            // NON-generic awaiter, and a generic one's GetResult must return exactly the type parameter.
            if (!t.IsGenericTypeDefinition && voidResult && !awaiterRet.IsGenericType)
            {
                // `suspend fun X.await([captureContext]): Unit` — non-generic void awaitable (Task, IAsyncAction).
                var ps = new JsonArray { new JsonObject { ["name"] = "__self", ["type"] = Ty(new TN.Fqn(kname)) } };
                if (wantCfg) ps.Add(CaptureCtxParam());
                funs.Add(FunObj("await", new TN.Fqn("Unit"), Mods(("ext", true), ("suspend", true)), "public", null, null, ps));
            }
            else if (t.IsGenericTypeDefinition && t.GetGenericArguments().Length == 1 && !voidResult
                     && GetResultReturnType(awaiterRet) is { IsGenericParameter: true })
            {
                // `suspend fun <T> X<T>.await([captureContext]): T` — arity-1 generic awaitable whose GetResult yields
                // its type arg (Task<T>, ValueTask<T>, IAsyncOperation<T>, a custom MyOp<T>). T = method type param 0.
                var tp = new JsonArray { new JsonObject { ["name"] = "T" } };
                var ps = new JsonArray { new JsonObject { ["name"] = "__self",
                    ["type"] = Ty(new TN.Fqn(kname, new TN[] { new TN.Tv("method", 0) })) } };
                if (wantCfg) ps.Add(CaptureCtxParam());
                funs.Add(FunObj("await", new TN.Tv("method", 0), Mods(("ext", true), ("suspend", true)), "public", null, tp, ps));
            }
            else
            {
                seen.Remove(kname);   // not an injectable arity/result shape — let a later arity of the same name try
            }
            }
            catch (Exception ex) { Console.Error.WriteLine($"warning: skipped awaitable {fn}: {ex.GetType().Name}: {ex.Message}"); }
        }
        if (funs.Count == 0) return;
        files.Add(new JsonObject { ["pkg"] = "kotlin.clr", ["fileClass"] = "kotlin.clr.CoroutinesKt", ["funs"] = funs });
        Console.WriteLine($"meta: kotlin.clr.await ({funs.Count} .await() CLR platform suspend extension(s) — bir2cir-lowered)");
    }

    // The awaiter type an awaitable's GetAwaiter yields: a public parameterless instance MEMBER first (Task/ValueTask),
    // then a referenced `[Extension] GetAwaiter(this X)` (WinRT/custom). Null when the type is not awaitable.
    static Type AwaitableAwaiterReturn(Type t)
    {
        var m = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(x => x.Name == "GetAwaiter" && !x.IsGenericMethodDefinition && x.GetParameters().Length == 0);
        if (m != null) return m.ReturnType;
        var def = t.IsGenericType && !t.IsGenericTypeDefinition ? t.GetGenericTypeDefinition() : t;
        return GetAwaiterExtIndex().TryGetValue(def.FullName ?? t.Name, out var ext) ? ext.ReturnType : null;
    }

    // Index (built once) of every referenced `[Extension] static GetAwaiter(this <recv>)`, keyed by the receiver's
    // type-definition FullName — an open generic receiver `IAsyncOperation<TResult>` keys on `IAsyncOperation`1`.
    static Dictionary<string, MethodInfo> _getAwaiterExtIndex;
    static Dictionary<string, MethodInfo> GetAwaiterExtIndex()
    {
        if (_getAwaiterExtIndex != null) return _getAwaiterExtIndex;
        var idx = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
        if (Mlc != null)
            foreach (var asm in Mlc.GetAssemblies())
                foreach (var t in SafeTypes(asm))
                {
                    if (!t.IsAbstract || !t.IsSealed) continue;   // a static class
                    // Per-type guard: a static class whose method signatures reference an MLC-unresolvable type would
                    // otherwise abort the whole scan (this index is built on the first non-awaitable type) — skip it.
                    try
                    {
                        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        {
                            if (m.Name != "GetAwaiter" || !IsExtensionMethod(m)) continue;
                            var ps = m.GetParameters();
                            if (ps.Length != 1) continue;
                            var recv = ps[0].ParameterType;
                            var recvDef = recv.IsGenericType ? recv.GetGenericTypeDefinition() : recv;
                            idx.TryAdd(recvDef.FullName ?? recv.Name, m);
                        }
                    }
                    catch { /* unreflectable static class — skip */ }
                }
        return _getAwaiterExtIndex = idx;
    }

    static IEnumerable<Type> SafeTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null)!; }
        catch { return Array.Empty<Type>(); }
    }

    // A conforming awaiter: PUBLIC `bool IsCompleted { get; }`, a public parameterless `GetResult()`, and a PUBLIC
    // `OnCompleted(Action)` — the members the bir2cir lowering binds by direct instance call (so this MUST agree with
    // ReferenceMetadataIndex.AwaiterConforms). We require the public OnCompleted method, NOT merely an INotifyCompletion
    // impl: an explicit-interface-only awaiter has no public member for the direct call, so it is rejected here (no
    // `.await()` injected — an honest frontend miss rather than a loud ilemit failure). Awaiter may be generic.
    static bool AwaiterConforms(Type awaiter)
    {
        if (awaiter == null) return false;
        var def = awaiter.IsGenericType ? awaiter.GetGenericTypeDefinition() : awaiter;
        var hasIsCompleted = def.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(p => p.Name == "IsCompleted" && p.PropertyType.FullName == "System.Boolean" && p.CanRead);
        var hasGetResult = GetResultReturnType(def) != null;
        var hasOnCompleted = def.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.Name == "OnCompleted" && m.GetParameters().Length == 1);
        return hasIsCompleted && hasGetResult && hasOnCompleted;
    }

    static Type GetResultReturnType(Type awaiter)
    {
        var def = awaiter.IsGenericType && !awaiter.IsGenericTypeDefinition ? awaiter.GetGenericTypeDefinition() : awaiter;
        return def.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "GetResult" && m.GetParameters().Length == 0)?.ReturnType;
    }

    static bool HasConfigureAwaitBool(Type t) =>
        t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.Name == "ConfigureAwait" && m.GetParameters() is { Length: 1 } ps
                && ps[0].ParameterType.FullName == "System.Boolean");

    // Emit one type's FIR-injection metadata (enum/interface/annotation/object/class + members).
    // A `[ClrIntrinsic]`/`[ClrTypeAlias]` binding on a ref-assembly type/member (when facadegen reflects a DotKt
    // library) registers the Kotlin type's dotNet name AS THE BCL TARGET, so the app binds it
    // (kotlin.collections.List -> System.Collections.Generic.IReadOnlyList). docs/design-clr-stdlib-ref-runtime-split.md.
    // NOTE (M3): in the PRODUCTION import-scan path (`--meta ... --import-list`, no DotKt ref.dll scanned) this always
    // returns null — no injected .NET type carries these stdlib-binding attributes. It is kept because the ref/runtime-
    // split round-trip design (reflecting a DotKt library's ref.dll) depends on it and its removal is bir2cir-owner
    // territory; do NOT delete without confirming that consumer. Its only live effect today is via GenuineNet's
    // `ClrAttrName(t) == null` guard, which is a no-op given the always-null result.
    static string ClrAttrName(MemberInfo m)
    {
        var a = m.GetCustomAttributesData().FirstOrDefault(x => (x.AttributeType.Name == "ClrIntrinsic" || x.AttributeType.Name == "ClrTypeAlias") && x.ConstructorArguments.Count == 1);
        return a?.ConstructorArguments[0].Value as string;
    }

    // A GENUINE .NET interop type: a real BCL/3rd-party type (not a `kotlin.*` stdlib shape, not a @Clr-bound stdlib
    // type). Its injected members are REAL BCL members addressed BY NAME, so each is emitted with an identity
    // `clr:<.NETName>` binding — the consumer's `clrInteropName` then resolves it as a DIRECT BCL member and the backend
    // routes the call straight to that BCL member. WITHOUT the binding, the backend's rule-3 (a non-@Clr concrete member
    // of a CLR-bound class) wrongly hoists the call to a non-existent `dotkt$ClrH_<Type>` static helper (the helper is
    // only emitted for @Clr STDLIB classes with real Kotlin bodies; a facadegen-injected member has no body). A
    // `kotlin.*`/@Clr type is EXCLUDED: those carry their substitution via @ClrIntrinsic (mclr) and their bodied members
    // are legitimate rule-3 hoist candidates — stamping identity bindings on them would suppress that hoist.
    static bool GenuineNet(Type t) => ClrAttrName(t) == null && !IsKotlinStdlibSymbol(t);

    // .NET operator method (`op_Addition`, `op_UnaryNegation`, …) -> the Kotlin `operator fun` name. Binary ops take the
    // LEFT operand as the receiver (drop param[0]); unary ops take the sole operand as the receiver (no value params).
    static readonly Dictionary<string, string> OPERATOR_NAMES = new()
    {
        ["op_Addition"] = "plus", ["op_Subtraction"] = "minus", ["op_Multiply"] = "times", ["op_Division"] = "div",
        ["op_Modulus"] = "rem", ["op_UnaryNegation"] = "unaryMinus", ["op_UnaryPlus"] = "unaryPlus",
        ["op_Increment"] = "inc", ["op_Decrement"] = "dec",
    };
    static readonly HashSet<string> UNARY_OPERATORS = new() { "unaryMinus", "unaryPlus", "inc", "dec" };

    // A C#-origin extension method (`static T M(this X self, …)`) carries [ExtensionAttribute]. Recognize it so the
    // first param is restored as a Kotlin extension receiver (`,ext`), exactly like a DotKt-origin `__self` extension.
    static bool IsExtensionMethod(MethodInfo m)
    {
        try { return m.GetCustomAttributesData().Any(c => c.AttributeType.FullName == "System.Runtime.CompilerServices.ExtensionAttribute"); }
        catch { return false; }
    }

    // ====================================================================================================
    // Structured injection-document builders (spec §5b) — the decls reuse the shared BIR TypeNode / mods
    // vocabulary. These replace the retired space-separated line grammar.
    // ====================================================================================================

    static JsonNode Ty(TN t) => TN.Write(t);
    static TN AnyQ() => new TN.Nullable(new TN.Fqn("Any"));            // the erased/unresolvable identity ("Any?")
    static bool IsAnyQ(TN t) => t is TN.Nullable { Of: TN.Fqn { Name: "Any", Args: null } };
    static bool IsGenericFqn(TN t) => t is TN.Fqn { Args: not null };  // a constructed generic (was `generic:`)

    // A `mods` object with only the TRUE flags present (absent key = false), per spec §2.1.
    static JsonObject Mods(params (string name, bool on)[] flags)
    {
        var o = new JsonObject();
        foreach (var (n, on) in flags) if (on) o[n] = true;
        return o;
    }

    // The member modality/visibility split (spec §2.1): modality abstract|open (final = neither) into `mods`,
    // visibility into a separate `vis` enum. `prot` = .NET Family/FamORAssem -> protected.
    static (JsonObject mods, string vis) ModVis(bool prot, bool isAbstract, bool isOpen,
        bool infix = false, bool op = false, bool suspend = false, bool inline = false, bool ext = false)
    {
        var mods = new JsonObject();
        if (isAbstract) mods["abstract"] = true; else if (isOpen) mods["open"] = true;
        if (infix) mods["infix"] = true; if (op) mods["operator"] = true; if (suspend) mods["suspend"] = true;
        if (inline) mods["inline"] = true; if (ext) mods["ext"] = true;
        return (mods, prot ? "protected" : "public");
    }

    // A `fun` decl `{name, ret, mods, vis?, clrName?, typeParams?, params}` (spec §5b).
    static JsonObject FunObj(string name, TN ret, JsonObject mods, string vis, string clrName,
        JsonArray typeParams, JsonArray paramObjs)
    {
        var o = new JsonObject { ["name"] = name, ["ret"] = Ty(ret), ["mods"] = mods };
        if (vis != "public") o["vis"] = vis;
        if (clrName != null) o["clrName"] = clrName;
        if (typeParams != null && typeParams.Count > 0) o["typeParams"] = typeParams;
        o["params"] = paramObjs;
        return o;
    }

    // A `prop` decl `{name, type, rw, mods, vis?, clrName?, recv?}` (spec §5b). `recv` (a top-level/member
    // extension property) is the discriminator vs a plain property.
    static JsonObject PropObj(string name, TN type, bool rw, JsonObject mods, string vis, string clrName, TN recv)
    {
        var o = new JsonObject { ["name"] = name, ["type"] = Ty(type), ["rw"] = rw, ["mods"] = mods };
        if (vis != "public") o["vis"] = vis;
        if (clrName != null) o["clrName"] = clrName;
        if (recv != null) o["recv"] = Ty(recv);
        return o;
    }

    // An event decl `{name, handlerRet, handlerParams}` from a delegate's Invoke.
    static JsonObject EventObj(EventInfo ev, MethodInfo inv, Type self) => new()
    {
        ["name"] = ev.Name,
        ["handlerRet"] = Ty(MapT(inv.ReturnType, self)),
        ["handlerParams"] = ParamsArr(inv.GetParameters(), self),
    };

    // A param decl `{name, type, mods?, default?}` (spec §5b). vararg -> mods.vararg (element type in `type`);
    // a Kotlin default arg -> a structured `default` (no opt: token). __self keeps its name (the ext-receiver marker).
    static JsonObject ParamObj(ParameterInfo p, int i, Type self)
    {
        var o = new JsonObject { ["name"] = MetaParamName(p, i) };
        var attrs = CustomAttributeData.GetCustomAttributes(p);
        var sfn = SuspendFnNode(attrs);   // H2: a `suspend (…) -> T` parameter
        if (sfn != null) { o["type"] = Ty(sfn); return o; }
        if (p.ParameterType.IsArray && IsParamArray(p))
        {
            // vararg: the ELEMENT type rides `type` (the consumer arrays it, picking IntArray/etc. for a primitive
            // element). A vararg is non-null by nature, so the element carries NO NRT wrapper — an `oblivious`/`nullable`
            // wrapper here would defeat the consumer's primitive-array detection (`Array<Int!>` instead of `IntArray`).
            o["type"] = Ty(MapT(p.ParameterType.GetElementType(), self));
            o["mods"] = Mods(("vararg", true));
            return o;
        }
        var pt = ApplyNrt(MapTFn(p.ParameterType, self, attrs, p.Member as MemberInfo), p.ParameterType, attrs, p.Member as MemberInfo);  // #150: NRT-threaded (delegate args)
        o["type"] = Ty(HasExtFnMarker(attrs) ? WithExtRecv(pt) : pt);   // #145: `block: P.() -> R` -> restore the receiver
        // #146: a NON-CONST default (`= {}` / a call / any non-metadata-representable expr) carries no CLR
        // [Optional]+[DefaultParameterValue] — it rides `[kotlin.clr.KotlinDefault]` (the BIR sub-tree bir2cir splices
        // at the omitted call site). Surface it as a `{"nonConst":true}` default so the consumer marks the param
        // OPTIONAL (accepts the omission); the real value is filled from @KotlinDefault at BIR->CIR, not from this meta.
        if (HasDefault(p)) o["default"] = DefaultObj(p, self);
        else if (HasKotlinDefault(attrs)) o["default"] = new JsonObject { ["nonConst"] = true };
        return o;
    }

    static JsonArray ParamsArr(ParameterInfo[] ps, Type self)
    {
        var arr = new JsonArray();
        for (int i = 0; i < ps.Length; i++) arr.Add(ParamObj(ps[i], i, self));
        return arr;
    }

    // A structured default-arg value `{valueType, value}` (replaces opt:T=<const>). `value` is a JSON string
    // literal, or JSON null for a null default; `valueType` is the primitive kind the consumer builds a
    // FirLiteralExpression of. An unbuildable kind (enum/struct) -> the consumer's @JvmOverloads arity fallback.
    static JsonObject DefaultObj(ParameterInfo p, Type self)
    {
        var o = new JsonObject { ["valueType"] = DefaultValueType(p.ParameterType, self) };
        object v; try { v = p.RawDefaultValue; } catch { v = null; }
        if (v == null) o["value"] = null;
        else o["value"] = v is bool b ? (b ? "true" : "false") : (v.ToString() ?? "");
        return o;
    }

    static string DefaultValueType(Type t, Type self)
    {
        TN n = MapT(t, self);
        while (n is TN.Nullable nn) n = nn.Of;
        while (n is TN.Oblivious oo) n = oo.Of;
        return n is TN.Fqn f ? f.Name : "";
    }

    // Type-parameter declarations `[{name, variance?, bounds?:[T]}]` (spec §5b) — folds the retired
    // tvariance/tbound/mbound lines into structured typeParam objects (variance is interface-type-level only).
    static JsonArray TypeParamsArr(Type[] genArgs, Type self, bool isInterface, bool typeLevel)
    {
        var arr = new JsonArray();
        foreach (var g in genArgs)
        {
            if (!g.IsGenericParameter) continue;
            var tp = new JsonObject { ["name"] = g.Name };
            if (typeLevel && isInterface)
            {
                var a = g.GenericParameterAttributes;
                if ((a & GenericParameterAttributes.Covariant) != 0) tp["variance"] = "out";
                else if ((a & GenericParameterAttributes.Contravariant) != 0) tp["variance"] = "in";
            }
            Type[] cons; try { cons = g.GetGenericParameterConstraints(); } catch { cons = Array.Empty<Type>(); }
            var bounds = new JsonArray();
            foreach (var c in cons)
            {
                if (NO_INJECT.Contains(c.FullName ?? "")) continue;
                var bt = MapBoundT(c, self);
                if (IsAnyQ(bt)) continue;
                bounds.Add(Ty(bt));
            }
            if (bounds.Count > 0) tp["bounds"] = bounds;
            arr.Add(tp);
        }
        return arr;
    }

    static void EmitOneType(Type t, JsonArray types, JsonArray files)
    {
        var typeObj = new JsonObject();
        var ctors = new JsonArray(); var props = new JsonArray(); var funs = new JsonArray();
        var memberExtProps = new JsonArray(); var events = new JsonArray(); var supers = new JsonArray();
        var staticFuns = new JsonArray(); var staticProps = new JsonArray(); var staticEvents = new JsonArray();
        // C#-origin `[Extension]` static methods (`static int Twice(this W w)`) ALSO surface as TOP-LEVEL Kotlin
        // extension funs (`fun W.Twice(): Int`) in a `file` decl keyed on this static class, so `import NS.*` reaches
        // them (the C# `using NS;` analog). They stay a member extension in `funs` too (the `import NS.Ext.M` /
        // `using static` analog); the two coexist because a user imports one way or the other, never both at once.
        var csExtFuns = new JsonArray();
            // A Kotlin file-facade ([KotlinFileClass]) -> top-level functions/properties (a `file` decl).
            if (HasKotlinFileClass(t)) { EmitKotlinFileClass(t, files); return; }
            // A .NET enum -> an object whose entries are `val` props typed as the enum itself.
            if (t.IsEnum)
            {
                typeObj["kind"] = "object"; typeObj["name"] = t.Name; typeObj["dotNet"] = t.FullName;
                // #107: declare the self-referential `kotlin.Enum<Self>` supertype so an injected .NET enum satisfies a
                // Kotlin `T : Enum<T>` bound at the frontend (enumValues<TheEnum>() / enumValueOf<TheEnum>() / a generic
                // `<T : Enum<T>>` fn). DOTTED name so ClrTypeInjection.superClassId resolves it directly to kotlin.Enum
                // (it does not consult builtinBoundOpen); the self arg is a lazy lookup-tag cone, same shape as
                // `Money : IComparable<Money>`. No member synthesis needed — name/ordinal/compareTo inherit from kotlin.Enum.
                supers.Add(Ty(new TN.Fqn("kotlin.Enum", new TN[] { new TN.Fqn(t.Name) })));
                typeObj["supers"] = supers;
                foreach (var nm in Enum.GetNames(t))
                    props.Add(PropObj(nm, new TN.Fqn(t.Name), false, new JsonObject(), "public", null, null));
                typeObj["props"] = props;
                types.Add(typeObj);
                Console.WriteLine($"meta: {t.FullName} (enum)");
                return;
            }
            // A .NET interface -> Kotlin can IMPLEMENT it (methods become abstract members). Generic interfaces
            // (`IList`1`) -> simple name + OPEN .NET name + type-parameter tokens, mirroring the class path, so
            // `generic:IList:Foo` resolves to a real `interface IList<T>` (P1-2). `interface <Name> <DotNet> [<TP>...]`.
            if (t.IsInterface)
            {
                // A @Clr-bound stdlib interface keeps its KOTLIN identity (drives namespace/ClassId) and carries the BCL
                // binding separately in `clrBinding` (List -> IReadOnlyList). The .NET name is the TRUE CLR name (backtick arity).
                typeObj["kind"] = "interface"; typeObj["name"] = KotlinName(t);
                typeObj["dotNet"] = t.IsGenericTypeDefinition ? ClrOpenName(t) : t.FullName;
                var iclr = ClrAttrName(t); if (iclr != null) typeObj["clrBinding"] = iclr;
                // Round-trip class-nature: a `fun interface` (SAM) / a `sealed` interface.
                if (HasKotlinFunInterface(t)) typeObj["funInterface"] = true;
                if (HasKotlinSealed(t)) typeObj["sealed"] = true;
                // Round-trip gap ①: declaration-site variance (`out`/`in`) + upper bounds of the interface's type params.
                if (t.IsGenericTypeDefinition)
                {
                    var tps = TypeParamsArr(t.GetGenericArguments(), t, isInterface: true, typeLevel: true);
                    if (tps.Count > 0) typeObj["typeParams"] = tps;
                }
                // Interface->interface supertypes (GENERIC only) so an injected `IList<T>` carries its inherited members.
                foreach (var s in InterfaceSuperTypes(t)) supers.Add(Ty(s));
                if (supers.Count > 0) typeObj["supers"] = supers;
                // (3)/(6): `for (x in it)` over an INTERFACE-typed receiver — the frontend-only iterator marker on
                // `IEnumerable<T>` ITSELF (elem = its own type param 0). Derived interfaces inherit it via the super chain.
                if (t.FullName == "System.Collections.Generic.IEnumerable`1")
                    typeObj["iteratorElem"] = Ty(new TN.Tv("type", 0));
                var iseen = new HashSet<string>();
                var iprops = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.IsSpecialName) continue;
                    // A property accessor (get_size/set_size) not flagged SpecialName -> skip; the `prop` below covers it.
                    if (iprops.Any(p => p.GetMethod == m || p.SetMethod == m)) continue;
                    if (m.IsGenericMethod && !m.IsGenericMethodDefinition) continue;
                    var gp = m.IsGenericMethodDefinition ? m.GetGenericArguments().Select(g => g.Name).ToList() : new List<string>();
                    var ps = m.GetParameters();
                    // DotKt round-trip: an INTERFACE member carries the same no-.NET-analog Kotlin flags a class member does.
                    var k = KotlinFun(m);
                    var retOk = k.suspend ? SuspendRetSupported(m.ReturnType) : Supported(m.ReturnType);
                    if (!ps.All(p => Supported(p.ParameterType)) || !retOk) continue;
                    if (!iseen.Add(m.Name + "<" + string.Join(",", gp) + ">(" + Sig(ps, t) + ")")) continue;
                    var iret = k.suspend ? SuspendRetNode(m, t) : RetTypeSfxN(m, t);
                    var (mmods, mvis) = ModVis(false, isAbstract: true, isOpen: false, infix: k.infix, op: k.op, suspend: k.suspend);
                    funs.Add(FunObj(m.Name, iret, mmods, mvis, ClrAttrName(m),
                        m.IsGenericMethodDefinition ? TypeParamsArr(m.GetGenericArguments(), t, false, false) : null,
                        ParamsArr(ps, t)));
                }
                // Interface properties (Count, IsReadOnly, ...) -> abstract members.
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0 || !Supported(p.PropertyType) || !p.CanRead || !iseen.Add("prop:" + p.Name)) continue;
                    var pclr = p.GetMethod != null ? ClrAttrName(p.GetMethod) : null;   // member substitution: size -> Count
                    props.Add(PropObj(p.Name, PropTypeN(p, t),
                        p.CanWrite, Mods(("abstract", true)), "public", pclr, null));
                }
                // (N6) INTERFACE instance events -> a `ClrEvent<T>` member.
                foreach (var ev in t.GetEvents(BindingFlags.Public | BindingFlags.Instance))
                {
                    var inv = ev.EventHandlerType?.GetMethod("Invoke");
                    if (inv == null || !iseen.Add("event:" + ev.Name)) continue;
                    var eps = inv.GetParameters();
                    if (!eps.All(p => Supported(p.ParameterType)) || !Supported(inv.ReturnType)) continue;
                    events.Add(EventObj(ev, inv, t));
                }
                var iix = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(p => p.GetIndexParameters().Length == 1
                        && Supported(p.GetIndexParameters()[0].ParameterType) && Supported(p.PropertyType));
                if (iix != null)
                    typeObj["indexer"] = new JsonObject {
                        ["indexType"] = Ty(MapT(iix.GetIndexParameters()[0].ParameterType, t)),
                        ["valueType"] = Ty(MapT(iix.PropertyType, t)),
                        ["rw"] = iix.CanWrite };
                if (props.Count > 0) typeObj["props"] = props;
                if (funs.Count > 0) typeObj["funs"] = funs;
                if (events.Count > 0) typeObj["events"] = events;
                types.Add(typeObj);
                Console.WriteLine($"meta: {t.FullName} (interface)");
                return;
            }
            // A .NET attribute type (System.Attribute-derived) -> a Kotlin annotation class, so the author can apply
            // `@TheAttr(args)` on Kotlin declarations and the backend re-applies the real .NET attribute (#54). The
            // longest constructor whose params are all supported defines the annotation's parameters.
            if (IsAttribute(t) && !t.IsAbstract)
            {
                var actor = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                    .Where(c => c.GetParameters().All(p => Supported(p.ParameterType)))
                    .OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
                var aps = actor?.GetParameters() ?? Array.Empty<ParameterInfo>();
                typeObj["kind"] = "annotation"; typeObj["name"] = t.Name; typeObj["dotNet"] = t.FullName;
                ctors.Add(new JsonObject { ["params"] = ParamsArr(aps, t) });
                typeObj["ctors"] = ctors;
                types.Add(typeObj);
                Console.WriteLine($"meta: {t.FullName} (annotation)");
                return;
            }
            var isStatic = t.IsAbstract && t.IsSealed;
            // A generic type definition -> Kotlin simple name (arity-suffixed iff its name family clashes) + the TRUE
            // CLR name (backtick arity) in `dotNet`; the type params carry variance/bounds via `typeParams`. `open` =
            // inheritability (a non-sealed CLR class).
            typeObj["kind"] = isStatic ? "object" : "class";
            typeObj["name"] = KotlinName(t);
            typeObj["dotNet"] = t.IsGenericTypeDefinition ? ClrOpenName(t) : t.FullName;
            if (!isStatic) typeObj["open"] = !t.IsSealed;
            // Round-trip: a Kotlin `sealed` class lowered to a CLR abstract (non-sealed) class.
            if (!isStatic && HasKotlinSealed(t)) typeObj["sealed"] = true;
            // Round-trip gap ①: upper bounds of the class's type params (a CLR class type param has no variance form).
            if (!isStatic && t.IsGenericTypeDefinition)
            {
                var tps = TypeParamsArr(t.GetGenericArguments(), t, isInterface: false, typeLevel: true);
                if (tps.Count > 0) typeObj["typeParams"] = tps;
            }
            // (1)(2) Supertypes: emit the injectable base class + interfaces so subtype assignability and inherited-
            // member access hold. Members declared in the contiguous injectable base chain ("covered") arrive via
            // those supertypes, so we skip re-declaring them (avoids fake-override clashes). `IM` includes protected.
            var covered = isStatic ? new HashSet<string>() : CoveredAncestors(t);
            const BindingFlags IM = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            if (!isStatic)
            {
                // Base class (assignability + inherited/protected members) + the interfaces this class fully and
                // publicly implements (assignable to an interface parameter, e.g. `Circle` -> `IShape`). The base is first.
                foreach (var s in SuperTypes(t).Concat(ClassInterfaceSuperTypes(t))) supers.Add(Ty(s));
                if (supers.Count > 0) typeObj["supers"] = supers;
                // The base edge is emitted for assignability even when the base has no accessible no-arg ctor (WinUI
                // UIElement, SafeHandle). `baseNoArgCtor=false` tells the injector NOT to synthesize a `: super()` call.
                if (t.BaseType != null && EmittableBase(t.BaseType) && !HasAccessibleNoArgCtor(t.BaseType))
                    typeObj["baseNoArgCtor"] = false;
            }
            var seen = new HashSet<string>();
            var accessorMembers = new HashSet<string>();   // get_/set_ method names surfaced as `prop` (skip in the fun loop)
            bool Covered(MemberInfo m) => m.DeclaringType?.FullName != null && covered.Contains(m.DeclaringType.FullName);
            if (!isStatic)
            {
                // Constructors: include protected ones so a Kotlin subclass can chain to a `protected` base ctor.
                foreach (var c in t.GetConstructors(IM))
                {
                    if (Vis(c) == null) continue;
                    var ps = c.GetParameters();
                    if (!ps.All(p => Supported(p.ParameterType))) continue;
                    if (!seen.Add("ctor(" + Sig(ps, t) + ")")) continue;
                    ctors.Add(new JsonObject { ["params"] = ParamsArr(ps, t) });
                }
                // Instance properties (non-indexer).
                foreach (var p in t.GetProperties(IM))
                {
                    if (Covered(p) || p.GetIndexParameters().Length > 0 || !Supported(p.PropertyType)) continue;
                    var prot = Vis(p.GetMethod); if (prot == null) continue;
                    if (!p.CanRead || !seen.Add("prop:" + p.Name)) continue;
                    var get = p.GetMethod;
                    var virt = (get?.IsVirtual ?? false) && !(get?.IsFinal ?? false);
                    var (pm, pv) = ModVis(prot.Value, get?.IsAbstract ?? false, virt);
                    props.Add(PropObj(p.Name, PropTypeN(p, t),
                        p.CanWrite, pm, pv, null, null));
                }
                // DotKt round-trip: a Kotlin property's BACKING FIELD -> a plain public field; a CUSTOM-ACCESSOR property
                // -> get_X/set_X methods. Surface both as `prop`s. `accessorMembers` keeps get_/set_ out of the fun loop.
                accessorMembers.Clear();
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (Covered(f) || !Supported(f.FieldType) || !seen.Add("prop:" + f.Name)) continue;
                    var rw = !(f.IsInitOnly || IsKotlinReadOnly(f));
                    props.Add(PropObj(f.Name, FieldTypeN(f, t), rw, new JsonObject(), "public", null, null));
                }
                foreach (var g in t.GetMethods(IM))
                {
                    if (g.IsSpecialName || !g.Name.StartsWith("get_") || g.GetParameters().Length != 0) continue;
                    if (Covered(g) || Vis(g) == null || !Supported(g.ReturnType)) continue;
                    var pn = g.Name.Substring(4);
                    if (!seen.Add("prop:" + pn)) continue;
                    var setter = t.GetMethods(IM).FirstOrDefault(m => !m.IsSpecialName && m.Name == "set_" + pn && m.GetParameters().Length == 1 && Vis(m) != null);
                    accessorMembers.Add(g.Name); if (setter != null) accessorMembers.Add(setter.Name);
                    var (pm, pv) = ModVis(Vis(g).Value, g.IsAbstract, g.IsVirtual && !g.IsFinal);
                    props.Add(PropObj(pn, RetTypeSfxN(g, t), setter != null, pm, pv, null, null));
                }
                // MEMBER extension properties (`class C { val T.p get() }`): accessors get_X(__self)/set_X(__self, v).
                foreach (var g in t.GetMethods(IM))
                {
                    if (g.IsSpecialName || !g.Name.StartsWith("get_")) continue;
                    var gps = g.GetParameters();
                    if (gps.Length != 1 || gps[0].Name != "__self" || !Supported(g.ReturnType) || !Supported(gps[0].ParameterType)) continue;
                    var prot = Vis(g); if (prot == null) continue;
                    var pn = g.Name.Substring(4);
                    if (!seen.Add("prop:" + pn)) continue;
                    var setter = t.GetMethods(IM).FirstOrDefault(m => !m.IsSpecialName && m.Name == "set_" + pn
                        && m.GetParameters().Length == 2 && m.GetParameters()[0].Name == "__self" && Vis(m) != null);
                    accessorMembers.Add(g.Name); if (setter != null) accessorMembers.Add(setter.Name);
                    memberExtProps.Add(PropObj(pn, RetTypeSfxN(g, t), setter != null, new JsonObject(),
                        prot.Value ? "protected" : "public", null, MapT(gps[0].ParameterType, t)));
                }
                // Events (I4).
                foreach (var ev in t.GetEvents(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (Covered(ev)) continue;
                    var inv = ev.EventHandlerType?.GetMethod("Invoke");
                    if (inv == null || !seen.Add("event:" + ev.Name)) continue;
                    var ps = inv.GetParameters();
                    if (!ps.All(p => Supported(p.ParameterType)) || !Supported(inv.ReturnType)) continue;
                    events.Add(EventObj(ev, inv, t));
                }
                // Indexer (`this[i]`) -> `{indexType, valueType, rw}`; the injector synthesizes operator get/set.
                var ix = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(p => p.GetIndexParameters().Length == 1
                        && Supported(p.GetIndexParameters()[0].ParameterType) && Supported(p.PropertyType));
                if (ix != null)
                    typeObj["indexer"] = new JsonObject {
                        ["indexType"] = Ty(MapT(ix.GetIndexParameters()[0].ParameterType, t)),
                        ["valueType"] = Ty(MapT(ix.PropertyType, t)),
                        ["rw"] = ix.CanWrite };
                // IEnumerable<T> -> a frontend-only `operator fun iterator(): Iterator<T>`.
                Type ienum = null;
                try { ienum = t.GetInterfaces().FirstOrDefault(i => i.IsGenericType
                    && i.GetGenericTypeDefinition().FullName == "System.Collections.Generic.IEnumerable`1"
                    && Supported(i.GetGenericArguments()[0])); } catch { }
                if (ienum != null)
                    typeObj["iteratorElem"] = Ty(MapT(ienum.GetGenericArguments()[0], t));
                // Public STATIC members of a NORMAL class -> companion-object members (App.Start / App.Current).
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                    if (Supported(f.FieldType) && seen.Add("sfield:" + f.Name))
                        staticProps.Add(PropObj(f.Name, FieldTypeN(f, t), false, new JsonObject(), "public", null, null));
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Static))
                    if (p.GetIndexParameters().Length == 0 && Supported(p.PropertyType) && p.CanRead && seen.Add("sprop:" + p.Name))
                        staticProps.Add(PropObj(p.Name, PropTypeN(p, t),
                            p.CanWrite, new JsonObject(), "public", null, null));
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    // A generic METHOD DEFINITION (`Task.FromResult<T>`/`Task.Run<T>`) is surfaced so Kotlin can BUILD a
                    // `Task<T>` (async interop §②); a CONSTRUCTED generic static is skipped.
                    // #143: skip the INHERITED System.Object statics (ReferenceEquals, Equals(o,o)) by declaring type,
                    // NOT by name — a type that DECLARES its own static `GetHashCode(object)` (RuntimeHelpers) is distinct.
                    if (m.IsSpecialName || m.DeclaringType?.FullName == "System.Object" || (m.IsGenericMethod && !m.IsGenericMethodDefinition)) continue;
                    var sgp = m.IsGenericMethodDefinition ? m.GetGenericArguments().Select(g => g.Name).ToList() : new List<string>();
                    var sps = m.GetParameters();
                    if (!sps.All(p => Supported(p.ParameterType)) || !Supported(m.ReturnType)) continue;
                    if (!seen.Add("sm:" + m.Name + "<" + string.Join(",", sgp) + ">(" + Sig(sps, t) + ")")) continue;
                    // #135: route the return through RetTypeSfxN (Nothing marker + NRT + ext-recv restore) — the same
                    // reader the instance/interface/top-level loops use — instead of raw MapRetT, so a companion-static
                    // `fun g(): Nothing` round-trips and NRT folds on companion statics.
                    staticFuns.Add(FunObj(m.Name, RetTypeSfxN(m, t), new JsonObject(), "public", null,
                        m.IsGenericMethodDefinition ? TypeParamsArr(m.GetGenericArguments(), t, false, false) : null,
                        ParamsArr(sps, t)));
                }
                // (N6) Public STATIC events of a NORMAL class -> a companion `ClrEvent<T>` property.
                foreach (var ev in t.GetEvents(BindingFlags.Public | BindingFlags.Static))
                {
                    var inv = ev.EventHandlerType?.GetMethod("Invoke");
                    if (inv == null || !seen.Add("sevent:" + ev.Name)) continue;
                    var eps = inv.GetParameters();
                    if (!eps.All(p => Supported(p.ParameterType)) || !Supported(inv.ReturnType)) continue;
                    staticEvents.Add(EventObj(ev, inv, t));
                }
            }
            else
            {
                // Static fields/consts (e.g. Math.PI) and static properties -> read-only `prop`s on the object.
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!Supported(f.FieldType) || !seen.Add("sfield:" + f.Name)) continue;
                    props.Add(PropObj(f.Name, FieldTypeN(f, t), false, new JsonObject(), "public", null, null));
                }
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Static))
                {
                    if (p.GetIndexParameters().Length > 0 || !Supported(p.PropertyType) || !p.CanRead || !seen.Add("sprop:" + p.Name)) continue;
                    props.Add(PropObj(p.Name, PropTypeN(p, t),
                        p.CanWrite, new JsonObject(), "public", null, null));
                }
                // (N6) Public STATIC events of a STATIC class (`Console.CancelKeyPress`) -> a `ClrEvent<T>` member of the object.
                foreach (var ev in t.GetEvents(BindingFlags.Public | BindingFlags.Static))
                {
                    var inv = ev.EventHandlerType?.GetMethod("Invoke");
                    if (inv == null || !seen.Add("event:" + ev.Name)) continue;
                    var eps = inv.GetParameters();
                    if (!eps.All(p => Supported(p.ParameterType)) || !Supported(inv.ReturnType)) continue;
                    events.Add(EventObj(ev, inv, t));
                }
            }
            var flags = BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            foreach (var m in t.GetMethods(flags))
            {
                if (m.IsSpecialName) continue;
                if (accessorMembers.Contains(m.Name)) continue;   // already surfaced as a `prop`
                // #143: the OBJECT_MEMBERS name-skip is for the Kotlin `Any` INSTANCE members (equals/hashCode/toString).
                // A STATIC method never collides with those — e.g. `RuntimeHelpers.GetHashCode(object)` must surface; the
                // inherited Object STATICS (ReferenceEquals, Equals(o,o)) are dropped by the DeclaringType guard below.
                if (!m.IsStatic && OBJECT_MEMBERS.Contains(m.Name)) continue;
                if (m.DeclaringType?.FullName == "System.Object") continue;
                if (Covered(m)) continue;                 // arrives via an injected supertype
                var prot = Vis(m); if (prot == null) continue;   // skip private/internal; keep public + protected
                if (m.IsGenericMethod && !m.IsGenericMethodDefinition) continue;
                var ps = m.GetParameters();
                // DotKt round-trip: a `suspend fun` is emitted returning Task<T>; restore the result type T and gate on it.
                var k = KotlinFun(m);
                var retOk = k.suspend ? SuspendRetSupported(m.ReturnType) : Supported(m.ReturnType);
                if (!ps.All(p => Supported(p.ParameterType)) || !retOk) continue;
                var gp = m.IsGenericMethodDefinition ? m.GetGenericArguments().Select(g => g.Name).ToList() : new List<string>();
                if (!seen.Add(m.Name + "<" + string.Join(",", gp) + ">(" + Sig(ps, t) + ")")) continue;
                var virt = m.IsVirtual && !m.IsFinal;
                var retNode = k.suspend ? SuspendRetNode(m, t) : RetTypeSfxN(m, t);
                // A fresh typeParams array per emission — a JsonNode can hold only one parent, so a C#-ext method
                // (emitted BOTH as an object member AND a top-level fun below) must NOT share one array across the calls.
                JsonArray Tps() => m.IsGenericMethodDefinition ? TypeParamsArr(m.GetGenericArguments(), t, false, false) : null;
                // An extension function -> first param is the receiver: a DotKt MEMBER extension (`class C { fun T.f() }`)
                // names it `__self`; a C#-origin `[Extension]` static (`static int Twice(this W w)`) names it for real.
                // Either way `ext:true` keeps it a member extension, reachable via `import Owner.f` (the C# `using static`
                // analog). `inline` carries the spliceable body (composes with suspend/generic).
                var csExt = ps.Length > 0 && ps[0].Name != "__self" && IsExtensionMethod(m);
                var isExt = ps.Length > 0 && (ps[0].Name == "__self" || csExt);
                var (mm, mv) = ModVis(prot.Value, m.IsAbstract, virt, infix: k.infix, op: k.op, suspend: k.suspend,
                    inline: KotlinInlineBody(m) != null, ext: isExt);
                funs.Add(FunObj(m.Name, retNode, mm, mv, GenuineNet(t) ? m.Name : null,   // identity BCL-member binding
                    Tps(), ParamsArr(ps, t)));
                // A C#-origin `[Extension]` static ALSO surfaces as a TOP-LEVEL extension fun in a `file` decl (below),
                // so `import NS.*` (the Kotlin analog of C# `using NS;`) brings it into scope — the whole Avalonia fluent
                // startup/render surface is namespace-imported extension methods (UsePlatformDetect/…). #137. A byref
                // receiver (`this ref W`/`this in W`, a struct ref-extension) is EXCLUDED from the top-level surface —
                // its receiver would map to a `ClrRef<T>`, not sensibly a `w.M()` call target (it stays a member ext).
                if (csExt && !ps[0].ParameterType.IsByRef)
                {
                    var (em, ev) = ModVis(prot.Value, false, false, infix: k.infix, op: k.op, suspend: k.suspend,
                        inline: KotlinInlineBody(m) != null, ext: true);
                    csExtFuns.Add(FunObj(m.Name, retNode, em, ev, null, Tps(), ParamsArr(ps, t)));
                }
            }
            // .NET OPERATORS (`op_Addition`/…) are STATIC methods -> Kotlin `operator fun`s on a genuine .NET type: the
            // LEFT operand is the receiver, so a binary op drops param[0] and a unary op has no value params. `clrName =
            // op_*` re-prepends the receiver + emits the static call. Only when param[0] IS the declaring type.
            if (!isStatic && GenuineNet(t))
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!m.IsSpecialName || !OPERATOR_NAMES.TryGetValue(m.Name, out var kop)) continue;
                    var unary = UNARY_OPERATORS.Contains(kop);
                    var ps = m.GetParameters();
                    var p0 = ps.Length > 0 ? ps[0].ParameterType : null;
                    var p0def = p0 != null && p0.IsGenericType ? p0.GetGenericTypeDefinition() : p0;
                    if (ps.Length != (unary ? 1 : 2) || p0def == null || (p0def != t && p0def.FullName != t.FullName)) continue;
                    if (!ps.All(p => Supported(p.ParameterType)) || !Supported(m.ReturnType)) continue;
                    var vps = unary ? Array.Empty<ParameterInfo>() : ps.Skip(1).ToArray();
                    if (!seen.Add("op:" + kop + "(" + Sig(vps, t) + ")")) continue;
                    funs.Add(FunObj(kop, MapRetT(m.ReturnType, t), Mods(("operator", true)), "public", m.Name, null, ParamsArr(vps, t)));
                }
            // (explicit impl) Concrete stubs for in-scope members of the generic interfaces this class implements but
            // doesn't expose PUBLICLY, so the injected class satisfies `ICollection<T>`/`IList<T>` with no abstract member left.
            if (!isStatic) EmitExplicitInterfaceStubs(t, props, funs, seen);
            // Commit the type: attach only the non-empty sub-arrays (the consumer defaults absent = empty).
            if (ctors.Count > 0) typeObj["ctors"] = ctors;
            if (props.Count > 0) typeObj["props"] = props;
            if (funs.Count > 0) typeObj["funs"] = funs;
            if (memberExtProps.Count > 0) typeObj["memberExtProps"] = memberExtProps;
            if (events.Count > 0) typeObj["events"] = events;
            if (staticFuns.Count > 0) typeObj["staticFuns"] = staticFuns;
            if (staticProps.Count > 0) typeObj["staticProps"] = staticProps;
            if (staticEvents.Count > 0) typeObj["staticEvents"] = staticEvents;
            types.Add(typeObj);
            // C#-origin `[Extension]` methods -> a `file` decl (pkg = the static class's namespace, fileClass = its
            // FullName). The consumer restores each as a top-level extension fun and routes the call to the static
            // method on this class (`NS.Ext.M(recv, …)`) — same shape as a DotKt [KotlinFileClass] top-level extension.
            if (csExtFuns.Count > 0)
                files.Add(new JsonObject
                {
                    ["pkg"] = string.IsNullOrEmpty(t.Namespace) ? "" : t.Namespace,
                    ["fileClass"] = t.FullName,
                    ["funs"] = csExtFuns
                });
            Console.WriteLine($"meta: {t.FullName} ({(isStatic ? "object" : "class")})");
    }

    static void EmitExplicitInterfaceStubs(Type t, JsonArray props, JsonArray funs, HashSet<string> seen)
    {
        var pubProps = new HashSet<string>();
        try { foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)) if (p.GetIndexParameters().Length == 0) pubProps.Add(p.Name); } catch { }
        var done = new HashSet<string>();
        foreach (var i in SatisfiableInterfaces(t))   // exactly the interfaces we emit edges for
        {
            if (!done.Add(i.FullName ?? i.Name)) continue;
            try
            {
                foreach (var p in i.GetProperties())
                {
                    if (p.GetIndexParameters().Length > 0 || !p.CanRead || !Supported(p.PropertyType)) continue;
                    if (pubProps.Contains(p.Name) || !seen.Add("prop:" + p.Name)) continue;
                    props.Add(PropObj(p.Name, PropTypeN(p, t),
                        p.CanWrite, new JsonObject(), "public", null, null));
                }
                foreach (var m in i.GetMethods())
                {
                    if (m.IsSpecialName || m.IsStatic || m.IsGenericMethod) continue;
                    var ps = m.GetParameters();
                    if (!ps.All(x => Supported(x.ParameterType)) || !Supported(m.ReturnType)) continue;
                    if (!seen.Add(m.Name + "<>(" + Sig(ps, t) + ")")) continue;   // already a public member -> skip
                    funs.Add(FunObj(m.Name, MapRetT(m.ReturnType, t), new JsonObject(), "public", null, null, ParamsArr(ps, t)));
                }
            }
            catch { }
        }
    }

    // Walk the base chain by FullName (LoadFrom'd reference assemblies give System.Attribute a different identity
    // than the runtime's typeof, so `typeof(Attribute).IsAssignableFrom` would miss — see Map's I2 note).
    static bool IsAttribute(Type t)
    {
        for (var b = t; b != null; b = b.BaseType) if (b.FullName == "System.Attribute") return true;
        return false;
    }

    // A .NET delegate type (System.MulticastDelegate-derived) -> surfaced as a Kotlin function type (item 4).
    static bool IsDelegate(Type t)
    {
        for (var b = t; b != null; b = b.BaseType) if (b.FullName == "System.MulticastDelegate") return true;
        return false;
    }

    // (6) closure: the types `t`'s API surface references — base class, implemented interfaces, and the
    // return/parameter/element types of its public members. Over-collects (ignores Supported) so the injected
    // closure is a SUPERSET of what the emitted metadata cross-references; extra types are harmless. Mirrors the
    // emission's `System.Object`-member skip — otherwise `GetType()` -> System.Type drags in the whole reflection
    // graph and the closure explodes.
    static IEnumerable<Type> ReferencedTypes(Type t)
    {
        if (t.BaseType != null) yield return t.BaseType;
        Type[] ifaces; try { ifaces = t.GetInterfaces(); } catch { ifaces = Array.Empty<Type>(); }
        foreach (var i in ifaces) yield return i;
        const BindingFlags PUB = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        ConstructorInfo[] ctors; try { ctors = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance); } catch { ctors = Array.Empty<ConstructorInfo>(); }
        foreach (var c in ctors) foreach (var p in c.GetParameters()) yield return p.ParameterType;
        PropertyInfo[] props; try { props = t.GetProperties(PUB); } catch { props = Array.Empty<PropertyInfo>(); }
        foreach (var p in props) { yield return p.PropertyType; foreach (var ip in p.GetIndexParameters()) yield return ip.ParameterType; }
        EventInfo[] evs; try { evs = t.GetEvents(PUB); } catch { evs = Array.Empty<EventInfo>(); }
        foreach (var e in evs) { var inv = e.EventHandlerType?.GetMethod("Invoke"); if (inv != null) { yield return inv.ReturnType; foreach (var p in inv.GetParameters()) yield return p.ParameterType; } }
        MethodInfo[] ms; try { ms = t.GetMethods(PUB); } catch { ms = Array.Empty<MethodInfo>(); }
        foreach (var m in ms)
        {
            // #143: mirror the emission narrowing (line 786) — a STATIC member (e.g. RuntimeHelpers.GetHashCode) now
            // surfaces, so its referenced types MUST be enqueued here too, else the closure omits a type reachable ONLY
            // through it -> the "unresolved reference" class this fix targets, reintroduced one layer down.
            if (m.IsSpecialName || (!m.IsStatic && OBJECT_MEMBERS.Contains(m.Name)) || m.DeclaringType?.FullName == "System.Object") continue;
            yield return m.ReturnType;
            foreach (var p in m.GetParameters()) yield return p.ParameterType;
        }
    }

    // Strip byref/array/pointer wrappers; for a constructed generic, yield its open definition AND each type
    // argument (recursively). A bare type yields itself.
    static IEnumerable<Type> Unwrap(Type t)
    {
        while (t.IsByRef || t.IsArray || t.IsPointer) { t = t.GetElementType(); if (t == null) yield break; }
        if (t.IsGenericType)
        {
            yield return t.GetGenericTypeDefinition();
            foreach (var a in t.GetGenericArguments()) foreach (var u in Unwrap(a)) yield return u;
        }
        else yield return t;
    }

    // Kotlin-builtin scalars (mapped directly by Map) + intrinsics surfaced specially (Span) + the special CLR base
    // types (Delegate/MulticastDelegate/ValueType/Enum/Array) that can't be injected as ordinary classes nor used as
    // supertypes — synthesizing a subclass ctor chaining to e.g. System.Delegate (no parameterless ctor) crashes
    // codegen. Members referencing them degrade to Any?, exactly as if never reached. `System.Nullable`1` joins them:
    // a value-type `X?` is projected to Kotlin's `X?` by Map (never the literal `Nullable<X>` generic), so injecting
    // the open `Nullable`1` definition is pointless AND surfaces a stray non-Kotlin generic (mirrors `Span`1`).
    static readonly HashSet<string> NO_INJECT = new()
    { "System.Void", "System.Object", "System.String", "System.Int32", "System.Int64", "System.Int16",
      "System.Byte", "System.Boolean", "System.Double", "System.Single", "System.Char", "System.Span`1",
      "System.Nullable`1",
      "System.Delegate", "System.MulticastDelegate", "System.ValueType", "System.Enum", "System.Array" };

    // BINDING INVARIANT (CLAUDE.md §"kotlin.* comes from the JAR, never from facadegen"; docs/ship-tasks.md §0):
    // kotc resolves the ENTIRE Kotlin stdlib (`kotlin.*`) from the frontend JAR on -classpath, which preserves full
    // Kotlin semantics (the Companion-object call sites the stdlib is premised on). facadegen owns the .NET space ONLY
    // and must NEVER inject a `kotlin.*` symbol — a facadegen-reconstructed `kotlin.*` DUPLICATES the jar's, which then
    // conflict (overload-resolution ambiguity), and re-scanning the whole stdlib is slower than the prebuilt jar.
    // facadegen resolves only explicit .NET type names + the PSI import-list (all .NET-space names), so `kotlin.*`
    // never enters a seed/closure; this predicate makes the guarantee live IN the owning layer (defense-in-depth,
    // output-neutral) rather than relying only on that plus the downstream `ClrTypeInjection.kt` filter (which
    // covers injected classes/interfaces but NOT top-level functions).
    // WHITELIST: the deliberate `kotlin.clr.await` CLR-async bridge is surfaced textually by EmitAwaitables (keyed off
    // the awaitable PATTERN), never through this type-injection path (`import kotlin.clr.await` resolves to no type here),
    // so it is naturally exempt — this predicate only gates types that flow through Enqueue/ShouldInject/seed-resolve.
    static bool IsKotlinStdlibSymbol(Type t)
    {
        var fqn = t.FullName ?? t.Namespace ?? "";
        return fqn.StartsWith("kotlin.") || (t.Namespace ?? "") == "kotlin";
    }

    // Inject only real, named, resolvable types — including generic DEFINITIONS (List`1, IList`1) so a
    // `generic:List:Foo` member type resolves to a real `class List<T>` (P1-2). Constructed generics are unwrapped
    // to (open def + args) by [Unwrap] before this check.
    // #68: a compiler-generated type (a DotKt closure/ref-cell/KProperty/CharSequence/ClrH helper, or any BCL generated
    // type) is skipped by its STANDARD [CompilerGenerated] attribute — never by `dotkt$` name-sniffing. Every such type
    // now carries the attribute (kotc/bir2cir flag `generated:true` -> ilemit stamps it; ilemit stamps its own synthetics
    // too), so this is the primary skip; the empty-namespace guard below is belt-and-suspenders. MetadataLoadContext-safe.
    static bool IsCompilerGenerated(Type t)
    {
        try { return t.GetCustomAttributesData().Any(c => c.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute"); }
        catch { return false; }
    }

    static bool ShouldInject(Type t)
    {
        if (t == null || t.IsGenericParameter || t.IsPointer || t.IsByRef) return false;
        if (IsCompilerGenerated(t)) return false;
        if (string.IsNullOrEmpty(t.Namespace) || t.FullName == null) return false;
        // BINDING INVARIANT: never inject a `kotlin.*` stdlib symbol — it comes from the frontend JAR (see
        // IsKotlinStdlibSymbol). Defense-in-depth: the closure never reaches one (facadegen resolves only .NET-space
        // names), so this is output-neutral; it just moves the guarantee into the owning layer.
        if (IsKotlinStdlibSymbol(t)) return false;
        // A NESTED generic type (`List`1+Enumerator` — it inherits the enclosing type's params, so IsGenericType is
        // true even without own params) has no CLR-addressable open name in the meta grammar; excluded, and CrossType
        // degrades references to it to Any?. Non-generic nested types (Environment+SpecialFolder) stay injectable.
        if (t.IsNested && t.IsGenericType) return false;
        return !NO_INJECT.Contains(t.FullName);
    }

    // (1) A type usable as an injected supertype: a real, non-generic, co-injectable class/interface (not Object).
    // Generic bases/interfaces (`Bar<int>`) wait for P1-2; until then `t` keeps the inherited members flattened.
    static bool IsInjectableSupertype(Type t) =>
        t != null && !t.IsGenericType && !string.IsNullOrEmpty(t.Namespace) && t.FullName != null
        && t.FullName != "System.Object" && !NO_INJECT.Contains(t.FullName);

    // A base CLASS supertype edge is emitted purely for ASSIGNABILITY (is-a): e.g. a WinUI `TextBlock` must be usable
    // where `UIElement` is expected. This is INDEPENDENT of whether the base is constructible — injected façade
    // instances come from .NET (method returns etc.), and `Foo()` lowers to native `new Foo()` (newClr, which chains
    // base ctors internally), so no Kotlin `: Base()` super-chain is synthesized for injected types. (A non-activatable
    // WinRT base like `UIElement` has no no-arg ctor; requiring one here dropped the FrameworkElement->UIElement edge
    // and broke assignability for every control below it — feedback item 1.)
    static bool EmittableBase(Type t) => IsInjectableSupertype(t);

    // Whether [t] has an accessible parameterless ctor — used only to decide if an injected subclass may synthesize a
    // `: super()` delegating call (NOT to gate the assignability edge; see EmittableBase / feedback item 1).
    static bool HasAccessibleNoArgCtor(Type t)
    {
        try { return t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Any(c => c.GetParameters().Length == 0 && (c.IsPublic || c.IsFamily || c.IsFamilyOrAssembly)); }
        catch { return false; }
    }

    static string SimpleName(Type t) => t.Name.Contains('`') ? t.Name.Substring(0, t.Name.IndexOf('`')) : t.Name;

    // OPEN .NET name of a generic type definition: namespace + simple name, WITHOUT the `<arity> suffix. Used as the
    // ARITY FAMILY key (HasArityClash); the meta's .NET-name token itself is ClrOpenName (arity-qualified). For a
    // ROOT-namespace type `t.Namespace` is null, and `null + "." + "Box"` would yield the broken `.Box`.
    static string OpenName(Type t) => string.IsNullOrEmpty(t.Namespace) ? SimpleName(t) : t.Namespace + "." + SimpleName(t);

    // The CLR metadata name of a generic type definition (`System.Threading.Tasks.Task`1`): namespace + CLR simple
    // name INCLUDING the backtick arity. The meta's .NET-name token carries this TRUE name, so `Task` and `Task`1`
    // are never ambiguous in the format. Consumers derive the arity-less open form by stripping at '`' (the kotc
    // injector registers that open form for the backend, which re-appends the arity from the constructed args).
    static string ClrOpenName(Type t) => string.IsNullOrEmpty(t.Namespace) ? t.Name : t.Namespace + "." + t.Name;

    // ---- arity-family Kotlin naming ----------------------------------------------------------------------------
    // .NET permits a non-generic type and generic definitions that differ only by arity in ONE namespace
    // (Task / Task`1, TaskCompletionSource / TaskCompletionSource`1, Tuple / Tuple`1..`8, Func`1..`17). A Kotlin
    // classifier is keyed by (package, simpleName) — it CANNOT be overloaded by type-argument count — so injecting
    // both under one name silently dropped one (ClassId last-wins in the injector). Kotlin-side naming rule
    // (kotlin.Function0/Function1/... arity-suffix precedent): the family's NON-generic member keeps the plain
    // simple name; when the (namespace, simpleName) family has MORE THAN ONE member, each GENERIC definition is
    // Kotlin-named `<Simple><arity>` (Task1<TResult>, Func2<T,R>). A SINGLETON generic family (List`1 — no
    // non-generic List anywhere) keeps the plain name. The family is computed against the LOADED REFERENCE
    // UNIVERSE (Resolve probes), NOT the emitted closure, so a type's Kotlin name is stable under import-set
    // changes (the BCL is always in the universe: Task`1 is ALWAYS Task1).
    static readonly Dictionary<string, bool> ARITY_CLASH = new();   // key = arity-less "Namespace.Simple"
    static bool HasArityClash(Type t)
    {
        var baseName = OpenName(t);
        if (ARITY_CLASH.TryGetValue(baseName, out var clash)) return clash;
        var members = Resolve(baseName) != null ? 1 : 0;
        for (var n = 1; n <= 17 && members < 2; n++)
            if (Resolve(baseName + "`" + n) != null) members++;
        return ARITY_CLASH[baseName] = members > 1;
    }

    // The Kotlin-visible simple name of a .NET type: plain for a non-generic / singleton-family generic;
    // `<Simple><arity>` for a generic definition (or constructed generic's definition) in a clashing family.
    static string KotlinName(Type t)
    {
        var simple = SimpleName(t);
        if (!t.IsGenericType && !t.IsGenericTypeDefinition) return simple;
        var def = t.IsGenericTypeDefinition ? t : t.GetGenericTypeDefinition();
        if (!HasArityClash(def)) return simple;
        var named = simple + def.GetGenericArguments().Length;
        // Insurance: a REAL .NET type already named `<Simple><arity>` in this namespace would collide with the
        // synthesized Kotlin name — surface it loudly instead of silently shadowing (never seen in the BCL).
        if (Resolve((string.IsNullOrEmpty(def.Namespace) ? "" : def.Namespace + ".") + named) != null)
            Console.Error.WriteLine($"warning: arity-qualified Kotlin name {named} collides with a real .NET type in {def.Namespace}");
        return named;
    }

    // The contiguous run of base classes from t.BaseType upward whose supertype edge IS emitted — members declared
    // there reach `t` through the injected supertype chain, so `t` must NOT re-declare them (fake-override clash).
    // The chain stops at the first ancestor we don't link (Object, a generic base, or a base with no no-arg ctor);
    // members above the break are still flattened onto `t` so nothing is lost. Mirrors [SuperTypes] exactly.
    static HashSet<string> CoveredAncestors(Type t)
    {
        var set = new HashSet<string>();
        for (var b = t.BaseType; b != null && EmittableBase(b); b = b.BaseType) set.Add(b.FullName!);
        return set;
    }

    // Interface->interface supertypes for an injected interface: its DIRECT, GENERIC interfaces, encoded as
    // `generic:Open:args` (via Map). Non-generic interfaces are skipped — they'd reintroduce the non-generic
    // GetEnumerator/etc. alongside the generic one (an overload clash). Interface inheritance imposes no member-
    // satisfaction obligation, so (unlike class-implements-interface) this is safe.
    static List<TN> InterfaceSuperTypes(Type t)
    {
        Type[] all; try { all = t.GetInterfaces(); } catch { return new List<TN>(); }
        var implied = new HashSet<Type>();
        foreach (var i in all) { try { foreach (var sub in i.GetInterfaces()) implied.Add(sub); } catch { } }
        var supers = new List<TN>(); var seen = new HashSet<string>();
        foreach (var i in all)
        {
            if (implied.Contains(i) || !i.IsGenericType) continue;   // direct + generic only
            var node = MapT(i, t);                                    // a constructed generic -> Fqn with args
            if (IsGenericFqn(node) && seen.Add(TN.ToJson(node))) supers.Add(node);
        }
        return supers;
    }

    // Interface supertypes a CLASS can safely declare: each DIRECT interface whose entire (transitive) member set is
    // matched by a public, Supported, EXACT-RETURN member of the class. Declaring `C : I` makes Kotlin require C to
    // implement all of I's abstract members, so we only link interfaces C fully and publicly provides — this emits
    // the normal `Circle : IShape` (assignability to an interface parameter) while safely skipping interfaces C
    // implements EXPLICITLY (non-public targets) or with a COVARIANT return (e.g. `List<T>.GetEnumerator(): Enumerator`
    // vs `IEnumerable<T>.GetEnumerator(): IEnumerator<T>`), which Kotlin would reject as unimplemented/mismatched.
    // The interfaces `c` can be declared to implement: injectable, no generic interface method, and — for a NON-generic
    // interface — not shadowed by a same-named generic one (so we drop the legacy `IList`/`ICollection`/`IEnumerable`
    // that sit alongside `IList<T>` etc. and only bring `object`-typed members). Self-referential generic interfaces
    // (`Money : IComparable<Money>`, the BCL value-type norm) ARE emitted — the injector resolves the self-argument
    // via a lazy lookup-tag cone, so it no longer recurses into the type being built.
    static List<Type> SatisfiableInterfaces(Type c)
    {
        Type[] all; try { all = c.GetInterfaces(); } catch { return new List<Type>(); }
        var genericNames = new HashSet<string>(all.Where(x => x.IsGenericType).Select(x => SimpleName(x.GetGenericTypeDefinition())));
        var res = new List<Type>();
        foreach (var i in all)
        {
            var openi = i.IsGenericType ? i.GetGenericTypeDefinition() : i;
            if (string.IsNullOrEmpty(openi.Namespace) || NO_INJECT.Contains(openi.FullName ?? "")
                || !SimpleName(openi).All(ch => char.IsLetterOrDigit(ch) || ch == '_')) continue;
            if (!i.IsGenericType && genericNames.Contains(SimpleName(i))) continue;   // legacy non-generic shadow
            if (ClassSatisfies(c, i)) res.Add(i);
        }
        return res;
    }

    static List<TN> ClassInterfaceSuperTypes(Type c)
    {
        // The MAXIMAL set of interfaces `c` satisfies. We consider ALL implemented interfaces, not just "direct" ones,
        // so e.g. `List<T>` links every interface it implements (`IList<T>`, `IReadOnlyList<T>`, ...); the explicit
        // members (`IsReadOnly`) are filled by EmitExplicitInterfaceStubs.
        var sat = SatisfiableInterfaces(c);
        // Drop any satisfiable interface implied by another satisfiable one (avoid redundant supertype edges).
        var implied = new HashSet<Type>();
        foreach (var i in sat) { try { foreach (var s in i.GetInterfaces()) implied.Add(s); } catch { } }
        var supers = new List<TN>(); var seen = new HashSet<string>();
        foreach (var i in sat)
        {
            if (implied.Contains(i)) continue;
            var node = i.IsGenericType ? MapT(i, c) : new TN.Fqn(SimpleName(i));
            if (IsAnyQ(node) || (i.IsGenericType && !IsGenericFqn(node))) continue;
            if (seen.Add(TN.ToJson(node))) supers.Add(node);
        }
        return supers;
    }

    // Whether class `c` can declare it implements interface `i`. `c : I` in .NET GUARANTEES `c` implements every
    // member of I (public or explicit): EmitExplicitInterfaceStubs emits the non-public ones, and fir2ir fake-overrides
    // the rest — INCLUDING generic interface methods (`U Convert<U>(object)`), which the frontend declares with their
    // own method type parameters. So the only failure is `i` not being reflectable; we walk the generic chain (exactly
    // what the injector emits) to surface a reflection error early and reject just that interface.
    static bool ClassSatisfies(Type c, Type i)
    {
        var chain = new List<Type> { i };
        try { chain.AddRange(i.GetInterfaces().Where(x => x.IsGenericType)); } catch { return false; }
        foreach (var iface in chain) { try { iface.GetMethods(); } catch { return false; } }
        return true;
    }

    // The supertypes to emit: the direct base CLASS only (if linkable). Interface supertypes are deferred: a class
    // implementing an interface must satisfy ALL its abstract members, but the injected concrete members don't
    // always line up with the interface contract (e.g. a generic `Collection<T>` vs the non-generic `IList.Add`),
    // which would leave "unimplemented abstract member" errors. Class-hierarchy assignability (the WinUI control
    // pass-through case — feedback item 1) is the priority; interfaces come with the P1-2 generic work.
    static List<TN> SuperTypes(Type t)
    {
        var supers = new List<TN>();
        if (t.BaseType != null && EmittableBase(t.BaseType)) supers.Add(new TN.Fqn(SimpleName(t.BaseType)));
        return supers;
    }

    // ----- DotKt metadata: restore Kotlin modifiers a DotKt-compiled assembly stamped (no .NET analog) -----
    const string KFuncAttr = "DotKt.Runtime.CompilerServices.KotlinFunctionAttribute";
    const string KFileAttr = "DotKt.Runtime.CompilerServices.KotlinFileClassAttribute";

    // The KotlinFunctionFlags carried by a method's [KotlinFunction] (Infix=1, Operator=2, Suspend=4), or 0/none.
    static (bool infix, bool op, bool suspend) KotlinFun(MethodInfo m)
    {
        bool infix = false, op = false, suspend = false;
        try
        {
            foreach (var cad in m.GetCustomAttributesData())
                if (cad.AttributeType.FullName == KFuncAttr && cad.ConstructorArguments.Count == 1)
                {
                    var f = Convert.ToInt32(cad.ConstructorArguments[0].Value);
                    infix = (f & 1) != 0; op = (f & 2) != 0; suspend = (f & 4) != 0;
                }
        }
        catch { /* DotKt.Runtime not in the resolver set -> no Kotlin modifiers to restore */ }
        // compareTo is ALWAYS an operator (Comparable.compareTo); some metadata carries it as infix-only, which makes a
        // consumer's `a > b` (-> a.compareTo(b)) fail with "operator modifier required". Force it for the known name.
        if (m.Name == "compareTo") op = true;
        return (infix, op, suspend);
    }

    static bool HasKotlinFileClass(Type t)
    {
        try { return t.GetCustomAttributesData().Any(c => c.AttributeType.FullName == KFileAttr); }
        catch { return false; }
    }

    // Class-nature round-trip markers: [KotlinFunInterface] on a `fun interface` (SAM), [KotlinSealed] on a `sealed`
    // class/interface. Read back so the injector restores the Kotlin nature the CLR shape (plain interface / abstract
    // class) dropped. Absent (a genuine .NET type, or an un-stamped assembly) -> the plain shape, as before.
    const string KFunIfaceAttr = "DotKt.Runtime.CompilerServices.KotlinFunInterfaceAttribute";
    const string KSealedAttr   = "DotKt.Runtime.CompilerServices.KotlinSealedAttribute";
    static bool HasKotlinFunInterface(Type t)
    {
        try { return t.GetCustomAttributesData().Any(c => c.AttributeType.FullName == KFunIfaceAttr); }
        catch { return false; }
    }
    static bool HasKotlinSealed(Type t)
    {
        try { return t.GetCustomAttributesData().Any(c => c.AttributeType.FullName == KSealedAttr); }
        catch { return false; }
    }

    const string KReadOnlyAttr = "DotKt.Runtime.CompilerServices.KotlinReadOnlyAttribute";
    static bool IsKotlinReadOnly(FieldInfo f)
    {
        try { return f.GetCustomAttributesData().Any(c => c.AttributeType.FullName == KReadOnlyAttr); }
        catch { return false; }
    }


    // .NET nullable-reference metadata (NRT): the C# compiler stamps [Nullable(b)] per element (1=not-null, 2=nullable,
    // 0=oblivious; a byte[] for nested generics, top level is [0]) and [NullableContext(b)] as a method/type default.
    static byte NrtByteOf(IList<CustomAttributeData> attrs) => NrtByteAt(attrs, 0);
    // The NRT flag byte at a given position of the flattened (preorder) [Nullable] byte array — index 0 is the outer
    // type, index 1 its first type argument, and so on. The SCALAR form ([Nullable(b)]) collapses a uniform flag for
    // EVERY position, so it answers any index. 255 = no [Nullable] on this element.
    static byte NrtByteAt(IList<CustomAttributeData> attrs, int index)
    {
        foreach (var c in attrs)
            if (c.AttributeType.FullName == "System.Runtime.CompilerServices.NullableAttribute" && c.ConstructorArguments.Count == 1)
            {
                var a = c.ConstructorArguments[0];
                if (a.Value is byte b) return b;   // scalar: uniform flag for all positions
                if (a.Value is IReadOnlyList<CustomAttributeTypedArgument> arr && index < arr.Count && arr[index].Value is byte bn) return bn;
            }
        return 255; // no [Nullable] on this element
    }
    static byte NrtContextOf(MemberInfo m)
    {
        for (MemberInfo cur = m; cur != null; cur = cur.DeclaringType)
            foreach (var c in CustomAttributeData.GetCustomAttributes(cur))
                if (c.AttributeType.FullName == "System.Runtime.CompilerServices.NullableContextAttribute"
                    && c.ConstructorArguments.Count == 1 && c.ConstructorArguments[0].Value is byte b) return b;
        return 0; // no context -> oblivious (the assembly didn't opt into NRT)
    }

    // Wrap a mapped TypeNode in the tri-state NRT nullability of a REFERENCE-type position (spec §1): bare = not-null
    // (NullableAttribute=1), `Nullable` = `T?` (=2), `Oblivious` = `T!` (=0, NRT-oblivious / platform). Read UNIFORMLY
    // from .NET NRT metadata for any assembly (DotKt emits NRT for its own output too). Value types / byref get no
    // wrapper (a value-type `X?` is already the structural `Nullable`); a node that ALREADY carries nullability
    // (`Any?`, a value-type `X?`) is left untouched (never double-wrapped).
    static TN ApplyNrt(TN node, Type t, IList<CustomAttributeData> attrs, MemberInfo ctx)
    {
        if (t.IsValueType || t.IsByRef || t.IsPointer) return node;
        if (node is TN.Nullable || node is TN.Oblivious) return node;
        byte b = NrtByteOf(attrs);
        if (b == 255) b = NrtContextOf(ctx);
        return b == 2 ? new TN.Nullable(node) : b == 0 ? new TN.Oblivious(node) : node;
    }
    // #143: fold an OUTPUT-position `[MaybeNull]`/`[NotNull]` flow contract (System.Diagnostics.CodeAnalysis) over an
    // NRT-wrapped node. `[MaybeNull]` (e.g. `ThreadLocal<T>.Value`, which returns `default(T)`=null when unset) demotes a
    // non-null node to a PLATFORM type `T!` (Oblivious), NOT hard `T?`: the position is typically an UNCONSTRAINED generic
    // `T`, so a value-type instantiation (`ThreadLocal<Int>`) returns `default(int)`=0 and is never null — forcing `T?`
    // there would be wrong. Platform `T!` is value-type-safe, matches .NET's own use of `[MaybeNull]` ("maybe null IF T is
    // a reference type"), and stops the consumer's FIR flagging `x == null` as 'always false'. `[NotNull]` conversely
    // re-asserts non-null over a nullable node. This is applied ONLY at return/property (getter) positions — a param's
    // `[MaybeNull]`/`[NotNull]` is an ON-EXIT (ref/out) contract and must NOT flip its input type, so ApplyNrt (shared
    // with the param call site) stays contract-agnostic.
    static TN ApplyFlowContract(TN node, Type t, IList<CustomAttributeData> attrs)
    {
        if (t.IsValueType || t.IsByRef || t.IsPointer) return node;
        if (HasMaybeNull(attrs) && node is not (TN.Nullable or TN.Oblivious)) return new TN.Oblivious(node);
        if (HasNotNull(attrs) && node is TN.Nullable nn) return nn.Of;
        return node;
    }
    // Exact-name match — must NOT catch the CONDITIONAL siblings (MaybeNullWhen/NotNullWhen/NotNullIfNotNull), whose
    // param-position semantics are unrelated to an unconditional output contract.
    static bool HasMaybeNull(IList<CustomAttributeData> attrs) => HasAttr(attrs, "System.Diagnostics.CodeAnalysis.MaybeNullAttribute");
    static bool HasNotNull(IList<CustomAttributeData> attrs) => HasAttr(attrs, "System.Diagnostics.CodeAnalysis.NotNullAttribute");
    static bool HasAttr(IList<CustomAttributeData> attrs, string fullName)
    {
        foreach (var c in attrs) if (c.AttributeType.FullName == fullName) return true;
        return false;
    }

    const string KInlineAttr = "DotKt.Runtime.CompilerServices.KotlinInlineAttribute";
    // The carried BIR body of an inline+lambda fn ([KotlinInline]), or null. Splice-able by a consuming module.
    // Reads the versioned `(string version, byte[] content)` carrier envelope (spec §0) via the single BirCarrier
    // dispatch — an UNKNOWN version throws (loud), never a silent mis-decode.
    static string KotlinInlineBody(MethodInfo m)
    {
        foreach (var cad in m.GetCustomAttributesData())
            if (cad.AttributeType.FullName == KInlineAttr && cad.ConstructorArguments.Count == 2)
                return DecodeCarrier(cad);
        return null;
    }

    // Materialize a `(string version, byte[] content)` carrier attribute (spec §0) to its decoded JSON string. The
    // byte[] ctor arg reflects back as an IReadOnlyList<CustomAttributeTypedArgument>, so reify it before decoding.
    static string DecodeCarrier(CustomAttributeData cad)
    {
        var version = (string)cad.ConstructorArguments[0].Value!;
        var raw = cad.ConstructorArguments[1].Value;
        byte[] content;
        if (raw is byte[] b) content = b;
        else if (raw is IReadOnlyList<CustomAttributeTypedArgument> arr)
        {
            content = new byte[arr.Count];
            for (int i = 0; i < arr.Count; i++) content[i] = (byte)arr[i].Value!;
        }
        else throw new FormatException("carrier content is not a byte[]");
        return DotKt.Bir.BirCarrier.DecodeBody(version, content).ToJsonString();
    }

    // H2: the [KotlinSuspendFunctionType(shape)] attribute stamped by ilemit on a `suspend (…) -> T` function-type
    // POSITION (param / return / field / property). bir2cir erases the CLR signature slot to `object` (a suspend-lambda
    // VALUE is a Continuation state-machine object, not a Func), so the suspend ORIGIN + arg/return SHAPE would be lost
    // on re-consumption. The attribute carries the pre-erasure type as a STRUCTURED TypeNode JSON — an `fn` node with
    // `suspend:true` (the #37 type-flip: every emitted type token is now a `{t:…}` object, NOT the old `sfunc:<ret>:…`
    // BIR string). SuspendFnMeta reads it via the shared TypeNode contract and re-serializes it into the injector's META
    // grammar `sfunc:[ret,arg,arg]` (bracketed) that ClrTypeInjection.coneOf restores to `kotlin.coroutines.SuspendFunctionN`.
    const string KSuspendFnAttr = "DotKt.Runtime.CompilerServices.KotlinSuspendFunctionTypeAttribute";
    // The pre-erasure `suspend (…) -> T` type carried in [KotlinSuspendFunctionType] as a STRUCTURED TypeNode JSON —
    // an `fn` node with `suspend:true` (bir2cir erased the CLR slot to `object`). Now embedded DIRECTLY as the shared
    // TypeNode (no META-string re-serialization): the injector's coneOf sees the `fn` and restores SuspendFunctionN.
    // Null when absent/malformed, or when it carries an extension receiver (coneSuspendFunctionType takes params+ret
    // only) -> the caller keeps the plain erased slot (suspend lost, safe).
    static TN SuspendFnNode(IList<CustomAttributeData> attrs)
    {
        string shape = null;
        foreach (var cad in attrs)
            if (cad.AttributeType.FullName == KSuspendFnAttr && cad.ConstructorArguments.Count == 2)
            { shape = DecodeCarrier(cad); break; }
        if (string.IsNullOrEmpty(shape)) return null;
        try { var node = TN.Parse(shape); return node is TN.Fn { Suspend: true, Recv: null } ? node : null; }
        catch { return null; }
    }

    // H2: a FIELD typed `suspend (…) -> T` -> restore the suspend function type from [KotlinSuspendFunctionType],
    // else the plain mapped field type.
    static TN FieldTypeN(FieldInfo f, Type self)
    {
        var attrs = f.GetCustomAttributesData();
        var sfn = SuspendFnNode(attrs);
        if (sfn != null) return sfn;
        var t = MapT(f.FieldType, self);
        return HasExtFnMarker(attrs) ? WithExtRecv(t) : t;   // #145: a `val handler: P.() -> R` field
    }
    // H2: a non-suspend method / property getter that RETURNS a `suspend (…) -> T` value -> restore it from the return
    // parameter's [KotlinSuspendFunctionType], else the plain mapped return type. (A `suspend fun` itself returns
    // Task/Task<T> and is restored via SuspendRetToken — a different path, untouched.)
    // A method/getter RETURN type as a TypeNode, folding the tri-state NRT nullability. A restored suspend function
    // type carries its own shape and takes no NRT wrapper (the erased `object` slot's NRT is meaningless for it).
    static TN RetTypeSfxN(MethodInfo m, Type self)
    {
        var attrs = CustomAttributeData.GetCustomAttributes(m.ReturnParameter);
        var sfn = SuspendFnNode(attrs);
        if (sfn != null) return sfn;
        // #133: a Kotlin `Nothing` return has no CLR analog — bir2cir erases it to `object` (BirTypeLowering
        // kotlin.Nothing->object). The [KotlinNothing] return-parameter marker (stamped by bir2cir's RoundtripMetadata,
        // riding the same retAttrs channel as [Nullable]/[KotlinSuspendFunctionType]) restores it, so the consumer's FIR
        // sees `kotlin.Nothing` and an `if/else` with a Nothing branch keeps the non-Nothing type instead of widening to
        // Any?. NRT composes on top: a `Nothing?` return rides the nullability byte. (kotc's coneOf resolves the bare
        // `Nothing` type node to bt.nothingType.) This covers the top-level/member method + getter return path AND the
        // companion-static return (the companion-static loop calls RetTypeSfxN here); the suspend-return position reads
        // the same marker via SuspendRetNode (#135).
        if (HasNothingMarker(attrs)) return ApplyNrt(new TN.Fqn("Nothing"), m.ReturnType, attrs, m);
        // #150: thread NRT so a `Func<string?>`-returning method surfaces the delegate return as `String?`. MapTFn maps
        // the (byref-stripped) return from array index 0; the outer ApplyNrt/ApplyFlowContract still fold the outer slot.
        var mapped = MapTFn(m.ReturnType.IsByRef ? m.ReturnType.GetElementType() : m.ReturnType, self, attrs, m);
        var rt = ApplyFlowContract(ApplyNrt(mapped, m.ReturnType, attrs, m), m.ReturnType, attrs);
        return HasExtFnMarker(attrs) ? WithExtRecv(rt) : rt;   // #145: a method returning `P.() -> R`
    }

    // #145: the bare marker `[KotlinExtensionFunctionType]` -> the delegate at this position was a Kotlin RECEIVER
    // function type `P.() -> R`. bir2cir did NOT erase it (unlike suspend): the receiver rides as the delegate's FIRST
    // CLR type arg. So restore `P.() -> R` by moving the mapped delegate's first param into the fn's receiver — the
    // consumer's ClrTypeInjection then builds an EXTENSION function type, giving the passed lambda an implicit `this: P`.
    const string KExtFnAttr = "DotKt.Runtime.CompilerServices.KotlinExtensionFunctionTypeAttribute";
    static bool HasExtFnMarker(IList<CustomAttributeData> attrs)
    {
        foreach (var cad in attrs)
            if (cad.AttributeType.FullName == KExtFnAttr) return true;
        return false;
    }
    // Move a mapped delegate's FIRST arg into the fn receiver (through a nullable/oblivious wrapper). A non-delegate or
    // an argless delegate is returned unchanged (defensive — the marker only rides genuine `P.() -> R` positions).
    static TN WithExtRecv(TN t) => t switch
    {
        TN.Fn { Params.Length: > 0 } f => new TN.Fn(f.Suspend, f.Ret, f.Params.Skip(1).ToArray(), f.Params[0]),
        TN.Nullable n => new TN.Nullable(WithExtRecv(n.Of)),
        TN.Oblivious o => new TN.Oblivious(WithExtRecv(o.Of)),
        _ => t,
    };

    // A DotKt property's Kotlin TypeNode: the mapped (NRT-folded) property type, restoring a `val/var p: P.() -> R`
    // receiver function type from the [KotlinExtensionFunctionType] marker landed on the PropertyDef (#145). Shared by
    // every property-surfacing loop (instance/interface/static/companion) so the receiver restore is uniform.
    static TN PropTypeN(PropertyInfo p, Type self)
    {
        var attrs = CustomAttributeData.GetCustomAttributes(p);
        var t = ApplyNrt(MapTFn(p.PropertyType, self, attrs, p), p.PropertyType, attrs, p);  // #150: NRT-threaded (delegate-typed property)
        // #143: a property's `[MaybeNull]`/`[NotNull]` flow contract may ride the PropertyDef OR its GETTER's return
        // parameter (`[return: MaybeNull] get`) — the BCL uses both placements (e.g. `ThreadLocal<T>.Value` on the getter
        // return). Fold from both attribute sets. Demotes `ThreadLocal<T>.Value` to a platform `T!`.
        t = ApplyFlowContract(t, p.PropertyType, attrs);
        if (p.GetMethod != null) t = ApplyFlowContract(t, p.PropertyType, CustomAttributeData.GetCustomAttributes(p.GetMethod.ReturnParameter));
        return HasExtFnMarker(attrs) ? WithExtRecv(t) : t;
    }

    // #133: the bare marker `[KotlinNothing]` on a return parameter -> the erased `object` return was a Kotlin `Nothing`.
    const string KNothingAttr = "DotKt.Runtime.CompilerServices.KotlinNothingAttribute";
    static bool HasNothingMarker(IList<CustomAttributeData> attrs)
    {
        foreach (var cad in attrs)
            if (cad.AttributeType.FullName == KNothingAttr) return true;
        return false;
    }

    // A `suspend fun` is emitted returning Task / Task<T>; restore the Kotlin result type and gate Supported on it.
    static bool IsTask1(Type t) => t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Threading.Tasks.Task`1";
    static bool SuspendRetSupported(Type ret) => IsTask1(ret) ? Supported(ret.GetGenericArguments()[0]) : ret.FullName == "System.Threading.Tasks.Task";

    // The Kotlin RESULT type of a `suspend fun` (the INNER T of the emitted `Task<T>`, or Unit for a non-generic Task),
    // as a TypeNode. Its nullability rides the result (index 1 of the return's flattened [Nullable] byte array — index 0
    // is the always-non-null `Task` wrapper), robust to the scalar and array NRT encodings. A value-type result carries
    // no NRT slot -> no wrapper.
    static TN SuspendRetNode(MethodInfo m, Type self)
    {
        var attrs = CustomAttributeData.GetCustomAttributes(m.ReturnParameter);
        // #135: a `suspend fun f(): Nothing` — the Kotlin `Nothing` result is erased (Task<object> / Task); the
        // [KotlinNothing] marker (stamped by bir2cir on the SAME retAttrs channel) restores it. Check BEFORE the Task
        // unwrap so a non-generic `Task` return doesn't collapse to Unit. NRT (index 1, the result slot) folds on top.
        if (HasNothingMarker(attrs))
        {
            byte nb = NrtByteAt(attrs, 1);
            if (nb == 255) nb = NrtContextOf(m);
            var nn = (TN)new TN.Fqn("Nothing");
            return nb == 2 ? new TN.Nullable(nn) : nb == 0 ? new TN.Oblivious(nn) : nn;
        }
        if (!IsTask1(m.ReturnType)) return new TN.Fqn("Unit");
        var inner = m.ReturnType.GetGenericArguments()[0];
        var node = MapRetT(inner, self);
        if (inner.IsValueType || node is TN.Nullable || node is TN.Oblivious) return node;
        byte b = NrtByteAt(attrs, 1);
        if (b == 255) b = NrtContextOf(m);
        return b == 2 ? new TN.Nullable(node) : b == 0 ? new TN.Oblivious(node) : node;
    }

    // Emit a Kotlin file-facade class ([KotlinFileClass]) as a `file` decl: top-level functions (a `fun` per public
    // static method, Main + object members skipped) + top-level (extension) properties.
    static void EmitKotlinFileClass(Type t, JsonArray files)
    {
        var funs = new JsonArray();
        var tlProps = new JsonArray();
        var seen = new HashSet<string>();
        // Extension properties (`val T.p`) compile to top-level get_p(__self: T) (+ set_p for `var`). Surface them as a
        // top-level `prop` WITH a `recv` (the discriminator vs a plain top-level prop); the consumer restores `val/var T.p`.
        var extPropMembers = new HashSet<string>();
        foreach (var g in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (g.IsSpecialName || !g.Name.StartsWith("get_")) continue;
            var gps = g.GetParameters();
            if (gps.Length != 1 || gps[0].Name != "__self" || !Supported(g.ReturnType) || !Supported(gps[0].ParameterType)) continue;
            var pn = g.Name.Substring(4);
            if (!seen.Add("tlextprop:" + pn)) continue;
            var setter = t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .FirstOrDefault(s => !s.IsSpecialName && s.Name == "set_" + pn && s.GetParameters().Length == 2 && s.GetParameters()[0].Name == "__self");
            extPropMembers.Add(g.Name); if (setter != null) extPropMembers.Add(setter.Name);
            tlProps.Add(PropObj(pn, RetTypeSfxN(g, t), setter != null, Mods(("ext", true)), "public", null, MapT(gps[0].ParameterType, t)));
        }
        // #103: a top-level field-backed property with a CUSTOM accessor compiles to a public static FIELD `<name>`
        // PLUS a separate non-special-name `get_<name>`/`set_<name>` method (the custom accessor body). Detect the
        // pairing so (a) the accessor methods are NOT surfaced as loose top-level `fun`s and (b) the `prop` below is
        // marked `customGet`/`customSet` — the consumer must INVOKE the accessor, not read/write the raw static field
        // (else the custom getter/setter is bypassed cross-module, silently returning the raw field: the #103 miscompile).
        var staticFieldNames = new HashSet<string>();
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            if (f.Name.Length > 0 && f.Name[0] != '<' && f.Name[0] != '$') staticFieldNames.Add(f.Name);
        var fieldAccessorMembers = new HashSet<string>();
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (m.IsSpecialName) continue;
            var mps = m.GetParameters();
            if (m.Name.StartsWith("get_") && mps.Length == 0 && staticFieldNames.Contains(m.Name.Substring(4)))
                fieldAccessorMembers.Add(m.Name);
            else if (m.Name.StartsWith("set_") && mps.Length == 1 && mps[0].Name != "__self" && staticFieldNames.Contains(m.Name.Substring(4)))
                fieldAccessorMembers.Add(m.Name);
        }
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (m.IsSpecialName || m.Name == "Main" || OBJECT_MEMBERS.Contains(m.Name) || extPropMembers.Contains(m.Name) || fieldAccessorMembers.Contains(m.Name)) continue;
            if (m.IsGenericMethod && !m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            var k = KotlinFun(m);
            var retOk = k.suspend ? SuspendRetSupported(m.ReturnType) : Supported(m.ReturnType);
            if (!ps.All(p => Supported(p.ParameterType)) || !retOk) continue;
            var gp = m.IsGenericMethodDefinition ? m.GetGenericArguments().Select(g => g.Name).ToList() : new List<string>();
            if (!seen.Add(m.Name + "<" + string.Join(",", gp) + ">(" + Sig(ps, t) + ")")) continue;
            var ret = k.suspend ? SuspendRetNode(m, t) : RetTypeSfxN(m, t);
            // An extension fun's receiver is the first param `__self` -> `mods.ext`. `mods.inline` carries the spliceable
            // body (in the assembly's [KotlinInline], read by the consumer's ilemit at splice time).
            var isExt = ps.Length > 0 && ps[0].Name == "__self";
            // ref/rt de-dup (Bug ④): skip a NON-EXTENSION factory (listOf/mapOf) whose return is an unresolvable kotlin
            // collection -> AMBIGUOUS with the jar's same-signature factory. Scoped to `kotlin.*` (a user `fun
            // makeList(): List<Int>` has no jar counterpart). EXTENSION funs are KEPT (receiver-based, no ambiguity).
            if (!isExt && (t.Namespace ?? "").StartsWith("kotlin") && IsStdlibCollectionFqn(ret))
            {
                Console.Error.WriteLine($"note: dropped stdlib collection factory tlfun {(string.IsNullOrEmpty(t.Namespace) ? "" : t.Namespace + ".")}{m.Name} (its {(ret as TN.Fqn)?.Name} return is ambiguous with the kotlin-stdlib.jar's same-signature factory)");
                continue;
            }
            // An EXTENSION whose RECEIVER maps to Any? is a CATCH-ALL that mis-wins overload resolution; skip it.
            if (isExt && IsAnyQ(MapT(ps[0].ParameterType, t))) continue;
            var (mm, mv) = ModVis(false, isAbstract: false, isOpen: false, infix: k.infix, op: k.op, suspend: k.suspend,
                inline: KotlinInlineBody(m) != null, ext: isExt);
            funs.Add(FunObj(m.Name, ret, mm, mv, null,
                m.IsGenericMethodDefinition ? TypeParamsArr(m.GetGenericArguments(), t, false, false) : null,
                ParamsArr(ps, t)));
        }
        // #34b: a top-level `val`/`var` (with a backing field) compiles to a plain static FIELD -> a top-level `prop`
        // WITHOUT a recv (the backend routes read/write to that static field). `val` vs `var` from [KotlinReadOnly]/InitOnly.
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (f.Name.Length == 0 || f.Name[0] == '<' || f.Name[0] == '$') continue;
            if (!Supported(f.FieldType) || !seen.Add("tlprop:" + f.Name)) continue;
            // #103: a sibling `get_<name>`/`set_<name>` (the custom accessor body) marks the prop so the consumer
            // invokes the accessor rather than the raw field. A custom setter implies `var`; otherwise rw is the
            // field's mutability (a custom-getter-only `val` keeps its mutable backing field, unchanged from before).
            var customGet = fieldAccessorMembers.Contains("get_" + f.Name);
            var customSet = fieldAccessorMembers.Contains("set_" + f.Name);
            var rw = customSet || !(f.IsInitOnly || IsKotlinReadOnly(f));
            tlProps.Add(PropObj(f.Name, FieldTypeN(f, t), rw, Mods(("customGet", customGet), ("customSet", customSet)), "public", null, null));
        }
        // A `file` decl: the package (empty = root) + the .NET file-class FQN (where the backend emits the static call).
        var fileObj = new JsonObject { ["pkg"] = string.IsNullOrEmpty(t.Namespace) ? "" : t.Namespace, ["fileClass"] = t.FullName };
        if (funs.Count > 0) fileObj["funs"] = funs;
        if (tlProps.Count > 0) fileObj["props"] = tlProps;
        files.Add(fileObj);
        Console.WriteLine($"meta: {t.FullName} (kotlin file -> top-level)");
    }

    // The kotlin.* stdlib collection factory return names whose constructed-generic form is ambiguous with the jar.
    static readonly HashSet<string> STDLIB_COLLECTION_NAMES = new()
    { "List", "MutableList", "Set", "MutableSet", "Map", "MutableMap", "Collection", "MutableCollection", "Iterable",
      "MutableIterable", "Pair", "Triple", "HashMap", "LinkedHashMap", "HashSet", "LinkedHashSet", "ArrayList", "Sequence" };
    static bool IsStdlibCollectionFqn(TN t) => t is TN.Fqn { Args: not null } f && STDLIB_COLLECTION_NAMES.Contains(f.Name);

    // null => skip (private/internal); false => public; true => protected (Family / protected-internal). Frameworks
    // (WinUI/WPF/Avalonia) override protected virtual lifecycle methods, so these MUST be injected (item 2).
    static bool? Vis(MethodBase m) =>
        m == null ? null : m.IsPublic ? false : (m.IsFamily || m.IsFamilyOrAssembly) ? true : (bool?)null;

    static bool HasDefault(ParameterInfo p) { try { return p.HasDefaultValue && !p.IsOut; } catch { return false; } }
    // #146: a non-const default arg the library exports as `[kotlin.clr.KotlinDefault]` (BIR sub-tree spliced at BIR->CIR).
    static bool HasKotlinDefault(IList<CustomAttributeData> attrs) =>
        attrs.Any(a => a.AttributeType.FullName == "kotlin.clr.KotlinDefault");
    static bool IsParamArray(ParameterInfo p)
    {
        try { return p.GetCustomAttributesData().Any(c => c.AttributeType.FullName == "System.ParamArrayAttribute"); }
        catch { return false; }
    }

    static string MetaParamName(ParameterInfo p, int i)
    {
        var n = p.Name;
        return (string.IsNullOrEmpty(n) || !IsIdent(n)) ? "arg" + i : n;
    }

    // A bare generic parameter (T) is fine as a Kotlin type parameter; constructed generics
    // containing parameters (List<T>) are not yet emittable -> Any?.
    // Array/cross-type member support is for the FIR-injection (--meta) path only. The legacy façade-.kt path
    // (GenerateType) needs valid Kotlin source, so it keeps the conservative behavior (arrays skipped, cross-type
    // -> Any?). MetaMode is set true while EmitMeta runs.
    static bool MetaMode = false;

    static bool Supported(Type t) =>
        // `out`/`ref` params (T&) surface as their element type on the injection path; the Kotlin caller wraps the
        // arg in the `byref(x)` marker and the backend re-applies the byref via a `byref:` param type.
        t.IsByRef ? (MetaMode && Supported(t.GetElementType()))
        : !t.IsPointer
        && ((MetaMode && t.IsArray) ? Supported(t.GetElementType())
            : (!t.IsArray && (t.IsGenericParameter || !t.ContainsGenericParameters
                // A constructed generic whose args are themselves supported (`Box<T>` in `fun <T> wrap(): Box<T>`):
                // it ContainsGenericParameters (the method's T), but each arg resolves, so the `generic:` encoding works.
                || (MetaMode && t.IsGenericType && t.GetGenericArguments().All(Supported)))));

    // Map a .NET type to a Kotlin TypeNode (spec §1). Primitives -> kotlin Fqn; a generic parameter -> a POSITIONAL
    // scope-tagged `tv`; a delegate -> an `fn`; a byref -> `byRef`; everything else via CrossTypeT / degrade to Any?.
    static TN MapT(Type t, Type self)
    {
        // An `out`/`ref` param / `ref`-returning method (T&) -> `byRef` (the injector surfaces it as ClrRef<T>).
        if (t.IsByRef) return new TN.ByRef(MapT(t.GetElementType(), self));
        // Compare by FullName, not typeof identity (LoadFrom'd reference assemblies have a different assembly identity).
        if (t.FullName == "System.Void") return new TN.Fqn("Unit");
        // A generic parameter -> a positional `tv`: scope "method" (the method's own params, CLR !!i) when declared on a
        // method, else scope "type" (the enclosing type's params, CLR !i). Non-nested here, so position IS the flattened index.
        if (t.IsGenericParameter)
            return new TN.Tv(t.DeclaringMethod != null ? "method" : "type", t.GenericParameterPosition);
        // A .NET `Span<T>` -> the intrinsic `kotlin.clr.Span<T>` (resolves via the dotted-FQN path; the caller passes buf.asSpan()).
        if (t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Span`1")
            return new TN.Fqn("kotlin.clr.Span", new[] { MapT(t.GetGenericArguments()[0], self) });
        // A .NET `X?` (Nullable<X>, a nullable value type) -> Kotlin's `X?`, NOT the literal generic `Nullable<X>`.
        if (t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Nullable`1")
        {
            var inner = MapT(t.GetGenericArguments()[0], self);
            return IsAnyQ(inner) ? AnyQ() : new TN.Nullable(inner);
        }
        // (4) A .NET delegate -> a Kotlin function type `fn`. A lambda then binds to the delegate parameter and overloads
        // disambiguate by arity; the backend builds the SPECIFIC delegate from the call-site param type.
        if (MetaMode && IsDelegate(t))
        {
            var inv = t.GetMethod("Invoke");
            if (inv != null && inv.GetParameters().All(p => Supported(p.ParameterType)) && Supported(inv.ReturnType))
            {
                var dret = MapRetT(inv.ReturnType, self);
                var dps = inv.GetParameters().Select(p => MapT(p.ParameterType, self)).ToArray();
                // #1: keep the delegate a FUNCTION TYPE even when its Invoke has an `object`/Any? param or return
                // (SendOrPostCallback.Invoke(object) -> `(Any?) -> Unit`). Collapsing the whole delegate to a bare
                // `Any?` broke overriding a BCL virtual that takes such a delegate (`Post(cb, state)` no longer
                // matched the injected member). `(Any?) -> R` is still a valid, more-specific type than bare `Any?`.
                return new TN.Fn(false, dret, dps);
            }
            return AnyQ();
        }
        // Only short-circuit to the enclosing type's name when the FullName MATCH is real (an OPEN constructed generic
        // referencing a type param has a NULL FullName -> must recurse into the arg, not return the enclosing type).
        if (t.FullName != null && t.FullName == self.FullName) return new TN.Fqn(KotlinName(self));
        return t.FullName switch
        {
            "System.Int32" => new TN.Fqn("Int"),
            "System.Int64" => new TN.Fqn("Long"),
            "System.Int16" => new TN.Fqn("Short"),
            // Unsigned .NET primitives -> Kotlin's unsigned types (System.UInt32 == kotlin.UInt, etc.).
            "System.UInt32" => new TN.Fqn("UInt"),
            "System.UInt64" => new TN.Fqn("ULong"),
            "System.UInt16" => new TN.Fqn("UShort"),
            // Kotlin `Byte` is SIGNED (== System.SByte); System.Byte is UNSIGNED (== kotlin.UByte). STRICT mapping
            // (#53), consistent with the other unsigned widths (UInt16->UShort / UInt32->UInt / UInt64->ULong) and with
            // the forward direction (kotlin.UByte->System.Byte). This preserves UByte round-trip fidelity: a .NET byte
            // 200 reads as UByte 200, not the lossy signed Byte -56 the old collapse produced.
            "System.SByte" => new TN.Fqn("Byte"),
            "System.Byte" => new TN.Fqn("UByte"),
            "System.Boolean" => new TN.Fqn("Boolean"),
            "System.Double" => new TN.Fqn("Double"),
            "System.Single" => new TN.Fqn("Float"),
            "System.Char" => new TN.Fqn("Char"),
            "System.String" => new TN.Fqn("String"),
            "System.Object" => AnyQ(),
            _ => MetaMode ? CrossTypeT(t) : AnyQ(),
        };
    }

    // A method's RETURN type: a `ref T` return surfaces as plain `T` (a plain `val x = m()` is a value copy).
    static TN MapRetT(Type t, Type self) => MapT(t.IsByRef ? t.GetElementType() : t, self);

    // Diagnostic: a member's signature type degraded to `Any?` — logged once per distinct type (a silent `Any?` weakens
    // the injected overload). Returns the "Any?" node so callers can use it directly.
    static readonly HashSet<string> s_degradeNoted = new(StringComparer.Ordinal);
    static TN DegradeToAny(Type t, string reason)
    {
        var id = t?.FullName ?? t?.Name ?? "<null>";
        if (s_degradeNoted.Add(id))
            Console.Error.WriteLine($"note: signature type degraded to Any? ({reason}): {id}");
        return AnyQ();
    }

    // A reference to another .NET type -> a Fqn (simple name / dotted FQN) the injector resolves IF injected, else Any?.
    static TN CrossTypeT(Type t)
    {
        if (t.IsArray) { var e = MapT(t.GetElementType(), t); return IsAnyQ(e) ? AnyQ() : new TN.Array(e); }
        // A root-namespace GENERIC user type (`Box<T>`) is handled by the generic branch; only reject an empty namespace
        // for NON-generic types (a global/compiler type with no useful injectable identity).
        if (t.IsByRef || t.IsPointer || t.IsGenericParameter || (string.IsNullOrEmpty(t.Namespace) && !t.IsGenericType)) return AnyQ();
        // (3) A constructed generic (`IList<ResourceDictionary>`) -> `Fqn(OpenSimple, [args])`; args may nest.
        if (t.IsGenericType)
        {
            var open = t.GetGenericTypeDefinition();
            // A NESTED generic definition (`List`1+Enumerator`) is NOT injected (no CLR-addressable open name); degrade.
            if (open.IsNested) return DegradeToAny(t, "nested generic definition — no CLR-addressable open name");
            var openName = KotlinName(open);   // arity-suffixed iff the name family clashes (Task`1 -> Task1)
            if (NO_INJECT.Contains(open.FullName ?? "") || !openName.All(c => char.IsLetterOrDigit(c) || c == '_'))
                return DegradeToAny(t, "generic open def not injectable / not a simple identifier");
            var args = t.GetGenericArguments().Select(a => MapT(a, t)).ToArray();
            if (args.Any(IsAnyQ)) return DegradeToAny(t, "a generic type argument was unresolvable");
            return new TN.Fqn(openName, args);
        }
        // The FULLY-QUALIFIED name (so the injector resolves the EXACT type, not a same-simple-name type from another
        // namespace); fall back to the simple name for nested types (FullName has '+').
        var fqn = t.FullName;
        if (fqn != null && fqn.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '.')) return new TN.Fqn(fqn);
        var n = t.Name;
        return n.All(c => char.IsLetterOrDigit(c) || c == '_') ? new TN.Fqn(n) : DegradeToAny(t, "name is not a resolvable identifier");
    }

    // ====================================================================================================
    // #150: NRT-threaded delegate mapping. `MapT`/`ApplyNrt` fold nullability only over the OUTER member type
    // (index 0 of the flattened [Nullable] byte array); a DELEGATE type arg (`Action<string?>` / `Func<string?>`)
    // dropped its nested byte -> a delegate lambda's param/return always surfaced FORCED non-null. `MapTN` walks the
    // type in Roslyn's exact preorder, advancing a cursor over every flattened-array slot, and applies NRT to a
    // delegate's fn params/return (the contravariant sibling of #143's covariant ThreadLocal.Value). Non-delegate
    // positions keep MapT's bare structure (wrap=false) — their outer NRT is still applied by the caller's ApplyNrt —
    // so only delegate internals change. Entry from the param/return/property call sites via MapTFn (pos=0).
    // ----------------------------------------------------------------------------------------------------
    // A type occurrence gets a byte in the flattened [Nullable] array iff it is a reference type, a type parameter,
    // an array, or a CONSTRUCTED GENERIC value type (ValueTuple<>/a custom struct<T>) — but NOT `Nullable<T>` (`X?`)
    // and NOT a simple (non-generic) value type (int/enum/DateTime). Confirmed empirically against Roslyn's encoder:
    // `Func<string?,int,string>`->[1,2,1] (int no slot), `ValueTuple<int,string?>`->[0,2] (struct=0, int no slot),
    // `Func<GS<string?>,string>`->[1,0,2,1] (generic struct=0 + recurse). Value-type slots are 0 (never NRT-wrapped).
    static bool ConsumesNrtSlot(Type t)
    {
        if (t.IsByRef || t.IsPointer) return false;
        if (t.IsArray || t.IsGenericParameter) return true;
        if (!t.IsValueType) return true;                                             // reference type
        if (t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Nullable`1") return false;  // X?
        return t.IsGenericType;                                                      // generic struct -> slot; simple value -> none
    }

    // Wrap `node` in the NRT nullability of the reference-type position at flattened index `pos`. Value-type / byref
    // positions (and pos < 0 = no slot) and a node already carrying nullability take no wrapper; wrap=false is a no-op
    // (the outer position is wrapped by the caller's ApplyNrt). Reuses the shared NrtByteAt/NrtContextOf decode.
    static TN WrapNrt(TN node, Type t, IList<CustomAttributeData> attrs, MemberInfo ctx, int pos, bool wrap)
    {
        if (!wrap || pos < 0 || t.IsValueType || t.IsByRef || t.IsPointer) return node;
        if (node is TN.Nullable || node is TN.Oblivious) return node;
        byte b = NrtByteAt(attrs, pos);
        if (b == 255) b = NrtContextOf(ctx);
        return b == 2 ? new TN.Nullable(node) : b == 0 ? new TN.Oblivious(node) : node;
    }

    // The param/return/property entry point: map `t` threading the member's flattened [Nullable] byte array from
    // position 0, WITHOUT wrapping the outer node (the caller's ApplyNrt does that) — only nested delegate fn
    // params/return gain their own byte.
    static TN MapTFn(Type t, Type self, IList<CustomAttributeData> attrs, MemberInfo ctx)
    { int pos = 0; return MapTN(t, self, attrs, ctx, ref pos, false); }

    // The threaded walk (mirrors MapT's structure). `pos` advances over every flattened-array slot in Roslyn preorder;
    // `wrap` gates NRT application (true once inside a delegate's args, so nested delegates/generics honor their bytes).
    static TN MapTN(Type t, Type self, IList<CustomAttributeData> attrs, MemberInfo ctx, ref int pos, bool wrap)
    {
        if (t.IsByRef) return new TN.ByRef(MapTN(t.GetElementType(), self, attrs, ctx, ref pos, wrap));  // byref: no own slot
        if (t.FullName == "System.Void") return new TN.Fqn("Unit");
        // `Nullable<X>` (`X?`): structural TN.Nullable, consumes NO slot (neither the Nullable nor its value-type inner).
        if (t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Nullable`1")
        {
            var inner = MapTN(t.GetGenericArguments()[0], self, attrs, ctx, ref pos, wrap);
            return IsAnyQ(inner) ? AnyQ() : new TN.Nullable(inner);
        }
        int my = ConsumesNrtSlot(t) ? pos++ : -1;
        if (t.IsGenericParameter)
            return WrapNrt(new TN.Tv(t.DeclaringMethod != null ? "method" : "type", t.GenericParameterPosition), t, attrs, ctx, my, wrap);
        if (t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Span`1")
            return WrapNrt(new TN.Fqn("kotlin.clr.Span", new[] { MapTN(t.GetGenericArguments()[0], self, attrs, ctx, ref pos, wrap) }), t, attrs, ctx, my, wrap);
        if (MetaMode && IsDelegate(t))
            return MapDelegateN(t, self, attrs, ctx, my, ref pos, wrap);
        if (t.FullName != null && t.FullName == self.FullName)
            return WrapNrt(new TN.Fqn(KotlinName(self)), t, attrs, ctx, my, wrap);
        switch (t.FullName)
        {
            case "System.Int32": return new TN.Fqn("Int");
            case "System.Int64": return new TN.Fqn("Long");
            case "System.Int16": return new TN.Fqn("Short");
            case "System.UInt32": return new TN.Fqn("UInt");
            case "System.UInt64": return new TN.Fqn("ULong");
            case "System.UInt16": return new TN.Fqn("UShort");
            case "System.SByte": return new TN.Fqn("Byte");
            case "System.Byte": return new TN.Fqn("UByte");
            case "System.Boolean": return new TN.Fqn("Boolean");
            case "System.Double": return new TN.Fqn("Double");
            case "System.Single": return new TN.Fqn("Float");
            case "System.Char": return new TN.Fqn("Char");
            case "System.String": return WrapNrt(new TN.Fqn("String"), t, attrs, ctx, my, wrap);
            case "System.Object": return AnyQ();
            default: return MetaMode ? CrossTypeTN(t, attrs, ctx, my, ref pos, wrap) : AnyQ();
        }
    }

    // A delegate -> a Kotlin `fn` node, honoring the flattened byte array. For the Action`n/Func`n families the
    // delegate's GENERIC ARGUMENTS ARE its Invoke shape (params..., [return]) in order, so map each generic arg with the
    // threaded cursor (NRT applied) — this is the #150 fix. A custom/non-generic delegate keeps the current Invoke-based
    // bare mapping (its Invoke params' NRT rides the delegate's OWN metadata, not this member's array); its own
    // generic-arg slots are still walked to keep the cursor aligned for any following sibling.
    static TN MapDelegateN(Type t, Type self, IList<CustomAttributeData> attrs, MemberInfo ctx, int my, ref int pos, bool wrap)
    {
        var inv = t.GetMethod("Invoke");
        if (inv != null && inv.GetParameters().All(p => Supported(p.ParameterType)) && Supported(inv.ReturnType))
        {
            var open = t.IsGenericType ? t.GetGenericTypeDefinition().FullName : null;
            bool isActionFunc = open != null && (open.StartsWith("System.Action`") || open.StartsWith("System.Func`"));
            if (isActionFunc)
            {
                var gargs = t.GetGenericArguments();
                var mapped = new TN[gargs.Length];
                for (int i = 0; i < gargs.Length; i++) mapped[i] = MapTN(gargs[i], self, attrs, ctx, ref pos, true);
                bool voidRet = inv.ReturnType.FullName == "System.Void";
                TN dret = voidRet ? new TN.Fqn("Unit") : mapped[^1];
                TN[] dps = voidRet ? mapped : mapped[..^1];
                // #1: an Any?-typed param/return no longer collapses the delegate to bare Any? (see MapT above).
                return WrapNrt(new TN.Fn(false, dret, dps), t, attrs, ctx, my, wrap);
            }
            foreach (var g in t.GetGenericArguments()) MapTN(g, self, attrs, ctx, ref pos, false);  // advance cursor only
            var dret2 = MapRetT(inv.ReturnType, self);
            var dps2 = inv.GetParameters().Select(p => MapT(p.ParameterType, self)).ToArray();
            // #1: keep the delegate a function type even with Any? Invoke params/return.
            return WrapNrt(new TN.Fn(false, dret2, dps2), t, attrs, ctx, my, wrap);
        }
        return AnyQ();
    }

    // Cursor-threaded mirror of CrossTypeT (a reference to another .NET type). Advances `pos` over the array element /
    // generic args in preorder and applies NRT via `wrap`. Same degrade decisions/structure as CrossTypeT — INCLUDING
    // its `self` REBASE: children recurse with `t` (the cross type) as the new self, so a nested self-reference resolves
    // as CrossTypeT does (a dotted Fqn), never MapTN's simple-name self-match branch.
    static TN CrossTypeTN(Type t, IList<CustomAttributeData> attrs, MemberInfo ctx, int my, ref int pos, bool wrap)
    {
        if (t.IsArray) { var e = MapTN(t.GetElementType(), t, attrs, ctx, ref pos, wrap); return IsAnyQ(e) ? AnyQ() : WrapNrt(new TN.Array(e), t, attrs, ctx, my, wrap); }
        if (t.IsByRef || t.IsPointer || t.IsGenericParameter || (string.IsNullOrEmpty(t.Namespace) && !t.IsGenericType)) return AnyQ();
        if (t.IsGenericType)
        {
            var open = t.GetGenericTypeDefinition();
            if (open.IsNested) return DegradeToAny(t, "nested generic definition — no CLR-addressable open name");
            var openName = KotlinName(open);
            if (NO_INJECT.Contains(open.FullName ?? "") || !openName.All(c => char.IsLetterOrDigit(c) || c == '_'))
                return DegradeToAny(t, "generic open def not injectable / not a simple identifier");
            var ga = t.GetGenericArguments();
            var args = new TN[ga.Length];
            for (int i = 0; i < ga.Length; i++) args[i] = MapTN(ga[i], t, attrs, ctx, ref pos, wrap);
            if (args.Any(IsAnyQ)) return DegradeToAny(t, "a generic type argument was unresolvable");
            return WrapNrt(new TN.Fqn(openName, args), t, attrs, ctx, my, wrap);
        }
        var fqn = t.FullName;
        if (fqn != null && fqn.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '.')) return WrapNrt(new TN.Fqn(fqn), t, attrs, ctx, my, wrap);
        var n = t.Name;
        return n.All(c => char.IsLetterOrDigit(c) || c == '_') ? WrapNrt(new TN.Fqn(n), t, attrs, ctx, my, wrap) : DegradeToAny(t, "name is not a resolvable identifier");
    }

    // Map a CLR generic-parameter CONSTRAINT type to a Kotlin bound TypeNode. ilemit lowers a Kotlin `Comparable<T>`
    // bound to CLR `System.IComparable<T>`, so reverse that (else `<T : Comparable<T>>` round-trips as BCL `IComparable`).
    static TN MapBoundT(Type c, Type self)
    {
        if (c.IsGenericType && c.GetGenericTypeDefinition().FullName == "System.IComparable`1")
            return new TN.Fqn("Comparable", new[] { MapT(c.GetGenericArguments()[0], self) });
        return MapT(c, self);
    }

    // A call-site signature dedup key — the canonical JSON of each mapped param type (never emitted; internal only).
    static string Sig(ParameterInfo[] ps, Type self) => string.Join(",", ps.Select(p => TN.ToJson(MapT(p.ParameterType, self))));

    static bool IsIdent(string s) => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_');

    static readonly HashSet<string> OBJECT_MEMBERS = new()
    { "Equals", "GetHashCode", "GetType", "ReferenceEquals", "Finalize", "MemberwiseClone", "Clone" };

    static readonly HashSet<string> KEYWORDS = new()
    { "value", "object", "fun", "val", "var", "this", "is", "in", "when", "if", "else",
      "class", "interface", "return", "null", "true", "false", "typealias", "for", "while", "do", "as" };
}
