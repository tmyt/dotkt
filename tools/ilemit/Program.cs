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
            if (rest[i] == "--ref" && i + 1 < rest.Count) { try { Assembly.LoadFrom(Path.GetFullPath(rest[++i])); } catch { } }
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

    // per-method context
    ILGenerator _il;
    readonly Dictionary<string, int> _args = new();
    readonly Dictionary<string, Type> _argTypes = new();
    readonly Dictionary<string, LocalBuilder> _locals = new();
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
    Dictionary<string, FieldBuilder> _coFields;
    // try-around-await: inside a try region of a MoveNext, `ret` is illegal — suspension/return `leave` to the
    // single method exit instead. Depth > 0 while emitting steps between coTryBegin and coTryEnd.
    int _coTryDepth;
    Label _coExit;

    public Emitter(string outDir, string asmName) { _outDir = outDir; _asmName = asmName; }

    public void EmitAssembly(List<JsonElement> files)
    {
        // NOTE (reverse-interop polish, 5.2): the emitted assembly's core type refs point at System.Private.CoreLib
        // (the impl assembly) because BCL types are resolved by runtime reflection. A standalone exe runs fine and
        // any .NET host can reflection-load it, but a C# project that <Reference>s it at COMPILE time hits CS0012
        // (Object lives in an unreferenced assembly). The proper fix is to resolve BCL types via a MetadataLoadContext
        // over reference assemblies so refs become System.Runtime — deferred (docs/csharp-retirement-design.md 5.2).
        var ab = new PersistedAssemblyBuilder(new AssemblyName(_asmName), typeof(object).Assembly);
        _mod = ab.DefineDynamicModule(_asmName);

        // Pass 1: DefineType for every file-static-class and every user class.
        foreach (var file in files)
        {
            var fileClass = file.GetProperty("fileClass").GetString();
            if (file.GetProperty("methods").GetArrayLength() > 0)
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
                    // Top-level CLR types are Public or NotPublic (assembly-internal); private/internal -> NotPublic.
                    var typeAccess = (t.TryGetProperty("vis", out var tv) ? tv.GetString() : "public") == "public"
                        ? TypeAttributes.Public : TypeAttributes.NotPublic;
                    var attrs = isIface
                        ? typeAccess | TypeAttributes.Interface | TypeAttributes.Abstract
                        : typeAccess | TypeAttributes.Class;
                    // An `abstract`/`sealed`(Kotlin) class -> a CLR abstract class (cannot be instantiated; may hold
                    // abstract members). Kotlin `sealed` is also abstract at the CLR level.
                    if (!isIface && t.TryGetProperty("abstract", out var clsAbs) && clsAbs.GetBoolean()) attrs |= TypeAttributes.Abstract;
                    var tb = _mod.DefineType(name, attrs);
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
                    // `Container[int]` -> implement the constructed generic interface, not the open definition.
                    var (open, constructed) = ParseOwner(i.GetString());
                    ti.TB.AddInterfaceImplementation(constructed ?? (Type)_types[open].TB);
                }
        }
        _curTypeParams = null;

        // Pass 3: declare fields, ctors, methods (signatures) so cross-refs resolve.
        foreach (var ti in _types.Values)
        {
            if (ti.IsEnum) continue;   // enums are fully defined (literals) in pass 1
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
                        var fattrs = FieldAttributes.Public;
                        if (f.TryGetProperty("static", out var st) && st.GetBoolean()) fattrs |= FieldAttributes.Static;
                        ti.Fields[f.GetProperty("name").GetString()] =
                            ti.TB.DefineField(f.GetProperty("name").GetString(), MapType(f.GetProperty("type").GetString()), fattrs);
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
                    var (open, constructed) = ParseOwner(i.GetString());
                    var iface = _types[open];
                    foreach (var im in iface.Methods)
                    {
                        var ifaceMethod = constructed != null ? TypeBuilder.GetMethod(constructed, im.Value) : (MethodInfo)im.Value;
                        ti.TB.DefineMethodOverride(FindMethod(ti.TB.Name, im.Key), ifaceMethod);
                    }
                }

        // Pass 4: emit all bodies (every ctor/method signature already exists).
        foreach (var ti in _types.Values)
            for (int ci = 0; ci < ti.Ctors.Count; ci++) EmitCtorBody(ti, ti.Ctors[ci], ti.CtorDefs[ci]);
        foreach (var ti in _types.Values)
            if (!ti.IsInterface && !ti.IsEnum)
                foreach (var m in ti.Def.GetProperty("methods").EnumerateArray()) EmitMethodBody(ti, m);

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
        foreach (var ti in Ordered()) { if (!ti.IsEnum) ti.TB.CreateType(); }

        Save(ab, entry);
    }

    IEnumerable<TypeInfo> Ordered()
    {
        var done = new HashSet<string>();
        var result = new List<TypeInfo>();
        void Visit(TypeInfo ti)
        {
            var key = ti.AsType.Name;
            if (!done.Add(key)) return;
            if (ti.BaseName != null && _types.TryGetValue(ti.BaseName, out var b)) Visit(b);
            // A generic interface used as a constructed parent/interface must be created before its implementers
            // (PersistedAssemblyBuilder materializes the instantiation at the implementer's CreateType).
            if (!ti.IsFileClass && ti.Def.TryGetProperty("interfaces", out var ifs))
                foreach (var i in ifs.EnumerateArray())
                    if (_types.TryGetValue(ParseOwner(i.GetString()).open, out var inf)) Visit(inf);
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
            var taskRet = rs == "void" ? typeof(System.Threading.Tasks.Task)
                : typeof(System.Threading.Tasks.Task<>).MakeGenericType(MapType(rs));
            var sps = m.GetProperty("params").EnumerateArray().Select(p => MapType(p.GetProperty("type").GetString())).ToArray();
            var smb = ti.TB.DefineMethod(name, attrs, taskRet, sps);
            ti.Methods[name] = smb;
            _mparams[smb] = sps;
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
        ti.Methods[name] = mb;
        _mparams[mb] = ps;   // MethodBuilder.GetParameters() throws pre-bake; record param types for call-site boxing
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
        var mb = ti.Methods[m.GetProperty("name").GetString()];
        _methodRetType = mb.ReturnType;
        _curTypeParams = ti.TypeParams;
        _curMethodParams = _methodTypeParams.TryGetValue(mb, out var mp) ? mp : null;
        if (m.TryGetProperty("suspend", out var su) && su.GetBoolean()) { EmitCoroutine(ti, mb, m); return; }
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

        var coFields = new Dictionary<string, FieldBuilder>();
        var cpsDefs = m.GetProperty("cpsFields").EnumerateArray().ToList();
        foreach (var f in cpsDefs)
            coFields[f.GetProperty("name").GetString()] = sm.DefineField(f.GetProperty("name").GetString(), MapType(f.GetProperty("type").GetString()), FieldAttributes.Public);

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
            il.Emit(OpCodes.Call, builderT.GetMethod("SetStateMachine", new[] { iasm }));
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
                        var ct = ResolveType(st.GetProperty("excType").GetString());
                        _il.BeginCatchBlock(ct);
                        var el = _il.DeclareLocal(ct);                   // bind the caught exception to the catch var
                        _locals[st.GetProperty("var").GetString()] = el;
                        _il.Emit(OpCodes.Stloc, el);
                        break;
                    }
                    case "coTryEnd":
                    {
                        int id = st.GetProperty("id").GetInt32();
                        if (fell) _il.Emit(OpCodes.Leave, tryEnd[id]);   // close the last catch
                        _il.EndExceptionBlock();
                        _coTryDepth--;
                        break;
                    }
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
                            _il.Emit(OpCodes.Call, builderT.GetMethod("SetResult"));
                        }
                        else _il.Emit(OpCodes.Call, builderT.GetMethod("SetResult"));
                        if (_coTryDepth > 0) _il.Emit(OpCodes.Leave, _coExit); else _il.Emit(OpCodes.Ret);
                        break;
                    case "coUnsupported":
                        throw new NotSupportedException("coroutine (deferred): " + st.GetProperty("of").GetString());
                    default:
                        EmitStmt(st);
                        break;
                }
                fell = !(kind == "coReturn" || kind == "coGoto");
            }
            _il.MarkLabel(_coExit);
            _il.Emit(OpCodes.Ret);   // single exit; suspension/return inside a try `leave` here, others `ret` directly
            _coFields = null;
        }

        // ---- kickoff body (the original method `mb`): start the machine, return its Task ----
        {
            _il = mb.GetILGenerator();
            _args.Clear(); _argTypes.Clear(); _locals.Clear();
            var locSm = _il.DeclareLocal(sm);
            int ai = mb.IsStatic ? 0 : 1;
            foreach (var p in m.GetProperty("params").EnumerateArray())
            {
                var pn = p.GetProperty("name").GetString();
                _il.Emit(OpCodes.Ldloca, locSm); _il.Emit(OpCodes.Ldarg, ai++); _il.Emit(OpCodes.Stfld, coFields[pn]);
            }
            _il.Emit(OpCodes.Ldloca, locSm); _il.Emit(OpCodes.Call, builderT.GetMethod("Create")); _il.Emit(OpCodes.Stfld, fBuilder);
            _il.Emit(OpCodes.Ldloca, locSm); EmitLdcI4(-1); _il.Emit(OpCodes.Stfld, fState);
            _il.Emit(OpCodes.Ldloca, locSm); _il.Emit(OpCodes.Ldflda, fBuilder); _il.Emit(OpCodes.Ldloca, locSm);
            _il.Emit(OpCodes.Call, builderT.GetMethod("Start").MakeGenericMethod(sm));
            _il.Emit(OpCodes.Ldloca, locSm); _il.Emit(OpCodes.Ldflda, fBuilder);
            _il.Emit(OpCodes.Call, builderT.GetMethod("get_Task"));
            _il.Emit(OpCodes.Ret);
        }

        sm.CreateType();
    }

    void EmitCoSuspend(JsonElement st, FieldBuilder fState, FieldBuilder fBuilder, Type builderT, TypeBuilder sm,
        Dictionary<int, Type> awaiterType, Dictionary<int, FieldBuilder> awaiterField, Dictionary<int, LocalBuilder> awaiterLocal,
        Dictionary<int, Label> resume, Dictionary<int, Label> after, Dictionary<string, FieldBuilder> coFields)
    {
        int k = st.GetProperty("state").GetInt32();
        var at = awaiterType[k];
        var aLoc = awaiterLocal[k];

        // awaiter = (awaitable).GetAwaiter();
        var taskType = EmitExpr(st.GetProperty("awaitable"));
        _il.Emit(OpCodes.Callvirt, taskType.GetMethod("GetAwaiter"));
        _il.Emit(OpCodes.Stloc, aLoc);
        // if (awaiter.IsCompleted) goto after;
        _il.Emit(OpCodes.Ldloca, aLoc); _il.Emit(OpCodes.Call, at.GetMethod("get_IsCompleted"));
        _il.Emit(OpCodes.Brtrue, after[k]);
        // suspend: state=k; <>u__k=awaiter; builder.AwaitUnsafeOnCompleted(ref awaiter, ref this); return;
        _il.Emit(OpCodes.Ldarg_0); EmitLdcI4(k); _il.Emit(OpCodes.Stfld, fState);
        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldloc, aLoc); _il.Emit(OpCodes.Stfld, awaiterField[k]);
        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldflda, fBuilder);
        _il.Emit(OpCodes.Ldloca, aLoc); _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Call, builderT.GetMethod("AwaitUnsafeOnCompleted").MakeGenericMethod(at, sm));
        if (_coTryDepth > 0) _il.Emit(OpCodes.Leave, _coExit); else _il.Emit(OpCodes.Ret);   // `ret` is illegal inside a .try
        // resume: awaiter = <>u__k; <>u__k = default; state = -1;
        _il.MarkLabel(resume[k]);
        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, awaiterField[k]); _il.Emit(OpCodes.Stloc, aLoc);
        _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldflda, awaiterField[k]); _il.Emit(OpCodes.Initobj, at);
        _il.Emit(OpCodes.Ldarg_0); EmitLdcI4(-1); _il.Emit(OpCodes.Stfld, fState);
        // after: <assignTo> = awaiter.GetResult();
        _il.MarkLabel(after[k]);
        var assignTo = st.GetProperty("assignTo").ValueKind == JsonValueKind.Null ? null : st.GetProperty("assignTo").GetString();
        var getResult = at.GetMethod("GetResult");
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
            var tmp = _il.DeclareLocal(at.GetMethod("GetResult").ReturnType);
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
                    _il.BeginCatchBlock(ResolveType(c.GetProperty("excType").GetString()));
                    _il.Emit(OpCodes.Pop); // discard the exception object (catch var unused for now)
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
            case "unsupportedStmt": throw new NotSupportedException("unsupported Kotlin construct (deferred): " + s.GetProperty("of").GetString());
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
    MethodInfo ResolveMethod(string spec, string name, out Type retType)
    {
        var (open, constructed) = ParseOwner(spec);
        var mb = FindMethod(open, name);
        if (constructed == null) { retType = mb.ReturnType; return mb; }
        retType = Subst(mb.ReturnType, constructed.GetGenericArguments());
        return TypeBuilder.GetMethod(constructed, mb);
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
            case "this": _il.Emit(OpCodes.Ldarg_0); return;
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

    MethodBuilder FindMethod(string typeName, string name)
    {
        for (var ti = _types[typeName]; ti != null; ti = ti.BaseName != null && _types.ContainsKey(ti.BaseName) ? _types[ti.BaseName] : null)
            if (ti.Methods.TryGetValue(name, out var m)) return m;
        throw new NotSupportedException($"method {typeName}.{name} not found");
    }

    // ---- expressions: push one value, return its CLR type ----
    Type EmitExpr(JsonElement e)
    {
        switch (e.GetProperty("k").GetString())
        {
            case "const": return EmitConst(e);
            case "this": _il.Emit(OpCodes.Ldarg_0); return typeof(object);
            case "local":
            {
                var name = e.GetProperty("name").GetString();
                // In a coroutine, a param/live-local reference is a load of the SM struct field.
                if (_coFields != null && _coFields.TryGetValue(name, out var cf)) { _il.Emit(OpCodes.Ldarg_0); _il.Emit(OpCodes.Ldfld, cf); return cf.FieldType; }
                if (_locals.TryGetValue(name, out var l)) { _il.Emit(OpCodes.Ldloc, l); return l.LocalType; }
                if (_args.TryGetValue(name, out var a)) { _il.Emit(OpCodes.Ldarg, a); return _argTypes[name]; }
                throw new NotSupportedException("load unknown var " + name);
            }
            case "field":
            {
                EmitExpr(e.GetProperty("recv"));
                var fb = ResolveField(e.GetProperty("ownerType").GetString(), e.GetProperty("name").GetString(), out var ft);
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
                var m0 = ResolveMethod(e.GetProperty("ownerType").GetString(), e.GetProperty("method").GetString(), out var rt);
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
                // owner present -> a static method on that named class (companion); else a file-class sibling.
                var mb = ApplyTypeArgs((e.TryGetProperty("owner", out var ow) && ow.ValueKind == JsonValueKind.String)
                    ? FindMethod(ow.GetString(), name) : FindStatic(name), e, out var srt, out var sps);
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
            case "staticFieldSet":
            {
                EmitExpr(e.GetProperty("value"));
                _il.Emit(OpCodes.Stsfld, FindField(e.GetProperty("ownerType").GetString(), e.GetProperty("name").GetString()));
                return typeof(void);
            }
            case "console":
            {
                var t = EmitExpr(e.GetProperty("args").EnumerateArray().First());
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
                _il.Emit(OpCodes.Newobj, listT.GetConstructor(Type.EmptyTypes));
                var add = listT.GetMethod("Add");
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
            case "mapNew":
            {
                // `mapOf(k to v, …)` -> new Dictionary<K,V> { [k]=v, … } via set_Item.
                var kt = MapType(e.GetProperty("keyType").GetString());
                var vt = MapType(e.GetProperty("valType").GetString());
                var dt = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(kt, vt);
                _il.Emit(OpCodes.Newobj, dt.GetConstructor(Type.EmptyTypes));
                var setItem = dt.GetMethod("set_Item");
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
                _il.Emit(OpCodes.Newobj, setT.GetConstructor(Type.EmptyTypes));
                var add = setT.GetMethod("Add");
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
            case "delegateInvoke":
            {
                var ft = MapType(e.GetProperty("funcType").GetString());
                EmitExpr(e.GetProperty("recv"));
                foreach (var a in e.GetProperty("args").EnumerateArray()) EmitExpr(a);
                _il.Emit(OpCodes.Callvirt, InvokeOf(ft));
                return FuncRetType(e.GetProperty("funcType").GetString());
            }
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
            case "unsupportedExpr": throw new NotSupportedException("unsupported Kotlin construct (deferred): " + e.GetProperty("of").GetString());
            default: throw new NotSupportedException("expr " + e.GetProperty("k").GetString());
        }
    }

    MethodBuilder FindStatic(string name)
    {
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
            case "double": _il.Emit(OpCodes.Ldc_R8, v.GetDouble()); return typeof(double);
            case "float": _il.Emit(OpCodes.Ldc_R4, v.GetSingle()); return typeof(float);
            case "bool": _il.Emit(v.GetBoolean() ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); return typeof(bool);
            case "char": _il.Emit(OpCodes.Ldc_I4, (int)v.GetString()[0]); return typeof(char);
            default: _il.Emit(OpCodes.Ldnull); return typeof(object);
        }
    }

    Type EmitBin(JsonElement e)
    {
        var op = e.GetProperty("op").GetString();
        var lt = EmitExpr(e.GetProperty("l"));
        EmitExpr(e.GetProperty("r"));
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
            EmitExpr(elems[i]);
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

    Type EmitCond(JsonElement e)
    {
        var elseL = _il.DefineLabel(); var end = _il.DefineLabel();
        EmitExpr(e.GetProperty("cond")); _il.Emit(OpCodes.Brfalse, elseL);
        var t = EmitExpr(e.GetProperty("then")); _il.Emit(OpCodes.Br, end);
        _il.MarkLabel(elseL); EmitExpr(e.GetProperty("else")); _il.MarkLabel(end);
        return t;
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
            ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(x => x != null);
        if (t == null) throw new NotSupportedException("cannot resolve .NET type " + name);
        _typeCache[name] = t;
        return t;
    }

    Type EmitClrNew(JsonElement e)
    {
        var type = ClrRef(e.GetProperty("type").GetString());
        var argTypes = e.GetProperty("argTypes").EnumerateArray().Select(a => ClrRef(a.GetString())).ToArray();
        var ci = type.GetConstructor(argTypes) ?? type.GetConstructor(Type.EmptyTypes);
        EmitArgs(e.GetProperty("args"), ci.GetParameters());
        _il.Emit(OpCodes.Newobj, ci);
        return type;
    }

    Type EmitClrCall(JsonElement e, bool instance)
    {
        // `ClrRef` (not `ResolveType`) so a method on a constructed generic .NET type (`Collection<int>`) resolves.
        var type = ClrRef(e.GetProperty("type").GetString());
        var name = e.GetProperty("method").GetString();
        var argSpecs = e.GetProperty("argTypes").EnumerateArray().Select(a => a.GetString()).ToList();
        var flags = BindingFlags.Public | (instance ? BindingFlags.Instance : BindingFlags.Static);
        MethodInfo mi = null;
        // Exact overload resolution when every arg type is a resolvable .NET type.
        var resolved = argSpecs.Select(TryResolveType).ToArray();
        if (resolved.All(x => x != null))
            mi = type.GetMethod(name, flags, null, resolved, null);
        // Fall back to name + arity — e.g. a generic-parameter arg type (`Add(T)` on `Collection<int>`) that
        // doesn't name a plain .NET type; on the constructed type GetMethods returns the substituted overload.
        mi ??= type.GetMethods(flags).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == argSpecs.Count);
        if (instance) EmitExpr(e.GetProperty("recv"));
        EmitArgs(e.GetProperty("args"), mi.GetParameters());
        _il.Emit(instance && mi.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, mi);
        return mi.ReturnType;
    }

    // ResolveType but returns null instead of throwing (for optional/best-effort overload resolution).
    static Type TryResolveType(string name)
    {
        try { return ResolveType(name); } catch (NotSupportedException) { return null; }
    }

    Type EmitClrPropGet(JsonElement e)
    {
        var type = ClrRef(e.GetProperty("type").GetString());
        var pi = type.GetProperty(e.GetProperty("name").GetString());
        var getter = pi.GetGetMethod();
        // A property getter on a VALUE type (e.g. KeyValuePair.Key/.Value) needs the receiver by managed pointer.
        if (!e.GetProperty("static").GetBoolean())
        {
            if (type.IsValueType) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv"));
        }
        _il.Emit(getter.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, getter);
        return getter.ReturnType;
    }

    Type EmitClrPropSet(JsonElement e)
    {
        var type = ClrRef(e.GetProperty("type").GetString());
        var pi = type.GetProperty(e.GetProperty("name").GetString());
        var setter = pi.GetSetMethod();
        if (!e.GetProperty("static").GetBoolean()) EmitExpr(e.GetProperty("recv"));
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
    }
    void EmitArgs2(JsonElement[] args, ParameterInfo[] ps)
    {
        for (int i = 0; i < args.Length; i++) EmitArg(args[i], ps[i].ParameterType);
    }
    void EmitArg(JsonElement a, Type want)
    {
        var got = EmitExpr(a);
        if (got == null) return;
        // `T` passed to a `T?` param -> wrap in Nullable<T>; value passed to a reference param -> box.
        if (want.IsGenericType && want.GetGenericTypeDefinition() == typeof(Nullable<>) && want.GetGenericArguments()[0] == got)
            _il.Emit(OpCodes.Newobj, want.GetConstructor(new[] { got }));
        // Box a value/generic-param arg passed to a reference param — but NOT when the param is itself a generic
        // param (passing `T` to a `T` slot flows the value as-is at the instantiation).
        else if (NeedsBoxToRef(got) && !want.IsValueType && !want.IsGenericParameter)
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
            "short" => typeof(short), "byte" => typeof(byte), _ => typeof(object),
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
