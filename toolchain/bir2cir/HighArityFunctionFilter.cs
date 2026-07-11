using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// HIGH-ARITY FUNCTION-TYPE DECL FILTER (#72, moved from kotc). System.Func/Action cap at 16 parameters, but a Kotlin
// function VALUE can be wider — ilemit SYNTHESIZES a module-local delegate DotKt.Runtime.CompilerServices.KFunc`N /
// KAction`N for it (Emitter.Delegates.cs; exercised by verify-wide-delegates). So an APP build LEAVES a >16-param
// function type ALONE — it emits fine via the synthesized delegate. This filter's sole job is the STDLIB self-build,
// where the 6 `context()` overloads (package `kotlin`, Context.kt, arity 17-22) are genuinely not emittable and are
// DROPPED with a warning (the same silent skip kotc's retired skipStdlibHighArityFunctionType did). Runs at the head of
// the per-file loop, BEFORE ClosureSynthesis, so a dropped body's lambdas are never synthesized into orphan closure
// types. (#97: the former app-mode hard error was a #72 over-reach — it broke the KFunc/KAction synthesis; removed.)
static class HighArityFunctionFilter
{
    public static void Apply(JsonNode root, BuildStdlibMode mode)
    {
        if (root != null) FilterRecursively(root, mode);
    }

    static void FilterRecursively(JsonNode node, BuildStdlibMode mode)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["methods"] is JsonArray methods) FilterMethodArray(methods, mode);
                foreach (var kv in obj) if (kv.Value != null) FilterRecursively(kv.Value, mode);
                break;
            case JsonArray arr:
                foreach (var it in arr) if (it != null) FilterRecursively(it, mode);
                break;
        }
    }

    static void FilterMethodArray(JsonArray methods, BuildStdlibMode mode)
    {
        for (int i = methods.Count - 1; i >= 0; i--)
        {
            if (methods[i] is not JsonObject m) continue;
            int arity = MethodHighArityFn(m);
            if (arity <= 16) continue;
            // App build: leave it — ilemit synthesizes KFunc`N/KAction`N for the wide function value (#97).
            if (mode == BuildStdlibMode.App) continue;
            var name = (m["name"] as JsonValue)?.GetValue<string>() ?? "<anonymous>";
            Console.Error.WriteLine(
                $"bir2cir: WARNING [DOTKT-STDLIB] skipped {name}: function type with {arity} parameters exceeds System.Func/Action's 16-parameter limit");
            methods.RemoveAt(i);
        }
    }

    // Returns the offending function-type arity (>16) found anywhere in the method's signature, else 0.
    static int MethodHighArityFn(JsonObject m)
    {
        int arity = 0;
        if (m["ret"] is JsonNode ret) arity = Math.Max(arity, TypeHighArityFn(ret));
        if (m["params"] is JsonArray ps)
            foreach (var p in ps)
                if (p is JsonObject po && po["type"] is JsonNode pt) arity = Math.Max(arity, TypeHighArityFn(pt));
        return arity;
    }

    static int TypeHighArityFn(JsonNode typeJson) =>
        TypeJson.Read(typeJson) is TypeNode t ? HighArity(t) : 0;

    static int Max4(int a, int b, int c, int d) => Math.Max(Math.Max(a, b), Math.Max(c, d));

    static int HighArity(TypeNode t) => t switch
    {
        // A function-type value (Kotlin FunctionN / SuspendFunctionN) — the direct >16-param case.
        TypeNode.Fn fn => Max4(
            fn.Params.Length > 16 ? fn.Params.Length : 0,
            HighArity(fn.Ret),
            fn.Params.Length == 0 ? 0 : fn.Params.Max(HighArity),
            fn.Recv is not null ? HighArity(fn.Recv) : 0),
        // A KFunctionN with type args is already folded to a `fn` node by kotc's birType, so this Fqn arm is a defensive
        // guard for a raw/argless KFunction token (N params + 1 return = N+1 args), never the primary path.
        TypeNode.Fqn f => Math.Max(
            (f.Name.StartsWith("kotlin.reflect.KFunction", StringComparison.Ordinal) && f.Args is { Length: > 17 }) ? f.Args!.Length - 1 : 0,
            (f.Args is null || f.Args.Length == 0) ? 0 : f.Args.Max(HighArity)),
        TypeNode.Nullable n => HighArity(n.Of),
        TypeNode.Oblivious ob => HighArity(ob.Of),
        TypeNode.Array a => HighArity(a.Elem),
        TypeNode.ByRef b => HighArity(b.Of),
        _ => 0,
    };
}
