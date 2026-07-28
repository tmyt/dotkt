using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// CALL-EVALUATION PLAN LOWERING (BIR `callEval`/`bindRef`/`delegationBindings` -> today's vocabulary).
//
// kotc emits an ORDERED PLAN at every call site whose values can acquire a second reader (docs/bir-cir-spec.md §2.7):
// the receiver, the supplied arguments and the filled defaults, in Kotlin evaluation order, each a BINDING; every
// reader — the call's own slot, a spliced default, a reconstructed `copy` field — is a `bindRef`, a pure READ.
//
// This pass runs once all splices have finished (right after DefaultArgSplice, which materialises the cross-module
// fills into the bindings kotc reserved for them) and turns each plan into locals:
//
//   * a binding with exactly ONE reader is INLINED back into that reader — it was never anything but the expression
//     in its own slot, so the emitted CIR is what it would have been without a plan;
//   * a binding with SEVERAL readers becomes a `var`, so the value is evaluated once and every reader loads it;
//   * a binding NOTHING reads is evaluated as a `var` anyway, because Kotlin evaluates every value the call supplies —
//     unless evaluating it is unobservable (BindingStability.IsTriviallyPure), in which case it is dropped;
//   * ORDER is never traded: if any binding becomes a `var`, every earlier non-stable binding becomes one too, or its
//     evaluation would slide behind it. That rule is what the three failed attempts in PR #270 each gave up one of.
//
// It decides NOTHING about storage. A `var` here is a request for a scoped local; whether the coroutine state machine
// can keep it as a MoveNext local or must promote it to an instance field — and whether the CLR admits the type at
// all — is SuspendColdLowering's single decision, ~300 passes later, from liveness. Splitting those two questions is
// the point of the redesign: this pass cannot be pushed into "declining to bind" by a storage answer it never sees.
//
// The plan's own vocabulary must not survive: the terminal assert refuses a leftover `callEval`/`bindRef`/
// `delegationBindings`, and scripts/verify-schema.py enforces the same split structurally.
static class CallEvalLowering
{
    public static void Apply(JsonNode root)
    {
        Walk(root);
        AssertLowered(root, "");
    }

