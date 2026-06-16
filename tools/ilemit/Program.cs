// ilemit — emit a runnable .NET assembly directly as CIL from Backend IR (BIR) JSON. No C#, no csc.
//
//   ilemit <output-dir> <file.bir.json>
//
// Emits <output-dir>/<fileClass>.dll (+ .runtimeconfig.json). D1.2 scope = the M0 subset.
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
        if (args.Length < 2) { Console.Error.WriteLine("usage: ilemit <out-dir> <file.bir.json>"); return 1; }
        var outDir = args[0];
        Directory.CreateDirectory(outDir);
        using var doc = JsonDocument.Parse(File.ReadAllText(args[1]));
        new Emitter(outDir).EmitFile(doc.RootElement);
        return 0;
    }
}

sealed class Emitter
{
    readonly string _outDir;
    readonly Dictionary<string, MethodBuilder> _methods = new();
    Emitter.MethodCtx _ctx;

    public Emitter(string outDir) => _outDir = outDir;

    sealed class MethodCtx
    {
        public ILGenerator IL;
        public readonly Dictionary<string, int> Args = new();
        public readonly Dictionary<string, LocalBuilder> Locals = new();
    }

    public void EmitFile(JsonElement file)
    {
        var className = file.GetProperty("fileClass").GetString();
        var hasMain = file.GetProperty("hasMain").GetBoolean();
        var methodDefs = file.GetProperty("methods").EnumerateArray().ToList();

        var ab = new PersistedAssemblyBuilder(new AssemblyName(className), typeof(object).Assembly);
        ModuleBuilder mod = ab.DefineDynamicModule(className);
        TypeBuilder type = mod.DefineType(className, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract);

        // Pass 1: declare every method so sibling calls resolve.
        foreach (var m in methodDefs)
        {
            var name = m.GetProperty("name").GetString();
            var ps = m.GetProperty("params").EnumerateArray().Select(p => MapType(p.GetProperty("type").GetString())).ToArray();
            var ret = MapType(m.GetProperty("ret").GetString());
            _methods[name] = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static, ret, ps);
        }

        // Pass 2: emit bodies.
        foreach (var m in methodDefs)
        {
            var mb = _methods[m.GetProperty("name").GetString()];
            _ctx = new MethodCtx { IL = mb.GetILGenerator() };
            int i = 0;
            foreach (var p in m.GetProperty("params").EnumerateArray())
                _ctx.Args[p.GetProperty("name").GetString()] = i++;
            foreach (var s in m.GetProperty("body").EnumerateArray()) EmitStmt(s);
            _ctx.IL.Emit(OpCodes.Ret); // implicit return for void methods (harmless after an explicit ret)
        }

        MethodBuilder entry = null;
        if (hasMain && _methods.TryGetValue("main", out var mainM))
        {
            entry = type.DefineMethod("Main", MethodAttributes.Public | MethodAttributes.Static, typeof(void), new[] { typeof(string[]) });
            var il = entry.GetILGenerator();
            il.Emit(OpCodes.Call, mainM);
            il.Emit(OpCodes.Ret);
        }

