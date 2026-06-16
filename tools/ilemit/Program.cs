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
        if (args.Length < 3) { Console.Error.WriteLine("usage: ilemit <out-dir> <asmName> <file.bir.json>..."); return 1; }
        var outDir = args[0];
        var asmName = args[1];
        Directory.CreateDirectory(outDir);
        var files = args.Skip(2).Select(p => JsonDocument.Parse(File.ReadAllText(p))).ToList();
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
    public readonly Dictionary<string, FieldBuilder> Fields = new();
    public readonly Dictionary<string, MethodBuilder> Methods = new();
    public ConstructorBuilder Ctor;
    public JsonElement CtorDef;
    public bool IsInterface;
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
    readonly Dictionary<string, LocalBuilder> _locals = new();

    public Emitter(string outDir, string asmName) { _outDir = outDir; _asmName = asmName; }

    public void EmitAssembly(List<JsonElement> files)
    {
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
                    var isIface = t.GetProperty("kind").GetString() == "interface";
                    var attrs = isIface
                        ? TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract
                        : TypeAttributes.Public | TypeAttributes.Class;
                    _types[name] = new TypeInfo
                    {
                        TB = _mod.DefineType(name, attrs),
                        Def = t,
                        IsInterface = isIface,
                        BaseName = t.TryGetProperty("base", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null,
                    };
                }
        }

        // Pass 2: set parents and interface implementations.
        foreach (var ti in _types.Values)
        {
            if (ti.BaseName != null) ti.TB.SetParent(_types[ti.BaseName].TB);
            if (!ti.IsFileClass && ti.Def.TryGetProperty("interfaces", out var ifs))
                foreach (var i in ifs.EnumerateArray()) ti.TB.AddInterfaceImplementation(_types[i.GetString()].TB);
        }

        // Pass 3: declare fields, ctors, methods (signatures) so cross-refs resolve.
        foreach (var ti in _types.Values)
        {
            if (ti.IsFileClass)
            {
                foreach (var m in ti.Def.GetProperty("methods").EnumerateArray()) DeclareMethod(ti, m, isStatic: true);
            }
            else
            {
                if (!ti.IsInterface)
                    foreach (var f in ti.Def.GetProperty("fields").EnumerateArray())
                        ti.Fields[f.GetProperty("name").GetString()] =
                            ti.TB.DefineField(f.GetProperty("name").GetString(), MapType(f.GetProperty("type").GetString()), FieldAttributes.Public);
                foreach (var m in ti.Def.GetProperty("methods").EnumerateArray()) DeclareMethod(ti, m, isStatic: false);
                var ctors = ti.Def.GetProperty("ctors");
                if (!ti.IsInterface && ctors.GetArrayLength() > 0)
                {
                    var c = ctors.EnumerateArray().First();
                    var ps = c.GetProperty("params").EnumerateArray().Select(p => MapType(p.GetProperty("type").GetString())).ToArray();
                    ti.Ctor = ti.TB.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, ps);
                    ti.CtorDef = c;
                }
            }
        }

        // Link interface implementations: every class method that satisfies an interface method.
        foreach (var ti in _types.Values)
            if (!ti.IsFileClass && !ti.IsInterface && ti.Def.TryGetProperty("interfaces", out var ifs))
                foreach (var i in ifs.EnumerateArray())
                {
                    var iface = _types[i.GetString()];
                    foreach (var im in iface.Methods)
                        ti.TB.DefineMethodOverride(FindMethod(ti.TB.Name, im.Key), im.Value);
                }

        // Pass 4: emit all bodies (every ctor/method signature already exists).
        foreach (var ti in _types.Values)
            if (ti.Ctor != null) EmitCtorBody(ti);
        foreach (var ti in _types.Values)
            if (!ti.IsInterface)
                foreach (var m in ti.Def.GetProperty("methods").EnumerateArray()) EmitMethodBody(ti, m);

        // Pass 5: synthesize entry point on the file class that has `main`.
        MethodBuilder entry = null;
        foreach (var ti in _types.Values)
            if (ti.IsFileClass && ti.FileElem.Value.GetProperty("hasMain").GetBoolean() && ti.Methods.ContainsKey("main"))
            {
                entry = ti.TB.DefineMethod("Main", MethodAttributes.Public | MethodAttributes.Static, typeof(void), new[] { typeof(string[]) });
                var il = entry.GetILGenerator();
                il.Emit(OpCodes.Call, ti.Methods["main"]);
                il.Emit(OpCodes.Ret);
            }

        // Pass 6: bake types (base before derived).
        foreach (var ti in Ordered()) ti.TB.CreateType();

        Save(ab, entry);
    }

    IEnumerable<TypeInfo> Ordered()
    {
        var done = new HashSet<string>();
        var result = new List<TypeInfo>();
        void Visit(TypeInfo ti)
        {
            var key = ti.TB.Name;
            if (!done.Add(key)) return;
            if (ti.BaseName != null && _types.TryGetValue(ti.BaseName, out var b)) Visit(b);
            result.Add(ti);
        }
        foreach (var ti in _types.Values) Visit(ti);
        return result;
    }

    void DeclareMethod(TypeInfo ti, JsonElement m, bool isStatic)
    {
        var name = m.GetProperty("name").GetString();
        var ps = m.GetProperty("params").EnumerateArray().Select(p => MapType(p.GetProperty("type").GetString())).ToArray();
        var ret = MapType(m.GetProperty("ret").GetString());
        var attrs = MethodAttributes.Public;
        if (ti.IsInterface) attrs |= MethodAttributes.Virtual | MethodAttributes.Abstract | MethodAttributes.NewSlot;
        else if (isStatic) attrs |= MethodAttributes.Static;
        else if (m.GetProperty("override").GetBoolean()) attrs |= MethodAttributes.Virtual;
        else if (m.GetProperty("virtual").GetBoolean()) attrs |= MethodAttributes.Virtual | MethodAttributes.NewSlot;
        ti.Methods[name] = ti.TB.DefineMethod(name, attrs, ret, ps);
    }

    void EmitCtorBody(TypeInfo ti)
    {
        var c = ti.CtorDef;
        BeginMethod(ti.Ctor.GetILGenerator(), c, isStatic: false);

        // base ctor call
        _il.Emit(OpCodes.Ldarg_0);
        if (ti.BaseName != null && c.TryGetProperty("baseArgs", out var ba) && ba.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in ba.EnumerateArray()) EmitExpr(a);
            _il.Emit(OpCodes.Call, _types[ti.BaseName].Ctor);
        }
        else
        {
            _il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes));
        }
        foreach (var s in c.GetProperty("body").EnumerateArray()) EmitStmt(s);
        _il.Emit(OpCodes.Ret);
    }

    void EmitMethodBody(TypeInfo ti, JsonElement m)
    {
        var mb = ti.Methods[m.GetProperty("name").GetString()];
        BeginMethod(mb.GetILGenerator(), m, isStatic: mb.IsStatic);
        foreach (var s in m.GetProperty("body").EnumerateArray()) EmitStmt(s);
        _il.Emit(OpCodes.Ret);
    }

    void BeginMethod(ILGenerator il, JsonElement m, bool isStatic)
    {
        _il = il; _args.Clear(); _locals.Clear();
        int i = isStatic ? 0 : 1; // arg0 = this for instance methods
        foreach (var p in m.GetProperty("params").EnumerateArray())
            _args[p.GetProperty("name").GetString()] = i++;
    }

    // ---- statements ----
    void EmitStmt(JsonElement s)
    {
        switch (s.GetProperty("k").GetString())
        {
            case "var":
            {
                var local = _il.DeclareLocal(MapType(s.GetProperty("type").GetString()));
                _locals[s.GetProperty("name").GetString()] = local;
                if (s.TryGetProperty("init", out var init) && init.ValueKind != JsonValueKind.Null)
                {
                    EmitExpr(init); _il.Emit(OpCodes.Stloc, local);
                }
                break;
            }
            case "setLocal":
                EmitExpr(s.GetProperty("value"));
                StoreVar(s.GetProperty("name").GetString());
                break;
            case "setField":
            {
                EmitExpr(s.GetProperty("recv"));
                EmitExpr(s.GetProperty("value"));
                _il.Emit(OpCodes.Stfld, Field(s));
                break;
            }
            case "return":
                if (s.TryGetProperty("value", out var rv)) EmitExpr(rv);
                _il.Emit(OpCodes.Ret);
                break;
            case "exprStmt":
            {
                var t = EmitExpr(s.GetProperty("expr"));
                if (t != typeof(void)) _il.Emit(OpCodes.Pop);
                break;
            }
            case "while":
            {
                var start = _il.DefineLabel(); var end = _il.DefineLabel();
                _il.MarkLabel(start);
                EmitExpr(s.GetProperty("cond")); _il.Emit(OpCodes.Brfalse, end);
                foreach (var b in s.GetProperty("body").EnumerateArray()) EmitStmt(b);
                _il.Emit(OpCodes.Br, start); _il.MarkLabel(end);
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
                var start = _il.DefineLabel(); var end = _il.DefineLabel();
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
                _il.Emit(OpCodes.Ldloc, local);
                _il.Emit(OpCodes.Ldc_I4, s.GetProperty("step").GetInt32());
                _il.Emit(OpCodes.Add); _il.Emit(OpCodes.Stloc, local);
                _il.Emit(OpCodes.Br, start);
                _il.MarkLabel(end);
                break;
            }
            case "block":
                foreach (var b in s.GetProperty("body").EnumerateArray()) EmitStmt(b);
                break;
            default: throw new NotSupportedException("stmt " + s.GetProperty("k").GetString());
        }
    }

    void StoreVar(string name)
    {
        if (_locals.TryGetValue(name, out var l)) _il.Emit(OpCodes.Stloc, l);
        else if (_args.TryGetValue(name, out var a)) _il.Emit(OpCodes.Starg, a);
        else throw new NotSupportedException("store unknown var " + name);
    }

    FieldBuilder Field(JsonElement node) =>
        FindField(node.GetProperty("ownerType").GetString(), node.GetProperty("name").GetString());

    // Members may be declared on a base type (inherited / fake-overridden); walk the chain.
    FieldBuilder FindField(string typeName, string name)
    {
        for (var ti = _types[typeName]; ti != null; ti = ti.BaseName != null ? _types[ti.BaseName] : null)
            if (ti.Fields.TryGetValue(name, out var f)) return f;
        throw new NotSupportedException($"field {typeName}.{name} not found");
    }

    MethodBuilder FindMethod(string typeName, string name)
    {
        for (var ti = _types[typeName]; ti != null; ti = ti.BaseName != null ? _types[ti.BaseName] : null)
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
                if (_locals.TryGetValue(name, out var l)) { _il.Emit(OpCodes.Ldloc, l); return l.LocalType; }
                if (_args.TryGetValue(name, out var a)) { _il.Emit(OpCodes.Ldarg, a); return typeof(int); }
                throw new NotSupportedException("load unknown var " + name);
            }
            case "field":
            {
                EmitExpr(e.GetProperty("recv"));
                var fb = Field(e);
                _il.Emit(OpCodes.Ldfld, fb);
                return fb.FieldType;
            }
            case "setFieldExpr":
            {
                EmitExpr(e.GetProperty("recv"));
                EmitExpr(e.GetProperty("value"));
                _il.Emit(OpCodes.Stfld, Field(e));
                return typeof(void);
            }
            case "new":
            {
                var ti = _types[e.GetProperty("type").GetString()];
                foreach (var a in e.GetProperty("args").EnumerateArray()) EmitExpr(a);
                _il.Emit(OpCodes.Newobj, ti.Ctor);
                return ti.TB;
            }
            case "callInstance":
            {
                EmitExpr(e.GetProperty("recv"));
                foreach (var a in e.GetProperty("args").EnumerateArray()) EmitExpr(a);
                var mb = FindMethod(e.GetProperty("ownerType").GetString(), e.GetProperty("method").GetString());
                _il.Emit(e.GetProperty("virtual").GetBoolean() ? OpCodes.Callvirt : OpCodes.Call, mb);
                return mb.ReturnType;
            }
            case "callStatic":
            {
                var name = e.GetProperty("method").GetString();
                foreach (var a in e.GetProperty("args").EnumerateArray()) EmitExpr(a);
                var mb = FindStatic(name);
                _il.Emit(OpCodes.Call, mb);
                return mb.ReturnType;
            }
            case "console":
            {
                var t = EmitExpr(e.GetProperty("args").EnumerateArray().First());
                if (t.IsValueType) _il.Emit(OpCodes.Box, t);
                _il.Emit(OpCodes.Call, typeof(Console).GetMethod(e.GetProperty("method").GetString(), new[] { typeof(object) }));
                return typeof(void);
            }
            case "bin": return EmitBin(e);
            case "un": return EmitUn(e);
            case "concat": return EmitConcat(e);
            case "cond": return EmitCond(e);
            case "clrNew": return EmitClrNew(e);
            case "clrStatic": return EmitClrCall(e, instance: false);
            case "clrInstance": return EmitClrCall(e, instance: true);
            case "clrPropGet": return EmitClrPropGet(e);
            case "clrPropSet": return EmitClrPropSet(e);
            default: throw new NotSupportedException("expr " + e.GetProperty("k").GetString());
        }
    }

    MethodBuilder FindStatic(string name)
    {
        foreach (var ti in _types.Values)
            if (ti.IsFileClass && ti.Methods.TryGetValue(name, out var mb)) return mb;
        throw new NotSupportedException("static method not found: " + name);
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
            case "!": _il.Emit(OpCodes.Ldc_I4_0); _il.Emit(OpCodes.Ceq); return typeof(bool);
            default: throw new NotSupportedException("un " + op);
        }
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
            if (t.IsValueType) _il.Emit(OpCodes.Box, t);
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

    // ---- BCL interop (@Clr) via reflection ----
    static readonly Dictionary<string, Type> _typeCache = new();
    static Type ResolveType(string name)
    {
        if (_typeCache.TryGetValue(name, out var c)) return c;
        var t = Type.GetType(name)
            ?? Type.GetType(name + ", System.Runtime")
            ?? Type.GetType(name + ", System.Private.CoreLib")
            ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(x => x != null);
        if (t == null) throw new NotSupportedException("cannot resolve .NET type " + name);
        _typeCache[name] = t;
        return t;
    }

    Type EmitClrNew(JsonElement e)
    {
        var type = ResolveType(e.GetProperty("type").GetString());
        var argTypes = e.GetProperty("argTypes").EnumerateArray().Select(a => ResolveType(a.GetString())).ToArray();
        var ci = type.GetConstructor(argTypes) ?? type.GetConstructor(Type.EmptyTypes);
        EmitArgs(e.GetProperty("args"), ci.GetParameters());
        _il.Emit(OpCodes.Newobj, ci);
        return type;
    }

    Type EmitClrCall(JsonElement e, bool instance)
    {
        var type = ResolveType(e.GetProperty("type").GetString());
        var argTypes = e.GetProperty("argTypes").EnumerateArray().Select(a => ResolveType(a.GetString())).ToArray();
        var flags = BindingFlags.Public | (instance ? BindingFlags.Instance : BindingFlags.Static);
        var mi = type.GetMethod(e.GetProperty("method").GetString(), flags, null, argTypes, null);
        if (instance) EmitExpr(e.GetProperty("recv"));
        EmitArgs(e.GetProperty("args"), mi.GetParameters());
        _il.Emit(instance && mi.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, mi);
        return mi.ReturnType;
    }

    Type EmitClrPropGet(JsonElement e)
    {
        var type = ResolveType(e.GetProperty("type").GetString());
        var pi = type.GetProperty(e.GetProperty("name").GetString());
        var getter = pi.GetGetMethod();
        if (!e.GetProperty("static").GetBoolean()) EmitExpr(e.GetProperty("recv"));
        _il.Emit(getter.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, getter);
        return getter.ReturnType;
    }

    Type EmitClrPropSet(JsonElement e)
    {
        var type = ResolveType(e.GetProperty("type").GetString());
        var pi = type.GetProperty(e.GetProperty("name").GetString());
        var setter = pi.GetSetMethod();
        if (!e.GetProperty("static").GetBoolean()) EmitExpr(e.GetProperty("recv"));
        EmitArgs2(new[] { e.GetProperty("value") }, setter.GetParameters());
        _il.Emit(setter.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, setter);
        return typeof(void);
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
        if (got != null && got.IsValueType && !want.IsValueType) _il.Emit(OpCodes.Box, got);
    }

    Type MapType(string t)
    {
        if (t != null && t.StartsWith("clr:")) return ResolveType(t.Substring(4));
        if (t != null && t.StartsWith("@")) return _types[t.Substring(1)].TB;
        return t switch
        {
            "void" => typeof(void), "int" => typeof(int), "long" => typeof(long),
            "double" => typeof(double), "float" => typeof(float), "bool" => typeof(bool),
            "char" => typeof(char), "string" => typeof(string), _ => typeof(object),
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
