using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Physical control-flow ownership over owning typed occurrences. Regions are
// statement-array identities, not labels inferred by ilemit or field-name walks.
static class TypedProtectedRegions
{
    internal sealed record Edge(BirGraphSlot Slot, IBirStatement[][] Source, IBirStatement[][] Target, int? LoopId = null);
    sealed record Loop(string Label, IBirStatement[][] Region, int Id);
    readonly record struct LoopShape(IBirStatement[] Body, string Label, int Id, Action<int> SetId, Action DropLabel);
    static int _loopId;

    internal static void Apply(BirGraphValue root, ReferenceMetadataIndex references) => Frames(root, references);

    static void Frames(BirGraphValue node, ReferenceMetadataIndex references, bool inFrame = false)
    {
        switch (node)
        {
            case BirFunctionDeclaration { Params.HasValue: true, Body.HasValue: true } method:
                Process(Statements(method.Body.Value), method.Ret.Value, references);
                node.VisitChildren(child => Frames(child, references, true));
                return;
            case BirConstructor { Params.HasValue: true, Body.HasValue: true } constructor:
                var parts = Statements(constructor.PreStmts.OrEmpty())
                    .Concat(Expressions(constructor.BaseArgs.OrEmpty()))
                    .Concat(Expressions(constructor.ThisArgs.OrEmpty()))
                    .Concat(Statements(constructor.Body.Value)).ToArray();
                Process(parts, new TypeNode.Fqn("void"), references);
                node.VisitChildren(child => Frames(child, references, true));
                return;
            case BirClosureDefinition { Params.HasValue: true, Body.HasValue: true } closure:
                Process(Statements(closure.Body.Value), closure.Ret.Value, references);
                node.VisitChildren(child => Frames(child, references, true));
                return;
            case BirFieldDeclaration { Init.HasValue: true } field when !inFrame:
                Process(new[] { new BirGraphSlot(() => (BirGraphValue)field.Init.Value,
                    value => field.Init = OptionalField<IBirExpression>.Of((IBirExpression)value)) },
                    new TypeNode.Fqn("void"), references);
                node.VisitChildren(child => Frames(child, references, true));
                return;
            default:
                node.VisitChildren(child => Frames(child, references, inFrame));
                return;
        }
    }

    internal static BirGraphSlot[] Statements(IBirStatement[] nodes) => Slots(nodes);
    static BirGraphSlot[] Expressions(IBirExpression[] nodes) => Slots(nodes);
    static BirGraphSlot[] Slots<T>(T[] nodes) where T : class
    {
        var result = new BirGraphSlot[nodes.Length];
        for (var index = 0; index < nodes.Length; index++)
        {
            var at = index;
            result[index] = new(() => (BirGraphValue)(object)nodes[at], value => nodes[at] = (T)(object)value);
        }
        return result;
    }

    static void Process(IReadOnlyList<BirGraphSlot> parts, TypeNode returnType, ReferenceMetadataIndex references)
    {
        TypedEscapingFinally.Apply(parts, returnType, references);
        foreach (var edge in Edges(parts))
        {
            if (edge.Slot.Value is BirReturn or BirReturnExpression) continue;
            if (!IsPrefix(edge.Target, edge.Source))
                throw new InvalidOperationException("bir2cir: control transfer enters a different protected region");
            var leave = edge.Source.Length != edge.Target.Length;
            switch (edge.Slot.Value)
            {
                case BirGoto value: value.Leave = leave; break;
                case BirBranchIf value: value.Leave = leave; break;
                case BirBreak value: value.Leave = leave; value.LoopId = edge.LoopId!.Value; value.Label = default; break;
                case BirContinue value: value.Leave = leave; value.LoopId = edge.LoopId!.Value; value.Label = default; break;
                default: throw new InvalidOperationException("Unaccounted physical transfer kind");
            }
        }
        void DropLabels(BirGraphValue node)
        {
            if (Shape(node) is { } loop) loop.DropLabel();
            node.VisitCurrentFrameSlots(child => DropLabels(child.Value));
        }
        foreach (var part in parts) DropLabels(part.Value);
    }

