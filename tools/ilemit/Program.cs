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
        var files = bir.Select(p => JsonDocument.Parse(File.ReadAllText(p))).ToList();
        new Emitter(outDir, asmName).EmitAssembly(files.Select(d => d.RootElement).ToList());
        return 0;
    }
}

sealed class TypeInfo
{
    public TypeBuilder TB;
    public JsonElement Def;
    public bool IsFileClass;
    public JsonElement? FileElem; // for file classes: the whole file (for hasMain)
    public string BaseName;
    public Type ClrBase;   // set when the base is a .NET type (`clr:`/`clrg:`); resolved by reflection, not in _types
    public readonly Dictionary<string, FieldBuilder> Fields = new();
    public readonly Dictionary<string, MethodBuilder> Methods = new();
    // Overloaded methods share a name, so `Methods` (name-keyed) collides — the last-declared wins, and the others'
    // bodies/calls get misrouted. `MethodsBySig` keys by name + parameter-type signature so each overload is distinct
    // (e.g. `text(string)` vs `text(func:string:)`). Both body emission and call resolution prefer it.
    public readonly Dictionary<string, MethodBuilder> MethodsBySig = new();
    public ConstructorBuilder Ctor;       // primary ctor (Ctors[0]) — convenience for the common single-ctor path
    public JsonElement CtorDef;
    public readonly List<ConstructorBuilder> Ctors = new();   // all ctors (primary + secondary)
    public readonly List<JsonElement> CtorDefs = new();
    public bool IsInterface;
    public bool IsEnum;
    public EnumBuilder EB;                 // set for enums (EnumBuilder is not a TypeBuilder)
    public Type Created;                   // baked enum Type (created early so its tokens are valid in other IL)
    // Generic type parameters (`class Box<T>`): name -> the GenericTypeParameterBuilder defined in pass 1.
    public readonly Dictionary<string, GenericTypeParameterBuilder> TypeParams = new();
    public bool IsGeneric => TypeParams.Count > 0;
    public Type AsType => Created ?? (EB != null ? (Type)EB : TB);
}

