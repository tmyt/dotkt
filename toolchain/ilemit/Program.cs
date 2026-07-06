// ilemit — emit a runnable .NET assembly directly as CIL from Backend IR (BIR) JSON. No C#, no csc.
//
//   ilemit <output-dir> <assemblyName> <file1.bir.json> [<file2.bir.json> ...]
//
// All BIR files compile into ONE assembly (so multi-file Kotlin cross-references resolve).
// D1.2 = M0 subset; D1.4 = user classes (fields, ctors, methods, inheritance, virtual/override).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

static class IlEmit
{
    static int Main(string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: ilemit <out-dir> <asmName> [--ref <dll>]... <file.bir.json>..."); return 1; }
        var outDir = args[0];
        var asmName = args[1];
        Directory.CreateDirectory(outDir);
        // `--ref <dll>`: preload an external .NET assembly (e.g. a coroutine runtime, a framework like Avalonia)
        // so its types resolve at emit time; the runtime dll must sit beside the emitted assembly to run.
        var bir = new List<string>();
        var rest = args.Skip(2).ToList();
        for (int i = 0; i < rest.Count; i++)
        {
            if (rest[i] == "--ref" && i + 1 < rest.Count) { var rp = Path.GetFullPath(rest[++i]); Emitter.T($"ref: {rp}"); try { Assembly.LoadFrom(rp); } catch { } }
            else bir.Add(rest[i]);
        }
        var files = bir.Select(LoadInputDocument).ToList();
        new Emitter(outDir, asmName).EmitAssembly(MergeByFileClass(files));
        return 0;
    }

    // Multiple CIR files can target the SAME file class — a Kotlin multiplatform `expect`/`actual` split (the common
    // `_Comparisons.kt` and the platform `_ComparisonsClr.kt` both compile to `kotlin.comparisons._ComparisonsKt`).
    // Emitting them as separate files made the 2nd `DefineType` collide, silently dropping one file's methods (the
    // platform's inline primitive overloads `maxOf(int,int)` etc.). Merge same-file-class inputs into ONE so every
    // overload lands in the single emitted type. (Inline funcs are normal callable methods on CLR; metadata is stripped.)
    static readonly List<JsonDocument> _mergedDocs = new();
    static List<JsonElement> MergeByFileClass(List<JsonDocument> docs)
    {
        var byFc = new Dictionary<string, System.Text.Json.Nodes.JsonObject>();
        var order = new List<string>();
        foreach (var d in docs)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(d.RootElement.GetRawText()).AsObject();
            var fc = node["fileClass"]?.GetValue<string>() ?? "";
            if (fc.Length > 0 && byFc.TryGetValue(fc, out var acc))
            {
                foreach (var key in new[] { "methods", "fields", "types" })
                    if (node[key] is System.Text.Json.Nodes.JsonArray src && src.Count > 0)
                    {
                        if (acc[key] is System.Text.Json.Nodes.JsonArray dst)
                            foreach (var it in src.ToList()) dst.Add(it.DeepClone());
                        else acc[key] = src.DeepClone();
                    }
                if (node["hasMain"]?.GetValue<bool>() == true) acc["hasMain"] = true;
            }
            else { byFc[fc] = node; order.Add(fc); }
        }
        _mergedDocs.Clear();
        var result = new List<JsonElement>();
        foreach (var fc in order)
        {
            var doc = JsonDocument.Parse(byFc[fc].ToJsonString());
            _mergedDocs.Add(doc);
            result.Add(doc.RootElement);
        }
        return result;
    }

    static JsonDocument LoadInputDocument(string path)
    {
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("cirVersion", out _))
            return JsonDocument.Parse(json);

        if (root.TryGetProperty("cirDraft", out var draft) &&
            draft.TryGetProperty("executableCir", out var executable))
            return JsonDocument.Parse(executable.GetRawText());

        throw new InvalidOperationException(
            $"ilemit: native CIR input '{path}' does not contain cirDraft.executableCir");
    }
}


sealed partial class Emitter
{
    readonly string _outDir;
    readonly string _asmName;
    // Strip ALL roundtrip metadata ([Kotlin*]/[KotlinInline]/NRT + the attr class defs) — the [KotlinInline] BIR payloads
    // are ~73.8% of the size. ORTHOGONAL to substitution (DOTKT_STDLIB_SUBSTITUTE): ONLY the stdlib RUNTIME sets this;
    // a USER LIBRARY is substituted but KEEPS its attributes (round-trip consumable AS KOTLIN). (Per user.)
    readonly bool _stripMetadata = Environment.GetEnvironmentVariable("DOTKT_STRIP_METADATA") != null;
    readonly Dictionary<string, TypeInfo> _types = new();
    readonly Dictionary<string, TypeBuilder> _syntheticDelegates = new();
    readonly Dictionary<TypeBuilder, ConstructorBuilder> _syntheticDelegateCtors = new();
    readonly Dictionary<TypeBuilder, MethodBuilder> _syntheticDelegateInvokes = new();
    ModuleBuilder _mod;

    // Crash localizer: Reflection.Emit can hard-CRASH the process (access violation, 0xC0000005) — uncatchable — on a
    // pathological reference (e.g. a WinRT/COM projection type) rather than throwing. With ILEMIT_TRACE set, each pass
    // step prints (flushed) to stderr, so the LAST line before the crash names the culprit type/method.
    static readonly bool Trace = Environment.GetEnvironmentVariable("ILEMIT_TRACE") != null;
    internal static void T(string m) { if (Trace) { Console.Error.WriteLine("[ilemit] " + m); Console.Error.Flush(); } }

    // per-method context
    ILGenerator _il;
    readonly Dictionary<string, int> _args = new();
    readonly Dictionary<string, Type> _argTypes = new();
    readonly Dictionary<string, LocalBuilder> _locals = new();
    // Cross-module inline splice substitution: a callee-body `local` referencing one of these names emits the bound
    // value instead; a `delegateInvoke` on a lambda-param name splices the caller's lambda body (binding its param).
    readonly Dictionary<string, JsonElement> _inlineSubst = new();
    readonly Dictionary<string, (string lamParam, JsonElement body)> _inlineLambdas = new();
    readonly List<JsonDocument> _inlineDocs = new();   // keep parsed [KotlinInline] bodies alive
    readonly Stack<LocalBuilder> _inlineThis = new();   // bound `this` (extension receiver) for the current inline splice
    readonly Dictionary<MethodInfo, Type[]> _mparams = new();   // declared param types per method (for call-site boxing)
    // active try blocks: a `return` inside stores to the result local and leaves to the end label.
    readonly Stack<(LocalBuilder result, Label end)> _tryStack = new();
    // active loops: break/continue target the innermost (or the one matching the Kotlin label).
    readonly List<(string label, Label cont, Label brk)> _loops = new();
    // CFG block-IR labels: `label`/`goto`/`brIf` (E-0.5). Forward references need every label defined up-front,
    // so EmitMethodBody/EmitCtorBody prescans the whole body. id -> IL Label. See docs/design-il-cfg.md.
    Dictionary<int, Label> _cfgLabels;
    Type _methodRetType = typeof(void);
    // The generic context for emitting a type's members = the type's OWN params PLUS every enclosing (`nestedIn`) type's
    // params — a .NET nested type references its outer generic type's parameters by the outer's builder (a Kotlin `inner
    // class IteratorImpl` inside `AbstractList<E>` whose `next(): E` must resolve `gp:E` to AbstractList's `E`).
    Dictionary<string, GenericTypeParameterBuilder> EffectiveTps(TypeInfo ti)
    {
        var chain = new List<TypeInfo>();
        for (var cur = ti; cur != null;
             cur = (cur.Def.TryGetProperty("nestedIn", out var ni) && _types.TryGetValue(ni.GetString(), out var p)) ? p : null)
            chain.Add(cur);
        if (chain.Count == 1) return ti.TypeParams;   // not nested -> the common case, no merge
        var merged = new Dictionary<string, GenericTypeParameterBuilder>();
        chain.Reverse();   // outermost first; an inner param of the same name shadows
        foreach (var c in chain) foreach (var kv in c.TypeParams) merged[kv.Key] = kv.Value;
        return merged;
    }
    // Generic context for resolving `gp:T` type references: method params shadow the enclosing type's.
    Dictionary<string, GenericTypeParameterBuilder> _curTypeParams;
    Dictionary<string, GenericTypeParameterBuilder> _curMethodParams;

    public Emitter(string outDir, string asmName) { _outDir = outDir; _asmName = asmName; }

    public void EmitAssembly(List<JsonElement> files)
    {
        // NOTE (R-1, reverse-interop): the emitted assembly's core type refs point at System.Private.CoreLib (the
        // impl assembly) because BCL types resolve via runtime reflection (typeof/Type.GetType, ~176 sites). A
        // standalone exe runs fine and any .NET host can reflection-load it (samples/il-revinterop), but a C# project
        // that <Reference>s it at COMPILE time hits CS0012 (Object lives in the unreferenced System.Private.CoreLib).
        // Investigated 2026-06-21: adding a consumer <Reference> to System.Private.CoreLib does NOT work either
        // (CS0433 — attributes exist in both it and System.Runtime). The proper fix is to resolve ALL BCL types
        // through a MetadataLoadContext over the REFERENCE assemblies and pass that core to PersistedAssemblyBuilder,
        // so refs become System.Runtime — a large, contained refactor (every typeof(Bcl) -> mlc lookup). Tracked #50.
        var ab = new PersistedAssemblyBuilder(new AssemblyName(_asmName), typeof(object).Assembly);
        _mod = ab.DefineDynamicModule(_asmName);
        // Embed the DotKt.Runtime.CompilerServices.* metadata attribute types into this module up front (so they exist
        // before any type/member that stamps one). No --ref DotKt.Runtime needed to resolve them. Skipped in the runtime
        // (substitute) build — no [Kotlin*] is stamped there, so the attr CLASS defs would just be dead weight.
        if (!_stripMetadata) EnsureKotlinAttrs();

        // Pass 1: DefineType for every file-static-class and every user class.
        foreach (var file in files)
        {
            var fileClass = file.GetProperty("fileClass").GetString();
            // Create the file class if it has methods OR top-level static fields — a file that only declares
            // top-level `val`/`var`s (no functions) still needs its class so OTHER files can reference those
            // fields (`StateKt.counter`); otherwise cross-file top-level property access fails (item 11).
            if (file.GetProperty("methods").GetArrayLength() > 0 ||
                (file.TryGetProperty("fields", out var ffl) && ffl.GetArrayLength() > 0))
                _types[fileClass] = new TypeInfo
                {
                    TB = _mod.DefineType(fileClass, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract),
                    Def = file, IsFileClass = true, FileElem = file,
                };
            if (file.TryGetProperty("types", out var ts))
                foreach (var t in ts.EnumerateArray())
                {
                    var name = t.GetProperty("name").GetString();
                    var kind = t.GetProperty("kind").GetString();
                    // Shared synthetic types (`<>dotkt_Result`/`KProperty`/`KIterator_*`/`CharSequence`/…) are emitted
                    // identically by EVERY file that uses them; in a multi-file assembly they'd redefine the same name
                    // and collide in `_types` (orphaning a TypeBuilder -> Save crash). They're structurally identical,
                    // so the first definition serves all references — skip the duplicates. (Per-file-DISTINCT synthetics
                    // — closures, ref cells, seq SMs — are now uniquely named by BirEmitter, so they never land here.)
                    if (name.StartsWith("<>dotkt_") && _types.ContainsKey(name)) continue;
                    // Canonicalization: a shared synthetic ALREADY defined (public) by a REFERENCED assembly (the rt
                    // stdlib dll) is REFERENCED, not re-emitted here — else the app's copy is a DISTINCT CLR type from
                    // the rt dll's, so a value crossing the app<->rt boundary (a stdlib CharSequence-extension receiving
                    // an app value) fails interface dispatch (EntryPointNotFound). Skipping the local definition routes
                    // every `@<>dotkt_X` reference through MapType/FindMethod/AddInterfaceImplementation -> ResolveType,
                    // which resolves it as the external canonical type in the --ref'd assembly. Scoped to the
                    // verified-safe set (CharSequence); the other shared synthetics (Result/KProperty/KIterator/
                    // RWProperty_*) still re-emit per-assembly until each is verified cross-assembly. Self-correcting:
                    // only skips when the type ACTUALLY resolves externally, so a --no-stdlib build (or the stdlib's own
                    // ref/rt build, which passes ilemit no --ref) still emits the canonical copy locally.
                    if (CanonicalSynthetics.Contains(name) && ResolvesExternally(name)) continue;
                    if (kind == "enum")
                    {
                        // A real .NET enum: each entry is a literal field of the int-backed enum.
                        var eb = _mod.DefineEnum(name, TypeAttributes.Public, typeof(int));
                        var eti = new TypeInfo { EB = eb, Def = t, IsEnum = true };
                        foreach (var en in t.GetProperty("entries").EnumerateArray())
                            eti.Fields[en.GetProperty("name").GetString()] =
                                (FieldBuilder)eb.DefineLiteral(en.GetProperty("name").GetString(), en.GetProperty("ordinal").GetInt32());
                        _types[name] = eti;
                        continue;
                    }
                    var isIface = kind == "interface";
                    var visStr = t.TryGetProperty("vis", out var tv) ? tv.GetString() : "public";
                    // A nested type (`nestedIn`) is defined on the enclosing type's builder with Nested* access, so it
                    // keeps CLR access to the enclosing type's private members; otherwise a top-level Public/NotPublic.
                    var nested = t.TryGetProperty("nestedIn", out var niEl) && _types.TryGetValue(niEl.GetString(), out var parentTi);
                    var typeAccess = nested
                        ? visStr switch { "internal" => TypeAttributes.NestedAssembly, "protected" => TypeAttributes.NestedFamily, "private" => TypeAttributes.NestedPrivate, _ => TypeAttributes.NestedPublic }
                        : (visStr == "public" ? TypeAttributes.Public : TypeAttributes.NotPublic);
                    var attrs = isIface
                        ? typeAccess | TypeAttributes.Interface | TypeAttributes.Abstract
                        : typeAccess | TypeAttributes.Class;
                    // An `abstract`/`sealed`(Kotlin) class -> a CLR abstract class (cannot be instantiated; may hold
                    // abstract members). Kotlin `sealed` is also abstract at the CLR level.
                    if (!isIface && t.TryGetProperty("abstract", out var clsAbs) && clsAbs.GetBoolean()) attrs |= TypeAttributes.Abstract;
                    // A generic type's CLR metadata name carries its arity (`Box`1`) — Reflection.Emit does NOT append
                    // it, and a cross-assembly consumer resolves the type by that standard name (`GetType("Box`1")`).
                    // The `_types` registry key stays the bare BIR name (`Box`), so same-assembly references are intact.
                    var arity = t.TryGetProperty("typeParams", out var tpArity) ? tpArity.GetArrayLength() : 0;
                    var simpleName = nested && name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
                    var metaName = arity > 0 ? simpleName + "`" + arity : simpleName;
                    var tb = nested ? _types[niEl.GetString()].TB.DefineNestedType(metaName, attrs) : _mod.DefineType(metaName, attrs);
                    // Compiler-generated synthetic types (`<>dotkt_*`: KProperty, Result, KIterator_*, …) get
                    // [CompilerGenerated] (and can't collide with user types — the `<>` prefix isn't source-legal).
                    if (name.StartsWith("<>dotkt_"))
                        tb.SetCustomAttribute(new CustomAttributeBuilder(
                            typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute).GetConstructor(Type.EmptyTypes), new object[0]));
                    var nti = new TypeInfo
                    {
                        TB = tb,
                        Def = t,
                        IsInterface = isIface,
                        BaseFqn = t.TryGetProperty("base", out var b) && b.ValueKind == JsonValueKind.Object
                            && DotKt.Bir.TypeNode.Read(b) is DotKt.Bir.TypeNode.Fqn bf ? bf : null,
                        BaseName = t.TryGetProperty("base", out var b2)
                            ? (b2.ValueKind == JsonValueKind.String ? b2.GetString() : SlotName(b2)) : null,
                    };
                    // Generic type `class Box<T>`: define its type parameters now so member signatures (pass 3) resolve.
                    // (Constraints are applied in pass 2, once every type — possibly referenced by a bound — exists.)
                    if (t.TryGetProperty("typeParams", out var tps) && tps.GetArrayLength() > 0)
                    {
                        var names = TpNames(tps);
                        var gps = tb.DefineGenericParameters(names);
                        for (int gi = 0; gi < names.Length; gi++) nti.TypeParams[names[gi]] = gps[gi];
                    }
                    _types[name] = nti;
                }
        }

        // Bake enums up front: their literals are fully defined in pass 1, and baking now gives a real metadata
        // token usable in other types' IL (box/castclass/ldtoken) — an un-baked EnumBuilder token breaks the PE.
        foreach (var ti in _types.Values)
            if (ti.IsEnum) ti.Created = ti.EB.CreateType();

        // Pass 2: set parents and interface implementations (DefineGenericParameters already ran in pass 1, so a
        // generic base/interface that references the type's own params resolves).
        foreach (var ti in _types.Values)
        {
            T($"pass2 parent/iface: {ti.TB?.Name}");
            _curTypeParams = EffectiveTps(ti);
            // Bounds may reference any type (now all defined) and the type's own params (now in _curTypeParams).
            if (ti.IsGeneric && ti.Def.TryGetProperty("typeParams", out var tps2)) ApplyConstraints(tps2, ti.TypeParams, ti.IsInterface, ti.Def);
            if (ti.BaseName != null)
            {
                // A `.NET` base (`clr:System.Exception` / `clrg:...[..]`) is resolved by reflection; a Kotlin-user
                // base is another TypeBuilder in `_types`.
                if (ti.BaseName.StartsWith("clr:") || ti.BaseName.StartsWith("clrg:")) ti.TB.SetParent(ti.ClrBase = MapType(ti.BaseName));
                else
                {
                    // A constructed user base (`...IteratorImpl[gp:E]` — an inner extending an inner) is INSTANTIATED via
                    // ParseOwner. A user base emitted OPEN (`AbstractCollection` for `AbstractList<E> : AbstractCollection
                    // <E>`) must NOT stay an un-instantiated open generic ("could not load" at type-load) — instantiate it
                    // with this type's leading generic params POSITIONALLY (the BIR keeps the open name so FindMethod still
                    // walks the base chain by bare name for inherited members like AbstractIterator.setNext).
                    var (bopen, bconstructed) = ti.BaseFqn != null ? ParseOwnerT(ti.BaseFqn) : ParseOwner(ti.BaseName);
                    if (bconstructed != null) { ti.TB.SetParent(bconstructed); }
                    else if (_types.TryGetValue(bopen, out var baseTi))
                    {
                        var baseTb = baseTi.TB;
                        var bArity = baseTb.IsGenericTypeDefinition ? baseTb.GetGenericArguments().Length : 0;
                        var myArgs = ti.TB.IsGenericTypeDefinition ? ti.TB.GetGenericArguments() : Type.EmptyTypes;
                        ti.TB.SetParent(bArity > 0 && myArgs.Length >= bArity ? baseTb.MakeGenericType(myArgs.Take(bArity).ToArray()) : (Type)baseTb);
                    }
                    // A bare external .NET base (kotc's pure-FQN output for a non-`clr:`-marked .NET supertype): not in
                    // `_types`, so resolve it by reflection over referenced assemblies.
                    else ti.TB.SetParent(ResolveType(bopen));
                }
            }
            if (!ti.IsFileClass && ti.Def.TryGetProperty("interfaces", out var ifs))
                foreach (var i in ifs.EnumerateArray())
                {
                    if (DotKt.Bir.TypeNode.Read(i) is not DotKt.Bir.TypeNode.Fqn iFqn) continue;
                    // A REFERENCED interface (not in `_types` — a .NET Continuation<int>) is resolved by reflection; an
                    // emitted Kotlin interface (`Container<int>`) comes from `_types` (constructed via ParseOwnerT).
                    Type itype;
                    if (!_types.ContainsKey(iFqn.Name)) itype = MapType(iFqn);
                    else { var (open, constructed) = ParseOwnerT(iFqn); itype = constructed ?? (Type)_types[open].TB; }
                    ti.TB.AddInterfaceImplementation(itype);
                }
        }
        _curTypeParams = null;

        // Pass 3: declare fields, ctors, methods (signatures) so cross-refs resolve.
        foreach (var ti in _types.Values)
        {
            if (ti.IsEnum) continue;   // enums are fully defined (literals) in pass 1
            T($"pass3 signatures: {ti.TB?.Name}");
            _curTypeParams = EffectiveTps(ti);   // so `gp:T` in field/ctor/method signatures resolves
            if (ti.IsFileClass)
            {
                // Top-level `val`/`var` -> static fields of the file class.
                if (ti.Def.TryGetProperty("fields", out var ffs))
                    foreach (var f in ffs.EnumerateArray())
                    {
                        var tlType = MapType(f.GetProperty("type"));
                        var tlAttrs = FieldAttributes.Public | FieldAttributes.Static;
                        // `@kotlin.concurrent.Volatile` on a top-level `var` -> a `modreq(IsVolatile)` static field.
                        var tlFb = f.TryGetProperty("volatile", out var tlVol) && tlVol.GetBoolean()
                                ? DefineVolatileField(ti.TB, f.GetProperty("name").GetString(), tlType, tlAttrs)
                                : ti.TB.DefineField(f.GetProperty("name").GetString(), tlType, tlAttrs);
                        // H2: a `suspend (…) -> T`-typed top-level property's backing field carries the pre-erasure shape.
                        if (f.TryGetProperty("suspendFnType", out var tlSf)) ApplySuspendFnType(tlFb, tlSf.GetRawText());
                        ti.Fields[f.GetProperty("name").GetString()] = tlFb;
                    }
                foreach (var m in ti.Def.GetProperty("methods").EnumerateArray()) DeclareMethod(ti, m, isStatic: true);
            }
            else
            {
                if (!ti.IsInterface)
                    foreach (var f in ti.Def.GetProperty("fields").EnumerateArray())
                    {
                        // A property's visibility maps to the field's CLR access. True CLR-private is now correct
                        // because `inner`/`nested` classes are emitted as real nested types, which retain access to the
                        // enclosing type's privates. (internal -> Assembly, protected -> FamORAssem so same-assembly
                        // nested/local types and subclasses both reach it.)
                        var fattrs = (f.TryGetProperty("vis", out var fv) ? fv.GetString() : "public") switch
                        {
                            "private" => FieldAttributes.Private,
                            "internal" => FieldAttributes.Assembly,
                            "protected" => FieldAttributes.FamORAssem,
                            _ => FieldAttributes.Public,
                        };
                        if (f.TryGetProperty("static", out var st) && st.GetBoolean()) fattrs |= FieldAttributes.Static;
                        var ftype = MapType(f.GetProperty("type"));
                        // `@kotlin.concurrent.Volatile` -> a `modreq(IsVolatile)` field (the C# `volatile` encoding).
                        var fb = f.TryGetProperty("volatile", out var vol) && vol.GetBoolean()
                            ? DefineVolatileField(ti.TB, f.GetProperty("name").GetString(), ftype, fattrs)
                            : ti.TB.DefineField(f.GetProperty("name").GetString(), ftype, fattrs);
                        // A not-publicly-settable property's backing field -> [KotlinReadOnly] (consumer restores it as `val`).
                        if (f.TryGetProperty("readOnly", out var ro) && ro.GetBoolean()) ApplyKotlinReadOnly(fb);
                        // H2: a `suspend (…) -> T`-typed field/property backing field carries the pre-erasure shape.
                        if (f.TryGetProperty("suspendFnType", out var fSf)) ApplySuspendFnType(fb, fSf.GetRawText());
                        ti.Fields[f.GetProperty("name").GetString()] = fb;
                    }
                foreach (var m in ti.Def.GetProperty("methods").EnumerateArray()) DeclareMethod(ti, m, isStatic: false);
                // Real CLR properties: DefineProperty over the already-declared get_/set_ accessor methods, so a Kotlin
                // property is seen as a PROPERTY (not a bare field/methods) by C#/F#/reflection. Additive — only fires
                // when kotc emits the `properties` metadata. See docs/design-clr-property-model.md.
                if (ti.Def.TryGetProperty("properties", out var props))
                    foreach (var p in props.EnumerateArray())
                    {
                        var pb = ti.TB.DefineProperty(p.GetProperty("name").GetString(), PropertyAttributes.None, MapType(p.GetProperty("type")), null);
                        if (p.TryGetProperty("get", out var g) && g.ValueKind == JsonValueKind.String && ti.Methods.TryGetValue(g.GetString(), out var gm)) pb.SetGetMethod(gm);
                        if (p.TryGetProperty("set", out var s) && s.ValueKind == JsonValueKind.String && ti.Methods.TryGetValue(s.GetString(), out var sm)) pb.SetSetMethod(sm);
                        // H2: a `val/var x: suspend (…) -> T` property carries the pre-erasure `sfunc:` shape (its CLR type is object).
                        if (p.TryGetProperty("suspendFnType", out var pSf)) ApplySuspendFnType(pb, pSf.GetRawText());
                    }
                EnsureCtorsDefined(ti);
            }
        }

        // Link interface implementations: every class method that satisfies an interface method. For a constructed
        // generic interface `Container[int]`, the override target is the method on the instantiation (static helper).
        // Iterate with the registry KEY (the BIR/full name, e.g. `p.Impl` for a packaged type, `Box` for a generic):
        // FindMethod looks the type up in `_types` by that key, NOT by `ti.TB.Name` (the *simple* name, which only
        // coincides with the key for a non-generic root-package type — so namespaced/generic types broke with KeyNotFound).
        // C3b reverse bridge: now that the Kotlin Iterator interface's hasNext/next exist, emit the IEnumerator adapter
        // (once) so qualifying classes' generated GetEnumerator can reference it. Emitter.ReverseBridge.cs.
        EmitEnumeratorAdapter();
        foreach (var (typeKey, ti) in _types)
            if (!ti.IsFileClass && !ti.IsInterface && ti.Def.TryGetProperty("interfaces", out var ifs))
            {
                _curTypeParams = EffectiveTps(ti);
                // Worklist over the class's interfaces INCLUDING transitively-inherited ones (a Kotlin interface method
                // can be inherited through a chain, e.g. MonotonicTimeSource : WithComparableMarks : TimeSource — the
                // covariant markNow over TimeSource.markNow must be bridged too, or the slot stays unimplemented).
                // The interface entries are STRUCTURED Fqn nodes (birType-emitted). ilemit DERIVES the "referenced-vs-
                // emitted" decision from the name (`_types` membership), not a clr:/clrg: marker.
                var ifWork = new Queue<DotKt.Bir.TypeNode.Fqn>();
                var ifSeen = new HashSet<string>();
                foreach (var i in ifs.EnumerateArray())
                    if (DotKt.Bir.TypeNode.Read(i) is DotKt.Bir.TypeNode.Fqn iff) ifWork.Enqueue(iff);
                while (ifWork.Count > 0)
                {
                    var specFqn = ifWork.Dequeue();
                    var spec = SigTokenOf(specFqn);          // the canonical key + a legacy-ish token for the string helpers below
                    var specName = specFqn.Name;
                    if (!ifSeen.Add(spec)) continue;
                    // The reverse GetEnumerator bridge fires below on a `clr:`/`clrg:` collection interface (the form
                    // bir2cir lowers Kotlin Set/MutableCollection/List/... to in every runnable build). ilemit holds NO
                    // Kotlin-collection-name knowledge — the Kotlin↔CLR identity was consumed upstream.
                    // A canonicalized shared synthetic (`<>dotkt_CharSequence`) this app REFERENCES from the rt stdlib
                    // dll — NOT re-emitted here, so absent from `_types` — is an EXTERNAL interface: bind the class's
                    // overrides to it by reflection, exactly like a `clr:` interface, so the interface slots are wired
                    // explicitly rather than relying on an implicit name/sig match a canonicalized supertype must not
                    // depend on. (Covers both a user `class S : CharSequence` and the injected `<>dotkt_StringCharSequence`.)
                    // Checked on the RAW spec (a canonical synthetic interface spec is the bare name), so a `clr:`/`clrg:`
                    // spec is NOT ParseOwner'd here — doing so eagerly mis-strips a `clrg:` self-ref interface (crash).
                    bool externalSynthIface = CanonicalSynthetics.Contains(specName)
                        && !_types.ContainsKey(specName) && ResolvesExternally(specName);
                    // A REFERENCED interface (not emitted in THIS assembly — a .NET-mapped Continuation<int>, or an
                    // external canonical synthetic): bind each interface method to the class method of the same .NET name
                    // by reflection. An EMITTED interface (in `_types`) falls to the ParseOwner path below.
                    if (!_types.ContainsKey(specName) || externalSynthIface)
                    {
                        var itype = externalSynthIface ? ResolveType(specName) : MapType(specFqn);
                        // C3b reverse bridge: if this is a @Clr collection interface (IEnumerable<E>-derived) and the
                        // class has only a Kotlin iterator(), synthesize GetEnumerator (handles the two overloads itself).
                        GenerateGetEnumeratorIfNeeded(ti, itype);
                        var have = ti.Methods.Keys.ToHashSet();
                        // A SELF-REFERENTIAL constructed generic interface (e.g. `V : IComparable<V>`, V the emitted
                        // type) is a TypeBuilderInstantiation whose .GetMethods() throws. Enumerate the OPEN
                        // definition's methods and re-anchor each to the instantiation via TypeBuilder.GetMethod
                        // (same pattern as the self-ref base-ctor below).
                        // A constructed generic interface whose OPEN def is a TypeBuilder (a self-ref `V : IComparable<V>`,
                        // OR a generic STDLIB interface instantiated even with a concrete arg) is a TypeBuilderInstantiation
                        // whose .GetMethods() throws. Try GetMethods; on failure, enumerate the OPEN definition's methods
                        // and re-anchor each to the instantiation via TypeBuilder.GetMethod.
                        MethodInfo[] ifaceMs; bool reanchor;
                        try { ifaceMs = itype.GetMethods(); reanchor = false; }
                        catch (NotSupportedException) { ifaceMs = itype.GetGenericTypeDefinition().GetMethods(); reanchor = true; }
                        foreach (var im in ifaceMs)
                        {
                            if (im.Name == "GetEnumerator" || !have.Contains(im.Name)) continue;   // GetEnumerator: handled by the reverse bridge above
                            // OVERLOADED body methods (e.g. the generic CompareTo(V) + the non-generic IComparable bridge
                            // CompareTo(object)) collide in the name-keyed ti.Methods — wiring the wrong one to the slot
                            // is a TypeLoad "signature ... do not match". Disambiguate by the interface method's
                            // (instantiation-substituted) parameter types against each overload's recorded params.
                            var body = ti.Methods[im.Name];
                            var cands = ti.MethodsBySig.Values.Where(b => b.Name == im.Name).Distinct().ToList();
                            if (cands.Count > 1)
                            {
                                var ips = im.GetParameters().Select(p => reanchor
                                    ? SubstituteIfaceArgs(p.ParameterType, itype.GetGenericArguments())
                                    : p.ParameterType).ToArray();
                                var match = cands.FirstOrDefault(b => _mparams.TryGetValue(b, out var bps)
                                    && bps.Length == ips.Length
                                    && bps.Zip(ips, SlotParamMatches).All(x => x));
                                if (match == null) continue;   // no exact overload -> skip rather than mis-wire
                                body = match;
                            }
                            ti.TB.DefineMethodOverride(body, reanchor ? TypeBuilder.GetMethod(itype, im) : im);
                        }
                        continue;
                    }
                    var (open, constructed) = ParseOwnerT(specFqn);
                    if (!_types.TryGetValue(open, out var iface)) continue;
                    // The interface's instantiation args (the concrete args at this implementer): `Comparable<Self>` ->
                    // [Self]. An interface method's declared type names the INTERFACE's OWN params as `Tv{type,i}`;
                    // SubstTv re-anchors each to specArgs[i] so it matches the class's own (concrete) member signature.
                    var specArgs = specFqn.Args;
                    // Transitively process this interface's base interfaces too, substituting the type args through the
                    // chain (e.g. WithComparableMarks : TimeSource, or List<object> : Collection<object>).
                    if (iface.Def.ValueKind == JsonValueKind.Object && iface.Def.TryGetProperty("interfaces", out var baseIfs))
                        foreach (var bi in baseIfs.EnumerateArray())
                            if (SubstTv(DotKt.Bir.TypeNode.Read(bi), specArgs) is DotKt.Bir.TypeNode.Fqn biF) ifWork.Enqueue(biF);
                    // Iterate the interface's method DEFS (not the name-keyed iface.Methods) so OVERLOADED interface
                    // methods (e.g. MutableMap.remove(K):V vs the JVM remove(K,V):Boolean) each resolve to their own
                    // builder by signature, and to the matching body overload by TYPE-ARG-SUBSTITUTED signature. A miss
                    // when the name is AMBIGUOUS (multiple body overloads) is skipped — wiring the wrong one is the bug.
                    if (iface.Def.ValueKind == JsonValueKind.Object && iface.Def.TryGetProperty("methods", out var ifMs))
                        foreach (var imDef in ifMs.EnumerateArray())
                        {
                            if (!imDef.TryGetProperty("name", out var imn) || !imDef.TryGetProperty("params", out _)) continue;
                            var imName = imn.GetString();
                            var ifaceBuilder = iface.MethodsBySig.TryGetValue(SigKey(imName, imDef), out var ib) ? ib
                                             : (iface.Methods.TryGetValue(imName, out var ib2) ? ib2 : null);
                            if (ifaceBuilder == null) continue;
                            // The interface method's params with each Tv{type,i} re-anchored to specArgs[i], rendered to
                            // the sig-token spelling — matched against the class's own MethodsBySig (a nested value-class
                            // arg like Continuation.resumeWith(Result<T>) substitutes correctly, not just a bare gp).
                            var subSig = imName + "(" + string.Join(",", imDef.GetProperty("params").EnumerateArray()
                                .Select(p => SigTokenOf(SubstTv(DotKt.Bir.TypeNode.Read(p.GetProperty("type")), specArgs)))) + ")";
                            // Only wire an EXACT signature match. A miss means the class doesn't override this exact
                            // overload (e.g. it lacks the JVM remove(K,V):Boolean default) -> SKIP rather than mis-wire a
                            // different overload; for a Kotlin interface the same-name+sig method resolves implicitly anyway.
                            if (!ti.MethodsBySig.TryGetValue(subSig, out var bodyMethod))
                            {
                                // ...unless a DIRECT base interface provides this method as a DEFAULT (e.g. ValueTimeMark :
                                // ComparableTimeMark, which has compareTo(ComparableTimeMark) as a DIM): the CLR does NOT
                                // treat an interface DIM as implicitly implementing the base interface method (Comparable.
                                // compareTo), so the class slot stays unimplemented. Emit a class-level forwarding bridge
                                // that calls the inherited DIM and put the MethodImpl on it.
                                if (!ti.IsInterface) TryEmitDimForwardBridge(ti, imDef, specArgs, subSig, constructed, ifaceBuilder);
                                continue;
                            }
                            var ifaceMethod = constructed != null ? TypeBuilder.GetMethod(constructed, ifaceBuilder) : (MethodInfo)ifaceBuilder;
                            // Covariant return: a NARROWED override return type (markNow():ValueTimeMark over the iface's
                            // :ComparableTimeMark) makes a direct MethodImpl fail the CLR's exact-return rule. Emit a bridge
                            // with the iface's (base) return type that calls the narrow body method and upcasts; put the
                            // MethodImpl on the bridge. The iface ret comes from imDef (BIR), Tv re-anchored to specArgs.
                            Type ifaceRet = null;
                            try { if (imDef.TryGetProperty("ret", out var rt)) ifaceRet = MapType(SubstTv(DotKt.Bir.TypeNode.Read(rt), specArgs)); } catch { }
                            // Bridge only on a genuine type NARROWING (different type name) — not two reference-different
                            // instantiations of the SAME generic (Iterator<Object> vs Iterator<Object>), which match fine.
                            if (ifaceRet != null && bodyMethod.ReturnType != ifaceRet &&
                                ((bodyMethod.ReturnType.Name != ifaceRet.Name && !bodyMethod.ReturnType.IsValueType && !ifaceRet.IsValueType)   // covariant reference narrowing
                                 || (ifaceRet == typeof(void) && bodyMethod.ReturnType != typeof(void))))   // a BCL slot that DROPS the Kotlin return (MutableCollection.add():Boolean -> ICollection.Add():void, set/removeAt:E -> void)
                                EmitCovariantBridge(ti, imName, imDef, specArgs, bodyMethod, ifaceMethod, ifaceRet);
                            else
                                ti.TB.DefineMethodOverride(bodyMethod, ifaceMethod);
                        }
                }
            }

