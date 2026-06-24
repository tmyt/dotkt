// facadegen — reads .NET type metadata via reflection and emits @Clr-annotated Kotlin façades,
// so a kotlin/clr program can call those .NET types with no hand-written façade.
//
//   facadegen <outDir> <Type.Full.Name> [<Type.Full.Name> ...]
//
// Emits <outDir>/clr/_Clr.kt (the annotation) and one <outDir>/clr/<Name>.kt per type.
using System.Reflection;
using System.Text;

static class FacadeGen
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: facadegen <outDir> <TypeFullName>...  |  facadegen --meta <outFile> <TypeFullName>...");
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
        var clrDir = Path.Combine(args[0], "clr");
        Directory.CreateDirectory(clrDir);
        File.WriteAllText(Path.Combine(clrDir, "_Clr.kt"),
            "package clr\n\n" +
            "@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY)\n" +
            "annotation class Clr(val name: String)\n");

        foreach (var typeName in args.Skip(1))
        {
            // Resolve directly, or as a generic type definition (List -> List`1, Dictionary -> `2).
            var t = Resolve(typeName) ?? Resolve(typeName + "`1") ?? Resolve(typeName + "`2");
            if (t == null) { Console.Error.WriteLine($"type not found: {typeName}"); continue; }
            var fileName = (t.Name.Contains('`') ? t.Name.Substring(0, t.Name.IndexOf('`')) : t.Name) + ".kt";
            File.WriteAllText(Path.Combine(clrDir, fileName), GenerateType(t));
            Console.WriteLine($"generated clr/{fileName}  <-  {t.FullName ?? t.Name}");
        }
        return 0;
    }

    static readonly string[] PROBE_ASSEMBLIES =
        { "System.Runtime", "System.Collections", "System.ObjectModel", "System.Private.CoreLib", "System.Console", "System.Runtime.Extensions", "System.Linq", "mscorlib" };

    // I2: referenced assemblies (Avalonia/WPF/NuGet) reflected via MetadataLoadContext — it reads
    // metadata WITHOUT executing, so it handles reference assemblies (ref/ folder) that LoadFrom rejects.
    static System.Reflection.MetadataLoadContext Mlc;

    static void LoadRefs(string[] paths)
    {
        if (paths.Length == 0) return;
        // The core assembly (System.Private.CoreLib / System.Runtime) must be in the path set.
        var core = new[] { "System.Private.CoreLib", "System.Runtime", "mscorlib", "netstandard" }
            .FirstOrDefault(n => paths.Any(p => Path.GetFileNameWithoutExtension(p).Equals(n, StringComparison.OrdinalIgnoreCase)))
            ?? "System.Runtime";
        Mlc = new System.Reflection.MetadataLoadContext(new System.Reflection.PathAssemblyResolver(paths), core);
        foreach (var p in paths)
            try { Mlc.LoadFromAssemblyPath(p); } catch { /* skip unloadable */ }
        ScanProjections();
    }

    // Kotlin-package <-> .NET-namespace projections declared by [assembly: DotKtNamespaceProjection] on the refs, so a
    // library in `DotKt.Coroutines` is consumed via `import kotlinx.coroutines.*`. Longest dotNet prefix first (so a
    // more specific mapping wins). Read once after the refs load.
    const string KNsProjAttr = "DotKt.Metadata.DotKtNamespaceProjectionAttribute";
    static readonly List<(string kotlin, string dotNet)> Projections = new();
    static void ScanProjections()
    {
        if (Mlc == null) return;
        foreach (var asm in Mlc.GetAssemblies())
            try
            {
                foreach (var c in asm.GetCustomAttributesData())
                    if (c.AttributeType.FullName == KNsProjAttr && c.ConstructorArguments.Count == 2)
                        Projections.Add(((string)c.ConstructorArguments[0].Value, (string)c.ConstructorArguments[1].Value));
            }
            catch { }
        Projections.Sort((a, b) => b.dotNet.Length.CompareTo(a.dotNet.Length));
    }

    // A consumer's import `kotlinx.coroutines.X` -> the real .NET name `DotKt.Coroutines.X` (reverse projection).
    static string ToDotNet(string kotlinName)
    {
        foreach (var (k, d) in Projections)
            if (kotlinName == k || kotlinName.StartsWith(k + ".")) return d + kotlinName.Substring(k.Length);
        return kotlinName;
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

    // Compact line-based metadata for the FIR injector (package fixed to `clrgen`):
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
        var dns = ToDotNet(ns);   // `import kotlinx.foo.*` scans the real .NET namespace `DotKt.Foo`
        var asms = Mlc != null ? Mlc.GetAssemblies() : Enumerable.Empty<Assembly>();
        var seen = new HashSet<string>();
        foreach (var asm in asms)
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch { continue; }
            foreach (var t in types)
            {
                if (t.Namespace != dns || !t.IsPublic || t.IsNested || t.FullName == null) continue;
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
        var sb = new StringBuilder();
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
        // Namespace projection: emit the active mappings so the injector exposes projected types under their Kotlin
        // package (`nsproj <kotlinPrefix> <dotNetPrefix>`), and resolve each import through the reverse projection.
        foreach (var (k, d) in Projections) sb.Append($"nsproj {k} {d}\n");
        var seeds = 0;
        foreach (var typeName in typeNames)
        {
            var dn = ToDotNet(typeName);   // `kotlinx.coroutines.X` -> the real .NET `DotKt.Coroutines.X` (identity if no projection)
            // Resolve a plain type, or a generic type definition (Collection -> Collection`1, etc.).
            var seed = Resolve(dn) ?? Resolve(dn + "`1") ?? Resolve(dn + "`2") ?? Resolve(dn + "`3");
            // A top-level function import (`import geom.greet`) isn't a type — resolve it to the [KotlinFileClass] facade
            // class that holds it (EmitOneType then emits `file`/`tlfun`). `import geom.*` already yields the facade
            // type directly via TypesInNamespace, so this only covers the single-function form.
            if (seed == null) seed = ResolveTopLevelFacade(dn);
            if (seed == null) { Console.Error.WriteLine($"warning: .NET import resolved to no type (injected nothing): {typeName}"); continue; }
            seeds++; Enqueue(seed);
        }
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            if (!done.Add(t.FullName!)) continue;
            EmitOneType(t, sb);
            if (done.Count >= CAP) { Console.Error.WriteLine($"warning: injection closure hit cap {CAP}; truncating reachable set"); break; }
            foreach (var r in ReferencedTypes(t))
                foreach (var u in Unwrap(r))
                    if (ShouldInject(u)) Enqueue(u);
        }
        Console.WriteLine($"closure: {seeds} seed(s) -> {done.Count} injected type(s)");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        File.WriteAllText(outFile, sb.ToString());
        return 0;
    }

    // Emit one type's FIR-injection metadata (enum/interface/annotation/object/class + members).
    static void EmitOneType(Type t, StringBuilder sb)
    {
            // A Kotlin file-facade ([KotlinFileClass]) -> its statics become TOP-LEVEL Kotlin functions, not a class.
            if (HasKotlinFileClass(t)) { EmitKotlinFileClass(t, sb); return; }
            // A .NET enum -> an object whose members are `val` properties typed as the enum itself
            // (avoids FIR enum-entry synthesis; `DayOfWeek.Friday` still maps to the real enum value).
            if (t.IsEnum)
            {
                sb.Append($"object {t.Name} {t.FullName}\n");
                foreach (var nm in Enum.GetNames(t)) sb.Append($"prop {nm} {t.Name} ro final\n");
                Console.WriteLine($"meta: {t.FullName} (enum)");
                return;
            }
            // A .NET interface -> Kotlin can IMPLEMENT it (methods become abstract members). Generic interfaces
            // (`IList`1`) -> simple name + OPEN .NET name + type-parameter tokens, mirroring the class path, so
            // `generic:IList:Foo` resolves to a real `interface IList<T>` (P1-2). `interface <Name> <DotNet> [<TP>...]`.
            if (t.IsInterface)
            {
                var iname = SimpleName(t);
                var idot = t.IsGenericTypeDefinition ? OpenName(t) : t.FullName;
                var itp = t.IsGenericTypeDefinition ? " " + string.Join(" ", t.GetGenericArguments().Select(g => g.Name)) : "";
                sb.Append($"interface {iname} {idot}{itp}\n");
                // Interface->interface supertypes (GENERIC only) so an injected `IList<T>` carries its inherited
                // members (`ICollection<T>.Add`, `IEnumerable<T>.GetEnumerator`) — the `IList<ResourceDictionary>.Add`
                // case (item 3). Non-generic shadows (IEnumerable) are skipped to avoid GetEnumerator generic-vs-
                // nongeneric overload clashes. Interface inheritance needs no member satisfaction, so it's safe.
                var isup = InterfaceSuperTypes(t);
                if (isup.Count > 0) sb.Append("super " + string.Join(" ", isup) + "\n");
                var iseen = new HashSet<string>();
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.IsSpecialName) continue;
                    // A GENERIC interface method (`U Convert<U>(object)`) is emitted with its own method type-parameter
                    // tokens, same as the class path: the frontend declares the type params and resolves the
                    // return/params against them, and fir2ir fake-overrides it onto an implementing class.
                    if (m.IsGenericMethod && !m.IsGenericMethodDefinition) continue;
                    var gp = m.IsGenericMethodDefinition ? m.GetGenericArguments().Select(g => g.Name).ToList() : new List<string>();
                    var ps = m.GetParameters();
                    if (!ps.All(p => Supported(p.ParameterType)) || !Supported(m.ReturnType)) continue;
                    if (!iseen.Add(m.Name + "<" + string.Join(",", gp) + ">(" + Sig(ps, t) + ")")) continue;
                    var toks = new List<string> { "fun", m.Name, MapRet(m.ReturnType, t), "abstract" };
                    toks.AddRange(gp);
                    toks.AddRange(ps.Select((p, i) => ParamTok(p, i, t)));
                    sb.Append(string.Join(" ", toks) + "\n");
                }
                // Interface properties (Count, IsReadOnly, ...) + indexer (`this[i]`) -> abstract members so member
                // access resolves on an interface-typed receiver.
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0 || !Supported(p.PropertyType) || !p.CanRead || !iseen.Add("prop:" + p.Name)) continue;
                    sb.Append($"prop {p.Name} {Map(p.PropertyType, t)} {(p.CanWrite ? "rw" : "ro")} abstract\n");
                }
                var iix = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(p => p.GetIndexParameters().Length == 1
                        && Supported(p.GetIndexParameters()[0].ParameterType) && Supported(p.PropertyType));
                if (iix != null)
                    sb.Append($"index {Map(iix.GetIndexParameters()[0].ParameterType, t)} {Map(iix.PropertyType, t)} {(iix.CanWrite ? "rw" : "ro")}\n");
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
                sb.Append($"annotation {t.Name} {t.FullName} {MetaParams(aps, t)}".TrimEnd() + "\n");
                Console.WriteLine($"meta: {t.FullName} (annotation)");
                return;
            }
            var isStatic = t.IsAbstract && t.IsSealed;
            // A generic type definition (`Collection`1`) -> simple name `Collection`, OPEN .NET name (namespace +
            // simple, no `1` — the backend appends the arity), and the type parameter names as trailing tokens.
            var simpleName = t.Name.Contains('`') ? t.Name.Substring(0, t.Name.IndexOf('`')) : t.Name;
            var dotNet = t.IsGenericTypeDefinition ? OpenName(t) : t.FullName;
            var tparams = t.IsGenericTypeDefinition ? " " + string.Join(" ", t.GetGenericArguments().Select(g => g.Name)) : "";
            // `class <Name> <DotNetName> <open|sealed> [<TypeParam>...]` carries inheritability + generic arity.
            sb.Append(isStatic ? $"object {simpleName} {dotNet}\n"
                               : $"class {simpleName} {dotNet} {(t.IsSealed ? "sealed" : "open")}{tparams}\n");
            // (1)(2) Supertypes: emit the injectable base class + interfaces so subtype assignability and inherited-
            // member access hold. Members declared in the contiguous injectable base chain ("covered") arrive via
            // those supertypes, so we skip re-declaring them (avoids fake-override clashes). `IM` includes protected.
            var covered = isStatic ? new HashSet<string>() : CoveredAncestors(t);
            const BindingFlags IM = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            if (!isStatic)
            {
                // Base class (assignability + inherited/protected members) + the interfaces this class fully and
                // publicly implements (assignable to an interface parameter, e.g. `Circle` -> `IShape`).
                var supers = SuperTypes(t).Concat(ClassInterfaceSuperTypes(t)).ToList();
                if (supers.Count > 0) sb.Append("super " + string.Join(" ", supers) + "\n");
                // The base edge is emitted for assignability even when the base has no accessible no-arg ctor (e.g.
                // WinUI UIElement, SafeHandle). In that case the injector must NOT synthesize a `: super()` delegating
                // call (it would fail — no no-arg base ctor); a façade ctor is never lowered anyway (clrNew). Mark it.
                if (t.BaseType != null && EmittableBase(t.BaseType) && !HasAccessibleNoArgCtor(t.BaseType))
                    sb.Append("basector none\n");
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
                    sb.Append($"ctor {MetaParams(ps, t)}".TrimEnd() + "\n");
                }
                // Instance properties (non-indexer). `prop <Name> <KType> <ro|rw> <prot-?open|final|abstract>`.
                foreach (var p in t.GetProperties(IM))
                {
                    if (Covered(p) || p.GetIndexParameters().Length > 0 || !Supported(p.PropertyType)) continue;
                    var prot = Vis(p.GetMethod); if (prot == null) continue;
                    if (!p.CanRead || !seen.Add("prop:" + p.Name)) continue;
                    var get = p.GetMethod;
                    var isAbstract = get?.IsAbstract ?? false;
                    var virt = (get?.IsVirtual ?? false) && !(get?.IsFinal ?? false);
                    sb.Append($"prop {p.Name} {Map(p.PropertyType, t)} {(p.CanWrite ? "rw" : "ro")} {Modifier(prot.Value, isAbstract, virt)}\n");
                }
                // DotKt round-trip: a Kotlin property's BACKING FIELD is emitted as a plain public field (no .NET
                // PropertyDef), and a CUSTOM-ACCESSOR property as `get_X`/`set_X` methods. Surface both as Kotlin `prop`s
                // (the consumer's clrPropGet/Set falls back to the field or calls the accessor). `accessorMembers` keeps
                // the get_/set_ methods out of the `fun` loop below.
                accessorMembers.Clear();
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (Covered(f) || !Supported(f.FieldType) || !seen.Add("prop:" + f.Name)) continue;
                    // `val`/`var ... private set` carries [KotlinReadOnly] -> `ro` (not publicly settable); initonly too.
                    var rw = (f.IsInitOnly || IsKotlinReadOnly(f)) ? "ro" : "rw";
                    sb.Append($"prop {f.Name} {Map(f.FieldType, t)} {rw} final\n");
                }
                foreach (var g in t.GetMethods(IM))
                {
                    if (g.IsSpecialName || !g.Name.StartsWith("get_") || g.GetParameters().Length != 0) continue;
                    if (Covered(g) || Vis(g) == null || !Supported(g.ReturnType)) continue;
                    var pn = g.Name.Substring(4);
                    if (!seen.Add("prop:" + pn)) continue;
                    var setter = t.GetMethods(IM).FirstOrDefault(m => !m.IsSpecialName && m.Name == "set_" + pn && m.GetParameters().Length == 1 && Vis(m) != null);
                    accessorMembers.Add(g.Name); if (setter != null) accessorMembers.Add(setter.Name);
                    var pv = Vis(g).Value;
                    sb.Append($"prop {pn} {MapRet(g.ReturnType, t)} {(setter != null ? "rw" : "ro")} {Modifier(pv, g.IsAbstract, g.IsVirtual && !g.IsFinal)}\n");
                }
                // MEMBER extension properties (`class C { val T.p get() }`): their accessors are `get_X(__self)` /
                // `set_X(__self, v)` member methods (a leading `__self` extension receiver, so the 0-param loop above
                // skipped them). `memextprop <name> <type> <ro|rw> <receiverType> <prot-?final>`.
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
                    sb.Append($"memextprop {pn} {MapRet(g.ReturnType, t)} {(setter != null ? "rw" : "ro")} {Map(gps[0].ParameterType, t)} {Modifier(prot.Value, g.IsAbstract, g.IsVirtual && !g.IsFinal)}\n");
                }
                // Events (I4). `event <Name> <handlerRetKType> <handlerParams...>` from the delegate's Invoke.
                foreach (var ev in t.GetEvents(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (Covered(ev)) continue;
                    var inv = ev.EventHandlerType?.GetMethod("Invoke");
                    if (inv == null || !seen.Add("event:" + ev.Name)) continue;
                    var ps = inv.GetParameters();
                    if (!ps.All(p => Supported(p.ParameterType)) || !Supported(inv.ReturnType)) continue;
                    sb.Append($"event {ev.Name} {Map(inv.ReturnType, t)} {MetaParams(ps, t)}".TrimEnd() + "\n");
                }
                // Indexer (`this[i]`) -> `index <indexKType> <valueKType> <ro|rw>`; injector synthesizes operator get/set.
                var ix = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(p => p.GetIndexParameters().Length == 1
                        && Supported(p.GetIndexParameters()[0].ParameterType) && Supported(p.PropertyType));
                if (ix != null)
                    sb.Append($"index {Map(ix.GetIndexParameters()[0].ParameterType, t)} {Map(ix.PropertyType, t)} {(ix.CanWrite ? "rw" : "ro")}\n");
                // IEnumerable<T> -> a FRONTEND-ONLY `operator fun iterator(): Iterator<T>` so `for (x in it)` resolves
                // unambiguously (otherwise the stdlib extension iterator()s clash). The backend ignores it and
                // enumerates via GetEnumerator/MoveNext/Current (forEachInline).
                Type ienum = null;
                try { ienum = t.GetInterfaces().FirstOrDefault(i => i.IsGenericType
                    && i.GetGenericTypeDefinition().FullName == "System.Collections.Generic.IEnumerable`1"
                    && Supported(i.GetGenericArguments()[0])); } catch { }
                if (ienum != null)
                    sb.Append($"iterator {Map(ienum.GetGenericArguments()[0], t)}\n");
                // Public STATIC members of a NORMAL class (it also has instance members) -> companion-object members,
                // so `App.Start(cb)` / `App.Current` resolve. `sfun`/`sprop` lines; the injector puts them on a
                // synthesized companion and the backend emits .NET static calls. (Feedback: WinUI Application.Start.)
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                    if (Supported(f.FieldType) && seen.Add("sfield:" + f.Name))
                        sb.Append($"sprop {f.Name} {Map(f.FieldType, t)} ro\n");
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Static))
                    if (p.GetIndexParameters().Length == 0 && Supported(p.PropertyType) && p.CanRead && seen.Add("sprop:" + p.Name))
                        sb.Append($"sprop {p.Name} {Map(p.PropertyType, t)} {(p.CanWrite ? "rw" : "ro")}\n");
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.IsSpecialName || OBJECT_MEMBERS.Contains(m.Name) || m.IsGenericMethod) continue;
                    var sps = m.GetParameters();
                    if (!sps.All(p => Supported(p.ParameterType)) || !Supported(m.ReturnType)) continue;
                    if (!seen.Add("sm:" + m.Name + "(" + Sig(sps, t) + ")")) continue;
                    sb.Append($"sfun {m.Name} {MapRet(m.ReturnType, t)} {MetaParams(sps, t)}".TrimEnd() + "\n");
                }
            }
            else
            {
                // Static fields/consts (e.g. Math.PI) and static properties -> read-only `prop`s on the object.
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!Supported(f.FieldType) || !seen.Add("sfield:" + f.Name)) continue;
                    sb.Append($"prop {f.Name} {Map(f.FieldType, t)} ro final\n");
                }
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Static))
                {
                    if (p.GetIndexParameters().Length > 0 || !Supported(p.PropertyType) || !p.CanRead || !seen.Add("sprop:" + p.Name)) continue;
                    sb.Append($"prop {p.Name} {Map(p.PropertyType, t)} {(p.CanWrite ? "rw" : "ro")} final\n");
                }
            }
            var flags = BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            foreach (var m in t.GetMethods(flags))
            {
                if (m.IsSpecialName) continue;
                if (accessorMembers.Contains(m.Name)) continue;   // already surfaced as a `prop`
                if (OBJECT_MEMBERS.Contains(m.Name)) continue;
                if (m.DeclaringType?.FullName == "System.Object") continue;
                if (Covered(m)) continue;                 // arrives via an injected supertype
                var prot = Vis(m); if (prot == null) continue;   // skip private/internal; keep public + protected
                // A generic method (`SizeOf<T>()`) is now emitted: its method type params + any T-typed param/return
                // map to the param names (Map returns the generic-parameter Name). Only simple shapes survive Supported.
                if (m.IsGenericMethod && !m.IsGenericMethodDefinition) continue;
                var gp = m.IsGenericMethodDefinition ? m.GetGenericArguments().Select(g => g.Name).ToList() : new List<string>();
                var ps = m.GetParameters();
                // DotKt round-trip: a `suspend fun` is emitted returning Task<T>; restore the result type T and gate on it.
                var k = KotlinFun(m);
                var retOk = k.suspend ? SuspendRetSupported(m.ReturnType) : Supported(m.ReturnType);
                if (!ps.All(p => Supported(p.ParameterType)) || !retOk) continue;
                if (!seen.Add(m.Name + "<" + string.Join(",", gp) + ">(" + Sig(ps, t) + ")")) continue;
                // `fun <Name> <ret> <prot-?open|final|abstract>[,infix][,operator][,suspend] [<TypeParam>...] [<param>:<type>]*`
                // — the modifier stays a single whitespace-free token; bare trailing tokens (no `:`) are type params.
                var virt = m.IsVirtual && !m.IsFinal;
                var nmask = KotlinNullMask(m);   // Kotlin nullability ([KotlinNullable]): `?` on return (bit 0) / params (bit i+1)
                var retTok = (k.suspend ? SuspendRetToken(m.ReturnType, t) : MapRet(m.ReturnType, t)) + NullSuffix(nmask, 0);
                // A MEMBER extension function (`class C { fun T.f() }`) -> first param `__self`; `,ext` so the injector
                // restores the extension receiver. `,inline` carries the spliceable body (composes with suspend/generic).
                var isExt = ps.Length > 0 && ps[0].Name == "__self";
                var mod = FunModifier(Modifier(prot.Value, m.IsAbstract, virt), k) + (KotlinInlineBody(m) != null ? ",inline" : "") + (isExt ? ",ext" : "");
                var toks = new List<string> { "fun", m.Name, retTok, mod };
                toks.AddRange(gp);
                toks.AddRange(ps.Select((p, i) => ParamTok(p, i, t, nmask)));
                sb.Append(string.Join(" ", toks) + "\n");
            }
            // (explicit impl) Emit concrete stubs for in-scope members of the generic interfaces this class implements
            // but doesn't expose PUBLICLY (e.g. `List<T>.IsReadOnly`, an explicit `ICollection<T>` impl). `c : I` in
            // .NET guarantees `c` implements every member of `I`, so we can always stub the non-public ones — that
            // makes the injected class satisfy `ICollection<T>`/`IList<T>` (so `List<T>` is assignable to them) with no
            // abstract member left for a user subclass. The backend resolves the call through the interface (P1-2).
            if (!isStatic) EmitExplicitInterfaceStubs(t, sb, seen);
            Console.WriteLine($"meta: {t.FullName} ({(isStatic ? "object" : "class")})");
    }

    static void EmitExplicitInterfaceStubs(Type t, StringBuilder sb, HashSet<string> seen)
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
                    sb.Append($"prop {p.Name} {Map(p.PropertyType, t)} {(p.CanWrite ? "rw" : "ro")} final\n");
                }
                foreach (var m in i.GetMethods())
                {
                    if (m.IsSpecialName || m.IsStatic || m.IsGenericMethod) continue;
                    var ps = m.GetParameters();
                    if (!ps.All(x => Supported(x.ParameterType)) || !Supported(m.ReturnType)) continue;
                    if (!seen.Add(m.Name + "<>(" + Sig(ps, t) + ")")) continue;   // already a public member -> skip
                    sb.Append($"fun {m.Name} {MapRet(m.ReturnType, t)} final {MetaParams(ps, t)}".TrimEnd() + "\n");
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
            if (m.IsSpecialName || OBJECT_MEMBERS.Contains(m.Name) || m.DeclaringType?.FullName == "System.Object") continue;
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
    // codegen. Members referencing them degrade to Any?, exactly as if never reached.
    static readonly HashSet<string> NO_INJECT = new()
    { "System.Void", "System.Object", "System.String", "System.Int32", "System.Int64", "System.Int16",
      "System.Byte", "System.Boolean", "System.Double", "System.Single", "System.Char", "System.Span`1",
      "System.Delegate", "System.MulticastDelegate", "System.ValueType", "System.Enum", "System.Array" };

    // Inject only real, named, resolvable types — including generic DEFINITIONS (List`1, IList`1) so a
    // `generic:List:Foo` member type resolves to a real `class List<T>` (P1-2). Constructed generics are unwrapped
    // to (open def + args) by [Unwrap] before this check.
    static bool ShouldInject(Type t)
    {
        if (t == null || t.IsGenericParameter || t.IsPointer || t.IsByRef) return false;
        if (string.IsNullOrEmpty(t.Namespace) || t.FullName == null) return false;
        return !NO_INJECT.Contains(t.FullName);
    }

    // (1) A type usable as an injected supertype: a real, non-generic, co-injectable class/interface (not Object).
    // Generic bases/interfaces (`Bar<int>`) wait for P1-2; until then `t` keeps the inherited members flattened.
    static bool IsInjectableSupertype(Type t) =>
        t != null && !t.IsGenericType && !string.IsNullOrEmpty(t.Namespace) && t.FullName != null
        && t.FullName != "System.Object" && !NO_INJECT.Contains(t.FullName);

    // A base CLASS supertype edge is emitted purely for ASSIGNABILITY (is-a): e.g. a WinUI `TextBlock` must be usable
    // where `UIElement` is expected. This is INDEPENDENT of whether the base is constructible — injected façade
    // instances come from .NET (method returns etc.), and `Foo()` lowers to native `new Foo()` (clrNew, which chains
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

    // OPEN .NET name of a generic type definition: namespace + simple name, WITHOUT the `<arity> suffix (the backend
    // appends it). `t.FullName` carries the arity, so we rebuild it — but for a ROOT-namespace type `t.Namespace` is
    // null, and `null + "." + "Box"` would yield the broken `.Box` (a leading dot the consumer's ilemit can't resolve).
    static string OpenName(Type t) => string.IsNullOrEmpty(t.Namespace) ? SimpleName(t) : t.Namespace + "." + SimpleName(t);

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
    static List<string> InterfaceSuperTypes(Type t)
    {
        Type[] all; try { all = t.GetInterfaces(); } catch { return new List<string>(); }
        var implied = new HashSet<Type>();
        foreach (var i in all) { try { foreach (var sub in i.GetInterfaces()) implied.Add(sub); } catch { } }
        var supers = new List<string>(); var seen = new HashSet<string>();
        foreach (var i in all)
        {
            if (implied.Contains(i) || !i.IsGenericType) continue;   // direct + generic only
            var enc = Map(i, t);                                      // -> "generic:Open:args" for a constructed generic
            if (enc.StartsWith("generic:") && seen.Add(enc)) supers.Add(enc);
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

    static List<string> ClassInterfaceSuperTypes(Type c)
    {
        // The MAXIMAL set of interfaces `c` satisfies. We consider ALL implemented interfaces, not just "direct" ones,
        // so e.g. `List<T>` links every interface it implements (`IList<T>`, `IReadOnlyList<T>`, ...); the explicit
        // members (`IsReadOnly`) are filled by EmitExplicitInterfaceStubs.
        var sat = SatisfiableInterfaces(c);
        // Drop any satisfiable interface implied by another satisfiable one (avoid redundant supertype edges).
        var implied = new HashSet<Type>();
        foreach (var i in sat) { try { foreach (var s in i.GetInterfaces()) implied.Add(s); } catch { } }
        var supers = new List<string>(); var seen = new HashSet<string>();
        foreach (var i in sat)
        {
            if (implied.Contains(i)) continue;
            var enc = i.IsGenericType ? Map(i, c) : SimpleName(i);
            if (enc == "Any?" || (i.IsGenericType && !enc.StartsWith("generic:"))) continue;
            if (seen.Add(enc)) supers.Add(enc);
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
    static List<string> SuperTypes(Type t)
    {
        var supers = new List<string>();
        if (t.BaseType != null && EmittableBase(t.BaseType)) supers.Add(SimpleName(t.BaseType));
        return supers;
    }

    // (2) Combined visibility+modality token for a member: optional `prot-` (protected) prefix + abstract|open|final.
    // A single space-free token so it can't be mistaken for a trailing type-parameter token in the `fun` line.
    static string Modifier(bool prot, bool isAbstract, bool isVirtual)
    {
        var m = isAbstract ? "abstract" : (isVirtual ? "open" : "final");
        return prot ? "prot-" + m : m;
    }

    // ----- DotKt metadata: restore Kotlin modifiers a DotKt-compiled assembly stamped (no .NET analog) -----
    const string KFuncAttr = "DotKt.Metadata.KotlinFunctionAttribute";
    const string KFileAttr = "DotKt.Metadata.KotlinFileClassAttribute";

    // The KotlinFunctionFlags carried by a method's [KotlinFunction] (Infix=1, Operator=2, Suspend=4), or 0/none.
    static (bool infix, bool op, bool suspend) KotlinFun(MethodInfo m)
    {
        try
        {
            foreach (var cad in m.GetCustomAttributesData())
                if (cad.AttributeType.FullName == KFuncAttr && cad.ConstructorArguments.Count == 1)
                {
                    var f = Convert.ToInt32(cad.ConstructorArguments[0].Value);
                    return ((f & 1) != 0, (f & 2) != 0, (f & 4) != 0);
                }
        }
        catch { /* DotKt.Runtime not in the resolver set -> no Kotlin modifiers to restore */ }
        return (false, false, false);
    }

    static bool HasKotlinFileClass(Type t)
    {
        try { return t.GetCustomAttributesData().Any(c => c.AttributeType.FullName == KFileAttr); }
        catch { return false; }
    }

    const string KReadOnlyAttr = "DotKt.Metadata.KotlinReadOnlyAttribute";
    static bool IsKotlinReadOnly(FieldInfo f)
    {
        try { return f.GetCustomAttributesData().Any(c => c.AttributeType.FullName == KReadOnlyAttr); }
        catch { return false; }
    }

    const string KNullableAttr = "DotKt.Metadata.KotlinNullableAttribute";
    // The Kotlin nullability bitmask carried by [KotlinNullable] (bit 0 = return, bit i+1 = param i), or 0.
    static uint KotlinNullMask(MethodInfo m)
    {
        try
        {
            foreach (var c in m.GetCustomAttributesData())
                if (c.AttributeType.FullName == KNullableAttr && c.ConstructorArguments.Count == 1)
                    return Convert.ToUInt32(c.ConstructorArguments[0].Value);
        }
        catch { }
        return 0;
    }
    static string NullSuffix(uint mask, int bit) => ((mask >> bit) & 1) != 0 ? "?" : "";

    const string KInlineAttr = "DotKt.Metadata.KotlinInlineAttribute";
    // The carried BIR body of an inline+lambda fn ([KotlinInline]), or null. Splice-able by a consuming module.
    static string KotlinInlineBody(MethodInfo m)
    {
        try
        {
            foreach (var cad in m.GetCustomAttributesData())
                if (cad.AttributeType.FullName == KInlineAttr && cad.ConstructorArguments.Count == 1)
                    return cad.ConstructorArguments[0].Value as string;
        }
        catch { }
        return null;
    }

    // A `suspend fun` is emitted returning Task / Task<T>; restore the Kotlin result type and gate Supported on it.
    static bool IsTask1(Type t) => t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Threading.Tasks.Task`1";
    static bool SuspendRetSupported(Type ret) => IsTask1(ret) ? Supported(ret.GetGenericArguments()[0]) : ret.FullName == "System.Threading.Tasks.Task";
    static string SuspendRetToken(Type ret, Type self) => IsTask1(ret) ? MapRet(ret.GetGenericArguments()[0], self) : "Unit";

    // Build the `fun`/`tlfun`/`sfun` modifier token, folding in the no-.NET-analog Kotlin flags as comma-suffixes
    // (`final,infix`, `open,suspend`, ...) — kept a SINGLE whitespace-free token so the meta parser's type-param
    // split (bare trailing tokens) is unaffected.
    static string FunModifier(string baseMod, (bool infix, bool op, bool suspend) k)
    {
        if (k.infix) baseMod += ",infix";
        if (k.op) baseMod += ",operator";
        if (k.suspend) baseMod += ",suspend";
        return baseMod;
    }

    // Emit a Kotlin file-facade class ([KotlinFileClass]) as TOP-LEVEL functions in its .NET namespace (= Kotlin package),
    // instead of a class: `file <package>` then a `tlfun` per public static method (Main and object members skipped).
    static void EmitKotlinFileClass(Type t, StringBuilder sb)
    {
        // `file <package> <fileClassFqn>` — the package is the Kotlin namespace; the .NET FQN is where the backend
        // emits the static call for each restored top-level function. Empty package ("") is the root package.
        sb.Append($"file {(string.IsNullOrEmpty(t.Namespace) ? "-" : t.Namespace)} {t.FullName}\n");
        var seen = new HashSet<string>();
        // Extension properties (`val T.p`) compile to top-level `get_p(__self: T)` (+ `set_p(__self, v)` for `var`). Surface
        // them as `tlextprop <name> <type> <ro|rw> <receiverType>` so the injector restores `val/var T.p`; the consumer's
        // `x.p` then routes to the static get_/set_. These accessor methods are kept out of the `tlfun` loop below.
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
            var nm = KotlinNullMask(g);
            sb.Append($"tlextprop {pn} {MapRet(g.ReturnType, t)}{NullSuffix(nm, 0)} {(setter != null ? "rw" : "ro")} {Map(gps[0].ParameterType, t)}\n");
        }
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (m.IsSpecialName || m.Name == "Main" || OBJECT_MEMBERS.Contains(m.Name) || extPropMembers.Contains(m.Name)) continue;
            if (m.IsGenericMethod && !m.IsGenericMethodDefinition) continue;
            var gp = m.IsGenericMethodDefinition ? m.GetGenericArguments().Select(g => g.Name).ToList() : new List<string>();
            var ps = m.GetParameters();
            var k = KotlinFun(m);
            var retOk = k.suspend ? SuspendRetSupported(m.ReturnType) : Supported(m.ReturnType);
            if (!ps.All(p => Supported(p.ParameterType)) || !retOk) continue;
            if (!seen.Add(m.Name + "<" + string.Join(",", gp) + ">(" + Sig(ps, t) + ")")) continue;
            var nmask = KotlinNullMask(m);   // Kotlin nullability ([KotlinNullable]): `?` on return (bit 0) / params (bit i+1)
            var ret = (k.suspend ? SuspendRetToken(m.ReturnType, t) : MapRet(m.ReturnType, t)) + NullSuffix(nmask, 0);
            // `,inline` tells the injector to mark the fn `inline` (so a non-local return through the lambda is accepted);
            // the body itself stays in the assembly's [KotlinInline] and is read by the consumer's ilemit at splice time.
            // An extension fun's receiver is emitted as the first param `__self` (DotKt convention) -> mark `,ext` so the
            // injector restores it as an extension receiver (composes with operator -> top-level extension operators).
            var isExt = ps.Length > 0 && ps[0].Name == "__self";
            var mod = FunModifier("final", k) + (KotlinInlineBody(m) != null ? ",inline" : "") + (isExt ? ",ext" : "");
            var toks = new List<string> { "tlfun", m.Name, ret, mod };
            toks.AddRange(gp);
            toks.AddRange(ps.Select((p, i) => ParamTok(p, i, t, nmask)));
            sb.Append(string.Join(" ", toks) + "\n");
        }
        Console.WriteLine($"meta: {t.FullName} (kotlin file -> top-level)");
    }

    // null => skip (private/internal); false => public; true => protected (Family / protected-internal). Frameworks
    // (WinUI/WPF/Avalonia) override protected virtual lifecycle methods, so these MUST be injected (item 2).
    static bool? Vis(MethodBase m) =>
        m == null ? null : m.IsPublic ? false : (m.IsFamily || m.IsFamilyOrAssembly) ? true : (bool?)null;

    // A method's RETURN type: a `ref T` return surfaces as plain `T` (a plain `val x = m()` is a value copy; the live
    // ref is captured only via `byref(m())`). Parameters keep their byref (-> ClrRef<T>) via Map.
    static string MapRet(Type t, Type self) => Map(t.IsByRef ? t.GetElementType() : t, self);

    static string MetaParams(ParameterInfo[] ps, Type self) =>
        string.Join(" ", ps.Select((p, i) => ParamTok(p, i, self)));

    // `<name>:<type>` for a meta param; a [ParamArray] (Kotlin `vararg`) -> `<name>:vararg:<elementType>` so the
    // injector restores a `vararg <name>: <elem>` (a cross-module consumer can then call `f(1, 2, 3)`).
    static string ParamTok(ParameterInfo p, int i, Type self, uint nmask = 0)
    {
        // `?` (nullable) rides the END of the type token (the injector strips it); param i is bit (i+1) of the mask.
        var nul = NullSuffix(nmask, i + 1);
        if (p.ParameterType.IsArray && IsParamArray(p))
            return $"{MetaParamName(p, i)}:vararg:{Map(p.ParameterType.GetElementType(), self)}{nul}";
        var t = Map(p.ParameterType, self);
        // A Kotlin default arg ([Optional]+DefaultParameterValue) -> `opt:<type>=<constant>` so the injector restores a
        // REAL default value (the consumer can omit it ANYWHERE, incl. a named middle omission `f(c=9)` — fir2ir inlines
        // the constant). The value rides the token (spaces escaped) so the meta's space-split is unaffected.
        if (HasDefault(p)) t = "opt:" + t + "=" + EncodeDefault(p.RawDefaultValue);
        return $"{MetaParamName(p, i)}:{t}{nul}";
    }
    // Encode a constant default value for the meta token: `\` -> `\\`, ` ` -> `\s` (so a String default with spaces
    // stays one whitespace-free token); `null` -> `\0`. bool lowercased to match Kotlin. The injector decodes + builds
    // a FirLiteralExpression of the param's type.
    static string EncodeDefault(object v)
    {
        if (v == null) return "\\0";   // null marker (a real string is backslash-escaped, so it can never produce `\0`)
        string s = v is bool b ? (b ? "true" : "false") : v.ToString() ?? "";
        return s.Replace("\\", "\\\\").Replace(" ", "\\s");
    }
    static bool HasDefault(ParameterInfo p) { try { return p.HasDefaultValue && !p.IsOut; } catch { return false; } }
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

    static string GenerateType(Type t)
    {
        var sb = new StringBuilder();
        sb.Append("package clr\n\n");
        // Generic type definitions (List`1) -> a generic Kotlin façade (`class List<T>`).
        var simpleName = t.Name.Contains('`') ? t.Name.Substring(0, t.Name.IndexOf('`')) : t.Name;
        var clrName = t.IsGenericTypeDefinition ? OpenName(t) : t.FullName;
        var typeParams = t.IsGenericTypeDefinition
            ? "<" + string.Join(", ", t.GetGenericArguments().Select(g => g.Name)) + ">"
            : "";
        sb.Append($"@Clr(\"{clrName}\")\n");
        sb.Append($"class {simpleName}{typeParams} {{\n");
        var seen = new HashSet<string>();

        // Indexer (this[i]) -> Kotlin operator get/set.
        var indexer = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.GetIndexParameters().Length == 1 && Supported(p.GetIndexParameters()[0].ParameterType) && Supported(p.PropertyType));
        if (indexer != null)
        {
            var it = Map(indexer.GetIndexParameters()[0].ParameterType, t);
            var vt = Map(indexer.PropertyType, t);
            sb.Append($"\toperator fun get(index: {it}): {vt} = TODO()\n");
            if (indexer.CanWrite) sb.Append($"\toperator fun set(index: {it}, value: {vt}): Unit = TODO()\n");
        }

        foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            var ps = c.GetParameters();
            if (!ps.All(p => Supported(p.ParameterType))) continue;
            if (!seen.Add("ctor(" + Sig(ps, t) + ")")) continue;
            sb.Append($"\tconstructor({KParams(ps, t)})\n");
        }

        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || !Supported(p.PropertyType)) continue;
            var nm = Decap(p.Name);
            if (!seen.Add("prop:" + nm)) continue;
            var kt = Map(p.PropertyType, t);
            if (p.CanWrite)
                sb.Append($"\t@Clr(\"{p.Name}\") var {nm}: {kt} get() = TODO(); set(value) {{}}\n");
            else
                sb.Append($"\t@Clr(\"{p.Name}\") val {nm}: {kt} get() = TODO()\n");
        }

        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.IsSpecialName || m.IsGenericMethod) continue;
            // Skip Object identity members that would clash with Kotlin's Any (keep ToString).
            if (OBJECT_MEMBERS.Contains(m.Name)) continue;
            if (m.DeclaringType?.FullName == "System.Object" && m.Name != "ToString") continue;
            var ps = m.GetParameters();
            if (!ps.All(p => Supported(p.ParameterType)) || !Supported(m.ReturnType)) continue;
            var nm = Decap(m.Name);
            if (!seen.Add(nm + "(" + Sig(ps, t) + ")")) continue;
            if (m.Name == "ToString" && ps.Length == 0)
                sb.Append("\t@Clr(\"ToString\") override fun toString(): String = TODO()\n");
            else
                sb.Append($"\t@Clr(\"{m.Name}\") fun {nm}({KParams(ps, t)}): {Map(m.ReturnType, t)} = TODO()\n");
        }

        var statics = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => !m.IsSpecialName && !m.IsGenericMethod).ToList();
        if (statics.Count > 0)
        {
            sb.Append($"\n\t@Clr(\"{t.FullName}\")\n\tcompanion object {{\n");
            var seenS = new HashSet<string>();
            foreach (var m in statics)
            {
                var ps = m.GetParameters();
                if (!ps.All(p => Supported(p.ParameterType)) || !Supported(m.ReturnType)) continue;
                var nm = Decap(m.Name);
                if (!seenS.Add(nm + "(" + Sig(ps, t) + ")")) continue;
                sb.Append($"\t\t@Clr(\"{m.Name}\") fun {nm}({KParams(ps, t)}): {Map(m.ReturnType, t)} = TODO()\n");
            }
            sb.Append("\t}\n");
        }

        sb.Append("}\n");
        return sb.ToString();
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

    // Map a .NET type to a Kotlin façade type. Primitives map precisely; generic params map to their
    // name (T); the type itself maps to its façade; everything else degrades to Any?.
    static string Map(Type t, Type self)
    {
        // An `out`/`ref` param or a `ref`-returning method (T&) surfaces as the intrinsic `ClrRef<T>` (meta `byref:T`):
        // the caller wraps an arg in `byref(x)`, and a ref return is `by`-delegatable.
        if (t.IsByRef) return "byref:" + Map(t.GetElementType(), self);
        // Compare by FullName, not typeof identity: types reflected from LoadFrom'd reference assemblies
        // (I2) have a different assembly identity than the runtime's, so `t == typeof(...)` would miss.
        if (t.FullName == "System.Void") return "Unit";
        if (t.IsGenericParameter) return t.Name;
        // A .NET `Span<T>` parameter -> the intrinsic `Span<T>` (meta `span:T`); the caller can pass `buf.asSpan()`.
        if (t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Span`1")
            return "span:" + Map(t.GetGenericArguments()[0], self);
        // (4) A .NET delegate type -> a Kotlin function type `func:<ret>:<arg>,<arg>` (meta/injection path only).
        // A lambda then binds to the delegate parameter and overloads disambiguate by arity; the backend builds the
        // SPECIFIC delegate from the parameter type resolved at the call site (so the delegate name isn't needed in
        // the metadata). Args/ret may themselves be compound (`generic:Box[V]`) — the bracketed grammar nests them.
        if (MetaMode && IsDelegate(t))
        {
            var inv = t.GetMethod("Invoke");
            if (inv != null && inv.GetParameters().All(p => Supported(p.ParameterType)) && Supported(inv.ReturnType))
            {
                var dret = MapRet(inv.ReturnType, self);
                var dps = inv.GetParameters().Select(p => Map(p.ParameterType, self)).ToList();
                // `func:[ret,arg,arg]` — bracketed so a compound child (its own `[...]`) keeps its commas; the
                // injector splits at bracket-depth 0. Only an unresolved child (`Any?`) sinks the whole delegate.
                if (dret != "Any?" && dps.All(a => a != "Any?"))
                    return "func:[" + string.Join(",", new[] { dret }.Concat(dps)) + "]";
            }
            return "Any?";
        }
        if (t.FullName == self.FullName) return self.Name.Contains('`') ? self.Name.Substring(0, self.Name.IndexOf('`')) : self.Name;
        return t.FullName switch
        {
            "System.Int32" => "Int",
            "System.Int64" => "Long",
            "System.Int16" => "Short",
            // Unsigned .NET primitives map to Kotlin's unsigned types (System.UInt32 == kotlin.UInt, etc.). Without
            // these they fell to CrossType -> the bare name "UInt32", which doesn't unify with `UInt` (e.g. a `uint`
            // parameter like WinUI's `Bootstrap.Initialize(uint)`).
            "System.UInt32" => "UInt",
            "System.UInt64" => "ULong",
            "System.UInt16" => "UShort",
            // Kotlin `Byte` is signed (== System.SByte). System.Byte is unsigned (strictly Kotlin `UByte`), but we map
            // it to `Byte` so Int literals stay assignable (e.g. `Stream.WriteByte(65)`); the bit pattern matches for
            // 0..127. (Revisit if a use needs the full 0..255 range typed as UByte.)
            "System.SByte" => "Byte",
            "System.Byte" => "Byte",
            "System.Boolean" => "Boolean",
            "System.Double" => "Double",
            "System.Single" => "Float",
            "System.Char" => "Char",
            "System.String" => "String",
            "System.Object" => "Any?",
            _ => MetaMode ? CrossType(t) : "Any?",
        };
    }

    // A reference to another .NET type -> emit its SIMPLE name. The FIR injector resolves it to that type IF it is
    // also injected (imported); otherwise it falls back to Any?. Generics/arrays/byref/global types stay Any? here.
    static string CrossType(Type t)
    {
        if (t.IsArray) { var e = Map(t.GetElementType(), t); return e == "Any?" ? "Any?" : "array:" + e; }
        // A root-namespace GENERIC user type (`Box<T>`, t.Namespace empty) is handled by the generic branch below; only
        // reject an empty namespace for NON-generic types (a global/compiler type with no useful injectable identity).
        if (t.IsByRef || t.IsPointer || t.IsGenericParameter || (string.IsNullOrEmpty(t.Namespace) && !t.IsGenericType)) return "Any?";
        // (3) A constructed generic (`IList<ResourceDictionary>`) -> `generic:<OpenSimple>[<arg>,<arg>]` so the
        // injector resolves it to `IList<ResourceDictionary>` (chained `.Add`/`for-in` work). Requires the open def
        // injectable; args may be compound (nested `generic:`/`func:`) — the bracketed grammar nests them recursively.
        if (t.IsGenericType)
        {
            var open = t.GetGenericTypeDefinition();
            var openName = SimpleName(open);
            // A root-namespace open def (`open.Namespace` null) is a legitimately injectable user type (`Box<T>`), not a
            // global/compiler type — only reject the explicitly non-injectable ones and names that aren't a simple ident.
            if (NO_INJECT.Contains(open.FullName ?? "")
                || !openName.All(c => char.IsLetterOrDigit(c) || c == '_')) return "Any?";
            var args = t.GetGenericArguments().Select(a => Map(a, t)).ToList();
            // `generic:Open[arg,arg]` — bracketed so a compound arg (`generic:Inner[X]`, `func:[...]`) nests; the
            // injector splits at bracket-depth 0. Only an unresolved arg (`Any?`) sinks the whole type.
            if (args.Any(a => a == "Any?")) return "Any?";
            return "generic:" + openName + "[" + string.Join(",", args) + "]";
        }
        // Emit the FULLY-QUALIFIED name so the injector resolves the EXACT type, not whichever same-simple-name type
        // from another namespace won the dedup (e.g. Microsoft.UI.Xaml.LaunchActivatedEventArgs vs the UWP
        // Windows.ApplicationModel.Activation.LaunchActivatedEventArgs — feedback item 2). Fall back to the simple
        // name for nested types (FullName has '+') so they at least resolve as before.
        var fqn = t.FullName;
        if (fqn != null && fqn.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '.')) return fqn;
        var n = t.Name;
        return n.All(c => char.IsLetterOrDigit(c) || c == '_') ? n : "Any?";
    }

    static string Sig(ParameterInfo[] ps, Type self) => string.Join(",", ps.Select(p => Map(p.ParameterType, self)));

    static string KParams(ParameterInfo[] ps, Type self) =>
        string.Join(", ", ps.Select((p, i) => $"{ParamName(p, i)}: {Map(p.ParameterType, self)}"));

    static string ParamName(ParameterInfo p, int i)
    {
        var n = p.Name;
        if (string.IsNullOrEmpty(n) || !IsIdent(n)) n = "arg" + i;
        return KEYWORDS.Contains(n) ? "`" + n + "`" : n;
    }

    static string Decap(string s) => string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);
    static bool IsIdent(string s) => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_');

    static readonly HashSet<string> OBJECT_MEMBERS = new()
    { "Equals", "GetHashCode", "GetType", "ReferenceEquals", "Finalize", "MemberwiseClone", "Clone" };

    static readonly HashSet<string> KEYWORDS = new()
    { "value", "object", "fun", "val", "var", "this", "is", "in", "when", "if", "else",
      "class", "interface", "return", "null", "true", "false", "typealias", "for", "while", "do", "as" };
}
