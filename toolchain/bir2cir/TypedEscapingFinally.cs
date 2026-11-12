using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using static TypedProtectedRegions;

// A finally that escapes its handler becomes one shared cleanup site plus an
// explicit pending completion. All operands and generic frames remain typed.
static class TypedEscapingFinally
{
    static int _temporary;
    internal static void Apply(IReadOnlyList<BirGraphSlot> parts, TypeNode returnType,
        ReferenceMetadataIndex references, bool physicalMembers = true)
    {
        var tries = new List<BirGraphSlot>();
        void Collect(BirGraphSlot slot)
        {
            slot.Value.VisitCurrentFrameSlots(Collect);
            if (slot.Value is BirTry) tries.Add(slot);
        }
        foreach (var part in parts) Collect(part);
        var nextLabel = LabelIds(parts).DefaultIfEmpty(-1).Max() + 1;
        foreach (var slot in tries)
        {
            var statement = (BirTry)slot.Value;
            if (!statement.Finally.HasValue) continue;
            var body = statement.Finally.Value;
            var edges = Edges(parts);
            if (!edges.Any(edge => edge.Source.Contains(body) && !edge.Target.Contains(body))) continue;
            Rewrite(slot, statement, body, edges, returnType, references, nextLabel++, physicalMembers);
        }
    }

    static IEnumerable<int> LabelIds(IReadOnlyList<BirGraphSlot> parts)
    {
        var ids = new List<int>();
        void Walk(BirGraphValue node)
        {
            if (node is BirLabel label) ids.Add(label.Id.Value);
            node.VisitCurrentFrameSlots(child => Walk(child.Value));
        }
        foreach (var part in parts) Walk(part.Value);
        return ids;
    }

    static void Rewrite(BirGraphSlot slot, BirTry statement, IBirStatement[] finallyBody, List<Edge> edges,
        TypeNode returnType, ReferenceMetadataIndex references, int cleanupLabel, bool physicalMembers)
    {
        var id = Interlocked.Increment(ref _temporary);
        var state = "cir$completion" + id;
        var exception = "cir$exception" + id;
        var caught = "cir$caught" + id;
        var result = "cir$return" + id;
        var exceptionType = new TypeNode.Fqn("System.Runtime.ExceptionServices.ExceptionDispatchInfo");
        var continuations = new List<IBirStatement>();
        var hasResult = false;
        var owned = new HashSet<IBirStatement[]> { statement.Body.Value };
        foreach (var handler in statement.Catches.OrEmpty()) owned.Add(handler.Body.Value);
        foreach (var edge in edges.Where(edge => edge.Source.Any(owned.Contains) && !edge.Target.Any(owned.Contains)).Reverse())
        {
            var original = Clone(edge.Slot.Value);
            var expressionReturn = original is BirReturnExpression;
            if (original is BirReturnExpression returning) original = new BirReturn { Value = returning.Value };
            var pre = new List<IBirStatement>();
            if (original is BirReturn { Value.HasValue: true } ret)
            {
                if (returnType is TypeNode.Fqn { Name: "void" or "System.Void" })
                    pre.Add(new BirExpressionStatement { Expr = ret.Value });
                else
                {
                    hasResult = true;
                    pre.Add(Set(result, ret.Value.Value));
                    ret.Value = Local(result);
                }
            }
            var continuation = continuations.Count + 1;
            pre.Add(Set(state, Number(continuation)));
            pre.Add(new BirGoto { Id = cleanupLabel });
            if (original is BirBranchIf conditional)
            {
                var condition = conditional.On.Value ? conditional.Cond.Value
                    : (IBirExpression)new BirUnaryOperation { Op = "!", E = conditional.Cond };
                edge.Slot.Replace(If(condition, pre.ToArray()));
                original = new BirGoto { Id = conditional.Id, Leave = conditional.Leave };
            }
            else if (expressionReturn)
                edge.Slot.Replace(new BirValueBlock { Stmts = pre.ToArray(), Result = new BirThrowExpression {
                    Value = Null(new TypeNode.Fqn("System.Exception")),
                } });
            else edge.Slot.Replace(new BirBlock { Body = pre.ToArray() });
            continuations.Add(If(new BirBinaryOperation { Op = "==", Lhs = Local(state), Rhs = Number(continuation) },
                new[] { (IBirStatement)original }));
        }
        var originalBody = Clone(statement.Body.Value);
        var originalCatches = statement.Catches.OrEmpty().Select(value => (BirCatch)Clone(value)).ToArray();
        if (originalCatches.Length != 0)
            originalBody = new IBirStatement[] { new BirTry { Body = originalBody, Catches = originalCatches } };
        var body = new List<IBirStatement> {
            new BirVariable { Name = state, Type = new TypeNode.Fqn("System.Int32"), Init = Number(0) },
            new BirVariable { Name = exception, Type = exceptionType, Init = Null(exceptionType) },
        };
        if (hasResult) body.Add(new BirVariable { Name = result, Type = returnType });
        body.Add(new BirTry { Body = originalBody, Catches = new[] { new BirCatch {
            ExcType = new TypeNode.Fqn("System.Exception"), Var = caught,
            Body = new IBirStatement[] { Set(exception, Capture(caught, references, physicalMembers)) },
        } } });
        body.Add(new BirLabel { Id = cleanupLabel });
        body.AddRange(Clone(finallyBody));
        body.Add(If(new BirBinaryOperation { Op = "!=", Lhs = Local(exception), Rhs = Null(exceptionType) },
            new IBirStatement[] { Rethrow(exception, references, physicalMembers) }));
        body.AddRange(continuations.Select(value => (IBirStatement)Clone((BirGraphValue)value)));
        slot.Replace(new BirBlock { Body = body.ToArray() });
    }

