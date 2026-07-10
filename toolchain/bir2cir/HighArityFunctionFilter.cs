using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// HIGH-ARITY FUNCTION-TYPE DECL FILTER (#72, moved from kotc). System.Func/Action cap at 16 parameters, so a Kotlin
// declaration whose signature mentions a function type with >16 parameters (the 6 `context()` overloads in package
// `kotlin`, file Context.kt, arity 17-22) or a KFunction17+ has no CLR delegate to bind — a CLR-representation fact, so the decision
// lives HERE, not in kotc (which emits every decl faithfully). In a stdlib self-build the offending decls are DROPPED
// with a warning (they were never emittable — the same silent skip kotc's retired skipStdlibHighArityFunctionType did);
// in an app build a >16-param function type is a hard error (there is no valid emission). Runs at the head of the
// per-file loop, BEFORE ClosureSynthesis, so a dropped body's lambdas are never synthesized into orphan closure types.
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
            var name = (m["name"] as JsonValue)?.GetValue<string>() ?? "<anonymous>";
            if (mode == BuildStdlibMode.App)
                throw new InvalidOperationException(
                    $"bir2cir: function '{name}' uses a function type with {arity} parameters, which has no System.Func/Action on the CLR");
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
