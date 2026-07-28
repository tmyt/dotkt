// bir2cir — SuspendLiveness: which locals of a suspend function body must survive a suspension?
//
// A cold state machine re-enters `invokeSuspend` once per resume, so a MoveNext LOCAL does not survive a
// suspension: only an SM instance FIELD does. The question "which locals must become fields" is exactly
// classical LIVENESS at each suspension point — a variable needs a field iff it is LIVE-OUT at some
// suspension (some path from that suspension reaches a read of it before a write).
//
// The analysis is precise, not an interval approximation. "Declared before a suspension and read after it"
// (a lexical interval) is NOT the criterion: a variable defined and consumed entirely within each iteration
// of a loop that also suspends is dead across that suspension and stays a local — which is what makes a
// byref-like (`ref struct`) value legal there, exactly as C# accepts it. The only permitted imprecision is
// in the direction of MORE fields (a spurious field is wasteful, never wrong); a variable wrongly reported
// dead would miscompile, so every approximation below is chosen to add liveness, never remove it.
//
// Shape: an evaluation-order walk of the NORMALIZED body (post FlattenSuspendingLoops/HoistSuspendingCatches,
// so a suspending loop is already flat `label`/`goto`/`brIf` CFG) turns the tree into a flat EVENT list —
// use / def / susp / label / goto / brIf / stop — then a standard round-robin backward liveness runs over the
// CFG those events induce. Structured control flow that survives normalization (`cond`/`if` expressions,
// non-suspending structured loops, `try`) is expanded into the same event vocabulary with SYNTHETIC labels
// (allocated NEGATIVE so they can never collide with a kotc/bir2cir label id).
//
// Exceptional edges: every event inside a protected region gets each catch clause entry AND the try's exit
// (= where the finally body starts) as additional successors, so anything a handler or finally reads is live
// throughout the region. Nested lambda/closure VALUES are opaque — their bodies are their own frame — but
// their CAPTURE values are evaluated here, so those are walked.
//
// Consumed by SuspendColdLowering.FunGen.Build: the sole input to the FieldStorage gate's "does it live
// across a suspension" question.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