        type.CreateType();
        Save(ab, className, entry);
    }

    // ---- statements ----
    void EmitStmt(JsonElement s)
    {
        var il = _ctx.IL;
        switch (s.GetProperty("k").GetString())
        {
            case "var":
            {
                var name = s.GetProperty("name").GetString();
                var local = il.DeclareLocal(MapType(s.GetProperty("type").GetString()));
                _ctx.Locals[name] = local;
                if (s.TryGetProperty("init", out var init) && init.ValueKind != JsonValueKind.Null)
                {
                    EmitExpr(init);
                    il.Emit(OpCodes.Stloc, local);
                }
                break;
            }
            case "setLocal":
            {
                var name = s.GetProperty("name").GetString();
                EmitExpr(s.GetProperty("value"));
                StoreVar(name);
                break;
            }
            case "return":
                if (s.TryGetProperty("value", out var rv)) EmitExpr(rv);
                il.Emit(OpCodes.Ret);
                break;
            case "exprStmt":
            {
                var t = EmitExpr(s.GetProperty("expr"));
                if (t != typeof(void)) il.Emit(OpCodes.Pop);
                break;
            }
            case "while":
            {
                var start = il.DefineLabel();
                var end = il.DefineLabel();
                il.MarkLabel(start);
                EmitExpr(s.GetProperty("cond"));
                il.Emit(OpCodes.Brfalse, end);
                foreach (var b in s.GetProperty("body").EnumerateArray()) EmitStmt(b);
                il.Emit(OpCodes.Br, start);
                il.MarkLabel(end);
                break;
            }
            case "if":
            {
                var end = il.DefineLabel();
                foreach (var br in s.GetProperty("branches").EnumerateArray())
                {
                    if (br.TryGetProperty("else", out _))
                    {
                        foreach (var b in br.GetProperty("body").EnumerateArray()) EmitStmt(b);
                    }
                    else
                    {
                        var next = il.DefineLabel();
                        EmitExpr(br.GetProperty("cond"));
                        il.Emit(OpCodes.Brfalse, next);
                        foreach (var b in br.GetProperty("body").EnumerateArray()) EmitStmt(b);
                        il.Emit(OpCodes.Br, end);
                        il.MarkLabel(next);
                    }
                }
                il.MarkLabel(end);
                break;
            }
            case "block":
                foreach (var b in s.GetProperty("body").EnumerateArray()) EmitStmt(b);
                break;
            default:
                throw new NotSupportedException("stmt " + s.GetProperty("k").GetString());
        }
    }

    void StoreVar(string name)
    {
        if (_ctx.Locals.TryGetValue(name, out var l)) _ctx.IL.Emit(OpCodes.Stloc, l);
        else if (_ctx.Args.TryGetValue(name, out var a)) _ctx.IL.Emit(OpCodes.Starg, a);
        else throw new NotSupportedException("store unknown var " + name);
    }

    // ---- expressions: push one value, return its CLR type (typeof(void) if none) ----
    Type EmitExpr(JsonElement e)
    {
        var il = _ctx.IL;
        switch (e.GetProperty("k").GetString())
        {
            case "const": return EmitConst(e);
            case "local":
            {
                var name = e.GetProperty("name").GetString();
                if (_ctx.Locals.TryGetValue(name, out var l)) { il.Emit(OpCodes.Ldloc, l); return l.LocalType; }
                if (_ctx.Args.TryGetValue(name, out var a)) { il.Emit(OpCodes.Ldarg, a); return typeof(int); /* type unknown; M0 uses through ops */ }
                throw new NotSupportedException("load unknown var " + name);
            }
            case "bin": return EmitBin(e);
            case "un": return EmitUn(e);
            case "concat": return EmitConcat(e);
            case "cond": return EmitCond(e);
            case "console":
            {
                var arg = e.GetProperty("args").EnumerateArray().First();
                var t = EmitExpr(arg);
                if (t.IsValueType) il.Emit(OpCodes.Box, t);
                var m = e.GetProperty("method").GetString();
                il.Emit(OpCodes.Call, typeof(Console).GetMethod(m, new[] { typeof(object) }));
                return typeof(void);
            }
            case "callStatic":
            {
                var name = e.GetProperty("method").GetString();
                foreach (var a in e.GetProperty("args").EnumerateArray()) EmitExpr(a);
                var target = _methods[name];
                il.Emit(OpCodes.Call, target);
                return target.ReturnType;
            }
            default:
                throw new NotSupportedException("expr " + e.GetProperty("k").GetString());
        }
    }

    Type EmitConst(JsonElement e)
    {
        var il = _ctx.IL;
        var t = e.GetProperty("type").GetString();
        var v = e.GetProperty("value");
        switch (t)
        {
            case "string":
                if (v.ValueKind == JsonValueKind.Null) { il.Emit(OpCodes.Ldnull); return typeof(string); }
                il.Emit(OpCodes.Ldstr, v.GetString()); return typeof(string);
            case "int": il.Emit(OpCodes.Ldc_I4, v.GetInt32()); return typeof(int);
            case "long": il.Emit(OpCodes.Ldc_I8, v.GetInt64()); return typeof(long);
            case "double": il.Emit(OpCodes.Ldc_R8, v.GetDouble()); return typeof(double);
            case "float": il.Emit(OpCodes.Ldc_R4, v.GetSingle()); return typeof(float);
            case "bool": il.Emit(v.GetBoolean() ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); return typeof(bool);
            case "char": il.Emit(OpCodes.Ldc_I4, (int)v.GetString()[0]); return typeof(char);
            default: il.Emit(OpCodes.Ldnull); return typeof(object);
        }
    }

    Type EmitBin(JsonElement e)
    {
        var il = _ctx.IL;
        var op = e.GetProperty("op").GetString();
        var lt = EmitExpr(e.GetProperty("l"));
        EmitExpr(e.GetProperty("r"));
        switch (op)
        {
            case "+": il.Emit(OpCodes.Add); return lt;
            case "-": il.Emit(OpCodes.Sub); return lt;
            case "*": il.Emit(OpCodes.Mul); return lt;
            case "/": il.Emit(OpCodes.Div); return lt;
            case "%": il.Emit(OpCodes.Rem); return lt;
            case "==": il.Emit(OpCodes.Ceq); return typeof(bool);
            case "!=": il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); return typeof(bool);
            case "<": il.Emit(OpCodes.Clt); return typeof(bool);
            case ">": il.Emit(OpCodes.Cgt); return typeof(bool);
            case "<=": il.Emit(OpCodes.Cgt); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); return typeof(bool);
            case ">=": il.Emit(OpCodes.Clt); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); return typeof(bool);
            default: throw new NotSupportedException("bin " + op);
        }
    }

    Type EmitUn(JsonElement e)
    {
        var il = _ctx.IL;
        var op = e.GetProperty("op").GetString();
        var t = EmitExpr(e.GetProperty("e"));
        switch (op)
        {
            case "-": il.Emit(OpCodes.Neg); return t;
            case "+": return t;
            case "!": il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); return typeof(bool);
            default: throw new NotSupportedException("un " + op);
        }
    }

    Type EmitConcat(JsonElement e)
    {
        var il = _ctx.IL;
        var parts = e.GetProperty("parts").EnumerateArray().ToList();
        il.Emit(OpCodes.Ldc_I4, parts.Count);
        il.Emit(OpCodes.Newarr, typeof(object));
        for (int i = 0; i < parts.Count; i++)
        {
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4, i);
            var t = EmitExpr(parts[i]);
            if (t.IsValueType) il.Emit(OpCodes.Box, t);
            il.Emit(OpCodes.Stelem_Ref);
        }
        il.Emit(OpCodes.Call, typeof(string).GetMethod("Concat", new[] { typeof(object[]) }));
        return typeof(string);
    }

    Type EmitCond(JsonElement e)
    {
        var il = _ctx.IL;
        var elseL = il.DefineLabel();
        var end = il.DefineLabel();
        EmitExpr(e.GetProperty("cond"));
        il.Emit(OpCodes.Brfalse, elseL);
        var t = EmitExpr(e.GetProperty("then"));
        il.Emit(OpCodes.Br, end);
        il.MarkLabel(elseL);
        EmitExpr(e.GetProperty("else"));
        il.MarkLabel(end);
        return t;
    }

    static Type MapType(string t) => t switch
    {
        "void" => typeof(void), "int" => typeof(int), "long" => typeof(long),
        "double" => typeof(double), "float" => typeof(float), "bool" => typeof(bool),
        "char" => typeof(char), "string" => typeof(string), _ => typeof(object),
    };

    void Save(PersistedAssemblyBuilder ab, string name, MethodBuilder entry)
    {
        MetadataBuilder metadata = ab.GenerateMetadata(out BlobBuilder ilStream, out BlobBuilder fieldData);
        var peHeader = new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll);
        var peBuilder = new ManagedPEBuilder(
            peHeader, new MetadataRootBuilder(metadata), ilStream,
            mappedFieldData: fieldData,
            entryPoint: entry != null ? MetadataTokens.MethodDefinitionHandle(entry.MetadataToken) : default);
        var blob = new BlobBuilder();
        peBuilder.Serialize(blob);
        using (var fs = new FileStream(Path.Combine(_outDir, name + ".dll"), FileMode.Create, FileAccess.Write))
            blob.WriteContentTo(fs);
        var v = Environment.Version;
        File.WriteAllText(Path.Combine(_outDir, name + ".runtimeconfig.json"),
            "{\n  \"runtimeOptions\": {\n    \"tfm\": \"net10.0\",\n" +
            "    \"framework\": { \"name\": \"Microsoft.NETCore.App\", \"version\": \"" + v.Major + "." + v.Minor + ".0\" }\n  }\n}\n");
        Console.WriteLine($"emitted {name}.dll");
    }
}
