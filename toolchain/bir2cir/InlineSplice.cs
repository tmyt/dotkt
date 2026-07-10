using System.Collections.Generic;
using System.Threading;
using System.Text.Json.Nodes;

// INLINE SPLICE (#75). kotc emits a `callInline` node for an inline stdlib function whose lambda body it carries
// UN-CLOSURED, in the CALLER's scope — the only shape from which a NON-LOCAL `return` (legal because the callee is
// `inline`) survives as the caller's return. bir2cir wraps that carried body in the structural lowering the callee
// stands for. This SUPERSEDES the delegate-invoke form (RepeatInlineLowering) for `repeat`, restoring the non-local
// return that #73 M7 deferred (a `delegateInvoke` on a closured lambda returns from the lambda, not the enclosing fn).
//
//   repeat(n) { i -> body }   (kotc callInline: count, loopVar, body[ …, {label end} ])
//     ->  { var __repn = <count>;  repeatInline __repN in 0..__repn-1 { <body> } }
//
// The carried body already has its returns routed by kotc: a labeled `return@repeat` (loop-local) -> `goto <end>`
// where `end` is the trailing `{k:label}` of the body (lands BEFORE the loop increment = `continue`); a NON-LOCAL
// `return` stays a plain `{k:return}` = the caller's return. References to the lambda's index parameter are already
// rewritten to `{k:local,name:<loopVar>}`, which becomes the `repeatInline` loop variable.
//
// Runs at the same phase-1 position RepeatInlineLowering did (before ClosureSynthesis so any nested closure inside the
// spliced body is synthesized once, before MemberCallSubstitution). Unconditional (ref + rt + app). Only `kotlin.repeat`
// is handled today; scope/use and same-/cross-module inline (design #75 stages 2-4) extend this same node + pass.
static class InlineSplice
{
    static int _counter;

    public static void Apply(JsonNode node)
    {
        if (node is JsonObject o)
        {
            foreach (var kv in o) if (kv.Value != null) Apply(kv.Value);
            Rewrite(o);
        }
        else if (node is JsonArray a)
        {
            for (var i = 0; i < a.Count; i++)
                if (a[i] is JsonNode c)
                {
                    Apply(c);
                    if (a[i] is JsonObject co) Rewrite(co);
                }
        }
    }

    static void Rewrite(JsonObject o)
    {
        if (Str(o["k"]) != "callInline") return;
        switch (Str(o["callee"]))
        {
            case "kotlin.repeat": RewriteRepeat(o); break;
            // Unknown callee: leave it — a downstream 0-candidate / schema red flags the gap loudly rather than silently
            // miscompiling (there is no other producer of `callInline`, so this only fires if kotc widens emission).
        }
    }

    static void RewriteRepeat(JsonObject o)
    {
        if (o["count"] is not JsonNode count || o["body"] is not JsonArray body || Str(o["var"]) is not string loopVar)
            return;
        var countType = o["countType"]?.DeepClone() ?? TypeJson.Fqn("kotlin.Int");
        // `times` evaluated ONCE, before the loop. Distinct prefix from RepeatInlineLowering's `__repn$` (the two passes
        // have independent counters, so a shared prefix could mint the same local name for a literal + function-ref repeat).
        var repnVar = "__repns$" + Interlocked.Increment(ref _counter);

        var repl = new JsonObject
        {
            ["k"] = "valueBlock",
            ["stmts"] = new JsonArray
            {
                new JsonObject { ["k"] = "var", ["name"] = repnVar, ["type"] = countType, ["init"] = count.DeepClone() },
            },
            ["result"] = new JsonObject
            {
                ["k"] = "repeatInline",
                ["var"] = loopVar,
                ["count"] = new JsonObject { ["k"] = "local", ["name"] = repnVar },
                ["body"] = body.DeepClone(),
            },
        };

        foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
        foreach (var kv in repl) o[kv.Key] = kv.Value?.DeepClone();
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
