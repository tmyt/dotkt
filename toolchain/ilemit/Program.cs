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
        if (args.Length < 3) { Console.Error.WriteLine("usage: ilemit <out-dir> <asmName> [--build-stdlib=metadata|runtime] [--ref <dll>]... <file.bir.json>..."); return 1; }
        var outDir = args[0];
        var asmName = args[1];
        Directory.CreateDirectory(outDir);
        // `--ref <dll>`: preload an external .NET assembly (e.g. a coroutine runtime, a framework like Avalonia)
        // so its types resolve at emit time; the runtime dll must sit beside the emitted assembly to run.
        // `--build-stdlib=metadata|runtime`: the stdlib self-build mode (the SAME flag bir2cir parses). It drives two
        // knobs: StdlibStub (either mode — stub un-emittable methods instead of aborting) and _stripMetadata (runtime
        // only — drop roundtrip metadata). Absent = an app build. (Superseded the old stdlib-build/strip env vars.)
        var bir = new List<string>();
        var mode = Emitter.BuildStdlibMode.App;
        var rest = args.Skip(2).ToList();
        for (int i = 0; i < rest.Count; i++)
        {
            if (rest[i] == "--ref" && i + 1 < rest.Count) { var rp = Path.GetFullPath(rest[++i]); Emitter.T($"ref: {rp}"); try { Assembly.LoadFrom(rp); } catch { } }
            else if (rest[i] == "--build-stdlib=metadata") mode = Emitter.BuildStdlibMode.Metadata;
            else if (rest[i] == "--build-stdlib=runtime") mode = Emitter.BuildStdlibMode.Runtime;
            else bir.Add(rest[i]);
        }
        var files = bir.Select(LoadInputDocument).ToList();
        new Emitter(outDir, asmName, mode).EmitAssembly(MergeByFileClass(files));
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
    // are ~73.8% of the size. TRUE for the stdlib RUNTIME build (`--build-stdlib=runtime`); a USER LIBRARY (no flag) is
    // substituted but KEEPS its attributes (round-trip consumable AS KOTLIN). Sourced from the `--build-stdlib` CLI flag,
    // the SAME flag bir2cir parses (retires the old DOTKT_STRIP_METADATA env var); only the boolean SOURCE changed.
    readonly bool _stripMetadata;
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

    // The stdlib self-build mode, from `--build-stdlib` (mirrors bir2cir's BuildStdlibMode; separate assembly).
    public enum BuildStdlibMode { App, Metadata, Runtime }

    public Emitter(string outDir, string asmName, BuildStdlibMode mode = BuildStdlibMode.App)
    {
        _outDir = outDir; _asmName = asmName;
        _stripMetadata = mode == BuildStdlibMode.Runtime;   // runtime stdlib build drops roundtrip metadata
        _stdlibStub = mode != BuildStdlibMode.App;           // either stdlib build stubs un-emittable methods
    }

    // A call to a generic method `fun <T> id(x:T)` carries `typeArgs` -> instantiate it (MakeGenericMethod).
    // `retType`/`paramTypes` give the SUBSTITUTED (concrete) signature, since the instantiation's own reflection
    // still reports `!!0` (and throws pre-bake) — needed so value args to `object`/concrete params get boxed.
    // Set by `--build-stdlib=metadata|runtime` (either stdlib self-build): while compiling the pure-kotlin stdlib,
    // methods the backend can't yet emit are stubbed (throw) instead of aborting the whole assembly — the "= TODO()"
    // stdlib still emits and loads. Driven by the `--build-stdlib` flag (superseded the old stdlib-build env read).
    readonly bool _stdlibStub;

    // Whether the current FindReflectedMethodBySig owner is a CONSTRUCTED generic type — set per-lookup, read by the
    // `gp:` structural case (a `gp:T` token matches a concrete arg when the owner instantiation already bound it).
    bool _sigConstructedOwner;

}
