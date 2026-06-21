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
            // C-2: explicit type names, then optionally `--scan <ktfile>...` which extracts `import Ns.Type` lines
            // from the Kotlin sources. Merge both. EmitMeta silently skips non-.NET imports (kotlin.*, own
            // packages), so a bare `import System.Text.StringBuilder` resolves — no manual <DotKtImport> needed.
            var scanAt = rest.IndexOf("--scan");
            var explicitTypes = scanAt < 0 ? rest : rest.Take(scanAt).ToList();
            var scanned = scanAt < 0 ? Enumerable.Empty<string>() : ScanImports(rest.Skip(scanAt + 1));
            return EmitMeta(args[1], explicitTypes.Concat(scanned).Distinct());
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
    // C-2: extract explicit `.NET` imports from Kotlin sources. Matches `import A.B.C` (dotted, >=2 segments);
    // excludes wildcard (`.*`), aliased (`as`), and Kotlin/own-façade imports — those aren't injectable .NET types.
    static IEnumerable<string> ScanImports(IEnumerable<string> ktFiles)
    {
        // `import A.B.C` (dotted, >=2 segments), allowing a trailing line comment (`// …`) and trailing whitespace.
        var re = new System.Text.RegularExpressions.Regex(@"^\s*import\s+([A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)+)\s*(?://.*)?$");
        var seen = new HashSet<string>();
        foreach (var f in ktFiles)
        {
            if (!File.Exists(f)) continue;
            foreach (var line in File.ReadLines(f))
            {
                var m = re.Match(line);
                if (!m.Success) continue;
                var imp = m.Groups[1].Value;
                if (imp.StartsWith("kotlin") || imp.StartsWith("clr.") || imp.StartsWith("java.")) continue;
                if (seen.Add(imp)) yield return imp;
            }
        }
    }

    static int EmitMeta(string outFile, IEnumerable<string> typeNames)
    {
        MetaMode = true;   // enable array/cross-type member support for the FIR-injection path
        var sb = new StringBuilder();
        // types resolve at their real .NET namespace; no synthetic package header
        foreach (var typeName in typeNames)
        {
            // Resolve a plain type, or a generic type definition (Collection -> Collection`1, etc.).
            var t = Resolve(typeName) ?? Resolve(typeName + "`1") ?? Resolve(typeName + "`2") ?? Resolve(typeName + "`3");
            if (t == null) { Console.Error.WriteLine($"type not found: {typeName}"); continue; }
            // A .NET enum -> an object whose members are `val` properties typed as the enum itself
            // (avoids FIR enum-entry synthesis; `DayOfWeek.Friday` still maps to the real enum value).
            if (t.IsEnum)
            {
                sb.Append($"object {t.Name} {t.FullName}\n");
                foreach (var nm in Enum.GetNames(t)) sb.Append($"prop {nm} {t.Name} ro final\n");
                Console.WriteLine($"meta: {t.FullName} (enum)");
                continue;
            }
            // A .NET interface -> Kotlin can IMPLEMENT it (methods become abstract members).
            if (t.IsInterface)
            {
                sb.Append($"interface {t.Name} {t.FullName}\n");
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.IsSpecialName || m.IsGenericMethod) continue;
                    var ps = m.GetParameters();
                    if (!ps.All(p => Supported(p.ParameterType)) || !Supported(m.ReturnType)) continue;
                    sb.Append($"fun {m.Name} {MapRet(m.ReturnType, t)} abstract {MetaParams(ps, t)}".TrimEnd() + "\n");
                }
                Console.WriteLine($"meta: {t.FullName} (interface)");
                continue;
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
                continue;
            }
            var isStatic = t.IsAbstract && t.IsSealed;
            // A generic type definition (`Collection`1`) -> simple name `Collection`, OPEN .NET name (namespace +
            // simple, no `1` — the backend appends the arity), and the type parameter names as trailing tokens.
            var simpleName = t.Name.Contains('`') ? t.Name.Substring(0, t.Name.IndexOf('`')) : t.Name;
            var dotNet = t.IsGenericTypeDefinition ? (t.Namespace + "." + simpleName) : t.FullName;
            var tparams = t.IsGenericTypeDefinition ? " " + string.Join(" ", t.GetGenericArguments().Select(g => g.Name)) : "";
            // `class <Name> <DotNetName> <open|sealed> [<TypeParam>...]` carries inheritability + generic arity.
            sb.Append(isStatic ? $"object {simpleName} {dotNet}\n"
                               : $"class {simpleName} {dotNet} {(t.IsSealed ? "sealed" : "open")}{tparams}\n");
            var seen = new HashSet<string>();
            if (!isStatic)
            {
                foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                {
                    var ps = c.GetParameters();
                    if (!ps.All(p => Supported(p.ParameterType))) continue;
                    if (!seen.Add("ctor(" + Sig(ps, t) + ")")) continue;
                    sb.Append($"ctor {MetaParams(ps, t)}".TrimEnd() + "\n");
                }
                // Instance properties (non-indexer). `prop <Name> <KType> <ro|rw> <open|final>`.
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0 || !Supported(p.PropertyType)) continue;
                    if (!p.CanRead || !seen.Add("prop:" + p.Name)) continue;
                    var virt = (p.GetMethod?.IsVirtual ?? false) && !(p.GetMethod?.IsFinal ?? false);
                    sb.Append($"prop {p.Name} {Map(p.PropertyType, t)} {(p.CanWrite ? "rw" : "ro")} {(virt ? "open" : "final")}\n");
                }
                // Events (I4). `event <Name> <handlerRetKType> <handlerParams...>` from the delegate's Invoke.
                foreach (var ev in t.GetEvents(BindingFlags.Public | BindingFlags.Instance))
                {
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
            var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            foreach (var m in t.GetMethods(flags))
            {
                if (m.IsSpecialName) continue;
                if (OBJECT_MEMBERS.Contains(m.Name)) continue;
                if (m.DeclaringType?.FullName == "System.Object") continue;
                // A generic method (`SizeOf<T>()`) is now emitted: its method type params + any T-typed param/return
                // map to the param names (Map returns the generic-parameter Name). Only simple shapes survive Supported.
                if (m.IsGenericMethod && !m.IsGenericMethodDefinition) continue;
                var gp = m.IsGenericMethodDefinition ? m.GetGenericArguments().Select(g => g.Name).ToList() : new List<string>();
                var ps = m.GetParameters();
                if (!ps.All(p => Supported(p.ParameterType)) || !Supported(m.ReturnType)) continue;
                if (!seen.Add(m.Name + "<" + string.Join(",", gp) + ">(" + Sig(ps, t) + ")")) continue;
                // `fun <Name> <ret> <open|final> [<TypeParam>...] [<param>:<type>]*` — bare trailing tokens (no `:`)
                // are method type parameters; tokens with `:` are value params.
                var virt = m.IsVirtual && !m.IsFinal;
                var toks = new List<string> { "fun", m.Name, MapRet(m.ReturnType, t), virt ? "open" : "final" };
                toks.AddRange(gp);
                toks.AddRange(ps.Select((p, i) => $"{MetaParamName(p, i)}:{Map(p.ParameterType, t)}"));
                sb.Append(string.Join(" ", toks) + "\n");
            }
            Console.WriteLine($"meta: {t.FullName} ({(isStatic ? "object" : "class")})");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        File.WriteAllText(outFile, sb.ToString());
        return 0;
    }

    // Walk the base chain by FullName (LoadFrom'd reference assemblies give System.Attribute a different identity
    // than the runtime's typeof, so `typeof(Attribute).IsAssignableFrom` would miss — see Map's I2 note).
    static bool IsAttribute(Type t)
    {
        for (var b = t; b != null; b = b.BaseType) if (b.FullName == "System.Attribute") return true;
        return false;
    }

    // A method's RETURN type: a `ref T` return surfaces as plain `T` (a plain `val x = m()` is a value copy; the live
    // ref is captured only via `byref(m())`). Parameters keep their byref (-> ClrRef<T>) via Map.
    static string MapRet(Type t, Type self) => Map(t.IsByRef ? t.GetElementType() : t, self);

    static string MetaParams(ParameterInfo[] ps, Type self) =>
        string.Join(" ", ps.Select((p, i) => $"{MetaParamName(p, i)}:{Map(p.ParameterType, self)}"));

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
        var clrName = t.IsGenericTypeDefinition ? (t.Namespace + "." + simpleName) : t.FullName;
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
        // arg in `__clrout(x)`/`__clrref(x)` and the backend re-applies the byref via a `byref:` param type.
        t.IsByRef ? (MetaMode && Supported(t.GetElementType()))
        : !t.IsPointer
        && ((MetaMode && t.IsArray) ? Supported(t.GetElementType())
            : (!t.IsArray && (t.IsGenericParameter || !t.ContainsGenericParameters)));

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
        if (t.FullName == self.FullName) return self.Name.Contains('`') ? self.Name.Substring(0, self.Name.IndexOf('`')) : self.Name;
        return t.FullName switch
        {
            "System.Int32" => "Int",
            "System.Int64" => "Long",
            "System.Int16" => "Short",
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
        if (t.IsGenericType || t.IsByRef || t.IsPointer || t.IsGenericParameter || string.IsNullOrEmpty(t.Namespace)) return "Any?";
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
