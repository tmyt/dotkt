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
//     unless evaluating it is unobservable (ValueStability.IsDroppable), in which case it is dropped
//     (docs/dotkt-semantics.md §7a);
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
            // NORMALIZE, unconditionally: a block whose RESULT is a block is one block — its statements run, then the
            // inner's, then the inner's value — and downstream expects the single layer the inline splice's own
            // flatten guarantees. Not restricted to the nesting this pass just created: a nested block is the same
            // shape whoever built it, and folding one that was already flat costs nothing. It matters here because a
            // plan is what re-introduced the shape — a spliced lambda body ending in a nested inline call reaches this
            // pass as `valueBlock{…, result: callEval{…}}`, which the splice could not flatten, a plan's bindings not
            // being statements until the line above makes them ones.
            if (Str(obj["k"]) == "valueBlock") MergeNestedResult(obj);
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

    /// Fold a `valueBlock` whose RESULT is a `valueBlock` into one block. Evaluation order is untouched — this block's
    /// statements, then the inner's, then the inner's value — and every name in either is already frame-unique (splice
    /// prefixes, `dotkt$local…`, `cir$b…`), so the merged scope cannot capture. The inner's `type` is the value's, so
    /// it wins when this block carries none.
    ///
    /// A `valueBlock` may carry EITHER of two statement lists, and its consumers run `stmts` and then `body`
    /// (SuspendColdLowering, InlineSplice's carrier flatten). The inner block's two are appended in that order, which
    /// is the order it would have run them in; the OUTER block's `body` would have to run AFTER the inner statements
    /// arriving in its `stmts`, which appending cannot express. No producer emits an outer `body` beside a block
    /// result today, so this refuses rather than reorders — the day one does, it says so instead of miscompiling.
    static void MergeNestedResult(JsonObject block)
    {
        while (block["result"] is JsonObject inner && Str(inner["k"]) == "valueBlock")
        {
            if (block["body"] is JsonArray ob && ob.Count > 0)
                throw new InvalidOperationException(
                    "bir2cir: a valueBlock carrying `body` statements has another valueBlock as its result — folding "
                    + "them would run this block's `body` after the inner block's statements instead of before. The "
                    + "producer of this shape is new; teach CallEvalLowering.MergeNestedResult how to order it.");
            if (block["stmts"] is not JsonArray stmts) { stmts = new JsonArray(); block["stmts"] = stmts; }
            foreach (var key in new[] { "stmts", "body" })
                if (inner[key] is JsonArray arr)
                    foreach (var st in arr.ToList()) { arr.Remove(st); stmts.Add(st); }
            if (block["type"] == null && inner["type"] is JsonNode it) block["type"] = it.DeepClone();
            block["result"] = inner["result"] is JsonNode r ? r.DeepClone() : null;
        }
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
        // DEFERRED READS. A binding is inlined back into its reader only when that reader sits where the CALL sits —
        // on the node's eager operand spine, evaluated once and unconditionally. A read anywhere else (inside a
        // spliced inline body's statements, a conditional branch, a loop, a closure) happens at a different time, a
        // different number of times, or not at all, so the value is materialised instead. Kotlin evaluates what a call
        // supplies AT the call, exactly once, whatever the callee then does with it.
        var deferred = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids) reads[id] = 0;
        var rank = 0;
        foreach (var r in readers) ScanReads(r, reads, pos, deferred, ref rank, eager: true);
        for (var i = 0; i < n; i++) CountReads((bindings[i] as JsonObject)["expr"], reads);

        var drop = new bool[n];
        var asVar = new bool[n];
        for (var i = 0; i < n; i++)
        {
            var expr = (bindings[i] as JsonObject)["expr"];
            var count = reads[ids[i]];
            // NOTHING READS IT: the emitted call shape has no slot for this value. Q2 alone decides what happens then
            // (docs/dotkt-semantics.md §7a) — drop it if evaluating it cannot be observed, otherwise evaluate it into
            // a local nobody reads, because Kotlin evaluated it. `stable` has no say here: it answers whether a value
            // may be read TWICE, which is not a licence to read it ZERO times.
            drop[i] = count == 0 && ValueStability.IsDroppable(expr);
            // A STABLE value is free to re-read, so it inlines at every reader — wherever the reader is, since re-reading
            // it cannot observe a different value or a side effect. An ADDRESS is not a value at all — no storage holds a
            // managed pointer — so it never becomes a local; what its LOCATION is computed from is pinned instead, at
            // this binding's own position in the loop below. Everything else needs a local unless exactly one reader puts
            // it back where it already was, at the point the call itself is evaluated.
            asVar[i] = !drop[i] && kinds[i] != "address"
                && (count == 0 || (!stable[i] && (count != 1 || deferred.Contains(ids[i]))));
        }
        // ORDER, rule 1 — INVERSION. An inlined binding is evaluated at its reader's position, and a plan's order is
        // NOT the node's: an omitted default occupies a slot the callee declared, while Kotlin evaluates it after every
        // value the call SUPPLIES, whatever slot that value sits in. So `f(a: Int = d(), y: Int)` called `f(y = p())`
        // plans [y, a] but emits args [a, y]. Where a later binding reads an EARLIER position, the earlier binding must
        // be materialised — moving it ahead of the call, which is ahead of everything.
        for (var i = 0; i < n; i++)
        {
            if (stable[i] || drop[i] || asVar[i] || kinds[i] == "address") continue;
            for (var k = i + 1; k < n; k++)
                if (!stable[k] && !drop[k] && ReadPos(pos, ids[k]) < ReadPos(pos, ids[i])) { asVar[i] = true; break; }
        }
        // ORDER, rule 2 — PREFIX, over PRE-CALL WORK rather than over materialisation. THE invariant, in one place:
        //
        //     the emitted pre-call statement sequence is ordered by plan position, and a binding that emits ANY
        //     pre-call statement forces every earlier non-stable binding to emit one too.
        //
        // A binding that emits a pre-call statement is evaluated ahead of the call; a binding that is inlined is
        // evaluated at its slot, i.e. during it. So an earlier value left inline would slide behind a later one. This
        // is stated over pre-call WORK, not over `asVar`, because an ADDRESS binding also emits pre-call statements —
        // the pins for the values its location is computed from — and treating those as free was how an address pin
        // came to overtake an earlier inline binding. A STABLE value is exempt by definition: re-reading it cannot
        // observe a different value. An address needs no forcing of its own: its impure operands are already pinned at
        // its position, and what is left in the slot is a pure location expression.
        var work = new bool[n];
        for (var i = 0; i < n; i++)
            work[i] = asVar[i] || (kinds[i] == "address" && !drop[i] && LocationHasPinWork((bindings[i] as JsonObject)["expr"]));
        var last = Array.FindLastIndex(work, x => x);
        for (var i = 0; i < last; i++)
            if (!stable[i] && !drop[i] && kinds[i] != "address") asVar[i] = true;

        var stmts = new JsonArray();
        var repl = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        // ONE ordered stream. Every binding is handled at its own plan position — a materialised value declares its
        // local here, and an address pins the values its LOCATION is computed from here — so the statements come out
        // in plan order, which is Kotlin's, whatever mix of kinds the plan holds.
        for (var i = 0; i < n; i++)
        {
            var b = (JsonObject)bindings[i];
            // A binding may READ an earlier binding of the same plan (`b: Int = a * 10`). Resolve those first, so what
            // is materialised or inlined is already in the emitted vocabulary.
            var expr = Substitute(b["expr"], repl);
            if (drop[i]) { repl[ids[i]] = null; continue; }
            if (kinds[i] == "address")
            {
                // An address is a LOCATION, not a value: it must be taken at the call, from operands evaluated HERE.
                // Two shapes, by what the location's ROOT is:
                //  * an lvalue FORMER (`local`/`field`/`arrayGet`/…) has no side effect of its own, so only the values
                //    it is computed from move: `byref(mk().f)` pins `mk()`, `byref(a[i()])` pins `i()`, `byref(x)`
                //    pins nothing. The pure location stays in the slot, where the call takes its address.
                //  * anything else IS an evaluation — pinning its operands would still leave it running at the slot,
                //    once but at the wrong time — so the whole thing moves here into a local, and the slot reads that
                //    local. WHICH local is decided by the root's own DECLARED type, never by its shape: a
                //    byref-RETURNING call (`byref(c.refSlot(i()))`) becomes a `ref T` local holding the pointer
                //    (`byrefOf`, the shape `var x by byref(m())` already produces), while an ordinary rvalue call
                //    (`byref(w.plainInt())`, which the frontend accepts) becomes a plain `T` local whose ADDRESS the
                //    slot takes — the temporary-address lowering, just performed at the right point in the order.
                //    Storing a `T` into a `T&` slot, which assuming ref-return would do, is unverifiable IL.
                if (IsLvalueFormer(expr))
                {
                    PinLocationOperands(expr, stmts);
                    repl[ids[i]] = expr;
                }
                else
                {
                    var locType = StaticTypeOf(expr) ?? throw new InvalidOperationException(
                        $"bir2cir: cannot type the `{Str((expr as JsonObject)?["k"])}` expression a by-reference " +
                        "argument takes the address of, so the local that holds it would be untyped. Its node kind " +
                        "needs an arm in bir-common/NodeType.cs.");
                    var refName = FreshLocal();
                    var typeJson = TypeJson.Write(locType);
                    stmts.Add(new JsonObject
                    {
                        ["k"] = "var", ["name"] = refName, ["type"] = typeJson,
                        // A byref-returning root keeps its POINTER (`byrefOf`); an rvalue is stored by value.
                        ["init"] = locType is TypeNode.ByRef
                            ? new JsonObject { ["k"] = "byrefOf", ["inner"] = expr.DeepClone() }
                            : expr.DeepClone(),
                    });
                    repl[ids[i]] = new JsonObject
                    { ["k"] = "local", ["name"] = refName, ["sty"] = TypeJson.Write(locType) };
                }
                continue;
            }
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

    /// The shared node-local deriver, bound to this toolchain's specialized-array table. One place, so an `arrayGet`
    /// over a `kotlin.IntArray` answers the same here as it does in the suspend lowering's spill typing.
    internal static TypeNode StaticTypeOf(JsonNode n) =>
        NodeType.Of(n, StaticTypeOf, name => BirTypeLowering.PrimArrayElem.TryGetValue(name, out var e) ? e : null);

    static int _counter;

    /// A frame-unique local name in bir2cir's own `cir$b…` namespace (spec §2.7). Minted from one process-global
    /// counter, so it is unique across every plan a compilation lowers — which is stronger than needed and cheaper
    /// than tracking frames.
    static string FreshLocal() => "cir$b" + System.Threading.Interlocked.Increment(ref _counter);

    /// Q5 of the five value questions (roster in bir-common/ValueStability.cs) — is this node an LVALUE FORMER, a
    /// shape that DESIGNATES storage without evaluating anything itself? Those are the locations whose operands can
    /// be pinned while the location stays in the slot. Anything else producing an address is a call whose invocation
    /// IS the evaluation, and has to move whole (see the address arm above).
    ///
    /// This asks about STORAGE, not about side effects, so its answer is unrelated to the other four questions and
    /// must not be reconciled with them: `arrayGet` is here because `byref(a[i])` names an element slot, even though
    /// the same kind is impure for the suspend lowering's Q3 and not droppable for Q2.
    static bool IsLvalueFormer(JsonNode node) =>
        node is JsonObject o && Str(o["k"]) is
            "local" or "this" or "field" or "staticField" or "arrayGet" or "stackGet" or "byrefLoad" or "nullableValue";

    /// May this child of an addressable location STAY where it is, or must it be pinned into a local?
    ///
    /// NOT Q2, though it once shared Q2's implementation and reads like it. Q2 asks whether an evaluation may be
    /// SKIPPED; nothing is skipped here — every operand of a location is evaluated, the only question is whether it
    /// is evaluated in the slot or one statement earlier, out of a local. What decides that is STORAGE IDENTITY: a
    /// `this`/`local`/`field`/`staticField` chain is the location's own path, and pinning a link of it would take the
    /// address of the LOCAL — a copy for a value type, so a callee writing through the `byref` would update the copy
    /// and not `a.b.c`. A `const` or `classRef` is not a location link but has nothing to pin either.
    ///
    /// Deliberately says nothing about side effects. A `field` link can throw and a `staticField` link can run a type
    /// initializer; both stay, and both then happen at the location's own position, which is where Kotlin puts them.
    static bool StaysInLocation(JsonNode? node) => node is JsonObject o && Str(o["k"]) switch
    {
        "const" or "this" or "local" or "bindRef" or "default" or "classRef"
            or "staticField" or "enumValue" => true,
        "field" => StaysInLocation(o["recv"]),
        _ => false,
    };

    /// Would [PinLocationOperands] emit anything? Asked before the emission loop, because a binding that emits
    /// pre-call statements participates in the ordering rule.
    static bool LocationHasPinWork(JsonNode location)
    {
        if (!IsLvalueFormer(location)) return true;   // the whole location moves — see the address arm
        var found = false;
        WalkOperands(location as JsonObject, child =>
        {
            if (StaysInLocation(child)) return null;
            found = true;
            return null;                              // probe only: never rewrite
        });
        return found;
    }

    /// Move every impure VALUE an addressable location is computed from into a local, in the location's own operand
    /// order, leaving a pure location expression behind.
    ///
    /// Shape-agnostic on purpose: kotc renders an address through the ordinary expression emitter, so a location is
    /// whatever the lvalue happens to be — a bare `local`, a `field` over a receiver chain, an `arrayGet` over an array
    /// and an index, a member access. The rule is the same for all of them: the NODE is the location and stays, its
    /// operand CHILDREN are values and are pinned when impure. Recursing through the links that STAY ([StaysInLocation])
    /// keeps a chain (`a.b.c[i()]`) pinning only the operands that actually carry a side effect.
    static void PinLocationOperands(JsonNode location, JsonArray into) =>
        WalkOperands(location as JsonObject, child =>
        {
            if (StaysInLocation(child)) { PinLocationOperands(child, into); return null; }
            return PinValue(child, into);
        });

    /// Visit every OPERAND node of `o` in evaluation order, replacing it with whatever `visit` returns (null = keep).
    /// A type slot (`{t:…}`) and a descriptor are not operands; only a `{k:…}` node is a value.
    static void WalkOperands(JsonObject o, Func<JsonObject, JsonNode> visit)
    {
        if (o == null) return;
        foreach (var key in o.Select(kv => kv.Key).OrderBy(SuspendLiveness.OperandRank).ToList())
        {
            if (key == "k") continue;
            switch (o[key])
            {
                case JsonObject child when Str(child["k"]) != null:
                    if (visit(child) is JsonNode rep) o[key] = rep;
                    break;
                case JsonArray arr:
                    for (var i = 0; i < arr.Count; i++)
                        if (arr[i] is JsonObject e && Str(e["k"]) != null && visit(e) is JsonNode r) arr[i] = r;
                    break;
            }
        }
    }

    /// Move one impure value into a fresh local and return the read that replaces it. The local is ALWAYS typed: an
    /// untyped local is not a lesser local, it is unverifiable IL (an `arrayGet` operand, for instance, carries
    /// neither `sty` nor `type` and is typed from its `elem`). No `kotlin.Any` fallback — that would box a value type
    /// and hide a type the CLR would refuse — so a node this cannot type is a hole in the shared deriver, and says so.
    static JsonNode PinValue(JsonObject node, JsonArray into)
    {
        var type = StaticTypeOf(node)
            ?? throw new InvalidOperationException(
                $"bir2cir: cannot type the `{Str(node["k"])}` operand a by-reference argument's location is computed " +
                "from, so the local that pins it would be untyped. Its node kind needs an arm in bir-common/NodeType.cs.");
        var name = FreshLocal();
        into.Add(new JsonObject { ["k"] = "var", ["name"] = name, ["type"] = TypeJson.Write(type), ["init"] = node.DeepClone() });
        return new JsonObject { ["k"] = "local", ["name"] = name, ["sty"] = TypeJson.Write(type) };
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

    /// One ordered walk of a reader root: counts every `bindRef`, records where each id is FIRST read, and records the
    /// ids read OFF the eager spine. Operand order comes from SuspendLiveness.KeyRank — the single statement of "which
    /// operand of a node runs first" this toolchain has — so an inlined binding's position here is the position the
    /// emitted code will evaluate it at.
    static void ScanReads(JsonNode node, Dictionary<string, int> counts, Dictionary<string, int> pos,
                          HashSet<string> deferred, ref int rank, bool eager)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "bindRef" && Str(o["id"]) is string id && counts.ContainsKey(id))
            {
                counts[id]++;
                if (!pos.ContainsKey(id)) pos[id] = rank;
                if (!eager) deferred.Add(id);
            }
            rank++;
            foreach (var kv in o.OrderBy(kv => SuspendLiveness.OperandRank(kv.Key)).ToList())
                if (kv.Value != null) ScanReads(kv.Value, counts, pos, deferred, ref rank, eager && EagerSlot(o, kv.Key));
        }
        else if (node is JsonArray a)
            foreach (var it in a) if (it != null) ScanReads(it, counts, pos, deferred, ref rank, eager);
    }

    /// The node kinds whose OPERANDS are evaluated once, in order, unconditionally, at the moment the node itself is
    /// evaluated — the "eager spine" a binding may be inlined onto. A call's own argument and receiver slots are here,
    /// as are the coercions kotc wraps a slot in (`argExpr`'s nullable unwrap / boxed-`Any` cast) and the ordinary
    /// operator shapes a materialised default is built from. Everything NOT listed defers: a `valueBlock` runs its
    /// statements first, a `cond` takes one branch, a loop repeats its body, a closure runs at some later time, and a
    /// value read there is not a value evaluated at the call. Listing the eager kinds rather than the lazy ones is
    /// deliberate — an unfamiliar kind then costs one local, not a reordered evaluation.
    ///
    /// `binOp` is eager in BOTH operands here: the short-circuit forms do not exist at this phase — kotc lowers
    /// `&amp;&amp;`/`||` to a `cond`, and the `binOp` spelling of them is minted by PrimitiveOperatorLowering, which runs
    /// after this pass.
    ///
    /// Only kinds that can REACH this pass are listed. This is the ninth pass bir2cir runs (Program.cs), so the set
    /// is what kotc emits plus what the handful of passes before it mint; the collection-literal constructions
    /// (`newList`/`newSet`/`newMap`) and the .NET property accesses (`clrPropGet`/`clrPropSet`) are NOT kotc
    /// vocabulary — MemberCallSubstitution and NetInteropBinding mint them, hundreds of lines later — so listing
    /// them here would only describe a node this pass cannot see.
    static readonly HashSet<string> EagerKinds = new(StringComparer.Ordinal)
    {
        "callStatic", "callInstance", "callInline", "objMethod", "delegateInvoke", "new", "newClr",
        "newArray", "newArraySized", "newArrayInit",
        "field", "setField", "staticField", "setStaticField", "arrayGet", "arraySet", "arrayLen",
        "binOp", "unaryOp", "conv", "cast", "isInst", "isInstRef", "objEq", "concat",
        "nullableWrap", "nullableValue", "nullableHasValue", "safeCastValue",
        "byrefOf", "byrefLoad", "stackGet", "enumOrdinal", "lateinitGet", "exprStmt",
    };

    static bool EagerSlot(JsonObject parent, string key) =>
        key != "synthClass" && Str(parent["k"]) is string k && EagerKinds.Contains(k);

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