static class SuspendLiveness
{
    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();
    static bool Bool(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    // Both halves of the emitter's subtree rule are taken STRAIGHT from it — this analysis owns no copy, so a new
    // kind placed in one of SuspendColdLowering's two disjoint halves reaches both sides in the same edit and
    // cannot land in the wrong one here.

    // The kinds whose SUSPENSIONS belong to ANOTHER state machine: skipped, because treating one as ours would
    // attribute a suspension to the wrong frame.
    static readonly HashSet<string> OtherFrameKinds = SuspendColdLowering.OtherFrameBaseKinds;

    // The loops the EMITTER treats as a separate scope for its own-suspension question, while their bodies still
    // run in THIS frame.
    static readonly HashSet<string> InlineLoopKinds = SuspendColdLowering.InlineLoopKinds;

    // A lambda/closure VALUE: only its capture VALUES are evaluated in THIS frame, so the walk visits those and
    // nothing else. That is the other-frame set plus `newSam`, whose suspensions are ours but whose remaining
    // shape is still a capture list — ClosureSynthesis has lifted its `synthClass` out by the time this pass runs.
    static readonly HashSet<string> ClosureValueKinds =
        new(OtherFrameKinds.Append("newSam"), StringComparer.Ordinal);

    // Structured loops that survive normalization (a loop whose body spans a suspension was already flattened).
    // The inline loops are part of it by construction rather than by a second listing of their names.
    static readonly HashSet<string> LoopKinds =
        new(new[] { "for", "forArray", "forRange", "while", "dowhile" }.Concat(InlineLoopKinds), StringComparer.Ordinal);

    static SuspendLiveness()
    {
        // The sets are references into SuspendColdLowering (whose own constructor asserts the two halves are
        // disjoint), so the only failure left is a static-initialization order mistake that would silently make
        // every lambda body transparent to the analysis.
        if (OtherFrameKinds.Count == 0 || InlineLoopKinds.Count == 0
            || !ClosureValueKinds.Contains("newSam") || LoopKinds.Count == 0)
            throw new InvalidOperationException(
                "bir2cir: suspend-lowering: the shared subtree-skipping sets came out empty — a static "
                + "initialization order change would silently make every lambda body transparent to the analysis.");
    }

    // Keys whose value is a STATEMENT list (elements are statements, not operand expressions).
    static readonly HashSet<string> StmtListKeys = new(StringComparer.Ordinal)
        { "body", "stmts", "finally", "then", "else" };

    // Keys of a loop node that are NOT its body/binding (its header operands). Walked inside the loop head so a
    // re-evaluated bound stays live around the back edge.
    static readonly HashSet<string> LoopStructuralKeys = new(StringComparer.Ordinal)
        { "k", "body", "var", "label", "elem", "type" };

    // Evaluation-order rank of an operand key, for the generic ordered walk. Lower runs first. Keys absent from
    // the table run last, in insertion order. The ranks encode the operand order of the CIR node shapes ilemit
    // emits (receiver, then the indexed/sized operand, then the value, then the argument list).
    static readonly Dictionary<string, int> KeyRank = new(StringComparer.Ordinal)
    {
        ["recv"] = 1,
        ["array"] = 2, ["src"] = 2, ["target"] = 2, ["ptr"] = 2,
        ["lhs"] = 3,
        ["index"] = 4, ["len"] = 4,
        ["from"] = 5, ["to"] = 5, ["step"] = 5, ["size"] = 5, ["count"] = 5,
        ["e"] = 6, ["key"] = 6, ["inner"] = 6,
        ["value"] = 7,
        ["rhs"] = 8,
        ["args"] = 9, ["elems"] = 9, ["parts"] = 9, ["captures"] = 9, ["capValues"] = 9,
        ["init"] = 10,
        ["stmts"] = 11, ["result"] = 12,
    };

    // Keys that never carry an evaluated operand (type/dispatch/signature metadata). Skipping them keeps the
    // walk cheap; none of them can contain a `{k:local}`.
    static readonly HashSet<string> NonOperandKeys = new(StringComparer.Ordinal)
    {
        "k", "type", "ret", "sty", "dynRet", "owner", "ownerType", "sig", "argTypes", "memberSig",
        "clrOverrideSig", "typeArgs", "shapeTypes", "elem", "excType", "closureType", "funcType",
        "calleeOwner", "method", "name", "op", "member", "accessor", "dispatch", "pos", "mods",
        "keyType", "valType", "local",
    };

    // The node kinds that reference a local through a bare STRING property instead of a `{k:"local"}` node.
    // `local`/`setLocal`/`byrefLoad`/`byrefStore` is the complete set (IrSanity.CheckRefs is the same list); the
    // two byref forms name the managed-pointer local in `local`, which no generic walk would ever see.
    static readonly HashSet<string> ByRefSlotKinds = new(StringComparer.Ordinal) { "byrefLoad", "byrefStore" };

    enum EvKind { Use, Def, Susp, Label, Goto, BrIf, Stop }

    readonly struct Ev
    {
        public readonly EvKind Kind;
        public readonly string Name;     // Use/Def: the variable; Susp: the callee description
        public readonly int Label;       // Label/Goto/BrIf: the target id
        public Ev(EvKind kind, string name, int label) { Kind = kind; Name = name; Label = label; }
    }

    /// <summary>
    /// The verdict for one suspend function body: the `{k:var}` declarations it owns (in walk order, the order
    /// their SM fields are minted in) and, for each, whether it lives across a suspension and which suspending
    /// callee it first lives across.
    /// </summary>
    public sealed class Result
    {
        readonly Dictionary<string, string> _across;   // var name -> first suspending callee it lives across
        public IReadOnlyList<(string Name, JsonNode Type)> DeclaredVars { get; }

        internal Result(List<(string, JsonNode)> declared, Dictionary<string, string> across)
        {
            DeclaredVars = declared;
            _across = across;
        }

        /// <summary>Does this variable have to be an SM field (is it live at some suspension point)?</summary>
        public bool LivesAcrossSuspension(string name) => name != null && _across.ContainsKey(name);

        /// <summary>The first suspending callee `name` lives across, for the diagnostic; null when it does not.</summary>
        public string FirstSuspensionAcross(string name) =>
            name != null && _across.TryGetValue(name, out var m) ? m : null;
    }

    /// <summary>
    /// Analyze a normalized suspend body. `body` is the statement list FunGen.Build is about to segment.
    /// </summary>
    public static Result Analyze(JsonArray body)
    {
        var b = new Builder();
        b.CollectDeclarations(body);
        b.Stmts(body);
        return b.Solve();
    }

    sealed class Builder
    {
        readonly List<Ev> _ev = new();
        readonly Dictionary<int, List<int>> _extraSucc = new();     // event index -> extra successor label ids
        readonly List<int> _handlers = new();                       // active handler/exit label ids (innermost last)
        readonly List<(string Label, int Cont, int End)> _loops = new();
        readonly List<(string Name, JsonNode Type)> _declared = new();
        readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);   // tracked var -> dense id
        int _synth;                                                 // synthetic label allocator (counts DOWN from -1)
        // Set when the walk meets a shape it cannot give a correct CFG edge for. Collapses the answer to the
        // pre-liveness one — every local a field — which costs storage and never correctness.
        bool _bailOut;

        int NewLabel() => --_synth;

        // ---- declaration collection ---------------------------------------------------------------------

        // Every `{k:var}` the ENCLOSING frame owns, in walk order. A nested lambda/closure body declares its own
        // frame's vars, so it is not descended into — mirroring which subtrees the event walk treats as opaque.
        public void CollectDeclarations(JsonNode n)
        {
            switch (n)
            {
                case JsonObject o:
                    var k = Str(o["k"]);
                    if (k != null && ClosureValueKinds.Contains(k))
                    {
                        // The capture VALUES are enclosing-frame expressions and may embed a `var` (a spliced
                        // valueBlock); the lambda's own body is not ours.
                        CollectDeclarations(o["captures"]);
                        CollectDeclarations(o["capValues"]);
                        CollectDeclarations(o["args"]);
                        return;
                    }
                    if (k == "var" && Str(o["name"]) is string vn && _ids.TryAdd(vn, _ids.Count))
                        _declared.Add((vn, o["type"]));
                    foreach (var kv in o) if (kv.Value != null) CollectDeclarations(kv.Value);
                    return;
                case JsonArray a:
                    foreach (var it in a) if (it != null) CollectDeclarations(it);
                    return;
            }
        }

        // ---- event emission -----------------------------------------------------------------------------

        void Emit(EvKind kind, string name, int label)
        {
            _ev.Add(new Ev(kind, name, label));
            if (_handlers.Count > 0) _extraSucc[_ev.Count - 1] = new List<int>(_handlers);
        }

        void Use(string n) { if (n != null && _ids.ContainsKey(n)) Emit(EvKind.Use, n, 0); }
        // A def of a name that is NOT a tracked `{k:var}` (a catch binding, a structured-loop variable, the
        // `result` parameter) kills nothing. A def whose name COLLIDES with a tracked var but denotes a different
        // binding would wrongly kill it, so a structural binder never emits one (see BindStructural).
        void Def(string n) { if (n != null && _ids.ContainsKey(n)) Emit(EvKind.Def, n, 0); }
        void BindStructural(string n) { if (n != null && !_ids.ContainsKey(n)) Emit(EvKind.Def, n, 0); }
        void Susp(string callee) => Emit(EvKind.Susp, callee ?? "a suspending call", 0);
        void Label(int id) => Emit(EvKind.Label, null, id);
        void Goto(int id) => Emit(EvKind.Goto, null, id);
        void BrIf(int id) => Emit(EvKind.BrIf, null, id);
        void Stop() => Emit(EvKind.Stop, null, 0);

        static int Id(JsonObject o) => o["id"] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

        // ---- statements ---------------------------------------------------------------------------------

        public void Stmts(JsonNode n)
        {
            if (n is JsonArray a) { foreach (var s in a) if (s != null) Stmt(s); return; }
            if (n != null) Stmt(n);
        }

        void Stmt(JsonNode n)
        {
            if (n is JsonArray a) { foreach (var s in a) if (s != null) Stmt(s); return; }
            if (n is not JsonObject o) return;
            switch (Str(o["k"]))
            {
                case "block": Stmts(o["body"]); return;
                case "exprStmt": Value(o["expr"]); return;
                case "label": Label(Id(o)); return;
                case "goto": Goto(Id(o)); return;
                case "brIf": Value(o["cond"]); BrIf(Id(o)); return;
                case "return": case "returnExpr": Value(o["value"]); Stop(); return;
                case "throw": case "throwExpr": Value(o["value"]); Stop(); return;
                case "var": Value(o["init"]); Def(Str(o["name"])); return;
                case "setLocal": Value(o["value"]); Def(Str(o["name"])); return;
                case "break": Jump(Str(o["label"]), cont: false); return;
                case "continue": Jump(Str(o["label"]), cont: true); return;
                case "try": Try(o); return;
                default: Value(o); return;
            }
        }

        // A statement's VALUE expression, with the re-ordering the emitter performs made visible.
        //
        // The state-machine emitter appends each suspension's segments to the statement list as it rewrites, then
        // emits the statement itself — assembled from the rewritten operands — AFTER them. So an operand that was
        // not spilled into its own slot is genuinely read at the RESUME point, not where it appears in the source.
        // (`acc + f()` reads `acc` after `f` resumes; that is exactly why `acc` needs a field.) Re-emitting the
        // subtree's reads after the walk models that, and it is the difference between a correct field and a
        // silently zeroed local.
        //
        // The re-emission deliberately does NOT descend into a child that carries a suspension of its own: such a
        // child was lowered to statements emitted IN PLACE (a flattened valueBlock, a `cond` turned into control
        // flow, the segments of a nested suspension), so its reads really do happen where they appear.
        void Value(JsonNode n)
        {
            Expr(n);
            if (n != null && HasSuspension(n)) ReRead(n);
        }

        // "Is there a suspension anywhere the ENCLOSING frame owns?" — the WIDE form, used to decide whether a
        // statement defers anything at all. It descends into an inline loop's body, because a suspension there is
        // this frame's; answering true too often only adds re-reads, which adds liveness.
        static bool HasSuspension(JsonNode n) => AnySuspension(n, wide: true);

        // The EMITTER's own predicate (SuspendColdLowering.HasOwnSuspension): an inline loop's body is a separate
        // scope for the "did Rewrite lower this node in place" question, because a suspending inline loop has
        // already been flattened away by the time this pass runs. Used only where a WRONG true would prune a
        // re-read and lose liveness.
        static bool HasOwnSuspension(JsonNode n) => AnySuspension(n, wide: false);

        static bool AnySuspension(JsonNode n, bool wide)
        {
            switch (n)
            {
                case JsonObject o:
                    var k = Str(o["k"]);
                    if (k != null && OtherFrameKinds.Contains(k))
                        // Another state machine owns its body's suspensions, but its CAPTURE VALUES are evaluated
                        // right here — a suspension in one is this frame's and defers the sibling captures.
                        return wide && (AnySuspension(o["captures"], true) || AnySuspension(o["capValues"], true)
                                        || AnySuspension(o["args"], true));
                    if (k != null && !wide && InlineLoopKinds.Contains(k)) return false;
                    if (Bool(o["suspendCall"])) return true;
                    foreach (var kv in o) if (kv.Value != null && AnySuspension(kv.Value, wide)) return true;
                    return false;
                case JsonArray a:
                    foreach (var it in a) if (it != null && AnySuspension(it, wide)) return true;
                    return false;
                default:
                    return false;
            }
        }

        // Pruning is by NODE KIND, never by "this subtree happens to contain a suspension": an ordinary call that
        // merely HAS a suspending argument is still assembled and emitted after that argument's segments, so its
        // other operands are exactly the deferred reads this models. Only three kinds are genuinely lowered to
        // statements emitted in place, and only they are pruned.
        void ReRead(JsonNode n)
        {
            switch (n)
            {
                case JsonObject o:
                    var k = Str(o["k"]);
                    // The suspending call: its operands are consumed BY the call at its own suspension point, so
                    // nothing of it is read afterwards. (An operand deferred past a suspension NESTED inside it is
                    // re-read by Expr, before that call's own Susp event.) Its result is the awaited field.
                    if (Bool(o["suspendCall"])) return;
                    if (k != null && ClosureValueKinds.Contains(k)) { ReReadCaptures(o); return; }
                    if (k == "local") { Use(Str(o["name"])); return; }
                    if (k != null && ByRefSlotKinds.Contains(k)) { Use(Str(o["local"])); ReRead(o["value"]); return; }
                    // A suspending `cond` became control flow and a `__cond$` slot; a suspending `valueBlock` was
                    // flattened into statements. Both ran IN PLACE — only the valueBlock's result stays in the
                    // deferred expression. The test must be the EMITTER's (HasOwnSuspension, which treats an inline
                    // loop's body as its own scope): pruning where the emitter did not lower loses liveness, while
                    // not pruning where it did only adds it.
                    if ((k == "cond" || k == "if") && HasOwnSuspension(o)) return;
                    if (k == "valueBlock" && HasOwnSuspension(o)) { ReRead(o["result"]); return; }
                    ReReadOperands(o);
                    return;
                case JsonArray a:
                    foreach (var it in a) if (it != null) ReRead(it);
                    return;
            }
        }

        // Re-read a node's operands, MINUS the ones the emitter stores into a temp before the suspension to their
        // right. Those never cross the resume: `f(s) + tick()` evaluates `f(s)` and saves the Int into `__ord$`
        // BEFORE suspending, so `s` itself is dead across the suspension and may stay a byref-like local. The spill
        // decision is SuspendColdLowering's own (SpilledBeforeSuspension), not a second copy of it — the two
        // disagreeing in this direction refuses source the emitter lowers fine, and in the other silently zeroes a
        // local.
        void ReReadOperands(JsonObject o)
        {
            var order = SuspendColdLowering.EvalOrderOf(o);
            HashSet<JsonNode> spilled = null;
            if (order is { } eo)
            {
                spilled = new HashSet<JsonNode>(ReferenceEqualityComparer.Instance);
                for (var i = 0; i < eo.Operands.Count; i++)
                    if (SuspendColdLowering.SpilledBeforeSuspension(eo.Operands, i)) spilled.Add(eo.Operands[i]);
            }
            foreach (var kv in o)
            {
                if (kv.Value == null || NonOperandKeys.Contains(kv.Key)) continue;
                // An operand key the eval-order list does not name (a `new`'s enclosing-instance slot, say) is
                // copied verbatim into the assembled node and so IS deferred — walk it.
                if (kv.Value is JsonArray arr)
                {
                    foreach (var it in arr) if (it != null && spilled?.Contains(it) != true) ReRead(it);
                }
                else if (spilled?.Contains(kv.Value) != true) ReRead(kv.Value);
            }
        }

        // The mirror of Captures(): the same three keys, so nothing Captures() evaluates is missed on the re-read.
        void ReReadCaptures(JsonObject o)
        {
            var overrides = o["capValues"] as JsonArray;
            if (o["captures"] is not JsonArray caps) { if (overrides != null) ReRead(overrides); }
            else
                for (var i = 0; i < caps.Count; i++)
                {
                    if (overrides != null && i < overrides.Count && overrides[i] != null) { ReRead(overrides[i]); continue; }
                    if (caps[i] is JsonObject c && c["k"] == null) Use(Str(c["name"]));
                    else if (caps[i] != null) ReRead(caps[i]);
                }
            ReRead(o["args"]);
        }

        void Jump(string label, bool cont)
        {
            for (var i = _loops.Count - 1; i >= 0; i--)
                if (label == null || _loops[i].Label == label) { Goto(cont ? _loops[i].Cont : _loops[i].End); return; }
            // A break/continue with no enclosing loop in this walk. Falling through would SUBSTITUTE the wrong edge
            // (the next statement instead of the loop exit), and a def between here and the real target would then
            // kill a value the exit path reads. There is no cheap correct edge to invent, so give the whole body
            // the pre-liveness answer: every local a field. Unreachable today — FlattenSuspendingLoops rewrites a
            // flattened loop's break/continue into `goto`, and Loop() tracks every structured loop that survives —
            // and it costs fields, never correctness, if it ever is.
            _bailOut = true;
        }

        // ---- expressions --------------------------------------------------------------------------------

        void Expr(JsonNode n)
        {
            if (n is JsonArray a) { foreach (var it in a) if (it != null) Expr(it); return; }
            if (n is not JsonObject o) return;
            Operands(o);
            if (!Bool(o["suspendCall"])) return;
            // A suspending call whose OWN operands carry a nested suspension: `ColdCall` rewrites the operands
            // through the same evaluation-order spill, so an operand it leaves inline is assembled into the call
            // after the nested suspension's segments — read at that resume point, just before this call runs.
            if (o.Any(kv => kv.Value != null && !NonOperandKeys.Contains(kv.Key) && HasSuspension(kv.Value)))
                ReReadOperands(o);
            // The suspension itself, AFTER the operands it consumes. Emitted on every path out of Operands, so a
            // `suspendCall` on a node kind Operands happens to know by name can never lose its event — losing one
            // would mean no liveness question is asked there and every variable live across it is demoted.
            Susp(Str(o["method"]));
        }

        void Operands(JsonObject o)
        {
            var k = Str(o["k"]);
            switch (k)
            {
                case "local": Use(Str(o["name"])); return;
                case "setLocal": Expr(o["value"]); Def(Str(o["name"])); return;
                case "var": Expr(o["init"]); Def(Str(o["name"])); return;
                // The managed-pointer local these read/write through is a bare STRING property, invisible to the
                // generic walk. A read/write through it is a use of the pointer, never a definition of it.
                case "byrefLoad": Use(Str(o["local"])); return;
                case "byrefStore": Use(Str(o["local"])); Expr(o["value"]); return;
                case "cond": Branch(o); return;
                // The STATEMENT `if` is a `branches:[{cond?,body,else?}]` chain (ilemit Emitter.Statements.cs), a
                // different shape from the `cond` ternary — reading `then`/`else` off it would silently skip the
                // whole subtree.
                case "if": IfChain(o); return;
                case "valueBlock": Stmts(o["stmts"]); Stmts(o["body"]); Expr(o["result"]); return;
                case "try": Try(o); return;
                case null: Children(o); return;
                // Control transfer reached in an EXPRESSION position (a `returnExpr`/`throwExpr` tail, an inline
                // scope function's `goto` out of a valueBlock). It is the same event either way — routing it back
                // to Stmt keeps every label/goto in the CFG, and a lost label would silently drop an edge.
                case "block": case "exprStmt": case "label": case "goto": case "brIf":
                case "return": case "returnExpr": case "throw": case "throwExpr":
                case "break": case "continue":
                    Stmt(o); return;
            }
            if (ClosureValueKinds.Contains(k)) { Captures(o); return; }
            if (LoopKinds.Contains(k)) { Loop(o); return; }
            Children(o);
        }

        // The statement `if`: a branch chain. Each non-else branch tests and jumps past its body; an `else` branch
        // is emitted inline with no test (ilemit Emitter.Statements.cs), so this mirrors that shape exactly.
        void IfChain(JsonObject o)
        {
            if (o["branches"] is not JsonArray brs) { Children(o); return; }
            var end = NewLabel();
            foreach (var br in brs.OfType<JsonObject>())
            {
                if (br["cond"] == null) { Stmts(br["body"]); continue; }
                var next = NewLabel();
                Value(br["cond"]);
                BrIf(next);
                Stmts(br["body"]);
                Goto(end);
                Label(next);
            }
            Label(end);
        }

        // The ordered generic walk: operand children in evaluation-order rank, metadata keys skipped. A
        // statement-list-valued key goes through Stmts, so a label/goto nested under a node kind this walker
        // does not know by name still enters the CFG.
        void Children(JsonObject o)
        {
            foreach (var kv in o.Where(kv => kv.Value != null && !NonOperandKeys.Contains(kv.Key))
                                .OrderBy(kv => KeyRank.GetValueOrDefault(kv.Key, 50)))
                if (StmtListKeys.Contains(kv.Key)) Stmts(kv.Value);
                else Expr(kv.Value);
        }

        // A lambda/closure VALUE. Its capture list is evaluated HERE; the body is another frame.
        // `captures` is either a list of value expressions (newClosure/newSam) or of {name,type} descriptors
        // naming an enclosing local (newSuspendLambda), optionally overridden slot-wise by `capValues`.
        void Captures(JsonObject o)
        {
            var overrides = o["capValues"] as JsonArray;
            if (o["captures"] is JsonArray caps)
                for (var i = 0; i < caps.Count; i++)
                {
                    if (overrides != null && i < overrides.Count && overrides[i] != null) { Expr(overrides[i]); continue; }
                    if (caps[i] is not JsonObject c) continue;
                    if (c["k"] != null) Expr(c);
                    else Use(Str(c["name"]));
                }
            else Expr(overrides);
            Expr(o["args"]);
        }

        // `cond ? then : else` (and the statement `if`) as real control flow — a def in one arm must never kill
        // a use in the other.
        void Branch(JsonObject o)
        {
            var elseL = NewLabel();
            var endL = NewLabel();
            Value(o["cond"]);
            BrIf(elseL);
            Stmts(o["then"]);
            Goto(endL);
            Label(elseL);
            Stmts(o["else"]);
            Label(endL);
        }

        // A structured loop that survived normalization — so it carries no suspension of its own, and the model
        // only has to keep the BACK EDGE (a use early in the body may follow a def later in a previous iteration).
        void Loop(JsonObject o)
        {
            var head = NewLabel();
            var end = NewLabel();
            Label(head);
            foreach (var kv in o.Where(kv => kv.Value != null
                                             && !LoopStructuralKeys.Contains(kv.Key)
                                             && !NonOperandKeys.Contains(kv.Key))
                                .OrderBy(kv => KeyRank.GetValueOrDefault(kv.Key, 50)))
                Expr(kv.Value);
            BrIf(end);                              // the loop may run zero times
            BindStructural(Str(o["var"]));          // the element/index binding, rebound each iteration
            _loops.Add((Str(o["label"]), head, end));
            Stmts(o["body"]);
            _loops.RemoveAt(_loops.Count - 1);
            Goto(head);
            Label(end);
        }

        // A protected region. Every event in the body reaches every catch entry and the region exit (where the
        // finally body starts), so a value a handler or finally reads is live throughout the region.
        void Try(JsonObject o)
        {
            var end = NewLabel();
            var catches = (o["catches"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            var catchLabels = catches.Select(_ => NewLabel()).ToList();

            var depth = _handlers.Count;
            _handlers.AddRange(catchLabels);
            _handlers.Add(end);
            // The exceptional edges ride on EMITTED events, and a protected region can contain none at all (a
            // no-argument call to a non-suspending function produces no event). Anchor one here so a value the
            // handlers read is still live before the region — otherwise a suspension preceding the try would see
            // it as dead.
            Label(NewLabel());
            Stmts(o["body"]);
            _handlers.RemoveRange(depth, _handlers.Count - depth);

            Goto(end);
            for (var i = 0; i < catches.Count; i++)
            {
                Label(catchLabels[i]);
                BindStructural(Str(catches[i]["var"]));
                _handlers.Add(end);                 // a throw inside a handler still runs the finally
                Stmts(catches[i]["body"]);
                _handlers.RemoveAt(_handlers.Count - 1);
                Goto(end);
            }
            Label(end);
            Stmts(o["finally"]);
        }

        // ---- the backward liveness solve ------------------------------------------------------------------

        public Result Solve()
        {
            var across = new Dictionary<string, string>(StringComparer.Ordinal);
            if (_declared.Count == 0 || _ev.Count == 0) return new Result(_declared, across);
            if (_bailOut)
            {
                foreach (var (name, _) in _declared) across.TryAdd(name, "a suspending call");
                return new Result(_declared, across);
            }

            var n = _ev.Count;
            var labelAt = new Dictionary<int, int>();
            for (var i = 0; i < n; i++)
                if (_ev[i].Kind == EvKind.Label) labelAt[_ev[i].Label] = i;

            // Normal and EXCEPTIONAL successors are kept apart: an exception can be raised BEFORE this event's own
            // write completes, so the handler's live set must not be killed by this event's def. (`try { x = f() }
            // catch { use(x) }` needs the pre-assignment `x`.)
            var succ = new List<int>[n];
            var hsucc = new List<int>[n];
            for (var i = 0; i < n; i++)
            {
                var s = new List<int>();
                switch (_ev[i].Kind)
                {
                    case EvKind.Goto:
                        if (labelAt.TryGetValue(_ev[i].Label, out var g)) s.Add(g);
                        break;
                    case EvKind.Stop:
                        break;                                       // only the handler edges
                    case EvKind.BrIf:
                        if (labelAt.TryGetValue(_ev[i].Label, out var t)) s.Add(t);
                        if (i + 1 < n) s.Add(i + 1);
                        break;
                    default:
                        if (i + 1 < n) s.Add(i + 1);
                        break;
                }
                var h = new List<int>();
                if (_extraSucc.TryGetValue(i, out var extra))
                    foreach (var lbl in extra)
                        if (labelAt.TryGetValue(lbl, out var hi) && !h.Contains(hi)) h.Add(hi);
                succ[i] = s;
                hsucc[i] = h;
            }

            var live = new HashSet<int>[n];
            for (var i = 0; i < n; i++) live[i] = new HashSet<int>();
            // Round-robin over the event list in reverse. Backward liveness on this CFG converges in a small
            // number of sweeps (one per loop-nesting depth plus one); the bound is a tripwire, not a policy —
            // hitting it would mean the CFG is not what this pass models, so it fails loud rather than silently
            // reporting a variable dead.
            const int MaxRounds = 2000;
            var rounds = 0;
            bool changed;
            do
            {
                if (++rounds > MaxRounds)
                    throw new InvalidOperationException(
                        "bir2cir: suspend-lowering: local liveness did not converge over the state-machine body "
                        + $"({n} events) — refusing to guess which locals survive a suspension.");
                changed = false;
                for (var i = n - 1; i >= 0; i--)
                {
                    var e = _ev[i];
                    var next = new HashSet<int>();
                    foreach (var s in succ[i]) next.UnionWith(live[s]);
                    if (e.Kind == EvKind.Def && _ids.TryGetValue(e.Name, out var dId)) next.Remove(dId);
                    if (e.Kind == EvKind.Use && _ids.TryGetValue(e.Name, out var uId)) next.Add(uId);
                    foreach (var h in hsucc[i]) next.UnionWith(live[h]);   // never killed by this event's def
                    if (next.Count != live[i].Count || !next.SetEquals(live[i])) { live[i] = next; changed = true; }
                }
            } while (changed);

            // A variable needs an SM field iff it is LIVE-OUT at some suspension point (a suspension neither reads
            // nor writes a local, so live-out equals this event's own live set).
            var byId = new string[_ids.Count];
            foreach (var kv in _ids) byId[kv.Value] = kv.Key;
            for (var i = 0; i < n; i++)
            {
                if (_ev[i].Kind != EvKind.Susp) continue;
                foreach (var id in live[i]) across.TryAdd(byId[id], _ev[i].Name);
            }
            return new Result(_declared, across);
        }
    }
}
