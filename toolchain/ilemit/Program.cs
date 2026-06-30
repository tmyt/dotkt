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

        if (root.TryGetProperty("cirDraft", out draft) &&
            draft.TryGetProperty("ilemitCompatBir", out var compat))
            return JsonDocument.Parse(compat.GetRawText());

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

    // The coroutine-ABI types the suspend emitter binds against. These were HARDCODED to the DotKt.Runtime
    // `DotKt.Coroutines.*` assembly — now ABANDONED: the stdlib IS the runtime and must not depend on it. They name
    // the canonical kotlin.coroutines.* types the stdlib provides. The suspend->Task bridge helpers (TypedCont/
    // Builders) and the kotlinx CancellableCont have no kotlin.* home yet — they are placeholders the coroutine
    // PORT (refactor-stdlib-types-out-of-coroutines) will move into the stdlib and finalize. Centralized so the port
    // repoints ONE place; suspend emission may break until the bridge is ported (accepted).
    const string CoContinuation = "kotlin.coroutines.Continuation`1";
    const string CoContext = "kotlin.coroutines.CoroutineContext";
    const string CoEmptyContext = "kotlin.coroutines.EmptyCoroutineContext";
    const string CoIntrinsics = "kotlin.coroutines.intrinsics.IntrinsicsKt";   // holder of COROUTINE_SUSPENDED
    const string CoTypedCont = "kotlin.coroutines.TypedCont`1";                 // bridge — coroutine port provides
    const string CoBuilders = "kotlin.coroutines.Builders";                     // bridge — coroutine port provides
    const string CoCancellableCont = "kotlinx.coroutines.CancellableCont`1";    // dotktx — port provides
    const string CoSeqStep = "kotlin.sequences.ISeqStep`1";                      // sequence{} bridge — port provides
    const string CoSeq = "kotlin.sequences.Seq";                                 // sequence{} bridge — port provides
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
    // Coroutine context: inside a state-machine MoveNext, a reference to a param/live-local (a "cpsField") is a
    // field of the SM struct, not an IL local/arg. Non-null only while emitting MoveNext. See EmitCoroutine.
    Dictionary<string, FieldInfo> _coFields;   // FieldInfo (not FieldBuilder) so a generic SM's self-instantiated fields fit
    // Coroutine `this` capture: for an INSTANCE suspend method/lambda (e.g. a capturing suspend lambda's closure
    // `invoke`), the original receiver is stored in an SM field so MoveNext can still reach it after a resume.
    // Non-null only while emitting such a MoveNext; `this` then loads this field instead of ldarg.0 (the SM).
    FieldInfo _coThis;
    int _seqCounter;   // unique suffix for emitted sequence{} state-machine types
    int _smCounter;    // unique suffix for coroutine state-machine types (nested-in-owner to reach protected members)
    // try-around-await: inside a try region of a MoveNext, `ret` is illegal — suspension/return `leave` to the
    // single method exit instead. Depth > 0 while emitting steps between coTryBegin and coTryEnd.
    int _coTryDepth;
    Label _coExit;

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
                        BaseName = t.TryGetProperty("base", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null,
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
                    var (bopen, bconstructed) = ParseOwner(ti.BaseName);
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
                    var spec = i.GetString();
                    // A .NET-mapped interface (`clr:`/`clrg:...[..]`, e.g. DotKt.Coroutines.Continuation<int>) is
                    // resolved by reflection; a Kotlin-user interface `Container[int]` comes from _types; a bare external
                    // .NET interface FQN (pure-FQN, not `_types`) falls back to reflection.
                    Type itype;
                    if (spec.StartsWith("clr:") || spec.StartsWith("clrg:")) itype = MapType(spec);
                    else { var (open, constructed) = ParseOwner(spec); itype = constructed ?? (_types.TryGetValue(open, out var oti) ? (Type)oti.TB : ResolveType(open)); }
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
                        ti.Fields[f.GetProperty("name").GetString()] =
                            ti.TB.DefineField(f.GetProperty("name").GetString(), MapType(f.GetProperty("type").GetString()), FieldAttributes.Public | FieldAttributes.Static);
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
                        var fb = ti.TB.DefineField(f.GetProperty("name").GetString(), MapType(f.GetProperty("type").GetString()), fattrs);
                        // A not-publicly-settable property's backing field -> [KotlinReadOnly] (consumer restores it as `val`).
                        if (f.TryGetProperty("readOnly", out var ro) && ro.GetBoolean()) ApplyKotlinReadOnly(fb);
                        ti.Fields[f.GetProperty("name").GetString()] = fb;
                    }
                foreach (var m in ti.Def.GetProperty("methods").EnumerateArray()) DeclareMethod(ti, m, isStatic: false);
                // Real CLR properties: DefineProperty over the already-declared get_/set_ accessor methods, so a Kotlin
                // property is seen as a PROPERTY (not a bare field/methods) by C#/F#/reflection. Additive — only fires
                // when kotc emits the `properties` metadata. See docs/design-clr-property-model.md.
                if (ti.Def.TryGetProperty("properties", out var props))
                    foreach (var p in props.EnumerateArray())
                    {
                        var pb = ti.TB.DefineProperty(p.GetProperty("name").GetString(), PropertyAttributes.None, MapType(p.GetProperty("type").GetString()), null);
                        if (p.TryGetProperty("get", out var g) && g.ValueKind == JsonValueKind.String && ti.Methods.TryGetValue(g.GetString(), out var gm)) pb.SetGetMethod(gm);
                        if (p.TryGetProperty("set", out var s) && s.ValueKind == JsonValueKind.String && ti.Methods.TryGetValue(s.GetString(), out var sm)) pb.SetSetMethod(sm);
                    }
                var ctors = ti.Def.GetProperty("ctors");
                if (!ti.IsInterface)
                    foreach (var c in ctors.EnumerateArray())
                    {
                        var ps = c.GetProperty("params").EnumerateArray().Select(p => MapType(p.GetProperty("type").GetString())).ToArray();
                        var cb = ti.TB.DefineConstructor(AccessOf(c), CallingConventions.Standard, ps);
                        DefineParamNames(cb, c);   // ctor param NAMES + [Optional]/DefaultParameterValue (named-arg ctor calls)
                        ti.Ctors.Add(cb);
                        ti.CtorDefs.Add(c);
                    }
                if (ti.Ctors.Count > 0) { ti.Ctor = ti.Ctors[0]; ti.CtorDef = ti.CtorDefs[0]; }
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
                var ifWork = new Queue<string>();
                var ifSeen = new HashSet<string>();
                foreach (var i in ifs.EnumerateArray()) ifWork.Enqueue(i.GetString());
                while (ifWork.Count > 0)
                {
                    var spec = ifWork.Dequeue();
                    if (!ifSeen.Add(spec)) continue;
                    // C3b transitive reverse bridge: a Kotlin collection interface (Set/MutableSet/... — extends Iterable
                    // but isn't itself @Clr) still makes the class IEnumerable<E> via its @Clr Collection supertype.
                    TryGenerateEnumeratorForKotlinIface(ti, spec);
                    // A .NET-mapped interface (DotKt.Coroutines.Continuation<int>): bind each interface method to the
                    // class method of the same .NET name via reflection (the class emits PascalCase names already).
                    if (spec.StartsWith("clr:") || spec.StartsWith("clrg:"))
                    {
                        var itype = MapType(spec);
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
                            if (im.Name != "GetEnumerator" && have.Contains(im.Name))   // GetEnumerator: handled by the reverse bridge above
                                ti.TB.DefineMethodOverride(ti.Methods[im.Name], reanchor ? TypeBuilder.GetMethod(itype, im) : im);
                        continue;
                    }
                    var (open, constructed) = ParseOwner(spec);
                    if (!_types.TryGetValue(open, out var iface)) continue;
                    // Transitively process this interface's base interfaces too, substituting the type args through the
                    // chain (e.g. WithComparableMarks : TimeSource, or List[object] : Collection[object]).
                    var ifSubstForBases = BuildTypeArgSubst(spec, iface);
                    if (iface.Def.ValueKind == JsonValueKind.Object && iface.Def.TryGetProperty("interfaces", out var baseIfs))
                        foreach (var bi in baseIfs.EnumerateArray()) ifWork.Enqueue(SubstSig(bi.GetString(), ifSubstForBases));
                    // ti.Methods is name-keyed, so OVERLOADED body methods collide and DefineMethodOverride would bind the
                    // wrong one (e.g. a value class's compareTo(UByte/UShort/UInt/ULong) all map to ti.Methods["compareTo"]
                    // = the last = ULong, wired to Comparable<UInt32>.compareTo -> TypeLoad "do not match"). Resolve the
                    // overload by the interface method's TYPE-ARG-SUBSTITUTED signature via MethodsBySig.
                    var ifSubst = BuildTypeArgSubst(spec, iface);
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
                            var subSig = imName + "(" + string.Join(",", imDef.GetProperty("params").EnumerateArray()
                                .Select(p => { var t = p.GetProperty("type").GetString(); return ifSubst.TryGetValue(t, out var s) ? s : t; })) + ")";
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
                                if (!ti.IsInterface) TryEmitDimForwardBridge(ti, imDef, ifSubst, subSig, constructed, ifaceBuilder);
                                continue;
                            }
                            var ifaceMethod = constructed != null ? TypeBuilder.GetMethod(constructed, ifaceBuilder) : (MethodInfo)ifaceBuilder;
                            // Covariant return: a NARROWED override return type (markNow():ValueTimeMark over the iface's
                            // :ComparableTimeMark) makes a direct MethodImpl fail the CLR's exact-return rule. Emit a bridge
                            // with the iface's (base) return type that calls the narrow body method and upcasts; put the
                            // MethodImpl on the bridge. The iface ret/params come from imDef (BIR) via MapType — no need to
                            // reflect the TypeBuilderInstantiation.
                            Type ifaceRet = null;
                            try { if (imDef.TryGetProperty("ret", out var rt)) ifaceRet = MapType(SubstSig(rt.GetString(), ifSubst)); } catch { }
                            // Bridge only on a genuine type NARROWING (different type name) — not two reference-different
                            // instantiations of the SAME generic (Iterator<Object> vs Iterator<Object>), which match fine.
                            if (ifaceRet != null && bodyMethod.ReturnType != ifaceRet &&
                                ((bodyMethod.ReturnType.Name != ifaceRet.Name && !bodyMethod.ReturnType.IsValueType && !ifaceRet.IsValueType)   // covariant reference narrowing
                                 || (ifaceRet == typeof(void) && bodyMethod.ReturnType != typeof(void))))   // a BCL slot that DROPS the Kotlin return (MutableCollection.add():Boolean -> ICollection.Add():void, set/removeAt:E -> void)
                                EmitCovariantBridge(ti, imName, imDef, ifSubst, bodyMethod, ifaceMethod, ifaceRet);
                            else
                                ti.TB.DefineMethodOverride(bodyMethod, ifaceMethod);
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
                    if (!(m.TryGetProperty("attrs", out var mattrs) && mattrs.GetArrayLength() > 0)
                        || !ti.Methods.TryGetValue(m.GetProperty("name").GetString(), out var mb)) continue;
                    foreach (var a in mattrs.EnumerateArray()) { var cab = BuildCab(a); if (cab != null) mb.SetCustomAttribute(cab); }
                }
            // DotKt metadata: stamp Kotlin modifiers with no .NET analog so a consuming Kotlin module can restore
            // them. [KotlinFileClass] on a file-facade class -> its statics are top-level fns; [KotlinFunction(flags)] on
            // methods carrying infix/operator/suspend. No-op when DotKt.Runtime isn't referenced (attrs unresolved).
            if (ti.IsFileClass) ApplyKotlinFileClass(ti.TB);
            if (ti.Def.TryGetProperty("methods", out var kms))
                foreach (var m in kms.EnumerateArray())
                {
                    int kf = 0;
                    if (m.TryGetProperty("infix", out var inf) && inf.GetBoolean()) kf |= 1;       // KotlinFunctionFlags.Infix
                    if (m.TryGetProperty("operator", out var op) && op.GetBoolean()) kf |= 2;       // .Operator
                    if (m.TryGetProperty("suspend", out var su) && su.GetBoolean()) kf |= 4;        // .Suspend
                    bool inl = m.TryGetProperty("inline", out var il) && il.GetBoolean();
                    // Nullability mask: bit 0 = return nullable, bit (i+1) = param i nullable.
                    uint nmask = 0;
                    if (m.TryGetProperty("retNullable", out var rn) && rn.GetBoolean()) nmask |= 1u;
                    if (m.TryGetProperty("params", out var nps)) { int pi = 0; foreach (var p in nps.EnumerateArray()) { if (p.TryGetProperty("nullable", out var pn) && pn.GetBoolean()) nmask |= 1u << (pi + 1); pi++; } }
                    if (kf == 0 && !inl && nmask == 0) continue;
                    var name = m.GetProperty("name").GetString();
                    if (!ti.MethodsBySig.TryGetValue(SigKey(name, m), out var mb) && !ti.Methods.TryGetValue(name, out mb)) continue;
                    if (kf != 0) ApplyKotlinFunction(mb, kf);
                    // [KotlinInline(body)]: carry this inline+lambda fn's BIR (params + body) so a consumer can splice it.
                    if (inl) ApplyKotlinInline(mb, "{\"params\":" + m.GetProperty("params").GetRawText() + ",\"body\":" + m.GetProperty("body").GetRawText() + "}");
                    // Nullable RETURN -> [Nullable(2)] on the return parameter (position 0; param nullability is stamped
                    // by DefineParamNames, which owns the parameter builders). The type's [NullableContext(1)] is the
                    // non-null default, so only the nullable positions need an override.
                    if ((nmask & 1u) != 0) ApplyNullable(mb.DefineParameter(0, ParameterAttributes.None, null));
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
            foreach (var f in inits) { PrescanCfgLabels(f.GetProperty("init")); EmitExpr(f.GetProperty("init")); _il.Emit(OpCodes.Stsfld, ti.Fields[f.GetProperty("name").GetString()]); }
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

    void DeclareMethod(TypeInfo ti, JsonElement m, bool isStatic)
    {
        var name = m.GetProperty("name").GetString();
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

        // A `suspend fun f(args): T` -> a kickoff `Task<T> f(args)` (Unit -> Task). The state machine that drives
        // it is synthesized in EmitCoroutine when the body is emitted. ABI: suspend <=> Task<T> (coroutine-abi).
        if (m.TryGetProperty("suspend", out var su) && su.GetBoolean())
        {
            var rs = m.GetProperty("resultType").GetString();
            var sTps = m.TryGetProperty("typeParams", out var stp) && stp.GetArrayLength() > 0 ? (JsonElement?)stp : null;
            MethodBuilder smb;
            if (sTps != null)
            {
                // Generic `suspend fun <T>`: define the kickoff's type params first so `Task<T>`/param types resolve;
                // EmitCoroutineClass then builds a generic state-machine TYPE mirroring them. (Generic suspend funs
                // always take the class form — see BirEmitter.suspendMethod.)
                var gn = TpNames(sTps.Value);
                smb = ti.TB.DefineMethod(name, attrs);
                var gps = smb.DefineGenericParameters(gn);
                var map = new Dictionary<string, GenericTypeParameterBuilder>();
                for (int gi = 0; gi < gn.Length; gi++) map[gn[gi]] = gps[gi];
                _methodTypeParams[smb] = map;
                _curMethodParams = map;
                ApplyConstraints(sTps.Value, map, false);
                var sps2 = m.GetProperty("params").EnumerateArray().Select(p => MapType(p.GetProperty("type").GetString())).ToArray();
                smb.SetParameters(sps2);
                smb.SetReturnType(rs == "void" ? typeof(System.Threading.Tasks.Task) : typeof(System.Threading.Tasks.Task<>).MakeGenericType(MapType(rs)));
                _curMethodParams = null;
                ti.Methods[name] = smb; ti.MethodsBySig[SigKey(name, m)] = smb;
                _mparams[smb] = sps2;
                DefineParamNames(smb, m);
                return;
            }
            var taskRet = rs == "void" ? typeof(System.Threading.Tasks.Task)
                : typeof(System.Threading.Tasks.Task<>).MakeGenericType(MapType(rs));
            var sps = m.GetProperty("params").EnumerateArray().Select(p => MapType(p.GetProperty("type").GetString())).ToArray();
            smb = ti.TB.DefineMethod(name, attrs, taskRet, sps);
            ti.Methods[name] = smb; ti.MethodsBySig[SigKey(name, m)] = smb;
            _mparams[smb] = sps;
            DefineParamNames(smb, m);
            return;
        }

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
            ps = m.GetProperty("params").EnumerateArray().Select(p => MapType(p.GetProperty("type").GetString())).ToArray();
            mb.SetParameters(ps);
            mb.SetReturnType(MapType(m.GetProperty("ret").GetString()));
            _curMethodParams = null;
        }
        else
        {
            ps = m.GetProperty("params").EnumerateArray().Select(p => MapType(p.GetProperty("type").GetString())).ToArray();
            mb = ti.TB.DefineMethod(name, attrs, MapType(m.GetProperty("ret").GetString()), ps);
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
            _il.Emit(OpCodes.Call, SelectCtor(ti, ta.GetArrayLength()));
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
            // `: base(...)` -> the Kotlin-user base class's primary ctor.
            foreach (var a in ba2.EnumerateArray()) EmitExpr(a);
            _il.Emit(OpCodes.Call, _types[ti.BaseName].Ctor);
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

    void EmitMethodBody(TypeInfo ti, JsonElement m)
    {
        // An abstract method has no IL body (subclasses provide it); GetILGenerator would throw.
        if (m.TryGetProperty("abstract", out var amb) && amb.GetBoolean()) return;
        var mname = m.GetProperty("name").GetString();
        // Pick THIS def's own MethodBuilder by signature (overloads share `mname`; the name-keyed map holds only the
        // last, so emitting by name alone routes a body into the wrong overload — the WinUI `text(String)` /
        // `text(()->String)` bug).
        var mb = ti.MethodsBySig.TryGetValue(SigKey(mname, m), out var bm) ? bm : ti.Methods[mname];
        _methodRetType = mb.ReturnType;
        _curTypeParams = EffectiveTps(ti);
        _curMethodParams = _methodTypeParams.TryGetValue(mb, out var mp) ? mp : null;
        if (m.TryGetProperty("suspend", out var su) && su.GetBoolean())
        {
            // DOTKT_STDLIB_COMPILE: the CLR coroutine BRIDGE (TypedCont / Builders / COROUTINE_SUSPENDED, etc.) is not
            // yet ported into the stdlib — the port is planned (docs/coroutine-stdlib-port-plan.md) but PENDING. So a
            // suspend fun is emitted as a throwing-stub IL: it depends only on the stdlib and throws when called. This
            // keeps the stdlib assembly emitting so the (non-coroutine) load problems can be worked on. Replace with the
            // real state machine at port time (the seam is the `Co*` consts + EmitCoroutine).
            if (StdlibStub) { EmitThrowStub(mb, "suspend (coroutine port pending)"); return; }
            if (m.TryGetProperty("coClass", out var cc) && cc.GetBoolean()) EmitCoroutineClass(ti, mb, m);
            else EmitCoroutine(ti, mb, m);
            return;
        }
        // A non-suspend method that builds a `sequence { }` (`sequenceNew` -> the restricted-suspension SM binding
        // Seq/ISeqStep, also part of the pending coroutine port) -> the same throwing stub under stdlib-compile.
        if (StdlibStub && m.GetRawText().Contains("sequenceNew")) { EmitThrowStub(mb, "sequence builder (coroutine port pending)"); return; }
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
            _argTypes[pn] = MapType(p.GetProperty("type").GetString());
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

    void StoreVar(string name)
    {
        if (_locals.TryGetValue(name, out var l)) _il.Emit(OpCodes.Stloc, l);
        else if (_args.TryGetValue(name, out var a)) _il.Emit(OpCodes.Starg, a);
        else throw new NotSupportedException("store unknown var " + name);
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
                var types = cs.EnumerateArray().Select(c => MapType(c.GetString())).ToList();
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

    // Substitute the interface's type params into a BIR type string ({gp:T -> uint}); simple token replace (type-param
    // names are short, e.g. T/K/V/E), enough for `gp:T` and `X[gp:T]` forms.
    static string SubstSig(string sig, Dictionary<string, string> subst)
    {
        foreach (var kv in subst) sig = sig.Replace(kv.Key, kv.Value);
        return sig;
    }

    // Emit a covariant-return bridge: a private explicit-interface-impl method with the iface's (base) return type +
    // params, calling the narrow body method on `this` and returning it (a ref upcast); the MethodImpl goes on the bridge.
    void EmitCovariantBridge(TypeInfo ti, string name, JsonElement imDef, Dictionary<string, string> subst, MethodBuilder body, MethodInfo ifaceMethod, Type ifaceRet)
    {
        var paramTypes = imDef.GetProperty("params").EnumerateArray()
            .Select(p => MapType(SubstSig(p.GetProperty("type").GetString(), subst))).ToArray();
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
    void TryEmitDimForwardBridge(TypeInfo ti, JsonElement imDef, Dictionary<string, string> ifSubst, string subSig, Type constructed, MethodBuilder ifaceBuilder)
    {
        if (ti.Def.ValueKind != JsonValueKind.Object || !ti.Def.TryGetProperty("interfaces", out var dirIfs)) return;
        foreach (var di in dirIfs.EnumerateArray())
        {
            var (dopen, _) = ParseOwner(di.GetString());
            if (!_types.TryGetValue(dopen, out var diTi) || !diTi.MethodsBySig.TryGetValue(subSig, out var dim)) continue;
            if (dim.Attributes.HasFlag(MethodAttributes.Abstract)) continue;   // need an actual DEFAULT (bodied) method
            Type ifaceRet; try { ifaceRet = imDef.TryGetProperty("ret", out var rt) ? MapType(SubstSig(rt.GetString(), ifSubst)) : typeof(void); } catch { return; }
            Type[] paramTypes; try { paramTypes = imDef.GetProperty("params").EnumerateArray().Select(p => MapType(SubstSig(p.GetProperty("type").GetString(), ifSubst))).ToArray(); } catch { return; }
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

    // {gp:T -> uint, ...} mapping the open interface's type params to a constructed spec's BIR type args
    // (`kotlin.Comparable[uint]` + typeParams ['T'] -> {gp:T: uint}), for resolving overloaded overrides by signature.
    Dictionary<string, string> BuildTypeArgSubst(string spec, TypeInfo iface)
    {
        var subst = new Dictionary<string, string>();
        var br = spec.IndexOf('[');
        if (br < 0 || iface.Def.ValueKind != JsonValueKind.Object || !iface.Def.TryGetProperty("typeParams", out var tps)) return subst;
        var args = SplitTopLevel(spec.Substring(br + 1, spec.Length - br - 2)).ToList();
        int k = 0;
        foreach (var tp in tps.EnumerateArray())
        {
            var nm = tp.ValueKind == JsonValueKind.String ? tp.GetString() : (tp.TryGetProperty("name", out var n) ? n.GetString() : null);
            if (nm != null && k < args.Count) subst["gp:" + nm] = args[k].Trim();
            k++;
        }
        return subst;
    }

    // The body method whose TYPE-ARG-SUBSTITUTED signature matches the interface method `name` (handles overloaded names
    // like compareTo). Null if no exact match -> caller falls back to the name-keyed ti.Methods[name].
    MethodBuilder ResolveOverload(TypeInfo ti, TypeInfo iface, string name, Dictionary<string, string> subst)
    {
        if (iface.Def.ValueKind != JsonValueKind.Object || !iface.Def.TryGetProperty("methods", out var ms)) return null;
        foreach (var m in ms.EnumerateArray())
        {
            if (!m.TryGetProperty("name", out var mn) || mn.GetString() != name) continue;
            var sig = string.Join(",", m.GetProperty("params").EnumerateArray().Select(p =>
            {
                var t = p.GetProperty("type").GetString();
                return subst.TryGetValue(t, out var s) ? s : t;
            }));
            if (ti.MethodsBySig.TryGetValue(name + "(" + sig + ")", out var mb)) return mb;
        }
        return null;
    }

    (string open, Type constructed) ParseOwner(string spec)
    {
        var br = spec.IndexOf('[');
        if (br < 0) return (spec, null);
        var open = spec.Substring(0, br);
        var args = SplitTopLevel(spec.Substring(br + 1, spec.Length - br - 2)).Select(MapType).ToArray();
        return (open, _types[open].TB.MakeGenericType(args));
    }

    // The constructed type's GetX helpers return members whose declared types are still the OPEN params (`!0`);
    // substitute a type-level param by position to its concrete arg so callers box value types correctly.
    // A value type OR a generic parameter must be boxed to become an `object` — a generic param's runtime type is
    // unknown (could be a value type), and `box !!0` is legal/correct for both value and reference instantiations.
    static bool NeedsBoxToRef(Type t) => t != null && (t.IsValueType || t.IsGenericParameter);

    static Type Subst(Type t, Type[] typeArgs) =>
        t != null && t.IsGenericParameter && t.DeclaringMethod == null && t.GenericParameterPosition < typeArgs.Length
            ? typeArgs[t.GenericParameterPosition] : t;

    // Resolve a field for emit; out-param gives the substituted (concrete) field type for boxing decisions.
    FieldInfo ResolveField(string spec, string name, out Type fieldType)
    {
        var (open, constructed) = ParseOwner(spec);
        var fb = FindField(open, name);
        if (constructed == null) { fieldType = fb.FieldType; return fb; }
        fieldType = Subst(fb.FieldType, constructed.GetGenericArguments());
        return TypeBuilder.GetField(constructed, fb);
    }

    // Resolve a method for emit; out-param gives the substituted (concrete) return type for boxing decisions.
    MethodInfo ResolveMethod(string spec, string name, out Type retType, string sig = null)
    {
        var (open, constructed) = ParseOwner(spec);
        var mb = FindMethod(open, name, sig);
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
            // (mb on a base) throws — fall back to the open MethodBuilder there (the pre-existing behavior).
            try { return TypeBuilder.GetMethod(constructed, mb); }
            catch (ArgumentException) { return mb; }
        }
        retType = Subst(mb.ReturnType, constructed.GetGenericArguments());
        // An INHERITED method on a NON-self constructed generic (mb declared on a base/interface, not on `constructed`'s
        // own generic def — e.g. AbstractMutableCollection<E> calling the inherited iterator()) throws the same
        // "method must be declared on the generic type definition" as the self case -> fall back to the open MethodBuilder.
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
    // construct the declaring one with `typeArgs` (the SHARED type parameters of a generic interface chain like
    // IList<T> : ICollection<T> : IEnumerable<T>) and re-anchor the method onto it. Null if not found.
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
            if (biOpen.GetGenericArguments().Length != typeArgs.Length) continue;   // shared-arity chains only
            var bom = biOpen.GetMethods(flags).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == argc);
            if (bom == null) continue;
            var biCon = biOpen.MakeGenericType(typeArgs);
            return IsTbInstantiation(biCon) ? TypeBuilder.GetMethod(biCon, bom)
                : biCon.GetMethods(flags).First(m => m.Name == name && m.GetParameters().Length == argc);
        }
        return null;
    }

    // A call to a generic method `fun <T> id(x:T)` carries `typeArgs` -> instantiate it (MakeGenericMethod).
    // `retType`/`paramTypes` give the SUBSTITUTED (concrete) signature, since the instantiation's own reflection
    // still reports `!!0` (and throws pre-bake) — needed so value args to `object`/concrete params get boxed.
    // Set by build-clr-stdlib.sh: while compiling the pure-kotlin stdlib, methods the backend can't yet emit are
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
        var ps = _mparams.TryGetValue(m, out var p) ? p : null;
        if (e.TryGetProperty("typeArgs", out var ta) && ta.GetArrayLength() > 0)
        {
            var targs = ta.EnumerateArray().Select(x => MapType(x.GetString())).ToArray();
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
                    retType = sub.TryGetValue(openMb.ReturnType, out var or) ? or : m.ReturnType;
                    paramTypes = ps;
                    return openMb.MakeGenericMethod(targs);
                }
            }
            retType = sub.TryGetValue(m.ReturnType, out var r) ? r : m.ReturnType;
            paramTypes = ps?.Select(x => sub.TryGetValue(x, out var s) ? s : x).ToArray();
            return m.MakeGenericMethod(targs);
        }
        retType = m.ReturnType;
        paramTypes = ps;
        return m;
    }

    // Emit call args, boxing each value arg passed to a reference/object param (param types known explicitly).
    void EmitArgsTyped(JsonElement args, Type[] pt)
    {
        int i = 0;
        foreach (var a in args.EnumerateArray()) { if (pt != null && i < pt.Length) EmitArg(a, pt[i]); else EmitExpr(a); i++; }
    }

    // Prefer a BIR-carried concrete result type (`retType`) over reflecting an un-baked builder's `!0`/`!!0`.
    Type RetOr(JsonElement e, Type fallback) =>
        e.TryGetProperty("retType", out var r) ? MapType(r.GetString()) : fallback;


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
                if (_coThis != null) { _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, _coThis); }   // instance coroutine: captured receiver
                else _il.Emit(OpCodes.Ldarg_0);
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
    FieldBuilder FindField(string typeName, string name)
    {
        for (var ti = _types[typeName]; ti != null; ti = ti.BaseName != null && _types.ContainsKey(ti.BaseName) ? _types[ti.BaseName] : null)
            if (ti.Fields.TryGetValue(name, out var f)) return f;
        throw new NotSupportedException($"field {typeName}.{name} not found");
    }

    // name + parameter-type signature -> the overload key. `m` is a method DEF (or a call carrying "sig"); the param
    // types are the BIR `type` strings, which match across def and call (same birType of the same function's params).
    static string SigKey(string name, JsonElement methodDef) =>
        name + "(" + string.Join(",", methodDef.GetProperty("params").EnumerateArray().Select(p => p.GetProperty("type").GetString())) + ")";
    static string SigKey(string name, string sig) => name + "(" + sig + ")";

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
                var (open, _) = ParseOwner(spec);
                if (!seenIfaces.Add(open) || !_types.TryGetValue(open, out var iti)) continue;
                if (sig != null && iti.MethodsBySig.TryGetValue(SigKey(name, sig), out var ms)) return ms;
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
            var ext = TryResolveType(typeName);
            if (ext == null) return null;
            var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            try { return ext.GetMethod(name, bf); }
            catch (AmbiguousMatchException) { return ext.GetMethods(bf).FirstOrDefault(mm => mm.Name == name); }
        }
        for (var ti = _types[typeName]; ti != null; ti = ti.BaseName != null && _types.ContainsKey(ti.BaseName) ? _types[ti.BaseName] : null)
        {
            if (sig != null && ti.MethodsBySig.TryGetValue(SigKey(name, sig), out var ms)) return ms;
            if (sig != null && UniqueGenericOverload(ti, name) is { } gm) return gm;
            if (ti.Methods.TryGetValue(name, out var m)) return m;
            var im = FindInInterfaces(ti);
            if (im != null) return im;
        }
        throw new NotSupportedException($"method {typeName}.{name} not found");
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

    Type EmitConst(JsonElement e)
    {
        var t = e.GetProperty("type").GetString();
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
        switch (op)
        {
            case "+": _il.Emit(OpCodes.Add); return lt;
            case "-": _il.Emit(OpCodes.Sub); return lt;
            case "*": _il.Emit(OpCodes.Mul); return lt;
            case "/": _il.Emit(isUns ? OpCodes.Div_Un : OpCodes.Div); return lt;
            case "%": _il.Emit(isUns ? OpCodes.Rem_Un : OpCodes.Rem); return lt;
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
            case "<=": _il.Emit(OpCodes.Cgt); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Ceq); return typeof(bool);
            case ">=": _il.Emit(OpCodes.Clt); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Ceq); return typeof(bool);
            default: throw new NotSupportedException("bin " + op);
        }
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

    // Numeric conversion (`x.toLong()` etc.) -> a CIL conv opcode; returns the target CLR type.
    Type EmitConv(JsonElement e)
    {
        EmitExpr(e.GetProperty("e"));
        switch (e.GetProperty("to").GetString())
        {
            case "int": _il.Emit(OpCodes.Conv_I4); return typeof(int);
            case "long": _il.Emit(OpCodes.Conv_I8); return typeof(long);
            case "double": _il.Emit(OpCodes.Conv_R8); return typeof(double);
            case "float": _il.Emit(OpCodes.Conv_R4); return typeof(float);
            case "short": _il.Emit(OpCodes.Conv_I2); return typeof(short);
            case "byte": _il.Emit(OpCodes.Conv_I1); return typeof(sbyte);
            case "char": _il.Emit(OpCodes.Conv_U2); return typeof(char);
            default: throw new NotSupportedException("conv " + e.GetProperty("to").GetString());
        }
    }

    Type EmitNativeClrIsInst(JsonElement e, bool resultIsBool)
    {
        EmitExpr(e.GetProperty("e"));
        var t = NativeType(e.GetProperty("type").GetString());
        _il.Emit(OpCodes.Isinst, t);
        if (!resultIsBool) return typeof(object);
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Cgt_Un);
        return typeof(bool);
    }

    Type EmitNativeClrCastClass(JsonElement e)
    {
        EmitExpr(e.GetProperty("e"));
        var t = NativeType(e.GetProperty("type").GetString());
        _il.Emit(t.IsValueType || t.IsGenericParameter ? OpCodes.Unbox_Any : OpCodes.Castclass, t);   // castclass is invalid for value-type/generic-param instantiations
        return t;
    }

    Type EmitNativeClrSafeCastValue(JsonElement e)
    {
        // `x as? T` for value T -> `T?`: isinst boxed-T, then unbox+wrap, else empty Nullable<T>.
        var elem = NativeType(e.GetProperty("elem").GetString());
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
        var elem = NativeType(e.GetProperty("elem").GetString());
        var nt = typeof(Nullable<>).MakeGenericType(elem);
        var loc = _il.DeclareLocal(nt);
        _il.Emit(OpCodes.Ldloca, loc);
        _il.Emit(OpCodes.Initobj, nt);
        _il.Emit(OpCodes.Ldloc, loc);
        return nt;
    }

    Type EmitNativeClrNullableWrap(JsonElement e)
    {
        var elem = NativeType(e.GetProperty("elem").GetString());
        var nt = typeof(Nullable<>).MakeGenericType(elem);
        EmitExpr(e.GetProperty("e"));
        _il.Emit(OpCodes.Newobj, nt.GetConstructor(new[] { elem }));
        return nt;
    }

    Type EmitNativeClrNullableHasValue(JsonElement e)
    {
        var elem = NativeType(e.GetProperty("elem").GetString());
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
        var elem = NativeType(e.GetProperty("elem").GetString());
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
        var t = NativeType(e.GetProperty("type").GetString());
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
        return NativeType(e.GetProperty("type").GetString());
    }

    Type EmitNativeClrEnumOrdinal(JsonElement e)
    {
        EmitExpr(e.GetProperty("e"));
        _il.Emit(OpCodes.Conv_I4);
        return typeof(int);
    }

    Type EmitNativeClrEnumValues(JsonElement e)
    {
        var et = NativeType(e.GetProperty("type").GetString());
        _il.Emit(OpCodes.Ldtoken, et);
        _il.Emit(OpCodes.Call, typeof(Type).GetMethod("GetTypeFromHandle"));
        _il.Emit(OpCodes.Call, typeof(Enum).GetMethod("GetValues", new[] { typeof(Type) }));
        _il.Emit(OpCodes.Castclass, et.MakeArrayType());
        return et.MakeArrayType();
    }

    Type EmitNativeClrEnumParse(JsonElement e)
    {
        var et = NativeType(e.GetProperty("type").GetString());
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
        var elem = MapType(e.GetProperty("elem").GetString());
        var elems = e.GetProperty("elems").EnumerateArray().ToList();
        _il.Emit(OpCodes.Ldc_I4, elems.Count);
        _il.Emit(OpCodes.Newarr, elem);
        for (int i = 0; i < elems.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            var et = EmitExpr(elems[i]);
            // Box a value element stored into a reference array (e.g. ints into `object[]` for String.Format).
            if (et != null && NeedsBoxToRef(et) && !elem.IsValueType && !elem.IsGenericParameter) _il.Emit(OpCodes.Box, et);
            _il.Emit(OpCodes.Stelem, elem);
        }
        return elem.MakeArrayType();
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
        return got;
    }

    Type EmitCond(JsonElement e)
    {
        // A value-type-nullable if/when (`Int?`) tags its result type so each branch's `T`/`null` coerces to Nullable<T>.
        Type want = null;
        if (e.TryGetProperty("type", out var tt)) { try { want = ClrRef(tt.GetString()); } catch { } }
        var elseL = _il.DefineLabel(); var end = _il.DefineLabel();
        EmitExpr(e.GetProperty("cond")); _il.Emit(OpCodes.Brfalse, elseL);
        var t = EmitNullableCoerced(e.GetProperty("then"), want); _il.Emit(OpCodes.Br, end);
        _il.MarkLabel(elseL); EmitNullableCoerced(e.GetProperty("else"), want); _il.MarkLabel(end);
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
        if (t == null) throw new NotSupportedException("cannot resolve .NET type " + name);
        _typeCache[name] = t;
        return t;
    }

    Type EmitClrNew(JsonElement e)
    {
        var type = ClrRef(e.GetProperty("type").GetString());
        var argTypes = e.GetProperty("argTypes").EnumerateArray().Select(a => { try { return ClrRef(a.GetString()); } catch { return (Type)null; } }).ToArray();
        var args = e.GetProperty("args");
        // `new List<R>()` where R is the enclosing generic FUNCTION's type parameter: List<R> is a
        // TypeBuilderInstantiation whose .GetConstructor/.GetConstructors throw — resolve the ctor on the open generic
        // definition (its params are non-generic for the cases we hit: no-arg, capacity), emit the args against those
        // params, and re-anchor via TypeBuilder.GetConstructor. (Mirrors GenericMethod for member access.)
        if (IsTbInstantiation(type))
        {
            var openDef = type.GetGenericTypeDefinition();
            var openCtor = (argTypes.All(t => t != null) ? openDef.GetConstructor(argTypes) : null)
                // ABI substitution (@Clr concrete collections, ArrayList->System.List): a Kotlin arg type doesn't EXACTLY
                // match the BCL ctor param (Collection->IReadOnlyCollection vs the List(IEnumerable<T>) ctor). Fall back to
                // arity + structural assignability (IReadOnlyCollection IS IEnumerable).
                ?? PickOpenCtor(openDef, argTypes, args.GetArrayLength())
                ?? throw new NotSupportedException($"no matching ctor on the open def of {type.FullName} with {args.GetArrayLength()} arg(s)");
            EmitArgs(args, openCtor.GetParameters());
            _il.Emit(OpCodes.Newobj, TypeBuilder.GetConstructor(type, openCtor));
            return type;
        }
        // Exact match first; else fall back to arity-based selection. The latter matters when a lambda arg's type was
        // erased to `object` by the façade (the param is really a delegate, e.g. `new Thread(ThreadStart)`): the real
        // ctor param type is recovered here so EmitArg can build the specific delegate.
        var ci = (argTypes.All(t => t != null) ? type.GetConstructor(argTypes) : null) ?? PickClrCtor(type, args);
        if (ci == null) throw new NotSupportedException($"no matching constructor for {type.FullName} with {args.GetArrayLength()} arg(s)");
        EmitArgs(args, ci.GetParameters());
        _il.Emit(OpCodes.Newobj, ci);
        return type;
    }

    Type EmitNativeClrNewObj(JsonElement e)
    {
        var member = e.GetProperty("memberRef");
        var type = ClrRef(NativeOwnerSpec(e, member));
        var argTypes = NativeParameterTypes(member);
        var args = e.GetProperty("args");
        var ci = (argTypes.All(t => t != null) ? type.GetConstructor(argTypes) : null) ?? PickClrCtor(type, args);
        if (ci == null) throw new NotSupportedException($"native CIR: no matching constructor for {type.FullName} with {args.GetArrayLength()} arg(s)");
        EmitArgs(args, ci.GetParameters());
        _il.Emit(OpCodes.Newobj, ci);
        return type;
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
        var typeName = e.GetProperty("type").GetString();
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

    Type EmitClrCall(JsonElement e, bool instance, bool deref = true)
    {
        // `ClrRef` (not `ResolveType`) so a method on a constructed generic .NET type (`Collection<int>`) resolves.
        var type = ClrRef(e.GetProperty("type").GetString());
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
            // Fall back to name + arity — e.g. a generic-parameter arg type (`Add(T)` on `Collection<int>`) that
            // doesn't name a plain .NET type; on the constructed type GetMethods returns the substituted overload.
            mi ??= type.GetMethods(flags).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == argSpecs.Count);
        }
        catch (NotSupportedException) { }
        // A constructed generic type whose arg is an emitted generic parameter (TypeBuilderInstantiation) refuses
        // reflection — re-anchor the open definition's method onto the constructed type via TypeBuilder.GetMethod.
        if (mi == null && type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            var open = type.GetGenericTypeDefinition();
            var typeArgs = type.GetGenericArguments();
            var om = open.GetMethods(flags).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == argSpecs.Count);
            if (om != null) mi = TypeBuilder.GetMethod(type, om);
            // An inherited INTERFACE member (`IList<T>.Add` lives on the base `ICollection<T>`): interface GetMethods
            // doesn't include base-interface methods, so walk the (transitively-flattened) base interfaces, find the
            // declaring one, construct it with this type's args (shared type parameters) and re-anchor. See item 3.
            else mi = ResolveInheritedIfaceMethod(open, typeArgs, name, argSpecs.Count, flags);
        }
        // Last resort: a UNIQUELY-named method (covers e.g. a `params`/vararg method called with one array arg whose
        // static argType — `object` — didn't match the `T[]` param, so neither exact nor arity resolution hit).
        if (mi == null)
        {
            // A generic TypeBuilder instantiation throws on GetMethods — enumerate the open def + re-anchor via GetMethod.
            MethodInfo[] all; bool reanchor = false;
            try { all = type.GetMethods(flags); }
            catch (NotSupportedException) { all = type.GetGenericTypeDefinition().GetMethods(flags); reanchor = true; }
            var named = all.Where(m => m.Name == name).ToList();
            if (named.Count == 1) mi = reanchor ? TypeBuilder.GetMethod(type, named[0]) : named[0];
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
        if (mi == null && instance && e.TryGetProperty("recv", out _)) return EmitDynamicCall(e);
        if (mi == null) throw new NotSupportedException($"clrInstance method not resolved: {type}.{name}/{argSpecs.Count}");
        // A value-type receiver's instance method needs a managed pointer (e.g. struct Vec2.Mag2()).
        if (instance)
        {
            if (type.IsValueType) EmitAddr(e.GetProperty("recv"));
            else { EmitExpr(e.GetProperty("recv")); if (arrayCloneFallback && !typeof(System.Array).IsAssignableFrom(type)) _il.Emit(OpCodes.Castclass, typeof(System.Array)); }
        }
        EmitArgs(e.GetProperty("args"), mi.GetParameters());
        _il.Emit(instance && mi.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, mi);
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

    Type EmitNativeClrCall(JsonElement e)
    {
        var member = e.GetProperty("memberRef");
        var type = ClrRef(NativeOwnerSpec(e, member));
        var name = member.GetProperty("name").GetString();
        var dispatch = e.TryGetProperty("dispatch", out var disp) && disp.ValueKind == JsonValueKind.String
            ? disp.GetString()
            : (member.TryGetProperty("isStatic", out var st) && st.GetBoolean() ? "static" : "instance");
        var instance = dispatch == "instance";
        var flags = BindingFlags.Public | (instance ? BindingFlags.Instance : BindingFlags.Static);
        var argTypes = NativeParameterTypes(member);
        var methodTypeArgs = e.TryGetProperty("typeArgs", out var ta) && ta.ValueKind == JsonValueKind.Array
            ? ta.EnumerateArray().Select(a => NativeType(a.GetString())).ToArray()
            : Array.Empty<Type>();
        MethodInfo mi = null;
        try
        {
            if (methodTypeArgs.Length > 0)
            {
                mi = ResolveNativeGenericMethod(type, name, flags, argTypes, methodTypeArgs);
            }
            else if (argTypes.All(x => x != null))
            {
                mi = type.GetMethod(name, flags, null, argTypes, null);
            }
            mi ??= type.GetMethods(flags).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == argTypes.Length && !m.IsGenericMethodDefinition);
        }
        catch (NotSupportedException) { }
        if (mi == null)
        {
            var named = type.GetMethods(flags).Where(m => m.Name == name).ToList();
            if (named.Count == 1) mi = named[0];
        }
        if (mi == null)
            throw new NotSupportedException($"native CIR: method {type.FullName}.{name}/{argTypes.Length} not found");

        if (instance)
        {
            if (type.IsValueType) EmitAddr(e.GetProperty("recv"));
            else EmitExpr(e.GetProperty("recv"));
        }
        EmitArgs(e.GetProperty("args"), mi.GetParameters());
        _il.Emit(instance && mi.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, mi);
        if (mi.ReturnType.IsByRef)
        {
            var elem = mi.ReturnType.GetElementType();
            _il.Emit(OpCodes.Ldobj, elem);
            return elem;
        }
        return mi.ReturnType;
    }

    MethodInfo ResolveNativeGenericMethod(Type type, string name, BindingFlags flags, Type[] argTypes, Type[] methodTypeArgs)
    {
        var candidates = type.GetMethods(flags)
            .Where(m => m.Name == name &&
                        m.IsGenericMethodDefinition &&
                        m.GetGenericArguments().Length == methodTypeArgs.Length &&
                        m.GetParameters().Length == argTypes.Length)
            .ToList();
        if (candidates.Count == 0) return null;

        foreach (var candidate in candidates)
        {
            var ps = candidate.GetParameters();
            var ok = true;
            for (var i = 0; i < ps.Length; i++)
            {
                if (argTypes[i] == null) continue;
                var expected = SubstituteMethodGenericParameter(ps[i].ParameterType, methodTypeArgs);
                if (expected != argTypes[i])
                {
                    ok = false;
                    break;
                }
            }
            if (ok) return candidate.MakeGenericMethod(methodTypeArgs);
        }

        return candidates.Count == 1 ? candidates[0].MakeGenericMethod(methodTypeArgs) : null;
    }

    static Type SubstituteMethodGenericParameter(Type type, Type[] methodTypeArgs)
    {
        if (type.IsGenericParameter && type.DeclaringMethod != null)
            return methodTypeArgs[type.GenericParameterPosition];
        if (type.IsByRef)
            return SubstituteMethodGenericParameter(type.GetElementType(), methodTypeArgs).MakeByRefType();
        if (type.IsArray)
            return SubstituteMethodGenericParameter(type.GetElementType(), methodTypeArgs).MakeArrayType();
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            var args = type.GetGenericArguments().Select(a => SubstituteMethodGenericParameter(a, methodTypeArgs)).ToArray();
            return type.GetGenericTypeDefinition().MakeGenericType(args);
        }
        return type;
    }

    Type EmitNativeClrFieldGet(JsonElement e, bool isStatic)
    {
        var member = e.GetProperty("memberRef");
        var type = ClrRef(NativeOwnerSpec(e, member));
        var name = member.GetProperty("name").GetString();
        var flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
        var fld = type.GetField(name, flags)
            ?? throw new NotSupportedException($"native CIR: field {type.FullName}.{name} not found");
        if (fld.IsLiteral) return EmitLiteralValue(fld.GetRawConstantValue(), fld.FieldType);
        if (!isStatic && !fld.IsStatic)
        {
            if (type.IsValueType) EmitAddr(e.GetProperty("recv"));
            else EmitExpr(e.GetProperty("recv"));
        }
        _il.Emit(fld.IsStatic ? OpCodes.Ldsfld : OpCodes.Ldfld, fld);
        return fld.FieldType;
    }

    Type EmitNativeClrFieldSet(JsonElement e, bool isStatic)
    {
        var member = e.GetProperty("memberRef");
        var type = ClrRef(NativeOwnerSpec(e, member));
        var name = member.GetProperty("name").GetString();
        var flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
        var fld = type.GetField(name, flags)
            ?? throw new NotSupportedException($"native CIR: field {type.FullName}.{name} not found");
        if (!isStatic && !fld.IsStatic)
        {
            if (type.IsValueType) EmitAddr(e.GetProperty("recv"));
            else EmitExpr(e.GetProperty("recv"));
        }
        EmitNullableCoerced(e.GetProperty("value"), fld.FieldType);
        _il.Emit(fld.IsStatic ? OpCodes.Stsfld : OpCodes.Stfld, fld);
        return typeof(void);
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
        // Inherited interface property (`ICollection<T>.Count` accessed on `IList<T>`): interface GetProperty
        // doesn't traverse base interfaces, so walk them and re-anchor (mirrors ResolveInheritedIfaceMethod).
        var typeArgs = type.GetGenericArguments();
        foreach (var bi in open.GetInterfaces())
        {
            var biOpen = bi.IsGenericType ? bi.GetGenericTypeDefinition() : bi;
            var bp = biOpen.GetProperty(name);
            var acc = getter ? bp?.GetGetMethod() : bp?.GetSetMethod();
            if (acc == null) continue;
            if (!bi.IsGenericType) return acc;
            if (biOpen.GetGenericArguments().Length != typeArgs.Length) continue;
            var biCon = biOpen.MakeGenericType(typeArgs);
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
        var typeName = e.GetProperty("type").GetString();
        var propName = e.GetProperty("name").GetString();
        var type = ClrRef(typeName);
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
                _il.Emit(gm.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, gm);
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
        _il.Emit(getter.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, getter);
        return getter.ReturnType;
    }

    Type EmitClrPropSet(JsonElement e)
    {
        var typeName = e.GetProperty("type").GetString();
        var propName = e.GetProperty("name").GetString();
        var type = ClrRef(typeName);
        var isStatic = e.GetProperty("static").GetBoolean();
        var setter = PropAccessor(type, propName, getter: false);
        if (setter == null)
        {
            // A DotKt custom-accessor property's `set_<name>` METHOD (no PropertyDef) -> call it.
            var sm = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .FirstOrDefault(mm => mm.Name == "set_" + propName && mm.GetParameters().Length == 1);
            if (sm != null)
            {
                if (!isStatic && !sm.IsStatic) EmitExpr(e.GetProperty("recv"));
                EmitArgs2(new[] { e.GetProperty("value") }, sm.GetParameters());
                _il.Emit(sm.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, sm);
                return typeof(void);
            }
            // A writable .NET FIELD surfaced as a Kotlin (mutable) property -> field store.
            var fld = type.GetField(propName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"ilemit: no writable property OR field '{propName}' on .NET type '{type}' (spec '{typeName}'). Available properties: [{PropList(type)}]");
            if (!isStatic && !fld.IsStatic) EmitExpr(e.GetProperty("recv"));
            EmitNullableCoerced(e.GetProperty("value"), fld.FieldType);
            _il.Emit(fld.IsStatic ? OpCodes.Stsfld : OpCodes.Stfld, fld);
            return typeof(void);
        }
        if (!isStatic) EmitExpr(e.GetProperty("recv"));
        EmitArgs2(new[] { e.GetProperty("value") }, setter.GetParameters());
        _il.Emit(setter.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, setter);
        return typeof(void);
    }

    // `.NET event +=/-=` -> call the event's add/remove accessor with the handler bound as the event's OWN
    // delegate type (e.g. EventHandler), not the Func/Action the lambda would otherwise produce. The lifted
    // method's signature matches the delegate's Invoke (the FIR injector typed the handler from the event's
    // handler signature), so `ldftn`+`newobj <EventDelegate>(object, IntPtr)` is verifiable — exactly what
    // `button.Click += (s,e)=>{}` lowers to in C#.
    Type EmitClrEvent(JsonElement e, bool add)
    {
        var type = ClrRef(e.GetProperty("type").GetString());
        var ev = type.GetEvent(e.GetProperty("event").GetString());
        var accessor = add ? ev.GetAddMethod() : ev.GetRemoveMethod();
        var delType = accessor.GetParameters()[0].ParameterType;   // == ev.EventHandlerType
        bool isStatic = e.GetProperty("static").GetBoolean();
        if (!isStatic) EmitExpr(e.GetProperty("recv"));
        EmitHandlerAsDelegate(e.GetProperty("handler"), delType);
        _il.Emit(isStatic ? OpCodes.Call : OpCodes.Callvirt, accessor);
        return typeof(void);
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
                var ct = _types[h.GetProperty("closureType").GetString()];
                foreach (var c in h.GetProperty("captures").EnumerateArray()) EmitExpr(c);
                _il.Emit(OpCodes.Newobj, ct.Ctor);
                _il.Emit(OpCodes.Ldftn, ct.Methods[h.GetProperty("method").GetString()]);
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

    // Args for a user method/ctor, boxing value types passed to reference (e.g. `object`/`Any`) params.
    // When the param type is unknown (lifted/unrecorded), emit the arg as-is (no spurious boxing).
    void EmitCallArgs(JsonElement args, MethodInfo mb)
    {
        var pt = _mparams.TryGetValue(mb, out var p) ? p : null;
        int i = 0;
        foreach (var a in args.EnumerateArray())
        {
            if (pt != null && i < pt.Length) EmitArg(a, pt[i]); else EmitExpr(a);
            i++;
        }
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

    // Index of the ':' separating RET from ARGS in a `func:` body. Skips a leading type prefix (clrg:/clr:/...)
    // so its colon isn't mistaken for the separator, and respects [] nesting (clrg:Task[int]).
    static int FuncRetEnd(string s)
    {
        int start = 0;
        foreach (var pre in new[] { "clrg:", "clr:", "array:", "nullable:", "func:", "gp:" })
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

    // BIR `clrg:<openName>[<arg1>,<arg2>,...]` -> a constructed generic .NET type. Args split at bracket-depth 0
    // so nested generics (List[ValueTuple[int,string]]) parse correctly.
    // Resolve a .NET type reference that may be a plain name (ResolveType), a generic `clrg:Open[args]`,
    // or a func/closed encoding (MapType). Used by clrNew/clrPropGet so they accept generic types (System.Lazy<T>).
    Type ClrRef(string s) =>
        s.StartsWith("byref:") ? ClrRef(s.Substring(6)).MakeByRefType() :   // `out`/`ref` param type (T&)
        s.StartsWith("clrg:") ? GenericType(s.Substring(5)) :
        (s.StartsWith("func:") || s.StartsWith("clr:") || s.StartsWith("array:") || s.StartsWith("nullable:") || s.StartsWith("gp:") || s.StartsWith("@")) ? MapType(s) :
        ResolveType(s);

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
            // `@Name` -> the user type; `@Name[arg,...]` -> that user generic type constructed (Box<int>).
            var spec = t.Substring(1);
            var br = spec.IndexOf('[');
            if (br < 0) return _types[spec].AsType;
            var open = spec.Substring(0, br);
            var args = SplitTopLevel(spec.Substring(br + 1, spec.Length - br - 2)).Select(MapArg).ToArray();
            return _types[open].AsType.MakeGenericType(args);
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
            _ => TryMapEmittedType(t) ?? ((t != null && t.Contains('.')) ? ResolveType(t) : typeof(object)),
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
