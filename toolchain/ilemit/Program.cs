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
        var nsProj = new List<(string, string)>();
        var rest = args.Skip(2).ToList();
        for (int i = 0; i < rest.Count; i++)
        {
            if (rest[i] == "--ref" && i + 1 < rest.Count) { var rp = Path.GetFullPath(rest[++i]); Emitter.T($"ref: {rp}"); try { Assembly.LoadFrom(rp); } catch { } }
            // `--ns-projection <kotlinPrefix>=<dotNetPrefix>`: stamp [assembly: DotKtNamespaceProjection] so a consumer
            // can import this library under <kotlinPrefix> though its types live in the .NET <dotNetPrefix> namespace.
            else if (rest[i] == "--ns-projection" && i + 1 < rest.Count) { var kv = rest[++i].Split('=', 2); if (kv.Length == 2) nsProj.Add((kv[0], kv[1])); }
            else bir.Add(rest[i]);
        }
        var files = bir.Select(p => JsonDocument.Parse(File.ReadAllText(p))).ToList();
        new Emitter(outDir, asmName, nsProj).EmitAssembly(files.Select(d => d.RootElement).ToList());
        return 0;
    }
}


sealed partial class Emitter
{
    readonly string _outDir;
    readonly string _asmName;
    readonly Dictionary<string, TypeInfo> _types = new();
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
    readonly Dictionary<MethodInfo, Type[]> _mparams = new();   // declared param types per method (for call-site boxing)
    // active try blocks: a `return` inside stores to the result local and leaves to the end label.
    readonly Stack<(LocalBuilder result, Label end)> _tryStack = new();
    // active loops: break/continue target the innermost (or the one matching the Kotlin label).
    readonly List<(string label, Label cont, Label brk)> _loops = new();
    // CFG block-IR labels: `label`/`goto`/`brIf` (E-0.5). Forward references need every label defined up-front,
    // so EmitMethodBody/EmitCtorBody prescans the whole body. id -> IL Label. See docs/design-il-cfg.md.
    Dictionary<int, Label> _cfgLabels;
    Type _methodRetType = typeof(void);
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