    // POST-ORDER: a nested plan (a default that is itself a call with defaults) lowers first, so by the time the outer
    // plan lowers, the inner is an ordinary `valueBlock` — which may still contain reads of the OUTER plan's bindings,
    // and those are substituted here. An unknown `bindRef` id is therefore left alone on purpose: it belongs to an
    // enclosing plan that has not lowered yet.
    static void Walk(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj.ToList()) if (kv.Value != null) Walk(kv.Value);
            if (Str(obj["k"]) == "callEval") LowerExpression(obj);
            else if (obj["delegationBindings"] is JsonArray) LowerDelegation(obj);
        }
        else if (node is JsonArray arr)
        {
            foreach (var it in arr.ToList()) if (it != null) Walk(it);
        }
    }

    /// A `callEval` in EXPRESSION position: the materialised bindings become the `stmts` of a `valueBlock` whose
    /// `result` is the call. No materialised binding ⇒ no block at all, and the node collapses to the bare call.
    static void LowerExpression(JsonObject plan)
    {
        var bindings = plan["bindings"] as JsonArray ?? new JsonArray();
        var call = plan["expr"] ?? throw new InvalidOperationException("bir2cir: a callEval carries no `expr`");
        var type = plan["type"]?.DeepClone();
        var (stmts, repl) = Materialise(bindings, new List<JsonNode> { call }, DescribeExpression(call));
        var lowered = Substitute(call, repl);
        plan.Clear();
        if (stmts.Count == 0)
        {
            foreach (var kv in ((JsonObject)lowered).ToList()) plan[kv.Key] = kv.Value?.DeepClone();
            return;
        }
        plan["k"] = "valueBlock";
        if (type != null) plan["type"] = type;
        plan["stmts"] = stmts;
        plan["result"] = lowered;
    }

    /// A constructor DELEGATION's plan. Its arguments ride the ctor DECLARATION (`thisArgs`/`baseArgs`), so there is
    /// no wrapping expression to hold the bindings: they become `preStmts`, which ilemit emits ahead of the `this`/
    /// `base` call — the first thing the constructor body does.
    static void LowerDelegation(JsonObject ctor)
    {
        var bindings = (JsonArray)ctor["delegationBindings"];
        var readers = new List<JsonNode>();
        if (ctor["thisArgs"] is JsonArray ta) readers.Add(ta);
        if (ctor["baseArgs"] is JsonArray ba) readers.Add(ba);
        var (stmts, repl) = Materialise(bindings, readers, "a constructor delegation");
        foreach (var r in readers.ToList())
        {
            var arr = (JsonArray)r;
            for (var i = 0; i < arr.Count; i++) if (arr[i] is JsonNode v) arr[i] = Substitute(v, repl);
        }
        ctor.Remove("delegationBindings");
        if (stmts.Count > 0) ctor["preStmts"] = stmts;
    }

    /// Decide each binding's physical form. Returns the `var` statements in plan order, and how each binding id is to
    /// be READ: a `local` load for a materialised binding, the expression itself for an inlined one, null for a
    /// dropped one.
    static (JsonArray Stmts, Dictionary<string, JsonNode> Repl) Materialise(
        JsonArray bindings, List<JsonNode> readers, string site)
    {
        var n = bindings.Count;
        var ids = new string[n];
        var kinds = new string[n];
        var stable = new bool[n];
        var roles = new string[n];
        for (var i = 0; i < n; i++)
        {
            var b = bindings[i] as JsonObject ?? throw new InvalidOperationException("bir2cir: a call-evaluation plan binding is not an object");
            ids[i] = Str(b["id"]) ?? throw new InvalidOperationException("bir2cir: a call-evaluation plan binding carries no `id`");
            kinds[i] = Str(b["kind"]) ?? "value";
            stable[i] = (b["stable"] as JsonValue)?.TryGetValue<bool>(out var sv) == true && sv;
            roles[i] = Str(b["role"]);
        }
        // READER COUNT, derived rather than stored: a plan records no `consumers` list, because a second
        // representation of "who reads this" is exactly the disease the plan cures — every splice would have to
        // maintain it. Counting a `bindRef` scan here is always right by construction.
        var reads = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in ids) reads[id] = 0;
        foreach (var r in readers) CountReads(r, reads);
        for (var i = 0; i < n; i++) CountReads((bindings[i] as JsonObject)["expr"], reads);

        var drop = new bool[n];
        var asVar = new bool[n];
        for (var i = 0; i < n; i++)
        {
            var expr = (bindings[i] as JsonObject)["expr"];
            var count = reads[ids[i]];
            // Nothing reads it and evaluating it cannot be observed — the emitted call shape simply has no slot for
            // this value (a companion object's dispatch receiver, say). Drop it rather than mint a dead local.
            drop[i] = count == 0 && BindingStability.IsTriviallyPure(expr);
            // An ADDRESS binding is a placement marker, not a value: no storage can hold a managed pointer, so it is
            // always re-inlined at its slot. A STABLE value is free to re-read, so it inlines at every reader.
            asVar[i] = !drop[i] && kinds[i] == "value" && !stable[i] && count != 1;
        }
        // ORDER. A materialised binding is evaluated ahead of the call, so every earlier value must be materialised
        // too or it would slide behind it — the `d` before `T` / `dH` before `Hd` class of reorder. A STABLE value is
        // exempt by definition (re-reading it cannot observe a different value). An ADDRESS cannot be materialised at
        // all, so a non-stable one to the left of a materialised binding has no representation: say so.
        var last = Array.FindLastIndex(asVar, x => x);
        for (var i = 0; i < last; i++)
        {
            if (stable[i] || drop[i] || asVar[i]) continue;
            if (kinds[i] == "address")
                throw new InvalidOperationException(
                    $"bir2cir: in {site}, the {roles[i] ?? "argument"} passes an address that cannot be bound, while a " +
                    "later value of the same call must be evaluated ahead of the call. There is no CLR form that keeps " +
                    "both the address and Kotlin's evaluation order; pass the argument explicitly, or take the address " +
                    "of a plain local.");
            asVar[i] = true;
        }

        var stmts = new JsonArray();
        var repl = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
            var b = (JsonObject)bindings[i];
            // A binding may READ an earlier binding of the same plan (`b: Int = a * 10`). Resolve those first, so what
            // is materialised or inlined is already in the emitted vocabulary.
            var expr = Substitute(b["expr"], repl);
            if (drop[i]) { repl[ids[i]] = null; continue; }
            if (!asVar[i]) { repl[ids[i]] = expr; continue; }
            var type = VarType(b);
            var decl = new JsonObject { ["k"] = "var", ["name"] = ids[i], ["type"] = type?.DeepClone(), ["init"] = expr };
            // The source ROLE travels with the local so a storage refusal names "the receiver of `copy`" rather than
            // the minted id (FieldLegality.SuspendMessage).
            if (roles[i] != null) decl["role"] = roles[i];
            stmts.Add(decl);
            var read = new JsonObject { ["k"] = "local", ["name"] = ids[i] };
            if (type != null) read["sty"] = type.DeepClone();
            repl[ids[i]] = read;
        }
        return (stmts, repl);
    }

    /// The local's declared type. The binding's own `type` is the caller-instantiated semantic type kotc resolved and
    /// is used whenever it is closed; a type that still names a positional type VARIABLE would resolve in the wrong
    /// generic frame, so the bound value's own static type — concrete at this call site — is preferred then. Neither
    /// closed is a best effort rather than a refusal: refusing to bind is what duplicated the evaluation.
    static JsonNode VarType(JsonObject binding)
    {
        var declared = binding["type"];
        if (declared != null && TypeJson.Read(declared) is TypeNode d && !IsOpen(d)) return declared;
        var own = (binding["expr"] as JsonObject)?["sty"] ?? (binding["expr"] as JsonObject)?["type"];
        if (own != null && TypeJson.Read(own) is TypeNode o && !IsOpen(o)) return own;
        return declared ?? own;
    }

    // A type mentioning a positional type VARIABLE: it names a slot in the DECLARING generic's frame, so it cannot be
    // written into a local of this body without re-resolving it there.
    static bool IsOpen(TypeNode t) => t switch
    {
        TypeNode.Tv => true,
        TypeNode.ByRef b => IsOpen(b.Of),
        TypeNode.Array a => IsOpen(a.Elem),
        TypeNode.Nullable nl => IsOpen(nl.Of),
        TypeNode.Oblivious ob => IsOpen(ob.Of),
        TypeNode.Fqn f => f.Args != null && f.Args.Any(IsOpen),
        TypeNode.Fn fn => fn.Params.Any(IsOpen) || IsOpen(fn.Ret),
        _ => false,
    };

    static void CountReads(JsonNode node, Dictionary<string, int> into)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "bindRef" && Str(o["id"]) is string id && into.ContainsKey(id)) into[id]++;
            foreach (var kv in o) if (kv.Value != null) CountReads(kv.Value, into);
        }
        else if (node is JsonArray a) foreach (var it in a) if (it != null) CountReads(it, into);
    }

    /// Rebuild `node` with every `bindRef` of `repl` replaced by a fresh clone of its resolved form. Rebuilt (never
    /// mutated in place) so no node is double-parented, and cloned per occurrence because an inlined binding with more
    /// than one reader is stable — cloning a stable READ is not cloning an evaluation.
    static JsonNode Substitute(JsonNode node, Dictionary<string, JsonNode> repl)
    {
        switch (node)
        {
            case JsonObject o when Str(o["k"]) == "bindRef" && Str(o["id"]) is string id && repl.TryGetValue(id, out var to):
                return to?.DeepClone();
            case JsonObject o:
            {
                var res = new JsonObject();
                foreach (var kv in o) res[kv.Key] = kv.Value == null ? null : Substitute(kv.Value, repl);
                return res;
            }
            case JsonArray a:
            {
                var res = new JsonArray();
                foreach (var it in a) res.Add(it == null ? null : Substitute(it, repl));
                return res;
            }
            default: return node.DeepClone();
        }
    }

    // How a diagnostic names the call site: the callee if the node has one, else the node kind.
    static string DescribeExpression(JsonNode call) =>
        call is JsonObject o
            ? (Str(o["method"]) is string m ? $"the call to `{m}`"
                : TypeJson.OwnerName(o["type"]) is string t ? $"the construction of `{t}`" : "a call")
            : "a call";

    /// CHOKEPOINT: no plan vocabulary reaches the passes below. Everything after this point sees `var`/`local`/
    /// `valueBlock`/`preStmts` only, which is why ~300 later passes need to know nothing about the plan.
    static void AssertLowered(JsonNode node, string path)
    {
        if (node is JsonObject o)
        {
            var k = Str(o["k"]);
            if (k == "callEval" || k == "bindRef" || o.ContainsKey("delegationBindings"))
                throw new InvalidOperationException(
                    $"bir2cir: a call-evaluation plan survived CallEvalLowering at {path} (`{k ?? "delegationBindings"}`) — " +
                    "a pass between kotc and this one rewrote a plan into a shape the lowering does not reach.");
            foreach (var kv in o) if (kv.Value != null) AssertLowered(kv.Value, path + "/" + kv.Key);
        }
        else if (node is JsonArray a)
            for (var i = 0; i < a.Count; i++) if (a[i] is JsonNode it) AssertLowered(it, path + "[" + i + "]");
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