    static BirNativeStatic Capture(string exception, ReferenceMetadataIndex references, bool physical)
    {
        var ownerType = new TypeNode.Fqn("System.Runtime.ExceptionServices.ExceptionDispatchInfo");
        var call = new BirNativeStatic { Type = ownerType, Method = "Capture",
            Args = new IBirExpression[] { Local(exception) }, ArgTypes = new TypeNode[] { new TypeNode.Fqn("System.Exception") },
            Ret = ownerType };
        if (physical)
        {
            var owner = references.ResolveRefType(ownerType.Name);
            var target = owner.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(method => method.Name == "Capture"
                && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType.FullName == "System.Exception");
            call.MemberRef = BirMemberReference.FromPhysical(ClrMemberResolution.MemberRefOf(target, "method", owner, null));
        }
        return call;
    }

    static BirBlock Rethrow(string exception, ReferenceMetadataIndex references, bool physical)
    {
        var ownerType = new TypeNode.Fqn("System.Runtime.ExceptionServices.ExceptionDispatchInfo");
        var call = new BirNativeInstance { Type = ownerType, Method = "Throw", Recv = Local(exception),
            Args = Array.Empty<IBirExpression>(), ArgTypes = Array.Empty<TypeNode>(), Ret = new TypeNode.Fqn("void") };
        if (physical)
        {
            var owner = references.ResolveRefType(ownerType.Name);
            var target = owner.GetMethods(BindingFlags.Public | BindingFlags.Instance).Single(method => method.Name == "Throw"
                && method.GetParameters().Length == 0);
            call.Dispatch = "callvirt";
            call.MemberRef = BirMemberReference.FromPhysical(ClrMemberResolution.MemberRefOf(target, "method", owner, null));
        }
        return new BirBlock { Body = new IBirStatement[] {
            new BirExpressionStatement { Expr = call }, new BirThrow { Value = Null(new TypeNode.Fqn("System.Exception")) },
        } };
    }

    static BirGraphValue Clone(BirGraphValue value) => value.Rewrite(node => node);
    static IBirStatement[] Clone(IBirStatement[] values) => values.Select(value => (IBirStatement)Clone((BirGraphValue)value)).ToArray();
    static BirLocal Local(string name) => new() { Name = name };
    static BirConstant Null(TypeNode type) => new() { Type = type, Value = new BirLiteral.NullLiteral() };
    static BirConstant Number(int value) => new() { Type = new TypeNode.Fqn("System.Int32"), Value = new BirLiteral.IntegerLiteral(value) };
    static BirSetLocal Set(string name, IBirExpression value) => new() { Name = name, Value = OptionalField<IBirExpression>.Of(value) };
    static BirIf If(IBirExpression condition, IBirStatement[] body) => new() { Branches = new[] {
        new BirBranch { Cond = OptionalField<IBirExpression>.Of(condition), Body = body },
    } };
}
