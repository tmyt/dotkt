using System.Collections.Generic;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Primitive membership evaluates lo, hi, subject once in Kotlin order. Only
// common Q1 re-readable values may move/duplicate; other values use the existing
// declared slot types, not a reconstructed static type.
static class RangeMembershipLowering
{
    static int _counter;
    static readonly HashSet<string> RangeOwners = new(System.StringComparer.Ordinal)
    { "kotlin.ranges.IntRange", "kotlin.ranges.LongRange", "kotlin.ranges.CharRange" };
    static readonly HashSet<string> PrimReceivers = new(System.StringComparer.Ordinal)
    { "kotlin.Byte", "kotlin.Short", "kotlin.Int", "kotlin.Long", "kotlin.Char" };

    public static BirFileDeclaration Apply(BirFileDeclaration root, ISet<string> localTopLevelFns, bool appBuild)
        => (BirFileDeclaration)SourceGraphRewrite.Rewrite(root, (node, _) => Rewrite(node, localTopLevelFns, appBuild));

    static BirGraphValue Rewrite(BirGraphValue node, ISet<string> localTopLevelFns, bool appBuild)
    {
        if (node is not BirCallInstance call || call.Method.OrDefault() != "contains"
            || call.OwnerType.OrDefault() is not TypeNode.Fqn owner || !RangeOwners.Contains(owner.Name)
            || call.Args.OrDefault() is not { Length: 1 } args || args[0] == null
            || ExtractBounds(call.Recv.OrDefault(), localTopLevelFns, appBuild) is not { } bounds)
            return node;
        var statements = new List<IBirStatement>();
        var site = System.Threading.Interlocked.Increment(ref _counter);
        var lo = Bind(bounds.Lo, bounds.LoType, "lo", site, statements);
        var hi = Bind(bounds.Hi, bounds.HiType, "hi", site, statements);
        var subjectType = call.Sig.OrDefault() is { Length: > 0 } signature ? signature[0] : null;
        var subject = Bind(args[0], subjectType, "in", site, statements);
        // Incomplete current source remains with its validation/resolution owner.
        if (lo == null || hi == null || subject == null) return node;
        var core = new BirConditional {
            Cond = OptionalField<IBirExpression>.Of(Binary(">=", Clone(subject), Clone(lo))),
            Then = OptionalField<IBirExpression>.Of(Binary(bounds.Comparison, Clone(subject), Clone(hi))),
            Else = OptionalField<IBirExpression>.Of(new BirConstant {
                Type = new TypeNode.Fqn("kotlin.Boolean"), Value = new BirLiteral.BooleanLiteral(false),
            }),
        };
        return statements.Count == 0 ? core : new BirValueBlock {
            Stmts = statements.ToArray(), Result = OptionalField<IBirExpression>.Of(core),
        };
    }

    static IBirExpression Bind(IBirExpression value, TypeNode slotType, string role, int site,
        List<IBirStatement> statements)
    {
        if (ValueStability.IsReReadable(((BirGraphNode)value).Tag,
            value is BirEnumValue { Underlying.HasValue: true, PhysicalValue.HasValue: true })) return value;
        if (slotType == null) return null;
        var name = "__range" + role + "$" + site;
        statements.Add(new BirVariable { Name = name, Type = OptionalField<TypeNode>.Of(slotType),
            Init = OptionalField<IBirExpression>.Of(value) });
        return new BirLocal { Name = name };
    }

    readonly record struct Bounds(IBirExpression Lo, IBirExpression Hi, TypeNode LoType, TypeNode HiType, string Comparison);

    static Bounds? ExtractBounds(IBirExpression receiver, ISet<string> localTopLevelFns, bool appBuild)
    {
        if (receiver is BirCallInstance member && member.Method.OrDefault() is "rangeTo" or "rangeUntil"
            && member.OwnerType.OrDefault() is TypeNode.Fqn owner && PrimReceivers.Contains(owner.Name)
            && member.Recv.HasValue && member.Args.OrDefault() is { Length: 1 } args && args[0] != null)
            return new(member.Recv.Value, args[0], owner,
                member.Sig.OrDefault() is { Length: > 0 } signature ? signature[0] : null,
                member.Method.Value == "rangeUntil" ? "<" : "<=");
        if (receiver is BirCallStatic extension && !extension.Owner.HasValue && extension.Method.OrDefault() == "until"
            && !(appBuild && localTopLevelFns.Contains("until"))
            && extension.Sig.OrDefault() is { Length: > 0 } slots
            && slots[0] is TypeNode.Fqn first && PrimReceivers.Contains(first.Name)
            && extension.Args.OrDefault() is { Length: 2 } values && values[0] != null && values[1] != null)
            return new(values[0], values[1], slots[0], slots.Length > 1 ? slots[1] : null, "<");
        return null;
    }

    static IBirExpression Clone(IBirExpression value) => (IBirExpression)((BirGraphValue)value).DeepClone();
    static BirBinaryOperation Binary(string op, IBirExpression left, IBirExpression right) => new() {
        Op = op, Lhs = OptionalField<IBirExpression>.Of(left), Rhs = OptionalField<IBirExpression>.Of(right),
    };
}