    readonly List<(string kotlin, string dotNet)> _nsProj;
    public Emitter(string outDir, string asmName, List<(string, string)> nsProj = null) { _outDir = outDir; _asmName = asmName; _nsProj = nsProj ?? new(); }

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
        // before any type/member that stamps one). No --ref DotKt.Runtime needed to resolve them. The assembly-level
        // [DotKtNamespaceProjection] is applied LATE (just before Save) — applying an assembly attribute whose type is a
        // module-internal embedded type before the module's other types exist corrupts the image.
        EnsureKotlinAttrs();

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
                    var metaName = arity > 0 ? name + "`" + arity : name;
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
            _curTypeParams = ti.TypeParams;
            // Bounds may reference any type (now all defined) and the type's own params (now in _curTypeParams).
            if (ti.IsGeneric && ti.Def.TryGetProperty("typeParams", out var tps2)) ApplyConstraints(tps2, ti.TypeParams, ti.IsInterface);
            if (ti.BaseName != null)
            {
                // A `.NET` base (`clr:System.Exception` / `clrg:...[..]`) is resolved by reflection; a Kotlin-user
                // base is another TypeBuilder in `_types`.
                if (ti.BaseName.StartsWith("clr:") || ti.BaseName.StartsWith("clrg:")) ti.TB.SetParent(ti.ClrBase = MapType(ti.BaseName));
                else ti.TB.SetParent(_types[ti.BaseName].TB);
            }
            if (!ti.IsFileClass && ti.Def.TryGetProperty("interfaces", out var ifs))
                foreach (var i in ifs.EnumerateArray())
                {
                    var spec = i.GetString();
                    // A .NET-mapped interface (`clr:`/`clrg:...[..]`, e.g. DotKt.Coroutines.Continuation<int>) is
                    // resolved by reflection; a Kotlin-user interface `Container[int]` comes from _types.
                    Type itype = (spec.StartsWith("clr:") || spec.StartsWith("clrg:"))
                        ? MapType(spec)
                        : ParseOwner(spec) is var (open, constructed) ? (constructed ?? (Type)_types[open].TB) : null;
                    ti.TB.AddInterfaceImplementation(itype);
                }
        }
        _curTypeParams = null;

        // Pass 3: declare fields, ctors, methods (signatures) so cross-refs resolve.
        foreach (var ti in _types.Values)
        {
            if (ti.IsEnum) continue;   // enums are fully defined (literals) in pass 1
            T($"pass3 signatures: {ti.TB?.Name}");
            _curTypeParams = ti.TypeParams;   // so `gp:T` in field/ctor/method signatures resolves
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
        foreach (var (typeKey, ti) in _types)
            if (!ti.IsFileClass && !ti.IsInterface && ti.Def.TryGetProperty("interfaces", out var ifs))
                foreach (var i in ifs.EnumerateArray())
                {
                    var spec = i.GetString();
                    // A .NET-mapped interface (DotKt.Coroutines.Continuation<int>): bind each interface method to the
                    // class method of the same .NET name via reflection (the class emits PascalCase names already).
                    if (spec.StartsWith("clr:") || spec.StartsWith("clrg:"))
                    {
                        var itype = MapType(spec);
                        var have = ti.Methods.Keys.ToHashSet();
                        // A SELF-REFERENTIAL constructed generic interface (e.g. `V : IComparable<V>`, V the emitted
                        // type) is a TypeBuilderInstantiation whose .GetMethods() throws. Enumerate the OPEN
                        // definition's methods and re-anchor each to the instantiation via TypeBuilder.GetMethod
                        // (same pattern as the self-ref base-ctor below).
                        if (itype.IsGenericType && itype.GetGenericArguments().Any(a => a is TypeBuilder || a.IsGenericParameter))
                        {
                            var openDef = itype.GetGenericTypeDefinition();
                            foreach (var im in openDef.GetMethods())
                                if (have.Contains(im.Name))
                                    ti.TB.DefineMethodOverride(FindMethod(typeKey, im.Name), TypeBuilder.GetMethod(itype, im));
                        }
                        else
                            foreach (var im in itype.GetMethods())
                                if (have.Contains(im.Name))
                                    ti.TB.DefineMethodOverride(FindMethod(typeKey, im.Name), im);
                        continue;
                    }
                    var (open, constructed) = ParseOwner(spec);
                    var iface = _types[open];
                    foreach (var im in iface.Methods)
                    {
                        var ifaceMethod = constructed != null ? TypeBuilder.GetMethod(constructed, im.Value) : (MethodInfo)im.Value;
                        ti.TB.DefineMethodOverride(FindMethod(typeKey, im.Key), ifaceMethod);
                    }
                }

        // Pass 4: emit all bodies (every ctor/method signature already exists).
        foreach (var ti in _types.Values)
            for (int ci = 0; ci < ti.Ctors.Count; ci++) { T($"pass4 ctor body: {ti.TB?.Name}#{ci}"); EmitCtorBody(ti, ti.Ctors[ci], ti.CtorDefs[ci]); }
        foreach (var ti in _types.Values)
            if (!ti.IsInterface && !ti.IsEnum)
                foreach (var m in ti.Def.GetProperty("methods").EnumerateArray()) { T($"pass4 method body: {ti.TB?.Name}.{(m.TryGetProperty("name", out var mn) ? mn.GetString() : "?")}"); EmitMethodBody(ti, m); }

        // User annotations -> .NET custom attributes, applied on the type and its methods (the ctor builder of the
        // synthesized `: System.Attribute` class already exists). Args are compile-time constants.
        foreach (var ti in _types.Values)
        {
            // [NullableContext(1)] — the per-type NRT default: every reference-type position is non-null unless it
            // carries its own [Nullable(2)]. So a consuming Kotlin (or C#) module sees DotKt's non-null `String` as
            // non-null and `String?` as nullable, through .NET's standard nullable-reference metadata.
            if (ti.TB != null) ApplyNullableContext(ti.TB);
            if (ti.Def.TryGetProperty("attrs", out var tattrs))
                foreach (var a in tattrs.EnumerateArray()) ti.TB.SetCustomAttribute(BuildCab(a));
            if (ti.Def.TryGetProperty("methods", out var ms))
                foreach (var m in ms.EnumerateArray())
                {
                    if (!(m.TryGetProperty("attrs", out var mattrs) && mattrs.GetArrayLength() > 0)
                        || !ti.Methods.TryGetValue(m.GetProperty("name").GetString(), out var mb)) continue;
                    foreach (var a in mattrs.EnumerateArray()) mb.SetCustomAttribute(BuildCab(a));
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
            foreach (var f in inits) { EmitExpr(f.GetProperty("init")); _il.Emit(OpCodes.Stsfld, ti.Fields[f.GetProperty("name").GetString()]); }
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

        // [assembly: DotKtNamespaceProjection(kotlin, dotNet)] for each --ns-projection — so a consumer can import this
        // library's types under a Kotlin package different from their .NET namespace (e.g. kotlinx.coroutines). Applied
        // here (after all module types are created) because the attribute type is a module-internal embedded type.
        foreach (var (kotlin, dotNet) in _nsProj)
        {
            var nsCtor = _kNsProjAttr?.GetConstructor(new[] { typeof(string), typeof(string) });
            if (nsCtor != null) ab.SetCustomAttribute(new CustomAttributeBuilder(nsCtor, new object[] { kotlin, dotNet }));
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
                    if (_types.TryGetValue(ParseOwner(spec).open, out var inf)) Visit(inf);
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
        if (ti.IsInterface) attrs |= MethodAttributes.Virtual | MethodAttributes.Abstract | MethodAttributes.NewSlot;
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
        _curTypeParams = ti.TypeParams; _curMethodParams = null;
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
        _curTypeParams = ti.TypeParams;
        _curMethodParams = _methodTypeParams.TryGetValue(mb, out var mp) ? mp : null;
        if (m.TryGetProperty("suspend", out var su) && su.GetBoolean())
        {
            if (m.TryGetProperty("coClass", out var cc) && cc.GetBoolean()) EmitCoroutineClass(ti, mb, m);
            else EmitCoroutine(ti, mb, m);
            return;
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
    void ApplyConstraints(JsonElement tps, Dictionary<string, GenericTypeParameterBuilder> map, bool isInterface)
    {
        foreach (var x in tps.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Object) continue;
            var gp = map[x.GetProperty("name").GetString()];
            // Declaration-site variance is legal in CLR metadata only on interface (and delegate) type params;
            // on a class it's Kotlin-level only, so we drop it (the runtime assignment isn't variant for classes).
            if (isInterface && x.TryGetProperty("variance", out var v))
            {
                var attr = v.GetString() == "out" ? GenericParameterAttributes.Covariant
                         : v.GetString() == "in" ? GenericParameterAttributes.Contravariant
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
        retType = Subst(mb.ReturnType, constructed.GetGenericArguments());
        return TypeBuilder.GetMethod(constructed, mb);
    }

    // A BCL constructed generic (List<T>, HashSet<T>, Dictionary<K,V>) whose type argument is an EMITTED type
    // (a TypeBuilderInstantiation) refuses reflection — `GetConstructor`/`GetMethod` throw "does not support
    // resolving members" (feedback item 12). Re-anchor the OPEN definition's member onto the constructed type via
    // the static TypeBuilder.GetX helpers, exactly like ResolveField/ResolveMethod do for emitted generics.
    static bool IsTbInstantiation(Type t) =>
        t.IsGenericType && !t.IsGenericTypeDefinition &&
        t.GetGenericArguments().Any(a => a is TypeBuilder || a is GenericTypeParameterBuilder || (a.IsGenericType && IsTbInstantiation(a)));

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
        if (iface.IsGenericType && iface.GetGenericArguments().Any(a => a.IsGenericParameter || a is TypeBuilder))
            return TypeBuilder.GetMethod(iface, iface.GetGenericTypeDefinition().GetMethod(name));
        return iface.GetMethod(name);
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

    MethodBuilder FindMethod(string typeName, string name, string sig = null)
    {
        for (var ti = _types[typeName]; ti != null; ti = ti.BaseName != null && _types.ContainsKey(ti.BaseName) ? _types[ti.BaseName] : null)
        {
            if (sig != null && ti.MethodsBySig.TryGetValue(SigKey(name, sig), out var ms)) return ms;
            if (ti.Methods.TryGetValue(name, out var m)) return m;
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
        if (t.IsGenericType) foreach (var a in t.GetGenericArguments()) if (ContainsTypeBuilder(a)) return true;
        return false;
    }
    ConstructorInfo DelegateCtor(Type ft)
    {
        var sig = new[] { typeof(object), typeof(IntPtr) };
        return (ft.IsGenericType && ContainsTypeBuilder(ft))
            ? TypeBuilder.GetConstructor(ft, ft.GetGenericTypeDefinition().GetConstructor(sig))
            : ft.GetConstructor(sig);
    }
    // The delegate's `Invoke` method, bridged via TypeBuilder.GetMethod for a TypeBuilder-involving instantiation.
    MethodInfo InvokeOf(Type ft)
        => (ft.IsGenericType && ContainsTypeBuilder(ft))
            ? TypeBuilder.GetMethod(ft, ft.GetGenericTypeDefinition().GetMethod("Invoke"))
            : ft.GetMethod("Invoke");
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
        switch (op)
        {
            case "+": _il.Emit(OpCodes.Add); return lt;
            case "-": _il.Emit(OpCodes.Sub); return lt;
            case "*": _il.Emit(OpCodes.Mul); return lt;
            case "/": _il.Emit(OpCodes.Div); return lt;
            case "%": _il.Emit(OpCodes.Rem); return lt;
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
        if (wantNullable && node.TryGetProperty("k", out var k) && k.GetString() == "const"
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
            var openCtor = type.GetGenericTypeDefinition().GetConstructor(argTypes.All(t => t != null) ? argTypes : Type.EmptyTypes)
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
        var mi = ResolveType(typeName).GetMethod(method)
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
        EmitSplicedStmts(doc.RootElement.GetProperty("body"));
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
                mi = type.GetMethod(name, flags, null, resolved, null);
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
        if (mi == null) { var named = type.GetMethods(flags).Where(m => m.Name == name).ToList(); if (named.Count == 1) mi = named[0]; }
        // A value-type receiver's instance method needs a managed pointer (e.g. struct Vec2.Mag2()).
        if (instance) { if (type.IsValueType) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv")); }
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
            var gm = type.GetMethod("get_" + propName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (gm != null)
            {
                if (!isStatic && !gm.IsStatic) { if (type.IsValueType) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv")); }
                _il.Emit(gm.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, gm);
                return gm.ReturnType;
            }
            // A .NET FIELD surfaced as a Kotlin property (facadegen records static/const fields, public instance fields,
            // and Kotlin backing-field properties). Emit a field access instead of a getter call.
            var fld = type.GetField(propName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"ilemit: no readable property OR field '{propName}' on .NET type '{type}' (spec '{typeName}'). Available properties: [{PropList(type)}]");
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
        var args = rest.Substring(colon + 1).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(MapType).ToArray();
        if (ret == "void")
            return args.Length == 0 ? typeof(Action)
                : Type.GetType("System.Action`" + args.Length).MakeGenericType(args);
        var all = args.Append(MapType(ret)).ToArray();
        return Type.GetType("System.Func`" + all.Length).MakeGenericType(all);
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
        (s.StartsWith("func:") || s.StartsWith("clr:") || s.StartsWith("array:") || s.StartsWith("nullable:") || s.StartsWith("@")) ? MapType(s) :
        ResolveType(s);

    Type GenericType(string spec)
    {
        var br = spec.IndexOf('[');
        var open = spec.Substring(0, br);
        var inner = spec.Substring(br + 1, spec.Length - br - 2);
        var args = SplitTopLevel(inner).Select(MapType).ToArray();
        return ResolveType(open + "`" + args.Length).MakeGenericType(args);
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
            var args = SplitTopLevel(spec.Substring(br + 1, spec.Length - br - 2)).Select(MapType).ToArray();
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
            // A bare .NET FQN (e.g. a hardcoded `System.Exception` catch type) -> resolve by reflection; otherwise object.
            _ => (t != null && t.Contains('.')) ? ResolveType(t) : typeof(object),
        };
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
