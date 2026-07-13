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
        // `--build-stdlib=metadata|runtime`: the stdlib self-build mode (the SAME flag bir2cir parses). It drives the
        // StdlibStub knob (either mode — stub un-emittable methods instead of aborting). Round-trip metadata is no longer
        // ilemit's concern (#71 S2: bir2cir generates it and skips it in the runtime build). Absent = an app build.
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
        // #84 Phase 1: give ilemit a diagnostic boundary. On any failure, print a clean one-line
        // `ilemit: <Type>.<method>: <message>` naming the declaration being emitted (carried by CirEmitException,
        // thrown per-method in EmitAssembly) instead of a raw unhandled .NET stack trace. ILEMIT_TRACE keeps the
        // full stack for debugging (rethrow), matching the existing crash-localizer flag (Emitter.Trace).
        try
        {
            var files = bir.Select(LoadInputDocument).ToList();
            new Emitter(outDir, asmName, mode).EmitAssembly(MergeByFileClass(files));
            return 0;
        }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("ILEMIT_TRACE") != null) throw;   // full stack for debugging
            Console.Error.WriteLine(ex is CirEmitException ce
                ? $"ilemit: {ce.Decl}: {ce.Message}"
                : $"ilemit: {ex.Message}");
            return 1;
        }
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
            var node = System.Text.Json.Nodes.JsonNode.Parse(d.RootElement.GetRawText(), documentOptions: DotKt.Bir.BirJson.DocOptions).AsObject();
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
            var doc = JsonDocument.Parse(byFc[fc].ToJsonString(DotKt.Bir.BirJson.Writer), DotKt.Bir.BirJson.DocOptions);
            _mergedDocs.Add(doc);
            result.Add(doc.RootElement);
        }
        return result;
    }

    static JsonDocument LoadInputDocument(string path)
    {
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json, DotKt.Bir.BirJson.DocOptions);
        var root = doc.RootElement;
        if (!root.TryGetProperty("cirVersion", out _))
            return JsonDocument.Parse(json, DotKt.Bir.BirJson.DocOptions);

        if (root.TryGetProperty("cirDraft", out var draft) &&
            draft.TryGetProperty("executableCir", out var executable))
            return JsonDocument.Parse(executable.GetRawText(), DotKt.Bir.BirJson.DocOptions);

        throw new InvalidOperationException(
            $"native CIR input '{path}' does not contain cirDraft.executableCir");   // #84: Main adds the `ilemit: ` prefix
    }
}


// #84 Phase 1: a failure during body emission, tagged with WHICH declaration (`Type.method [node]`) was being
// emitted. Thrown per-method by EmitAssembly's body-emit guard; caught in IlEmit.Main for a clean one-line message.
// Not sealed: #84 Phase 4's CirSanityException derives from it so the SAME `ilemit: <Decl>: <message>` catch in
// Main handles both (a sanity violation bakes a `sanity: ` prefix into its message).
class CirEmitException : Exception
{
    public string Decl { get; }
    public CirEmitException(string decl, string message, Exception inner) : base(message, inner) { Decl = decl; }
}

sealed partial class Emitter
{
    readonly string _outDir;
    readonly string _asmName;
    // #71 S2: ALL round-trip metadata ([Kotlin*]/[KotlinInline]/NRT + the attr class defs) is now GENERATED by bir2cir
    // (RoundtripMetadata) as ordinary CIR attrs/type-decls and SKIPPED in the runtime build there — so ilemit's old
    // `_stripMetadata` gate is gone. bir2cir ALSO strips kotc's verbatim user annotations from the runtime CIR
    // (RoundtripMetadata.StripRuntimeAttrs), so a runtime-build CIR reaches ilemit already free of every applied
    // attribute; ilemit just stamps whatever `attrs` a CIR carries, with no build-mode knowledge.
    readonly Dictionary<string, TypeInfo> _types = new();
    ModuleBuilder _mod;

    // Crash localizer: Reflection.Emit can hard-CRASH the process (access violation, 0xC0000005) — uncatchable — on a
    // pathological reference (e.g. a WinRT/COM projection type) rather than throwing. With ILEMIT_TRACE set, each pass
    // step prints (flushed) to stderr, so the LAST line before the crash names the culprit type/method.
    static readonly bool Trace = Environment.GetEnvironmentVariable("ILEMIT_TRACE") != null;
    internal static void T(string m) { if (Trace) { Console.Error.WriteLine("[ilemit] " + m); Console.Error.Flush(); } }

    // #84 Phase 1 diagnostic breadcrumb: the declaration (and current node kind) being emitted, so a throw deep in
    // resolution surfaces as `ilemit: <Type>.<method> [node]: <message>`. Set at EmitMethodBody/EmitCtorBody head,
    // refined per node in EmitStmt/EmitExpr. Pure error-path plumbing — no IL effect (a valid emit is byte-identical).
    string _ctxType;
    string _ctxMethod;
    string _ctxNode;
    // #112 Phase 2: the decl's originating `File.kt:line` (from the CIR decl's `pos`), prefixed onto the breadcrumb so
    // an emit throw reads `ilemit: File.kt:42: Foo.bar [node]: <message>`. Null when the decl carries no `pos`
    // (a synthetic with no source) -> the breadcrumb degrades to the pre-#112 `Type.method` form.
    string _ctxPos;
    internal string CurrentDecl =>
        (_ctxPos != null ? _ctxPos + ": " : "")
        + (_ctxType ?? "?") + "." + (_ctxMethod ?? "?") + (_ctxNode != null ? " [" + _ctxNode + "]" : "");

    // Extract the `File.kt:line` (or bare `File.kt`) from a decl's optional `pos` {f,l,c}; null when absent.
    static string PosOf(JsonElement decl)
    {
        if (decl.ValueKind != JsonValueKind.Object || !decl.TryGetProperty("pos", out var pos) || pos.ValueKind != JsonValueKind.Object) return null;
        if (!pos.TryGetProperty("f", out var f) || f.ValueKind != JsonValueKind.String) return null;
        var file = Path.GetFileName(f.GetString());
        if (pos.TryGetProperty("l", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var line) && line >= 0)
            return file + ":" + line;
        return file;
    }

    // per-method context
    ILGenerator _il;
    readonly Dictionary<string, int> _args = new();
    readonly Dictionary<string, Type> _argTypes = new();
    readonly Dictionary<string, LocalBuilder> _locals = new();
    readonly Dictionary<MethodInfo, Type[]> _mparams = new();   // declared param types per method (for call-site boxing)
    // active try blocks: a `return` inside stores to the result local and leaves to the end label. `labels` = the
    // CFG-`label` ids declared physically inside this protected region, so a `goto` that exits it emits `leave` not `br`.
    readonly Stack<(LocalBuilder result, Label end, HashSet<int> labels)> _tryStack = new();
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