sealed class Emitter
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
                    var tb = nested ? _types[niEl.GetString()].TB.DefineNestedType(name, attrs) : _mod.DefineType(name, attrs);
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
                        ti.Ctors.Add(ti.TB.DefineConstructor(AccessOf(c), CallingConventions.Standard, ps));
                        ti.CtorDefs.Add(c);
                    }
                if (ti.Ctors.Count > 0) { ti.Ctor = ti.Ctors[0]; ti.CtorDef = ti.CtorDefs[0]; }
            }
        }

        // Link interface implementations: every class method that satisfies an interface method. For a constructed
        // generic interface `Container[int]`, the override target is the method on the instantiation (static helper).
        foreach (var ti in _types.Values)
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
                                    ti.TB.DefineMethodOverride(FindMethod(ti.TB.Name, im.Name), TypeBuilder.GetMethod(itype, im));
                        }
                        else
                            foreach (var im in itype.GetMethods())
                                if (have.Contains(im.Name))
                                    ti.TB.DefineMethodOverride(FindMethod(ti.TB.Name, im.Name), im);
                        continue;
                    }
                    var (open, constructed) = ParseOwner(spec);
                    var iface = _types[open];
                    foreach (var im in iface.Methods)
                    {
                        var ifaceMethod = constructed != null ? TypeBuilder.GetMethod(constructed, im.Value) : (MethodInfo)im.Value;
                        ti.TB.DefineMethodOverride(FindMethod(ti.TB.Name, im.Key), ifaceMethod);
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
            // them. [KotlinFile] on a file-facade class -> its statics are top-level fns; [KotlinFunction(flags)] on
            // methods carrying infix/operator/suspend. No-op when DotKt.Runtime isn't referenced (attrs unresolved).
            if (ti.IsFileClass) ApplyKotlinFile(ti.TB);
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
                    if (nmask != 0) ApplyKotlinNullable(mb, nmask);
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

    // Synthesize a CLR-native async state machine (strategy B) for a `suspend fun`. Builds a struct
    // `<class>_<name>__sm : IAsyncStateMachine` (state + AsyncTaskMethodBuilder + cpsFields + awaiter caches),
    // emits MoveNext from the CPS-linearized `steps`, and fills the kickoff `mb` (Create/Start/return Task).
    // Proven shape — see docs/coroutine-il.md PoC. Capability bar = linear / loop / branch / direct-suspend-call.
    void EmitCoroutine(TypeInfo ti, MethodBuilder mb, JsonElement m)
    {
        var rs = m.GetProperty("resultType").GetString();
        bool unit = rs == "void";
        Type resultT = unit ? null : MapType(rs);
        Type builderT = unit ? typeof(System.Runtime.CompilerServices.AsyncTaskMethodBuilder)
                             : typeof(System.Runtime.CompilerServices.AsyncTaskMethodBuilder<>).MakeGenericType(resultT);
        var iasm = typeof(System.Runtime.CompilerServices.IAsyncStateMachine);
        var steps = m.GetProperty("steps").EnumerateArray().ToList();

        // ---- struct SM : IAsyncStateMachine ----
        var sm = _mod.DefineType(ti.TB.Name + "_" + mb.Name + "__sm",
            TypeAttributes.Public | TypeAttributes.SequentialLayout | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            typeof(ValueType));
        sm.AddInterfaceImplementation(iasm);
        var fState = sm.DefineField("<>1__state", typeof(int), FieldAttributes.Public);
        var fBuilder = sm.DefineField("<>t__builder", builderT, FieldAttributes.Public);

        var coFields = new Dictionary<string, FieldInfo>();
        var cpsDefs = m.GetProperty("cpsFields").EnumerateArray().ToList();
        foreach (var f in cpsDefs)
            coFields[f.GetProperty("name").GetString()] = sm.DefineField(f.GetProperty("name").GetString(), MapType(f.GetProperty("type").GetString()), FieldAttributes.Public);

        // Instance coroutine (e.g. a capturing suspend lambda's closure `invoke`): capture the receiver so resume
        // can reach the declaring type's fields (the lambda's captured vars). `this` in MoveNext reads this field.
        var fThis = mb.IsStatic ? null : sm.DefineField("<>4__this", ti.TB, FieldAttributes.Public);

        // One awaiter cache field + type per suspension point (keyed by state). Task<Tk> -> TaskAwaiter<Tk>.
        var awaiterType = new Dictionary<int, Type>();
        var awaiterField = new Dictionary<int, FieldBuilder>();
        foreach (var st in steps)
            if (st.GetProperty("k").GetString() == "coSuspend")
            {
                int k = st.GetProperty("state").GetInt32();
                var art = st.GetProperty("resultType").GetString();
                var at = art == "void" ? typeof(System.Runtime.CompilerServices.TaskAwaiter)
                    : typeof(System.Runtime.CompilerServices.TaskAwaiter<>).MakeGenericType(MapType(art));
                awaiterType[k] = at;
                awaiterField[k] = sm.DefineField("<>u__" + k, at, FieldAttributes.Public);
            }

        // SetStateMachine(IAsyncStateMachine) { <>t__builder.SetStateMachine(value); }
        var setSm = sm.DefineMethod("SetStateMachine",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig,
            typeof(void), new[] { iasm });
        {
            var il = setSm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldflda, fBuilder); il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, GenM(builderT, "SetStateMachine"));
            il.Emit(OpCodes.Ret);
        }
        sm.DefineMethodOverride(setSm, iasm.GetMethod("SetStateMachine"));

        // ---- MoveNext ----
        var moveNext = sm.DefineMethod("MoveNext",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig,
            typeof(void), Type.EmptyTypes);
        sm.DefineMethodOverride(moveNext, iasm.GetMethod("MoveNext"));
        {
            _il = moveNext.GetILGenerator();
            _args.Clear(); _argTypes.Clear(); _locals.Clear();
            _methodRetType = typeof(void);
            _coFields = coFields;
            _coThis = fThis;
            PrescanCfgLabels(m.GetProperty("steps"));   // a non-suspending while inside a suspend fun lowers to CFG

            // labels: one resume + one "after" per suspension; one per coLabel id; awaiter local per suspension.
            var resume = new Dictionary<int, Label>();
            var after = new Dictionary<int, Label>();
            var awaiterLocal = new Dictionary<int, LocalBuilder>();
            var coLabel = new Dictionary<int, Label>();
            foreach (var st in steps)
            {
                var kind = st.GetProperty("k").GetString();
                if (kind == "coSuspend")
                {
                    int k = st.GetProperty("state").GetInt32();
                    resume[k] = _il.DefineLabel(); after[k] = _il.DefineLabel();
                    awaiterLocal[k] = _il.DeclareLocal(awaiterType[k]);
                }
                else if (kind == "coLabel" || kind == "coGoto" || kind == "coCondGoto")
                {
                    int id = st.GetProperty("id").GetInt32();
                    if (!coLabel.ContainsKey(id)) coLabel[id] = _il.DefineLabel();
                }
            }

            // try-around-await: a suspension state inside a try can't be branched to from outside the protected
            // region. Map each in-try state to its try's landing label; the outer dispatch jumps THERE, and an
            // inner dispatch (emitted at coTryBegin, inside the try) re-branches to the actual resume point.
            var tryStart = new Dictionary<int, Label>();
            var tryStates = new Dictionary<int, List<int>>();
            var stateTry = new Dictionary<int, int>();
            {
                int open = -1;
                foreach (var st in steps)
                {
                    var kind = st.GetProperty("k").GetString();
                    if (kind == "coTryBegin") { int id = st.GetProperty("id").GetInt32(); open = id; tryStart[id] = _il.DefineLabel(); tryStates[id] = new List<int>(); }
                    else if (kind == "coTryEnd") open = -1;
                    else if (kind == "coSuspend" && open >= 0) { int k = st.GetProperty("state").GetInt32(); stateTry[k] = open; tryStates[open].Add(k); }
                }
            }
            _coExit = _il.DefineLabel(); _coTryDepth = 0;

            // dispatch: jump to the resume point for the saved state (state -1/-2 fall through to the start). An
            // in-try state jumps to its try's landing label instead (the inner dispatch then resumes inside it).
            foreach (var kv in resume)
            {
                _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fState);
                EmitLdcI4(kv.Key);
                _il.Emit(OpCodes.Beq, stateTry.TryGetValue(kv.Key, out var otid) ? tryStart[otid] : kv.Value);
            }

            var tryEnd = new Dictionary<int, Label>();
            bool fell = true;   // does the previous step fall through to here? (false after a return/unconditional goto)
            foreach (var st in steps)
            {
                var kind = st.GetProperty("k").GetString();
                switch (kind)
                {
                    case "coTryBegin":
                    {
                        int id = st.GetProperty("id").GetInt32();
                        _il.MarkLabel(tryStart[id]);
                        _il.Emit(OpCodes.Nop);                       // landing pad OUTSIDE the region (legal branch target)
                        tryEnd[id] = _il.BeginExceptionBlock();
                        _coTryDepth++;
                        // inner dispatch: resume to the suspension point that lives inside THIS try.
                        foreach (var k in tryStates[id])
                        {
                            _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fState);
                            EmitLdcI4(k); _il.Emit(OpCodes.Beq, resume[k]);
                        }
                        break;
                    }
                    case "coCatchBegin":
                    {
                        int id = st.GetProperty("id").GetInt32();
                        if (fell) _il.Emit(OpCodes.Leave, tryEnd[id]);   // close the try body / previous catch
                        var ct = MapType(st.GetProperty("excType").GetString());
                        _il.BeginCatchBlock(ct);
                        var el = _il.DeclareLocal(ct);                   // bind the caught exception to the catch var
                        _locals[st.GetProperty("var").GetString()] = el;
                        _il.Emit(OpCodes.Stloc, el);
                        break;
                    }
                    case "coTryEnd":
                        EmitCoTryEnd(st, tryEnd[st.GetProperty("id").GetInt32()], fell);
                        break;
                    case "coSuspend":
                        EmitCoSuspend(st, fState, fBuilder, builderT, sm, awaiterType, awaiterField, awaiterLocal, resume, after, coFields);
                        break;
                    case "coLabel": _il.MarkLabel(coLabel[st.GetProperty("id").GetInt32()]); break;
                    case "coGoto": _il.Emit(OpCodes.Br, coLabel[st.GetProperty("id").GetInt32()]); break;
                    case "coCondGoto":
                        EmitExpr(st.GetProperty("cond"));
                        _il.Emit(OpCodes.Brfalse, coLabel[st.GetProperty("id").GetInt32()]);   // goto when cond is false
                        break;
                    case "coReturn":
                        _il.Emit(OpCodes.Ldarg_0); EmitLdcI4(-2); _il.Emit(OpCodes.Stfld, fState);
                        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldflda, fBuilder);
                        if (!unit && st.TryGetProperty("value", out var rv) && rv.ValueKind != JsonValueKind.Null)
                        {
                            var gt = EmitExpr(rv);
                            if (gt != null && NeedsBoxToRef(gt) && !resultT.IsValueType && !resultT.IsGenericParameter) _il.Emit(OpCodes.Box, gt);
                            _il.Emit(OpCodes.Call, GenM(builderT, "SetResult"));
                        }
                        else _il.Emit(OpCodes.Call, GenM(builderT, "SetResult"));
                        if (_coTryDepth > 0) _il.Emit(OpCodes.Leave, _coExit); else _il.Emit(OpCodes.Ret);
                        break;
                    case "coUnsupported":
                        throw new NotSupportedException("coroutine feature not supported by the .NET backend: " + st.GetProperty("of").GetString());
                    default:
                        EmitStmt(st);
                        break;
                }
                fell = !(kind == "coReturn" || kind == "coGoto");
            }
            _il.MarkLabel(_coExit);
            _il.Emit(OpCodes.Ret);   // single exit; suspension/return inside a try `leave` here, others `ret` directly
            _coFields = null;
            _coThis = null;
        }

        // ---- kickoff body (the original method `mb`): start the machine, return its Task ----
        {
            _il = mb.GetILGenerator();
            _args.Clear(); _argTypes.Clear(); _locals.Clear();
            var locSm = _il.DeclareLocal(sm);
            int ai = mb.IsStatic ? 0 : 1;
            if (fThis != null) { _il.Emit(OpCodes.Ldloca, locSm); _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Stfld, fThis); }
            foreach (var p in m.GetProperty("params").EnumerateArray())
            {
                var pn = p.GetProperty("name").GetString();
                _il.Emit(OpCodes.Ldloca, locSm); _il.Emit(OpCodes.Ldarg, ai++); _il.Emit(OpCodes.Stfld, coFields[pn]);
            }
            _il.Emit(OpCodes.Ldloca, locSm); _il.Emit(OpCodes.Call, GenM(builderT, "Create")); _il.Emit(OpCodes.Stfld, fBuilder);
            _il.Emit(OpCodes.Ldloca, locSm); EmitLdcI4(-1); _il.Emit(OpCodes.Stfld, fState);
            _il.Emit(OpCodes.Ldloca, locSm); _il.Emit(OpCodes.Ldflda, fBuilder); _il.Emit(OpCodes.Ldloca, locSm);
            _il.Emit(OpCodes.Call, GenM(builderT, "Start").MakeGenericMethod(sm));
            _il.Emit(OpCodes.Ldloca, locSm); _il.Emit(OpCodes.Ldflda, fBuilder);
            _il.Emit(OpCodes.Call, GenM(builderT, "get_Task"));
            _il.Emit(OpCodes.Ret);
        }

        sm.CreateType();
    }

    // Continuation-core state machine (Path B / B2-as-generalization, docs §13b): a CLASS implementing
    // DotKt.Coroutines.Continuation<object>, driven by ResumeWith -> InvokeSuspend (label switch). The default
    // Task sink (future{}, via NewRoot<T>) is the kickoff. Selected by "coClass":true (opt-in `@KCont` while the
    // struct/Task IAsyncStateMachine path remains the default). Reuses the same coSuspend/coLabel/coGoto/coReturn
    // step stream as the struct form; only the lowered runtime form differs.
    // A field/ctor on the (possibly generic) state-machine type: on a constructed generic SM, accesses go through
    // TypeBuilder.GetField/GetConstructor(constructed, def); on a non-generic SM, the def itself.
    static FieldInfo SmField(Type inst, FieldBuilder def) => inst.IsGenericType ? TypeBuilder.GetField(inst, def) : def;
    static ConstructorInfo SmCtor(Type inst, ConstructorBuilder def) => inst.IsGenericType ? TypeBuilder.GetConstructor(inst, def) : def;

    // Resolve a (unique-by-name) method on a possibly TypeBuilder-instantiated generic type. When the result type of
    // a `suspend fun` is a USER type, AsyncTaskMethodBuilder<UserT>/Task<UserT>/TaskAwaiter<UserT> are
    // TypeBuilderInstantiations, whose GetMethod throws "use TypeBuilder.GetMethod instead" — so re-anchor the open
    // definition's method onto the instantiation. Baked instantiations / non-generic types resolve directly. This is
    // the method-side counterpart of SmCtor; it unblocks member `suspend fun`s returning a user class.
    static MethodInfo GenM(Type t, string name)
    {
        try { return t.GetMethod(name); }
        catch (NotSupportedException) { return TypeBuilder.GetMethod(t, t.GetGenericTypeDefinition().GetMethod(name)); }
    }

    // Emit parameter NAMES into the metadata (DefineParameter is 1-based; 0 = return). ilemit otherwise defines
    // methods by type only, so the names are lost — and facadegen falls back to arg0/arg1, which blocks named-argument
    // calls across an assembly boundary. The names come straight from the BIR params.
    static void DefineParamNames(MethodBuilder mb, JsonElement m)
    {
        if (!m.TryGetProperty("params", out var ps)) return;
        int i = 1;
        foreach (var p in ps.EnumerateArray())
        {
            var name = (p.TryGetProperty("name", out var nn) ? nn.GetString() : null) ?? "";
            bool vararg = p.TryGetProperty("vararg", out var vv) && vv.GetBoolean();
            bool hasDefault = p.TryGetProperty("default", out var dflt);
            if (name.Length == 0 && !vararg && !hasDefault) { i++; continue; }
            // A constant default -> [Optional] + DefaultParameterValue, so a cross-module caller can omit the arg.
            var attrs = hasDefault ? ParameterAttributes.Optional | ParameterAttributes.HasDefault : ParameterAttributes.None;
            var pb = mb.DefineParameter(i, attrs, name.Length > 0 ? name : null);
            // `vararg xs: T` -> [ParamArray] so the .NET signature is a params array (a C# OR Kotlin consumer can spread).
            if (vararg) pb.SetCustomAttribute(new CustomAttributeBuilder(typeof(ParamArrayAttribute).GetConstructor(Type.EmptyTypes), new object[0]));
            if (hasDefault) { try { pb.SetConstant(ConstArgValue(dflt)); } catch { } }
            i++;
        }
    }

    // Close a coroutine try region (shared by the struct & class SM forms). A `finally` around a suspension is NOT
    // emitted as a CLR finally clause (a suspend `leave`s the .try, which would run a real finally on every
    // suspend); instead the finally body runs explicitly on the normal-exit path and in a synthesized catch-all
    // that rethrows (T10). v1: fall-through try body only — a `return` inside the try skips the finally.
    void EmitCoTryEnd(JsonElement st, Label tryEndL, bool fell)
    {
        if (st.TryGetProperty("finally", out var fin) && fin.GetArrayLength() > 0)
        {
            if (fell) { foreach (var f in fin.EnumerateArray()) EmitStmt(f); _il.Emit(OpCodes.Leave, tryEndL); }
            _il.BeginCatchBlock(ResolveType("System.Exception"));
            _il.Emit(OpCodes.Pop);                                  // discard the caught exception (we rethrow)
            foreach (var f in fin.EnumerateArray()) EmitStmt(f);
            _il.Emit(OpCodes.Rethrow);
            _il.EndExceptionBlock();
        }
        else
        {
            if (fell) _il.Emit(OpCodes.Leave, tryEndL);
            _il.EndExceptionBlock();
        }
        _coTryDepth--;
    }

    // The single ctor of a constructed generic reflected type, re-anchored via TypeBuilder.GetConstructor when a
    // type arg is an emitted generic param / TypeBuilder (e.g. TypedCont<T> in a generic suspend fun whose result
    // is the method's own type param T — reflection can't resolve members on such an instantiation).
    static ConstructorInfo CtorOf(Type constructed) =>
        constructed.GetGenericArguments().Any(a => a is TypeBuilder || a.IsGenericParameter)
            ? TypeBuilder.GetConstructor(constructed, constructed.GetGenericTypeDefinition().GetConstructors()[0])
            : constructed.GetConstructors()[0];

    void EmitCoroutineClass(TypeInfo ti, MethodBuilder mb, JsonElement m)
    {
        var rs = m.GetProperty("resultType").GetString();
        bool unitResult = rs == "void";   // a `suspend fun … : Unit` surfaces as a non-generic Task (RootUnit sink)
        var steps = m.GetProperty("steps").EnumerateArray().ToList();

        var contObj = ResolveType("DotKt.Coroutines.Continuation`1").MakeGenericType(typeof(object));
        var resObj = ResolveType("DotKt.Result`1").MakeGenericType(typeof(object));
        var ctxType = ResolveType("DotKt.Coroutines.CoroutineContext");
        var builders = ResolveType("DotKt.Coroutines.Builders");
        var fSuspended = ResolveType("DotKt.Coroutines.Intrinsics").GetField("COROUTINE_SUSPENDED");
        var mResSuccess = resObj.GetMethod("Success");
        var mResFailure = resObj.GetMethod("Failure");
        var mResIsFailure = resObj.GetMethod("get_IsFailure");
        var mResExOrNull = resObj.GetMethod("get_ExceptionOrNull");
        var mResGetOrThrow = resObj.GetMethod("GetOrThrow");
        var mContResume = contObj.GetMethod("ResumeWith");
        var mContGetCtx = contObj.GetMethod("get_Context");

        var sm = _mod.DefineType(ti.TB.Name + "_" + mb.Name + "__sm",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, typeof(object));
        // Generic suspend fun -> a generic state-machine type mirroring the method's type params (so `gp:T`-typed
        // cps fields resolve to the SM's own params; the kickoff instantiates sm<methodT>). See docs §13f.
        var sTps = m.TryGetProperty("typeParams", out var stpC) && stpC.GetArrayLength() > 0 ? (JsonElement?)stpC : null;
        Dictionary<string, GenericTypeParameterBuilder> smMap = null;
        string[] smNames = null;
        GenericTypeParameterBuilder[] smGps = null;
        if (sTps != null)
        {
            smNames = TpNames(sTps.Value);
            smGps = sm.DefineGenericParameters(smNames);
            smMap = new Dictionary<string, GenericTypeParameterBuilder>();
            for (int gi = 0; gi < smNames.Length; gi++) smMap[smNames[gi]] = smGps[gi];
        }
        sm.AddInterfaceImplementation(contObj);
        // Inside a GENERIC SM's own methods, references to its own fields/methods must go through the
        // self-instantiation sm<itsOwnParams> (Reflection.Emit rule), else "type is not fully instantiated".
        Type selfInst = smGps == null ? (Type)sm : sm.MakeGenericType(smGps.Cast<Type>().ToArray());
        FieldInfo SelfF(FieldBuilder f) => smGps == null ? f : TypeBuilder.GetField(selfInst, f);
        var savedTP = _curTypeParams; var savedMP = _curMethodParams;
        _curTypeParams = smMap; _curMethodParams = null;   // `gp:T` inside the SM resolves to the SM's own params
        // Field DEFINITIONS (open generic). The kickoff resolves these against sm<methodT>; the SM's own method
        // bodies use the self-instantiated (SelfF) forms below.
        var fStateD = sm.DefineField("<>1__state", typeof(int), FieldAttributes.Public);
        var fCompletionD = sm.DefineField("<>completion", contObj, FieldAttributes.Public);
        var fParamD = sm.DefineField("<>param", typeof(object), FieldAttributes.Public);
        var fErrD = sm.DefineField("<>err", typeof(Exception), FieldAttributes.Public);
        var coDefs = new Dictionary<string, FieldBuilder>();
        foreach (var f in m.GetProperty("cpsFields").EnumerateArray())
            coDefs[f.GetProperty("name").GetString()] = sm.DefineField(f.GetProperty("name").GetString(), MapType(f.GetProperty("type").GetString()), FieldAttributes.Public);
        var fThisD = mb.IsStatic ? null : sm.DefineField("<>4__this", ti.TB, FieldAttributes.Public);
        // Self-instantiated views used inside the SM's own methods (= the defs when non-generic).
        FieldInfo fState = SelfF(fStateD), fCompletion = SelfF(fCompletionD), fParam = SelfF(fParamD), fErr = SelfF(fErrD);
        FieldInfo fThis = fThisD == null ? null : SelfF(fThisD);
        var coFields = new Dictionary<string, FieldInfo>();
        foreach (var kv in coDefs) coFields[kv.Key] = SelfF(kv.Value);

        var ctor = sm.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        { var il = ctor.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)); il.Emit(OpCodes.Ret); }

        // CoroutineContext get_Context => <>completion.Context
        var getCtx = sm.DefineMethod("get_Context", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.SpecialName, ctxType, Type.EmptyTypes);
        { var il = getCtx.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fCompletion); il.Emit(OpCodes.Callvirt, mContGetCtx); il.Emit(OpCodes.Ret); }
        sm.DefineMethodOverride(getCtx, mContGetCtx);

        // object InvokeSuspend(): the label-switch body. Returns the result value, or COROUTINE_SUSPENDED.
        var invoke = sm.DefineMethod("InvokeSuspend", MethodAttributes.Public | MethodAttributes.HideBySig, typeof(object), Type.EmptyTypes);
        {
            _il = invoke.GetILGenerator();
            _args.Clear(); _argTypes.Clear(); _locals.Clear();
            _methodRetType = typeof(object);
            _coFields = coFields; _coThis = fThis;
            PrescanCfgLabels(m.GetProperty("steps"));
            var outcome = _il.DeclareLocal(typeof(object));

            var resume = new Dictionary<int, Label>();
            var coLabel = new Dictionary<int, Label>();
            foreach (var st in steps)
            {
                var kind = st.GetProperty("k").GetString();
                if (kind == "coSuspend" || kind == "coSuspendIntrinsic") resume[st.GetProperty("state").GetInt32()] = _il.DefineLabel();
                else if (kind == "coLabel" || kind == "coGoto" || kind == "coCondGoto") { int id = st.GetProperty("id").GetInt32(); if (!coLabel.ContainsKey(id)) coLabel[id] = _il.DefineLabel(); }
            }
            var tryStart = new Dictionary<int, Label>();
            var tryStates = new Dictionary<int, List<int>>();
            var stateTry = new Dictionary<int, int>();
            { int open = -1; foreach (var st in steps) { var kind = st.GetProperty("k").GetString();
                if (kind == "coTryBegin") { int id = st.GetProperty("id").GetInt32(); open = id; tryStart[id] = _il.DefineLabel(); tryStates[id] = new List<int>(); }
                else if (kind == "coTryEnd") open = -1;
                else if ((kind == "coSuspend" || kind == "coSuspendIntrinsic") && open >= 0) { int k = st.GetProperty("state").GetInt32(); stateTry[k] = open; tryStates[open].Add(k); } } }
            _coExit = _il.DefineLabel(); _coTryDepth = 0;

            foreach (var kv in resume)
            {
                _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fState); EmitLdcI4(kv.Key);
                _il.Emit(OpCodes.Beq, stateTry.TryGetValue(kv.Key, out var otid) ? tryStart[otid] : kv.Value);
            }

            var tryEnd = new Dictionary<int, Label>();
            bool fell = true;
            foreach (var st in steps)
            {
                var kind = st.GetProperty("k").GetString();
                switch (kind)
                {
                    case "coTryBegin": { int id = st.GetProperty("id").GetInt32(); _il.MarkLabel(tryStart[id]); _il.Emit(OpCodes.Nop);
                        tryEnd[id] = _il.BeginExceptionBlock(); _coTryDepth++;
                        foreach (var k in tryStates[id]) { _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fState); EmitLdcI4(k); _il.Emit(OpCodes.Beq, resume[k]); }
                        break; }
                    case "coCatchBegin": { int id = st.GetProperty("id").GetInt32(); if (fell) _il.Emit(OpCodes.Leave, tryEnd[id]);
                        var ct = MapType(st.GetProperty("excType").GetString()); _il.BeginCatchBlock(ct);
                        var el = _il.DeclareLocal(ct); _locals[st.GetProperty("var").GetString()] = el; _il.Emit(OpCodes.Stloc, el); break; }
                    case "coTryEnd": EmitCoTryEnd(st, tryEnd[st.GetProperty("id").GetInt32()], fell); break;
                    case "coSuspend": EmitCoSuspendClass(st, fState, fParam, fErr, resume, coFields, builders, fSuspended, outcome); break;
                    case "coSuspendIntrinsic": EmitCoSuspendIntrinsicClass(st, fState, fParam, fErr, resume, coFields, fSuspended, outcome); break;
                    case "coLabel": _il.MarkLabel(coLabel[st.GetProperty("id").GetInt32()]); break;
                    case "coGoto": _il.Emit(OpCodes.Br, coLabel[st.GetProperty("id").GetInt32()]); break;
                    case "coCondGoto": EmitExpr(st.GetProperty("cond")); _il.Emit(OpCodes.Brfalse, coLabel[st.GetProperty("id").GetInt32()]); break;
                    case "coReturn":
                        if (st.TryGetProperty("value", out var rv) && rv.ValueKind != JsonValueKind.Null) { var gt = EmitExpr(rv); if (gt != null && (gt.IsValueType || gt.IsGenericParameter)) _il.Emit(OpCodes.Box, gt); }   // box value types AND generic params (T)
                        else _il.Emit(OpCodes.Ldnull);
                        _il.Emit(OpCodes.Stloc, outcome);
                        if (_coTryDepth > 0) _il.Emit(OpCodes.Leave, _coExit); else _il.Emit(OpCodes.Br, _coExit);
                        break;
                    case "coUnsupported": throw new NotSupportedException("coroutine feature not supported by the .NET backend: " + st.GetProperty("of").GetString());
                    default: EmitStmt(st); break;
                }
                fell = !(kind == "coReturn" || kind == "coGoto");
            }
            _il.MarkLabel(_coExit);
            _il.Emit(OpCodes.Ldloc, outcome);
            _il.Emit(OpCodes.Ret);
            _coFields = null; _coThis = null;
        }

        // void ResumeWith(Result<object>): unpack the result, drive InvokeSuspend, route the outcome to <>completion.
        var resumeWith = sm.DefineMethod("ResumeWith", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig, typeof(void), new[] { resObj });
        {
            var il = resumeWith.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarga_S, (byte)1); il.Emit(OpCodes.Call, mResExOrNull); il.Emit(OpCodes.Stfld, fErr);
            var setNull = il.DefineLabel(); var afterParam = il.DefineLabel();
            il.Emit(OpCodes.Ldarga_S, (byte)1); il.Emit(OpCodes.Call, mResIsFailure); il.Emit(OpCodes.Brtrue, setNull);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarga_S, (byte)1); il.Emit(OpCodes.Call, mResGetOrThrow); il.Emit(OpCodes.Stfld, fParam); il.Emit(OpCodes.Br, afterParam);
            il.MarkLabel(setNull); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Stfld, fParam);
            il.MarkLabel(afterParam);
            var lOut = il.DeclareLocal(typeof(object)); var lFaulted = il.DeclareLocal(typeof(bool)); var lEx = il.DeclareLocal(typeof(Exception));
            il.BeginExceptionBlock();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, smGps == null ? (MethodInfo)invoke : TypeBuilder.GetMethod(selfInst, invoke)); il.Emit(OpCodes.Stloc, lOut);
            il.BeginCatchBlock(typeof(Exception));
            il.Emit(OpCodes.Stloc, lEx);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fCompletion); il.Emit(OpCodes.Ldloc, lEx); il.Emit(OpCodes.Call, mResFailure); il.Emit(OpCodes.Callvirt, mContResume);
            il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stloc, lFaulted);
            il.EndExceptionBlock();
            var ret = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, lFaulted); il.Emit(OpCodes.Brtrue, ret);
            il.Emit(OpCodes.Ldloc, lOut); il.Emit(OpCodes.Ldsfld, fSuspended); il.Emit(OpCodes.Beq, ret);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fCompletion); il.Emit(OpCodes.Ldloc, lOut); il.Emit(OpCodes.Call, mResSuccess); il.Emit(OpCodes.Callvirt, mContResume);
            il.MarkLabel(ret); il.Emit(OpCodes.Ret);
        }
        sm.DefineMethodOverride(resumeWith, mContResume);
        _curTypeParams = savedTP; _curMethodParams = savedMP;

        // kickoff: build the SM (sm<methodT> when generic), copy params/this, bind a NewRoot<T> sink, drive
        // ResumeWith(success(null)), return root.Task. Runs in the METHOD's generic context (mb's own type params).
        {
            _curTypeParams = ti.TypeParams; _curMethodParams = sTps != null ? _methodTypeParams[mb] : null;
            Type smInst = smMap == null ? sm : sm.MakeGenericType(smNames.Select(n => (Type)_methodTypeParams[mb][n]).ToArray());
            _il = mb.GetILGenerator();
            _args.Clear(); _argTypes.Clear(); _locals.Clear();
            var locSm = _il.DeclareLocal(smInst);
            _il.Emit(OpCodes.Newobj, SmCtor(smInst, ctor)); _il.Emit(OpCodes.Stloc, locSm);
            int ai = mb.IsStatic ? 0 : 1;
            if (fThisD != null) { _il.Emit(OpCodes.Ldloc, locSm); _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Stfld, SmField(smInst, fThisD)); }
            foreach (var p in m.GetProperty("params").EnumerateArray())
            {
                var pn = p.GetProperty("name").GetString();
                _il.Emit(OpCodes.Ldloc, locSm); _il.Emit(OpCodes.Ldarg, ai++); _il.Emit(OpCodes.Stfld, SmField(smInst, coDefs[pn]));
            }
            var newRoot = unitResult ? builders.GetMethod("NewRootUnit") : builders.GetMethod("NewRoot").MakeGenericMethod(MapType(rs));
            var emptyCtx = ResolveType("DotKt.Coroutines.EmptyCoroutineContext").GetField("Instance");
            var locRoot = _il.DeclareLocal(newRoot.ReturnType);
            _il.Emit(OpCodes.Ldsfld, emptyCtx); _il.Emit(OpCodes.Call, newRoot); _il.Emit(OpCodes.Stloc, locRoot);
            _il.Emit(OpCodes.Ldloc, locSm); _il.Emit(OpCodes.Ldloc, locRoot); _il.Emit(OpCodes.Stfld, SmField(smInst, fCompletionD));
            _il.Emit(OpCodes.Ldloc, locSm); _il.Emit(OpCodes.Ldnull); _il.Emit(OpCodes.Call, mResSuccess); _il.Emit(OpCodes.Callvirt, mContResume);
            _il.Emit(OpCodes.Ldloc, locRoot); _il.Emit(OpCodes.Callvirt, newRoot.ReturnType.GetMethod("get_Task")); _il.Emit(OpCodes.Ret);
            _curTypeParams = savedTP; _curMethodParams = savedMP;
        }

        sm.CreateType();
    }

    // A suspension point in the class form: register the awaited Task to resume this continuation (AwaitOnto), set
    // the resume state, and return COROUTINE_SUSPENDED; on resume, rethrow a faulted result or unbox <>param.
    void EmitCoSuspendClass(JsonElement st, FieldInfo fState, FieldInfo fParam, FieldInfo fErr,
        Dictionary<int, Label> resume, Dictionary<string, FieldInfo> coFields, Type builders, FieldInfo fSuspended, LocalBuilder outcome)
    {
        int k = st.GetProperty("state").GetInt32();
        var taskType = EmitExpr(st.GetProperty("awaitable"));
        var lTask = _il.DeclareLocal(taskType); _il.Emit(OpCodes.Stloc, lTask);
        _il.Emit(OpCodes.Ldarg_0); EmitLdcI4(k); _il.Emit(OpCodes.Stfld, fState);
        bool genericTask = taskType.IsGenericType && taskType.GetGenericTypeDefinition() == typeof(System.Threading.Tasks.Task<>);
        MethodInfo awaitOnto = genericTask
            ? builders.GetMethods().First(mm => mm.Name == "AwaitOnto" && mm.IsGenericMethodDefinition).MakeGenericMethod(taskType.GetGenericArguments()[0])
            : builders.GetMethods().First(mm => mm.Name == "AwaitOnto" && !mm.IsGenericMethodDefinition);
        _il.Emit(OpCodes.Ldloc, lTask); _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Call, awaitOnto);
        _il.Emit(OpCodes.Ldsfld, fSuspended); _il.Emit(OpCodes.Stloc, outcome);
        if (_coTryDepth > 0) _il.Emit(OpCodes.Leave, _coExit); else _il.Emit(OpCodes.Br, _coExit);

        _il.MarkLabel(resume[k]);
        var noErr = _il.DefineLabel();
        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fErr); _il.Emit(OpCodes.Brfalse, noErr);
        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fErr); _il.Emit(OpCodes.Throw);
        _il.MarkLabel(noErr);
        var assignTo = st.GetProperty("assignTo").ValueKind == JsonValueKind.Null ? null : st.GetProperty("assignTo").GetString();
        var resType = st.GetProperty("resultType").GetString();
        if (assignTo != null && resType != "void")
        {
            var tk = MapType(resType);
            if (coFields.TryGetValue(assignTo, out var destF))
            {
                _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fParam);
                _il.Emit((tk.IsValueType || tk.IsGenericParameter) ? OpCodes.Unbox_Any : OpCodes.Castclass, tk); _il.Emit(OpCodes.Stfld, destF);
            }
            else
            {
                var tmp = _il.DeclareLocal(tk); _locals[assignTo] = tmp;
                _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fParam);
                _il.Emit((tk.IsValueType || tk.IsGenericParameter) ? OpCodes.Unbox_Any : OpCodes.Castclass, tk); _il.Emit(OpCodes.Stloc, tmp);
            }
        }
    }

    // `sequence { yield(…) }` -> a state machine implementing DotKt.Sequences.ISeqStep<elem> (MoveNext advances to
    // the next yield; Current holds it), wrapped by Seq.Of into a lazy IEnumerable<elem>. The yield SM reuses the
    // coYield/coLabel/coGoto/coCondGoto step stream. Emitted inline at the call site (state is saved/restored so the
    // enclosing method's IL emission resumes afterward). See docs §13h.
    Type EmitSequenceSm(JsonElement e)
    {
        var elem = MapType(e.GetProperty("elem").GetString());
        var steps = e.GetProperty("steps").EnumerateArray().ToList();
        var iseq = ResolveType("DotKt.Sequences.ISeqStep`1").MakeGenericType(elem);

        var sm = _mod.DefineType("<>dotkt_SeqSm" + (_seqCounter++),
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, typeof(object));
        sm.AddInterfaceImplementation(iseq);
        var fState = sm.DefineField("<>state", typeof(int), FieldAttributes.Public);
        var fCurrent = sm.DefineField("<>current", elem, FieldAttributes.Public);
        var coFields = new Dictionary<string, FieldInfo>();
        foreach (var f in e.GetProperty("cpsFields").EnumerateArray())
            coFields[f.GetProperty("name").GetString()] = sm.DefineField(f.GetProperty("name").GetString(), MapType(f.GetProperty("type").GetString()), FieldAttributes.Public);

        var ctor = sm.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        { var il = ctor.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)); il.Emit(OpCodes.Ret); }

        var getCur = sm.DefineMethod("get_Current", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.SpecialName, elem, Type.EmptyTypes);
        { var il = getCur.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fCurrent); il.Emit(OpCodes.Ret); }
        sm.DefineMethodOverride(getCur, iseq.GetMethod("get_Current"));

        var mv = sm.DefineMethod("MoveNext", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig, typeof(bool), Type.EmptyTypes);
        sm.DefineMethodOverride(mv, iseq.GetMethod("MoveNext"));

        // Save the enclosing method's emit state (the shared _il / locals / coField context is reused for the SM body).
        var sIl = _il; var sFields = _coFields; var sThis = _coThis; var sRet = _methodRetType;
        var sCfg = _cfgLabels; var sExit = _coExit; var sTryDepth = _coTryDepth;
        var sLocals = new Dictionary<string, LocalBuilder>(_locals);
        var sArgs = new Dictionary<string, int>(_args); var sArgTypes = new Dictionary<string, Type>(_argTypes);
        {
            _il = mv.GetILGenerator();
            _args.Clear(); _argTypes.Clear(); _locals.Clear(); _methodRetType = typeof(bool);
            _coFields = coFields; _coThis = null;
            PrescanCfgLabels(e.GetProperty("steps"));
            var resume = new Dictionary<int, Label>();
            var coLabel = new Dictionary<int, Label>();
            var enumFields = new Dictionary<int, FieldInfo>();   // coYieldAll: per-step IEnumerator<elem> field
            var ienumerable = ResolveType("System.Collections.Generic.IEnumerable`1").MakeGenericType(elem);
            var ienumerator = ResolveType("System.Collections.Generic.IEnumerator`1").MakeGenericType(elem);
            foreach (var st in steps)
            {
                var kind = st.GetProperty("k").GetString();
                if (kind == "coYield") resume[st.GetProperty("state").GetInt32()] = _il.DefineLabel();
                else if (kind == "coYieldAll") { int k2 = st.GetProperty("state").GetInt32(); resume[k2] = _il.DefineLabel(); enumFields[k2] = sm.DefineField("<>e" + k2, ienumerator, FieldAttributes.Public); }
                else if (kind == "coLabel" || kind == "coGoto" || kind == "coCondGoto") { int id = st.GetProperty("id").GetInt32(); if (!coLabel.ContainsKey(id)) coLabel[id] = _il.DefineLabel(); }
            }
            var endL = _il.DefineLabel();
            // if (<>state == -1) return false;   (exhausted)
            var notDone = _il.DefineLabel();
            _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fState); _il.Emit(OpCodes.Ldc_I4_M1); _il.Emit(OpCodes.Bne_Un, notDone);
            _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Ret);
            _il.MarkLabel(notDone);
            // dispatch to the resume point after the saved yield (state 0 = start, falls through)
            foreach (var kv in resume) { _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fState); EmitLdcI4(kv.Key); _il.Emit(OpCodes.Beq, kv.Value); }
            foreach (var st in steps)
            {
                var kind = st.GetProperty("k").GetString();
                switch (kind)
                {
                    case "coYield":
                    {
                        int k = st.GetProperty("state").GetInt32();
                        _il.Emit(OpCodes.Ldarg_0); var vt = EmitExpr(st.GetProperty("value"));
                        if (vt != null && (vt.IsValueType || vt.IsGenericParameter) && !elem.IsValueType && !elem.IsGenericParameter) _il.Emit(OpCodes.Box, vt);
                        _il.Emit(OpCodes.Stfld, fCurrent);
                        _il.Emit(OpCodes.Ldarg_0); EmitLdcI4(k); _il.Emit(OpCodes.Stfld, fState);
                        _il.Emit(OpCodes.Ldc_I4_1); _il.Emit(OpCodes.Ret);
                        _il.MarkLabel(resume[k]);
                        break;
                    }
                    case "coLabel": _il.MarkLabel(coLabel[st.GetProperty("id").GetInt32()]); break;
                    case "coGoto": _il.Emit(OpCodes.Br, coLabel[st.GetProperty("id").GetInt32()]); break;
                    case "coCondGoto": EmitExpr(st.GetProperty("cond")); _il.Emit(OpCodes.Brfalse, coLabel[st.GetProperty("id").GetInt32()]); break;
                    case "coYieldAll":
                    {
                        // Yield every element of an IEnumerable<elem>. Get its enumerator into a field ONCE (the
                        // resume dispatch jumps PAST this init), then on each MoveNext call advance the inner
                        // enumerator: fe.MoveNext() ? (current = fe.Current; state = k; return true) : fall through.
                        int k = st.GetProperty("state").GetInt32();
                        var fe = enumFields[k];
                        _il.Emit(OpCodes.Ldarg_0);
                        EmitExpr(st.GetProperty("iterable"));
                        _il.Emit(OpCodes.Callvirt, ienumerable.GetMethod("GetEnumerator"));
                        _il.Emit(OpCodes.Stfld, fe);
                        _il.MarkLabel(resume[k]);
                        var afterAll = _il.DefineLabel();
                        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fe);
                        _il.Emit(OpCodes.Callvirt, ResolveType("System.Collections.IEnumerator").GetMethod("MoveNext"));
                        _il.Emit(OpCodes.Brfalse, afterAll);
                        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fe);
                        _il.Emit(OpCodes.Callvirt, ienumerator.GetMethod("get_Current"));
                        _il.Emit(OpCodes.Stfld, fCurrent);
                        _il.Emit(OpCodes.Ldarg_0); EmitLdcI4(k); _il.Emit(OpCodes.Stfld, fState);
                        _il.Emit(OpCodes.Ldc_I4_1); _il.Emit(OpCodes.Ret);
                        _il.MarkLabel(afterAll);
                        break;
                    }
                    case "coReturn": _il.Emit(OpCodes.Br, endL); break;   // `return` from the block ends the sequence
                    case "coUnsupported": throw new NotSupportedException("sequence feature not supported: " + st.GetProperty("of").GetString());
                    default: EmitStmt(st); break;
                }
            }
            _il.MarkLabel(endL);
            _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldc_I4_M1); _il.Emit(OpCodes.Stfld, fState);   // mark exhausted
            _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Ret);
        }
        _il = sIl; _coFields = sFields; _coThis = sThis; _methodRetType = sRet;
        _cfgLabels = sCfg; _coExit = sExit; _coTryDepth = sTryDepth;
        _locals.Clear(); foreach (var kv in sLocals) _locals[kv.Key] = kv.Value;
        _args.Clear(); foreach (var kv in sArgs) _args[kv.Key] = kv.Value;
        _argTypes.Clear(); foreach (var kv in sArgTypes) _argTypes[kv.Key] = kv.Value;
        sm.CreateType();

        // call site: Seq.Of<elem>(new SeqSm())
        _il.Emit(OpCodes.Newobj, ctor);
        _il.Emit(OpCodes.Call, ResolveType("DotKt.Sequences.Seq").GetMethod("Of").MakeGenericMethod(elem));
        return ResolveType("System.Collections.Generic.IEnumerable`1").MakeGenericType(elem);
    }

    // The raw `suspendCoroutineUninterceptedOrReturn` leaf in the class form: set the resume state, run the block's
    // leading statements (which typically register `this` to be resumed), then evaluate its result — if it is
    // COROUTINE_SUSPENDED, suspend; otherwise resume synchronously with that value. On resume, rethrow a faulted
    // result or unbox <>param. State is set BEFORE the block runs, so a same-thread resume during registration is safe.
    void EmitCoSuspendIntrinsicClass(JsonElement st, FieldInfo fState, FieldInfo fParam, FieldInfo fErr,
        Dictionary<int, Label> resume, Dictionary<string, FieldInfo> coFields, FieldInfo fSuspended, LocalBuilder outcome)
    {
        int k = st.GetProperty("state").GetInt32();
        _il.Emit(OpCodes.Ldarg_0); EmitLdcI4(k); _il.Emit(OpCodes.Stfld, fState);
        foreach (var pre in st.GetProperty("pre").EnumerateArray()) EmitStmt(pre);
        var gt = EmitExpr(st.GetProperty("value")); if (gt != null && (gt.IsValueType || gt.IsGenericParameter)) _il.Emit(OpCodes.Box, gt);   // box value types AND generic params (T)
        var vTmp = _il.DeclareLocal(typeof(object)); _il.Emit(OpCodes.Stloc, vTmp);
        var notSusp = _il.DefineLabel();
        _il.Emit(OpCodes.Ldloc, vTmp); _il.Emit(OpCodes.Ldsfld, fSuspended); _il.Emit(OpCodes.Bne_Un, notSusp);
        _il.Emit(OpCodes.Ldsfld, fSuspended); _il.Emit(OpCodes.Stloc, outcome);
        if (_coTryDepth > 0) _il.Emit(OpCodes.Leave, _coExit); else _il.Emit(OpCodes.Br, _coExit);
        _il.MarkLabel(notSusp);                                  // synchronous return: stash the value as the resume param
        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldloc, vTmp); _il.Emit(OpCodes.Stfld, fParam);

        _il.MarkLabel(resume[k]);
        var noErr = _il.DefineLabel();
        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fErr); _il.Emit(OpCodes.Brfalse, noErr);
        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fErr); _il.Emit(OpCodes.Throw);
        _il.MarkLabel(noErr);
        var assignTo = st.GetProperty("assignTo").ValueKind == JsonValueKind.Null ? null : st.GetProperty("assignTo").GetString();
        var resType = st.GetProperty("resultType").GetString();
        if (assignTo != null && resType != "void")
        {
            var tk = MapType(resType);
            if (coFields.TryGetValue(assignTo, out var destF))
            {
                _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fParam);
                _il.Emit((tk.IsValueType || tk.IsGenericParameter) ? OpCodes.Unbox_Any : OpCodes.Castclass, tk); _il.Emit(OpCodes.Stfld, destF);
            }
            else
            {
                var tmp = _il.DeclareLocal(tk); _locals[assignTo] = tmp;
                _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, fParam);
                _il.Emit((tk.IsValueType || tk.IsGenericParameter) ? OpCodes.Unbox_Any : OpCodes.Castclass, tk); _il.Emit(OpCodes.Stloc, tmp);
            }
        }
    }

    void EmitCoSuspend(JsonElement st, FieldBuilder fState, FieldBuilder fBuilder, Type builderT, TypeBuilder sm,
        Dictionary<int, Type> awaiterType, Dictionary<int, FieldBuilder> awaiterField, Dictionary<int, LocalBuilder> awaiterLocal,
        Dictionary<int, Label> resume, Dictionary<int, Label> after, Dictionary<string, FieldInfo> coFields)
    {
        int k = st.GetProperty("state").GetInt32();
        var at = awaiterType[k];
        var aLoc = awaiterLocal[k];

        // awaiter = (awaitable).GetAwaiter();
        var taskType = EmitExpr(st.GetProperty("awaitable"));
        _il.Emit(OpCodes.Callvirt, GenM(taskType, "GetAwaiter"));
        _il.Emit(OpCodes.Stloc, aLoc);
        // if (awaiter.IsCompleted) goto after;
        _il.Emit(OpCodes.Ldloca, aLoc); _il.Emit(OpCodes.Call, GenM(at, "get_IsCompleted"));
        _il.Emit(OpCodes.Brtrue, after[k]);
        // suspend: state=k; <>u__k=awaiter; builder.AwaitUnsafeOnCompleted(ref awaiter, ref this); return;
        _il.Emit(OpCodes.Ldarg_0); EmitLdcI4(k); _il.Emit(OpCodes.Stfld, fState);
        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldloc, aLoc); _il.Emit(OpCodes.Stfld, awaiterField[k]);
        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldflda, fBuilder);
        _il.Emit(OpCodes.Ldloca, aLoc); _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Call, GenM(builderT, "AwaitUnsafeOnCompleted").MakeGenericMethod(at, sm));
        if (_coTryDepth > 0) _il.Emit(OpCodes.Leave, _coExit); else _il.Emit(OpCodes.Ret);   // `ret` is illegal inside a .try
        // resume: awaiter = <>u__k; <>u__k = default; state = -1;
        _il.MarkLabel(resume[k]);
        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, awaiterField[k]); _il.Emit(OpCodes.Stloc, aLoc);
        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldflda, awaiterField[k]); _il.Emit(OpCodes.Initobj, at);
        _il.Emit(OpCodes.Ldarg_0); EmitLdcI4(-1); _il.Emit(OpCodes.Stfld, fState);
        // after: <assignTo> = awaiter.GetResult();
        _il.MarkLabel(after[k]);
        var assignTo = st.GetProperty("assignTo").ValueKind == JsonValueKind.Null ? null : st.GetProperty("assignTo").GetString();
        var getResult = GenM(at, "GetResult");
        bool voidResult = getResult.ReturnType == typeof(void);
        if (assignTo != null && coFields.TryGetValue(assignTo, out var destF))
        {
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldloca, aLoc); _il.Emit(OpCodes.Call, getResult);
            _il.Emit(OpCodes.Stfld, destF);
        }
        else if (assignTo != null && !voidResult)
        {
            // A non-field temp (e.g. `return await(...)`): a fresh IL local read by the following coReturn.
            var tmp = _il.DeclareLocal(GenM(at, "GetResult").ReturnType);
            _locals[assignTo] = tmp;
            _il.Emit(OpCodes.Ldloca, aLoc); _il.Emit(OpCodes.Call, getResult);
            _il.Emit(OpCodes.Stloc, tmp);
        }
        else
        {
            _il.Emit(OpCodes.Ldloca, aLoc); _il.Emit(OpCodes.Call, getResult);
            if (!voidResult) _il.Emit(OpCodes.Pop);
        }
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

    void EmitStmt(JsonElement s)
    {
        switch (s.GetProperty("k").GetString())
        {
            case "var":
            {
                var vname = s.GetProperty("name").GetString();
                var declared = MapType(s.GetProperty("type").GetString());
                // In a coroutine, a `var` declaring a cpsField is a STORE into the SM field (no IL local).
                if (_coFields != null && _coFields.TryGetValue(vname, out var cf))
                {
                    if (s.TryGetProperty("init", out var cinit) && cinit.ValueKind != JsonValueKind.Null)
                    {
                        _il.Emit(OpCodes.Ldarg_0);
                        var cg = EmitExpr(cinit);
                        if (cg != null && NeedsBoxToRef(cg) && !cf.FieldType.IsValueType && !cf.FieldType.IsGenericParameter) _il.Emit(OpCodes.Box, cg);
                        _il.Emit(OpCodes.Stfld, cf);
                    }
                    break;
                }
                var local = _il.DeclareLocal(declared);
                _locals[vname] = local;
                if (s.TryGetProperty("init", out var init) && init.ValueKind != JsonValueKind.Null)
                {
                    var got = EmitExpr(init);
                    // Assigning a value type to a reference local (e.g. an `Any`/`object` temp) needs boxing.
                    if (got != null && NeedsBoxToRef(got) && !declared.IsValueType && !declared.IsGenericParameter) _il.Emit(OpCodes.Box, got);
                    _il.Emit(OpCodes.Stloc, local);
                }
                break;
            }
            case "setLocal":
            {
                var sname = s.GetProperty("name").GetString();
                if (_coFields != null && _coFields.TryGetValue(sname, out var sf))
                {
                    _il.Emit(OpCodes.Ldarg_0);
                    EmitExpr(s.GetProperty("value"));
                    _il.Emit(OpCodes.Stfld, sf);
                    break;
                }
                EmitExpr(s.GetProperty("value"));
                StoreVar(sname);
                break;
            }
            case "setField":
            {
                EmitExpr(s.GetProperty("recv"));
                EmitExpr(s.GetProperty("value"));
                _il.Emit(OpCodes.Stfld, ResolveField(s.GetProperty("ownerType").GetString(), s.GetProperty("name").GetString(), out _));
                break;
            }
            case "return":
                if (_tryStack.Count > 0)
                {
                    // Can't `ret` inside a protected region: store the value and leave the block.
                    var ctx = _tryStack.Peek();
                    if (s.TryGetProperty("value", out var trv)) { EmitExpr(trv); if (ctx.result != null) _il.Emit(OpCodes.Stloc, ctx.result); else _il.Emit(OpCodes.Pop); }
                    _il.Emit(OpCodes.Leave, ctx.end);
                }
                else
                {
                    if (s.TryGetProperty("value", out var rv))
                    {
                        var got = EmitExpr(rv);
                        // `T` returned where the declared type is `T?` -> wrap in Nullable<T> (e.g. a `sortedBy`
                        // selector typed `(T)->R?` whose body yields a non-null R). Mirrors EmitArg's coercion.
                        if (got != null && _methodRetType.IsGenericType && _methodRetType.GetGenericTypeDefinition() == typeof(Nullable<>)
                            && _methodRetType.GetGenericArguments()[0] == got)
                            _il.Emit(OpCodes.Newobj, _methodRetType.GetConstructor(new[] { got }));
                    }
                    _il.Emit(OpCodes.Ret);
                }
                break;
            case "throw":
                EmitExpr(s.GetProperty("value"));
                _il.Emit(OpCodes.Throw);
                break;
            case "try":
            {
                // `ret` is illegal inside a protected region, so a `return` in the try stores its value and
                // `leave`s to a dedicated label where the real `ret` lives. The trailing ret is emitted ONLY when
                // the try actually contains a return — otherwise control FALLS THROUGH to the following statements
                // (e.g. `try { x = f() } finally { … }; return x`). Earlier this returned unconditionally, dropping
                // the code after a fall-through try.
                var bodyArr = s.GetProperty("body");
                var catchesArr = s.GetProperty("catches");
                bool hasRet = StmtsHaveReturn(bodyArr) || catchesArr.EnumerateArray().Any(c => StmtsHaveReturn(c.GetProperty("body")));
                LocalBuilder result = (_methodRetType != typeof(void) && hasRet) ? _il.DeclareLocal(_methodRetType) : null;
                Label retLabel = _il.DefineLabel();
                _il.BeginExceptionBlock();
                _tryStack.Push((result, retLabel));
                foreach (var b in bodyArr.EnumerateArray()) EmitStmt(b);
                foreach (var c in catchesArr.EnumerateArray())
                {
                    var ct = MapType(c.GetProperty("excType").GetString());
                    _il.BeginCatchBlock(ct);
                    // Bind the caught exception to the catch variable (a local); referenced by the handler body.
                    if (c.TryGetProperty("var", out var cv) && cv.ValueKind == JsonValueKind.String)
                    { var el = _il.DeclareLocal(ct); _locals[cv.GetString()] = el; _il.Emit(OpCodes.Stloc, el); }
                    else _il.Emit(OpCodes.Pop);
                    foreach (var b in c.GetProperty("body").EnumerateArray()) EmitStmt(b);
                }
                if (s.TryGetProperty("finally", out var fin))
                {
                    _il.BeginFinallyBlock();
                    foreach (var b in fin.EnumerateArray()) EmitStmt(b);
                }
                _il.EndExceptionBlock();
                _tryStack.Pop();
                if (hasRet)
                {
                    bool allRet = StmtsAlwaysReturn(bodyArr) && catchesArr.EnumerateArray().All(c => StmtsAlwaysReturn(c.GetProperty("body")));
                    if (!allRet)   // a fall-through path exists -> it skips the ret and continues
                    {
                        Label cont = _il.DefineLabel();
                        _il.Emit(OpCodes.Br, cont);
                        _il.MarkLabel(retLabel);
                        if (result != null) _il.Emit(OpCodes.Ldloc, result);
                        _il.Emit(OpCodes.Ret);
                        _il.MarkLabel(cont);
                    }
                    else           // every path returns -> the ret is the sole exit (fall-through unreachable)
                    {
                        _il.MarkLabel(retLabel);
                        if (result != null) _il.Emit(OpCodes.Ldloc, result);
                        _il.Emit(OpCodes.Ret);
                    }
                }
                break;
            }
            case "exprStmt":
            {
                var t = EmitExpr(s.GetProperty("expr"));
                if (t != typeof(void)) _il.Emit(OpCodes.Pop);
                break;
            }
            case "while":
            {
                var start = _il.DefineLabel(); var end = _il.DefineLabel();
                _loops.Add((LoopLabel(s), start, end));   // continue -> re-check, break -> end
                _il.MarkLabel(start);
                EmitExpr(s.GetProperty("cond")); _il.Emit(OpCodes.Brfalse, end);
                foreach (var b in s.GetProperty("body").EnumerateArray()) EmitStmt(b);
                _il.Emit(OpCodes.Br, start); _il.MarkLabel(end);
                _loops.RemoveAt(_loops.Count - 1);
                break;
            }
            case "if":
            {
                var end = _il.DefineLabel();
                foreach (var br in s.GetProperty("branches").EnumerateArray())
                {
                    if (br.TryGetProperty("else", out _))
                        foreach (var b in br.GetProperty("body").EnumerateArray()) EmitStmt(b);
                    else
                    {
                        var next = _il.DefineLabel();
                        EmitExpr(br.GetProperty("cond")); _il.Emit(OpCodes.Brfalse, next);
                        foreach (var b in br.GetProperty("body").EnumerateArray()) EmitStmt(b);
                        _il.Emit(OpCodes.Br, end); _il.MarkLabel(next);
                    }
                }
                _il.MarkLabel(end);
                break;
            }
            case "for":
            {
                var local = _il.DeclareLocal(typeof(int));
                _locals[s.GetProperty("var").GetString()] = local;
                EmitExpr(s.GetProperty("from")); _il.Emit(OpCodes.Stloc, local);
                var start = _il.DefineLabel(); var cont = _il.DefineLabel(); var end = _il.DefineLabel();
                _loops.Add((LoopLabel(s), cont, end));   // continue -> increment, break -> end
                _il.MarkLabel(start);
                _il.Emit(OpCodes.Ldloc, local);
                EmitExpr(s.GetProperty("to"));
                switch (s.GetProperty("cmp").GetString())   // exit when the bound is crossed
                {
                    case "<=": _il.Emit(OpCodes.Bgt, end); break;
                    case "<": _il.Emit(OpCodes.Bge, end); break;
                    case ">=": _il.Emit(OpCodes.Blt, end); break;
                }
                foreach (var b in s.GetProperty("body").EnumerateArray()) EmitStmt(b);
                _il.MarkLabel(cont);
                _il.Emit(OpCodes.Ldloc, local);
                _il.Emit(OpCodes.Ldc_I4, s.GetProperty("step").GetInt32());
                _il.Emit(OpCodes.Add); _il.Emit(OpCodes.Stloc, local);
                _il.Emit(OpCodes.Br, start);
                _il.MarkLabel(end);
                _loops.RemoveAt(_loops.Count - 1);
                break;
            }
            case "dowhile":
            {
                var start = _il.DefineLabel(); var cont = _il.DefineLabel(); var end = _il.DefineLabel();
                _loops.Add((LoopLabel(s), cont, end));
                _il.MarkLabel(start);
                foreach (var b in s.GetProperty("body").EnumerateArray()) EmitStmt(b);
                _il.MarkLabel(cont);
                EmitExpr(s.GetProperty("cond")); _il.Emit(OpCodes.Brtrue, start);
                _il.MarkLabel(end);
                _loops.RemoveAt(_loops.Count - 1);
                break;
            }
            case "forArray":
            {
                // for (x in arr): evaluate arr once, index 0..Length, bind loop var = arr[i] each iteration.
                var arrT = EmitExpr(s.GetProperty("array"));
                var arr = _il.DeclareLocal(arrT); _il.Emit(OpCodes.Stloc, arr);
                var idx = _il.DeclareLocal(typeof(int));
                _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Stloc, idx);
                var elem = MapType(s.GetProperty("elem").GetString());
                var lv = _il.DeclareLocal(elem);
                _locals[s.GetProperty("var").GetString()] = lv;
                var start = _il.DefineLabel(); var cont = _il.DefineLabel(); var end = _il.DefineLabel();
                _loops.Add((LoopLabel(s), cont, end));
                _il.MarkLabel(start);
                _il.Emit(OpCodes.Ldloc, idx); _il.Emit(OpCodes.Ldloc, arr); _il.Emit(OpCodes.Ldlen); _il.Emit(OpCodes.Conv_I4);
                _il.Emit(OpCodes.Bge, end);
                _il.Emit(OpCodes.Ldloc, arr); _il.Emit(OpCodes.Ldloc, idx); _il.Emit(OpCodes.Ldelem, elem); _il.Emit(OpCodes.Stloc, lv);
                foreach (var b in s.GetProperty("body").EnumerateArray()) EmitStmt(b);
                _il.MarkLabel(cont);
                _il.Emit(OpCodes.Ldloc, idx); _il.Emit(OpCodes.Ldc_I4_1); _il.Emit(OpCodes.Add); _il.Emit(OpCodes.Stloc, idx);
                _il.Emit(OpCodes.Br, start);
                _il.MarkLabel(end);
                _loops.RemoveAt(_loops.Count - 1);
                break;
            }
            case "block":
                foreach (var b in s.GetProperty("body").EnumerateArray()) EmitStmt(b);
                break;
            // Loop-expressions used in statement position (for-in over a collection, repeat) -> emit, no value.
            case "forEachInline":
            case "repeatInline":
                EmitExpr(s);
                break;
            case "break": { var (_, brk) = TargetLoop(s); _il.Emit(OpCodes.Br, brk); break; }
            case "continue": { var (cont, _) = TargetLoop(s); _il.Emit(OpCodes.Br, cont); break; }
            // CFG block-IR (E-0.5): a basic-block boundary and (un)conditional branches. See docs/design-il-cfg.md.
            case "label": _il.MarkLabel(_cfgLabels[s.GetProperty("id").GetInt32()]); break;
            case "goto": _il.Emit(OpCodes.Br, _cfgLabels[s.GetProperty("id").GetInt32()]); break;
            case "brIf":
                EmitExpr(s.GetProperty("cond"));
                _il.Emit(s.GetProperty("on").GetBoolean() ? OpCodes.Brtrue : OpCodes.Brfalse, _cfgLabels[s.GetProperty("id").GetInt32()]);
                break;
            case "unsupportedStmt": throw new NotSupportedException("the .NET backend does not support this Kotlin construct: " + s.GetProperty("of").GetString());
            default: throw new NotSupportedException("stmt " + s.GetProperty("k").GetString());
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

    // ---- expressions: push one value, return its CLR type ----
    Type EmitExpr(JsonElement e)
    {
        switch (e.GetProperty("k").GetString())
        {
            case "const": return EmitConst(e);
            case "this":
                if (_coThis != null) { _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, _coThis); return _coThis.FieldType; }   // instance coroutine: captured receiver
                _il.Emit(OpCodes.Ldarg_0); return typeof(object);
            case "coSuspendedSentinel":   // kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED
                { var f = ResolveType("DotKt.Coroutines.Intrinsics").GetField("COROUTINE_SUSPENDED"); _il.Emit(OpCodes.Ldsfld, f); return typeof(object); }
            case "sequenceNew": return EmitSequenceSm(e);
            case "coSelfCont":   // the coroutine's own continuation (the SM), as a typed Continuation<T>: new TypedCont<T>(this)
                {
                    var tk = MapType(e.GetProperty("resultType").GetString());
                    var typed = ResolveType("DotKt.Coroutines.TypedCont`1").MakeGenericType(tk);
                    var contObj = ResolveType("DotKt.Coroutines.Continuation`1").MakeGenericType(typeof(object));
                    _il.Emit(OpCodes.Ldarg_0);   // the SM (Continuation<object>)
                    _il.Emit(OpCodes.Newobj, CtorOf(typed));
                    return typed;
                }
            case "coContext":   // kotlin.coroutines.coroutineContext -> the SM's own Context (the SM is Continuation<object>)
                {
                    var contObj = ResolveType("DotKt.Coroutines.Continuation`1").MakeGenericType(typeof(object));
                    _il.Emit(OpCodes.Ldarg_0);
                    _il.Emit(OpCodes.Callvirt, contObj.GetMethod("get_Context"));
                    return ResolveType("DotKt.Coroutines.CoroutineContext");
                }
            case "coSelfCancellable":   // the SM as a CancellableContinuation<T>: new CancellableCont<T>(new TypedCont<T>(this))
                {
                    var tk = MapType(e.GetProperty("resultType").GetString());
                    var typed = ResolveType("DotKt.Coroutines.TypedCont`1").MakeGenericType(tk);
                    var cancel = ResolveType("DotKtx.Coroutines.CancellableCont`1").MakeGenericType(tk);
                    _il.Emit(OpCodes.Ldarg_0);
                    _il.Emit(OpCodes.Newobj, CtorOf(typed));
                    _il.Emit(OpCodes.Newobj, CtorOf(cancel));
                    return cancel;
                }
            case "local":
            {
                var name = e.GetProperty("name").GetString();
                // Inside a cross-module inline splice, a callee param reference emits the bound arg/value instead.
                if (_inlineSubst.TryGetValue(name, out var sub)) return EmitExpr(sub);
                // In a coroutine, a param/live-local reference is a load of the SM struct field.
                if (_coFields != null && _coFields.TryGetValue(name, out var cf)) { _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, cf); return cf.FieldType; }
                if (_locals.TryGetValue(name, out var l)) { _il.Emit(OpCodes.Ldloc, l); return l.LocalType; }
                if (_args.TryGetValue(name, out var a)) { _il.Emit(OpCodes.Ldarg, a); return _argTypes[name]; }
                throw new NotSupportedException("load unknown var " + name);
            }
            case "field":
            {
                var fon = e.GetProperty("ownerType").GetString();
                var fnm = e.GetProperty("name").GetString();
                // `Throwable.message`/`.cause` (a Kotlin property accessed as a field) -> System.Exception property.
                if (fon == "Throwable" && (fnm == "message" || fnm == "cause"))
                {
                    EmitExpr(e.GetProperty("recv"));
                    var m = typeof(Exception).GetMethod(fnm == "message" ? "get_Message" : "get_InnerException");
                    _il.Emit(OpCodes.Callvirt, m);
                    return m.ReturnType;
                }
                EmitExpr(e.GetProperty("recv"));
                var fb = ResolveField(fon, fnm, out var ft);
                _il.Emit(OpCodes.Ldfld, fb);
                return RetOr(e, ft);
            }
            case "setFieldExpr":
            {
                EmitExpr(e.GetProperty("recv"));
                EmitExpr(e.GetProperty("value"));
                _il.Emit(OpCodes.Stfld, ResolveField(e.GetProperty("ownerType").GetString(), e.GetProperty("name").GetString(), out _));
                return typeof(void);
            }
            case "lateinitGet":
            {
                // `lateinit var` read: load the field; if still null (uninitialized), throw.
                EmitExpr(e.GetProperty("recv"));
                var fld = ResolveField(e.GetProperty("ownerType").GetString(), e.GetProperty("name").GetString(), out _);
                _il.Emit(OpCodes.Ldfld, fld);
                _il.Emit(OpCodes.Dup);
                var ok = _il.DefineLabel();
                _il.Emit(OpCodes.Brtrue, ok);
                _il.Emit(OpCodes.Pop);
                _il.Emit(OpCodes.Ldstr, "lateinit property " + e.GetProperty("name").GetString() + " has not been initialized");
                _il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) }));
                _il.Emit(OpCodes.Throw);
                _il.MarkLabel(ok);
                return fld.FieldType;
            }
            case "new":
            {
                var (open, constructed) = ParseOwner(e.GetProperty("type").GetString());
                var ti = _types[open];
                var nargs = e.GetProperty("args");
                var ctor = SelectCtor(ti, nargs.GetArrayLength());
                foreach (var a in nargs.EnumerateArray()) EmitExpr(a);
                // Constructed user generic `Box<int>` -> resolve the ctor onto the instantiation (static helper).
                _il.Emit(OpCodes.Newobj, constructed != null ? TypeBuilder.GetConstructor(constructed, ctor) : (ConstructorInfo)ctor);
                return constructed ?? (Type)ti.TB;
            }
            case "callInstance":
            {
                var cisig = e.TryGetProperty("sig", out var ciEl) && ciEl.ValueKind == JsonValueKind.String ? ciEl.GetString() : null;
                var m0 = ResolveMethod(e.GetProperty("ownerType").GetString(), e.GetProperty("method").GetString(), out var rt, cisig);
                var m = ApplyTypeArgs(m0, e, out var mrt, out var mps);
                EmitExpr(e.GetProperty("recv"));
                if (m == m0) EmitCallArgs(e.GetProperty("args"), m); else EmitArgsTyped(e.GetProperty("args"), mps);
                _il.Emit(e.GetProperty("virtual").GetBoolean() ? OpCodes.Callvirt : OpCodes.Call, m);
                return RetOr(e, m == m0 ? rt : mrt);
            }
            case "constrainedCall":
            {
                // `a.compareTo(b)` on a Comparable -> `constrained. recvType; callvirt IComparable<T>::CompareTo`.
                // The receiver must be a managed pointer; `constrained.` then dispatches for value/ref/generic T.
                var recvType = MapType(e.GetProperty("recvType").GetString());
                var iface = MapType(e.GetProperty("iface").GetString());
                var mi = InterfaceMethodOn(iface, e.GetProperty("method").GetString());
                EmitAddr(e.GetProperty("recv"));
                EmitExpr(e.GetProperty("arg"));
                _il.Emit(OpCodes.Constrained, recvType);
                _il.Emit(OpCodes.Callvirt, mi);
                return mi.ReturnType;
            }
            case "callStatic":
            {
                var name = e.GetProperty("method").GetString();
                var csig = e.TryGetProperty("sig", out var csEl) && csEl.ValueKind == JsonValueKind.String ? csEl.GetString() : null;
                // owner present -> a static method on that named class (companion); else a file-class sibling.
                var mb = ApplyTypeArgs((e.TryGetProperty("owner", out var ow) && ow.ValueKind == JsonValueKind.String)
                    ? FindMethod(ow.GetString(), name, csig) : FindStatic(name, csig), e, out var srt, out var sps);
                if (e.TryGetProperty("typeArgs", out _)) EmitArgsTyped(e.GetProperty("args"), sps);
                else EmitCallArgs(e.GetProperty("args"), mb);
                _il.Emit(OpCodes.Call, mb);
                return RetOr(e, srt);
            }
            case "staticField":
            {
                var f = FindField(e.GetProperty("ownerType").GetString(), e.GetProperty("name").GetString());
                _il.Emit(OpCodes.Ldsfld, f);
                return f.FieldType;
            }
            case "clrStaticField":   // a static field on a .NET (reflected) type, e.g. EmptyCoroutineContext.Instance
            {
                var ct = ResolveType(e.GetProperty("type").GetString());
                var cf = ct.GetField(e.GetProperty("name").GetString(), BindingFlags.Public | BindingFlags.Static);
                _il.Emit(OpCodes.Ldsfld, cf);
                return cf.FieldType;
            }
            case "staticFieldSet":
            {
                EmitExpr(e.GetProperty("value"));
                _il.Emit(OpCodes.Stsfld, FindField(e.GetProperty("ownerType").GetString(), e.GetProperty("name").GetString()));
                return typeof(void);
            }
            case "console":
            {
                var cargs = e.GetProperty("args").EnumerateArray().ToList();
                if (cargs.Count == 0)   // bare `println()` -> Console.WriteLine() (blank line)
                {
                    _il.Emit(OpCodes.Call, typeof(Console).GetMethod(e.GetProperty("method").GetString(), Type.EmptyTypes));
                    return typeof(void);
                }
                var t = EmitExpr(cargs[0]);
                if (NeedsBoxToRef(t)) _il.Emit(OpCodes.Box, t);
                _il.Emit(OpCodes.Call, typeof(Console).GetMethod(e.GetProperty("method").GetString(), new[] { typeof(object) }));
                return typeof(void);
            }
            case "bin": return EmitBin(e);
            case "objEq": return EmitObjEq(e);
            case "un": return EmitUn(e);
            case "conv": return EmitConv(e);
            case "valueBlock":
            {
                // Inlined scope function: run the spliced statements, then yield the result expression.
                foreach (var st in e.GetProperty("stmts").EnumerateArray()) EmitStmt(st);
                return EmitExpr(e.GetProperty("result"));
            }
            case "listNew":
            {
                // `listOf(...)` -> new List<elem> { ... } via repeated Add.
                var elem = MapType(e.GetProperty("elem").GetString());
                var listT = typeof(List<>).MakeGenericType(elem);
                _il.Emit(OpCodes.Newobj, GenericCtor(listT));
                var add = GenericMethod(listT, "Add");
                foreach (var item in e.GetProperty("elems").EnumerateArray())
                {
                    _il.Emit(OpCodes.Dup);
                    EmitArg(item, elem);
                    _il.Emit(OpCodes.Callvirt, add);
                }
                return listT;
            }
            case "clrGenericStatic":
            {
                // Generic static call (LINQ): pick the exact overload by parameter shapes, MakeGenericMethod, call.
                var type = ResolveType(e.GetProperty("type").GetString());
                var typeArgs = e.GetProperty("typeArgs").EnumerateArray().Select(a => MapType(a.GetString())).ToArray();
                var shapes = e.GetProperty("shapes").EnumerateArray().Select(a => a.GetString()).ToArray();
                var argEls = e.GetProperty("args").EnumerateArray().ToList();
                var mi = ResolveGenericMethod(type, e.GetProperty("method").GetString(), typeArgs.Length, shapes, typeArgs, instance: false);
                var ps = mi.GetParameters();
                for (int i = 0; i < argEls.Count; i++) EmitArg(argEls[i], ps[i].ParameterType);
                _il.Emit(OpCodes.Call, mi);
                return mi.ReturnType;
            }
            case "clrGenericInstance":
            {
                // Generic instance call (`obj.M<T>(...)`): same overload resolution as the static path, but address
                // the constructed receiver type and `callvirt`. (Shares ResolveGenericMethod's MakeGenericMethod core.)
                var type = ClrRef(e.GetProperty("type").GetString());
                var typeArgs = e.GetProperty("typeArgs").EnumerateArray().Select(a => MapType(a.GetString())).ToArray();
                var shapes = e.GetProperty("shapes").EnumerateArray().Select(a => a.GetString()).ToArray();
                var argEls = e.GetProperty("args").EnumerateArray().ToList();
                var mi = ResolveGenericMethod(type, e.GetProperty("method").GetString(), typeArgs.Length, shapes, typeArgs, instance: true);
                var ps = mi.GetParameters();
                EmitExpr(e.GetProperty("recv"));
                for (int i = 0; i < argEls.Count; i++) EmitArg(argEls[i], ps[i].ParameterType);
                _il.Emit(mi.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, mi);
                return mi.ReturnType;
            }
            case "newArray": return EmitNewArray(e);
            case "nullableOf":
            {
                // value `v` -> `new Nullable<elem>(v)` (the implicit T -> T? wrap).
                var elem = MapType(e.GetProperty("elem").GetString());
                EmitExpr(e.GetProperty("e"));
                var nt = typeof(Nullable<>).MakeGenericType(elem);
                _il.Emit(OpCodes.Newobj, nt.GetConstructor(new[] { elem }));
                return nt;
            }
            case "default":
            {
                // `default(T)` -> the zero value: ldnull for a reference type, else a zero-init local (initobj).
                var dt = MapType(e.GetProperty("type").GetString());
                if (!dt.IsValueType && !dt.IsGenericParameter) { _il.Emit(OpCodes.Ldnull); return dt; }
                var loc = _il.DeclareLocal(dt);
                _il.Emit(OpCodes.Ldloca, loc); _il.Emit(OpCodes.Initobj, dt);
                _il.Emit(OpCodes.Ldloc, loc);
                return dt;
            }
            case "spreadConcat":
            {
                // `f(1, *a, 2)` -> new List<elem>(); Add(literal) / AddRange(spread); ToArray().
                var elem = MapType(e.GetProperty("elem").GetString());
                var listT = typeof(List<>).MakeGenericType(elem);
                var ienumT = typeof(System.Collections.Generic.IEnumerable<>).MakeGenericType(elem);
                var loc = _il.DeclareLocal(listT);
                _il.Emit(OpCodes.Newobj, listT.GetConstructor(Type.EmptyTypes));
                _il.Emit(OpCodes.Stloc, loc);
                foreach (var p in e.GetProperty("parts").EnumerateArray())
                {
                    _il.Emit(OpCodes.Ldloc, loc);
                    EmitExpr(p.GetProperty("e"));
                    _il.Emit(OpCodes.Callvirt, p.GetProperty("spread").GetBoolean()
                        ? listT.GetMethod("AddRange", new[] { ienumT })
                        : listT.GetMethod("Add", new[] { elem }));
                }
                _il.Emit(OpCodes.Ldloc, loc);
                _il.Emit(OpCodes.Callvirt, listT.GetMethod("ToArray", Type.EmptyTypes));
                return elem.MakeArrayType();
            }
            case "arrayGet":
            {
                EmitExpr(e.GetProperty("array")); EmitExpr(e.GetProperty("index"));
                var elem = MapType(e.GetProperty("elem").GetString());
                _il.Emit(OpCodes.Ldelem, elem); return elem;
            }
            case "arraySet":
            {
                EmitExpr(e.GetProperty("array")); EmitExpr(e.GetProperty("index")); EmitExpr(e.GetProperty("value"));
                _il.Emit(OpCodes.Stelem, MapType(e.GetProperty("elem").GetString())); return typeof(void);
            }
            case "arrayLen":
                EmitExpr(e.GetProperty("array")); _il.Emit(OpCodes.Ldlen); _il.Emit(OpCodes.Conv_I4); return typeof(int);
            case "forEachInline":
            {
                // `xs.forEach { it -> body }` (inline) -> enumerate src, bind `it` to a loop local, splice body.
                // Inlining (not a delegate) lets the body read/write enclosing locals without closure Ref cells.
                var elem = MapType(e.GetProperty("elem").GetString());
                var ienumT = typeof(System.Collections.Generic.IEnumerable<>).MakeGenericType(elem);
                var ienumrT = typeof(System.Collections.Generic.IEnumerator<>).MakeGenericType(elem);
                EmitExpr(e.GetProperty("src"));
                _il.Emit(OpCodes.Callvirt, ienumT.GetMethod("GetEnumerator"));
                var en = _il.DeclareLocal(ienumrT); _il.Emit(OpCodes.Stloc, en);
                var lv = _il.DeclareLocal(elem); _locals[e.GetProperty("var").GetString()] = lv;
                var start = _il.DefineLabel(); var end = _il.DefineLabel();
                _loops.Add((LoopLabel(e), start, end));
                _il.MarkLabel(start);
                _il.Emit(OpCodes.Ldloc, en);
                _il.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetMethod("MoveNext"));
                _il.Emit(OpCodes.Brfalse, end);
                _il.Emit(OpCodes.Ldloc, en);
                _il.Emit(OpCodes.Callvirt, ienumrT.GetMethod("get_Current"));
                _il.Emit(OpCodes.Stloc, lv);
                foreach (var b in e.GetProperty("body").EnumerateArray()) EmitStmt(b);
                _il.Emit(OpCodes.Br, start);
                _il.MarkLabel(end);
                _loops.RemoveAt(_loops.Count - 1);
                return typeof(void);
            }
            case "isinst":
            {
                // `x is T` -> isinst T; (ref != null) as bool.
                EmitExpr(e.GetProperty("e"));
                _il.Emit(OpCodes.Isinst, MapType(e.GetProperty("type").GetString()));
                _il.Emit(OpCodes.Ldnull);
                _il.Emit(OpCodes.Cgt_Un);
                return typeof(bool);
            }
            case "cast":
            {
                // `x as T` / smart-cast downcast -> castclass (reference) or unbox.any (value type).
                EmitExpr(e.GetProperty("e"));
                var t = MapType(e.GetProperty("type").GetString());
                _il.Emit(t.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, t);
                return t;
            }
            case "classRef":
            {
                // `T::class` -> a System.Type token (ldtoken + GetTypeFromHandle).
                var t = MapType(e.GetProperty("type").GetString());
                _il.Emit(OpCodes.Ldtoken, t);
                _il.Emit(OpCodes.Call, typeof(Type).GetMethod("GetTypeFromHandle"));
                return typeof(Type);
            }
            case "getType":
            {
                // `x::class` -> x.GetType() (a runtime System.Type). Box a value-type receiver first (GetType is
                // declared on object); a generic-param value also needs boxing to call the object method.
                var gt = EmitExpr(e.GetProperty("e"));
                if (gt != null && (gt.IsValueType || gt.IsGenericParameter)) _il.Emit(OpCodes.Box, gt);
                _il.Emit(OpCodes.Callvirt, typeof(object).GetMethod("GetType"));
                return typeof(Type);
            }
            case "isinstRef":
            {
                // `x as? T` for reference T -> `isinst T` (leaves the ref, or null on mismatch). The result is a
                // reference (objref or null), so report `object` — never a generic-param type that would make a
                // downstream consumer (objMethod/objEq) wrongly re-box an already-reference value.
                EmitExpr(e.GetProperty("e"));
                var t = MapType(e.GetProperty("type").GetString());
                _il.Emit(OpCodes.Isinst, t);
                return typeof(object);
            }
            case "safeCastValue":
            {
                // `x as? T` for value T -> `T?`: isinst boxed-T, then unbox+wrap, else empty Nullable<T>.
                var elem = MapType(e.GetProperty("elem").GetString());
                var nt = typeof(Nullable<>).MakeGenericType(elem);
                var res = _il.DeclareLocal(nt);
                var has = _il.DefineLabel(); var done = _il.DefineLabel();
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
            case "nullableNull":
            {
                // `null` typed as Int? -> a Nullable<T> with HasValue=false. NOT ldnull: a value type
                // has no null reference. `initobj` zero-inits the local (HasValue defaults to false).
                var elem = MapType(e.GetProperty("elem").GetString());
                var nt = typeof(Nullable<>).MakeGenericType(elem);
                var loc = _il.DeclareLocal(nt);
                _il.Emit(OpCodes.Ldloca, loc);
                _il.Emit(OpCodes.Initobj, nt);
                _il.Emit(OpCodes.Ldloc, loc);
                return nt;
            }
            case "nullableWrap":
            {
                // A present value -> `new Nullable<T>(v)` (the implicit T -> T? conversion).
                var elem = MapType(e.GetProperty("elem").GetString());
                var nt = typeof(Nullable<>).MakeGenericType(elem);
                EmitExpr(e.GetProperty("e"));
                _il.Emit(OpCodes.Newobj, nt.GetConstructor(new[] { elem }));
                return nt;
            }
            case "nullableHasValue":
            {
                // `x != null` for a value-nullable -> x.HasValue. The getter takes a `this` *address*,
                // so spill to a local and `ldloca`; never `callvirt` (Nullable<T> is a sealed value type).
                var elem = MapType(e.GetProperty("elem").GetString());
                var nt = typeof(Nullable<>).MakeGenericType(elem);
                EmitExpr(e.GetProperty("e"));
                var loc = _il.DeclareLocal(nt);
                _il.Emit(OpCodes.Stloc, loc);
                _il.Emit(OpCodes.Ldloca, loc);
                _il.Emit(OpCodes.Call, nt.GetProperty("HasValue").GetGetMethod());
                return typeof(bool);
            }
            case "nullableValue":
            {
                // `x!!` / the present-branch of `?:` -> x.Value (address-based call, like HasValue).
                var elem = MapType(e.GetProperty("elem").GetString());
                var nt = typeof(Nullable<>).MakeGenericType(elem);
                EmitExpr(e.GetProperty("e"));
                var loc = _il.DeclareLocal(nt);
                _il.Emit(OpCodes.Stloc, loc);
                _il.Emit(OpCodes.Ldloca, loc);
                _il.Emit(OpCodes.Call, nt.GetProperty("Value").GetGetMethod());
                return elem;
            }
            case "repeatInline":
            {
                // `repeat(n) { i -> body }` -> for (i = 0; i < n; i++) { body } (i bound to a loop local).
                var lv = _il.DeclareLocal(typeof(int)); _locals[e.GetProperty("var").GetString()] = lv;
                _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Stloc, lv);
                var cnt = _il.DeclareLocal(typeof(int)); EmitExpr(e.GetProperty("count")); _il.Emit(OpCodes.Stloc, cnt);
                var start = _il.DefineLabel(); var end = _il.DefineLabel();
                _loops.Add((LoopLabel(e), start, end));
                _il.MarkLabel(start);
                _il.Emit(OpCodes.Ldloc, lv); _il.Emit(OpCodes.Ldloc, cnt); _il.Emit(OpCodes.Bge, end);
                foreach (var b in e.GetProperty("body").EnumerateArray()) EmitStmt(b);
                _il.Emit(OpCodes.Ldloc, lv); _il.Emit(OpCodes.Ldc_I4_1); _il.Emit(OpCodes.Add); _il.Emit(OpCodes.Stloc, lv);
                _il.Emit(OpCodes.Br, start);
                _il.MarkLabel(end);
                _loops.RemoveAt(_loops.Count - 1);
                return typeof(void);
            }
            case "enumValue":
            {
                // An enum entry -> its ordinal constant, typed as the enum (box later yields the name).
                _il.Emit(OpCodes.Ldc_I4, e.GetProperty("ordinal").GetInt32());
                return MapType(e.GetProperty("type").GetString());
            }
            case "enumOrdinal":
                EmitExpr(e.GetProperty("e")); _il.Emit(OpCodes.Conv_I4); return typeof(int);
            case "enumValues":
            {
                // `Color.values()`/`entries` -> Enum.GetValues(typeof(Color)) cast to Color[] (TypeBuilder-safe).
                var et = MapType(e.GetProperty("type").GetString());
                _il.Emit(OpCodes.Ldtoken, et);
                _il.Emit(OpCodes.Call, typeof(Type).GetMethod("GetTypeFromHandle"));
                _il.Emit(OpCodes.Call, typeof(Enum).GetMethod("GetValues", new[] { typeof(Type) }));
                _il.Emit(OpCodes.Castclass, et.MakeArrayType());
                return et.MakeArrayType();
            }
            case "enumParse":
            {
                // `Color.valueOf(s)` -> (Color)Enum.Parse(typeof(Color), s).
                var et = MapType(e.GetProperty("type").GetString());
                _il.Emit(OpCodes.Ldtoken, et);
                _il.Emit(OpCodes.Call, typeof(Type).GetMethod("GetTypeFromHandle"));
                EmitExpr(e.GetProperty("arg"));
                _il.Emit(OpCodes.Call, typeof(Enum).GetMethod("Parse", new[] { typeof(Type), typeof(string) }));
                _il.Emit(OpCodes.Unbox_Any, et);
                return et;
            }
            case "objMethod":
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
            case "strRepeat":
            {
                // `s.repeat(n)` -> string.Concat(Enumerable.Repeat(s, n)).
                EmitExpr(e.GetProperty("s")); EmitExpr(e.GetProperty("n"));
                _il.Emit(OpCodes.Call, typeof(System.Linq.Enumerable).GetMethod("Repeat").MakeGenericMethod(typeof(string)));
                _il.Emit(OpCodes.Call, typeof(string).GetMethod("Concat", new[] { typeof(System.Collections.Generic.IEnumerable<string>) }));
                return typeof(string);
            }
            case "strReversed":
            {
                // `s.reversed()` -> new string(Enumerable.Reverse(s).ToArray()).
                EmitExpr(e.GetProperty("s"));
                _il.Emit(OpCodes.Call, typeof(System.Linq.Enumerable).GetMethods().First(m => m.Name == "Reverse" && m.GetParameters().Length == 1).MakeGenericMethod(typeof(char)));
                _il.Emit(OpCodes.Call, typeof(System.Linq.Enumerable).GetMethods().First(m => m.Name == "ToArray" && m.GetParameters().Length == 1).MakeGenericMethod(typeof(char)));
                _il.Emit(OpCodes.Newobj, typeof(string).GetConstructor(new[] { typeof(char[]) }));
                return typeof(string);
            }
            case "split":
            {
                // `s.split(seps…)` -> s.Split(string[] seps, StringSplitOptions.None) |> ToList<string>.
                EmitExpr(e.GetProperty("recv"));
                var seps = e.GetProperty("seps").EnumerateArray().ToList();
                _il.Emit(OpCodes.Ldc_I4, seps.Count);
                _il.Emit(OpCodes.Newarr, typeof(string));
                for (int i = 0; i < seps.Count; i++)
                {
                    _il.Emit(OpCodes.Dup); _il.Emit(OpCodes.Ldc_I4, i);
                    EmitExpr(seps[i]); _il.Emit(OpCodes.Stelem_Ref);
                }
                _il.Emit(OpCodes.Ldc_I4_0); // StringSplitOptions.None
                _il.Emit(OpCodes.Callvirt, typeof(string).GetMethod("Split", new[] { typeof(string[]), typeof(StringSplitOptions) }));
                var toList = typeof(System.Linq.Enumerable).GetMethods().First(m => m.Name == "ToList" && m.GetParameters().Length == 1).MakeGenericMethod(typeof(string));
                _il.Emit(OpCodes.Call, toList);
                return typeof(System.Collections.Generic.List<string>);
            }
            case "associateWith":
            case "associateBy":
            {
                // associateWith{v}: d[x]=sel(x); associateBy{k}: d[sel(x)]=x.
                bool byKey = e.GetProperty("k").GetString() == "associateBy";
                var kt = MapType(e.GetProperty("keyType").GetString());
                var vt = MapType(e.GetProperty("valType").GetString());
                var elemT = byKey ? vt : kt;                                  // src element type
                var selFn = typeof(Func<,>).MakeGenericType(elemT, byKey ? kt : vt);
                var dt = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(kt, vt);
                EmitExpr(e.GetProperty("sel")); var sel = _il.DeclareLocal(selFn); _il.Emit(OpCodes.Stloc, sel);
                _il.Emit(OpCodes.Newobj, dt.GetConstructor(Type.EmptyTypes)); var d = _il.DeclareLocal(dt); _il.Emit(OpCodes.Stloc, d);
                EmitForEachOf(e.GetProperty("src"), elemT, x =>
                {
                    _il.Emit(OpCodes.Ldloc, d);
                    if (byKey) { _il.Emit(OpCodes.Ldloc, sel); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, selFn.GetMethod("Invoke")); _il.Emit(OpCodes.Ldloc, x); }
                    else { _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Ldloc, sel); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, selFn.GetMethod("Invoke")); }
                    _il.Emit(OpCodes.Callvirt, dt.GetMethod("set_Item"));
                });
                _il.Emit(OpCodes.Ldloc, d);
                return dt;
            }
            case "groupBy":
            {
                // groupBy{k}: d=Dictionary<K,List<E>>; for x: k=sel(x); d.GetOrAdd(k).Add(x).
                var kt = MapType(e.GetProperty("keyType").GetString());
                var elemT = MapType(e.GetProperty("elemType").GetString());
                var listT = typeof(System.Collections.Generic.List<>).MakeGenericType(elemT);
                var dt = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(kt, listT);
                var selFn = typeof(Func<,>).MakeGenericType(elemT, kt);
                EmitExpr(e.GetProperty("sel")); var sel = _il.DeclareLocal(selFn); _il.Emit(OpCodes.Stloc, sel);
                _il.Emit(OpCodes.Newobj, dt.GetConstructor(Type.EmptyTypes)); var d = _il.DeclareLocal(dt); _il.Emit(OpCodes.Stloc, d);
                var k = _il.DeclareLocal(kt);
                EmitForEachOf(e.GetProperty("src"), elemT, x =>
                {
                    _il.Emit(OpCodes.Ldloc, sel); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, selFn.GetMethod("Invoke")); _il.Emit(OpCodes.Stloc, k);
                    var have = _il.DefineLabel();
                    _il.Emit(OpCodes.Ldloc, d); _il.Emit(OpCodes.Ldloc, k); _il.Emit(OpCodes.Callvirt, dt.GetMethod("ContainsKey")); _il.Emit(OpCodes.Brtrue, have);
                    _il.Emit(OpCodes.Ldloc, d); _il.Emit(OpCodes.Ldloc, k); _il.Emit(OpCodes.Newobj, listT.GetConstructor(Type.EmptyTypes)); _il.Emit(OpCodes.Callvirt, dt.GetMethod("set_Item"));
                    _il.MarkLabel(have);
                    _il.Emit(OpCodes.Ldloc, d); _il.Emit(OpCodes.Ldloc, k); _il.Emit(OpCodes.Callvirt, dt.GetMethod("get_Item")); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, listT.GetMethod("Add"));
                });
                _il.Emit(OpCodes.Ldloc, d);
                return dt;
            }
            case "linqPartition":
            {
                // `partition { pred }` -> (matched, unmatched) : ValueTuple<List<T>, List<T>>.
                var elemT = MapType(e.GetProperty("elem").GetString());
                var listT = typeof(System.Collections.Generic.List<>).MakeGenericType(elemT);
                var predFn = typeof(Func<,>).MakeGenericType(elemT, typeof(bool));
                EmitExpr(e.GetProperty("pred")); var p = _il.DeclareLocal(predFn); _il.Emit(OpCodes.Stloc, p);
                _il.Emit(OpCodes.Newobj, listT.GetConstructor(Type.EmptyTypes)); var m = _il.DeclareLocal(listT); _il.Emit(OpCodes.Stloc, m);
                _il.Emit(OpCodes.Newobj, listT.GetConstructor(Type.EmptyTypes)); var u = _il.DeclareLocal(listT); _il.Emit(OpCodes.Stloc, u);
                var add = listT.GetMethod("Add");
                EmitForEachOf(e.GetProperty("src"), elemT, x =>
                {
                    var elseL = _il.DefineLabel(); var end = _il.DefineLabel();
                    _il.Emit(OpCodes.Ldloc, p); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, predFn.GetMethod("Invoke"));
                    _il.Emit(OpCodes.Brfalse, elseL);
                    _il.Emit(OpCodes.Ldloc, m); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, add); _il.Emit(OpCodes.Br, end);
                    _il.MarkLabel(elseL); _il.Emit(OpCodes.Ldloc, u); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, add);
                    _il.MarkLabel(end);
                });
                var vtP = ResolveType("System.ValueTuple`2").MakeGenericType(listT, listT);
                _il.Emit(OpCodes.Ldloc, m); _il.Emit(OpCodes.Ldloc, u); _il.Emit(OpCodes.Newobj, vtP.GetConstructor(new[] { listT, listT }));
                return vtP;
            }
            case "linqWithIndex":
            {
                // `withIndex()` -> List<ValueTuple<int,T>>; `for ((i,v) in …)` destructures (component1/2 -> Item1/2).
                var elemT = MapType(e.GetProperty("elem").GetString());
                var vtW = ResolveType("System.ValueTuple`2").MakeGenericType(typeof(int), elemT);
                var listT = typeof(System.Collections.Generic.List<>).MakeGenericType(vtW);
                _il.Emit(OpCodes.Newobj, listT.GetConstructor(Type.EmptyTypes)); var l = _il.DeclareLocal(listT); _il.Emit(OpCodes.Stloc, l);
                var i = _il.DeclareLocal(typeof(int)); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Stloc, i);
                var add = listT.GetMethod("Add");
                EmitForEachOf(e.GetProperty("src"), elemT, x =>
                {
                    _il.Emit(OpCodes.Ldloc, l);
                    _il.Emit(OpCodes.Ldloc, i); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Newobj, vtW.GetConstructor(new[] { typeof(int), elemT }));
                    _il.Emit(OpCodes.Callvirt, add);
                    _il.Emit(OpCodes.Ldloc, i); _il.Emit(OpCodes.Ldc_I4_1); _il.Emit(OpCodes.Add); _il.Emit(OpCodes.Stloc, i);
                });
                _il.Emit(OpCodes.Ldloc, l); return listT;
            }
            case "linqAssociate":
            {
                // `associate { it to (k,v) }` -> Dictionary<K,V> from a selector returning a Pair (ValueTuple<K,V>).
                var elemT = MapType(e.GetProperty("elem").GetString());
                var kt = MapType(e.GetProperty("keyType").GetString());
                var vt2 = MapType(e.GetProperty("valType").GetString());
                var pairT = ResolveType("System.ValueTuple`2").MakeGenericType(kt, vt2);
                var selFn = typeof(Func<,>).MakeGenericType(elemT, pairT);
                var dt = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(kt, vt2);
                EmitExpr(e.GetProperty("sel")); var f = _il.DeclareLocal(selFn); _il.Emit(OpCodes.Stloc, f);
                _il.Emit(OpCodes.Newobj, dt.GetConstructor(Type.EmptyTypes)); var d = _il.DeclareLocal(dt); _il.Emit(OpCodes.Stloc, d);
                var pair = _il.DeclareLocal(pairT);
                EmitForEachOf(e.GetProperty("src"), elemT, x =>
                {
                    _il.Emit(OpCodes.Ldloc, f); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, selFn.GetMethod("Invoke")); _il.Emit(OpCodes.Stloc, pair);
                    _il.Emit(OpCodes.Ldloc, d);
                    _il.Emit(OpCodes.Ldloca, pair); _il.Emit(OpCodes.Ldfld, pairT.GetField("Item1"));
                    _il.Emit(OpCodes.Ldloca, pair); _il.Emit(OpCodes.Ldfld, pairT.GetField("Item2"));
                    _il.Emit(OpCodes.Callvirt, dt.GetMethod("set_Item"));
                });
                _il.Emit(OpCodes.Ldloc, d); return dt;
            }
            case "linqScan":
            {
                // `scan/runningFold(init){acc,e -> }` -> List<acc> = [init, op(init,e0), op(prev,e1), …].
                var elemT = MapType(e.GetProperty("elem").GetString());
                var accT = MapType(e.GetProperty("accType").GetString());
                var listT = typeof(System.Collections.Generic.List<>).MakeGenericType(accT);
                var opFn = typeof(Func<,,>).MakeGenericType(accT, elemT, accT);
                EmitExpr(e.GetProperty("op")); var f = _il.DeclareLocal(opFn); _il.Emit(OpCodes.Stloc, f);
                _il.Emit(OpCodes.Newobj, listT.GetConstructor(Type.EmptyTypes)); var l = _il.DeclareLocal(listT); _il.Emit(OpCodes.Stloc, l);
                EmitArg(e.GetProperty("init"), accT); var acc = _il.DeclareLocal(accT); _il.Emit(OpCodes.Stloc, acc);
                var add = listT.GetMethod("Add");
                _il.Emit(OpCodes.Ldloc, l); _il.Emit(OpCodes.Ldloc, acc); _il.Emit(OpCodes.Callvirt, add);
                EmitForEachOf(e.GetProperty("src"), elemT, x =>
                {
                    _il.Emit(OpCodes.Ldloc, f); _il.Emit(OpCodes.Ldloc, acc); _il.Emit(OpCodes.Ldloc, x); _il.Emit(OpCodes.Callvirt, opFn.GetMethod("Invoke")); _il.Emit(OpCodes.Stloc, acc);
                    _il.Emit(OpCodes.Ldloc, l); _il.Emit(OpCodes.Ldloc, acc); _il.Emit(OpCodes.Callvirt, add);
                });
                _il.Emit(OpCodes.Ldloc, l); return listT;
            }
            case "linqWindowed":
            {
                // `windowed(size)` -> List<List<T>> sliding windows (step 1, no partial windows).
                var elemT = MapType(e.GetProperty("elem").GetString());
                var listT = typeof(System.Collections.Generic.List<>).MakeGenericType(elemT);
                var outerT = typeof(System.Collections.Generic.List<>).MakeGenericType(listT);
                var toList = typeof(System.Linq.Enumerable).GetMethods().First(mm => mm.Name == "ToList" && mm.GetParameters().Length == 1).MakeGenericMethod(elemT);
                EmitExpr(e.GetProperty("src")); _il.Emit(OpCodes.Call, toList); var arr = _il.DeclareLocal(listT); _il.Emit(OpCodes.Stloc, arr);
                EmitExpr(e.GetProperty("size")); var size = _il.DeclareLocal(typeof(int)); _il.Emit(OpCodes.Stloc, size);
                _il.Emit(OpCodes.Newobj, outerT.GetConstructor(Type.EmptyTypes)); var outl = _il.DeclareLocal(outerT); _il.Emit(OpCodes.Stloc, outl);
                var getRange = listT.GetMethod("GetRange", new[] { typeof(int), typeof(int) });
                var getCount = listT.GetMethod("get_Count");
                var iw = _il.DeclareLocal(typeof(int)); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Stloc, iw);
                // test-at-top loop (the back-branch target has a known stack height via the fall-through from init).
                var top = _il.DefineLabel(); var done = _il.DefineLabel();
                _il.MarkLabel(top);
                _il.Emit(OpCodes.Ldloc, iw); _il.Emit(OpCodes.Ldloc, size); _il.Emit(OpCodes.Add);
                _il.Emit(OpCodes.Ldloc, arr); _il.Emit(OpCodes.Callvirt, getCount);
                _il.Emit(OpCodes.Bgt, done);     // (iw + size) > count -> stop
                _il.Emit(OpCodes.Ldloc, outl); _il.Emit(OpCodes.Ldloc, arr); _il.Emit(OpCodes.Ldloc, iw); _il.Emit(OpCodes.Ldloc, size); _il.Emit(OpCodes.Callvirt, getRange); _il.Emit(OpCodes.Callvirt, outerT.GetMethod("Add"));
                _il.Emit(OpCodes.Ldloc, iw); _il.Emit(OpCodes.Ldc_I4_1); _il.Emit(OpCodes.Add); _il.Emit(OpCodes.Stloc, iw);
                _il.Emit(OpCodes.Br, top);
                _il.MarkLabel(done);
                _il.Emit(OpCodes.Ldloc, outl); return outerT;
            }
            case "linqGetOrElse":
            {
                // `getOrElse(index){ default(index) }` -> in-bounds ? src[index] : default(index).
                var elemT = MapType(e.GetProperty("elem").GetString());
                var listT = typeof(System.Collections.Generic.List<>).MakeGenericType(elemT);
                var defFn = typeof(Func<,>).MakeGenericType(typeof(int), elemT);
                var toList = typeof(System.Linq.Enumerable).GetMethods().First(mm => mm.Name == "ToList" && mm.GetParameters().Length == 1).MakeGenericMethod(elemT);
                EmitExpr(e.GetProperty("src")); _il.Emit(OpCodes.Call, toList); var arr = _il.DeclareLocal(listT); _il.Emit(OpCodes.Stloc, arr);
                EmitExpr(e.GetProperty("index")); var idx = _il.DeclareLocal(typeof(int)); _il.Emit(OpCodes.Stloc, idx);
                EmitExpr(e.GetProperty("default")); var df = _il.DeclareLocal(defFn); _il.Emit(OpCodes.Stloc, df);
                var elseL = _il.DefineLabel(); var end = _il.DefineLabel();
                _il.Emit(OpCodes.Ldloc, idx); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Blt, elseL);
                _il.Emit(OpCodes.Ldloc, idx); _il.Emit(OpCodes.Ldloc, arr); _il.Emit(OpCodes.Callvirt, listT.GetMethod("get_Count")); _il.Emit(OpCodes.Bge, elseL);
                _il.Emit(OpCodes.Ldloc, arr); _il.Emit(OpCodes.Ldloc, idx); _il.Emit(OpCodes.Callvirt, listT.GetMethod("get_Item")); _il.Emit(OpCodes.Br, end);
                _il.MarkLabel(elseL); _il.Emit(OpCodes.Ldloc, df); _il.Emit(OpCodes.Ldloc, idx); _il.Emit(OpCodes.Callvirt, defFn.GetMethod("Invoke"));
                _il.MarkLabel(end);
                return elemT;
            }
            case "mapNew":
            {
                // `mapOf(k to v, …)` -> new Dictionary<K,V> { [k]=v, … } via set_Item.
                var kt = MapType(e.GetProperty("keyType").GetString());
                var vt = MapType(e.GetProperty("valType").GetString());
                var dt = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(kt, vt);
                _il.Emit(OpCodes.Newobj, GenericCtor(dt));
                var setItem = GenericMethod(dt, "set_Item");
                foreach (var en in e.GetProperty("entries").EnumerateArray())
                {
                    _il.Emit(OpCodes.Dup);
                    EmitArg(en.GetProperty("key"), kt);
                    EmitArg(en.GetProperty("val"), vt);
                    _il.Emit(OpCodes.Callvirt, setItem);
                }
                return dt;
            }
            case "listGet":
            {
                var elem = MapType(e.GetProperty("elem").GetString());
                var lt = typeof(System.Collections.Generic.List<>).MakeGenericType(elem);
                EmitExpr(e.GetProperty("list")); EmitExpr(e.GetProperty("index"));
                _il.Emit(OpCodes.Callvirt, lt.GetMethod("get_Item"));
                return elem;
            }
            case "listSet":
            {
                var elem = MapType(e.GetProperty("elem").GetString());
                var lt = typeof(System.Collections.Generic.List<>).MakeGenericType(elem);
                EmitExpr(e.GetProperty("list")); EmitExpr(e.GetProperty("index")); EmitArg(e.GetProperty("value"), elem);
                _il.Emit(OpCodes.Callvirt, lt.GetMethod("set_Item"));
                return typeof(void);
            }
            case "mapGet":
            {
                var kt = MapType(e.GetProperty("keyType").GetString());
                var vt = MapType(e.GetProperty("valType").GetString());
                var dt = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(kt, vt);
                EmitExpr(e.GetProperty("map"));
                EmitArg(e.GetProperty("key"), kt);
                _il.Emit(OpCodes.Callvirt, dt.GetMethod("get_Item"));
                return vt;
            }
            case "mapSet":
            {
                var kt = MapType(e.GetProperty("keyType").GetString());
                var vt = MapType(e.GetProperty("valType").GetString());
                var dt = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(kt, vt);
                EmitExpr(e.GetProperty("map"));
                EmitArg(e.GetProperty("key"), kt);
                EmitArg(e.GetProperty("value"), vt);
                _il.Emit(OpCodes.Callvirt, dt.GetMethod("set_Item"));
                return typeof(void);
            }
            case "mapSize":
            {
                var kt = MapType(e.GetProperty("keyType").GetString());
                var vt = MapType(e.GetProperty("valType").GetString());
                var dt = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(kt, vt);
                EmitExpr(e.GetProperty("map"));
                _il.Emit(OpCodes.Callvirt, dt.GetMethod("get_Count"));
                return typeof(int);
            }
            case "setNew":
            {
                // `setOf(...)` -> new HashSet<elem> { ... } via repeated Add (Add returns bool -> pop).
                var elem = MapType(e.GetProperty("elem").GetString());
                var setT = typeof(System.Collections.Generic.HashSet<>).MakeGenericType(elem);
                _il.Emit(OpCodes.Newobj, GenericCtor(setT));
                var add = GenericMethod(setT, "Add");
                foreach (var item in e.GetProperty("elems").EnumerateArray())
                {
                    _il.Emit(OpCodes.Dup);
                    EmitArg(item, elem);
                    _il.Emit(OpCodes.Callvirt, add);
                    _il.Emit(OpCodes.Pop);
                }
                return setT;
            }
            case "linqSum":
            {
                // `sum()` -> the non-generic Enumerable.Sum(IEnumerable<elem>) overload for that numeric element.
                var elem = MapType(e.GetProperty("elem").GetString());
                var ienum = typeof(System.Collections.Generic.IEnumerable<>).MakeGenericType(elem);
                var mi = typeof(System.Linq.Enumerable).GetMethod("Sum", new[] { ienum });
                EmitExpr(e.GetProperty("src"));
                _il.Emit(OpCodes.Call, mi);
                return mi.ReturnType;
            }
            case "linqSumOf":
            {
                // `sumOf { selector }` -> Sum<T>(IEnumerable<T>, Func<T,selRet>); pick the overload by selector return type.
                var t = MapType(e.GetProperty("elem").GetString());
                var selRet = MapType(e.GetProperty("selRet").GetString());
                var def = typeof(System.Linq.Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .First(m => m.Name == "Sum" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2
                             && m.GetParameters()[1].ParameterType.IsGenericType
                             && m.GetParameters()[1].ParameterType.GetGenericArguments().Last() == selRet)
                    .MakeGenericMethod(t);
                EmitExpr(e.GetProperty("src"));
                EmitArg(e.GetProperty("sel"), def.GetParameters()[1].ParameterType);
                _il.Emit(OpCodes.Call, def);
                return def.ReturnType;
            }
            case "throwExpr":
            {
                // A throwing expression (error()/TODO()/exhaustive-when else): construct + throw; no value reaches a merge.
                EmitExpr(e.GetProperty("value"));
                _il.Emit(OpCodes.Throw);
                return typeof(object);
            }
            case "returnExpr":
            {
                // `return` in expression position: emit the method return; no value reaches the surrounding merge
                // (mirrors the "return" statement, incl. the protected-region leave).
                if (_tryStack.Count > 0)
                {
                    var ctx = _tryStack.Peek();
                    if (e.TryGetProperty("value", out var trv)) { EmitExpr(trv); if (ctx.result != null) _il.Emit(OpCodes.Stloc, ctx.result); else _il.Emit(OpCodes.Pop); }
                    _il.Emit(OpCodes.Leave, ctx.end);
                }
                else
                {
                    if (e.TryGetProperty("value", out var rv))
                    {
                        var got = EmitExpr(rv);
                        if (got != null && _methodRetType.IsGenericType && _methodRetType.GetGenericTypeDefinition() == typeof(Nullable<>)
                            && _methodRetType.GetGenericArguments()[0] == got)
                            _il.Emit(OpCodes.Newobj, _methodRetType.GetConstructor(new[] { got }));
                    }
                    _il.Emit(OpCodes.Ret);
                }
                return typeof(object);
            }
            case "tupleNew":
            {
                // `a to b` -> new System.ValueTuple<A,B>(a, b) (value type; newobj leaves the struct on the stack).
                var elems = e.GetProperty("elems").EnumerateArray().Select(a => MapType(a.GetString())).ToArray();
                var vt = ResolveType("System.ValueTuple`" + elems.Length).MakeGenericType(elems);
                var args = e.GetProperty("args").EnumerateArray().ToList();
                for (int i = 0; i < args.Count; i++) EmitArg(args[i], elems[i]);
                _il.Emit(OpCodes.Newobj, vt.GetConstructor(elems));
                return vt;
            }
            case "tupleItem":
            {
                // `.first`/`.second`/`.third` -> ValueTuple ItemN field (public field, not a property).
                var vt = MapType(e.GetProperty("tupleType").GetString());
                EmitExpr(e.GetProperty("recv"));
                var fld = vt.GetField("Item" + e.GetProperty("index").GetInt32());
                _il.Emit(OpCodes.Ldfld, fld);
                return fld.FieldType;
            }
            case "delegateNew":
            {
                // Non-capturing lambda: bind the lifted static method into a Func/Action delegate.
                var ft = MapType(e.GetProperty("funcType").GetString());
                var mb = FindStatic(e.GetProperty("method").GetString());
                _il.Emit(OpCodes.Ldnull);
                _il.Emit(OpCodes.Ldftn, mb);
                _il.Emit(OpCodes.Newobj, DelegateCtor(ft));
                return ft;
            }
            case "boundDelegateNew":
            {
                // `obj::method` -> a delegate bound to the receiver. ldvirtftn needs the object twice (dup); a
                // final method uses ldftn (the target stays on the stack as the delegate's first ctor arg).
                var ft = MapType(e.GetProperty("funcType").GetString());
                var mb = FindMethod(e.GetProperty("ownerType").GetString(), e.GetProperty("method").GetString());
                EmitExpr(e.GetProperty("recv"));
                if (e.GetProperty("virtual").GetBoolean()) { _il.Emit(OpCodes.Dup); _il.Emit(OpCodes.Ldvirtftn, mb); }
                else _il.Emit(OpCodes.Ldftn, mb);
                _il.Emit(OpCodes.Newobj, DelegateCtor(ft));
                return ft;
            }
            case "boundClrDelegateNew":
            {
                // `netObj::method` -> a delegate bound to a .NET instance method (resolved by reflection).
                var ft = MapType(e.GetProperty("funcType").GetString());
                var type = ClrRef(e.GetProperty("clrType").GetString());
                var argTypes = e.GetProperty("argTypes").EnumerateArray().Select(a => ClrRef(a.GetString())).ToArray();
                var mi = type.GetMethod(e.GetProperty("method").GetString(),
                    BindingFlags.Public | BindingFlags.Instance, null, argTypes, null)
                    ?? type.GetMethod(e.GetProperty("method").GetString());
                EmitExpr(e.GetProperty("recv"));
                if (e.GetProperty("virtual").GetBoolean()) { _il.Emit(OpCodes.Dup); _il.Emit(OpCodes.Ldvirtftn, mi); }
                else _il.Emit(OpCodes.Ldftn, mi);
                _il.Emit(OpCodes.Newobj, DelegateCtor(ft));
                return ft;
            }
            case "delegateInvoke":
            {
                // A splice's invocation of a lambda PARAM -> inline the caller's lambda body (binding its param to the
                // invoke arg) right here, so a non-local `return` in it returns from THIS (the caller's) method.
                var recv0 = e.GetProperty("recv");
                if (recv0.TryGetProperty("k", out var rk) && rk.GetString() == "local"
                    && _inlineLambdas.TryGetValue(recv0.GetProperty("name").GetString(), out var lam))
                {
                    var iargs = e.GetProperty("args").EnumerateArray().ToList();
                    var had = _inlineSubst.TryGetValue(lam.lamParam, out var prev);
                    if (iargs.Count > 0) _inlineSubst[lam.lamParam] = iargs[0];   // bind the lambda's param to the invoke arg
                    EmitSplicedStmts(lam.body);
                    if (had) _inlineSubst[lam.lamParam] = prev; else _inlineSubst.Remove(lam.lamParam);
                    return typeof(void);
                }
                var ft = MapType(e.GetProperty("funcType").GetString());
                EmitExpr(e.GetProperty("recv"));
                foreach (var a in e.GetProperty("args").EnumerateArray()) EmitExpr(a);
                _il.Emit(OpCodes.Callvirt, InvokeOf(ft));
                return FuncRetType(e.GetProperty("funcType").GetString());
            }
            case "inlineSplice": return EmitInlineSplice(e);
            case "closureNew":
            {
                // Capturing lambda: `new Closure(captures)` then bind its `invoke` instance method as a delegate.
                var ct = _types[e.GetProperty("closureType").GetString()];
                foreach (var c in e.GetProperty("captures").EnumerateArray()) EmitExpr(c);
                _il.Emit(OpCodes.Newobj, ct.Ctor);           // closure instance is the delegate target
                _il.Emit(OpCodes.Ldftn, ct.Methods[e.GetProperty("method").GetString()]);
                var ft = MapType(e.GetProperty("funcType").GetString());
                _il.Emit(OpCodes.Newobj, DelegateCtor(ft));
                return ft;
            }
            case "concat": return EmitConcat(e);
            case "cond": return EmitCond(e);
            case "clrNew": return EmitClrNew(e);
            case "clrStatic": return EmitClrCall(e, instance: false);
            case "clrInstance": return EmitClrCall(e, instance: true);
            case "clrPropGet": return EmitClrPropGet(e);
            case "clrPropSet": return EmitClrPropSet(e);
            case "clrEventAdd": return EmitClrEvent(e, add: true);
            case "clrEventRemove": return EmitClrEvent(e, add: false);
            case "byrefOf":
            {
                // The live managed pointer behind `byref(...)` in a `var x by` delegate: keep a ref return's pointer
                // (deref:false), or take the address of a local/field lvalue.
                var inner = e.GetProperty("inner");
                var ik = inner.GetProperty("k").GetString();
                if (ik == "clrInstance") return EmitClrCall(inner, instance: true, deref: false);
                if (ik == "clrStatic") return EmitClrCall(inner, instance: false, deref: false);
                EmitAddr(inner);
                return null;
            }
            case "stackAlloc":
            {
                // `localloc` a zero-initialized stack buffer of `count * sizeof(elem)` bytes, leaving its pointer.
                // (Unverifiable, like C#'s own stackalloc.)
                var elem = MapType(e.GetProperty("elem").GetString());
                var bc = _il.DeclareLocal(typeof(int));
                EmitExpr(e.GetProperty("count"));
                _il.Emit(OpCodes.Sizeof, elem);
                _il.Emit(OpCodes.Mul);
                _il.Emit(OpCodes.Dup); _il.Emit(OpCodes.Stloc, bc);   // keep byteCount for initblk
                _il.Emit(OpCodes.Conv_U);
                _il.Emit(OpCodes.Localloc);
                _il.Emit(OpCodes.Dup); _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Ldloc, bc); _il.Emit(OpCodes.Initblk);
                return typeof(byte).MakePointerType();
            }
            case "stackGet":
            {
                EmitStackBounds(e);
                var elem = MapType(e.GetProperty("elem").GetString());
                EmitStackAddr(e, elem);
                _il.Emit(OpCodes.Ldobj, elem);
                return elem;
            }
            case "stackSet":
            {
                EmitStackBounds(e);
                var elem = MapType(e.GetProperty("elem").GetString());
                EmitStackAddr(e, elem);
                EmitArg(e.GetProperty("value"), elem);
                _il.Emit(OpCodes.Stobj, elem);
                return typeof(void);
            }
            case "stackAsSpan":
            {
                // `new System.Span<T>(void* ptr, int length)` over the stack buffer -> a real Span for .NET APIs.
                var elem = MapType(e.GetProperty("elem").GetString());
                var spanT = typeof(System.Span<>).MakeGenericType(elem);
                var ctor = spanT.GetConstructor(new[] { typeof(void*), typeof(int) });
                EmitExpr(e.GetProperty("ptr"));
                EmitExpr(e.GetProperty("len"));
                _il.Emit(OpCodes.Newobj, ctor);
                return spanT;
            }
            case "byrefLoad":
            {
                // Read through a byref local (the ClrRef delegate): ldloc the pointer, ldobj to dereference.
                _il.Emit(OpCodes.Ldloc, _locals[e.GetProperty("local").GetString()]);
                var elem = MapType(e.GetProperty("elem").GetString());
                _il.Emit(OpCodes.Ldobj, elem);
                return elem;
            }
            case "byrefStore":
            {
                // Write through a byref local: ldloc the pointer, push the value, stobj.
                _il.Emit(OpCodes.Ldloc, _locals[e.GetProperty("local").GetString()]);
                var elem = MapType(e.GetProperty("elem").GetString());
                EmitArg(e.GetProperty("value"), elem);
                _il.Emit(OpCodes.Stobj, elem);
                return typeof(void);
            }
            case "unsupportedExpr": throw new NotSupportedException("the .NET backend does not support this Kotlin construct: " + e.GetProperty("of").GetString());
            default: throw new NotSupportedException("expr " + e.GetProperty("k").GetString());
        }
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
            case "double": _il.Emit(OpCodes.Ldc_R8, v.GetDouble()); return typeof(double);
            case "float": _il.Emit(OpCodes.Ldc_R4, v.GetSingle()); return typeof(float);
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
        var cad = mi.GetCustomAttributesData().FirstOrDefault(c => c.AttributeType.FullName == "DotKt.Metadata.KotlinInlineAttribute")
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
    // Build a .NET custom attribute from a BIR `attr` node (a user annotation): the synthesized `: System.Attribute`
    // class's ctor + compile-time-constant args.
    CustomAttributeBuilder BuildCab(JsonElement a)
    {
        var attr = a.GetProperty("attr").GetString();
        var args = a.GetProperty("args").EnumerateArray().Select(ConstArgValue).ToArray();
        if (attr.StartsWith("clr:"))
        {
            // An imported .NET attribute (#54): bind its real constructor (resolved by the declared arg types,
            // falling back to arity) and apply it with the constant args.
            var at = ClrRef(attr);
            var argTypes = a.GetProperty("argTypes").EnumerateArray().Select(s => ClrRef(s.GetString())).ToArray();
            var nctor = at.GetConstructor(argTypes)
                        ?? at.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == args.Length);
            return new CustomAttributeBuilder(nctor, args);
        }
        var ti = _types[attr];
        var ctor = ti.Ctors.Count > 0 ? ti.Ctors[0] : ti.TB.DefineDefaultConstructor(MethodAttributes.Public);
        return new CustomAttributeBuilder(ctor, args);
    }

    static object ConstArgValue(JsonElement e)
    {
        // Annotation arguments are always compile-time constants (const nodes).
        if (!e.TryGetProperty("value", out var v)) return null;
        switch (v.ValueKind)
        {
            case JsonValueKind.String: return v.GetString();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number:
                return e.GetProperty("type").GetString() switch
                {
                    "long" => (object)v.GetInt64(),
                    "double" => v.GetDouble(),
                    "float" => (float)v.GetDouble(),
                    "short" => (short)v.GetInt32(),
                    "byte" => (sbyte)v.GetInt32(),
                    _ => v.GetInt32(),
                };
            default: return null;
        }
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
        var cands = type.GetMethods(BindingFlags.Public | (instance ? BindingFlags.Instance : BindingFlags.Static))
            .Where(m => m.Name == name && m.IsGenericMethodDefinition
                     && m.GetGenericArguments().Length == typeArgCount
                     && m.GetParameters().Length == shapes.Length
                     && m.GetParameters().Select((p, i) => Shape(p.ParameterType) == shapes[i]).All(x => x))
            .ToList();
        return cands.First().MakeGenericMethod(typeArgs);
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
