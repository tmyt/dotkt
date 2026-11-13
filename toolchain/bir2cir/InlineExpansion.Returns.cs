using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class InlineExpansion
{
    internal (IBirStatement[] Body, IBirExpression Result) RouteReturns(
        IBirStatement[] body, bool unit, TypeNode returnType, string prefix)
    {
        if (!HasEarlyReturn(body))
        {
            if (!unit && body.LastOrDefault() is BirReturn { Value.HasValue: true } tail)
                return (body[..^1], HygieneClone(tail.Value.Value));
            if (!unit) return (body, TerminalNothing());
            if (body.LastOrDefault() is BirReturn unitTail)
            {
                var statements = body[..^1].ToList();
                if (unitTail.Value.OrDefault() is { } value && value is not BirConstant)
                    statements.Add(new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(HygieneClone(value)) });
                body = statements.ToArray();
            }
            return (body, UnitConst());
        }
        var end = Pass.NextLabel();
        var resultName = unit ? null : prefix + "ret";
        var routed = RouteStatementVector(body, resultName, end).ToList();
        if (!unit)
        {
            var type = BirGraphValue.CopyType(returnType ?? new TypeNode.Fqn("kotlin.Unit"));
            routed.Insert(0, new BirVariable {
                Name = resultName, Type = type,
                Init = OptionalField<IBirExpression>.Of(new BirDefaultValue { Type = BirGraphValue.CopyType(type) }),
            });
        }
        routed.Add(new BirLabel { Id = end });
        return (routed.ToArray(), unit ? UnitConst() : new BirLocal { Name = resultName });
    }

    static IBirExpression TerminalNothing() => new BirThrowExpression {
        Value = OptionalField<IBirExpression>.Of(new BirConstant {
            Type = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Nothing")), Value = new BirLiteral.NullLiteral(),
        }),
    };

    static bool IsReturnBoundary(BirGraphValue node) => node is BirNewClosure or BirNewDelegate or BirNewSam or BirNewSuspendLambda;

    static bool HasEarlyReturn(IBirStatement[] body)
    {
        bool Found(BirGraphValue node)
        {
            if (IsReturnBoundary(node) || node is IBirExecutablePayload) return false;
            if (node is BirReturn or BirReturnExpression) return true;
            var found = false;
            node.VisitChildren(child => found |= Found(child));
            return found;
        }
        for (var index = 0; index < body.Length; index++)
        {
            var node = (BirGraphValue)body[index];
            if (index == body.Length - 1 && node is BirReturn tail)
            {
                if (tail.Value.HasValue && Found((BirGraphValue)tail.Value.Value)) return true;
            }
            else if (Found(node)) return true;
        }
        return false;
    }

    IBirStatement[] RouteStatementVector(IBirStatement[] statements, string resultName, int end)
    {
        var result = new List<IBirStatement>();
        var resized = false;
        foreach (var statement in statements)
        {
            if (statement is BirReturn returned)
            {
                resized = true;
                var value = returned.Value.HasValue ? RouteReturnValue(returned.Value.Value, resultName, end) : null;
                AppendTransfer(result, value, resultName, end);
            }
            else
            {
                RouteReturnsInNode((BirGraphValue)statement, resultName, end);
                result.Add(statement);
            }
        }
        return resized ? result.ToArray() : statements;
    }

    static void AppendTransfer(List<IBirStatement> statements, IBirExpression value, string resultName, int end)
    {
        if (value != null && resultName != null)
            statements.Add(new BirSetLocal { Name = resultName, Value = OptionalField<IBirExpression>.Of(HygieneClone(value)) });
        else if (value != null && value is not BirConstant)
            statements.Add(new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(HygieneClone(value)) });
        statements.Add(new BirGoto { Id = end });
    }

    IBirExpression RouteReturnValue(IBirExpression value, string resultName, int end)
    {
        if (value is BirReturnExpression returned)
        {
            var nested = returned.Value.HasValue ? RouteReturnValue(returned.Value.Value, resultName, end) : null;
            var statements = new List<IBirStatement>();
            AppendTransfer(statements, nested, resultName, end);
            return new BirValueBlock {
                Stmts = statements.ToArray(),
                Result = OptionalField<IBirExpression>.Of(new BirThrowExpression {
                    Value = OptionalField<IBirExpression>.Of(UnitConst()),
                }),
            };
        }
        RouteReturnsInNode((BirGraphValue)value, resultName, end);
        return value;
    }

    void RouteReturnsInNode(BirGraphValue node, string resultName, int end)
    {
        if (node == null || IsReturnBoundary(node) || node is IBirExecutablePayload) return;
        node.VisitSourceChildren(slot => {
            if (slot.Value is IBirExpression expression)
                slot.Replace((BirGraphValue)RouteReturnValue(expression, resultName, end));
            else RouteReturnsInNode(slot.Value, resultName, end);
        }, region => {
            var rewritten = RouteStatementVector(region.Statements, resultName, end);
            if (!ReferenceEquals(rewritten, region.Statements)) region.ReplaceStatements!(rewritten);
            if (region.Result is { } result)
                result.Replace((BirGraphValue)RouteReturnValue((IBirExpression)result.Value, resultName, end));
        });
    }
}
