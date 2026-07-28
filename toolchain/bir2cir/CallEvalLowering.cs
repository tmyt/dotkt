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
        // READER COUNT and READER POSITION, both DERIVED rather than stored: a plan records no `consumers` list,
        // because a second representation of "who reads this" is exactly the disease the plan cures — every splice
        // would have to maintain it. One ordered `bindRef` scan of the emitted node answers both, and is right by
        // construction. `pos` is where a binding's FIRST read sits in the node's own evaluation order (receiver, then
        // the argument slots left to right, recursively) — which is where an INLINED binding will be evaluated.
        var reads = new Dictionary<string, int>(StringComparer.Ordinal);
        var pos = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in ids) reads[id] = 0;
        var rank = 0;
        foreach (var r in readers) ScanReads(r, reads, pos, ref rank);
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
            // A STABLE value is free to re-read, so it inlines at every reader. Everything else needs a local unless
            // exactly one reader puts it back where it already was.
            asVar[i] = !drop[i] && !stable[i] && count != 1;
        }
        // ORDER, rule 1 — INVERSION. An inlined binding is evaluated at its reader's position, and a plan's order is
        // NOT the node's: an omitted default occupies a slot the callee declared, while Kotlin evaluates it after every
        // value the call SUPPLIES, whatever slot that value sits in. So `f(a: Int = d(), y: Int)` called `f(y = p())`
        // plans [y, a] but emits args [a, y]. Where a later binding reads an EARLIER position, the earlier binding must
        // be materialised — moving it ahead of the call, which is ahead of everything.
        for (var i = 0; i < n; i++)
        {
            if (stable[i] || drop[i] || asVar[i]) continue;
            for (var k = i + 1; k < n; k++)
                if (!stable[k] && !drop[k] && ReadPos(pos, ids[k]) < ReadPos(pos, ids[i])) { asVar[i] = true; break; }
        }
        // ORDER, rule 2 — PREFIX. A materialised binding is evaluated ahead of the call, so every earlier value must be
        // materialised too or it would slide behind it (the `d` before `T` / `dH` before `Hd` class of reorder). A
        // STABLE value is exempt by definition: re-reading it cannot observe a different value.
        var last = Array.FindLastIndex(asVar, x => x);
        for (var i = 0; i < last; i++)
            if (!stable[i] && !drop[i]) asVar[i] = true;
        // An ADDRESS is not a value: no storage holds a managed pointer, so the binding itself never becomes a local.
        // What CAN be pinned is the value the address is computed FROM — `byref(mk().field)` binds `mk()`, and the
        // lvalue `<local>.field` then stays inline at its slot, taken from a value already evaluated in plan order.
        // A plain `byref(x)` needs nothing: reading an lvalue has no side effect and cannot be observed out of order.
        var pinned = new JsonArray();
        for (var i = 0; i < n; i++)
        {
            if (kinds[i] != "address") continue;
            if (asVar[i]) PinAddressOperands((bindings[i] as JsonObject)["expr"], pinned);
            asVar[i] = false;
        }

        var stmts = pinned;
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
            // A FRESH local name, not the binding id. A binding id is unique in its PRODUCER's counter, and a plan can
            // arrive here having been cloned out of another module — a spliced `[KotlinInline]` body, a materialised
            // `@KotlinDefault` carrier — so two plans in one frame can carry the same id. The name is minted here, in
            // the frame that will hold it, which is the only counter that can promise uniqueness there.
            var name = FreshLocal();
            var decl = new JsonObject { ["k"] = "var", ["name"] = name, ["type"] = type?.DeepClone(), ["init"] = expr };
            // The source ROLE travels with the local so a storage refusal names "the receiver of `copy`" rather than
            // the minted name (FieldLegality.SuspendMessage).
            if (roles[i] != null) decl["role"] = roles[i];
            stmts.Add(decl);
            var read = new JsonObject { ["k"] = "local", ["name"] = name };
            if (type != null) read["sty"] = type.DeepClone();
            repl[ids[i]] = read;
        }
        return (stmts, repl);
    }

    /// Re-mint the binding ids of every plan in `subtree`, and the reads that resolve to them.
    ///
    /// A binding id is unique only in its PRODUCER's counter. Two paths clone a plan out of another module into a
    /// consumer document — an inline `[KotlinInline]` body spliced at a call site, and a `@KotlinDefault` carrier
    /// materialised into a reserved binding — and both can land two copies of one plan, or two plans from different
    /// producers, in a single frame. Worse, the carrier path SUBSTITUTES the consumer's own `bindRef`s into the
    /// carrier's subtree, so a coincidental id would make the inner plan's lowering swallow an outer read. Freshening
    /// at the clone is what keeps the ids a per-document fact, so nothing downstream has to reason about provenance.
    internal static void FreshenPlanIds(JsonNode subtree)
    {
        if (subtree is JsonObject o)
        {
            if (Str(o["k"]) == "callEval" && o["bindings"] is JsonArray bs) Rename(o, bs);
            else if (o["delegationBindings"] is JsonArray ds) Rename(o, ds);
            foreach (var kv in o.ToList()) if (kv.Value != null) FreshenPlanIds(kv.Value);
        }
        else if (subtree is JsonArray a) foreach (var it in a.ToList()) if (it != null) FreshenPlanIds(it);

        static void Rename(JsonObject host, JsonArray bindings)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var b in bindings)
                if (b is JsonObject bo && Str(bo["id"]) is string old)
                { var fresh = "cir$b" + System.Threading.Interlocked.Increment(ref _counter); map[old] = fresh; bo["id"] = fresh; }
            if (map.Count > 0) RewriteReads(host, map);
        }

        // A NESTED plan's ids come from the same producer counter, so they are absent from this map and its reads pass
        // through untouched — one whole-subtree walk per plan is therefore correct, not merely convenient.
        static void RewriteReads(JsonNode node, Dictionary<string, string> map)
        {
            if (node is JsonObject o)
            {
                if (Str(o["k"]) == "bindRef" && Str(o["id"]) is string id && map.TryGetValue(id, out var fresh)) o["id"] = fresh;
                foreach (var kv in o) if (kv.Value != null) RewriteReads(kv.Value, map);
            }
            else if (node is JsonArray a) foreach (var it in a) if (it != null) RewriteReads(it, map);
        }
    }

    static int _counter;

    /// A frame-unique local name in bir2cir's own `cir$b…` namespace (spec §2.7). Minted from one process-global
    /// counter, so it is unique across every plan a compilation lowers — which is stronger than needed and cheaper
    /// than tracking frames.
    static string FreshLocal() => "cir$b" + System.Threading.Interlocked.Increment(ref _counter);

    /// Move the impure OPERANDS an addressable lvalue is computed from into locals, so the address stays at its own
    /// slot while what it is taken from is evaluated in plan order. kotc emits an address as `{k:local}` (nothing to
    /// pin) or `{k:field,recv:<expr>}` (the receiver is the operand); the walk is recursive so a field CHAIN pins its
    /// outermost impure link only, which is all that carries a side effect.
    static void PinAddressOperands(JsonNode address, JsonArray into)
    {
        if (address is not JsonObject o || o["recv"] is not JsonNode recv) return;
        if (BindingStability.IsTriviallyPure(recv)) { PinAddressOperands(recv, into); return; }
        var name = FreshLocal();
        var type = (recv as JsonObject)?["sty"] ?? (recv as JsonObject)?["type"];
        into.Add(new JsonObject { ["k"] = "var", ["name"] = name, ["type"] = type?.DeepClone(), ["init"] = recv.DeepClone() });
        var read = new JsonObject { ["k"] = "local", ["name"] = name };
        if (type != null) read["sty"] = type.DeepClone();
        o["recv"] = read;
    }

    static int ReadPos(Dictionary<string, int> pos, string id) => pos.TryGetValue(id, out var p) ? p : int.MaxValue;

    /// The local's declared type. The binding's own `type` is the caller-instantiated semantic type kotc resolved and
    /// is used whenever it is closed; a type that still names a positional type VARIABLE would resolve in the wrong
    /// generic frame, so the bound value's own static type — concrete at this call site — is preferred then. Neither
    /// closed is a best effort rather than a refusal: refusing to bind is what duplicated the evaluation.
    static JsonNode VarType(JsonObject binding)
    {
        var declared = binding["type"];
        if (declared != null && TypeJson.Read(declared) is TypeNode d && !IsOpen(d)) return declared;
        // The declared type still names a positional type VARIABLE. The bound VALUE's own static type was stamped by
        // kotc in the CALLER's frame, so it resolves where the local lives even when it is itself open — which the
        // declared type, written in the CALLEE's frame, may not. Prefer it whenever there is one.
        var own = (binding["expr"] as JsonObject)?["sty"] ?? (binding["expr"] as JsonObject)?["type"];
        return own ?? declared;
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

    /// One ordered walk of a reader root: counts every `bindRef` and records where each id is FIRST read. Operand
    /// order comes from SuspendLiveness.KeyRank — the single statement of "which operand of a node runs first" this
    /// toolchain has — so an inlined binding's position here is the position the emitted code will evaluate it at.
    static void ScanReads(JsonNode node, Dictionary<string, int> counts, Dictionary<string, int> pos, ref int rank)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "bindRef" && Str(o["id"]) is string id && counts.ContainsKey(id))
            {
                counts[id]++;
                if (!pos.ContainsKey(id)) pos[id] = rank;
            }
            rank++;
            foreach (var kv in o.OrderBy(kv => SuspendLiveness.OperandRank(kv.Key)).ToList())
                if (kv.Value != null) ScanReads(kv.Value, counts, pos, ref rank);
        }
        else if (node is JsonArray a)
            foreach (var it in a) if (it != null) ScanReads(it, counts, pos, ref rank);
    }

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