        // An INTERFACE with an EXTERNAL (clr:/clrg:) base interface — e.g. ComparableTimeMark : IComparable<CTM>
        // (via the Comparable alias) — must wire its own DEFAULT (bodied) method to the external base slot with an
        // explicit MethodImpl: unlike a class, an interface method does NOT implicitly implement a same-name+sig
        // base-interface method, so without the .override the DIM is an unrelated NewSlot and every implementing
        // class fails to LOAD ("Method 'CompareTo' in type 'ValueTimeMark' ... does not have an implementation").
        // The loader requires a MethodImpl body on an INTERFACE to be a FINAL method ("must be a final method"),
        // so the public (overridable) DIM can't carry the .override itself — emit C#'s explicit-impl shape: a
        // private final bridge that callvirts the DIM (keeping virtual dispatch for class overrides) and hangs
        // the MethodImpl on the bridge. Classes providing their own override still win ("most specific"), so
        // this only FILLS previously-unimplemented slots.
        foreach (var (_, ti) in _types)
        {
            if (!ti.IsInterface || ti.Def.ValueKind != JsonValueKind.Object || !ti.Def.TryGetProperty("interfaces", out var extIbs)) continue;
            _curTypeParams = EffectiveTps(ti);
            // Only a BODIED method (a DIM) can implement an external slot; an abstract redeclaration stays for the class.
            var bodied = new HashSet<string>();
            foreach (var m in ti.Def.GetProperty("methods").EnumerateArray())
                if (m.TryGetProperty("name", out var bn) && m.TryGetProperty("body", out var bb)
                    && bb.ValueKind == JsonValueKind.Array && bb.GetArrayLength() > 0)
                    bodied.Add(bn.GetString());
            if (bodied.Count == 0) continue;
            foreach (var ib in extIbs.EnumerateArray())
            {
                var spec = ib.GetString();
                if (!spec.StartsWith("clr:") && !spec.StartsWith("clrg:")) continue;
                var itype = MapType(spec);
                // A generic instantiation over an EMITTED TypeBuilder arg can't GetMethods() — enumerate the OPEN
                // definition and re-anchor each slot onto the instantiation (same pattern as the class wiring).
                MethodInfo[] ifaceMs; bool reanchor;
                try { ifaceMs = itype.GetMethods(); reanchor = false; }
                catch (NotSupportedException) { ifaceMs = itype.GetGenericTypeDefinition().GetMethods(); reanchor = true; }
                foreach (var im in ifaceMs)
                {
                    if (!bodied.Contains(im.Name) || !ti.Methods.TryGetValue(im.Name, out MethodBuilder dim)) continue;
                    var ips = im.GetParameters().Select(p => reanchor
                        ? SubstituteIfaceArgs(p.ParameterType, itype.GetGenericArguments())
                        : p.ParameterType).ToArray();
                    // Overload disambiguation by the slot's (substituted) param types — mirrors the class wiring.
                    var cands = ti.MethodsBySig.Values.Where(b => b.Name == im.Name).Distinct().ToList();
                    if (cands.Count > 1)
                    {
                        var match = cands.FirstOrDefault(b => _mparams.TryGetValue(b, out var bps)
                            && bps.Length == ips.Length && bps.Zip(ips, SlotParamMatches).All(x => x));
                        if (match == null) continue;   // no exact overload -> skip rather than mis-wire
                        dim = match;
                    }
                    var iret = reanchor ? SubstituteIfaceArgs(im.ReturnType, itype.GetGenericArguments()) : im.ReturnType;
                    var bridge = ti.TB.DefineMethod("<>dotkt_dimimpl$" + im.Name + "$" + (_covarBridge++),
                        MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
                        iret, ips);
                    var bil = bridge.GetILGenerator();
                    bil.Emit(OpCodes.Ldarg_0);
                    for (int i = 0; i < ips.Length; i++) bil.Emit(OpCodes.Ldarg, i + 1);
                    var dimCall = ti.IsGeneric ? TypeBuilder.GetMethod(ti.TB.MakeGenericType(ti.TB.GetGenericArguments()), dim) : (MethodInfo)dim;
                    bil.Emit(OpCodes.Callvirt, dimCall);
                    bil.Emit(OpCodes.Ret);
                    ti.TB.DefineMethodOverride(bridge, reanchor ? TypeBuilder.GetMethod(itype, im) : im);
                }
            }
        }
        _curTypeParams = null;

        // Pass 4: emit all bodies (every ctor/method signature already exists).
        foreach (var ti in _types.Values)
            for (int ci = 0; ci < ti.Ctors.Count; ci++) { T($"pass4 ctor body: {ti.TB?.Name}#{ci}"); EmitCtorBody(ti, ti.Ctors[ci], ti.CtorDefs[ci]); }
        foreach (var ti in _types.Values)
            if (!ti.IsEnum)
                foreach (var m in ti.Def.GetProperty("methods").EnumerateArray())
                {
                    // Interfaces: emit an IL body ONLY for default methods (those that carry one); abstract slots have none.
                    if (ti.IsInterface && !(m.TryGetProperty("body", out var ib) && ib.ValueKind == JsonValueKind.Array && ib.GetArrayLength() > 0)) continue;
                    T($"pass4 method body: {ti.TB?.Name}.{(m.TryGetProperty("name", out var mn) ? mn.GetString() : "?")}"); EmitMethodBody(ti, m);
                }

        // User annotations -> .NET custom attributes, applied on the type and its methods (the ctor builder of the
        // synthesized `: System.Attribute` class already exists). Args are compile-time constants.
        foreach (var ti in _types.Values)
        {
            if (_stripMetadata) continue;   // runtime build: strip ALL roundtrip metadata (NRT, [Kotlin*], [KotlinInline])
            // [NullableContext(1)] — the per-type NRT default: every reference-type position is non-null unless it
            // carries its own [Nullable(2)]. So a consuming Kotlin (or C#) module sees DotKt's non-null `String` as
            // non-null and `String?` as nullable, through .NET's standard nullable-reference metadata.
            if (ti.TB != null) ApplyNullableContext(ti.TB);
            if (ti.Def.TryGetProperty("attrs", out var tattrs))
                foreach (var a in tattrs.EnumerateArray()) { var cab = BuildCab(a); if (cab != null) ti.TB.SetCustomAttribute(cab); }
            if (ti.Def.TryGetProperty("methods", out var ms))
                foreach (var m in ms.EnumerateArray())
                {
                    if (!(m.TryGetProperty("attrs", out var mattrs) && mattrs.GetArrayLength() > 0)) continue;
                    // Resolve the target MethodBuilder by SIGNATURE first (MethodsBySig), name-only second — overloaded
                    // methods (sin(Double)+sin(Float), append(...), println(...)) share a name, so a name-only lookup
                    // collides on the last-declared overload: every def's attrs land on that ONE builder while the other
                    // overloads get NONE (this dropped @ClrIntrinsic from all-but-last overloads in the ref.dll, which
                    // bir2cir reads as its binding source). Mirror the Kotlin-metadata path below.
                    var mname = m.GetProperty("name").GetString();
                    if (!ti.MethodsBySig.TryGetValue(SigKey(mname, m), out var mb) && !ti.Methods.TryGetValue(mname, out mb)) continue;
                    foreach (var a in mattrs.EnumerateArray()) { var cab = BuildCab(a); if (cab != null) mb.SetCustomAttribute(cab); }
                }
            // DotKt metadata: stamp Kotlin modifiers with no .NET analog so a consuming Kotlin module can restore
            // them. [KotlinFileClass] on a file-facade class -> its statics are top-level fns; [KotlinFunction(flags)] on
            // methods carrying infix/operator/suspend. The attribute types are SYNTHESIZED per-assembly (embedded
            // internal) by DefineEmbeddedAttr (Emitter.CompilerServices.cs) — NOT loaded from DotKt.Runtime.
            if (ti.IsFileClass) ApplyKotlinFileClass(ti.TB);
            // Class-nature markers: a `fun interface` (SAM) lowers to a plain CLR interface, and a `sealed` class/
            // interface lowers to a CLR abstract-class/interface — both lose the Kotlin nature. Stamp a marker so a
            // re-consuming Kotlin module can restore it (facadegen reads them back; a C# consumer ignores them).
            if (ti.TB != null && ti.Def.TryGetProperty("isFun", out var isFun) && isFun.GetBoolean()) ApplyKotlinFunInterface(ti.TB);
            if (ti.TB != null && ti.Def.TryGetProperty("isSealed", out var isSealed) && isSealed.GetBoolean()) ApplyKotlinSealed(ti.TB);
            if (ti.Def.TryGetProperty("methods", out var kms))
                foreach (var m in kms.EnumerateArray())
                {
                    int kf = 0;
                    if (m.TryGetProperty("infix", out var inf) && inf.GetBoolean()) kf |= 1;       // KotlinFunctionFlags.Infix
                    if (m.TryGetProperty("operator", out var op) && op.GetBoolean()) kf |= 2;       // .Operator
                    if (m.TryGetProperty("suspend", out var su) && su.GetBoolean()) kf |= 4;        // .Suspend
                    // The bir2cir-synthesized public Task<R> bridge (bundle-6 P4): a plain `Task`-returning method that
                    // IS the Kotlin `suspend fun`'s CLR ABI. Stamp it Suspend so a round-tripping consumer (kcc/facadegen)
                    // restores `suspend fun f(...)` — its suspend CALLS then lower to the `f$dotkt_suspend` cold entry.
                    if (m.TryGetProperty("suspendBridge", out var sb) && sb.GetBoolean()) kf |= 4;   // .Suspend
                    bool inl = m.TryGetProperty("inline", out var il) && il.GetBoolean();
                    // Nullability mask: bit 0 = return nullable, bit (i+1) = param i nullable.
                    uint nmask = 0;
                    if (m.TryGetProperty("retNullable", out var rn) && rn.GetBoolean()) nmask |= 1u;
                    if (m.TryGetProperty("params", out var nps)) { int pi = 0; foreach (var p in nps.EnumerateArray()) { if (p.TryGetProperty("nullable", out var pn) && pn.GetBoolean()) nmask |= 1u << (pi + 1); pi++; } }
                    // NESTED return nullability (bundle-6 BUG 2): when the nullable `?` rides an INNER type arg — a
                    // `suspend fun f(): String?`'s bridge return `Task<string?>` — the scalar `retNullable` can't express
                    // it. bir2cir supplies the flattened byte walk in `retNullableFlags` ([1,2] = outer non-null, inner
                    // nullable); it takes precedence over the scalar. (No emitter today -> a verified no-op until bir2cir
                    // lands the walk; see the reported CIR contract.)
                    byte[] retFlags = m.TryGetProperty("retNullableFlags", out var rnf) && rnf.ValueKind == JsonValueKind.Array ? ReadNullableFlags(rnf) : null;
                    // H2: a `suspend (…) -> T` RETURN type — bir2cir carries the pre-erasure `sfunc:` shape in `retSuspendFnType`.
                    string retSuspendFn = m.TryGetProperty("retSuspendFnType", out var rsf) ? rsf.GetRawText() : null;
                    if (kf == 0 && !inl && nmask == 0 && retFlags == null && string.IsNullOrEmpty(retSuspendFn)) continue;
                    var name = m.GetProperty("name").GetString();
                    if (!ti.MethodsBySig.TryGetValue(SigKey(name, m), out var mb) && !ti.Methods.TryGetValue(name, out mb)) continue;
                    if (kf != 0) ApplyKotlinFunction(mb, kf);
                    // [KotlinInline(body)]: carry this inline+lambda fn's BIR (params + body) so a consumer can splice it.
                    if (inl) ApplyKotlinInline(mb, "{\"params\":" + m.GetProperty("params").GetRawText() + ",\"body\":" + m.GetProperty("body").GetRawText() + "}");
                    // Return-position metadata rides the return parameter (position 0). Define it ONCE (a second
                    // DefineParameter(0) would be a duplicate builder) and stamp every present fact: [Nullable(...)] for a
                    // nullable return (nested byte-array form wins over the scalar), [KotlinSuspendFunctionType] for a
                    // suspend fn-type return. The type's [NullableContext(1)] is the non-null default, so only the nullable
                    // positions need an override.
                    if (retFlags != null || (nmask & 1u) != 0 || !string.IsNullOrEmpty(retSuspendFn))
                    {
                        var retPb = mb.DefineParameter(0, ParameterAttributes.None, null);
                        if (retFlags != null) ApplyNullable(retPb, retFlags);
                        else if ((nmask & 1u) != 0) ApplyNullable(retPb);
                        if (!string.IsNullOrEmpty(retSuspendFn)) ApplySuspendFnType(retPb, retSuspendFn);
                    }
                }
        }

        // Pass 4b: static-field initializers (companion `val`s) -> a type initializer (.cctor).
        foreach (var ti in _types.Values)
        {
            if (ti.IsInterface || !ti.Def.TryGetProperty("fields", out var fs)) continue;
            var inits = fs.EnumerateArray().Where(f => f.TryGetProperty("init", out _) && f.TryGetProperty("static", out var s) && s.GetBoolean()).ToList();
            if (inits.Count == 0) continue;
            _il = ti.TB.DefineTypeInitializer().GetILGenerator();
            _args.Clear(); _argTypes.Clear(); _locals.Clear(); _methodRetType = typeof(void);
            // A field initializer can contain CFG control flow (a `while`/`when` lowered to label/goto), so its labels
            // must be pre-defined just like a method body — otherwise MarkLabel/Br throws "key not in _cfgLabels".
            // Coerce the init value to the field's declared type (box a value-type/enum RHS stored into an
            // `object`/wider reference field) — the SAME shared store coercion the method-body sites use; without
            // it, `val X: Any = SomeEnum.ENTRY` stored the raw ordinal (int) into an object field as a null ref.
            foreach (var f in inits) { var fb = ti.Fields[f.GetProperty("name").GetString()]; PrescanCfgLabels(f.GetProperty("init")); EmitStoreCoerced(f.GetProperty("init"), fb.FieldType); MaybeVolatile(fb); _il.Emit(OpCodes.Stsfld, fb); }
            _il.Emit(OpCodes.Ret);
        }

