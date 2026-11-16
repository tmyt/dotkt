using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendLiveness
{
    internal sealed class SourceResult
    {
        readonly IReadOnlyDictionary<string, string> across;
        internal IReadOnlyList<(string Name, TypeNode Type, string Role)> DeclaredVars { get; }
        internal SourceResult(IReadOnlyList<(string Name, TypeNode Type, string Role)> declared,
            IReadOnlyDictionary<string, string> across)
        { DeclaredVars = declared; this.across = across; }
        internal bool LivesAcrossSuspension(string name) => name != null && across.ContainsKey(name);
        internal string FirstSuspensionAcross(string name) =>
            name != null && across.TryGetValue(name, out var callee) ? callee : null;
    }

    // The normalized body belongs to one actual callable. No declarations are
    // copied, registered, or inferred; both inputs share the event CFG solver.
    internal static SourceResult Analyze(IReadOnlyList<IBirStatement> body)
    {
        var builder = new SourceBuilder();
        foreach (var statement in body ?? Array.Empty<IBirStatement>()) builder.Collect((BirGraphValue)statement);
        builder.Statements(body);
        return builder.Solve();
    }

    static BirGraphValue SourceNode(IBirExpression value) => (BirGraphValue)value;
    static bool SourceClosureValue(BirGraphValue node) =>
        SuspendColdLowering.IsSourceOtherFrame(node) || node is BirNewSam;

    // The maintained graph provides only actual structured child fields. The
    // existing scalar rank orders them; encoded facts and opaque payload bytes
    // are never reparsed as a second executable graph.
    static IReadOnlyList<(string Field, BirGraphValue Node)> SourceChildren(BirGraphValue node, bool ordered = false)
    {
        var children = new List<(string Field, BirGraphValue Node)>();
        if (node == null || node is IBirExecutablePayload) return children;
        node.VisitOwnChildSlots((field, _, slot) => children.Add((field, slot.Value)));
        return ordered ? children.OrderBy(child => OperandRank(child.Field)).ToArray() : children;
    }

    static IEnumerable<BirGraphValue> SourceCaptureGraphs(BirGraphValue node)
    {
        switch (node)
        {
            case BirNewClosure closure:
                foreach (var capture in closure.Captures.OrEmpty()) yield return SourceNode(capture);
                break;
            case BirNewSam sam:
                foreach (var capture in sam.Captures.OrEmpty()) yield return SourceNode(capture);
                break;
            case BirInlineLambda lambda:
                foreach (var capture in lambda.Captures.OrEmpty()) yield return capture;
                break;
            case BirNewSuspendLambda lambda:
                foreach (var capture in lambda.Captures.OrEmpty()) yield return capture;
                foreach (var capture in lambda.CapValues.OrEmpty())
                    if (capture != null) yield return SourceNode(capture);
                break;
        }
    }

    static bool SourceHasSuspension(BirGraphValue node, bool wide)
    {
        if (node == null || node is IBirExecutablePayload) return false;
        if (SuspendColdLowering.IsSourceOtherFrame(node))
            return wide && SourceCaptureGraphs(node).Any(capture => SourceHasSuspension(capture, true));
        if (!wide && SuspendColdLowering.IsSourceInlineLoop(node)) return false;
        return SuspendColdLowering.TrySourceSuspension(node, out _)
            || SourceChildren(node).Any(child => SourceHasSuspension(child.Node, wide));
    }

    sealed class SourceBuilder
    {
        readonly List<Ev> events = new();
        readonly Dictionary<int, List<int>> extraSuccessors = new();
        readonly List<int> handlers = new();
        readonly List<(string Label, int Continue, int End)> loops = new();
        readonly List<(string Name, TypeNode Type, string Role)> declared = new();
        readonly Dictionary<string, int> ids = new(StringComparer.Ordinal);
        int synthetic;
        bool bailOut;
        int NewLabel() => --synthetic;

        internal void Collect(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return;
            if (SourceClosureValue(node))
            {
                foreach (var capture in SourceCaptureGraphs(node)) Collect(capture);
                return;
            }
            if (node is BirVariable { Name.HasValue: true } variable && ids.TryAdd(variable.Name.Value, ids.Count))
                declared.Add((variable.Name.Value, variable.Type.OrDefault(), variable.Role.OrDefault()));
            node.VisitChildren(Collect);
        }

        void Emit(EvKind kind, string name = null, int label = 0)
        {
            events.Add(new Ev(kind, name, label));
            if (handlers.Count != 0) extraSuccessors[events.Count - 1] = new List<int>(handlers);
        }
        void Use(string name) { if (name != null && ids.ContainsKey(name)) Emit(EvKind.Use, name); }
        void Define(string name) { if (name != null && ids.ContainsKey(name)) Emit(EvKind.Def, name); }
        void BindStructural(string name) { if (name != null && !ids.ContainsKey(name)) Emit(EvKind.Def, name); }
        void Label(int label) => Emit(EvKind.Label, label: label);
        void Goto(int label) => Emit(EvKind.Goto, label: label);
        void BranchIf(int label) => Emit(EvKind.BrIf, label: label);

        internal void Statements(IReadOnlyList<IBirStatement> statements)
        { foreach (var statement in statements ?? Array.Empty<IBirStatement>()) Statement((BirGraphValue)statement); }

        void Statement(BirGraphValue node)
        {
            switch (node)
            {
                case null: return;
                case BirBlock block: Statements(block.Body.OrEmpty()); return;
                case BirExpressionStatement statement: Value(SourceNode(statement.Expr.OrDefault())); return;
                case BirLabel label: Label(label.Id.OrDefault()); return;
                case BirGoto jump: Goto(jump.Id.OrDefault()); return;
                case BirBranchIf branch: Value(SourceNode(branch.Cond.OrDefault())); BranchIf(branch.Id.OrDefault()); return;
                case BirReturn result: Value(SourceNode(result.Value.OrDefault())); Emit(EvKind.Stop); return;
                case BirReturnExpression result: Value(SourceNode(result.Value.OrDefault())); Emit(EvKind.Stop); return;
                case BirThrow thrown: Value(SourceNode(thrown.Value.OrDefault())); Emit(EvKind.Stop); return;
                case BirThrowExpression thrown: Value(SourceNode(thrown.Value.OrDefault())); Emit(EvKind.Stop); return;
                case BirVariable variable: Value(SourceNode(variable.Init.OrDefault())); Define(variable.Name.OrDefault()); return;
                case BirSetLocal store: Value(SourceNode(store.Value.OrDefault())); Define(store.Name.OrDefault()); return;
                case BirBreak jump: Jump(jump.Label.OrDefault(), false); return;
                case BirContinue jump: Jump(jump.Label.OrDefault(), true); return;
                case BirTry region: Try(region); return;
                default: Value(node); return;
            }
        }

        void Value(BirGraphValue node)
        {
            Expression(node);
            if (SourceHasSuspension(node, true)) ReRead(node);
        }

        // Residual operands are consumed by the post-resume statement. In-place
        // conditional/value-block lowering is the same exception as the emitter.
        void ReRead(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload || SuspendColdLowering.TrySourceSuspension(node, out _)) return;
            if (SourceClosureValue(node)) { Captures(node, reRead: true); return; }
            switch (node)
            {
                case BirLocal local: Use(local.Name.OrDefault()); return;
                case BirByRefLoad load: Use(load.Local.OrDefault()); return;
                case BirByRefStore store: Use(store.Local.OrDefault()); ReRead(SourceNode(store.Value.OrDefault())); return;
                case BirConditional or BirIf when SourceHasSuspension(node, false): return;
                case BirValueBlock block when SourceHasSuspension(block, false): ReRead(SourceNode(block.Result.OrDefault())); return;
                default: ReReadOperands(node); return;
            }
        }

        void ReReadOperands(BirGraphValue node)
        { foreach (var child in SourceChildren(node)) ReRead(child.Node); }

        void Jump(string label, bool isContinue)
        {
            for (var index = loops.Count - 1; index >= 0; index--)
                if (label == null || loops[index].Label == label)
                { Goto(isContinue ? loops[index].Continue : loops[index].End); return; }
            bailOut = true;
        }

        void Expression(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return;
            Operands(node);
            if (!SuspendColdLowering.TrySourceSuspension(node, out var callee)) return;
            if (SourceChildren(node).Any(child => SourceHasSuspension(child.Node, true))) ReReadOperands(node);
            Emit(EvKind.Susp, callee ?? "a suspending call");
        }

        void Operands(BirGraphValue node)
        {
            switch (node)
            {
                case BirLocal local: Use(local.Name.OrDefault()); return;
                case BirSetLocal store: Expression(SourceNode(store.Value.OrDefault())); Define(store.Name.OrDefault()); return;
                case BirVariable variable: Expression(SourceNode(variable.Init.OrDefault())); Define(variable.Name.OrDefault()); return;
                case BirByRefLoad load: Use(load.Local.OrDefault()); return;
                case BirByRefStore store: Use(store.Local.OrDefault()); Expression(SourceNode(store.Value.OrDefault())); return;
                case BirConditional conditional: Branch(conditional); return;
                case BirIf conditional: IfChain(conditional); return;
                case BirValueBlock block: Statements(block.Stmts.OrEmpty()); Expression(SourceNode(block.Result.OrDefault())); return;
                case BirTry region: Try(region); return;
                case BirBlock or BirExpressionStatement or BirLabel or BirGoto or BirBranchIf
                    or BirReturn or BirReturnExpression or BirThrow or BirThrowExpression or BirBreak or BirContinue:
                    Statement(node); return;
            }
            if (SourceClosureValue(node)) { Captures(node, reRead: false); return; }
            if (Loop(node)) return;
            foreach (var child in SourceChildren(node, ordered: true))
                if (child.Node is IBirStatement) Statement(child.Node); else Expression(child.Node);
        }

        void IfChain(BirIf conditional)
        {
            var end = NewLabel();
            foreach (var branch in conditional.Branches.OrEmpty())
            {
                if (!branch.Cond.HasValue) { Statements(branch.Body.OrEmpty()); continue; }
                var next = NewLabel();
                Value(SourceNode(branch.Cond.Value)); BranchIf(next);
                Statements(branch.Body.OrEmpty()); Goto(end); Label(next);
            }
            Label(end);
        }

        void Captures(BirGraphValue node, bool reRead)
        {
            void Read(BirGraphValue capture) { if (reRead) ReRead(capture); else Expression(capture); }
            switch (node)
            {
                case BirNewClosure closure:
                    foreach (var capture in closure.Captures.OrEmpty()) Read(SourceNode(capture));
                    break;
                case BirNewSam sam:
                    foreach (var capture in sam.Captures.OrEmpty()) Read(SourceNode(capture));
                    break;
                case BirInlineLambda lambda:
                    foreach (var capture in lambda.Captures.OrEmpty()) Use(capture.Name.OrDefault());
                    break;
                case BirNewSuspendLambda lambda:
                    var overrides = lambda.CapValues.OrEmpty();
                    if (!lambda.Captures.HasValue)
                    {
                        foreach (var capture in overrides) if (capture != null) Read(SourceNode(capture));
                    }
                    else
                    {
                        var captures = lambda.Captures.Value;
                        for (var index = 0; index < captures.Length; index++)
                            if (index < overrides.Length && overrides[index] is { } capture) Read(SourceNode(capture));
                            else Use(captures[index].Name.OrDefault());
                    }
                    break;
            }
        }

        void Branch(BirConditional conditional)
        {
            var otherwise = NewLabel(); var end = NewLabel();
            Value(SourceNode(conditional.Cond.OrDefault())); BranchIf(otherwise);
            Statement(SourceNode(conditional.Then.OrDefault())); Goto(end); Label(otherwise);
            Statement(SourceNode(conditional.Else.OrDefault())); Label(end);
        }

        // The same conservative structured-loop model: the header may be
        // re-evaluated, zero iterations are possible, and body uses see backedges.
        bool Loop(BirGraphValue node)
        {
            IBirStatement[] body;
            string variable, label;
            BirGraphValue[] header;
            switch (node)
            {
                case BirForArray loop:
                    body = loop.Body.OrEmpty(); variable = loop.Var.OrDefault(); label = loop.Label.OrDefault();
                    header = new[] { SourceNode(loop.Array.OrDefault()) }; break;
                case BirForRange loop:
                    body = loop.Body.OrEmpty(); variable = loop.Var.OrDefault(); label = loop.Label.OrDefault();
                    header = new[] { SourceNode(loop.Range.OrDefault()) }; break;
                case BirFor loop:
                    body = loop.Body.OrEmpty(); variable = loop.Var.OrDefault(); label = loop.Label.OrDefault();
                    header = new[] { SourceNode(loop.From.OrDefault()), SourceNode(loop.To.OrDefault()) }; break;
                case BirWhile loop:
                    body = loop.Body.OrEmpty(); variable = null; label = loop.Label.OrDefault();
                    header = new[] { SourceNode(loop.Cond.OrDefault()) }; break;
                case BirForEach loop:
                    body = loop.Body.OrEmpty(); variable = loop.Var.OrDefault(); label = loop.Label.OrDefault();
                    header = new[] { SourceNode(loop.Src.OrDefault()) }; break;
                case BirRepeat loop:
                    body = loop.Body.OrEmpty(); variable = loop.Var.OrDefault(); label = null;
                    header = new[] { SourceNode(loop.Count.OrDefault()) }; break;
                default: return false;
            }
            var head = NewLabel(); var end = NewLabel();
            Label(head);
            foreach (var operand in header) Expression(operand);
            BranchIf(end); BindStructural(variable);
            loops.Add((label, head, end)); Statements(body); loops.RemoveAt(loops.Count - 1);
            Goto(head); Label(end);
            return true;
        }

        void Try(BirTry region)
        {
            var end = NewLabel();
            var catches = region.Catches.OrEmpty();
            var catchLabels = catches.Select(_ => NewLabel()).ToArray();
            var depth = handlers.Count;
            handlers.AddRange(catchLabels); handlers.Add(end);
            // Even an eventless protected body can throw before a later write.
            Label(NewLabel()); Statements(region.Body.OrEmpty());
            handlers.RemoveRange(depth, handlers.Count - depth); Goto(end);
            for (var index = 0; index < catches.Length; index++)
            {
                Label(catchLabels[index]); BindStructural(catches[index].Var.OrDefault());
                handlers.Add(end); Statements(catches[index].Body.OrEmpty()); handlers.RemoveAt(handlers.Count - 1);
                Goto(end);
            }
            Label(end); Statements(region.Finally.OrEmpty());
        }

        internal SourceResult Solve() => new(declared, SolveEvents(events, extraSuccessors, ids, bailOut));
    }
}
