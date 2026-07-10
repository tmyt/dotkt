using System.Collections.Generic;
using System.Threading;
using System.Text.Json.Nodes;
using DotKt.Bir;

// TOP-LEVEL `repeat(n, <action>)` INLINE LOOP — the NON-LITERAL-lambda fallback (#73 M7 / #75). A LITERAL-lambda
// `repeat(n){…}` is now spliced by kotc into a `callInline` node and lowered by InlineSplice (which honors a non-local
// `return`); this pass handles only the residual shape where the action is NOT a lambda literal — e.g. `repeat(n, ::fn)`,
// a callable reference kotc emits as a `callStatic owner:null method:repeat args:[<n>, <action>]` with the action as a
// `newClosure`/`newDelegate`. This @InlineOnly stdlib helper has NO rt.dll body, so bir2cir re-emits the counted loop
// (n evaluated ONCE, index 0..n-1) that invokes the action once per iteration:
//
//   repeat(n) { i -> body }  ->  { var __repc = <action>; repeatInline __rep in 0..<n>-1 { __repc.invoke(__rep) } }
//
// The action is loop-INVOKED as a delegate (`delegateInvoke`), which is SHAPE-AGNOSTIC over kotc's two lambda-arg
// forms — a capturing `newClosure` (body inline in `synthClass`, capture reads already rewritten to `this.<field>`) and
// a non-capturing `newDelegate` (body hoisted into a lifted static method). Both ARE delegate values, so invoking the
// value sidesteps splicing either body. The action is hoisted into a `var` so the closure/delegate is allocated ONCE,
// before the loop; `times` is hoisted into a preceding `var` so it is evaluated once and BEFORE the action (Kotlin
// arg-0-before-arg-1 order); `repeatInline` (ilemit Emitter.Statements/Expressions) re-reads that local.
//
// LIMITATION (recorded in docs/dotkt-semantics.md): because the action is invoked as a delegate rather than spliced,
// a NON-LOCAL `return` through this fallback does NOT return from the enclosing function. This only affects the
// non-literal (callable-reference) action; a literal-lambda `repeat{…}` splices via InlineSplice and DOES honor a
// non-local return. A non-literal argument to `repeat` is only a callable reference (its param is not `noinline`).
//
// The action's function type is read off the call's `sig[1]` (the declared `(Int)->Unit` — always concrete for repeat),
// used both as the hoist var's type and the delegateInvoke `funcType`. Gate: `owner:null method:repeat`, 2 args, and
// `sig[1]` a function type (so `CharSequence.repeat(n): String` — a member call carrying ownerType — never matches).
// A user top-level `fun repeat(...)` shadow (app build) is skipped via localTopLevelFns.
//
// Runs BEFORE ClosureSynthesis so the action's `newClosure` (moved into the hoist var's init) is synthesized into a
// closure class exactly once there, and before MemberCallSubstitution (the bodiless @InlineOnly `repeat` never binds).
// Unconditional (ref + rt + app).
static class RepeatInlineLowering
{
    static int _counter;

    public static void Apply(JsonNode node, ISet<string> localTopLevelFns, bool appBuild)
    {
        if (node is JsonObject o)
        {
            foreach (var kv in o) if (kv.Value != null) Apply(kv.Value, localTopLevelFns, appBuild);
            Rewrite(o, localTopLevelFns, appBuild);
        }
        else if (node is JsonArray a)
        {
            for (var i = 0; i < a.Count; i++)
                if (a[i] is JsonNode c)
                {
                    Apply(c, localTopLevelFns, appBuild);
                    if (a[i] is JsonObject co) Rewrite(co, localTopLevelFns, appBuild);
                }
        }
    }

    static void Rewrite(JsonObject o, ISet<string> localTopLevelFns, bool appBuild)
    {
        if (Str(o["k"]) != "callStatic" || Str(o["method"]) != "repeat") return;
        // Top-level call only (owner:null, no ownerType) — the member `CharSequence.repeat(n)` carries ownerType.
        if (o.ContainsKey("ownerType") || !o.ContainsKey("owner") || o["owner"] != null) return;
        // A top-level PROPERTY accessor rides the same owner:null shape (`prop:` marker, #81) — exclude a user `val repeat`.
        if (o.ContainsKey("prop")) return;
        // User top-level `fun repeat(...)` shadow (app build) is NOT the kotlin intrinsic — leave it.
        if (appBuild && localTopLevelFns.Contains("repeat")) return;
        if (o["args"] is not JsonArray args || args.Count != 2
            || args[0] is not JsonNode count || args[1] is not JsonNode action) return;
        // The action parameter must be a function type (distinguishes the top-level repeat(n){} from any other overload).
        if (o["sig"] is not JsonArray sig || sig.Count < 2 || sig[0] is not JsonNode countType || sig[1] is not JsonNode funcType
            || TypeJson.Read(funcType) is not TypeNode.Fn) return;

        var n = "$" + Interlocked.Increment(ref _counter);
        var repnVar = "__repn" + n;   // `times` evaluated ONCE, FIRST (Kotlin evaluates arg0 before the action arg1)
        var repcVar = "__repc" + n;   // the hoisted action delegate (allocated once)
        var repVar = "__rep" + n;     // the loop index local (0..times-1)

        var repl = new JsonObject
        {
            ["k"] = "valueBlock",
            ["stmts"] = new JsonArray
            {
                new JsonObject { ["k"] = "var", ["name"] = repnVar, ["type"] = countType.DeepClone(), ["init"] = count.DeepClone() },
                new JsonObject { ["k"] = "var", ["name"] = repcVar, ["type"] = funcType.DeepClone(), ["init"] = action.DeepClone() },
            },
            ["result"] = new JsonObject
            {
                ["k"] = "repeatInline",
                ["var"] = repVar,
                ["count"] = new JsonObject { ["k"] = "local", ["name"] = repnVar },
                ["body"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["k"] = "exprStmt",
                        ["expr"] = new JsonObject
                        {
                            ["k"] = "delegateInvoke",
                            ["funcType"] = funcType.DeepClone(),
                            ["recv"] = new JsonObject { ["k"] = "local", ["name"] = repcVar },
                            ["args"] = new JsonArray { new JsonObject { ["k"] = "local", ["name"] = repVar } },
                        },
                    },
                },
            },
        };

        foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
        foreach (var kv in repl) o[kv.Key] = kv.Value?.DeepClone();
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