        // Pass 5: synthesize entry point on the file class that has `main`.
        MethodBuilder entry = null;
        foreach (var ti in _types.Values)
            if (ti.IsFileClass && ti.FileElem.Value.GetProperty("hasMain").GetBoolean() && ti.Methods.ContainsKey("main"))
            {
                entry = ti.TB.DefineMethod("Main", MethodAttributes.Public | MethodAttributes.Static, typeof(void), new[] { typeof(string[]) });
                var il = entry.GetILGenerator();
                var mainMb = ti.Methods["main"];
                // `fun main(args: Array<String>)` -> forward the CLR args; `fun main()` -> call with none.
                if (_mparams.TryGetValue(mainMb, out var mp) && mp.Length > 0) il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, mainMb);
                il.Emit(OpCodes.Ret);
            }

        // Pass 6: bake types (base before derived). Enums were already baked up front.
        foreach (var ti in Ordered()) { if (!ti.IsEnum) { T($"pass6 createType: {ti.TB?.Name}"); ti.TB.CreateType(); } }
        // The reverse-bridge adapter references the (now-baked) Kotlin Iterator type, so bake it after the user types.
        if (_enumAdapterTB != null && !_enumAdapterTB.IsCreated()) _enumAdapterTB.CreateType();
        foreach (var tb in _syntheticDelegates.Values)
            if (!tb.IsCreated())
                tb.CreateType();
        // Safety net: any user type Ordered() somehow missed (so Save won't throw "not supported before the type is
        // created"). Repeat until stable, since creating one may be a prerequisite for another.
        for (bool again = true; again;)
        {
            again = false;
            foreach (var ti in _types.Values)
                if (!ti.IsEnum && ti.TB != null && !ti.TB.IsCreated())
                {
                    T($"pass6 createType (leftover): {ti.TB.Name}");
                    ti.TB.CreateType(); again = true;
                }
        }

        T("save: writing PE");
        Save(ab, entry);
        T("save: done");
    }

    IEnumerable<TypeInfo> Ordered()
    {
        // Dedup by type IDENTITY, not simple name: two distinct types can share a simple name (a top-level `State`
        // and a nested `X.State`, or same-named types in different files). Keying by name dropped the second from the
        // create order -> it was never CreateType()'d -> Save threw "not supported before the type is created".
        var done = new HashSet<TypeInfo>();
        var result = new List<TypeInfo>();
        void Visit(TypeInfo ti)
        {
            if (!done.Add(ti)) return;
            if (ti.BaseName != null && _types.TryGetValue(ti.BaseName, out var b)) Visit(b);
            // A generic interface used as a constructed parent/interface must be created before its implementers
            // (PersistedAssemblyBuilder materializes the instantiation at the implementer's CreateType).
            if (!ti.IsFileClass && ti.Def.TryGetProperty("interfaces", out var ifs))
                foreach (var i in ifs.EnumerateArray())
                {
                    var spec = i.GetString();
                    if (spec.StartsWith("clr:") || spec.StartsWith("clrg:")) continue;  // .NET iface — not a user-type dep
                    if (_types.TryGetValue(OwnerOpen(spec), out var inf)) Visit(inf);
                }
            // A nested type must be CreateType()'d BEFORE its enclosing type (Reflection.Emit bakes children into the
            // parent). `done` already holds `ti` (added at entry), so a child whose base IS `ti` won't recurse forever.
            var myName = ti.IsFileClass ? null : (ti.Def.TryGetProperty("name", out var nm) ? nm.GetString() : null);
            if (myName != null)
                foreach (var child in _types.Values)
                    if (!child.IsFileClass && !child.IsEnum && child.Def.TryGetProperty("nestedIn", out var cni) && cni.GetString() == myName)
                        Visit(child);
            result.Add(ti);
        }
        foreach (var ti in _types.Values) Visit(ti);
        return result;
    }

    // Kotlin visibility -> CLR method/ctor access flag (default public).
    static MethodAttributes AccessOf(JsonElement m) =>
        (m.TryGetProperty("vis", out var v) ? v.GetString() : "public") switch
        {
            "private" => MethodAttributes.Private,
            "internal" => MethodAttributes.Assembly,
            "protected" => MethodAttributes.Family,
            _ => MethodAttributes.Public,
        };

    // Method-level generic params, keyed by MethodInfo, so call sites can MakeGenericMethod.
    readonly Dictionary<MethodBuilder, Dictionary<string, GenericTypeParameterBuilder>> _methodTypeParams = new();

    // Body-phase occurrence counter for duplicate (name, params) defs — mirrors DeclareMethod's $dupN mangling.
    readonly Dictionary<(TypeInfo, string), int> _bodyDupSeen = new();

    void DeclareMethod(TypeInfo ti, JsonElement m, bool isStatic)
    {
        var name = m.GetProperty("name").GetString();
        // DUPLICATE (name, params) defs — Kotlin overloads distinguished ONLY by receiver types that COLLAPSE under a
        // @ClrTypeAlias (Map.iterator() vs MutableMap.iterator(): both receivers lower to IDictionary<K,V>) — would
        // otherwise share one MethodsBySig slot, concatenating BOTH bodies into a single MethodBuilder (malformed IL,
        // BadImageFormatException). Mangle the SECOND-and-later defs' emitted names (deterministic, def order — the
        // FIRST def keeps the clean name, so by-(name,params) reflection callers bind it unambiguously). EmitMethodBody
        // consumes the same #dupN keys in the same def order.
        var dupKey = SigKey(name, m);
        if (ti.MethodsBySig.ContainsKey(dupKey))
        {
            var n = 2;
            while (ti.MethodsBySig.ContainsKey(SigKey(name + "$dup" + n, m))) n++;
            name = name + "$dup" + n;
        }
        // Interface members are always public; otherwise map Kotlin visibility to a CLR access flag.
        var attrs = ti.IsInterface ? MethodAttributes.Public : AccessOf(m);
        // A method's own `static` flag (companion methods are static members of a user class).
        isStatic = isStatic || m.GetProperty("static").GetBoolean();
        var objOverride = m.TryGetProperty("objectOverride", out var oo) && oo.GetBoolean();
        // Overriding a .NET base virtual (e.g. `override val Message`) reuses the base slot, like an object-method.
        var clrOverride = m.TryGetProperty("clrOverride", out var co) ? co.GetString() : null;
        // An interface method with a DEFAULT body -> a CLR default interface method (Virtual|NewSlot, real IL body in
        // Pass 4); a bare slot (no body) stays Virtual|Abstract|NewSlot. (A Kotlin interface default impl, e.g.
        // CoroutineContext.plus, must carry its body so non-overriding implementers inherit it instead of failing load.)
        if (ti.IsInterface)
        {
            attrs |= MethodAttributes.Virtual | MethodAttributes.NewSlot;
            if (!(m.TryGetProperty("body", out var ifb) && ifb.ValueKind == JsonValueKind.Array && ifb.GetArrayLength() > 0))
                attrs |= MethodAttributes.Abstract;
        }
        else if (isStatic) attrs |= MethodAttributes.Static;
        // `ToString`/`Equals`/`GetHashCode` and .NET base overrides reuse the base slot (Virtual, no NewSlot).
        else if (objOverride || clrOverride != null) attrs |= MethodAttributes.Virtual | MethodAttributes.HideBySig;
        else if (m.GetProperty("override").GetBoolean()) attrs |= MethodAttributes.Virtual;
        else if (m.GetProperty("virtual").GetBoolean()) attrs |= MethodAttributes.Virtual | MethodAttributes.NewSlot;
        // An `abstract fun` (no body) -> a CLR abstract method: Virtual|Abstract, no IL body (subclasses override).
        if (m.TryGetProperty("abstract", out var amb) && amb.GetBoolean()) attrs |= MethodAttributes.Abstract | MethodAttributes.Virtual;

        // NOTE: ilemit no longer rewrites a `suspend fun`'s signature to `Task<T>`. The cold-core coroutine lowering
        // (bir2cir, bundle-6) already arrives here as ordinary CIR: the public `Task<T>` bridge is its OWN method
        // carrying `suspendBridge:true` (stamped `[KotlinFunction(Suspend)]` below), and the cold entry / state-machine
        // class are plain methods/types. A leftover `"suspend":true` method falls through to the normal signature path;
        // at body time it emits a throwing stub in a STDLIB build (expected — the coroutine primitives have no SM form)
        // but is an emit-time ERROR in an app build (a bir2cir transform miss — see EmitMethodBody's suspend guard).

        MethodBuilder mb;
        Type[] ps;
        var genTps = m.TryGetProperty("typeParams", out var mtp) && mtp.GetArrayLength() > 0 ? (JsonElement?)mtp : null;
        if (genTps != null)
        {
            // Generic method `fun <T> id(x: T): T`: the signature references the method's own type params, so
            // they must be defined before SetParameters/SetReturnType (staged form, not the one-shot DefineMethod).
            var genNames = TpNames(genTps.Value);
            mb = ti.TB.DefineMethod(name, attrs);
            var gps = mb.DefineGenericParameters(genNames);
            var map = new Dictionary<string, GenericTypeParameterBuilder>();
            for (int gi = 0; gi < genNames.Length; gi++) map[genNames[gi]] = gps[gi];
            _methodTypeParams[mb] = map;
            _curMethodParams = map;
            ApplyConstraints(genTps.Value, map, false);   // `<T : Comparable<T>>` on the method (variance N/A on methods)
            ps = m.GetProperty("params").EnumerateArray().Select(p => MapType(p.GetProperty("type"))).ToArray();
            mb.SetParameters(ps);
            mb.SetReturnType(MapType(m.GetProperty("ret")));
            _curMethodParams = null;
        }
        else
        {
            ps = m.GetProperty("params").EnumerateArray().Select(p => MapType(p.GetProperty("type"))).ToArray();
            mb = ti.TB.DefineMethod(name, attrs, MapType(m.GetProperty("ret")), ps);
        }
        ti.Methods[name] = mb; ti.MethodsBySig[SigKey(name, m)] = mb;
        _mparams[mb] = ps;   // MethodBuilder.GetParameters() throws pre-bake; record param types for call-site boxing
        DefineParamNames(mb, m);
        if (objOverride)
        {
            var objM = name switch
            {
                "ToString" => typeof(object).GetMethod("ToString", Type.EmptyTypes),
                "GetHashCode" => typeof(object).GetMethod("GetHashCode", Type.EmptyTypes),
                "Equals" => typeof(object).GetMethod("Equals", new[] { typeof(object) }),
                _ => null,
            };
            if (objM != null) ti.TB.DefineMethodOverride(mb, objM);
        }
        if (clrOverride != null)
        {
            // Link the override to the .NET base virtual (matched by name + parameter types) so virtual dispatch
            // through the base type reaches it (`callvirt System.Exception::get_Message` -> our override).
            var baseT = ResolveType(clrOverride);
            var baseM = baseT.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, ps, null)
                        ?? baseT.GetMethod(name);
            if (baseM != null) ti.TB.DefineMethodOverride(mb, baseM);
        }
    }

    void EmitCtorBody(TypeInfo ti, ConstructorBuilder cb, JsonElement c)
    {
        _methodRetType = typeof(void);
        _curTypeParams = EffectiveTps(ti); _curMethodParams = null;
        BeginMethod(cb.GetILGenerator(), c, isStatic: false);
        PrescanCfgLabels(c.GetProperty("body"));

        _il.Emit(OpCodes.Ldarg_0);
        if (c.TryGetProperty("thisArgs", out var ta) && ta.ValueKind == JsonValueKind.Array)
        {
            // `constructor(...) : this(...)` -> delegate to a sibling ctor (it runs field inits / base call).
            foreach (var a in ta.EnumerateArray()) EmitExpr(a);
            ConstructorInfo sibling = SelectCtor(ti, ta.GetArrayLength());
            // Inside a GENERIC type, the sibling ctor must be referenced through the SELF-instantiation
            // `C`1<!T>` (the type over its OWN generic params), NOT the open definition `C`1` — a bare
            // `call C`1::.ctor` is "not fully instantiated" at JIT. Mirrors the base-ctor anchoring below
            // (the `: base(...)` branches ~lines 918-920 / 894-898); do not "simplify" this away.
            if (ti.TB is TypeBuilder stb && stb.IsGenericTypeDefinition)
                sibling = TypeBuilder.GetConstructor(stb.MakeGenericType(stb.GetGenericArguments()), (ConstructorBuilder)sibling);
            _il.Emit(OpCodes.Call, sibling);
        }
        else if (ti.ClrBase != null)
        {
            // `: base(...)` on a .NET base -> the matching base constructor (resolved by reflection). A constructed
            // generic base (`Collection<int>`) needs the static helper to map the open ctor onto the instantiation.
            var ba = c.TryGetProperty("baseArgs", out var b) && b.ValueKind == JsonValueKind.Array ? b : default;
            int argc = ba.ValueKind == JsonValueKind.Array ? ba.GetArrayLength() : 0;
            const BindingFlags ctorFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            ConstructorInfo ctor;
            // A generic base instantiated with a TypeBuilder/generic-param arg needs the static helper; a base
            // that is non-generic or instantiated over concrete types is a real RuntimeType -> direct reflection.
            if (ti.ClrBase.IsGenericType && ti.ClrBase.GetGenericArguments().Any(a => a is TypeBuilder || a.IsGenericParameter))
            {
                var open = ti.ClrBase.GetGenericTypeDefinition();
                var openCtor = open.GetConstructors(ctorFlags).FirstOrDefault(x => x.GetParameters().Length == argc) ?? open.GetConstructor(Type.EmptyTypes);
                ctor = TypeBuilder.GetConstructor(ti.ClrBase, openCtor);
            }
            else
            {
                ctor = ti.ClrBase.GetConstructors(ctorFlags).FirstOrDefault(x => x.GetParameters().Length == argc) ?? ti.ClrBase.GetConstructor(Type.EmptyTypes);
            }
            if (ba.ValueKind == JsonValueKind.Array) EmitArgs(ba, ctor.GetParameters());
            _il.Emit(OpCodes.Call, ctor);
        }
        else if (ti.BaseName != null && _types.ContainsKey(ti.BaseName) && c.TryGetProperty("baseArgs", out var ba2) && ba2.ValueKind == JsonValueKind.Array)
        {
            // `: base(...)` -> the Kotlin-user base class's ctor whose param count matches (a base with
            // secondary ctors — e.g. ContinuationImpl(completion) vs (completion, _context) — must bind the
            // right overload, not always the primary; mirrors the ClrBase (arg-count) + thisArgs (SelectCtor) paths).
            ConstructorInfo bctor = SelectCtor(_types[ti.BaseName], ba2.GetArrayLength());
            // A generic base instantiated over THIS type's own type params (`class D<T> : Base<T>()`) has its
            // parent set to the CONSTRUCTED base `Base<!T>` (ti.TB.BaseType); the base-ctor operand must be scoped
            // to that constructed type, not the open definition `Base<>` — a bare `call Base``1::.ctor` is "not
            // fully instantiated" (InvalidProgramException). Anchor the open ConstructorBuilder onto the constructed
            // base via the static helper (mirrors closureNew's TypeBuilder.GetConstructor over MakeGenericType).
            var baseType = ti.TB.BaseType;
            if (baseType != null && baseType.IsGenericType && !baseType.IsGenericTypeDefinition)
                bctor = TypeBuilder.GetConstructor(baseType, bctor);
            foreach (var a in ba2.EnumerateArray()) EmitExpr(a);
            _il.Emit(OpCodes.Call, bctor);
        }
        else
        {
            _il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes));
        }
        foreach (var s in c.GetProperty("body").EnumerateArray()) EmitStmt(s);
        _il.Emit(OpCodes.Ret);
    }

    // Pick the ctor (primary or secondary) whose parameter count matches the delegating/`new` arg count.
    ConstructorBuilder SelectCtor(TypeInfo ti, int argCount)
    {
        for (int i = 0; i < ti.Ctors.Count; i++)
            if (ti.CtorDefs[i].GetProperty("params").GetArrayLength() == argCount) return ti.Ctors[i];
        return ti.Ctor;
    }

    // Define a type's constructors from its CIR (idempotent). Normally runs in pass 3, but BuildCab pulls it EARLY when
    // stamping a param/method attribute whose attribute type is emitted in THIS assembly (e.g. `@kotlin.clr.KotlinDefault
    // (index, bir)` on a defaulted stdlib parameter): pass 3 declares members type-by-type, so a `@KotlinDefault`
    // application on an EARLIER type's method would otherwise reach BuildCab before KotlinDefault's own `(int,string)`
    // ctor was defined — the old `ti.Ctors[0] ?? DefineDefaultConstructor()` then minted a bogus parameterless ctor per
    // application and every stamp failed "Parameter count does not match". Defining ctors on demand (guarded) makes the
    // real ctor available whenever it is first needed. Interfaces/enums/file classes have no CIR ctors.
    void EnsureCtorsDefined(TypeInfo ti)
    {
        if (ti.CtorsDefined) return;
        ti.CtorsDefined = true;
        if (ti.IsInterface || ti.IsEnum || ti.IsFileClass || !ti.Def.TryGetProperty("ctors", out var ctors)) return;
        var saved = _curTypeParams;
        _curTypeParams = EffectiveTps(ti);   // so a `gp:T` ctor param resolves when pulled early out of pass-3 order
        foreach (var c in ctors.EnumerateArray())
        {
            var ps = c.GetProperty("params").EnumerateArray().Select(p => MapType(p.GetProperty("type"))).ToArray();
            var cb = ti.TB.DefineConstructor(AccessOf(c), CallingConventions.Standard, ps);
            DefineParamNames(cb, c);   // ctor param NAMES + [Optional]/DefaultParameterValue (named-arg ctor calls)
            ti.Ctors.Add(cb);
            ti.CtorDefs.Add(c);
        }
        if (ti.Ctors.Count > 0) { ti.Ctor = ti.Ctors[0]; ti.CtorDef = ti.CtorDefs[0]; }
        _curTypeParams = saved;
    }

    void EmitMethodBody(TypeInfo ti, JsonElement m)
    {
        // An abstract method has no IL body (subclasses provide it); GetILGenerator would throw.
        if (m.TryGetProperty("abstract", out var amb) && amb.GetBoolean()) return;
        var mname = m.GetProperty("name").GetString();
        // A DUPLICATE (name, params) def was define-phase-mangled to `name$dupN` (see DeclareMethod); body emission
        // walks the same def array in the same order, so consume the occurrences symmetrically — without this, both
        // bodies would be written into ONE MethodBuilder (concatenated IL -> BadImageFormatException).
        var dupCount = _bodyDupSeen.TryGetValue((ti, SigKey(mname, m)), out var seen) ? seen : 0;
        _bodyDupSeen[(ti, SigKey(mname, m))] = dupCount + 1;
        if (dupCount > 0) mname = mname + "$dup" + (dupCount + 1);
        // Pick THIS def's own MethodBuilder by signature (overloads share `mname`; the name-keyed map holds only the
        // last, so emitting by name alone routes a body into the wrong overload — the WinUI `text(String)` /
        // `text(()->String)` bug).
        var mb = ti.MethodsBySig.TryGetValue(SigKey(mname, m), out var bm) ? bm : ti.Methods[mname];
        _methodRetType = mb.ReturnType;
        _curTypeParams = EffectiveTps(ti);
        _curMethodParams = _methodTypeParams.TryGetValue(mb, out var mp) ? mp : null;
        if (m.TryGetProperty("suspend", out var su) && su.GetBoolean())
        {
            // A leftover `"suspend":true` method reaching ilemit means the real coroutine state machine (cold entry +
            // `ContinuationImpl` SM class + public `Task<T>` bridge) was NOT synthesized — that lowering is bir2cir's
            // (cold-core, bundle-6); ilemit itself is coroutine-codegen-free.
            //
            // In a STDLIB build (ref OR rt) this is EXPECTED: the coroutine PRIMITIVES — suspendCoroutine[Unintercepted
            // OrReturn], yield/yieldAll, callRecursive, and the kotlin.clr.CoroutinesKt await/delay bridge — have no
            // state-machine form; bir2cir deliberately leaves their DEFINITIONS un-lowered "for the ilemit throw-stub"
            // (SuspendColdLowering.cs), transforming only their CALL SITES. Their bodies are effectively dead (no real
            // caller survives), so a throwing stub is the correct emission. Keep it, unchanged.
            if (StdlibStub) { EmitThrowStub(mb, "suspend (reference stub)"); return; }
            // In an APP build there are no such primitives — every suspend fn is a real coroutine that bir2cir must
            // lower. Reaching here is therefore a bir2cir transform MISS (a disqualified/un-lowered suspend shape). Fail
            // LOUD at emit time — naming the method — instead of silently emitting a throwing stub that surfaces as a
            // distant runtime throw. A NEW error here is a real bir2cir defect to fix upstream, NOT to re-silence.
            throw new NotSupportedException(
                $"ilemit: suspend method '{ti.TB?.Name}.{mname}' reached codegen un-lowered — bir2cir's cold-core suspend " +
                $"lowering must transform it into a public Task bridge + plain state-machine methods before ilemit (which " +
                $"is coroutine-codegen-free). This is a bir2cir transform MISS.");
        }
        BeginMethod(mb.GetILGenerator(), m, isStatic: mb.IsStatic);
        PrescanCfgLabels(m.GetProperty("body"));
        foreach (var s in m.GetProperty("body").EnumerateArray()) EmitStmt(s);
        _il.Emit(OpCodes.Ret);
    }

    // Define an IL Label for every CFG `label` node anywhere in the body (forward refs from goto/brIf), so the
    // single emit pass can branch to not-yet-emitted blocks. Recursive: labels can sit inside nested structured
    // bodies (a CFG-lowered `while` spliced into a still-structured `if`). See docs/design-il-cfg.md.
    void PrescanCfgLabels(JsonElement node)
    {
        _cfgLabels = new Dictionary<int, Label>();
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("k", out var k) && k.GetString() == "label")
                {
                    var id = e.GetProperty("id").GetInt32();
                    if (!_cfgLabels.ContainsKey(id)) _cfgLabels[id] = _il.DefineLabel();
                }
                foreach (var p in e.EnumerateObject()) Walk(p.Value);
            }
            else if (e.ValueKind == JsonValueKind.Array)
                foreach (var x in e.EnumerateArray()) Walk(x);
        }
        Walk(node);
    }


    void EmitLdcI4(int n)
    {
        if (n == -1) _il.Emit(OpCodes.Ldc_I4_M1);
        else _il.Emit(OpCodes.Ldc_I4, n);
    }

    void BeginMethod(ILGenerator il, JsonElement m, bool isStatic)
    {
        _il = il; _args.Clear(); _argTypes.Clear(); _locals.Clear();
        int i = isStatic ? 0 : 1; // arg0 = this for instance methods
        foreach (var p in m.GetProperty("params").EnumerateArray())
        {
            var pn = p.GetProperty("name").GetString();
            _argTypes[pn] = MapType(p.GetProperty("type"));
            _args[pn] = i++;
        }
    }

    // ---- statements ----
    // Does this statement list contain a `return` anywhere (recursing into if/while/try bodies)? Drives whether a
    // `try` needs a dedicated return label + trailing ret.
    static bool StmtsHaveReturn(JsonElement arr)
    {
        foreach (var s in arr.EnumerateArray()) if (StmtHasReturn(s)) return true;
        return false;
    }
    static bool StmtHasReturn(JsonElement s)
    {
        if (s.GetProperty("k").GetString() == "return") return true;
        foreach (var key in new[] { "body", "finally" })
            if (s.TryGetProperty(key, out var b) && b.ValueKind == JsonValueKind.Array && StmtsHaveReturn(b)) return true;
        if (s.TryGetProperty("branches", out var brs))
            foreach (var br in brs.EnumerateArray())
                if (br.TryGetProperty("body", out var bb) && StmtsHaveReturn(bb)) return true;
        if (s.TryGetProperty("catches", out var cs))
            foreach (var c in cs.EnumerateArray())
                if (StmtsHaveReturn(c.GetProperty("body"))) return true;
        return false;
    }
    // Does this statement list ALWAYS return/throw (no fall-through)? Used to decide if a `try`'s fall-through path
    // is reachable (and thus whether to emit a `br` over the trailing ret).
    static bool StmtsAlwaysReturn(JsonElement arr)
    {
        JsonElement last = default; bool any = false;
        foreach (var s in arr.EnumerateArray()) { last = s; any = true; }
        return any && StmtAlwaysReturns(last);
    }
    static bool StmtAlwaysReturns(JsonElement s)
    {
        switch (s.GetProperty("k").GetString())
        {
            case "return": case "throw": return true;
            case "if":
                bool hasElse = false;
                foreach (var br in s.GetProperty("branches").EnumerateArray())
                {
                    if (br.TryGetProperty("else", out _)) hasElse = true;
                    if (!StmtsAlwaysReturn(br.GetProperty("body"))) return false;
                }
                return hasElse;
            case "try":
                if (!StmtsAlwaysReturn(s.GetProperty("body"))) return false;
                foreach (var c in s.GetProperty("catches").EnumerateArray())
                    if (!StmtsAlwaysReturn(c.GetProperty("body"))) return false;
                return true;
            default: return false;
        }
    }


    // The loop a break/continue targets: the innermost, or the one whose Kotlin label matches.
    (Label cont, Label brk) TargetLoop(JsonElement s)
    {
        string label = s.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
        for (int i = _loops.Count - 1; i >= 0; i--)
            if (label == null || _loops[i].label == label) return (_loops[i].cont, _loops[i].brk);
        throw new NotSupportedException("break/continue with no matching loop");
    }

    static string LoopLabel(JsonElement s) => s.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;

    // Enumerate an IEnumerable<elemT> `src`, binding each element to a fresh local passed to `body`.
    void EmitForEachOf(JsonElement src, Type elemT, Action<LocalBuilder> body)
    {
        var ienumT = typeof(System.Collections.Generic.IEnumerable<>).MakeGenericType(elemT);
        var ienumrT = typeof(System.Collections.Generic.IEnumerator<>).MakeGenericType(elemT);
        EmitExpr(src);
        _il.Emit(OpCodes.Callvirt, ienumT.GetMethod("GetEnumerator"));
        var en = _il.DeclareLocal(ienumrT); _il.Emit(OpCodes.Stloc, en);
        var x = _il.DeclareLocal(elemT);
        var start = _il.DefineLabel(); var end = _il.DefineLabel();
        _il.MarkLabel(start);
        _il.Emit(OpCodes.Ldloc, en);
        _il.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetMethod("MoveNext"));
        _il.Emit(OpCodes.Brfalse, end);
        _il.Emit(OpCodes.Ldloc, en);
        _il.Emit(OpCodes.Callvirt, ienumrT.GetMethod("get_Current"));
        _il.Emit(OpCodes.Stloc, x);
        body(x);
        _il.Emit(OpCodes.Br, start);
        _il.MarkLabel(end);
    }

    // Emit `value` COERCED to the store target's type — the ONE shared RHS coercion for every store site
    // (var init, setLocal into a local/arg, setField/setFieldExpr via setter or field, staticFieldSet):
    //  - `T`/null-const stored into a `Nullable<T>` slot -> wrap / default(Nullable<T>) (EmitNullableCoerced);
    //  - a value-type / generic-param RHS stored into a REFERENCE slot -> box (the var-init rule; the other store
    //    sites used to emit the raw RHS, so `var a: Any = "x"; a = 42` stored a raw int32 into an object local ->
    //    NRE/heap corruption at use).
    // A null/unknown target emits the value as-is (no spurious boxing).
    void EmitStoreCoerced(JsonElement value, Type target)
    {
        if (target == null) { EmitExpr(value); return; }
        var got = EmitNullableCoerced(value, target);
        if (got != null && NeedsBoxToRef(got) && !target.IsValueType && !target.IsGenericParameter)
            _il.Emit(OpCodes.Box, got);
    }

    // The value-parameter type of a property setter, when retrievable: a TypeBuilder-anchored accessor
    // (a TypeBuilder.GetMethod re-anchor) throws NotSupportedException on GetParameters() — treat as unknown
    // (EmitStoreCoerced then emits the RHS as-is, the pre-helper behavior for that path).
    static Type SetterValueType(MethodInfo setter)
    {
        try { var ps = setter.GetParameters(); return ps.Length > 0 ? ps[^1].ParameterType : null; }
        catch (NotSupportedException) { return null; }
    }

    // An ownerType spec is either `Name` (plain) or `Name[arg,...]` (a constructed user generic, e.g. `Box[int]`).
    // For a constructed generic, members are resolved on the OPEN definition (the Builder) and then wrapped onto
    // the constructed type via the static `TypeBuilder.GetX` helpers — the MakeGenericType result's own
    // GetMethod/GetField/GetConstructor throw NotSupportedException on the persisted builder (verified, .NET 10).
    // A typeParams entry is either a bare name string `"T"` (unconstrained) or `{"name":"T","constraints":[...]}`.
    static string TpName(JsonElement x) => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetProperty("name").GetString();
    static string[] TpNames(JsonElement tps) => tps.EnumerateArray().Select(TpName).ToArray();

    // Apply generic constraints (`<T : Comparable<T>>` -> `T : IComparable<T>`) to already-defined params. The
    // constraint context map (type or method params) must be current so a `gp:T` inside a bound resolves.
    // True if the type string mentions the type param `gp:<pname>` (token-exact, so `gp:E` doesn't match `gp:E2`).
    static bool MentionsParam(string typeStr, string pname)
    {
        if (typeStr == null) return false;
        var tok = "gp:" + pname; int i = 0;
        while ((i = typeStr.IndexOf(tok, i, StringComparison.Ordinal)) >= 0)
        {
            int end = i + tok.Length;
            if (end >= typeStr.Length || !(char.IsLetterOrDigit(typeStr[end]) || typeStr[end] == '_')) return true;
            i = end;
        }
        return false;
    }

    void ApplyConstraints(JsonElement tps, Dictionary<string, GenericTypeParameterBuilder> map, bool isInterface, JsonElement? typeDef = null)
    {
        foreach (var x in tps.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Object) continue;
            var gp = map[x.GetProperty("name").GetString()];
            // Declaration-site variance is legal CLR metadata only on an interface type param, AND only when the param
            // is NOT used in a conflicting position: a covariant `out E` may not appear in an `in` (method-argument)
            // position, a contravariant `in E` not in an `out` (return) position. Kotlin permits the conflict via
            // @UnsafeVariance (e.g. `Collection<out E>.contains(element: E)`); the CLR has no such escape, so such a
            // param MUST be emitted invariant or the whole type fails to load. Keep clearly-valid variance, drop the rest.
            if (isInterface && x.TryGetProperty("variance", out var v))
            {
                var vs = v.GetString();
                var pname = x.GetProperty("name").GetString();
                bool conflict = false;
                if ((vs == "out" || vs == "in") && typeDef is { } td && td.TryGetProperty("methods", out var ms))
                    foreach (var m in ms.EnumerateArray())
                    {
                        if (vs == "out" && m.TryGetProperty("params", out var ps))
                            foreach (var p in ps.EnumerateArray())
                                if (p.TryGetProperty("type", out var pt) && MentionsParam(pt.GetString(), pname)) { conflict = true; break; }
                        if (vs == "in" && m.TryGetProperty("ret", out var rt) && MentionsParam(rt.GetString(), pname)) conflict = true;
                        if (conflict) break;
                    }
                var attr = conflict ? GenericParameterAttributes.None
                         : vs == "out" ? GenericParameterAttributes.Covariant
                         : vs == "in" ? GenericParameterAttributes.Contravariant
                         : GenericParameterAttributes.None;
                if (attr != GenericParameterAttributes.None) gp.SetGenericParameterAttributes(attr);
            }
            if (x.TryGetProperty("constraints", out var cs))
            {
                var types = cs.EnumerateArray().Select(c => MapType(c)).ToList();
                var ifaces = types.Where(t => t.IsInterface).ToArray();
                var baseT = types.FirstOrDefault(t => !t.IsInterface);
                if (baseT != null) gp.SetBaseTypeConstraint(baseT);
                if (ifaces.Length > 0) gp.SetInterfaceConstraints(ifaces);
            }
        }
    }

    // The OPEN type name of an owner spec, WITHOUT resolving its generic args. The type-ordering pass runs with no
    // type-param scope, so a `Foo[gp:E]` base/interface would crash MapType("gp:E"); ordering only needs the open dep.
    static string OwnerOpen(string spec) { var br = spec.IndexOf('['); return br < 0 ? spec : spec.Substring(0, br); }

    int _covarBridge = 0;

    // Emit a covariant-return bridge: a private explicit-interface-impl method with the iface's (base) return type +
    // params, calling the narrow body method on `this` and returning it (a ref upcast); the MethodImpl goes on the bridge.
    void EmitCovariantBridge(TypeInfo ti, string name, JsonElement imDef, DotKt.Bir.TypeNode[] specArgs, MethodBuilder body, MethodInfo ifaceMethod, Type ifaceRet)
    {
        var paramTypes = imDef.GetProperty("params").EnumerateArray()
            .Select(p => MapType(SubstTv(DotKt.Bir.TypeNode.Read(p.GetProperty("type")), specArgs))).ToArray();
        var bridge = ti.TB.DefineMethod("<>dotkt_covar$" + name + "$" + (_covarBridge++),
            MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
            ifaceRet, paramTypes);
        var il = bridge.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        for (int i = 0; i < paramTypes.Length; i++) il.Emit(OpCodes.Ldarg, i + 1);
        var bodyCall = ti.IsGeneric ? TypeBuilder.GetMethod(ti.TB.MakeGenericType(ti.TB.GetGenericArguments()), body) : (MethodInfo)body;
        il.Emit(OpCodes.Callvirt, bodyCall);
        // ifaceRet==void but the body returns a value (add():Boolean -> ICollection.Add():void): the BCL slot drops the
        // Kotlin return -> pop it so the void bridge leaves an empty stack. Else the (reference) narrow return upcasts.
        if (ifaceRet == typeof(void) && body.ReturnType != typeof(void)) il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);
        ti.TB.DefineMethodOverride(bridge, ifaceMethod);
    }

    // A class implements an interface method (Comparable.compareTo) for which it has no own body, but a DIRECT base
    // interface provides it as a DEFAULT method (ComparableTimeMark.compareTo DIM). The CLR doesn't treat the DIM as
    // implicitly implementing the base interface method, so emit a class-level forwarding bridge that calls the inherited
    // DIM (callvirt the base-interface method on `this`) and put the MethodImpl for the interface method on the bridge.
    void TryEmitDimForwardBridge(TypeInfo ti, JsonElement imDef, DotKt.Bir.TypeNode[] specArgs, string subSig, Type constructed, MethodBuilder ifaceBuilder)
    {
        if (ti.Def.ValueKind != JsonValueKind.Object || !ti.Def.TryGetProperty("interfaces", out var dirIfs)) return;
        foreach (var di in dirIfs.EnumerateArray())
        {
            if (DotKt.Bir.TypeNode.Read(di) is not DotKt.Bir.TypeNode.Fqn diF) continue;
            var (dopen, _) = ParseOwnerT(diF);
            if (!_types.TryGetValue(dopen, out var diTi) || !diTi.MethodsBySig.TryGetValue(subSig, out var dim)) continue;
            if (dim.Attributes.HasFlag(MethodAttributes.Abstract)) continue;   // need an actual DEFAULT (bodied) method
            Type ifaceRet; try { ifaceRet = imDef.TryGetProperty("ret", out var rt) ? MapType(SubstTv(DotKt.Bir.TypeNode.Read(rt), specArgs)) : typeof(void); } catch { return; }
            Type[] paramTypes; try { paramTypes = imDef.GetProperty("params").EnumerateArray().Select(p => MapType(SubstTv(DotKt.Bir.TypeNode.Read(p.GetProperty("type")), specArgs))).ToArray(); } catch { return; }
            var bridge = ti.TB.DefineMethod("<>dotkt_dimfwd$" + (_covarBridge++),
                MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
                ifaceRet, paramTypes);
            var il = bridge.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            for (int i = 0; i < paramTypes.Length; i++) il.Emit(OpCodes.Ldarg, i + 1);
            il.Emit(OpCodes.Callvirt, dim);   // dispatches to the DIM inherited by `this`
            il.Emit(OpCodes.Ret);
            var ifaceMethod = constructed != null ? TypeBuilder.GetMethod(constructed, ifaceBuilder) : (MethodInfo)ifaceBuilder;
            ti.TB.DefineMethodOverride(bridge, ifaceMethod);
            return;
        }
    }


    // A structured owner Fqn -> (open name, constructed .NET type or null for a non-generic). An emitted open type
    // (`_types`) is MakeGenericType'd; a referenced generic is arity-suffixed by reflection.
    (string open, Type constructed) ParseOwnerT(DotKt.Bir.TypeNode.Fqn f)
    {
        if (f.Args == null) return (f.Name, null);
        var args = f.Args.Select(a => { var r = MapType(a); return r == typeof(void) ? typeof(object) : r; }).ToArray();
        if (_types.TryGetValue(f.Name, out var ti)) return (f.Name, ti.TB.MakeGenericType(args));
        return (f.Name, ResolveType(f.Name + "`" + args.Length).MakeGenericType(args));
    }

    // An owner slot (structured Fqn or legacy string) -> (open name, constructed type).
    (string open, Type constructed) ParseOwnerSlot(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && DotKt.Bir.TypeNode.Read(e) is DotKt.Bir.TypeNode.Fqn f
            ? ParseOwnerT(f) : ParseOwner(e.GetString());

    (string open, Type constructed) ParseOwner(string spec)
    {
        var br = spec.IndexOf('[');
        if (br < 0) return (spec, null);
        var open = spec.Substring(0, br);
        var args = SplitTopLevel(spec.Substring(br + 1, spec.Length - br - 2)).Select(MapType).ToArray();
        if (_types.TryGetValue(open, out var ti)) return (open, ti.TB.MakeGenericType(args));
        // Owner not emitted in THIS assembly -> a REFERENCED generic type (e.g. `kotlin.Result[int]` from
        // DotKt.Stdlib.dll): construct it by reflection so ResolveMethod/ResolveField can reflect against the
        // instantiation (its members carry substituted signatures).
        return (open, ResolveType(open + "`" + args.Length).MakeGenericType(args));
    }

    // The constructed type's GetX helpers return members whose declared types are still the OPEN params (`!0`);
    // substitute a type-level param by position to its concrete arg so callers box value types correctly.
    // A value type OR a generic parameter must be boxed to become an `object` — a generic param's runtime type is
    // unknown (could be a value type), and `box !!0` is legal/correct for both value and reference instantiations.
    static bool NeedsBoxToRef(Type t) => t != null && (t.IsValueType || t.IsGenericParameter);

    // Array element STORE. ECMA-335 requires the SPECIALIZED opcode (stelem.i2/i4/…) for a BCL PRIMITIVE
    // element type; the generic token form `stelem <T>` is UNVERIFIABLE for primitives (ilverify:
    // `stelem <char>` -> [StackUnexpected][found Char]). Reference elements -> stelem.ref. A generic-param
    // (`!T`/`!!T`) OR a non-primitive struct element MUST keep the token form -- a generic-param's runtime
    // type is unknown (could be value), and specializing it would be wrong for a value instantiation.
    void EmitStelem(Type elem)
    {
        if (elem.IsGenericParameter) { _il.Emit(OpCodes.Stelem, elem); return; }
        if (!elem.IsValueType) { _il.Emit(OpCodes.Stelem_Ref); return; }
        if (elem == typeof(bool) || elem == typeof(sbyte) || elem == typeof(byte)) _il.Emit(OpCodes.Stelem_I1);
        else if (elem == typeof(char) || elem == typeof(short) || elem == typeof(ushort)) _il.Emit(OpCodes.Stelem_I2);
        else if (elem == typeof(int) || elem == typeof(uint)) _il.Emit(OpCodes.Stelem_I4);
        else if (elem == typeof(long) || elem == typeof(ulong)) _il.Emit(OpCodes.Stelem_I8);
        else if (elem == typeof(float)) _il.Emit(OpCodes.Stelem_R4);
        else if (elem == typeof(double)) _il.Emit(OpCodes.Stelem_R8);
        else if (elem == typeof(IntPtr) || elem == typeof(UIntPtr)) _il.Emit(OpCodes.Stelem_I);
        else _il.Emit(OpCodes.Stelem, elem); // user struct / enum / Nullable<> -> token form (verifiable)
    }

    // Array element LOAD -- specialized opcode for a BCL primitive, ldelem.ref for a reference, token form
    // (`ldelem <T>`) for a generic-param / non-primitive struct. Mirror of EmitStelem; sign-extends per type
    // (u1/u2 for unsigned+char+bool, i1/i2 for signed).
    void EmitLdelem(Type elem)
    {
        if (elem.IsGenericParameter) { _il.Emit(OpCodes.Ldelem, elem); return; }
        if (!elem.IsValueType) { _il.Emit(OpCodes.Ldelem_Ref); return; }
        if (elem == typeof(bool) || elem == typeof(byte)) _il.Emit(OpCodes.Ldelem_U1);
        else if (elem == typeof(sbyte)) _il.Emit(OpCodes.Ldelem_I1);
        else if (elem == typeof(char) || elem == typeof(ushort)) _il.Emit(OpCodes.Ldelem_U2);
        else if (elem == typeof(short)) _il.Emit(OpCodes.Ldelem_I2);
        else if (elem == typeof(int)) _il.Emit(OpCodes.Ldelem_I4);
        else if (elem == typeof(uint)) _il.Emit(OpCodes.Ldelem_U4);
        else if (elem == typeof(long) || elem == typeof(ulong)) _il.Emit(OpCodes.Ldelem_I8);
        else if (elem == typeof(float)) _il.Emit(OpCodes.Ldelem_R4);
        else if (elem == typeof(double)) _il.Emit(OpCodes.Ldelem_R8);
        else if (elem == typeof(IntPtr) || elem == typeof(UIntPtr)) _il.Emit(OpCodes.Ldelem_I);
        else _il.Emit(OpCodes.Ldelem, elem); // user struct / enum / Nullable<> -> token form (verifiable)
    }

    static Type Subst(Type t, Type[] typeArgs) =>
        t != null && t.IsGenericParameter && t.DeclaringMethod == null && t.GenericParameterPosition < typeArgs.Length
            ? typeArgs[t.GenericParameterPosition] : t;

    // Resolve a field for emit; out-param gives the substituted (concrete) field type for boxing decisions.
    FieldInfo ResolveField(string spec, string name, out Type fieldType)
    {
        var (open, constructed) = ParseOwner(spec);
        // A REFERENCED generic owner constructed from PURE reflection types (NOT a TypeBuilder instantiation): reflect
        // the field directly on the constructed instantiation — its GetField carries the substituted field type, so no
        // TypeBuilder.GetField re-anchoring is needed. Mirrors ResolveMethod's external-constructed branch. (Reaches a
        // referenced data class's public fields, e.g. `kotlin.Pair[..]`.first/.second from `partition`/`associate`.)
        if (constructed != null && !_types.ContainsKey(open) && !IsTbInstantiation(constructed))
        {
            var rf = FindReflectedField(constructed, name) ?? throw new NotSupportedException($"field {open}.{name} not found");
            fieldType = rf.FieldType;
            return rf;
        }
        var fb = FindField(open, name);
        if (constructed == null) { fieldType = fb.FieldType; return fb; }
        fieldType = Subst(fb.FieldType, constructed.GetGenericArguments());
        return TypeBuilder.GetField(constructed, fb);
    }

    // Resolve a field by name on an already-RESOLVED (referenced .NET / baked) type, walking its base-class chain
    // (reflection's GetField already includes inherited members). Pure CLR resolution; null if absent.
    static FieldInfo FindReflectedField(Type t, string name) =>
        t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

    // Resolve a method for emit; out-param gives the substituted (concrete) return type for boxing decisions.
    MethodInfo ResolveMethod(string spec, string name, out Type retType, string sig = null)
    {
        var (open, constructed) = ParseOwner(spec);
        // A REFERENCED generic owner constructed from PURE reflection types (NOT a TypeBuilder instantiation): resolve
        // the member directly on the constructed instantiation — its GetMethods carry the substituted signature, so no
        // TypeBuilder.GetMethod re-anchoring (below) is needed. A referenced-generic instantiated with an EMITTED
        // (TypeBuilder) arg stays on the TypeBuilder.GetMethod path below (reflection GetMethods throws on such a type).
        if (constructed != null && !_types.ContainsKey(open) && !IsTbInstantiation(constructed))
        {
            var argc = sig == null ? -1 : (sig.Length == 0 ? 0 : SplitTopLevel(sig).Count);
            // Prefer a SIG-DRIVEN pick: a referenced constructed-generic owner can carry same-name/same-arity overloads
            // that differ only in a PARAM's generic-type owner (SequenceScope<T>.yieldAll$dotkt_suspend over
            // Iterator<T> vs IEnumerable<T> vs Sequence<T>) — arity alone binds an arbitrary one -> BadImageFormat.
            // FindReflectedMethodBySig maps the declared-callee `sig` tokens (structurally for open `gp:T` args) to
            // disambiguate; fall back to the arity pick when no sig is carried (or it can't uniquely resolve).
            // A miss must be a LEGIBLE error (and lets callInstance's dynRet fallback catch it) — an unchecked
            // deref here was an opaque NRE.
            var rrm = FindReflectedMethodBySig(constructed, name, sig)
                ?? FindReflectedMethod(constructed, name, argc)
                ?? throw new NotSupportedException($"method {name} not found on referenced type {constructed}");
            retType = rrm.ReturnType;
            return rrm;
        }
        var mb = FindMethod(open, name, sig)
            ?? throw new NotSupportedException($"method {open}.{name}({sig}) not found (external owner did not resolve or lacks the member)");
        if (constructed == null) { retType = mb.ReturnType; return mb; }
        // The owner constructed with its OWN class type parameters (`RingBuffer<T>` referenced from inside
        // RingBuffer<T>) is the self instantiation. A call must reference the method on that self-instantiation
        // (`C<!0>::m`), NOT the open type def (`C`1::m`) — the open form is "not fully instantiated" at runtime (any
        // self method-call `this.b()` inside a generic class). EXCEPTION: a generic-METHOD self-call must keep the open
        // MethodBuilder, because TypeBuilder.GetMethod yields a MethodBuilderInstantiation that can't be
        // MakeGenericMethod'd (`this.toArray<object>()`); ApplyTypeArgs instantiates the open mb instead.
        if (IsSelfInstantiation(constructed))
        {
            retType = mb.ReturnType;
            if (mb.IsGenericMethodDefinition) return mb;
            // TypeBuilder.GetMethod requires `mb` declared on `constructed`'s OWN generic def. An INHERITED self-call
            // (mb on a generic base, `class D<T> : Base<T>` — `this.show()`) throws; anchor it onto the CONSTRUCTED
            // base instantiation (`Base<!T>`), not the open def (`Base``1::m` is "not fully instantiated").
            try { return TypeBuilder.GetMethod(constructed, mb); }
            catch (ArgumentException) { return AnchorInheritedOnBase(constructed, mb, out retType) ?? mb; }
        }
        retType = Subst(mb.ReturnType, constructed.GetGenericArguments());
        // An INHERITED method on a NON-self constructed generic (mb declared on a base/interface, not on `constructed`'s
        // own generic def — `D<int> : Base<int>`.get_x, or AbstractMutableCollection<E>'s inherited iterator()) throws the
        // same "method must be declared on the generic type definition" as the self case -> anchor onto the constructed
        // base instantiation (`Base<int>`); only fall back to the open MethodBuilder when no such base exists (interface).
        try { return TypeBuilder.GetMethod(constructed, mb); }
        catch (ArgumentException) { return AnchorInheritedOnBase(constructed, mb, out retType) ?? mb; }
    }

    // `mb` is INHERITED — declared on a generic BASE class, not on `constructed`'s own generic def. A call must
    // reference it on the constructed base instantiation (`class D<T> : Base<T>()` -> `Base<!T>::m`, or `D<int>` ->
    // `Base<int>::m`), NOT the open `Base<>` (a bare `Base``1::m` operand is "not fully instantiated" -> the JIT
    // raises InvalidProgram / "not fully instantiated"). Walk the constructed receiver's base-CLASS chain for the
    // instantiation whose generic def is mb's declaring type, then anchor via TypeBuilder.GetMethod. Returns null
    // when the declaring type is an INTERFACE (not on the class chain) — the caller keeps the open MethodBuilder.
    MethodInfo AnchorInheritedOnBase(Type constructed, MethodInfo mb, out Type retType)
    {
        retType = mb.ReturnType;
        var targetDef = mb.DeclaringType;
        for (var bt = constructed.BaseType; bt != null; bt = bt.BaseType)
            if (bt.IsGenericType && !bt.IsGenericTypeDefinition && ReferenceEquals(bt.GetGenericTypeDefinition(), targetDef))
            {
                try { var anchored = TypeBuilder.GetMethod(bt, mb); retType = Subst(mb.ReturnType, bt.GetGenericArguments()); return anchored; }
                catch (ArgumentException) { return null; }
            }
        return null;
    }

    // A property read/write on an EXTERNAL type must go through the public accessor (`get_`/`set_<name>`), NOT the
    // backing field: the CLR property model gives every Kotlin property a PRIVATE backing field, which is inaccessible
    // cross-assembly (a direct Ldfld/Stfld -> FieldAccessException at runtime, e.g. `kotlin.Pair`.first/.second read
    // via a destructuring `component1()`/`component2()` that kotc lowers to a field access). Returns the accessor
    // anchored on the (possibly constructed-generic) owner, or null when the owner is emitted in THIS assembly (its
    // backing field is directly accessible) or no such accessor exists (a public `@ClrField` -> keep the direct field).
    MethodInfo ExternalPropAccessor(string spec, string accessor)
    {
        var (open, constructed) = ParseOwner(spec);
        if (_types.ContainsKey(open)) return null;
        // Pure-reflection constructed generic: resolve directly on the instantiation (its accessor carries the
        // SUBSTITUTED return/param types, so no TypeBuilder re-anchoring is needed) — mirrors ResolveMethod's branch.
        if (constructed != null && !IsTbInstantiation(constructed))
            return FindReflectedMethod(constructed, accessor);
        var mb = FindMethod(open, accessor);
        if (mb == null) return null;
        if (constructed == null) return mb;
        try { return TypeBuilder.GetMethod(constructed, mb); }
        catch (ArgumentException) { return mb; }
    }

    // True when `constructed` is a generic type instantiated with exactly its own open definition's type parameters
    // (in order) — i.e. `C<T0,T1,…>` referenced from within C, which is the same as the open type in emitted IL.
    static bool IsSelfInstantiation(Type constructed)
    {
        if (!constructed.IsGenericType || constructed.IsGenericTypeDefinition) return false;
        if (constructed.GetGenericTypeDefinition() is not TypeBuilder def) return false;
        var args = constructed.GetGenericArguments();
        var pars = def.GetGenericArguments();
        if (args.Length != pars.Length) return false;
        for (int i = 0; i < args.Length; i++) if (!ReferenceEquals(args[i], pars[i])) return false;
        return true;
    }

    // A BCL constructed generic (List<T>, HashSet<T>, Dictionary<K,V>) whose type argument is an EMITTED type
    // (a TypeBuilderInstantiation) refuses reflection — `GetConstructor`/`GetMethod` throw "does not support
    // resolving members" (feedback item 12). Re-anchor the OPEN definition's member onto the constructed type via
    // the static TypeBuilder.GetX helpers, exactly like ResolveField/ResolveMethod do for emitted generics.
    static bool IsTbInstantiation(Type t) =>
        t.IsGenericType && !t.IsGenericTypeDefinition &&
        // The type's own open definition is a TypeBuilder (`Iterator<int>` while Iterator is being emitted), OR one of
        // its args transitively involves a TypeBuilder. The first clause is what `ContainsTypeBuilder` also needed: a
        // constructed-generic-of-a-TypeBuilder is itself a TypeBuilderInstantiation but is not `is TypeBuilder`.
        (t.GetGenericTypeDefinition() is TypeBuilder
         || t.GetGenericArguments().Any(a => a is TypeBuilder || a is GenericTypeParameterBuilder || (a.IsGenericType && IsTbInstantiation(a))));

    // True when `t` is (or transitively contains) a generic PARAMETER — a GenericTypeParameterBuilder of the enclosing
    // emitting context. Distinguishes a concrete owner instantiation (`Holder<int>`) from an erased-context one
    // (`Holder<E>` referenced from inside another generic). Recursive, NOT Type.ContainsGenericParameters (unreliable
    // on un-baked builder instantiations); GenericTypeParameterBuilder reports IsGenericParameter reliably.
    static bool ContainsGenericParam(Type t) =>
        t.IsGenericParameter || (t.IsGenericType && t.GetGenericArguments().Any(ContainsGenericParam));

    static ConstructorInfo GenericCtor(Type constructed, params Type[] argTypes) =>
        IsTbInstantiation(constructed)
            ? TypeBuilder.GetConstructor(constructed, constructed.GetGenericTypeDefinition().GetConstructor(argTypes))
            : constructed.GetConstructor(argTypes);

    static MethodInfo GenericMethod(Type constructed, string name) =>
        IsTbInstantiation(constructed)
            ? TypeBuilder.GetMethod(constructed, constructed.GetGenericTypeDefinition().GetMethod(name))
            : constructed.GetMethod(name);

    // An interface member inherited through the interface chain (`IList<T>.Add` -> `ICollection<T>.Add`). Interface
    // `GetMethods` excludes base-interface members, so search the open def's transitively-flattened base interfaces,
    // substitute the open def's type parameters with `typeArgs` into each base reference — which covers BOTH a
    // shared-arity chain (IList<T> : ICollection<T>) AND an ARITY-CHANGING constructed-arg chain
    // (IDictionary<K,V> : ICollection<KeyValuePair<K,V>>, where Count/Clear live 2->1 down the chain) — and
    // re-anchor the method onto the constructed base. Null if not found.
    static MethodInfo ResolveInheritedIfaceMethod(Type open, Type[] typeArgs, string name, int argc, BindingFlags flags)
    {
        foreach (var bi in open.GetInterfaces())
        {
            if (!bi.IsGenericType)
            {
                var nm = bi.GetMethods(flags).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == argc);
                if (nm != null) return nm;
                continue;
            }
            var biOpen = bi.GetGenericTypeDefinition();
            var bom = biOpen.GetMethods(flags).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == argc);
            if (bom == null) continue;
            var biCon = SubstituteIfaceArgs(bi, typeArgs);
            return IsTbInstantiation(biCon) ? TypeBuilder.GetMethod(biCon, bom)
                : biCon.GetMethods(flags).First(m => m.Name == name && m.GetParameters().Length == argc);
        }
        return null;
    }

    // Substitute an open interface's own generic parameters (positionally = `typeArgs`) throughout a base-interface
    // reference as declared on that open def — including CONSTRUCTED args (`ICollection<KeyValuePair<K,V>>` with
    // K:=string,V:=int -> `ICollection<KeyValuePair<string,int>>`). Every generic parameter appearing in such a
    // reference is declared by the open type, so GenericParameterPosition indexes `typeArgs` directly.
    static Type SubstituteIfaceArgs(Type t, Type[] typeArgs)
    {
        if (t.IsGenericParameter) return typeArgs[t.GenericParameterPosition];
        if (!t.IsGenericType) return t;
        var args = t.GetGenericArguments().Select(a => SubstituteIfaceArgs(a, typeArgs)).ToArray();
        return t.GetGenericTypeDefinition().MakeGenericType(args);
    }

    // Overload disambiguation for interface-slot wiring: does a body method's declared param type satisfy the
    // interface method's (substituted) param type? Reference/Type equality first; builders vs runtime
    // instantiations of the same shape compare by name (TypeBuilderInstantiation instances are not reference-equal
    // even for identical shapes). Deliberately shallow — the caller only disambiguates same-name OVERLOADS, whose
    // param lists differ at the top level (CompareTo(Ver) vs CompareTo(object)).
    static bool SlotParamMatches(Type body, Type iface) =>
        ReferenceEquals(body, iface) || body == iface
        || (body.Name == iface.Name && (body.Namespace ?? "") == (iface.Namespace ?? ""));

    // A STATIC method declared on a GENERIC emitted class (a Kotlin companion fun of a generic class —
    // `Result<T>`'s `fun <T> success(value: T)` emitted as a static generic method on `Result`1`) resolved via a
    // bare owner spec is an open MethodBuilder. Emitting a call with that open-typedef parent from ANOTHER class
    // is invalid IL (`call kotlin.Result`1::success<T>` -> InvalidProgramException at JIT: a member of a generic
    // type must be referenced through a constructed typespec). Anchor it onto a concrete instantiation — `object`
    // for each class param: a Kotlin companion member cannot reference the enclosing class's type parameters, so
    // every instantiation is signature-identical and `object` is canonical (Codex-confirmed: the documented
    // TypeBuilder.GetMethod owner-form; ApplyTypeArgs' concrete-owner branch then MakeGenericMethod's the anchored
    // method with the call's own type args). No-op for non-generic owners and non-builder methods.
    MethodInfo AnchorOpenGenericOwnerStatic(MethodInfo m)
    {
        if (m == null || !m.IsStatic) return m;
        // LOCAL emitted generic owner: anchor the open MethodBuilder onto the `object`-instantiation.
        if (m is MethodBuilder mb)
        {
            if (mb.DeclaringType is not TypeBuilder tb || !tb.IsGenericTypeDefinition) return m;
            var constructed = tb.MakeGenericType(tb.GetGenericArguments().Select(_ => (Type)typeof(object)).ToArray());
            var anchored = TypeBuilder.GetMethod(constructed, mb);
            // Keep the param-type record visible through the anchored identity (call-site boxing decisions).
            if (_mparams.TryGetValue(mb, out var ps)) _mparams[anchored] = ps;
            return anchored;
        }
        // EXTERNAL (referenced .NET / rt-stdlib) reflection static on a generic type DEFINITION — the SAME problem for
        // any cross-assembly call to a static on a generic type (`kotlin.Result`1::success`/`failure`, …): FindMethod
        // resolves the member on the open `C`1` typedef, and emitting a call scoped to that open typedef is an invalid
        // memberref (runtime `TypeLoadException: Could not load type 'C`1' from assembly '<app>'`). Anchor onto the
        // `object`-instantiation exactly like the local path — a Kotlin companion static cannot reference the enclosing
        // class's type params, so every instantiation is signature-identical and `object` is canonical (this mirrors
        // the stdlib's OWN emitted IL: `call C`1<object>::success<…>(…)`). Match the constructed owner's member by
        // (module, metadata token): a method on a constructed RuntimeType instantiation shares its definition's token.
        // ApplyTypeArgs then MakeGenericMethod's the anchored method with the call's own type args.
        if (m.DeclaringType is not { IsGenericTypeDefinition: true } odt) return m;
        var con = odt.MakeGenericType(odt.GetGenericArguments().Select(_ => (Type)typeof(object)).ToArray());
        return con.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                  .Single(x => x.Module == m.Module && x.MetadataToken == m.MetadataToken);
    }

    // A call to a generic method `fun <T> id(x:T)` carries `typeArgs` -> instantiate it (MakeGenericMethod).
    // `retType`/`paramTypes` give the SUBSTITUTED (concrete) signature, since the instantiation's own reflection
    // still reports `!!0` (and throws pre-bake) — needed so value args to `object`/concrete params get boxed.
    // Set by build-stdlib-ref.sh: while compiling the pure-kotlin stdlib, methods the backend can't yet emit are
    // stubbed (throw) instead of aborting the whole assembly — the "= TODO()" stdlib still emits and loads.
    static readonly bool StdlibStub = Environment.GetEnvironmentVariable("DOTKT_STDLIB_COMPILE") == "1";

    // Emit a body that just throws — stubs a method the backend can't yet emit during the stdlib build.
    void EmitThrowStub(MethodBuilder mb, string feature)
    {
        var il = mb.GetILGenerator();
        il.Emit(OpCodes.Ldstr, "DOTKT-STDLIB stub: " + feature + " not yet supported by the .NET backend");
        il.Emit(OpCodes.Newobj, typeof(NotSupportedException).GetConstructor(new[] { typeof(string) }));
        il.Emit(OpCodes.Throw);
    }

    MethodInfo ApplyTypeArgs(MethodInfo m, JsonElement e, out Type retType, out Type[] paramTypes)
    {
        // Defense: an unresolved call (FindMethod/FindStatic returned null — e.g. a bad owner the CIR should never carry)
        // must fail with a legible message naming the call, not a cryptic Dictionary ArgumentNullException(key) below.
        if (m == null)
        {
            var mn = e.TryGetProperty("method", out var mnEl) && mnEl.ValueKind == JsonValueKind.String ? mnEl.GetString() : "?";
            var on = e.TryGetProperty("owner", out var onEl) && onEl.ValueKind == JsonValueKind.String ? onEl.GetString() : null;
            throw new NotSupportedException($"unresolved method: {(on != null ? on + "." : "")}{mn}");
        }
        var ps = _mparams.TryGetValue(m, out var p) ? p : null;
        if (e.TryGetProperty("typeArgs", out var ta) && ta.GetArrayLength() > 0)
        {
            var targs = ta.EnumerateArray().Select(x => MapType(x)).ToArray();
            // Substitute by REFERENCE IDENTITY (the method's own gp builders -> the concrete type args), NOT by
            // reflecting `DeclaringMethod`/`GenericParameterPosition` — those are null/garbage on an un-baked
            // MethodBuilder, which silently dropped the substitution and boxed value args. Identity is reliable.
            var sub = new Dictionary<Type, Type>();
            if (m is MethodBuilder mbk && _methodTypeParams.TryGetValue(mbk, out var gps))
            {
                int k = 0;
                foreach (var gp in gps.Values) { if (k < targs.Length) sub[gp] = targs[k]; k++; }
            }
            // A generic method on a CONSTRUCTED-generic TypeBuilder owner (non-self, e.g. `ringBuffer<E>.toArray<T>()`)
            // is a MethodBuilderInstantiation whose MakeGenericMethod is unsupported. Instantiate the OPEN method's
            // generic params. Reflection.Emit has NO API for a generic method on a constructed-generic TypeBuilder
            // owner (TypeBuilder.GetMethod wants a generic-method-DEFINITION; MethodBuilderInstantiation can't
            // MakeGenericMethod). The owner here is constructed with enclosing type params (e.g. ringBuffer<E>.toArray
            // <T>() from inside another generic), which erases to the open owner in IL, so use the OPEN method's
            // instantiation directly — the same shape the self-instantiation path emits.
            if (m is not MethodBuilder && m.DeclaringType is { IsGenericType: true } dt && !dt.IsGenericTypeDefinition
                && dt.GetGenericTypeDefinition() is TypeBuilder openTb)
            {
                var nm = e.GetProperty("method").GetString();
                // Detect a generic MethodBuilder via _methodTypeParams (IsGenericMethodDefinition/GetGenericArguments
                // are unreliable on an un-baked MethodBuilder).
                var openMb = _types.Values.FirstOrDefault(t => ReferenceEquals(t.TB, openTb))?.Methods.Values
                    .OfType<MethodBuilder>().FirstOrDefault(b => b.Name == nm
                        && _methodTypeParams.TryGetValue(b, out var g) && g.Count == targs.Length);
                if (openMb != null && _methodTypeParams.TryGetValue(openMb, out var ogps))
                {
                    int k = 0;
                    foreach (var gp in ogps.Values) { if (k < targs.Length) sub[gp] = targs[k]; k++; }
                    // A CONCRETE owner instantiation (`Holder<int>.pairWith<string>` — a call site OUTSIDE any generic
                    // context, e.g. main): the open-method form below loses the container's `<int>` and throws "not
                    // fully instantiated" at runtime. Here the anchored MethodOnTypeBuilderInstantiation (m, produced
                    // by ResolveMethod's TypeBuilder.GetMethod) DOES support MakeGenericMethod (the documented
                    // TypeBuilder.GetMethod-then-MakeGenericMethod order; verified on .NET 10 persisted emit), carrying
                    // BOTH instantiations. Gated STRICTLY to owners with no generic-parameter args so the erased-context
                    // path below (the rt-stdlib self/enclosing-generic case) is untouched. KNOWN EDGE (Codex review,
                    // no failing sample): a MIXED owner (`Holder<int, U>` — concrete + enclosing-generic args) still
                    // takes the open path and would lose the concrete arg; if such CIR ever appears, route it here too.
                    if (!dt.GetGenericArguments().Any(ContainsGenericParam))
                    {
                        var cpars = openTb.GetGenericArguments();
                        var cargs = dt.GetGenericArguments();
                        for (int i = 0; i < cpars.Length && i < cargs.Length; i++) sub[cpars[i]] = cargs[i];
                        retType = sub.TryGetValue(openMb.ReturnType, out var cr) ? cr : m.ReturnType;
                        paramTypes = _mparams.TryGetValue(openMb, out var ops)
                            ? ops.Select(x => sub.TryGetValue(x, out var s) ? s : x).ToArray() : ps;
                        return m.MakeGenericMethod(targs);
                    }
                    // Owner constructed with enclosing GENERIC PARAMS (`ringBuffer<E>.toArray<T>()` from inside another
                    // generic) — erases to the open owner in IL, so the OPEN method's instantiation is the right shape.
                    retType = sub.TryGetValue(openMb.ReturnType, out var or) ? or : m.ReturnType;
                    paramTypes = ps;
                    return openMb.MakeGenericMethod(targs);
                }
            }
            retType = sub.TryGetValue(m.ReturnType, out var r) ? r : m.ReturnType;
            paramTypes = ps?.Select(x => sub.TryGetValue(x, out var s) ? s : x).ToArray();
            // A specialized NON-generic overload can still carry the generic call's typeArgs: Kotlin specializes
            // `maxOrNull`/`sum`/`min` for Double/Float as a non-generic `Iterable<Double>.maxOrNull(): Double?`, but the
            // call site keeps `typeArgs=[Double]` from the generic `<T>` form. MakeGenericMethod throws on a non-generic
            // method ("not a GenericMethodDefinition"). When the resolved REFERENCED method is not a generic definition,
            // FindMethod already picked the right specialization — use it as-is. (A MethodBuilder reports
            // IsGenericMethodDefinition unreliably pre-bake, so this guards only reflected referenced methods.)
            if (m is not MethodBuilder && !m.IsGenericMethodDefinition) { retType = m.ReturnType; paramTypes = ps; return m; }
            var inst = m.MakeGenericMethod(targs);
            // A pure-reflection generic method (an EXTERNAL rt-stdlib static, e.g. `Result`1<object>::success<T>`
            // anchored by AnchorOpenGenericOwnerStatic) carries no `_mparams`/`_methodTypeParams` record, so `sub` is
            // empty and `ps` is null — read the concrete signature straight off the instantiation instead, so the
            // return type and value-arg boxing decisions are correct. Gated to reflection instantiations whose owner is
            // NOT a TypeBuilder instantiation (those go through the branches above / can't be reflected pre-bake).
            if (ps == null && inst is not MethodBuilder && inst.DeclaringType is { IsGenericType: true } idt && !IsTbInstantiation(idt))
            {
                retType = inst.ReturnType;
                paramTypes = inst.GetParameters().Select(p => p.ParameterType).ToArray();
            }
            return inst;
        }
        retType = m.ReturnType;
        paramTypes = ps;
        return m;
    }

    // Emit call args, boxing each value arg passed to a reference/object param (param types known explicitly).
    // When `mb` is a REFERENCED (reflectable) method, backfill omitted trailing [Optional]/[DefaultParameterValue]
    // args exactly like EmitCallArgs — a GENERIC (typeArgs) cross-module call may omit defaulted trailing params
    // (the frontend jar strips default VALUES; kotc emits fewer args than the full sig, e.g. `windowed(3)` vs the
    // 4-param `windowed(list, size, step=1, partialWindows=false)`), and the CLR caller must supply them or the
    // stack is short -> InvalidProgram. In-assembly emitted methods (MethodBuilder / MethodBuilderInstantiation)
    // can't be reflected pre-bake and carry no default metadata, so GetParameters() there is skipped (try/catch).
    void EmitArgsTyped(JsonElement args, Type[] pt, MethodInfo mb = null)
    {
        int i = 0;
        foreach (var a in args.EnumerateArray()) { if (pt != null && i < pt.Length) EmitArg(a, pt[i]); else EmitExpr(a); i++; }
        // Backfill omitted trailing defaults. Drive off the resolved method's own ParameterInfo (NOT `pt`, which is
        // null for a generic METHOD on a NON-generic owner — `windowed<T>` on `_CollectionsKt` — where ApplyTypeArgs
        // leaves paramTypes null).
        if (mb == null) return;
        ParameterInfo[] ps;
        try { ps = mb.GetParameters(); } catch (NotSupportedException) { return; }  // un-baked builder: no defaults
        for (; i < ps.Length; i++) EmitDefaultArg(ps[i]);
    }

    // Emit `new T(..)` ctor args honoring the node's declared ctor param types (`argTypes`): a value/generic-param
    // arg flowing into an `object`/reference ctor param must be BOXED (`Result<T>..ctor(object)` receiving a bare
    // `!!T` was InvalidProgram at a value instantiation), exactly like EmitArgsTyped does for method calls.
    // Falls back to raw emission when the node carries no (or arity-mismatched) argTypes, or a type fails to map.
    void EmitNewArgs(JsonElement e, JsonElement nargs)
    {
        Type[] want = null;
        if (e.TryGetProperty("argTypes", out var at) && at.ValueKind == JsonValueKind.Array
            && at.GetArrayLength() == nargs.GetArrayLength())
            want = at.EnumerateArray().Select(x => { try { return MapType(x); } catch { return null; } }).ToArray();
        int i = 0;
        foreach (var a in nargs.EnumerateArray()) { if (want?[i] != null) EmitArg(a, want[i]); else EmitExpr(a); i++; }
    }

    // Prefer a BIR-carried concrete result type (`retType`) over reflecting an un-baked builder's `!0`/`!!0`.
    Type RetOr(JsonElement e, Type fallback)
    {
        if (!e.TryGetProperty("retType", out var r)) return fallback;
        var declared = MapType(r);
        // A generic method `<T> f(): T` instantiated with T = kotlin.Unit genuinely PUSHES a kotlin.Unit value, yet a
        // Unit/statement-context call site carries retType="void" (kotc lowers Unit results to void). Trusting that
        // "void" would skip the caller's pop, stranding the kotlin.Unit on the stack (ilverify ReturnVoid — e.g. a
        // discarded `blockOn { …Unit… }`). When the RESOLVED method's actual return (`fallback`, computed by
        // ApplyTypeArgs from the reified type args) is a real non-void type, keep it so the caller pops/uses it. A
        // genuinely void method reports fallback==void here, so this only rescues the generic-Unit-erasure mismatch.
        if (declared == typeof(void) && fallback != null && fallback != typeof(void)) return fallback;
        return declared;
    }

    // Boundary conversion after a call whose ACTUAL return is `System.Object` — the erased representation of a
    // generic `T?` (NullableGenericReturnErasure in bir2cir). The caller's statically-known type (`retType`) says
    // what to recover: a value-type nullable `Nullable<V>` via `unbox.any` (a null ref -> HasValue=false; a boxed V
    // -> HasValue=true), a reference type via `castclass` (null stays null). When the caller ALSO wants `object`
    // (an internal nullable->nullable hand-off) there is nothing to do. A non-object actual return is untouched.
    Type CoerceReturn(JsonElement e, Type actual)
    {
        if (actual == typeof(object) && e.TryGetProperty("retType", out var r))
        {
            var want = MapType(r);
            if (want != null && want != typeof(object))
            {
                if (want.IsValueType || want.IsGenericParameter) { _il.Emit(OpCodes.Unbox_Any, want); return want; }
                _il.Emit(OpCodes.Castclass, want); return want;
            }
        }
        return RetOr(e, actual);
    }


    // Resolve a method on a (possibly generic) interface. When the instantiation carries a TypeBuilder/generic
    // param arg (e.g. IComparable<!!0>), its own GetMethod throws on the persisted builder -> use the static helper.
    MethodInfo InterfaceMethodOn(Type iface, string name)
    {
        if (iface.IsGenericType && (IsTbInstantiation(iface) || iface.GetGenericArguments().Any(a => a.IsGenericParameter || a is TypeBuilder)))
            return TypeBuilder.GetMethod(iface, iface.GetGenericTypeDefinition().GetMethod(name));
        try { return iface.GetMethod(name); }
        catch (NotSupportedException) when (iface.IsGenericType)
        {
            return TypeBuilder.GetMethod(iface, iface.GetGenericTypeDefinition().GetMethod(name));
        }
    }

    // Load a managed pointer (&) to an addressable lvalue (for `constrained.` / struct-member calls). Falls back
    // to materializing the value into a temp and taking its address for arbitrary expressions.
    void EmitAddr(JsonElement e)
    {
        switch (e.GetProperty("k").GetString())
        {
            case "local":
            {
                var name = e.GetProperty("name").GetString();
                if (_locals.TryGetValue(name, out var l)) { _il.Emit(OpCodes.Ldloca, l); return; }
                if (_args.TryGetValue(name, out var a)) { _il.Emit(OpCodes.Ldarga, a); return; }
                break;
            }
            case "this":
                if (_inlineThis.Count > 0) { _il.Emit(OpCodes.Ldloc, _inlineThis.Peek()); return; }   // spliced extension receiver
                _il.Emit(OpCodes.Ldarg_0);
                return;
            case "field":
                EmitExpr(e.GetProperty("recv"));
                _il.Emit(OpCodes.Ldflda, ResolveField(e.GetProperty("ownerType").GetString(), e.GetProperty("name").GetString(), out _));
                return;
        }
        var t = EmitExpr(e);
        var tmp = _il.DeclareLocal(t);
        _il.Emit(OpCodes.Stloc, tmp);
        _il.Emit(OpCodes.Ldloca, tmp);
    }

    // Members may be declared on a base type (inherited / fake-overridden); walk the chain.
    FieldInfo FindField(string typeName, string name)
    {
        // A type NOT in this assembly's `_types` is EXTERNAL (a referenced .NET / rt-stdlib type) -> reflect the field
        // on the resolved type instead of indexing `_types` (which would KeyNotFound). Mirrors FindMethod's external
        // branch; reaches a referenced type's public/static fields (e.g. a data class field on the rt stdlib dll).
        if (!_types.ContainsKey(typeName))
        {
            Type ext = null;
            try { ext = ClrRef(typeName); } catch (NotSupportedException) { }
            if (ext == null && !typeName.Contains('`'))
                for (int arity = 1; arity <= 8; arity++)
                    if (TryResolveType(typeName + "`" + arity) is { } cand)
                    {
                        if (ext != null) { ext = null; break; }
                        ext = cand;
                    }
            return ext == null ? null : FindReflectedField(ext, name);
        }
        for (var ti = _types[typeName]; ti != null; ti = ti.BaseName != null && _types.ContainsKey(BareTypeKey(ti.BaseName)) ? _types[BareTypeKey(ti.BaseName)] : null)
            if (ti.Fields.TryGetValue(name, out var f)) return f;
        throw new NotSupportedException($"field {typeName}.{name} not found");
    }

    // A `base` token's `_types` key: bases are normally stored OPEN (bare name), but an INNER generic class's base
    // carries its instantiation args (`AbstractList$IteratorImpl[gp:E]`, the nested-generic encoding) — strip them
    // for the emitted-type lookup (the constructed form is only needed at SetParent).
    static string BareTypeKey(string n)
    {
        var b = n.IndexOf('[');
        return b < 0 ? n : n[..b];
    }

    // name + parameter-type signature -> the overload key. `m` is a method DEF; the param types are STRUCTURED Type
    // nodes rendered to the m3 legacy sig-token spelling (the ONE documented legacy-string exception) so a DEF-side key
    // matches a CALL-side `sig` comma-string (kotc renders the call sig with the same legacyToken grammar; both are
    // bir2cir-lowered to the same vocabulary, so `int` == `int` / `gp:T` == `gp:T`).
    static string SigKey(string name, JsonElement methodDef) =>
        name + "(" + string.Join(",", methodDef.GetProperty("params").EnumerateArray().Select(p => SigTokenOf(p.GetProperty("type")))) + ")";
    static string SigKey(string name, string sig) => name + "(" + sig + ")";

    // The m3 legacy sig-token spelling of a type SLOT (structured Type node -> token; a legacy string passes verbatim).
    // Mirrors kotc.bir.TypeNode.legacyToken (a type var collapses to `gp:T`), so def-side and call-side sigs agree.
    static string SigTokenOf(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? e.GetString()
        : e.ValueKind == JsonValueKind.Object ? SigTokenOf(DotKt.Bir.TypeNode.Read(e))
        : "object";
    static string SigTokenOf(DotKt.Bir.TypeNode t) => t switch
    {
        DotKt.Bir.TypeNode.Fqn f => f.Args == null ? f.Name : f.Name + "[" + string.Join(",", f.Args.Select(SigTokenOf)) + "]",
        DotKt.Bir.TypeNode.Tv => "gp:T",
        DotKt.Bir.TypeNode.Fn fn => (fn.Suspend ? "sfunc:" : "func:") + SigTokenOf(fn.Ret) + ":" + string.Join(",", fn.Params.Select(SigTokenOf)),
        DotKt.Bir.TypeNode.Nullable n => "nullable:" + SigTokenOf(n.Of),
        DotKt.Bir.TypeNode.Array a => "array:" + SigTokenOf(a.Elem),
        DotKt.Bir.TypeNode.ByRef b => "byref:" + SigTokenOf(b.Of),
        _ => "object",
    };

    // Substitute a type-scope type variable `Tv{type,i}` -> `args[i]` (the interface's instantiation), recursively.
    // Used to re-anchor an interface method's declared type (which names the INTERFACE's own params) to the
    // implementer's concrete args. A method-scope tv / an out-of-range index is left as-is.
    static DotKt.Bir.TypeNode SubstTv(DotKt.Bir.TypeNode t, DotKt.Bir.TypeNode[] args) => t switch
    {
        DotKt.Bir.TypeNode.Tv { Scope: "type" } tv when args != null && tv.I >= 0 && tv.I < args.Length => args[tv.I],
        DotKt.Bir.TypeNode.Fqn { Args: { } fa } f => new DotKt.Bir.TypeNode.Fqn(f.Name, fa.Select(a => SubstTv(a, args)).ToArray()),
        DotKt.Bir.TypeNode.Nullable n => new DotKt.Bir.TypeNode.Nullable(SubstTv(n.Of, args)),
        DotKt.Bir.TypeNode.Array a => new DotKt.Bir.TypeNode.Array(SubstTv(a.Elem, args)),
        DotKt.Bir.TypeNode.ByRef b => new DotKt.Bir.TypeNode.ByRef(SubstTv(b.Of, args)),
        DotKt.Bir.TypeNode.Fn fn => new DotKt.Bir.TypeNode.Fn(fn.Suspend, SubstTv(fn.Ret, args), fn.Params.Select(p => SubstTv(p, args)).ToArray(), fn.Recv == null ? null : SubstTv(fn.Recv, args)),
        _ => t,
    };

    // On an exact-sig MISS for a call that targets a GENERIC method: the call carries the INSTANTIATED arg types
    // (`array:object,object`) while the method is registered under its generic sig (`array:gp:T,gp:T`), so the exact
    // lookup fails and the name-only fallback returns the wrong (often primitive) overload. Prefer the UNIQUE generic
    // overload of that name — a non-generic overload would have matched exactly, so on a miss the generic one is the
    // intended target. Null if there are zero or several generic overloads (keep the existing fallback).
    MethodBuilder UniqueGenericOverload(TypeInfo ti, string name)
    {
        MethodBuilder cand = null;
        foreach (var kv in ti.MethodsBySig)
            if (kv.Key.StartsWith(name + "(", StringComparison.Ordinal) && _methodTypeParams.ContainsKey(kv.Value))
            {
                if (cand != null) return null;   // ambiguous: more than one generic overload
                cand = kv.Value;
            }
        return cand;
    }

    // Canonicalize the generic-parameter NAMES in a sig by their order of first appearance (`gp:E,gp:E` -> `gp:#0,gp:#0`;
    // `gp:K,gp:V` -> `gp:#0,gp:#1`). A method DEF names its OWN type parameter (`addAll<E>` -> `...[gp:E]`), but a CALL
    // from inside another generic names the same slot by the CALLER's parameter (`plus<T>` calling addAll -> `...[gp:T]`),
    // so the verbatim SigKey strings differ and MethodsBySig misses even though the overload is the intended one. The sig
    // SHAPE (which slot uses which type param, in which order) is identical across def and call — only the names differ —
    // so first-appearance ordinals make them agree, distinguishing `addAll(List,coll)` from `addAll(List,int,coll)`.
    static string NormalizeGpNames(string sig)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        return System.Text.RegularExpressions.Regex.Replace(sig, @"gp:([A-Za-z_][A-Za-z0-9_]*)", m =>
        {
            var nm = m.Groups[1].Value;
            if (!map.TryGetValue(nm, out var idx)) { idx = map.Count; map[nm] = idx; }
            return "gp:#" + idx;
        });
    }

    // The exact SigKey missed (a generic method whose sig mentions a `gp:` — def-side name != call-side name). Match by
    // the NAME-CANONICALIZED sig instead (first-appearance ordinals), returning the UNIQUE overload of `name` whose
    // normalized def-sig equals the normalized call-sig. Null on no/ambiguous match (keeps the existing fallbacks). Only
    // consulted after the exact lookup, so it never overrides a precise match — it recovers the cross-generic-rename case.
    MethodBuilder FindByNormalizedSig(TypeInfo ti, string name, string sig)
    {
        if (sig == null || !sig.Contains("gp:", StringComparison.Ordinal)) return null;
        var want = NormalizeGpNames(sig);
        var prefix = name + "(";
        MethodBuilder cand = null;
        foreach (var kv in ti.MethodsBySig)
        {
            if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal) || !kv.Key.EndsWith(")", StringComparison.Ordinal)) continue;
            var defSig = kv.Key.Substring(prefix.Length, kv.Key.Length - prefix.Length - 1);
            if (NormalizeGpNames(defSig) != want) continue;
            if (cand != null && !ReferenceEquals(cand, kv.Value)) return null;   // ambiguous
            cand = kv.Value;
        }
        return cand;
    }

    MethodInfo FindMethod(string typeName, string name, string sig = null)
    {
        var seenIfaces = new HashSet<string>();
        MethodBuilder FindInInterfaces(TypeInfo ti)
        {
            if (ti == null || !ti.Def.TryGetProperty("interfaces", out var ifs)) return null;
            foreach (var i in ifs.EnumerateArray())
            {
                var spec = i.GetString();
                if (spec.StartsWith("clr:") || spec.StartsWith("clrg:")) continue;
                // Best-effort probe: only the OPEN name matters here, but ParseOwner eagerly maps the `[args]`
                // (a `[gp:T]` of an inner generic class is unresolvable in an enclosing ctor context — skip the
                // interface rather than abort; the base-chain walk continues past it).
                string open;
                try { (open, _) = ParseOwner(spec); }
                catch (NotSupportedException) { continue; }
                if (!seenIfaces.Add(open) || !_types.TryGetValue(open, out var iti)) continue;
                if (sig != null && iti.MethodsBySig.TryGetValue(SigKey(name, sig), out var ms)) return ms;
                if (sig != null && FindByNormalizedSig(iti, name, sig) is { } insm) return insm;
                if (sig != null && UniqueGenericOverload(iti, name) is { } igm) return igm;
                if (iti.Methods.TryGetValue(name, out var m)) return m;
                var inherited = FindInInterfaces(iti);
                if (inherited != null) return inherited;
            }
            return null;
        }
        // A type NOT in this assembly's `_types` is EXTERNAL — an rt-internal helper (`ClrCollectionDefaultsKt`,
        // referenced from an APP that links the rt via --ref). Resolve it by reflection on the loaded assembly instead
        // of indexing `_types` (which would KeyNotFound). (indexOf/listIterator/etc. lower to such helper callStatics.)
        if (!_types.ContainsKey(typeName))
        {
            // The owner is not emitted in THIS assembly -> a referenced .NET type. Resolve it with the prefix-aware
            // resolver (`ClrRef` strips `clr:`/`clrg:`/etc.; a bare FQN falls to reflection), then look the member up
            // including the reflected base-class + interface chain.
            Type ext = null;
            try { ext = ClrRef(typeName); } catch (NotSupportedException) { }
            // A bare OPEN generic Kotlin interface name (`kotlin.collections.Iterator`/`Map`, arrived via ParseOwner
            // stripping the `[gp:T]` args off `Iterator[gp:T]`.hasNext / `Map[gp:K,gp:V]`.get) has no reflection type
            // under its arity-less name — reflection knows it only as `Iterator`1`/`Map`2`. ResolveMethod then re-anchors
            // the returned OPEN member onto the constructed instantiation (TypeBuilder.GetMethod). Probe the arity suffix
            // and take the UNIQUE resolvable open definition (ambiguous bare name -> give up, keep the arity/null path).
            if (ext == null && !typeName.Contains('`'))
                for (int arity = 1; arity <= 8; arity++)
                    if (TryResolveType(typeName + "`" + arity) is { } cand)
                    {
                        if (ext != null) { ext = null; break; }   // ambiguous bare generic name
                        ext = cand;
                    }
            if (ext == null) return null;
            // A referenced file-class can carry several overloads that share name AND arity but differ in PARAM TYPES —
            // e.g. the stdlib's String-face `StringsKt.substring(String,int,int)` vs its CharSequence-face
            // `substring(<>dotkt_CharSequence,int,int)`, or `trim(String)` vs `trim(<>dotkt_CharSequence)`. Arity alone
            // then picks arbitrarily (reflection order) -> the wrong body runs (a String passed where the CharSequence
            // interface is expected -> EntryPointNotFound). Resolve by the FULL `sig` first (the same signature-keyed
            // disambiguation the in-`_types` path does via MethodsBySig); fall back to the arity pick on any miss.
            var extArgc = sig == null ? -1 : (sig.Length == 0 ? 0 : SplitTopLevel(sig).Count);
            return FindReflectedMethodBySig(ext, name, sig) ?? FindReflectedMethod(ext, name, extArgc);
        }
        // Walk this type's own members, then its EMITTED base/interface chain. If the base is NOT emitted here (an
        // external .NET base, e.g. an emitted class extending a BCL type), fall through to a reflected lookup on the
        // resolved base type so inherited .NET members are still found.
        for (var ti = _types[typeName]; ti != null; ti = ti.BaseName != null && _types.ContainsKey(BareTypeKey(ti.BaseName)) ? _types[BareTypeKey(ti.BaseName)] : null)
        {
            if (sig != null && ti.MethodsBySig.TryGetValue(SigKey(name, sig), out var ms)) return ms;
            if (sig != null && FindByNormalizedSig(ti, name, sig) is { } nsm) return nsm;
            if (sig != null && UniqueGenericOverload(ti, name) is { } gm) return gm;
            if (ti.Methods.TryGetValue(name, out var m)) return m;
            var im = FindInInterfaces(ti);
            if (im != null) return im;
            // Base is an EXTERNAL (non-emitted) type -> inherited member must come from reflection on it. `ti.ClrBase`
            // is set when the base parsed to a `clr:`/`clrg:` type; otherwise resolve the base name on demand.
            if (ti.BaseName != null && !_types.ContainsKey(BareTypeKey(ti.BaseName)))
            {
                Type extBase = ti.ClrBase;
                if (extBase == null) { try { extBase = ClrRef(ti.BaseName); } catch (NotSupportedException) { } }
                if (extBase != null) { var rm = FindReflectedMethod(extBase, name); if (rm != null) return rm; }
            }
        }
        throw new NotSupportedException($"method {typeName}.{name} not found");
    }

    // Resolve a method by name on an already-RESOLVED (referenced .NET / baked) type, walking the standard CLR member
    // lookup: the type's own members + its base CLASS chain (reflection's `GetMethod` already includes inherited base
    // members for a class), and — because `GetMethod` on an INTERFACE type does NOT surface base-interface members —
    // the transitively-inherited interface chain too. Pure CLR resolution; no Kotlin/BCL name mapping. Null if absent.
    static MethodInfo FindReflectedMethod(Type t, string name, int argCount = -1)
    {
        var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        // Arity-disambiguated lookup FIRST when the caller knows the parameter count: a referenced file-class can carry
        // overloads of the same name (e.g. _CollectionsKt.first(List<T>) vs first(Iterable<T>, predicate)); the
        // unconstrained GetMethod(name) below throws Ambiguous and an arbitrary pick mis-counts the stack.
        MethodInfo ByArity(Type ty) =>
            argCount < 0 ? null : ty.GetMethods(bf).FirstOrDefault(mm => mm.Name == name && mm.GetParameters().Length == argCount);
        if (ByArity(t) is { } am) return am;
        try { var m = t.GetMethod(name, bf); if (m != null) return m; }
        catch (AmbiguousMatchException) { var m = t.GetMethods(bf).FirstOrDefault(mm => mm.Name == name); if (m != null) return m; }
        // Interface members are inherited but `GetMethod`/`GetMethods` on an interface only reports the interface's own
        // slots — search the (flattened) base interfaces. (`GetInterfaces` returns the full transitive set.)
        if (t.IsInterface)
            foreach (var bi in t.GetInterfaces())
            {
                if (ByArity(bi) is { } bam) return bam;
                try { var m = bi.GetMethod(name, bf); if (m != null) return m; }
                catch (AmbiguousMatchException) { var m = bi.GetMethods(bf).FirstOrDefault(mm => mm.Name == name); if (m != null) return m; }
            }
        return null;
    }


    // Whether the current FindReflectedMethodBySig owner is a CONSTRUCTED generic type — set per-lookup, read by the
    // `gp:` structural case (a `gp:T` token matches a concrete arg when the owner instantiation already bound it).
    bool _sigConstructedOwner;

    // STRUCTURAL match for a sig token MapType could not resolve (an open generic-parameter token from the declared
    // callee's sig, e.g. `gp:T` / `array:gp:T` / `clrg:Collection[gp:T]`, unbound at a cross-module call site):
    // the token's SHAPE must agree with the candidate parameter's (generic param / array-of / constructed-generic).
    // Concrete tokens never reach here (MapType resolves them), so this cannot loosen an exact-type comparison.
    bool SigTokenMatchesOpen(string tok, Type p)
    {
        if (tok.StartsWith("byref:", StringComparison.Ordinal))
            return p.IsByRef && SigTokenMatches(tok.Substring(6), p.GetElementType());
        if (tok.StartsWith("array:", StringComparison.Ordinal))
            return p.IsArray && SigTokenMatches(tok.Substring(6), p.GetElementType());
        if (tok.StartsWith("nullable:", StringComparison.Ordinal))
            return SigTokenMatches(tok.Substring(9), p.IsGenericType && p.GetGenericTypeDefinition() == typeof(Nullable<>) ? p.GetGenericArguments()[0] : p);
        if (tok.StartsWith("gp:", StringComparison.Ordinal)) return p.IsGenericParameter || _sigConstructedOwner;
        if (tok.StartsWith("clrg:", StringComparison.Ordinal))
        {
            // Match on the generic-type-DEFINITION owner, not just "is a constructed generic": several same-arity
            // overloads (SequenceScope.yieldAll over Iterator<T> / IEnumerable<T> / Sequence<T>) all satisfy
            // IsGenericType, so the loose test binds an arbitrary one. The token's arg (`gp:T`) stays open, but its
            // OWNER (`System.Collections.Generic.IEnumerable`) still distinguishes IEnumerable<T> from Iterator<T>.
            if (!p.IsGenericType) return false;
            var body = tok.Substring(5);
            var br = body.IndexOf('[');
            var openName = br < 0 ? body : body.Substring(0, br);
            var argToks = br < 0 ? new List<string>() : SplitTopLevel(body.Substring(br + 1, body.Length - br - 2)).ToList();
            var def = TryResolveType(openName + "`" + argToks.Count);
            // Owner unresolvable (a Kotlin-only alias not in any referenced .NET assembly, e.g. `clrg:Collection[..]`
            // as a bare name) -> keep the OLD loose shape match rather than falsely reject (strictly additive).
            if (def == null) return true;
            if (!ReferenceEquals(p.GetGenericTypeDefinition(), def)) return false;
            // Recurse into the constructed generic's TYPE-ARGUMENTS. An open token arg (`gp:T`) must line up with a
            // generic-parameter position, so `IEnumerable[gp:T]` selects the GENERIC overload `maxOrNull<T>(IEnumerable<T>)`
            // and rejects the Double-specialized `maxOrNull(IEnumerable<Double>)` (whose arg is the concrete Double). A
            // concrete sub-token likewise requires the candidate's actual type-argument to equal it (via SigTokenMatches).
            var actualArgs = p.GetGenericArguments();
            for (var i = 0; i < argToks.Count && i < actualArgs.Length; i++)
                if (!SigTokenMatches(argToks[i], actualArgs[i])) return false;
            return true;
        }
        if (tok.StartsWith("func:", StringComparison.Ordinal))
        {
            // `func:<ret>:<arg1>,<arg2>,...` -> Func<arg1,...,argN,ret> (or Action<...> when ret==void). Match the
            // return type AND each parameter type structurally so overloads that differ ONLY by the selector's return
            // type stay distinguishable — e.g. `sumOf`'s Int/Long/Double/UInt/ULong family, where the loose "any Func"
            // test used to collapse them all onto the first-reflected (Double) overload -> wrong body / 0 result.
            if (!p.IsGenericType) return false;
            var rest = tok.Substring(5);
            var colon = FuncRetEnd(rest);
            var retTok = rest.Substring(0, colon);
            var argsPart = colon < rest.Length ? rest.Substring(colon + 1) : "";
            var argToks = argsPart.Length == 0 ? new List<string>() : SplitTopLevel(argsPart).ToList();
            var gargs = p.GetGenericArguments();
            if (retTok == "void")
            {
                if (gargs.Length != argToks.Count) return true;   // shape mismatch (Func vs Action) -> keep loose accept
                for (var i = 0; i < argToks.Count; i++)
                    if (!SigTokenMatches(argToks[i], gargs[i])) return false;
                return true;
            }
            if (gargs.Length != argToks.Count + 1) return true;   // shape mismatch -> keep loose accept
            for (var i = 0; i < argToks.Count; i++)
                if (!SigTokenMatches(argToks[i], gargs[i])) return false;
            return SigTokenMatches(retTok, gargs[gargs.Length - 1]);
        }
        return false;
    }

    // Combined sig-token match: a token MapType can fully resolve here (no unbound `gp:`) must EQUAL the candidate's
    // type exactly; an unresolvable token falls to the structural open-token shape match. Used to recurse into a
    // constructed-generic type-argument or a func's return/param slot, so a concrete inner token (a func's Int-vs-Double
    // return, `IEnumerable[Double]` vs `[gp:T]`) is still discriminating instead of collapsing onto the loose shape.
    bool SigTokenMatches(string tok, Type p)
    {
        // A token mentioning an unbound `gp:` is inherently OPEN — compare by SHAPE. (MapType would either throw or,
        // for a bare `gp:T`, resolve it to a placeholder Type that never ReferenceEquals the candidate's actual generic
        // parameter, wrongly rejecting the right overload -> arity fallback picks the wrong one. e.g. yieldAll's
        // IEnumerable<T> overload lost to the first-reflected Iterator<T>.) Only a fully-concrete token uses MapType.
        if (!tok.Contains("gp:", StringComparison.Ordinal))
        {
            Type want; try { want = MapType(tok); } catch { want = null; }
            if (want != null) return want == p;
        }
        return SigTokenMatchesOpen(tok, p);
    }

    // Sig-aware overload pick on a REFERENCED file-class: several methods can share name+arity but differ in PARAM
    // TYPES (a String-face vs a `<>dotkt_CharSequence`-face stdlib extension). Map each `sig` token to its Type and
    // require an EXACT full match against a reflected overload's parameters; return the UNIQUE match, or null on a
    // miss/ambiguity so the caller falls back to the arity pick (never a regression — this only ADDS disambiguation).
    // A token that MapType can't resolve here (an unbound `gp:T`, a not-yet-emitted type) yields no match -> fall back.
    MethodInfo FindReflectedMethodBySig(Type ext, string name, string sig)
    {
        if (sig == null) return null;
        var toks = sig.Length == 0 ? new List<string>() : SplitTopLevel(sig).ToList();
        var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        // When `ext` is a CONSTRUCTED generic (SequenceScope<String>), its methods' params reflect the instantiation
        // (IEnumerable<String>, not IEnumerable<T>) — so a `gp:T` token, which is the caller's OWN open param bound by
        // that same instantiation, must match the concrete arg. When `ext` is OPEN/non-generic (the static _CollectionsKt),
        // a `gp:T` token discriminates the method-generic overload (`maxOrNull<T>(IEnumerable<T>)`) from a concrete
        // sibling (`maxOrNull(IEnumerable<Double>)`), so it must require a genuine generic-parameter arg.
        _sigConstructedOwner = ext.IsConstructedGenericType;
        MethodInfo match = null;
        foreach (var m in ext.GetMethods(bf))
        {
            if (m.Name != name) continue;
            var ps = m.GetParameters();
            if (ps.Length != toks.Count) continue;
            var ok = true;
            for (var i = 0; i < ps.Length; i++)
                // SigTokenMatches is the combined matcher: a fully-CONCRETE token (no `gp:`) requires an EXACT type
                // (so a String-face overload isn't confused with a CharSequence-face one), while ANY token mentioning
                // `gp:` is compared STRUCTURALLY — even when it happens to resolve here. That last point is essential:
                // a call from INSIDE a generic method (`fun <T> mx(c) = c.maxOrNull()`) carries `sig=IEnumerable[gp:T]`
                // where `gp:T` resolves to the CALLER's own T builder; an exact compare against the callee's OWN `T`
                // never matches, dropping to the arity fallback which arbitrarily picks a specialized sibling
                // (`maxOrNull(IEnumerable<Double>)`). The structural path selects the generic `maxOrNull<T>(IEnumerable<T>)`
                // in both the generic-caller and the non-generic-caller case. (Mirrors the in-`_types` MethodsBySig keys.)
                if (!SigTokenMatches(toks[i], ps[i].ParameterType)) { ok = false; break; }
            if (!ok) continue;
            if (match != null)
            {
                // Two methods matching the SAME sig token necessarily have identical parameter types (each was
                // checked against the same MapType(toks)). A genuine overload set can't collide here — a distinct
                // overload has a distinct sig. So a second exact match is a DUPLICATE method emission (the stdlib
                // expect/actual fileClass merge can emit a top-level fn twice, e.g. `_ArraysKt.sum(int[])` x2) — NOT
                // a real ambiguity. Keeping the first is correct (the bodies are identical); returning null here
                // would drop to the arity fallback and pick the wrong same-arity overload (sum(int[]) -> sum(sbyte[])).
                continue;
            }
            match = m;
        }
        return match;
    }

    // Throw IndexOutOfRangeException unless 0 <= index < len (unsigned compare catches negatives too).
    void EmitStackBounds(JsonElement e)
    {
        EmitExpr(e.GetProperty("index"));
        EmitExpr(e.GetProperty("len"));
        var ok = _il.DefineLabel();
        _il.Emit(OpCodes.Blt_Un, ok);
        _il.Emit(OpCodes.Ldstr, "StackBuffer index out of bounds");
        _il.Emit(OpCodes.Newobj, typeof(IndexOutOfRangeException).GetConstructor(new[] { typeof(string) }));
        _il.Emit(OpCodes.Throw);
        _il.MarkLabel(ok);
    }

    // Push the address `ptr + index * sizeof(elem)` (a byte* into the stack buffer).
    void EmitStackAddr(JsonElement e, Type elem)
    {
        EmitExpr(e.GetProperty("ptr"));
        EmitExpr(e.GetProperty("index"));
        _il.Emit(OpCodes.Sizeof, elem);
        _il.Emit(OpCodes.Mul);
        _il.Emit(OpCodes.Add);
    }

    MethodBuilder FindStatic(string name, string sig = null)
    {
        if (sig != null)
            foreach (var ti in _types.Values)
                if (ti.IsFileClass && ti.MethodsBySig.TryGetValue(SigKey(name, sig), out var ms)) return ms;
        // exact-sig miss on a generic method whose sig carries a `gp:` — the DEF names its own type param but the CALL
        // names it by the caller's (a top-level static called from inside a generic): match by the name-canonicalized sig.
        if (sig != null)
            foreach (var ti in _types.Values)
                if (ti.IsFileClass && FindByNormalizedSig(ti, name, sig) is { } nsm) return nsm;
        // exact-sig miss with a generic target -> the unique generic overload (its instantiated call sig won't match).
        if (sig != null)
            foreach (var ti in _types.Values)
                if (ti.IsFileClass && UniqueGenericOverload(ti, name) is { } gm) return gm;
        foreach (var ti in _types.Values)
            if (ti.IsFileClass && ti.Methods.TryGetValue(name, out var mb)) return mb;
        throw new NotSupportedException("static method not found: " + name);
    }

    // The `(object, IntPtr)` delegate constructor. For a delegate type instantiated with a user TypeBuilder
    // (e.g. `Func<int, Point>` where Point is still being emitted), plain reflection `GetConstructor` throws
    // ("generic instantiation does not support resolving members"); TypeBuilder.GetConstructor bridges it. This
    // unblocks delegates/refs whose signature mentions a user type (`::Ctor`, unbound `Class::method`, lambdas
    // returning a user class).
    static bool ContainsTypeBuilder(Type t)
    {
        if (t is TypeBuilder || t is GenericTypeParameterBuilder) return true;
        if (t.HasElementType) return ContainsTypeBuilder(t.GetElementType());
        if (t.IsGenericType)
        {
            // A CONSTRUCTED generic whose open definition is a TypeBuilder (e.g. `Iterator<int>` while Iterator is being
            // emitted) is a TypeBuilderInstantiation: resolving its members needs TypeBuilder.GetX, yet it is not itself
            // `is TypeBuilder`. Detect it via its definition (catches e.g. `Func<Iterator<int>>` through recursion).
            if (t.GetGenericTypeDefinition() is TypeBuilder) return true;
            foreach (var a in t.GetGenericArguments()) if (ContainsTypeBuilder(a)) return true;
        }
        return false;
    }
    static bool IsTypeBuilderBackedGeneric(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition() is TypeBuilder;

    ConstructorInfo DelegateCtor(Type ft)
    {
        var sig = new[] { typeof(object), typeof(IntPtr) };
        if (ft.IsGenericType && ft.GetGenericTypeDefinition() is TypeBuilder dtb && _syntheticDelegateCtors.TryGetValue(dtb, out var dctor))
            return TypeBuilder.GetConstructor(ft, dctor);
        return (ft.IsGenericType && (ContainsTypeBuilder(ft) || IsTypeBuilderBackedGeneric(ft)))
            ? TypeBuilder.GetConstructor(ft, ft.GetGenericTypeDefinition().GetConstructor(sig))
            : ft.GetConstructor(sig);
    }
    // The delegate's `Invoke` method, bridged via TypeBuilder.GetMethod for a TypeBuilder-involving instantiation.
    MethodInfo InvokeOf(Type ft)
    {
        if (ft.IsGenericType && ft.GetGenericTypeDefinition() is TypeBuilder dtb && _syntheticDelegateInvokes.TryGetValue(dtb, out var invoke))
            return TypeBuilder.GetMethod(ft, invoke);
        return (ft.IsGenericType && (ContainsTypeBuilder(ft) || IsTypeBuilderBackedGeneric(ft)))
            ? TypeBuilder.GetMethod(ft, ft.GetGenericTypeDefinition().GetMethod("Invoke"))
            : ft.GetMethod("Invoke");
    }
    // The RETURN .NET type from a `func:<ret>:<args>` string — carried by the BIR, so we never reflect the
    // ReturnType of a TypeBuilder-baked Invoke (which is unreliable on an un-baked generic instantiation).
    Type FuncRetType(string t)
    {
        var rest = t.Substring(5);
        var ret = rest.Substring(0, FuncRetEnd(rest));
        return ret == "void" ? typeof(void) : MapType(ret);
    }

    // The delegate's PARAMETER type specs from a `func:<ret>:<arg,arg,...>` funcType token (the `<ret>` may itself be a
    // bracketed/prefixed type whose own ':' is not the separator — split at the first depth-0 ':' after the ret prefix).
    // Empty for a nullary function type. Used to coerce delegateInvoke args to the Invoke param the JIT expects.
    List<string> FuncArgSpecs(string t)
    {
        var rest = t.Substring(5);
        var argsPart = rest.Substring(FuncRetEnd(rest) + 1);
        return argsPart.Length == 0 ? new List<string>() : SplitTopLevel(argsPart).ToList();
    }

    // The bare NAME a type slot carries (a bir2cir CLR shorthand `int`/`void`/… Fqn, or a legacy string token), for a
    // name-keyed opcode switch (const/conv). null for a non-Fqn structured node.
    static string SlotName(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? e.GetString()
        : e.ValueKind == JsonValueKind.Object && DotKt.Bir.TypeNode.Read(e) is DotKt.Bir.TypeNode.Fqn f ? f.Name
        : null;

    Type EmitConst(JsonElement e)
    {
        var t = SlotName(e.GetProperty("type"));
        var v = e.GetProperty("value");
        switch (t)
        {
            case "string":
                if (v.ValueKind == JsonValueKind.Null) { _il.Emit(OpCodes.Ldnull); return typeof(string); }
                _il.Emit(OpCodes.Ldstr, v.GetString()); return typeof(string);
            case "int": _il.Emit(OpCodes.Ldc_I4, v.GetInt32()); return typeof(int);
            case "long": _il.Emit(OpCodes.Ldc_I8, v.GetInt64()); return typeof(long);
            // Unsigned consts carry the SIGNED bit-pattern (e.g. 4000000000u stored as -294967296); the same
            // ldc opcode loads the right bits, only the stack TYPE differs (so add/print are unsigned).
            case "uint": _il.Emit(OpCodes.Ldc_I4, v.GetInt32()); return typeof(uint);
            case "ulong": _il.Emit(OpCodes.Ldc_I8, v.GetInt64()); return typeof(ulong);
            case "ubyte": _il.Emit(OpCodes.Ldc_I4, v.GetInt32()); return typeof(byte);
            case "ushort": _il.Emit(OpCodes.Ldc_I4, v.GetInt32()); return typeof(ushort);
            // Signed Byte/Short (Kotlin Byte = sbyte, Short = Int16). Without these a `const byte`/`const short`
            // fell to default -> Ldnull -> InvalidProgramException when passed to a byte/short parameter.
            case "byte": _il.Emit(OpCodes.Ldc_I4, v.GetInt32()); return typeof(sbyte);
            case "short": _il.Emit(OpCodes.Ldc_I4, v.GetInt32()); return typeof(short);
            // NaN / ±Infinity are emitted as a JSON STRING (not a number token, which JSON forbids) — parse them back.
            case "double": _il.Emit(OpCodes.Ldc_R8, v.ValueKind == JsonValueKind.String ? double.Parse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture) : v.GetDouble()); return typeof(double);
            case "float": _il.Emit(OpCodes.Ldc_R4, v.ValueKind == JsonValueKind.String ? float.Parse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture) : v.GetSingle()); return typeof(float);
            case "bool": _il.Emit(v.GetBoolean() ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); return typeof(bool);
            case "char": _il.Emit(OpCodes.Ldc_I4, (int)v.GetString()[0]); return typeof(char);
            default: _il.Emit(OpCodes.Ldnull); return typeof(object);
        }
    }

    // Push a .NET CONSTANT (literal field) value, inlined — mirrors how C# emits a `const` read. `ft` is the field's
    // declared type (its underlying type if it's an enum). Returns `ft` (the stack type).
    Type EmitLiteralValue(object cv, Type ft)
    {
        var ut = ft.IsEnum ? Enum.GetUnderlyingType(ft) : ft;
        if (cv == null) { _il.Emit(OpCodes.Ldnull); return ft; }
        if (ut == typeof(string)) { _il.Emit(OpCodes.Ldstr, (string)cv); return ft; }
        if (ut == typeof(bool)) { _il.Emit((bool)cv ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); return ft; }
        if (ut == typeof(float)) { _il.Emit(OpCodes.Ldc_R4, Convert.ToSingle(cv)); return ft; }
        if (ut == typeof(double)) { _il.Emit(OpCodes.Ldc_R8, Convert.ToDouble(cv)); return ft; }
        if (ut == typeof(long) || ut == typeof(ulong)) { _il.Emit(OpCodes.Ldc_I8, unchecked((long)Convert.ToUInt64(cv))); return ft; }
        // char and every <=32-bit integer load via ldc.i4 (the bit pattern).
        if (ut == typeof(char)) { _il.Emit(OpCodes.Ldc_I4, (int)(char)cv); return ft; }
        if (ut == typeof(uint)) { _il.Emit(OpCodes.Ldc_I4, unchecked((int)Convert.ToUInt32(cv))); return ft; }
        _il.Emit(OpCodes.Ldc_I4, Convert.ToInt32(cv)); return ft;   // sbyte/byte/short/ushort/int
    }

    static int NumRank(Type t) =>
        t == typeof(double) ? 5 : t == typeof(float) ? 4 :
        (t == typeof(long) || t == typeof(ulong)) ? 3 :
        (t == typeof(int) || t == typeof(uint)) ? 2 :
        (t == typeof(short) || t == typeof(ushort) || t == typeof(char)) ? 1 :
        (t == typeof(byte) || t == typeof(sbyte)) ? 0 : -1;

    // The common numeric type of two operands (the wider), or null if no coercion is needed / they're not numeric.
    static Type NumericCommon(Type a, Type b)
    {
        if (a == b) return null;
        int ra = NumRank(a), rb = NumRank(b);
        if (ra < 0 || rb < 0) return null;
        return ra >= rb ? a : b;
    }

    void ConvTo(Type t)
    {
        if (t == typeof(double)) _il.Emit(OpCodes.Conv_R8);
        else if (t == typeof(float)) _il.Emit(OpCodes.Conv_R4);
        else if (t == typeof(long)) _il.Emit(OpCodes.Conv_I8);
        else if (t == typeof(ulong)) _il.Emit(OpCodes.Conv_U8);
        else if (t == typeof(int)) _il.Emit(OpCodes.Conv_I4);
        else if (t == typeof(uint)) _il.Emit(OpCodes.Conv_U4);
    }

    Type EmitBin(JsonElement e)
    {
        var op = e.GetProperty("op").GetString();
        var lt = EmitExpr(e.GetProperty("l"));
        var rt = EmitExpr(e.GetProperty("r"));
        // Mixed numeric operands (e.g. `Double / Int`, `Int + Long`) -> coerce both to the wider type. Shifts keep
        // their int shift-amount operand, so they're excluded.
        if (op != "<<" && op != ">>" && op != ">>>")
        {
            var common = NumericCommon(lt, rt);
            if (common != null)
            {
                if (rt != common) ConvTo(common);                       // coerce r (top of stack)
                if (lt != common)                                       // coerce l (below r): stash r, conv l, restore
                {
                    var tmp = _il.DeclareLocal(common);
                    _il.Emit(OpCodes.Stloc, tmp); ConvTo(common); _il.Emit(OpCodes.Ldloc, tmp);
                }
                lt = common;
            }
        }
        // Unsigned operands (Kotlin UInt/ULong -> .NET uint/ulong) need the UNSIGNED CIL ops for division and
        // remainder (a direct `bin` on the raw unsigned operand). Reads the CIR operand type only -- no Kotlin
        // knowledge. Without this, `a / b` on UInt >= 2^31 is silently wrong (signed Div on the bit pattern).
        // NOTE: ordered compares are NOT here -- Kotlin lowers `a > b` on UInt to `a.compareTo(b) > 0`, where
        // compareTo does the UNSIGNED compare and the outer `> 0` is a plain signed int compare. (`byte`/`ushort`
        // arithmetic promotes to UInt, so only uint/ulong reach a direct unsigned div here.)
        bool isUns = lt == typeof(uint) || lt == typeof(ulong);
        // Float/double `<=`/`>=` need the UNORDERED-inverted compare (C#'s shape): `a <= b` == !(a > b treating
        // unordered as TRUE) -> `cgt.un; ldc.i4.0; ceq` (resp. `>=` -> `clt.un; ...`). The plain signed cgt/clt
        // inversion returns TRUE for a NaN operand (`NaN <= 1.0` was True) because cgt/clt yield 0 on unordered
        // and the inversion flips it. `<`/`>` stay ordered clt/cgt (0 on unordered = correct false), and integer
        // paths keep the signed opcodes (unsigned compares never reach a direct bin — see the note above).
        bool isFloat = lt == typeof(float) || lt == typeof(double);
        switch (op)
        {
            case "+": _il.Emit(OpCodes.Add); return lt;
            case "-": _il.Emit(OpCodes.Sub); return lt;
            case "*": _il.Emit(OpCodes.Mul); return lt;
            // Signed integer `/` and `%` by -1 overflow the raw CIL `div`/`rem` opcode at MinValue (the CLR throws
            // OverflowException on `MinValue / -1`), but Kotlin's integer division WRAPS: `MIN / -1 == MIN`, `x % -1 == 0`.
            // Guard the divisor==-1 case with identities that also cover MinValue: `x / -1 == -x` (CIL `neg` wraps
            // MinValue, no overflow) and `x % -1 == 0` for every x. Unsigned/float never overflow here — raw opcode.
            case "/":
                if (!isUns && (lt == typeof(int) || lt == typeof(long))) { EmitDivRemGuarded(isRem: false, lt); return lt; }
                _il.Emit(isUns ? OpCodes.Div_Un : OpCodes.Div); return lt;
            case "%":
                if (!isUns && (lt == typeof(int) || lt == typeof(long))) { EmitDivRemGuarded(isRem: true, lt); return lt; }
                _il.Emit(isUns ? OpCodes.Rem_Un : OpCodes.Rem); return lt;
            case "&": _il.Emit(OpCodes.And); return lt;
            case "|": _il.Emit(OpCodes.Or); return lt;
            case "^": _il.Emit(OpCodes.Xor); return lt;
            case "<<": _il.Emit(OpCodes.Shl); return lt;
            case ">>": _il.Emit(OpCodes.Shr); return lt;
            case ">>>": _il.Emit(OpCodes.Shr_Un); return lt;
            case "==": _il.Emit(OpCodes.Ceq); return typeof(bool);
            case "!=": _il.Emit(OpCodes.Ceq); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Ceq); return typeof(bool);
            case "<": _il.Emit(OpCodes.Clt); return typeof(bool);
            case ">": _il.Emit(OpCodes.Cgt); return typeof(bool);
            case "<=": _il.Emit(isFloat ? OpCodes.Cgt_Un : OpCodes.Cgt); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Ceq); return typeof(bool);
            case ">=": _il.Emit(isFloat ? OpCodes.Clt_Un : OpCodes.Clt); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Ceq); return typeof(bool);
            default: throw new NotSupportedException("bin " + op);
        }
    }

    // Emit signed integer `/`/`%` with the divisor==-1 guard (stack on entry: [dividend, divisor]; leaves [result]).
    // Kotlin's integer division wraps at MinValue; the raw CIL `div`/`rem` throws OverflowException on `MinValue / -1`.
    // Since `x / -1 == -x` (CIL `neg` wraps MinValue) and `x % -1 == 0` for all x, we branch on divisor==-1 and use the
    // wrapping identity, dodging the overflow entirely without a MinValue comparison. `t` is int or long.
    void EmitDivRemGuarded(bool isRem, Type t)
    {
        var divisor = _il.DeclareLocal(t);
        _il.Emit(OpCodes.Stloc, divisor);       // stack: [dividend]
        var normal = _il.DefineLabel();
        var done = _il.DefineLabel();
        _il.Emit(OpCodes.Ldloc, divisor);
        _il.Emit(OpCodes.Ldc_I4_M1);
        if (t == typeof(long)) _il.Emit(OpCodes.Conv_I8);
        _il.Emit(OpCodes.Bne_Un, normal);       // divisor != -1 -> normal path (stack: [dividend])
        // divisor == -1: result is -dividend (div) or 0 (rem)
        if (isRem) { _il.Emit(OpCodes.Pop); _il.Emit(OpCodes.Ldc_I4_0); if (t == typeof(long)) _il.Emit(OpCodes.Conv_I8); }
        else _il.Emit(OpCodes.Neg);
        _il.Emit(OpCodes.Br, done);
        _il.MarkLabel(normal);                  // stack: [dividend]
        _il.Emit(OpCodes.Ldloc, divisor);
        _il.Emit(isRem ? OpCodes.Rem : OpCodes.Div);
        _il.MarkLabel(done);
    }

    Type EmitUn(JsonElement e)
    {
        var op = e.GetProperty("op").GetString();
        var t = EmitExpr(e.GetProperty("e"));
        switch (op)
        {
            case "-": _il.Emit(OpCodes.Neg); return t;
            case "+": return t;
            case "~": _il.Emit(OpCodes.Not); return t;
            case "!": _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Ceq); return typeof(bool);
            default: throw new NotSupportedException("un " + op);
        }
    }

    // A CIR `conv` instruction -> the matching CIL conv opcode; returns the target CLR type. ilemit only selects the
    // opcode for the requested target width — WHERE a Kotlin numeric conversion becomes a `conv` node is bir2cir's call.
    Type EmitConv(JsonElement e)
    {
        EmitExpr(e.GetProperty("e"));
        switch (SlotName(e.GetProperty("to")))
        {
            case "int": _il.Emit(OpCodes.Conv_I4); return typeof(int);
            case "long": _il.Emit(OpCodes.Conv_I8); return typeof(long);
            case "double": _il.Emit(OpCodes.Conv_R8); return typeof(double);
            case "float": _il.Emit(OpCodes.Conv_R4); return typeof(float);
            case "short": _il.Emit(OpCodes.Conv_I2); return typeof(short);
            case "byte": _il.Emit(OpCodes.Conv_I1); return typeof(sbyte);
            case "char": _il.Emit(OpCodes.Conv_U2); return typeof(char);
            default: throw new NotSupportedException("conv " + SlotName(e.GetProperty("to")));
        }
    }

    Type EmitNativeClrSafeCastValue(JsonElement e)
    {
        // `x as? T` for value T -> `T?`: isinst boxed-T, then unbox+wrap, else empty Nullable<T>.
        var elem = NativeType(e.GetProperty("elem"));
        var nt = typeof(Nullable<>).MakeGenericType(elem);
        var res = _il.DeclareLocal(nt);
        var has = _il.DefineLabel();
        var done = _il.DefineLabel();
        EmitExpr(e.GetProperty("e"));
        _il.Emit(OpCodes.Isinst, elem);
        _il.Emit(OpCodes.Dup);
        _il.Emit(OpCodes.Brtrue, has);
        _il.Emit(OpCodes.Pop);
        _il.Emit(OpCodes.Ldloca, res);
        _il.Emit(OpCodes.Initobj, nt);
        _il.Emit(OpCodes.Ldloc, res);
        _il.Emit(OpCodes.Br, done);
        _il.MarkLabel(has);
        _il.Emit(OpCodes.Unbox_Any, elem);
        _il.Emit(OpCodes.Newobj, nt.GetConstructor(new[] { elem }));
        _il.MarkLabel(done);
        return nt;
    }

    Type EmitNativeClrNullableNull(JsonElement e)
    {
        // `null` typed as Int? -> a Nullable<T> with HasValue=false. NOT ldnull: a value type has no null reference.
        var elem = NativeType(e.GetProperty("elem"));
        var nt = typeof(Nullable<>).MakeGenericType(elem);
        var loc = _il.DeclareLocal(nt);
        _il.Emit(OpCodes.Ldloca, loc);
        _il.Emit(OpCodes.Initobj, nt);
        _il.Emit(OpCodes.Ldloc, loc);
        return nt;
    }

    Type EmitNativeClrNullableWrap(JsonElement e)
    {
        var elem = NativeType(e.GetProperty("elem"));
        var nt = typeof(Nullable<>).MakeGenericType(elem);
        EmitExpr(e.GetProperty("e"));
        _il.Emit(OpCodes.Newobj, nt.GetConstructor(new[] { elem }));
        return nt;
    }

    Type EmitNativeClrNullableHasValue(JsonElement e)
    {
        var elem = NativeType(e.GetProperty("elem"));
        var nt = typeof(Nullable<>).MakeGenericType(elem);
        EmitExpr(e.GetProperty("e"));
        var loc = _il.DeclareLocal(nt);
        _il.Emit(OpCodes.Stloc, loc);
        _il.Emit(OpCodes.Ldloca, loc);
        _il.Emit(OpCodes.Call, nt.GetProperty("HasValue").GetGetMethod());
        return typeof(bool);
    }

    Type EmitNativeClrNullableValue(JsonElement e)
    {
        var elem = NativeType(e.GetProperty("elem"));
        var nt = typeof(Nullable<>).MakeGenericType(elem);
        EmitExpr(e.GetProperty("e"));
        var loc = _il.DeclareLocal(nt);
        _il.Emit(OpCodes.Stloc, loc);
        _il.Emit(OpCodes.Ldloca, loc);
        _il.Emit(OpCodes.Call, nt.GetProperty("Value").GetGetMethod());
        return elem;
    }

    Type EmitNativeClrTypeOf(JsonElement e)
    {
        var t = NativeType(e.GetProperty("type"));
        _il.Emit(OpCodes.Ldtoken, t);
        _il.Emit(OpCodes.Call, typeof(Type).GetMethod("GetTypeFromHandle"));
        return typeof(Type);
    }

    Type EmitNativeClrGetType(JsonElement e)
    {
        var got = EmitExpr(e.GetProperty("e"));
        if (got != null && NeedsBoxToRef(got)) _il.Emit(OpCodes.Box, got);
        _il.Emit(OpCodes.Callvirt, typeof(object).GetMethod("GetType"));
        return typeof(Type);
    }

    Type EmitNativeClrEnumValue(JsonElement e)
    {
        _il.Emit(OpCodes.Ldc_I4, e.GetProperty("ordinal").GetInt32());
        return NativeType(e.GetProperty("type"));
    }

    Type EmitNativeClrEnumOrdinal(JsonElement e)
    {
        EmitExpr(e.GetProperty("e"));
        _il.Emit(OpCodes.Conv_I4);
        return typeof(int);
    }

    Type EmitNativeClrEnumValues(JsonElement e)
    {
        var et = NativeType(e.GetProperty("type"));
        _il.Emit(OpCodes.Ldtoken, et);
        _il.Emit(OpCodes.Call, typeof(Type).GetMethod("GetTypeFromHandle"));
        _il.Emit(OpCodes.Call, typeof(Enum).GetMethod("GetValues", new[] { typeof(Type) }));
        _il.Emit(OpCodes.Castclass, et.MakeArrayType());
        return et.MakeArrayType();
    }

    Type EmitNativeClrEnumParse(JsonElement e)
    {
        var et = NativeType(e.GetProperty("type"));
        _il.Emit(OpCodes.Ldtoken, et);
        _il.Emit(OpCodes.Call, typeof(Type).GetMethod("GetTypeFromHandle"));
        EmitExpr(e.GetProperty("arg"));
        _il.Emit(OpCodes.Call, typeof(Enum).GetMethod("Parse", new[] { typeof(Type), typeof(string) }));
        _il.Emit(OpCodes.Unbox_Any, et);
        return et;
    }

    // Array literal (`intArrayOf(...)` / `arrayOf(...)`) -> newarr + per-element stelem.
    Type EmitNewArray(JsonElement e)
    {
        var elem = MapType(e.GetProperty("elem"));
        var elems = e.GetProperty("elems").EnumerateArray().ToList();
        _il.Emit(OpCodes.Ldc_I4, elems.Count);
        _il.Emit(OpCodes.Newarr, elem);
        for (int i = 0; i < elems.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            EmitArrayElemCoerced(elems[i], elem);
            EmitStelem(elem);
        }
        return elem.MakeArrayType();
    }

    // Coerce an array-element value to the array's element type before `stelem` (C2: `Array<Int?>` = `Nullable<int>[]`).
    // `arrayOf(1, null, 3)` / `arr[i] = 5` push a BARE `int` (or a null literal) into a `Nullable<int>` slot — without
    // the `T -> Nullable<T>` wrap (or `default(Nullable<T>)` for null) `stelem Nullable<int>` stores raw int bits as a
    // Nullable struct -> memory corruption / SIGSEGV. ONLY a genuine `Nullable<>` element takes the wrap path (the
    // EmitNullableCoerced `T -> Nullable<T>` / null-default); every other element keeps the pre-existing box-only
    // behavior (a value into a reference element `Array<Any?>` / `object[]`), so a `gp:T`-element array
    // (AbstractCollection.toArray's `newarr !T`) is UNTOUCHED — routing it through the broad EmitNullableCoerced would
    // spuriously unbox.any an object element into the `gp:T` slot (regressed the collection/map stdlib emit).
    void EmitArrayElemCoerced(JsonElement value, Type elem)
    {
        if (elem.IsGenericType && elem.GetGenericTypeDefinition() == typeof(Nullable<>)) { EmitNullableCoerced(value, elem); return; }
        var et = EmitExpr(value);
        if (et != null && NeedsBoxToRef(et) && !elem.IsValueType && !elem.IsGenericParameter) _il.Emit(OpCodes.Box, et);
    }

    Type EmitConcat(JsonElement e)
    {
        var parts = e.GetProperty("parts").EnumerateArray().ToList();
        _il.Emit(OpCodes.Ldc_I4, parts.Count);
        _il.Emit(OpCodes.Newarr, typeof(object));
        for (int i = 0; i < parts.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            var t = EmitExpr(parts[i]);
            if (NeedsBoxToRef(t)) _il.Emit(OpCodes.Box, t);
            _il.Emit(OpCodes.Stelem_Ref);
        }
        _il.Emit(OpCodes.Call, typeof(string).GetMethod("Concat", new[] { typeof(object[]) }));
        return typeof(string);
    }

    // Emit an expression, coercing a bare `T` (or a null literal) to `Nullable<T>` when `want` is a Nullable<T>.
    // Shared by EmitArg and EmitCond so value-type `T?` flows correctly through args and if/when branches.
    Type EmitNullableCoerced(JsonElement node, Type want)
    {
        bool wantNullable = want != null && want.IsGenericType && want.GetGenericTypeDefinition() == typeof(Nullable<>);
        if (wantNullable && node.TryGetProperty("k", out var k) && k.GetString() is "const" or "clr.const"
            && node.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Null)
        {
            var loc = _il.DeclareLocal(want);
            _il.Emit(OpCodes.Ldloca, loc); _il.Emit(OpCodes.Initobj, want); _il.Emit(OpCodes.Ldloc, loc);
            return want;
        }
        var got = EmitExpr(node);
        if (wantNullable && got != null && want.GetGenericArguments()[0] == got)
        {
            _il.Emit(OpCodes.Newobj, want.GetConstructor(new[] { got }));
            return want;
        }
        // A value-type / generic-param branch flowing into an `object` want (an erased generic `T?` return whose
        // branch type-tag was retyped to object by bir2cir's NullableGenericReturnErasure) must box; a `null` branch
        // already left a real null ref (EmitExpr(null-const) is a reference), so it is unaffected.
        if (want == typeof(object) && got != null && NeedsBoxToRef(got)) { _il.Emit(OpCodes.Box, got); return want; }
        return got;
    }

    // A cond/when BRANCH coerced to the result type `want`: EmitNullableCoerced (T -> Nullable<T> / null-default /
    // box-to-object) PLUS the REVERSE (C2) — a REFERENCE branch (`object`, the erased nullable-generic map read
    // `clrMapGet<K,V>:object`) flowing into a VALUE-type / generic-param `want`. `Map.getOrElse`/`getOrPut` return a
    // `cond` typed `gp:V` whose `else` branch is the object-typed `value`/`__subj` local; without the universal
    // `unbox.any <want>` the reference sits where a value/`!!V` is expected -> a value reinterpreted from a reference
    // -> garbage. Scoped to cond branches (not the shared EmitNullableCoerced) so ordinary object->gp stores are untouched.
    Type EmitBranchCoerced(JsonElement node, Type want)
    {
        var got = EmitNullableCoerced(node, want);
        if (want != null && got != null && !got.IsValueType && !got.IsGenericParameter && got != want
            && (want.IsValueType || want.IsGenericParameter)) { _il.Emit(OpCodes.Unbox_Any, want); return want; }
        return got;
    }

    Type EmitCond(JsonElement e)
    {
        // A value-type-nullable if/when (`Int?`) tags its result type so each branch's `T`/`null` coerces to Nullable<T>.
        Type want = null;
        if (e.TryGetProperty("type", out var tt)) { try { want = ClrRef(tt); } catch { } }
        var elseL = _il.DefineLabel(); var end = _il.DefineLabel();
        EmitExpr(e.GetProperty("cond")); _il.Emit(OpCodes.Brfalse, elseL);
        var t = EmitBranchCoerced(e.GetProperty("then"), want); _il.Emit(OpCodes.Br, end);
        _il.MarkLabel(elseL); EmitBranchCoerced(e.GetProperty("else"), want); _il.MarkLabel(end);
        return want ?? t;
    }

    // Kotlin structural `==`: `if (a == null) b == null else a.Equals((object)b)`.
    // Value types are boxed first — boxing a Nullable<T> with HasValue=false yields a real null ref,
    // so the same null-safe shape works for `Int?` as for reference types.
    Type EmitObjMethod(JsonElement e)
    {
        // Kotlin Any-method on a builtin receiver -> System.Object virtual (box value types first).
        var rt = EmitExpr(e.GetProperty("recv"));
        if (NeedsBoxToRef(rt)) _il.Emit(OpCodes.Box, rt);
        switch (e.GetProperty("method").GetString())
        {
            case "GetHashCode": _il.Emit(OpCodes.Callvirt, typeof(object).GetMethod("GetHashCode")); return typeof(int);
            case "ToString": _il.Emit(OpCodes.Callvirt, typeof(object).GetMethod("ToString")); return typeof(string);
            case "Equals":
                var at = EmitExpr(e.GetProperty("arg"));
                if (NeedsBoxToRef(at)) _il.Emit(OpCodes.Box, at);
                _il.Emit(OpCodes.Callvirt, typeof(object).GetMethod("Equals", new[] { typeof(object) }));
                return typeof(bool);
        }
        return typeof(object);
    }

    Type EmitObjEq(JsonElement e)
    {
        var nonNull = _il.DefineLabel();
        var done = _il.DefineLabel();
        var lt = EmitExpr(e.GetProperty("l"));
        if (NeedsBoxToRef(lt)) _il.Emit(OpCodes.Box, lt);
        _il.Emit(OpCodes.Dup);
        _il.Emit(OpCodes.Brtrue, nonNull);
        _il.Emit(OpCodes.Pop);                                   // a is null -> result = (b == null)
        var rt1 = EmitExpr(e.GetProperty("r"));
        if (NeedsBoxToRef(rt1)) _il.Emit(OpCodes.Box, rt1);
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Ceq);
        _il.Emit(OpCodes.Br, done);
        _il.MarkLabel(nonNull);                                  // a non-null -> a.Equals((object)b)
        var rt2 = EmitExpr(e.GetProperty("r"));
        if (NeedsBoxToRef(rt2)) _il.Emit(OpCodes.Box, rt2);
        _il.Emit(OpCodes.Callvirt, typeof(object).GetMethod("Equals", new[] { typeof(object) }));
        _il.MarkLabel(done);
        return typeof(bool);
    }

    // ---- BCL interop (@Clr) via reflection ----
    // A shared compiler-synthetic type that, once verified cross-assembly, is emitted ONCE (public) in the rt stdlib
    // dll and REFERENCED by app assemblies instead of re-synthesized per-assembly (canonicalization), so a value
    // crossing the app<->rt boundary keeps ONE CLR identity. CharSequence first; extend as each synthetic is verified.
    // KProperty(+Impl) verified 2026-07-02: MONOMORPHIC (one shape — get_name/ctor(string) — everywhere, unlike the
    // per-element KIterator_* family), and Map delegation (`val x by map`) passes the app's KPropertyImpl into the rt's
    // `MapAccessorsKt.getValue(map, thisRef, <>dotkt_KProperty)` — a distinct per-assembly copy EntryPointNotFound-s
    // on `get_name`. Both names skip together (Impl's iface/method sigs reference the canonical interface).
    static readonly HashSet<string> CanonicalSynthetics = new(StringComparer.Ordinal)
        { "<>dotkt_CharSequence", "<>dotkt_KProperty", "<>dotkt_KPropertyImpl" };
    // True when `name` is already defined by a REFERENCED (--ref, Assembly.LoadFrom'd) assembly. The module under
    // construction is a PersistedAssemblyBuilder (not a loaded AppDomain assembly), so it never self-matches.
    static bool ResolvesExternally(string name) =>
        AppDomain.CurrentDomain.GetAssemblies().Any(a => { try { return a.GetType(name) != null; } catch { return false; } });
    static readonly Dictionary<string, Type> _typeCache = new();
    static Type ResolveType(string name)
    {
        if (_typeCache.TryGetValue(name, out var c)) return c;
        var t = Type.GetType(name)
            ?? Type.GetType(name + ", System.Runtime")
            ?? Type.GetType(name + ", System.Private.CoreLib")
            ?? Type.GetType(name + ", System.Linq")
            ?? Type.GetType(name + ", System.Collections")
            ?? Type.GetType(name + ", System.ObjectModel")
            ?? Type.GetType(name + ", System.Text.RegularExpressions")
            ?? Type.GetType(name + ", System.Console")
            ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(x => x != null);
        // A dotted FQN may denote a NESTED type: CLR metadata separates nesting with '+' (Outer+Inner) while the
        // producer's spec is dotted (`kotlin.time.Clock.System` -> `kotlin.time.Clock+System`). Probe by replacing
        // the LAST '.' with '+' and re-resolving; the recursion (via TryResolveType) walks deeper nesting levels
        // (a.b.C.D -> a.b.C+D -> a.b+C+D). Pure CLR name resolution — no source-language knowledge.
        if (t == null)
        {
            var dot = name.LastIndexOf('.');
            if (dot > 0) t = TryResolveType(name[..dot] + "+" + name[(dot + 1)..]);
        }
        if (t == null) throw new NotSupportedException("cannot resolve .NET type " + name);
        _typeCache[name] = t;
        return t;
    }

    Type EmitClrNew(JsonElement e)
    {
        var type = ClrRef(e.GetProperty("type"));
        var argTypes = e.GetProperty("argTypes").EnumerateArray().Select(a => { try { return ClrRef(a); } catch { return (Type)null; } }).ToArray();
        var args = e.GetProperty("args");
        // `new List<R>()` where R is the enclosing generic FUNCTION's type parameter: List<R> is a
        // TypeBuilderInstantiation whose .GetConstructor/.GetConstructors throw — resolve the ctor on the open generic
        // definition (its params are non-generic for the cases we hit: no-arg, capacity), emit the args against those
        // params, and re-anchor via TypeBuilder.GetConstructor. (Mirrors GenericMethod for member access.)
        if (IsTbInstantiation(type))
        {
            var openDef = type.GetGenericTypeDefinition();
            // GetConstructor(argTypes) throws ArgumentException when argTypes contains a TypeBuilder (a generic collection
            // constructed with an EMITTED element type, e.g. `new HashSet<EmittedType>()`) -> null it and fall through to
            // PickOpenCtor (the exact mirror of the EmitClrCall ArgumentException catch).
            ConstructorInfo directCtor = null;
            if (argTypes.All(t => t != null)) try { directCtor = openDef.GetConstructor(argTypes); } catch (ArgumentException) { }
            var openCtor = directCtor
                // ABI substitution (@Clr concrete collections, ArrayList->System.List): a Kotlin arg type doesn't EXACTLY
                // match the BCL ctor param (Collection->IReadOnlyCollection vs the List(IEnumerable<T>) ctor). Fall back to
                // arity + structural assignability (IReadOnlyCollection IS IEnumerable).
                ?? PickOpenCtor(openDef, argTypes, args.GetArrayLength())
                ?? throw new NotSupportedException($"no matching ctor on the open def of {type.FullName} with {args.GetArrayLength()} arg(s)");
            EmitArgs(args, openCtor.GetParameters());
            _il.Emit(OpCodes.Newobj, TypeBuilder.GetConstructor(type, openCtor));
            return type;
        }
        // Exact match first; else an assignability pick (the @Clr concrete-collection ABI: a `Collection<T>` arg lowered
        // to IReadOnlyCollection<T> matches List's `IEnumerable<T>` ctor, disambiguating it from the `int` capacity ctor
        // — an exact GetConstructor misses because the param type differs); else arity-based selection (matters when a
        // lambda arg's type was erased to `object` by the façade — the real delegate param is recovered here).
        // GetConstructor throws ArgumentException when argTypes contains an EMITTED TypeBuilder ("Type must be a type
        // provided by the runtime") — precise ctor argTypes can now resolve to emitted stdlib types; null it and let the
        // assignability/arity fallbacks (which tolerate emitted types) resolve. Mirrors the Tb-instantiation catch above.
        ConstructorInfo exact = null;
        if (argTypes.All(t => t != null))
            try { exact = type.GetConstructor(argTypes); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException) { }
        var ci = exact
                 ?? PickCtorByAssignable(type, argTypes, args.GetArrayLength())
                 ?? PickClrCtor(type, args);
        if (ci == null) throw new NotSupportedException($"no matching constructor for {type.FullName} with {args.GetArrayLength()} arg(s)");
        EmitArgs(args, ci.GetParameters());
        _il.Emit(OpCodes.Newobj, ci);
        return type;
    }

    // A `new` node's `argTypes` (kotc's resolved ctor param types, pure Kotlin FQNs) -> the EXACT ctor on an
    // external/reflected type, when the field is present, same-arity, and every entry resolves. Null tells the caller to
    // fall back to arity-based selection: a mismatched count (prepended enclosing/capture args) or an unresolvable entry
    // must not force a wrong pick, and `GetConstructor` itself returns null when nothing matches the signature.
    ConstructorInfo NewCtorBySig(Type type, JsonElement e, int argc)
    {
        if (!e.TryGetProperty("argTypes", out var atEl) || atEl.ValueKind != JsonValueKind.Array) return null;
        if (atEl.GetArrayLength() != argc) return null;
        Type[] argTypes;
        try { argTypes = atEl.EnumerateArray().Select(a => ClrRef(a)).ToArray(); } catch { return null; }
        if (argTypes.Any(t => t == null)) return null;
        try { return type.GetConstructor(argTypes); } catch { return null; }
    }

    // When exact GetConstructor fails, pick the UNIQUE same-arity ctor on a (constructed/reflected) type whose params
    // ACCEPT the KNOWN arg types by assignability — the @Clr collection ABI (a `Collection<T>` arg lowered to
    // IReadOnlyCollection<T> is assignable to List's `IEnumerable<T>` ctor param, but NOT to its `int` capacity ctor).
    // Null when arg types are unknown or the assignable match is not unique — the caller then falls back to arity scoring.
    ConstructorInfo PickCtorByAssignable(Type type, Type[] argTypes, int n)
    {
        if (argTypes.Length != n || argTypes.Any(t => t == null)) return null;
        ConstructorInfo hit = null;
        try
        {
            foreach (var c in type.GetConstructors().Where(c => c.GetParameters().Length == n))
            {
                var ps = c.GetParameters();
                if (!Enumerable.Range(0, n).All(i => ParamAccepts(ps[i].ParameterType, argTypes[i]))) continue;
                if (hit != null) return null;   // ambiguous
                hit = c;
            }
        }
        catch (Exception ex) when (ex is NotSupportedException || ex is ArgumentException) { return null; }   // emitted/Tb types
        return hit;
    }

    // Pick a ctor on an open generic def by arity + STRUCTURAL assignability (a Kotlin arg whose @Clr type derives from
    // the BCL ctor's param generic def, e.g. IReadOnlyCollection<T> for a List(IEnumerable<T>) ctor). For the @Clr
    // concrete-collection bindings where the Kotlin and BCL signatures aren't identical (Codex's ABI caveat).
    ConstructorInfo PickOpenCtor(Type openDef, Type[] argTypes, int n)
    {
        var cands = openDef.GetConstructors().Where(c => c.GetParameters().Length == n).ToList();
        if (cands.Count <= 1) return cands.FirstOrDefault();
        foreach (var c in cands)
        {
            var ps = c.GetParameters();
            if (Enumerable.Range(0, n).All(i => ParamAccepts(ps[i].ParameterType, argTypes[i]))) return c;
        }
        return cands.FirstOrDefault();
    }

    // Whether a ctor/method param of (possibly open-generic) type `param` accepts an arg of type `arg`: exact assignable,
    // same generic def, or `arg`'s generic def derives from `param`'s generic def (IReadOnlyCollection<> : IEnumerable<>).
    static bool ParamAccepts(Type param, Type arg)
    {
        if (arg == null) return true;                 // unknown arg type -> don't reject
        try { if (param.IsAssignableFrom(arg)) return true; } catch { }
        if (param.IsGenericType && arg.IsGenericType)
        {
            var pdef = param.GetGenericTypeDefinition();
            var adef = arg.GetGenericTypeDefinition();
            if (adef == pdef) return true;
            try { if (adef.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == pdef)) return true; } catch { }
        }
        return false;
    }

    /** Pick a ctor by arity when exact type match fails; among equal-arity ctors prefer the one whose delegate-typed
     *  params match the arity of the lambda (delegateNew/closureNew) args — disambiguates ThreadStart (`()->`) from
     *  ParameterizedThreadStart (`(object)->`). */
    ConstructorInfo PickClrCtor(Type type, JsonElement args)
    {
        int n = args.GetArrayLength();
        var cands = type.GetConstructors().Where(c => c.GetParameters().Length == n).ToList();
        if (cands.Count == 0) return n == 0 ? type.GetConstructor(Type.EmptyTypes) : null;
        if (cands.Count == 1) return cands[0];
        return cands.OrderByDescending(c =>
        {
            var ps = c.GetParameters(); int score = 0, i = 0;
            foreach (var a in args.EnumerateArray())
            {
                var p = ps[i++].ParameterType;
                if (a.TryGetProperty("k", out var k) && (k.GetString() == "delegateNew" || k.GetString() == "closureNew")
                    && typeof(System.Delegate).IsAssignableFrom(p) && a.TryGetProperty("funcType", out var ft))
                {
                    var invoke = p.GetMethod("Invoke");
                    if (invoke != null && invoke.GetParameters().Length == FuncArity(ft.GetString())) score += 2;
                }
            }
            return score;
        }).First();
    }

    /** Arity of a `func:<ret>:<p1,p2,...>` encoding (`func:void:` -> 0, `func:void:object` -> 1). */
    static int FuncArity(string funcType)
    {
        var c = funcType.IndexOf(':'); if (c < 0) return 0;
        var c2 = funcType.IndexOf(':', c + 1); if (c2 < 0) return 0;
        var ps = funcType.Substring(c2 + 1);
        return ps.Length == 0 ? 0 : SplitTopLevel(ps).Count();
    }

    // Cross-module inline splice: read the callee's carried BIR body from its [KotlinInline] (on a --ref'd assembly)
    // and emit it HERE with the call's bindings substituted (param `local`s -> bound values; lambda-param invokes ->
    // the caller's lambda body). A non-local `return` in a spliced lambda body emits a `ret` from the caller. Scope:
    // lambda-taking inline funcs (the only ones whose body must travel); callee-local name scoping is not handled yet.
    // Emit spliced statements giving their CFG labels FRESH Label objects for THIS emission (the BIR's label ids are
    // baked, so re-splicing a body — or one whose ids collide with the caller's — would MarkLabel a Label twice).
    void EmitSplicedStmts(JsonElement stmts)
    {
        var ids = new List<int>();
        void Collect(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("k", out var k) && k.GetString() == "label") ids.Add(el.GetProperty("id").GetInt32());
                foreach (var p in el.EnumerateObject()) Collect(p.Value);
            }
            else if (el.ValueKind == JsonValueKind.Array) foreach (var c in el.EnumerateArray()) Collect(c);
        }
        foreach (var st in stmts.EnumerateArray()) Collect(st);
        var saved = new Dictionary<int, Label?>();
        foreach (var id in ids) { saved[id] = _cfgLabels.TryGetValue(id, out var L) ? L : (Label?)null; _cfgLabels[id] = _il.DefineLabel(); }
        foreach (var st in stmts.EnumerateArray()) EmitStmt(st);
        foreach (var kv in saved) { if (kv.Value.HasValue) _cfgLabels[kv.Key] = kv.Value.Value; else _cfgLabels.Remove(kv.Key); }
    }

    Type EmitInlineSplice(JsonElement e)
    {
        var typeName = SlotName(e.GetProperty("type"));
        var method = e.GetProperty("method").GetString();
        // Disambiguate overloads (forEach/count for Iterable/Array/CharSequence...) by param count + generic arity, since
        // GetMethod(name) throws AmbiguousMatch. Older nodes without pc/ga fall back to the by-name lookup.
        MethodInfo mi;
        if (e.TryGetProperty("pc", out var pcEl))
        {
            int pc = pcEl.GetInt32(), ga = e.GetProperty("ga").GetInt32();
            // Search ALL referenced assemblies: the runtime stdlib is metadata-stripped (no [KotlinInline]); the inline
            // body lives in the @Clr-metadata REF assembly (DotKt.Private.Stdlib). ResolveType returns just the first.
            mi = AppDomain.CurrentDomain.GetAssemblies().Select(a => { try { return a.GetType(typeName); } catch { return null; } })
                     .Where(t => t != null).SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                     .FirstOrDefault(m => m.Name == method && m.GetParameters().Length == pc && m.GetGenericArguments().Length == ga
                          && m.GetCustomAttributesData().Any(c => c.AttributeType.FullName == "DotKt.Runtime.CompilerServices.KotlinInlineAttribute"))
                 ?? throw new NotSupportedException($"inline splice: {typeName}.{method} (pc={pc} ga={ga}) with [KotlinInline] not found");
        }
        else mi = ResolveType(typeName).GetMethod(method)
                 ?? throw new NotSupportedException($"inline splice: method {typeName}.{method} not found");
        var cad = mi.GetCustomAttributesData().FirstOrDefault(c => c.AttributeType.FullName == "DotKt.Runtime.CompilerServices.KotlinInlineAttribute")
                  ?? throw new NotSupportedException($"inline splice: [KotlinInline] body missing on {typeName}.{method}");
        var doc = JsonDocument.Parse((string)cad.ConstructorArguments[0].Value);
        _inlineDocs.Add(doc);
        var addedVals = new List<string>(); var addedLams = new List<string>();
        foreach (var b in e.GetProperty("bindings").EnumerateArray())
        {
            var pn = b.GetProperty("name").GetString();
            if (b.TryGetProperty("lambdaParam", out var lp)) { _inlineLambdas[pn] = (lp.GetString(), b.GetProperty("lambdaBody")); addedLams.Add(pn); }
            else { _inlineSubst[pn] = b.GetProperty("value"); addedVals.Add(pn); }
        }
        // An EXTENSION inline fun's body references the receiver via `this`; evaluate the bound receiver ONCE into a
        // local and push it so a `this` node in the spliced body loads it (instead of the enclosing method's arg0).
        LocalBuilder thisLoc = null;
        if (e.TryGetProperty("thisValue", out var tv))
        {
            var tt = EmitExpr(tv);
            thisLoc = _il.DeclareLocal(tt);
            _il.Emit(OpCodes.Stloc, thisLoc);
            _inlineThis.Push(thisLoc);
        }
        EmitSplicedStmts(doc.RootElement.GetProperty("body"));
        if (thisLoc != null) _inlineThis.Pop();
        foreach (var s in addedVals) _inlineSubst.Remove(s);
        foreach (var s in addedLams) _inlineLambdas.Remove(s);
        return typeof(void);
    }

    // Does a candidate overload's parameter accept the resolved arg type? A null (un-resolvable) arg or an open generic
    // param binds anything. `object` accepts every ref (and boxed value). Two reference types: accept only if PROVABLY
    // assignable — an emitted TypeBuilder arg makes IsAssignableFrom throw OR return false; either way it is not provably
    // assignable to a concrete BCL class (only to `object`), so we reject, steering a `<>dotkt_CharSequence` to
    // `Append(object)` (ToStrings) rather than `Append(String)` (reinterprets the object -> corruption). A value-type is
    // matched by identity (no implicit numeric widening in the fallback pick).
    static bool ParamAcceptsArg(Type param, Type arg)
    {
        if (arg == null || param.IsGenericParameter || param.ContainsGenericParameters) return true;
        if (param == typeof(object)) return true;
        if (!param.IsValueType && !arg.IsValueType)
        {
            try { return param.IsAssignableFrom(arg); } catch { return false; }
        }
        return param == arg;
    }

    // Emit the actual call opcode for an instance/static .NET method whose receiver (if any) is already on the stack
    // (by ADDRESS when `recvType` is a value type — see EmitAddr at the call sites). Chooses the verifiable opcode:
    //   - static or non-virtual method                          -> `call`
    //   - virtual method, REFERENCE receiver                     -> `callvirt`
    //   - virtual FINAL method whose impl is on the value type   -> `call` (value types are sealed; e.g. the TaskAwaiter
    //       struct's INotifyCompletion.OnCompleted, marked virtual-final in metadata — C# emits a direct `call` on the &)
    //   - virtual NON-final method inherited by the value type   -> `constrained. <VT>; callvirt` (e.g. object.ToString
    //       on a struct that doesn't override it — the prefix lets the JIT box/dispatch)
    // A bare `callvirt` on a value-type receiver is CallVirtOnValueType (ilverify-rejected though JIT-tolerated).
    void EmitInstanceCall(MethodInfo mi, bool instance, Type recvType)
    {
        if (!(instance && mi.IsVirtual)) { _il.Emit(OpCodes.Call, mi); return; }
        if (!recvType.IsValueType) { _il.Emit(OpCodes.Callvirt, mi); return; }
        if (mi.IsFinal) { _il.Emit(OpCodes.Call, mi); return; }   // value type's own sealed impl -> direct call on the address
        _il.Emit(OpCodes.Constrained, recvType);
        _il.Emit(OpCodes.Callvirt, mi);
    }

    Type EmitClrCall(JsonElement e, bool instance, bool deref = true)
    {
        // `ClrRef` (not `ResolveType`) so a method on a constructed generic .NET type (`Collection<int>`) resolves.
        var type = ClrRef(e.GetProperty("type"));
        var name = e.GetProperty("method").GetString();
        var argSpecs = e.GetProperty("argTypes").EnumerateArray().Select(a => a.GetString()).ToList();
        var flags = BindingFlags.Public | (instance ? BindingFlags.Instance : BindingFlags.Static);
        MethodInfo mi = null;
        // Exact overload resolution when every arg type resolves (ClrRef handles array:/clrg:/nullable:/func: too,
        // so e.g. `array:object` -> object[] selects String.Format(string, params object[]) over (string, object)).
        var resolved = argSpecs.Select(s => { try { return ClrRef(s); } catch { return (Type)null; } }).ToArray();
        try
        {
            if (resolved.All(x => x != null))
                try { mi = type.GetMethod(name, flags, null, resolved, null); }
                // Overloads that collapse to the SAME CLR signature (e.g. IntArray.sum & Array<out Int>.sum -> sum(int[])
                // under the primitive/boxed dual-representation) make GetMethod ambiguous -> pick the EXACT-param match
                // (also prefers the concrete overload over a generic `T[]` one, which doesn't param-equal `int[]`).
                catch (AmbiguousMatchException) {
                    mi = type.GetMethods(flags).FirstOrDefault(m => m.Name == name
                        && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(resolved));
                }
                // A TypeBuilder in `resolved` (a generic collection of an emitted element type, e.g.
                // ICollection<EmittedType>.Add(EmittedType)) makes GetMethod throw ArgumentException ("Type must be a
                // type provided by the runtime"). Null it out so the name+arity fallback re-anchors on the constructed type.
                catch (ArgumentException) { mi = null; }
            // Fall back to name + arity — e.g. a generic-parameter arg type (`Add(T)` on `Collection<int>`) that
            // doesn't name a plain .NET type; on the constructed type GetMethods returns the substituted overload.
            // When SEVERAL overloads share the arity (StringBuilder.Append has ~19 one-arg overloads) an arbitrary
            // FirstOrDefault can pick a param the arg is NOT assignable to — e.g. a non-String `<>dotkt_CharSequence`
            // into `Append(String)` reinterprets the object as a string -> memory corruption. So, when the arg types
            // resolved, keep only overloads whose every param ACCEPTS the resolved arg (is assignable-from it), then
            // prefer the MOST-SPECIFIC (fewest `object` params): a real String still binds `Append(String)`, while a
            // synthetic/emitted ref (a `<>dotkt_CharSequence` adapter) binds `Append(object)` which ToStrings it.
            if (mi == null)
            {
                var cand = type.GetMethods(flags).Where(m => m.Name == name && m.GetParameters().Length == argSpecs.Count).ToList();
                if (cand.Count > 1)
                {
                    var ok = cand.Where(m => m.GetParameters().Select(p => p.ParameterType).Zip(resolved, ParamAcceptsArg).All(b => b)).ToList();
                    if (ok.Count > 0) cand = ok.OrderBy(m => m.GetParameters().Count(p => p.ParameterType == typeof(object))).ToList();
                }
                mi = cand.FirstOrDefault();
            }
        }
        catch (NotSupportedException) { }
        // A constructed generic type whose arg is an emitted generic parameter (TypeBuilderInstantiation) refuses
        // reflection — re-anchor the open definition's method onto the constructed type via TypeBuilder.GetMethod.
        if (mi == null && type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            try {
            var open = type.GetGenericTypeDefinition();
            var typeArgs = type.GetGenericArguments();
            var om = open.GetMethods(flags).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == argSpecs.Count);
            if (om != null) mi = TypeBuilder.GetMethod(type, om);
            // An inherited INTERFACE member (`IList<T>.Add` lives on the base `ICollection<T>`): interface GetMethods
            // doesn't include base-interface methods, so walk the (transitively-flattened) base interfaces, find the
            // declaring one, construct it with this type's args (shared type parameters) and re-anchor. See item 3.
            else mi = ResolveInheritedIfaceMethod(open, typeArgs, name, argSpecs.Count, flags);
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is ArgumentException) { mi = null; }
        }
        // Last resort: a UNIQUELY-named method (covers e.g. a `params`/vararg method called with one array arg whose
        // static argType — `object` — didn't match the `T[]` param, so neither exact nor arity resolution hit).
        if (mi == null)
        {
            // A generic TypeBuilder instantiation throws on GetMethods — enumerate the open def + re-anchor via GetMethod.
            // Every reflection step here can throw on a TypeBuilderInstantiation (GetMethods/GetGenericTypeDefinition ->
            // NotSupportedException "Derived classes must provide an implementation"); any such failure must leave mi == null
            // so we fall through to dynamic dispatch (below) rather than aborting the emit.
            try {
                MethodInfo[] all; bool reanchor = false;
                try { all = type.GetMethods(flags); }
                catch (NotSupportedException) { all = type.GetGenericTypeDefinition().GetMethods(flags); reanchor = true; }
                var named = all.Where(m => m.Name == name).ToList();
                if (named.Count == 1) mi = reanchor ? TypeBuilder.GetMethod(type, named[0]) : named[0];
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is ArgumentException) { mi = null; }
        }
        // `Array<T>.Clone()` (@ClrIntrinsic("Clone")): a generic array receiver erases to `object`, whose Clone is protected, so
        // resolution fails — but the runtime value is always a System.Array. Resolve Array.Clone and (below) cast the
        // receiver to System.Array before the callvirt. Returns object; the stdlib `as Array<T>` re-types it.
        bool arrayCloneFallback = false;
        if (mi == null && name == "Clone" && argSpecs.Count == 0)
        {
            mi = typeof(System.Array).GetMethod("Clone", BindingFlags.Public | BindingFlags.Instance);
            arrayCloneFallback = mi != null;
        }
        // An unbound Kotlin member that became a clrInstance because its receiver type is @Clr-substituted (e.g.
        // MutableCollection.removeAll/addAll on ICollection -- no BCL equivalent by that name) -> dynamic dispatch.
        // GATED to an INTERFACE owner (the clrInstance analog of the callInstance path's `OwnerHasClrInterface` gate):
        // the runtime value implements that BCL interface under a different concrete type, so `recv.GetType().GetMethod`
        // resolves the real slot. A NON-interface owner (a concrete BCL class) that missed static resolution is a
        // bir2cir Rule-4 ROUTING MISS -- reflection would silently return null -> opaque runtime NRE -- so it must throw
        // at EMIT instead of falling to dynamic dispatch. (bir2cir now refuses lowercase members on non-interface CLR
        // owners upstream; this is the defense-in-depth twin of that compile-time refusal.)
        if (mi == null && instance && type.IsInterface && e.TryGetProperty("recv", out _)) return EmitDynamicCall(e);
        if (mi == null) throw new NotSupportedException($"clrInstance method not resolved: {type}.{name}/{argSpecs.Count} (no BCL match; dynamic-dispatch fallback is gated to interface owners -- a routing MISS on a concrete BCL owner)");
        // A generic BCL method (`System.Array.Fill<T>(T[],T,int,int)`) resolved as its open DEFINITION must be
        // instantiated with the call's type args (threaded by bir2cir from the @ClrIntrinsic generic Kotlin callee),
        // or the emitted MethodSpec stays open -> "method/type not fully instantiated" at run. Non-generic targets
        // (Array.Clone) leave IsGenericMethodDefinition false, so this is a no-op there.
        if (mi.IsGenericMethodDefinition
            && e.TryGetProperty("typeArgs", out var clrTa) && clrTa.ValueKind == JsonValueKind.Array && clrTa.GetArrayLength() > 0)
            mi = mi.MakeGenericMethod(clrTa.EnumerateArray().Select(a => MapType(a)).ToArray());
        // A value-type receiver's instance method needs a managed pointer (e.g. struct Vec2.Mag2()).
        if (instance)
        {
            if (type.IsValueType) EmitAddr(e.GetProperty("recv"));
            else { EmitExpr(e.GetProperty("recv")); if (arrayCloneFallback && !typeof(System.Array).IsAssignableFrom(type)) _il.Emit(OpCodes.Castclass, typeof(System.Array)); }
        }
        EmitArgs(e.GetProperty("args"), mi.GetParameters());
        EmitInstanceCall(mi, instance, type);
        // A `ref T`-returning method used as a value -> dereference the managed pointer (value copy). The live-ref
        // form (`byrefOf(m())`, behind `var x by byref(m())`) passes deref:false to keep the pointer.
        if (mi.ReturnType.IsByRef)
        {
            if (!deref) return mi.ReturnType;
            var elem = mi.ReturnType.GetElementType();
            _il.Emit(OpCodes.Ldobj, elem);
            return elem;
        }
        // TypeBuilder.GetMethod re-anchors the call onto the instantiation but leaves the method's RETURN type open
        // (e.g. `Task<Vec>::GetAwaiter()` reports `TaskAwaiter`1<!0>`, not `<Vec>`). The IL token is correct, but the
        // STATIC type we hand back must be the substituted one or the caller mis-types its temp/local — so trust the
        // BIR `ret` hint, which already carries the substituted type. (Only when the reflected return is still open.)
        if ((mi.ReturnType.IsGenericParameter || mi.ReturnType.ContainsGenericParameters)
            && e.TryGetProperty("ret", out var rh) && rh.ValueKind == JsonValueKind.String)
        {
            var hinted = TryResolveClr(rh.GetString());
            if (hinted != null) return hinted;
        }
        return mi.ReturnType;
    }

    Type[] NativeParameterTypes(JsonElement member) =>
        member.GetProperty("parameterTypes").EnumerateArray()
            .Select(t => TryResolveNativeType(t.GetString()))
            .ToArray();

    Type TryResolveNativeType(string spec)
    {
        try { return NativeType(spec); }
        catch { return null; }
    }

    // A type slot for an IL-opcode context (newarr elem / conv / default): a structured node resolves via MapType, a
    // legacy string token via the shorthand/prefix path below.
    Type NativeType(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object ? MapType(DotKt.Bir.TypeNode.Read(e)) : NativeType(e.GetString());

    Type NativeType(string spec)
    {
        if (spec == null) return typeof(object);
        if (spec.StartsWith("clr:", StringComparison.Ordinal) ||
            spec.StartsWith("clrg:", StringComparison.Ordinal) ||
            spec.StartsWith("array:", StringComparison.Ordinal) ||
            spec.StartsWith("func:", StringComparison.Ordinal) ||
            spec.StartsWith("nullable:", StringComparison.Ordinal) ||
            spec.StartsWith("byref:", StringComparison.Ordinal) ||
            spec.StartsWith("gp:", StringComparison.Ordinal) ||
            spec.StartsWith("@", StringComparison.Ordinal))
            return MapType(spec);
        return spec switch
        {
            "void" or "int" or "long" or "double" or "float" or "bool" or "char" or "string" or
            "uint" or "ulong" or "ubyte" or "ushort" or "short" or "byte" or "object" => MapType(spec),
            _ => ClrRef(ClrOwnerSpec(spec)),
        };
    }

    static string ClrOwnerSpec(string owner) =>
        owner.StartsWith("clr:", StringComparison.Ordinal) || owner.StartsWith("clrg:", StringComparison.Ordinal)
            ? owner
            : "clr:" + owner;

    static string NativeOwnerSpec(JsonElement node, JsonElement member) =>
        node.TryGetProperty("ownerType", out var ownerType) && ownerType.ValueKind == JsonValueKind.String
            ? ownerType.GetString()
            : ClrOwnerSpec(member.GetProperty("owner").GetString());

    // ClrRef (generic-aware type resolution) that returns null instead of throwing.
    Type TryResolveClr(string spec) { try { return ClrRef(spec); } catch { return null; } }

    // ResolveType but returns null instead of throwing (for optional/best-effort overload resolution).
    static Type TryResolveType(string name)
    {
        try { return ResolveType(name); } catch (NotSupportedException) { return null; }
    }

    // A property getter/setter on a (possibly emitted-generic-instantiated) type. On a constructed generic type
    // whose arg is an emitted generic parameter (TypeBuilderInstantiation), runtime reflection refuses
    // GetProperty — re-anchor the open definition's accessor onto the constructed type via TypeBuilder.GetMethod.
    static MethodInfo PropAccessor(Type type, string name, bool getter)
    {
        try { var pi = type.GetProperty(name); var m = getter ? pi?.GetGetMethod() : pi?.GetSetMethod(); if (m != null) return m; }
        catch (NotSupportedException) { }
        // A non-generic type with the property on a base class (e.g. an Element's inherited members) — walk up.
        if (!type.IsGenericType)
        {
            for (var b = type.BaseType; b != null; b = b.BaseType)
            {
                var pi = b.GetProperty(name); var m = getter ? pi?.GetGetMethod() : pi?.GetSetMethod();
                if (m != null) return m;
            }
        }
        var open = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        var openPi = open.GetProperty(name);
        if (openPi != null) return TypeBuilder.GetMethod(type, getter ? openPi.GetGetMethod() : openPi.GetSetMethod());
        // Inherited interface property (`ICollection<T>.Count` accessed on `IList<T>`, or the ARITY-CHANGING
        // `IReadOnlyCollection<KeyValuePair<K,V>>.Count` accessed on `IReadOnlyDictionary<K,V>`/`IDictionary<K,V>`):
        // interface GetProperty doesn't traverse base interfaces, so walk them, substitute the open def's type
        // parameters into the (possibly constructed-arg) base reference, and re-anchor (mirrors
        // ResolveInheritedIfaceMethod).
        var typeArgs = type.GetGenericArguments();
        foreach (var bi in open.GetInterfaces())
        {
            var biOpen = bi.IsGenericType ? bi.GetGenericTypeDefinition() : bi;
            var bp = biOpen.GetProperty(name);
            var acc = getter ? bp?.GetGetMethod() : bp?.GetSetMethod();
            if (acc == null) continue;
            if (!bi.IsGenericType) return acc;
            var biCon = SubstituteIfaceArgs(bi, typeArgs);
            return IsTbInstantiation(biCon) ? TypeBuilder.GetMethod(biCon, acc)
                : (getter ? biCon.GetProperty(name).GetGetMethod() : biCon.GetProperty(name).GetSetMethod());
        }
        return null;
    }

    // List a .NET type's public property names (instance+static, incl. inherited) — for an actionable "no such
    // property" diagnostic instead of a bare NullReferenceException.
    static string PropList(Type t)
    {
        try { return string.Join(", ", t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Select(p => p.Name).Distinct().OrderBy(s => s)); }
        catch { return "?"; }
    }

    Type EmitClrPropGet(JsonElement e)
    {
        var typeName = SlotName(e.GetProperty("type"));
        var propName = e.GetProperty("name").GetString();
        var type = ClrRef(e.GetProperty("type"));
        var isStatic = e.GetProperty("static").GetBoolean();
        var getter = PropAccessor(type, propName, getter: true);
        if (getter == null)
        {
            // Not a .NET property. A DotKt custom-accessor property is a plain `get_<name>` METHOD (no PropertyDef) ->
            // call it. (A backing-field property is a public FIELD -> field access below.)
            MethodInfo gm;
            try
            {
                gm = type.GetMethod("get_" + propName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance, null, Type.EmptyTypes, null);
            }
            catch (NotSupportedException)
            {
                // A constructed generic over a TypeBuilder (TypeBuilderInstantiation) can't resolve members directly:
                // resolve the getter on the open generic def, then re-anchor it to the constructed type via GetMethod.
                gm = null;
                if (type.IsConstructedGenericType)
                {
                    var openGm = type.GetGenericTypeDefinition().GetMethod("get_" + propName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (openGm != null) gm = TypeBuilder.GetMethod(type, openGm);
                }
            }
            if (gm != null)
            {
                if (!isStatic && !gm.IsStatic) { if (type.IsValueType) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv")); }
                EmitInstanceCall(gm, !isStatic && !gm.IsStatic, type);   // routes `constrained.` for a virtual value-type accessor (else CallVirtOnValueType)
                return gm.ReturnType;
            }
            // A .NET FIELD surfaced as a Kotlin property (facadegen records static/const fields, public instance fields,
            // and Kotlin backing-field properties). Emit a field access instead of a getter call.
            FieldInfo fld;
            try
            {
                fld = type.GetField(propName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
            }
            catch (NotSupportedException)
            {
                // Backing field of a constructed generic over a TypeBuilder: resolve on the open def + re-anchor.
                fld = null;
                if (type.IsConstructedGenericType)
                {
                    var openFld = type.GetGenericTypeDefinition().GetField(propName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
                    if (openFld != null) fld = TypeBuilder.GetField(type, openFld);
                }
            }
            if (fld == null)
                throw new InvalidOperationException($"ilemit: no readable property OR field '{propName}' on .NET type '{type}' (spec '{typeName}'). Available properties: [{PropList(type)}]");
            // A `const` (literal) field has no storage — `ldsfld` is invalid (and a memberref to it fails). Inline its
            // value, exactly as C# does. Covers .NET consts surfaced by facadegen as `sprop` (e.g. WinRT constants).
            if (fld.IsLiteral) return EmitLiteralValue(fld.GetRawConstantValue(), fld.FieldType);
            if (!isStatic && !fld.IsStatic)
            {
                if (type.IsValueType) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv"));
            }
            _il.Emit(fld.IsStatic ? OpCodes.Ldsfld : OpCodes.Ldfld, fld);
            return fld.FieldType;
        }
        // A property getter on a VALUE type (e.g. KeyValuePair.Key/.Value) needs the receiver by managed pointer.
        if (!isStatic)
        {
            if (type.IsValueType) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv"));
        }
        EmitInstanceCall(getter, !isStatic, type);   // routes `constrained.` for a virtual value-type accessor (else CallVirtOnValueType)
        return getter.ReturnType;
    }

    Type EmitClrPropSet(JsonElement e)
    {
        var typeName = SlotName(e.GetProperty("type"));
        var propName = e.GetProperty("name").GetString();
        var type = ClrRef(e.GetProperty("type"));
        var isStatic = e.GetProperty("static").GetBoolean();
        var setter = PropAccessor(type, propName, getter: false);
        if (setter == null)
        {
            // A DotKt custom-accessor property's `set_<name>` METHOD (no PropertyDef) -> call it.
            var sm = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .FirstOrDefault(mm => mm.Name == "set_" + propName && mm.GetParameters().Length == 1);
            if (sm != null)
            {
                // A value-type receiver's setter takes `this` by managed pointer -> load its ADDRESS so the mutation
                // lands on the real struct (an addressable lvalue), not a spilled copy. Mirrors the getter path.
                if (!isStatic && !sm.IsStatic) { if (type.IsValueType) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv")); }
                EmitArgs2(new[] { e.GetProperty("value") }, sm.GetParameters());
                EmitInstanceCall(sm, !isStatic && !sm.IsStatic, type);   // routes `constrained.` for a virtual value-type accessor (else CallVirtOnValueType)
                return typeof(void);
            }
            // A writable .NET FIELD surfaced as a Kotlin (mutable) property -> field store.
            var fld = type.GetField(propName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"ilemit: no writable property OR field '{propName}' on .NET type '{type}' (spec '{typeName}'). Available properties: [{PropList(type)}]");
            // `stfld` on a value-type receiver needs the struct's ADDRESS (managed pointer), not a copy.
            if (!isStatic && !fld.IsStatic) { if (type.IsValueType) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv")); }
            EmitNullableCoerced(e.GetProperty("value"), fld.FieldType);
            _il.Emit(fld.IsStatic ? OpCodes.Stsfld : OpCodes.Stfld, fld);
            return typeof(void);
        }
        // A property setter on a VALUE type takes `this` by managed pointer -> load the receiver ADDRESS.
        if (!isStatic) { if (type.IsValueType) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv")); }
        EmitArgs2(new[] { e.GetProperty("value") }, setter.GetParameters());
        EmitInstanceCall(setter, !isStatic, type);   // routes `constrained.` for a virtual value-type accessor (else CallVirtOnValueType)
        return typeof(void);
    }

    // `.NET event +=/-=` -> call the event's add/remove accessor with the handler bound as the event's OWN
    // delegate type (e.g. EventHandler), not the Func/Action the lambda would otherwise produce. The lifted
    // method's signature matches the delegate's Invoke (the FIR injector typed the handler from the event's
    // handler signature), so `ldftn`+`newobj <EventDelegate>(object, IntPtr)` is verifiable — exactly what
    // `button.Click += (s,e)=>{}` lowers to in C#.
    Type EmitClrEvent(JsonElement e, bool add)
    {
        var type = ClrRef(e.GetProperty("type"));
        var ev = type.GetEvent(e.GetProperty("event").GetString());
        var accessor = add ? ev.GetAddMethod() : ev.GetRemoveMethod();
        var delType = accessor.GetParameters()[0].ParameterType;   // == ev.EventHandlerType
        bool isStatic = e.GetProperty("static").GetBoolean();
        if (!isStatic) EmitExpr(e.GetProperty("recv"));
        EmitHandlerAsDelegate(e.GetProperty("handler"), delType);
        _il.Emit(isStatic ? OpCodes.Call : OpCodes.Callvirt, accessor);
        return typeof(void);
    }

    // Resolve a closureNew node's ctor + invoke, INSTANTIATING the closure generic when it is a generic definition.
    // A capturing closure over an enclosing type param (`{ seed }` in `generateSequence<T>`) is a GENERIC class;
    // left as its open definition the `newobj Closure`1::.ctor(!0)` operand is OPEN -> a TypeLoadException at run.
    // Close it with the node's explicit `typeArgs`, else (C13a: kotc/bir2cir omitted them for the non-`this`-capturing
    // form) with the enclosing params matched by NAME (the same resolution `MapType("gp:<name>")` uses). Shared by the
    // main closureNew emit and the delegate-arg binding path so neither can diverge.
    (ConstructorInfo Ctor, MethodInfo Invoke) ResolveClosure(JsonElement e)
    {
        var ct = _types[e.GetProperty("closureType").GetString()];
        ConstructorInfo ctor = ct.Ctor;
        MethodInfo invoke = ct.Methods[e.GetProperty("method").GetString()];
        Type constructed = null;
        if (e.TryGetProperty("typeArgs", out var taProp) && taProp.GetArrayLength() > 0)
            constructed = ct.TB.MakeGenericType(taProp.EnumerateArray().Select(a => MapType(a)).ToArray());
        else if (ct.TB.IsGenericTypeDefinition)
            constructed = ct.TB.MakeGenericType(ct.TB.GetGenericArguments().Select(gp => MapType("gp:" + gp.Name)).ToArray());
        if (constructed != null)
        {
            ctor = TypeBuilder.GetConstructor(constructed, ct.Ctor);
            invoke = TypeBuilder.GetMethod(constructed, invoke);
        }
        return (ctor, invoke);
    }

    // Bind a lambda handler (delegateNew = non-capturing, closureNew = capturing) into a SPECIFIC delegate type.
    // Mirrors the delegateNew/closureNew cases but uses `want` (the event's delegate type) for the ctor.
    void EmitHandlerAsDelegate(JsonElement h, Type want)
    {
        var ctor = DelegateCtor(want);
        switch (h.GetProperty("k").GetString())
        {
            case "delegateNew":
                _il.Emit(OpCodes.Ldnull);
                _il.Emit(OpCodes.Ldftn, FindStatic(h.GetProperty("method").GetString()));
                _il.Emit(OpCodes.Newobj, ctor);
                break;
            case "closureNew":
                var (cctor, cinvoke) = ResolveClosure(h);
                foreach (var c in h.GetProperty("captures").EnumerateArray()) EmitExpr(c);
                _il.Emit(OpCodes.Newobj, cctor);
                _il.Emit(OpCodes.Ldftn, cinvoke);
                _il.Emit(OpCodes.Newobj, ctor);
                break;
            default:
                // A stored handler value (a Func/Action local/field). Re-wrap it into the event's delegate
                // type via its Invoke — `new EventDelegate(value.Invoke)`. Two wrappers around the SAME stored
                // value share target+method, so Delegate equality holds and `-=` removes the right handler.
                var src = EmitExpr(h);                       // stack: the stored delegate value
                _il.Emit(OpCodes.Dup);
                _il.Emit(OpCodes.Ldvirtftn, src.GetMethod("Invoke"));
                _il.Emit(OpCodes.Newobj, ctor);
                break;
        }
    }

    void EmitArgs(JsonElement args, ParameterInfo[] ps)
    {
        int i = 0;
        foreach (var a in args.EnumerateArray()) { EmitArg(a, ps[i].ParameterType); i++; }
        // .NET optional parameters: Kotlin may omit trailing args that have a default — the CLR caller must
        // supply them. Push each missing param's default value (filled from the method metadata).
        for (; i < ps.Length; i++) EmitDefaultArg(ps[i]);
    }

    void EmitDefaultArg(ParameterInfo p)
    {
        var pt = p.ParameterType;
        // An omitted `vararg` ([ParamArray]) -> an EMPTY array, not null (the callee iterates it).
        if (pt.IsArray && p.IsDefined(typeof(ParamArrayAttribute), false)) { EmitLdcI4(0); _il.Emit(OpCodes.Newarr, pt.GetElementType()); return; }
        var dv = p.HasDefaultValue ? p.DefaultValue : null;
        switch (dv)
        {
            case null when !pt.IsValueType: _il.Emit(OpCodes.Ldnull); break;
            case null: var loc = _il.DeclareLocal(pt); _il.Emit(OpCodes.Ldloca, loc); _il.Emit(OpCodes.Initobj, pt); _il.Emit(OpCodes.Ldloc, loc); break;
            case bool b: _il.Emit(b ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); break;
            case char c: _il.Emit(OpCodes.Ldc_I4, (int)c); break;
            case string s: _il.Emit(OpCodes.Ldstr, s); break;
            case long l: _il.Emit(OpCodes.Ldc_I8, l); break;
            case double d: _il.Emit(OpCodes.Ldc_R8, d); break;
            case float f: _il.Emit(OpCodes.Ldc_R4, f); break;
            default: _il.Emit(OpCodes.Ldc_I4, Convert.ToInt32(dv)); break;  // int/short/byte/enum
        }
    }
    void EmitArgs2(JsonElement[] args, ParameterInfo[] ps)
    {
        for (int i = 0; i < args.Length; i++) EmitArg(args[i], ps[i].ParameterType);
    }

    void EmitArg(JsonElement a, Type want)
    {
        // A by-ref parameter (`out`/`ref`, from the `byref(x)` marker) -> pass the lvalue's address.
        if (want.IsByRef) { EmitAddr(a); return; }
        // (4) A LAMBDA passed to a .NET DELEGATE parameter -> build that SPECIFIC delegate (the FIR types the param
        // as a Kotlin function type; the real delegate is `want`, resolved here from the target method's signature).
        // Mirrors the event path; covers custom delegates (ApplicationInitializationCallback, ThreadStart) and BCL
        // Func/Action alike. Scoped to literal lambdas (delegateNew/closureNew) so stored delegate/Func values keep
        // their existing pass-through path.
        if (typeof(System.Delegate).IsAssignableFrom(want) && want != typeof(System.Delegate) && want != typeof(System.MulticastDelegate)
            && a.TryGetProperty("k", out var dk) && (dk.GetString() == "delegateNew" || dk.GetString() == "closureNew"))
        {
            EmitHandlerAsDelegate(a, want);
            return;
        }
        // `T`/null passed to a `T?` slot -> Nullable<T> wrap / default(Nullable<T>) (shared with EmitCond).
        var got = EmitNullableCoerced(a, want);
        if (got == null) return;
        // Box a value/generic-param arg passed to a reference param — but NOT when the param is itself a generic
        // param (passing `T` to a `T` slot flows the value as-is at the instantiation).
        if (NeedsBoxToRef(got) && !want.IsValueType && !want.IsGenericParameter)
            _il.Emit(OpCodes.Box, got);
    }

    // Coerce a just-emitted return VALUE (static type `got`, on the stack) to the declared method return type.
    // Shared by ALL return sites — the plain `return`, the return-inside-try store into the _methodRetType-typed
    // result local, and both `returnExpr` twins — so every path applies the identical coercion:
    //  - `T` returned where the declared type is `T?` -> wrap in Nullable<T> (e.g. a `sortedBy` selector typed
    //    `(T)->R?` whose body yields a non-null R). Mirrors EmitArg's coercion.
    //  - a value-type / generic-param value returned where the method returns `object` (an erased generic `T?` —
    //    NullableGenericReturnErasure) must be boxed so `ldnull`/boxed-value share the object return. A null-const
    //    return already left a real null (no box). Mirrors the var-store box.
    void EmitReturnCoerced(Type got)
    {
        if (got == null) return;
        if (_methodRetType.IsGenericType && _methodRetType.GetGenericTypeDefinition() == typeof(Nullable<>)
            && _methodRetType.GetGenericArguments()[0] == got)
            _il.Emit(OpCodes.Newobj, _methodRetType.GetConstructor(new[] { got }));
        // A value type / `gp:T` returned where the method declares ANY reference type must BOX (C2: the
        // `compareBy { it }` selector lambda returns `it: Int` declared `kotlin.Comparable[object]` = System.IComparable
        // — the boxed Int IS an IComparable). `box` alone yields the tracked type `O`; when the return is a NON-object
        // reference (an interface / concrete ref type) add `castclass <ret>` so the boxed value verifies as that slot
        // (mirrors the `cast` emitter's box+castclass). Previously only `== object` boxed, so a value flowing into a
        // non-object reference return (`IComparable`) landed unboxed -> a value reinterpreted as a reference -> NRE.
        else if (NeedsBoxToRef(got) && !_methodRetType.IsValueType && !_methodRetType.IsGenericParameter)
        {
            _il.Emit(OpCodes.Box, got);
            if (_methodRetType != typeof(object)) _il.Emit(OpCodes.Castclass, _methodRetType);
        }
        // A REFERENCE value (`object` — e.g. an erased generic stdlib return like `clrMapGet<K,V>:object`) returned where
        // the method declares a VALUE type or a generic PARAMETER (`V`) needs the universal cast `unbox.any <ret>` (NOT
        // castclass — `castclass !!V` JIT-crashes value-type instantiations). Without it the reference sits where a value
        // is expected -> ilverify StackUnexpected (found ref 'object', expected value 'V'). Only when it isn't already
        // the exact return type.
        else if (got != _methodRetType && !got.IsValueType && !got.IsGenericParameter
                 && (_methodRetType.IsValueType || _methodRetType.IsGenericParameter))
            _il.Emit(OpCodes.Unbox_Any, _methodRetType);
    }

    // Args for a user method/ctor, boxing value types passed to reference (e.g. `object`/`Any`) params.
    // When the param type is unknown (lifted/unrecorded), emit the arg as-is (no spurious boxing).
    void EmitCallArgs(JsonElement args, MethodInfo mb)
    {
        var pt = _mparams.TryGetValue(mb, out var p) ? p : null;
        // An in-assembly method's declared params live in `_mparams`; a REFERENCED method's don't (MethodBuilder can't
        // be reflected pre-bake, but a resolved referenced MethodInfo can). Read its real ParameterInfo so a value-type
        // / Nullable<> / gp: arg still BOXES into an `object`/reference param — mirrors EmitArgsTyped and the typeArgs
        // referenced path. Without this the `pt==null` branch emitted the arg raw (no box) -> InvalidProgram for e.g.
        // `toString(object)` of an `Int?` (`box Nullable<int>` yields the boxed underlying value, or null).
        var ps = pt == null ? mb.GetParameters() : null;
        int i = 0;
        foreach (var a in args.EnumerateArray())
        {
            if (pt != null && i < pt.Length) EmitArg(a, pt[i]);
            else if (ps != null && i < ps.Length) EmitArg(a, ps[i].ParameterType);
            else EmitExpr(a);
            i++;
        }
        // Fill omitted trailing default/params args (a cross-module caller may omit a `= <const>` default; kotc drops the
        // unrecoverable-from-metadata default expression, so the real value is stamped as [Optional]/DefaultParameterValue
        // on the callee). Only referenced methods carry that metadata (in-assembly emitted params live in `_mparams`, no
        // defaults there), so this fills from `mb.GetParameters()`.
        if (pt == null)
            for (; i < ps.Length; i++) EmitDefaultArg(ps[i]);
    }

    // BIR `func:<ret>:<arg1>,<arg2>,...` -> a System.Func<...> (ret != void) or System.Action<...>.
    Type FuncType(string t)
    {
        var rest = t.Substring(5);
        // RET:ARGS — but RET may itself be a prefixed/bracketed type whose own ':' (clrg:Task[int]) must NOT be
        // taken as the separator. Find the first ':' at bracket-depth 0 AFTER any leading type prefix.
        var colon = FuncRetEnd(rest);
        var ret = rest.Substring(0, colon);
        var argsPart = rest.Substring(colon + 1);
        var args = SplitTopLevel(argsPart).Select(MapType).ToArray();
        if (ret == "void")
            return args.Length == 0 ? typeof(Action)
                : args.Length <= 16
                    ? ResolveType("System.Action`" + args.Length).MakeGenericType(args)
                    : SyntheticActionType(args);
        var all = args.Append(MapType(ret)).ToArray();
        return args.Length <= 16
            ? ResolveType("System.Func`" + all.Length).MakeGenericType(all)
            : SyntheticFuncType(args, MapType(ret));
    }

    Type SyntheticFuncType(Type[] args, Type ret) =>
        SyntheticDelegateType("KFunc", args.Append(ret).ToArray(), returnsValue: true).MakeGenericType(args.Append(ret).ToArray());

    Type SyntheticActionType(Type[] args) =>
        SyntheticDelegateType("KAction", args, returnsValue: false).MakeGenericType(args);

    TypeBuilder SyntheticDelegateType(string baseName, Type[] genericArgs, bool returnsValue)
    {
        var arity = genericArgs.Length;
        var metadataName = CompilerServicesNs + baseName + "`" + arity;
        if (_syntheticDelegates.TryGetValue(metadataName, out var cached))
            return cached;

        var tb = _mod.DefineType(metadataName,
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
            typeof(MulticastDelegate));
        tb.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute).GetConstructor(Type.EmptyTypes), new object[0]));
        tb.SetCustomAttribute(new CustomAttributeBuilder(
            _kFuncAttr.GetConstructor(new[] { typeof(int) }), new object[] { 0 }));

        var names = Enumerable.Range(1, arity).Select(i => i == arity && returnsValue ? "TResult" : "T" + i).ToArray();
        var gps = tb.DefineGenericParameters(names);
        var invokeParams = returnsValue ? gps.Take(arity - 1).Cast<Type>().ToArray() : gps.Cast<Type>().ToArray();
        var invokeRet = returnsValue ? (Type)gps[^1] : typeof(void);

        var ctor = tb.DefineConstructor(
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.RTSpecialName | MethodAttributes.SpecialName,
            CallingConventions.Standard,
            new[] { typeof(object), typeof(IntPtr) });
        ctor.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

        var invoke = tb.DefineMethod(
            "Invoke",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            invokeRet,
            invokeParams);
        invoke.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

        _syntheticDelegates[metadataName] = tb;
        _syntheticDelegateCtors[tb] = ctor;
        _syntheticDelegateInvokes[tb] = invoke;
        return tb;
    }

    // Index of the ':' separating RET from ARGS in a `func:` BODY (the leading "func:" already stripped by the caller).
    // When the RET is itself a NESTED func — `(Int)->(()->Int)` encodes as body `func:kotlin.Int::kotlin.Int` — the
    // inner func's OWN ret/args colon sits at depth 0 and the old "skip one prefix, grab first ':'" split mis-parsed it
    // (ret=`func:kotlin.Int`, args=`:kotlin.Int`, leaving `:kotlin.Int` unresolvable). Recursively skip the whole inner
    // func in that case. Every OTHER ret shape (leaf / clrg:/array:/nullable: with its own bracket-protected or single
    // leading colon) keeps the prior single-prefix scan — scoped narrowly so only the genuine nested-func-ret changes.
    static int FuncRetEnd(string s)
    {
        if (s.StartsWith("func:", StringComparison.Ordinal) || s.StartsWith("sfunc:", StringComparison.Ordinal))
            return SkipTypeToken(s, 0);
        int start = 0;
        foreach (var pre in new[] { "clrg:", "clr:", "array:", "nullable:", "gp:", "byref:" })
            if (s.StartsWith(pre)) { start = pre.Length; break; }
        int depth = 0;
        for (int i = start; i < s.Length; i++)
        {
            if (s[i] == '[') depth++;
            else if (s[i] == ']') depth--;
            else if (s[i] == ':' && depth == 0) return i;
        }
        return s.Length;
    }

    // Advance past exactly ONE type token at `i`; return the index just after it (a top-level ':' / ',' / ']' / end).
    // A `func:` token recurses through its ret + its comma-list args (args present iff the next char begins a type);
    // a modifier prefix (array:/nullable:/byref:) recurses into its element; a clrg:/clr:/gp:/leaf token scans to the
    // next top-level delimiter with [] nesting protecting inner ':'/','. Pure structural parse — no type resolution.
    static int SkipTypeToken(string s, int i)
    {
        static bool At(string s, int i, string pre) => i + pre.Length <= s.Length && s.AsSpan(i, pre.Length).SequenceEqual(pre);
        foreach (var pre in new[] { "array:", "nullable:", "byref:" })
            if (At(s, i, pre)) return SkipTypeToken(s, i + pre.Length);
        if (At(s, i, "func:"))
        {
            i = SkipTypeToken(s, i + 5);                                    // ret
            if (i < s.Length && s[i] == ':') i++;                          // ret/args separator
            if (i < s.Length && s[i] != ':' && s[i] != ',' && s[i] != ']') // non-empty args -> comma-list
            {
                i = SkipTypeToken(s, i);
                while (i < s.Length && s[i] == ',') i = SkipTypeToken(s, i + 1);
            }
            return i;
        }
        foreach (var pre in new[] { "clrg:", "clr:", "gp:" })
            if (At(s, i, pre)) { i += pre.Length; break; }
        int depth = 0;
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '[') depth++;
            else if (c == ']') { if (depth == 0) break; depth--; }
            else if (depth == 0 && (c == ':' || c == ',')) break;
        }
        return i;
    }

    // BIR `clrg:<openName>[<arg1>,<arg2>,...]` -> a constructed generic .NET type. Args split at bracket-depth 0
    // so nested generics (List[ValueTuple[int,string]]) parse correctly.
    // Resolve a .NET type reference that may be a plain name (ResolveType), a generic `clrg:Open[args]`,
    // or a func/closed encoding (MapType). Used by clrNew/clrPropGet so they accept generic types (System.Lazy<T>).
    // A clr* owner/type slot: structured (bir2cir MemberCallSubstitution) walks TypeNode; a legacy string (kotc's own
    // clrInstance interop `type`, a synthesized argType shorthand) keeps the string path.
    Type ClrRef(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object ? MapType(DotKt.Bir.TypeNode.Read(e)) : ClrRef(e.GetString());

    Type ClrRef(string s) =>
        s.StartsWith("byref:") ? ClrRef(s.Substring(6)).MakeByRefType() :   // `out`/`ref` param type (T&)
        s.StartsWith("clrg:") ? GenericType(s.Substring(5)) :
        (s.StartsWith("func:") || s.StartsWith("clr:") || s.StartsWith("array:") || s.StartsWith("nullable:") || s.StartsWith("gp:") || s.StartsWith("@")) ? MapType(s) :
        // A bare PRIMITIVE/string/void shorthand (int/long/bool/char/string/object/…) is CLR-resolution vocabulary that
        // ResolveType (BCL reflection by FQN) cannot resolve — route it through MapType (which owns the shorthand switch).
        // An `argTypes` entry can be such a shorthand: bir2cir's TransformNew synthesizes clrNew.argTypes from an arg's BIR
        // token and the type-lowering pass lowers e.g. `kotlin.String` -> "string"; without this the ctor-overload lookup
        // nulls that entry and falls back to arity, mis-picking StringBuilder(Int32) for StringBuilder(String).
        PrimShorthand.Contains(s) ? MapType(s) :
        ResolveType(s);

    static readonly HashSet<string> PrimShorthand = new(StringComparer.Ordinal)
    { "void", "object", "string", "int", "long", "short", "byte", "double", "float", "bool", "char", "uint", "ulong", "ushort", "ubyte" };

    // A generic TYPE ARGUMENT of `System.Void` is illegal in .NET; Kotlin `Unit`/`Nothing` map to `void` for a return
    // position but as a type arg (`Continuation<Unit>`, `Map<K, Unit>`, …) they must be a real type -> `object`.
    Type MapArg(string t) { var r = MapType(t); return r == typeof(void) ? typeof(object) : r; }

    Type GenericType(string spec)
    {
        var br = spec.IndexOf('[');
        var open = spec.Substring(0, br);
        var inner = spec.Substring(br + 1, spec.Length - br - 2);
        var args = SplitTopLevel(inner).Select(MapArg).ToArray();
        // A Kotlin generic type @ClrIntrinsic-aliased to a NON-generic BCL type (e.g. Comparator<T> ->
        // System.Collections.IComparer) still carries the Kotlin type args in the spec, but the BCL target has no `N
        // arity. If `open`N` doesn't exist, fall back to the non-generic type (drop the args).
        var openGen = TryResolveType(open + "`" + args.Length);
        return openGen != null ? openGen.MakeGenericType(args) : ResolveType(open);
    }

    static List<string> SplitTopLevel(string s)
    {
        var res = new List<string>(); int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '[') depth++;
            else if (s[i] == ']') depth--;
            else if (s[i] == ',' && depth == 0) { res.Add(s.Substring(start, i - start)); start = i + 1; }
        }
        if (s.Length > 0) res.Add(s.Substring(start));
        return res;
    }

    // A parameter-shape token used to pick the EXACT intended overload deterministically (no heuristic):
    // the Kotlin op is uniquely named, so the caller knows precisely which .NET method it maps to.
    static string Shape(Type p)
    {
        if (p.IsByRef) p = p.GetElementType();
        if (p.IsArray) return "array";
        if (p.IsGenericParameter) return "gp";
        if (p == typeof(string)) return "string";
        if (p == typeof(char)) return "char";
        if (p == typeof(int)) return "int";
        if (p.IsGenericType)
        {
            var d = p.GetGenericTypeDefinition();
            if (d == typeof(System.Collections.Generic.IEnumerable<>)) return "ienum";
            if (d.Name.StartsWith("Func`") || d.Name.StartsWith("Action`")) return "func:" + p.GetGenericArguments().Length;
            return "generic";
        }
        return p.Name;
    }

    // Resolve a generic static method by name + type-arity + exact parameter shapes, then instantiate it.
    MethodInfo ResolveGenericMethod(Type type, string name, int typeArgCount, string[] shapes, Type[] typeArgs, bool instance = false)
    {
        // The caller may omit TRAILING default/params args (a generic fn restored @JvmOverloads-style supplies fewer
        // shapes than the single .NET method has params); accept >= shapes, match shapes over the provided prefix, and
        // require the extra trailing params to be optional (the emit path fills them via EmitDefaultArg).
        var cands = type.GetMethods(BindingFlags.Public | (instance ? BindingFlags.Instance : BindingFlags.Static))
            .Where(m => m.Name == name && m.IsGenericMethodDefinition
                     && m.GetGenericArguments().Length == typeArgCount
                     && m.GetParameters().Length >= shapes.Length
                     && m.GetParameters().Take(shapes.Length).Select((p, i) => Shape(p.ParameterType) == shapes[i]).All(x => x)
                     && m.GetParameters().Skip(shapes.Length).All(p => p.HasDefaultValue || p.IsDefined(typeof(ParamArrayAttribute), false)))
            .ToList();
        // Prefer an exact-arity match over one that needs default-filling.
        return (cands.FirstOrDefault(m => m.GetParameters().Length == shapes.Length) ?? cands.First()).MakeGenericMethod(typeArgs);
    }

    // #37 m1: a type slot is a STRUCTURED Type node (birType-emitted / bir2cir clr*) OR a legacy STRING token (kotc's
    // own clrInstance interop `type`, the m3 `sig`/typeArgs tokens). Dispatch on the JSON kind; the string path keeps
    // the shorthand/legacy-token resolver below, the object path walks TypeNode.
    Type MapType(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? MapType(e.GetString())
        : e.ValueKind == JsonValueKind.Object ? MapType(DotKt.Bir.TypeNode.Read(e))
        : typeof(object);

    Type MapType(DotKt.Bir.TypeNode t) => t switch
    {
        DotKt.Bir.TypeNode.ByRef b => MapType(b.Of).MakeByRefType(),
        DotKt.Bir.TypeNode.Array a => MapType(a.Elem).MakeArrayType(),
        DotKt.Bir.TypeNode.Nullable n => typeof(Nullable<>).MakeGenericType(MapType(n.Of)),
        DotKt.Bir.TypeNode.Fn fn => FuncType(fn),
        DotKt.Bir.TypeNode.Tv tv => ResolveTv(tv),
        DotKt.Bir.TypeNode.Fqn { Args: null } f => MapType(f.Name),   // reuse the shorthand / bare-FQN resolver
        DotKt.Bir.TypeNode.Fqn f => ConstructGeneric(f.Name, f.Args),
        _ => typeof(object),
    };

    // A constructed generic from a structured Fqn(name, args): an emitted open type -> MakeGenericType, else a
    // referenced .NET generic by arity-suffixed FQN. (A void type-arg -> object, illegal as a .NET type arg.)
    Type ConstructGeneric(string name, DotKt.Bir.TypeNode[] args)
    {
        var mapped = args.Select(a => { var r = MapType(a); return r == typeof(void) ? typeof(object) : r; }).ToArray();
        if (_types.TryGetValue(name, out var oti)) return oti.AsType.MakeGenericType(mapped);
        return ResolveType(name + "`" + mapped.Length).MakeGenericType(mapped);
    }

    // A `tv` (scope + flattened index) -> the CLR generic-parameter builder: scope "method" -> the method's own params
    // (`!!i`, GenericMethodParameter), scope "type" -> the enclosing type's flattened params (`!i`, GenericTypeParameter).
    Type ResolveTv(DotKt.Bir.TypeNode.Tv tv)
    {
        var pool = tv.Scope == "method" ? _curMethodParams : _curTypeParams;
        if (pool != null)
            foreach (var g in pool.Values)
                if (g.GenericParameterPosition == tv.I) return g;
        throw new NotSupportedException($"unresolved type variable {tv.Scope}!{tv.I} (no generic param at that position in scope)");
    }

    // Structured function type -> the CLR delegate (Action/Func or a synthetic for arity > 16).
    Type FuncType(DotKt.Bir.TypeNode.Fn fn)
    {
        var args = fn.Params.Select(MapType).ToArray();
        var ret = MapType(fn.Ret);
        if (ret == typeof(void))
            return args.Length == 0 ? typeof(Action)
                : args.Length <= 16 ? ResolveType("System.Action`" + args.Length).MakeGenericType(args)
                : SyntheticActionType(args);
        var all = args.Append(ret).ToArray();
        return args.Length <= 16 ? ResolveType("System.Func`" + all.Length).MakeGenericType(all) : SyntheticFuncType(args, ret);
    }

    Type MapType(string t)
    {
        if (t != null && t.StartsWith("byref:")) return MapType(t.Substring(6)).MakeByRefType();   // a `ref T` local
        if (t == "stackptr") return typeof(byte).MakePointerType();   // a localloc'd stack buffer pointer (unverifiable)
        if (t != null && t.StartsWith("clr:")) return ResolveType(t.Substring(4));
        if (t != null && t.StartsWith("array:")) return MapType(t.Substring(6)).MakeArrayType();
        if (t != null && t.StartsWith("func:")) return FuncType(t);
        if (t != null && t.StartsWith("clrg:")) return GenericType(t.Substring(5));
        if (t != null && t.StartsWith("nullable:")) return typeof(Nullable<>).MakeGenericType(MapType(t.Substring(9)));
        // `gp:T` -> a generic type parameter, resolved in context (method params shadow the enclosing type's).
        if (t != null && t.StartsWith("gp:"))
        {
            var gpName = t.Substring(3);
            if (_curMethodParams != null && _curMethodParams.TryGetValue(gpName, out var mgp)) return mgp;
            if (_curTypeParams != null && _curTypeParams.TryGetValue(gpName, out var tgp)) return tgp;
            throw new NotSupportedException("unresolved generic type parameter " + gpName);
        }
        if (t != null && t.StartsWith("@"))
        {
            // `@Name` -> the user type; `@Name[arg,...]` -> that user generic type constructed (Box<int>). The `@` marker
            // is kotc's "emitted-type" hint, but a PURE-Kotlin stdlib type with no @ClrTypeAlias (kotlin.Result,
            // kotlin.text.MatchResult) is emitted in a REFERENCED assembly (DotKt.Stdlib.dll), not this one — so when it
            // isn't in THIS assembly's `_types`, resolve it as a referenced .NET type (by FQN, arity-suffixed for a generic).
            var spec = t.Substring(1);
            var br = spec.IndexOf('[');
            if (br < 0) return _types.TryGetValue(spec, out var ti0) ? ti0.AsType : ResolveType(spec);
            var open = spec.Substring(0, br);
            var args = SplitTopLevel(spec.Substring(br + 1, spec.Length - br - 2)).Select(MapArg).ToArray();
            if (_types.TryGetValue(open, out var oti)) return oti.AsType.MakeGenericType(args);
            return ResolveType(open + "`" + args.Length).MakeGenericType(args);
        }
        return t switch
        {
            "void" => typeof(void), "int" => typeof(int), "long" => typeof(long),
            "double" => typeof(double), "float" => typeof(float), "bool" => typeof(bool),
            "char" => typeof(char), "string" => typeof(string),
            "uint" => typeof(uint), "ulong" => typeof(ulong), "ubyte" => typeof(byte), "ushort" => typeof(ushort),
            // Kotlin Byte is SIGNED (sbyte, -128..127); UByte is the unsigned `byte`.
            "short" => typeof(short), "byte" => typeof(sbyte),
            // A bare FQN identity (kotc's pure-FQN output — NO `@`/`clr:` marker): ilemit DERIVES where the type lives.
            // An in-assembly emitted type (`_types`, incl. the constructed `Name[args]` form) wins FIRST, else a
            // referenced .NET type by reflection (`System.X`), else fall back to object (the pre-existing default for an
            // erased/unknown non-dotted token). This is the ilemit half of "kotc emits pure FQNs; ilemit derives
            // resolution" — so a plain `kotlin.Int`/`Foo`/`kotlin.Any` reference resolves to its emitted TypeBuilder.
            // A bare constructed-generic `Name[args]` whose open name isn't emitted here (e.g. the `ownerType` of a
            // referenced `kotlin.Result[int]` member call) resolves as a referenced generic (GenericType arity-suffixes).
            _ => TryMapEmittedType(t) ?? ((t != null && t.Contains('[')) ? GenericType(t)
                 : (t != null && t.Contains('.')) ? ResolveType(t) : typeof(object)),
        };
    }

    // Resolve a bare type spec (no `@`/`clr:`/shorthand prefix) against THIS assembly's emitted types (`_types`).
    // Handles the plain `Name` and the constructed-generic `Name[arg,...]` forms (the `_types` key is the open name
    // WITHOUT arity, so the `[...]` suffix is stripped to look it up). Returns null when the name is not emitted here
    // (the caller then falls back to reflection over referenced assemblies).
    Type TryMapEmittedType(string spec)
    {
        if (spec == null) return null;
        var br = spec.IndexOf('[');
        if (br < 0) return _types.TryGetValue(spec, out var ti) ? ti.AsType : null;
        var open = spec.Substring(0, br);
        if (!_types.TryGetValue(open, out var oti)) return null;
        var args = SplitTopLevel(spec.Substring(br + 1, spec.Length - br - 2)).Select(MapArg).ToArray();
        return oti.AsType.MakeGenericType(args);
    }

    void Save(PersistedAssemblyBuilder ab, MethodBuilder entry)
    {
        MetadataBuilder metadata = ab.GenerateMetadata(out BlobBuilder ilStream, out BlobBuilder fieldData);
        var peHeader = new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll);
        var peBuilder = new ManagedPEBuilder(
            peHeader, new MetadataRootBuilder(metadata), ilStream,
            mappedFieldData: fieldData,
            entryPoint: entry != null ? MetadataTokens.MethodDefinitionHandle(entry.MetadataToken) : default);
        var blob = new BlobBuilder();
        peBuilder.Serialize(blob);
        using (var fs = new FileStream(Path.Combine(_outDir, _asmName + ".dll"), FileMode.Create, FileAccess.Write))
            blob.WriteContentTo(fs);
        var v = Environment.Version;
        File.WriteAllText(Path.Combine(_outDir, _asmName + ".runtimeconfig.json"),
            "{\n  \"runtimeOptions\": {\n    \"tfm\": \"net10.0\",\n" +
            "    \"framework\": { \"name\": \"Microsoft.NETCore.App\", \"version\": \"" + v.Major + "." + v.Minor + ".0\" }\n  }\n}\n");
        Console.WriteLine($"emitted {_asmName}.dll");
    }
}