    internal static List<Edge> Edges(IReadOnlyList<BirGraphSlot> parts)
    {
        var labels = new Dictionary<int, IBirStatement[][]>();
        var edges = new List<Edge>();
        foreach (var part in parts) Visit(part, [], new(), labels, null);
        foreach (var part in parts) Visit(part, [], new(), labels, edges);
        return edges;
    }

    static void Visit(BirGraphSlot slot, IBirStatement[][] region, List<Loop> loops,
        Dictionary<int, IBirStatement[][]> labels, List<Edge> edges)
    {
        var node = slot.Value;
        if (node is BirLabel labelNode && edges == null) labels.Add(labelNode.Id.Value, region);
        if (edges != null)
        {
            IBirStatement[][] target = null;
            int? loopId = null;
            switch (node)
            {
                case BirGoto jump: target = labels[jump.Id.Value]; break;
                case BirBranchIf jump: target = labels[jump.Id.Value]; break;
                case BirBreak jump: SelectLoop(jump.Label.OrDefault()); break;
                case BirContinue jump: SelectLoop(jump.Label.OrDefault()); break;
                case BirReturn or BirReturnExpression: target = []; break;
            }
            if (target != null) edges.Add(new(slot, region, target, loopId));
            void SelectLoop(string label)
            {
                var loop = loops.Last(loop => label == null || loop.Label == label);
                target = loop.Region;
                loopId = loop.Id;
            }
        }
        if (node is BirTry statement)
        {
            InRegion(statement.Body);
            foreach (var handler in statement.Catches.OrEmpty()) InRegion(handler.Body);
            InRegion(statement.Finally);
            return;
        }
        if (Shape(node) is { } shape)
        {
            var id = edges == null ? Interlocked.Increment(ref _loopId) : shape.Id;
            if (edges == null) shape.SetId(id);
            var bodyNodes = new HashSet<BirGraphValue>(shape.Body.Cast<BirGraphValue>(), ReferenceEqualityComparer.Instance);
            node.VisitChildSlots(child => { if (!bodyNodes.Contains(child.Value)) Visit(child, region, loops, labels, edges); });
            loops.Add(new(shape.Label, region, id));
            foreach (var child in Statements(shape.Body)) Visit(child, region, loops, labels, edges);
            loops.RemoveAt(loops.Count - 1);
            return;
        }
        node.VisitCurrentFrameSlots(child => Visit(child, region, loops, labels, edges));

        void InRegion(OptionalField<IBirStatement[]> body)
        {
            if (!body.HasValue) return;
            var path = region.Append(body.Value).ToArray();
            foreach (var child in Statements(body.Value)) Visit(child, path, loops, labels, edges);
        }
    }

    internal static bool IsPrefix(IBirStatement[][] prefix, IBirStatement[][] path) => prefix.Length <= path.Length
        && prefix.Select((body, index) => ReferenceEquals(body, path[index])).All(equal => equal);

    static LoopShape? Shape(BirGraphValue node) => node switch {
        BirFor value => new(value.Body.OrEmpty(), value.Label.OrDefault(), value.LoopId.OrDefault(),
            id => value.LoopId = id, () => value.Label = default),
        BirForArray value => new(value.Body.OrEmpty(), value.Label.OrDefault(), value.LoopId.OrDefault(),
            id => value.LoopId = id, () => value.Label = default),
        BirForRange value => new(value.Body.OrEmpty(), value.Label.OrDefault(), value.LoopId.OrDefault(),
            id => value.LoopId = id, () => value.Label = default),
        BirForEach value => new(value.Body.OrEmpty(), value.Label.OrDefault(), value.LoopId.OrDefault(),
            id => value.LoopId = id, () => value.Label = default),
        BirWhile value => new(value.Body.OrEmpty(), value.Label.OrDefault(), value.LoopId.OrDefault(),
            id => value.LoopId = id, () => value.Label = default),
        BirRepeat value => new(value.Body.OrEmpty(), null, value.LoopId.OrDefault(),
            id => value.LoopId = id, () => { }),
        _ => null,
    };
}
