// bir2cir — SuspendColdLowering STAGE 0: the operand plan that puts a suspension where the state machine can
// segment it.
//
// THE PROBLEM. Kotlin evaluates an expression's operands left to right, exactly once each. A suspension inside one
// of them splits the enclosing expression in half: everything to its LEFT has already run when the coroutine
// suspends, everything to its RIGHT runs after the resume. The cold state machine emits a suspension as a LABEL, a
// state save and a resume arm — statements, not an expression — so an operand that still sits in a slot when its
// neighbour's suspension is emitted is evaluated on the wrong side of the resume. Two shapes made that fatal rather
// than merely wrong:
//
//   * a suspension inside a SUSPEND CALL's own operand list (`corAdd(x, corTick(1))`, issue #272). The outer call's
//     `label = <outer state>` is written before its operands are lowered, and the inner suspension then overwrites
//     it — the machine resumes into the inner state and loops forever.
//   * an operand that CONTAINS a suspension without BEING one (`h(f()) + g()`, issue #286). The suspension's
//     segments are appended and the residual `h(<awaited>)` is left in the slot, so it runs after the NEXT
//     operand's suspension instead of before it. The same in a string concatenation (`wrap(f()) + g()`,
//     `"…${f()}…${g()}…"`), which is where every `+` with a String on either side lands by the time this runs.
//
// THE ANSWER, in one place. Stage 0 runs over every input file after the compilation-wide type indexes are built
// and BEFORE any state machine exists. For each node the shared operand descriptor (`EvalOrderOf`) recognises, it
// finds L = the LAST operand carrying a suspension, and — when there is one — wraps the node in a call-evaluation
// plan (docs/bir-cir-spec.md §2.7) that binds every operand in array order, then lowers that plan immediately
// through `CallEvalLowering.Materialise` with the forcing rule
//
//     force[i]  =  i < L   OR   (i == L  AND  the node is itself a suspend call)
//
// A forced binding becomes a `var` STATEMENT ahead of the node; an unforced one is inlined straight back into its
// own slot, so nothing to the RIGHT of the suspension moves. The two shapes above fall out of that rule rather than
// being detected: `i < L` puts every earlier operand's evaluation before the suspension (#286), and `i == L` for a
// suspend call lifts the nested suspension out of the argument list entirely, so the label-overwrite arrangement is
// structurally unreachable (#272).
//
// WHY THE PLAN, RATHER THAN A SECOND SPILL ENGINE. The physical question — does this value become a local, and
// where is it declared — already has exactly one answer in this backend, and materialising a binding is it. Stage 0
// therefore decides only the ORDER (the `force` array); `CallEvalLowering` decides the form and `SuspendLiveness`
// decides, later and separately, whether the resulting local needs a state-machine field. Consuming
// `suspendCall:true` is consuming a frontend fact kotc stamped, and minting `cir$b…` bindings is bir2cir's own
// documented authority (§2.7 *Id namespaces*), so this is a physical-representation decision in the layer that owns
// them.
//
// WHAT THIS RETIRED. A purity predicate used to decide which earlier operands were spilled: an operand free of
// calls/allocations/assignments/control transfers was judged stable to read after the resume and left inline. Every
// argument for widening that set — a raw `field`/`staticField`/`lateinitGet` read the callee can mutate, an
// `arrayGet` over an array the callee can reach, and the gap that never closed (`clrPropGet`, `delegateInvoke`,
// `objMethod`, `constrainedCall`) — is answered here by POSITION instead of by kind: an operand left of a suspension
// is evaluated left of it, whatever it is made of. The only exemption left is Q1 re-readability
// (`ValueStability.IsReReadable`: a literal, `this`, a plan read), where re-reading can observe neither a side
// effect nor a different value.
//
// THE TERMINAL RULE. An operand whose evaluation never completes — an expression-position `throw`/`return`, the
// `valueBlock` a spliced `run { throw … }` becomes, a `Nothing`-returning call — is not a value to bind, and
// everything to its right is unreachable. When such an operand sits at index t with a suspension to its right
// (L > t), the node becomes `valueBlock{stmts: <the forced bindings 0..t-1>, result: ops[t]}`, typed as the node
// was: the enclosing expression's value IS the terminal operand, and the node and every operand right of t are
// dropped. Reached at stage 0 there is no state machine yet, so there is nothing to refuse — the same arrangement
// inside a suspend call's argument list used to be a compile-time refusal, because the cold-call builder was
// already half-way through assembling a suspension point it then had no way to elide.
//
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class SuspendColdLowering
{
    // --- the operand descriptor -----------------------------------------------------------------------
    //
    // ONE description of "what does this node evaluate, in what order, and how is it reassembled", so no consumer
    // restates a part of it. Stage 0 asks it which nodes it plans and in what order; `ColdCall` asks it for the
    // receiver/argument split of the call it is rewriting; `SuspendedCalleeIn` asks it which operand a diagnostic
    // should name first.

    // One value-bearing slot of an ordered-eval node. A slot is either a direct property (`array`), an element of a
    // value list (`elems[2]`), or a member of an element object (`entries[1].value`). Keeping the slot beside the
    // operand makes extraction and reassembly the SAME description: widening the kind roster cannot add an operand
    // to the planner while forgetting where its replacement belongs.
    internal readonly record struct EvalOrderSlot(string Key, int Index = -1, string Member = null)
    {
        public JsonNode Read(JsonObject o)
        {
            if (Index < 0) return o[Key];
            if (o[Key] is not JsonArray a || Index >= a.Count) return null;
            if (Member == null) return a[Index];
            return a[Index] is JsonObject item ? item[Member] : null;
        }

        public void Write(JsonObject o, JsonNode value)
        {
            if (Index < 0) { o[Key] = value; return; }
            var a = o[Key] as JsonArray
                ?? throw new InvalidOperationException($"ordered operand list `{Key}` disappeared while rebuilding a node");
            if (Member == null) { a[Index] = value; return; }
            var item = a[Index] as JsonObject
                ?? throw new InvalidOperationException($"ordered operand container `{Key}[{Index}]` disappeared while rebuilding a node");
            item[Member] = value;
        }
    }

    // A node's operands in EVALUATION ORDER, the exact slots they came from, and whether the first is a dispatch
    // receiver. `ArgumentStart` remains the call-cold-lowering split; non-call descriptors use it only for the
    // diagnostic phrase "receiver" versus "argument N".
    internal readonly record struct EvalOrder(List<JsonNode> Operands, List<EvalOrderSlot> Slots, bool HasReceiver)
    {
        public int ArgumentStart => HasReceiver ? 1 : 0;
    }

    // The call/new kinds whose operands this descriptor names — ONE list, whether or not the node is itself a
    // suspend call. It was two, and `clrGenericInstance` appeared in only one of them: a generic cross-assembly
    // callee was ordered against a suspension when it WAS the suspension and not when it merely contained one.
    static readonly HashSet<string> OrderedEvalKinds = new(StringComparer.Ordinal)
    {
        "callStatic", "callInstance", "clrStatic", "clrInstance", "clrGenericStatic", "clrGenericInstance",
        "new", "newClr",
    };

    // The subset that carries a DISPATCH RECEIVER as its first operand. (`ColdCall` passes a receiver for exactly
    // these; the suspend-VALUE invoke, which always has one, is a `callInstance`, so the same test covers it.)
    static readonly HashSet<string> InstanceCallKinds = new(StringComparer.Ordinal)
        { "callInstance", "clrInstance", "clrGenericInstance" };

    // The operand description of a node whose evaluation order this pass owns — or null when it has none (an
    // `.await()` marker is reconstructed by its own emitter, which reads its operand directly; every other node kind
    // is copied operand-by-operand in key-rank order. A suspendCoroutine intrinsic participates like any other suspend
    // call: its block can be an arbitrary function-valued expression and therefore can itself carry a suspension.
    internal static EvalOrder? EvalOrderOf(JsonObject o)
    {
        var k = Str(o["k"]);
        if (k is "binOp" or "objEq") return Direct(o, hasReceiver: false, "lhs", "rhs");
        // A string concatenation evaluates its parts left to right exactly like any other operand list, and it is
        // where `a + b` LANDS whenever either side is a String: PrimitiveOperatorLowering re-emits
        // `kotlin.String.plus` as a `concat`, and a `"…$a…$b…"` template is one from the start. Both run before this
        // pass, so `f().toString() + g()` is a `concat` here, not a `callInstance` — the sibling spelling of the
        // reorder that made the binary form wrong.
        if (k == "concat") return List(o, "parts");

        if (k != null && OrderedEvalKinds.Contains(k))
        {
            if (Bool(o["suspendCall"]))
            {
                if (IsAwaitMarkerCall(o)) return null;
                return Call(o, InstanceCallKinds.Contains(k));
            }
            return Call(o, o["recv"] != null);
        }

        return k switch
        {
            "arrayGet" => Direct(o, false, "array", "index"),
            "arraySet" => Direct(o, false, "array", "index", "value"),
            "setField" or "setFieldExpr" => ReceiverThen(o, "value"),
            "clrPropSet" => ReceiverThen(o, "value"),
            "delegateInvoke" => Call(o, hasReceiver: true),
            "objMethod" => o["arg"] != null
                ? Direct(o, true, "recv", "arg")
                : Direct(o, true, "recv"),
            "constrainedCall" => o["args"] is JsonArray
                ? Call(o, hasReceiver: true)
                : Direct(o, true, "recv", "arg"),
            "newArray" or "newList" or "newSet" => List(o, "elems"),
            "newArraySized" => Direct(o, false, "size"),
            "newArrayInit" => Direct(o, false, "size", "init"),
            "spreadConcat" => ListMember(o, "parts", "e"),
            "newMap" => MapEntries(o),
            _ => null,
        };
    }

    static EvalOrder FromSlots(JsonObject o, bool hasReceiver, List<EvalOrderSlot> slots) =>
        new(slots.Select(s => s.Read(o)).ToList(), slots, hasReceiver);

    static EvalOrder Direct(JsonObject o, bool hasReceiver, params string[] keys) =>
        FromSlots(o, hasReceiver, keys.Select(k => new EvalOrderSlot(k)).ToList());

    static EvalOrder ReceiverThen(JsonObject o, params string[] keys)
    {
        var hasReceiver = o["recv"] != null;
        var slots = new List<EvalOrderSlot>();
        if (hasReceiver) slots.Add(new EvalOrderSlot("recv"));
        slots.AddRange(keys.Select(k => new EvalOrderSlot(k)));
        return FromSlots(o, hasReceiver, slots);
    }

    static EvalOrder List(JsonObject o, string key)
    {
        var count = (o[key] as JsonArray)?.Count ?? 0;
        return FromSlots(o, false, Enumerable.Range(0, count).Select(i => new EvalOrderSlot(key, i)).ToList());
    }

    static EvalOrder ListMember(JsonObject o, string key, string member)
    {
        var count = (o[key] as JsonArray)?.Count ?? 0;
        return FromSlots(o, false,
            Enumerable.Range(0, count).Select(i => new EvalOrderSlot(key, i, member)).ToList());
    }

    static EvalOrder MapEntries(JsonObject o)
    {
        var slots = new List<EvalOrderSlot>();
        if (o["entries"] is JsonArray entries)
            for (var i = 0; i < entries.Count; i++)
            {
                slots.Add(new EvalOrderSlot("entries", i, "key"));
                slots.Add(new EvalOrderSlot("entries", i, "value"));
            }
        return FromSlots(o, false, slots);
    }

    static EvalOrder Call(JsonObject o, bool hasReceiver)
    {
        var slots = new List<EvalOrderSlot>();
        if (hasReceiver) slots.Add(new EvalOrderSlot("recv"));
        if (o["args"] is JsonArray args)
            for (var i = 0; i < args.Count; i++) slots.Add(new EvalOrderSlot("args", i));
        return FromSlots(o, hasReceiver, slots);
    }

    // The position of the LAST operand that carries a suspension; -1 when none does. Everything to its left is
    // evaluated before that suspension, and everything to its right after the resume.
    static int LastSuspending(IReadOnlyList<JsonNode> kids)
    {
        var last = -1;
        for (var i = 0; i < kids.Count; i++)
            if (kids[i] != null && HasOwnSuspension(kids[i])) last = i;
        return last;
    }

    /// Rebuild `o` with `ops` in the exact slots the descriptor read. The slots, rather than a second kind/shape
    /// switch, are the reassembly contract; map key/value streams and construction value lists therefore use the
    /// same path as calls and direct named operands.
    static JsonObject Reassemble(JsonObject o, EvalOrder order, IReadOnlyList<JsonNode> ops)
    {
        var copy = o.DeepClone() as JsonObject
            ?? throw new InvalidOperationException("ordered expression did not clone as an object");
        if (ops.Count != order.Slots.Count)
            throw new InvalidOperationException("ordered operand count changed while rebuilding a node");
        for (var i = 0; i < ops.Count; i++) order.Slots[i].Write(copy, ops[i]);
        return copy;
    }

    // --- the frame's declared locals -------------------------------------------------------------------

    /// What a frame's scope records about one local: the DECLARED type (the only thing that types a `{k:local}`
    /// read, which carries a name and nothing else) and, when the declaration is itself a call-evaluation plan
    /// binding, the SOURCE ROLE a storage refusal names the value by. Carrying the role matters because a plan can
    /// bind a READ of a value an earlier plan already materialised: the value is the same value, so the phrase that
    /// names it must not be replaced by the outer node's generic one.
    internal readonly record struct LocalDecl(TypeNode Type, string Role);

    // --- the compilation-wide expression typer ---------------------------------------------------------

    /// The static type of an expression, for typing a plan binding's local. The shared node-local deriver
    /// (bir-common/NodeType.cs) answers most of it; this adds the arms it cannot — the two only an INDEX of the
    /// compilation can answer (a call carrying no `sty`, a raw member/static field read) and the one only a lexical
    /// SCOPE can (a `local` read, whose type lives on the `var` or parameter that declares it, not on the read). So
    /// a binding is typed the same way here as everywhere else that mints a local.
    ///
    /// Returns NULL when the type is unknown. A local is not a lesser local for being untyped, it is unverifiable
    /// IL, and `kotlin.Any` is not a fallback: it boxes a value type and hides a type the CLR would refuse as a
    /// state-machine field. So the caller reports the drop rather than substituting one.
    internal sealed class ExprTyper
    {
        readonly IReadOnlyDictionary<string, TypeNode> _methodRets;
        readonly IReadOnlyDictionary<string, TypeNode> _fieldTypes;

        internal ExprTyper(IReadOnlyDictionary<string, TypeNode> methodRets, IReadOnlyDictionary<string, TypeNode> fieldTypes)
        {
            _methodRets = methodRets;
            _fieldTypes = fieldTypes;
        }

        /// <param name="locals">The enclosing frame's declared locals and parameters (name -> type), threaded by
        /// the walk. A frame's local names are unique within it — ilemit keeps ONE `_locals` scope per method — so
        /// a flat map is the whole lexical answer, not an approximation of one.</param>
        internal TypeNode Of(JsonNode n, IReadOnlyDictionary<string, LocalDecl> locals)
        {
            if (n is not JsonObject o) return null;
            // A `local` FIRST: the read carries a name and (usually) nothing else, and what types it is the `var` or
            // parameter that DECLARES it — a statement of the enclosing frame, not part of this node. That is the one
            // question the node-local core is defined not to answer (bir-common/NodeType.cs `case "this"` has the
            // same shape of answer: only the owner knows).
            if (Str(o["k"]) == "local" && Str(o["name"]) is string ln && locals.TryGetValue(ln, out var ld)) return ld.Type;
            // The node-local answer next (an explicit `ret`/`dynRet`/`sty`, then whatever slot the kind carries its
            // own result type in). `Of` is passed as the recursion so an operand of a `binOp` still resolves through
            // the scope and index arms rather than falling back to the index-less core.
            if (NodeType.Of(o, x => Of(x, locals), name => BirTypeLowering.PrimArrayElem.TryGetValue(name, out var pe) ? pe : null)
                is TypeNode nodeLocal) return nodeLocal;
            switch (Str(o["k"]))
            {
                case "callStatic":
                {
                    // #199 — `sty` (read by the core above) is the PRIMARY source and needs no owner disambiguation.
                    // This bare-name index is the fallback for the rare synthesized node without one, so it cannot
                    // distinguish two same-simple-name funs across packages.
                    var name = Str(o["method"]);
                    var owner = TypeJson.OwnerName(o["owner"]);
                    var key = (owner == null ? "#" : owner + "#") + name;
                    if (_methodRets.TryGetValue(key, out var rt)) return rt;
                    if (_methodRets.TryGetValue("#" + name, out var rt2)) return rt2;
                    break;
                }
                case "callInstance":
                {
                    var ot = TypeJson.OwnerName(o["ownerType"]);
                    if (ot != null && _methodRets.TryGetValue(ot + "#" + Str(o["method"]), out var rt)) return rt;
                    break;
                }
                case "field": case "staticField": case "lateinitGet":
                {
                    // A raw field read carries no result type of its own; the DECLARED type is the answer.
                    // Owner-qualified first (`owner#name`), then top-level (`#name`).
                    var fname = Str(o["name"]);
                    var fowner = TypeJson.OwnerName(o["ownerType"]);
                    if (fowner != null && _fieldTypes.TryGetValue(fowner + "#" + fname, out var fft)) return fft;
                    if (_fieldTypes.TryGetValue("#" + fname, out var fft2)) return fft2;
                    break;
                }
            }
            return null;
        }
    }

    // --- stage 0 ---------------------------------------------------------------------------------------

    /// Plan every suspension-bearing operand in the compilation. POST-ORDER, so an inner node is already in its
    /// final shape when the node around it binds it.
    static void PlanSuspensionBearingOperands(IReadOnlyList<JsonNode> roots, ExprTyper typer)
    {
        // The names of the locals the plans MATERIALISED. The chokepoint below needs them: after this walk, an
        // operand left of a suspension is either a Q1 re-readable value the plan exempted or a READ of one of
        // these — and "a read of one of these" is the property being asserted, not "some local", which would let
        // an operand the plan failed to bind pass for a bound one.
        var materialised = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in roots)
            PlanNode(r, typer, materialised, new Dictionary<string, LocalDecl>(StringComparer.Ordinal),
                     Str((r as JsonObject)?["fileClass"]) ?? "?");
        AssertOperandsPlanned(roots, materialised);
    }

    /// Walk one subtree, planning each node's operands POST-ORDER — so an inner node is already in its final shape
    /// when the node around it binds it — while threading the LOCAL SCOPE a `local` operand is typed from.
    ///
    /// The scope is a flat name -> type map per FRAME, which is exactly how the frame's storage works: ilemit keeps
    /// one `_locals` scope per method, so a name means one thing throughout it. A declaration (a method, a
    /// constructor) opens a fresh map seeded with its parameters; a lambda/closure node opens a COPY of the
    /// enclosing one, seeded with its own, so what it declares cannot leak back out onto a same-named local of the
    /// frame that contains it. Every `var`, catch variable and loop variable registers itself as it is reached, and
    /// a body is walked in statement order, so a read always sees the declaration that precedes it.
    static void PlanNode(JsonNode node, ExprTyper typer, HashSet<string> materialised,
                         Dictionary<string, LocalDecl> locals, string where)
    {
        switch (node)
        {
            case JsonObject o:
                var frame = OpensFrame(o);
                var scope = frame ? NewFrame(o, locals) : locals;
                // The declaration a refusal names, updated at each frame — so it says WHICH function the operand it
                // could not type is in, not only which node kind it was.
                var site = frame && Str(o["name"]) is string dn ? dn : where;
                Declare(o, scope);
                foreach (var kv in o.ToList()) if (kv.Value != null) PlanNode(kv.Value, typer, materialised, scope, site);
                PlanOperandsOf(o, typer, materialised, scope, site);
                return;
            case JsonArray a:
                foreach (var it in a.ToList()) if (it != null) PlanNode(it, typer, materialised, locals, where);
                return;
        }
    }

    /// Does this object begin a new local FRAME? A method/constructor declaration (no `k`, but parameters), or a
    /// lambda/closure VALUE whose body is another state machine's or another frame's scope.
    static bool OpensFrame(JsonObject o) =>
        (Str(o["k"]) is null && o["params"] is JsonArray)
        || (Str(o["k"]) is string k && LambdaKinds.Contains(k));

    /// A frame's initial scope: empty for a declaration, a COPY of the enclosing frame for a lambda (its captures
    /// read under the same names), plus this frame's own parameters.
    static Dictionary<string, LocalDecl> NewFrame(JsonObject o, Dictionary<string, LocalDecl> outer)
    {
        var scope = Str(o["k"]) is null
            ? new Dictionary<string, LocalDecl>(StringComparer.Ordinal)
            : new Dictionary<string, LocalDecl>(outer, StringComparer.Ordinal);
        if (o["params"] is JsonArray ps)
            foreach (var p in ps)
                if (p is JsonObject po && Str(po["name"]) is string pn && TypeJson.Read(po["type"]) is TypeNode pt)
                    scope[pn] = new LocalDecl(pt, null);
        return scope;
    }

    /// Register whatever names this node DECLARES into the current frame: a `var`, a `try`'s catch variables, and a
    /// loop's element variable. Each carries its type in a different slot, and all three are read back as a plain
    /// `{k:local}` that has no type of its own.
    static void Declare(JsonObject o, Dictionary<string, LocalDecl> scope)
    {
        switch (Str(o["k"]))
        {
            case "var":
                // A plan-materialised binding carries the SOURCE ROLE a storage refusal names it by; record it, so
                // a later plan that binds a READ of this local inherits it instead of replacing it with its own.
                if (Str(o["name"]) is string vn && TypeJson.Read(o["type"]) is TypeNode vt)
                    scope[vn] = new LocalDecl(vt, Str(o["role"]));
                return;
            case "try":
                if (o["catches"] is JsonArray cs)
                    foreach (var c in cs)
                        if (c is JsonObject co && Str(co["var"]) is string cn && TypeJson.Read(co["excType"]) is TypeNode ct)
                            scope[cn] = new LocalDecl(ct, null);
                return;
            case "forArray": case "forRange": case "forEachInline": case "forIn": case "for":
                if (Str(o["var"]) is string fn && TypeJson.Read(o["elem"]) is TypeNode ft)
                    scope[fn] = new LocalDecl(ft, null);
                return;
        }
    }

    /// Wrap ONE node's operands in a plan and lower it, in place. Leaves the node byte-untouched unless the plan
    /// actually materialises something — a node with no suspension in an operand, and a node whose only forced
    /// operands are re-readable, both emit exactly the CIR they emitted before this pass existed.
    static void PlanOperandsOf(JsonObject o, ExprTyper typer, HashSet<string> materialised,
                               Dictionary<string, LocalDecl> locals, string where)
    {
        if (EvalOrderOf(o) is not { } order) return;
        var ops = order.Operands;
        var last = LastSuspending(ops);
        if (last < 0) return;

        // A TERMINAL operand LEFT of the suspension truncates the node: everything from it rightwards is
        // unreachable, and it is the enclosing expression's value. With the suspension to its LEFT instead
        // (`f(later(), throw x)`) nothing is truncated — that suspension completes and resumes normally, and the
        // terminal operand then leaves through the call that was going to consume it.
        var terminalAt = -1;
        for (var i = 0; i < ops.Count && terminalAt < 0; i++)
            if (ops[i] != null && NodeType.IsNothing(typer.Of(ops[i], locals))) terminalAt = i;
        if (terminalAt >= 0 && last <= terminalAt) terminalAt = -1;

        var n = terminalAt >= 0 ? terminalAt : ops.Count;
        // An ABSENT operand slot (a `binOp` missing a side) is a malformed node, not an ordering question — it has
        // no value to bind. Leave it alone and let IrSanity's structural check name it.
        for (var i = 0; i < n; i++) if (ops[i] == null) return;
        var force = new bool[n];
        var anyForced = false;
        for (var i = 0; i < n; i++)
        {
            force[i] = i < last || (i == last && Bool(o["suspendCall"]));
            anyForced |= force[i];
        }
        if (terminalAt < 0 && !anyForced) return;

        var role = OperandRole(o);
        var bindings = new JsonArray();
        var reads = new List<JsonNode>(n);
        for (var i = 0; i < n; i++)
        {
            var expr = ops[i];
            var id = CallEvalLowering.FreshBindingId();
            var stable = ValueStability.IsReReadable(expr);
            var type = typer.Of(expr, locals);
            if (type == null && force[i] && !stable)
                throw new NotSupportedException(
                    $"bir2cir: suspend-lowering: in `{where}`, the `{Str((expr as JsonObject)?["k"]) ?? "?"}` "
                    + $"{(order.HasReceiver && i == 0 ? "receiver" : "argument " + i)} of an expression is evaluated "
                    + "before a suspending operand and carries no static type, so the local that holds its value "
                    + "across the suspension would be untyped — an earlier lowering dropped the operand's type, or "
                    + "its node kind needs an arm in bir-common/NodeType.cs.");
            var binding = new JsonObject
            {
                ["id"] = id,
                // Documentation and diagnostics only: the array order already carries the evaluation order.
                ["phase"] = order.HasReceiver && i == 0 ? "recv" : "arg",
                ["kind"] = "value",
                ["stable"] = stable,
                // The role a storage refusal names this value by — INHERITED when the operand is a read of a value
                // an earlier plan already materialised. It is the same value, so it keeps the phrase kotc's plan gave
                // it ("the argument 's' of `cfbLen`") rather than being renamed after the node that reads it.
                ["role"] = InheritedRole(expr, locals) ?? role,
                ["expr"] = expr?.DeepClone(),
            };
            if (type != null) binding["type"] = TypeJson.Write(type);
            bindings.Add(binding);
            var read = new JsonObject { ["k"] = "bindRef", ["id"] = id };
            if (type != null) read["sty"] = TypeJson.Write(type);
            reads.Add(read);
        }

        // The reader: the node with every operand replaced by its plan read — or, when the node is truncated, the
        // terminal operand alone, which reads nothing the plan bound.
        var reader = terminalAt >= 0 ? ops[terminalAt].DeepClone() : Reassemble(o, order, reads);
        var (stmts, repl) = CallEvalLowering.Materialise(bindings, new List<JsonNode> { reader }, role, force);
        if (terminalAt < 0 && stmts.Count == 0) return;    // every forced binding was re-readable — nothing moves
        foreach (var st in stmts)
            if (st is JsonObject sv && Str(sv["k"]) == "var" && Str(sv["name"]) is string vn) materialised.Add(vn);
        var lowered = CallEvalLowering.Substitute(reader, repl);

        // The block's value is what the node's value was: the truncated node's declared result type, or the
        // lowered node's own. Rewritten IN PLACE, because the node's parent holds this object.
        var type0 = typer.Of(o, locals);
        o.Clear();
        o["k"] = "valueBlock";
        if (type0 != null) o["type"] = TypeJson.Write(type0);
        o["stmts"] = stmts;
        o["result"] = lowered;
    }

    /// The role of the value this operand READS, when it reads a local an earlier plan materialised — so a refusal
    /// keeps naming the value in the words its own producer chose. Null when the operand is not such a read.
    static string InheritedRole(JsonNode expr, IReadOnlyDictionary<string, LocalDecl> locals) =>
        expr is JsonObject o && Str(o["k"]) == "local" && Str(o["name"]) is string n
        && locals.TryGetValue(n, out var d) ? d.Role : null;

    /// The source-level phrase a storage refusal names a bound operand by — it travels onto the materialised local,
    /// so a refusal says "the operand of the call to `corAdd`" rather than the minted `cir$b7`.
    static string OperandRole(JsonObject o) =>
        Str(o["method"]) is string m ? $"operand of the call to `{m}`"
        : Str(o["op"]) is string op ? $"operand of the `{op}` operator"
        : Str(o["k"]) == "concat" ? "operand of a string concatenation"
        : TypeJson.OwnerName(o["type"]) is string t ? $"operand of the construction of `{t}`"
        : "operand of an expression";

    // --- the chokepoint --------------------------------------------------------------------------------

    /// P1: stage 0 re-walks its own output and refuses the two arrangements it exists to remove. Same discipline as
    /// `CallEvalLowering.AssertLowered` — the pass proves its post-condition rather than leaving it to a fixture.
    ///
    ///   * a SUSPEND CALL with any suspension-bearing operand (the #272 label overwrite), and
    ///   * any descriptor-bearing node with a non-re-readable operand LEFT of a suspension-bearing one (#286 and
    ///     the whole reorder family).
    ///
    /// SCOPE — the bodies that survive to become IL, which at this phase is every constructor, every
    /// static-initializer group, and every method except a `mods.suspend` one this pass will NOT rewrite into a state
    /// machine. That body is DISCARDED: SuspendResidueLowering replaces the residual declaration's body with a
    /// call-time throw (the rt-stdlib's `suspendCoroutine`/`suspendCoroutineUninterceptedOrReturn` primitives are
    /// exactly that case), so an unplanned operand inside it can never reach codegen. A suspend method this pass DOES
    /// transform is checked: its body becomes the state machine's `invokeSuspend`, which is emitted.
    static void AssertOperandsPlanned(IReadOnlyList<JsonNode> roots, HashSet<string> materialised)
    {
        foreach (var r in roots)
        {
            if (r is not JsonObject file) continue;
            var fileClass = Str(file["fileClass"]) ?? "Kt";
            CheckMembers(file, fileClass, topLevel: true);
            if (file["types"] is JsonArray types)
                foreach (var t in types)
                    if (t is JsonObject to)
                        CheckMembers(to, Str(to["name"]) ?? fileClass, topLevel: false);
        }

        void CheckMembers(JsonObject container, string owner, bool topLevel)
        {
            if (container["methods"] is JsonArray ms)
                foreach (var m in ms)
                    if (m is JsonObject mo && (!Mod(mo, "suspend")
                                               || (topLevel ? IsColdCandidate(mo) : IsMemberColdCandidate(mo))))
                        Check(mo["body"], owner + "." + (Str(mo["name"]) ?? "?"));
            if (container["ctors"] is JsonArray cs)
                foreach (var c in cs)
                    if (c is JsonObject co)
                        foreach (var key in new[] { "body", "preStmts", "thisArgs", "baseArgs" })
                            Check(co[key], owner + "..ctor");
            if (container["fields"] is JsonArray fs)
                foreach (var f in fs)
                    if (f is JsonObject fo && Bool(fo["static"]))
                        Check(fo["init"], owner + "..cctor");
        }

        void Check(JsonNode node, string where)
        {
            switch (node)
            {
                case JsonObject o:
                    if (EvalOrderOf(o) is { } order)
                    {
                        var last = LastSuspending(order.Operands);
                        if (last >= 0 && Bool(o["suspendCall"]))
                            throw new InvalidOperationException(
                                $"bir2cir: suspend-lowering: in `{where}`, the suspending call to "
                                + $"`{Str(o["method"]) ?? "?"}` still carries a suspension in its own operand list "
                                + "after the operand plans were materialised. The state machine would write the "
                                + "outer resume label and then have the inner suspension overwrite it.");
                        for (var i = 0; i < last; i++)
                            if (order.Operands[i] != null && !Settled(order.Operands[i]))
                                throw new InvalidOperationException(
                                    $"bir2cir: suspend-lowering: in `{where}`, operand {i} of a "
                                    + $"`{Str(o["k"])}` node is neither re-readable nor materialised, yet a later "
                                    + "operand carries a suspension — it would be evaluated after that suspension "
                                    + "resumes instead of before it.");
                    }
                    foreach (var kv in o) if (kv.Value != null) Check(kv.Value, where);
                    return;
                case JsonArray a:
                    foreach (var it in a) if (it != null) Check(it, where);
                    return;
            }
        }

        // What an operand left of a suspension is allowed to be once the plans are materialised: a Q1 re-readable
        // value the plan exempted (a literal, `this`), or a READ of a local one of the plans declared — the value
        // itself having been evaluated by the `var` statement the plan emitted ahead of the node. Membership, not
        // the `local` KIND: an operand that was already a local before this pass ran and was NOT bound would
        // otherwise pass for a bound one, which is exactly the miss this chokepoint exists to catch.
        bool Settled(JsonNode n) =>
            ValueStability.IsReReadable(n)
            || (n is JsonObject o && Str(o["k"]) == "local" && materialised.Contains(Str(o["name"]) ?? ""));
    }
}
