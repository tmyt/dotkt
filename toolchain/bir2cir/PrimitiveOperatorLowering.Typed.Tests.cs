using System;
using System.Collections.Generic;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class PrimitiveOperatorLowering
{
    internal static void TypedSelfTest()
    {
        var locals = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        BirCallStatic Intrinsic(string name, IBirExpression left, IBirExpression right) => new() {
            Owner = new TypeNode.Fqn(IntrinsicOwner), Method = name, Args = new[] { left, right },
        };
        BirLocal Local(string name, TypeNode type) => new() { Name = name, Sty = type };
        foreach (var owner in new[] { "kotlin.Int", "kotlin.Long", "kotlin.UInt", "kotlin.ULong" })
        foreach (var member in owner is "kotlin.UInt" or "kotlin.ULong"
                     ? new[] { "shl", "shr" } : new[] { "shl", "shr", "ushr" })
        {
            var distance = new BirCallStatic { Method = "distance", Args = Array.Empty<IBirExpression>() };
            var call = new BirCallInstance {
                OwnerType = new TypeNode.Fqn(owner), Method = member, Recv = new BirLocal { Name = "value" },
                Args = new IBirExpression[] { distance },
            };
            var expected = Lower(call.ToDom(), BirScope.Empty);
            var actual = (BirGraphValue)TypedOperator(call, locals);
            if (!System.Text.Json.Nodes.JsonNode.DeepEquals(expected, actual.ToDom())
                || !ReferenceEquals(call.Args.Value[0], distance))
                throw new InvalidOperationException("Typed primitive shift changed masking, evaluation count, or source ownership");
        }
        foreach (var right in new TypeNode[] { new TypeNode.Tv("method", 0), new TypeNode.Tv("method", 1),
                     new TypeNode.Tv("type", 0), new TypeNode.Nullable(new TypeNode.Tv("method", 0)) })
        {
            var left = new BirCallStatic { Method = "value", Sty = new TypeNode.Tv("method", 0), Ret = new TypeNode.Fqn("object") };
            var identity = Intrinsic("EQEQEQ", left, Local("other", right));
            if (TypedOperator(identity, locals) is not BirBinaryOperation { Op.Value: "===" } lowered
                || !System.Text.Json.Nodes.JsonNode.DeepEquals(((BirGraphValue)lowered.Lhs.Value).ToDom(), left.ToDom())
                || !System.Text.Json.Nodes.JsonNode.DeepEquals(((BirGraphValue)lowered.Rhs.Value).ToDom(), ((BirGraphValue)identity.Args.Value[1]).ToDom()))
                throw new InvalidOperationException("Typed identity lowering inferred an early physical generic slot");
        }
        foreach (var name in new[] { "kotlin.Double", "kotlin.Float" })
        foreach (var structural in new[] { false, true })
        foreach (var mixed in structural ? new[] { false } : new[] { false, true })
        {
            TypeNode element = new TypeNode.Fqn(name);
            var nullable = new TypeNode.Nullable(element);
            var left = new BirCallStatic { Method = "leftOperand", Sty = nullable };
            var right = new BirCallStatic { Method = "rightOperand", Sty = mixed ? element : nullable };
            var call = Intrinsic(structural ? "EQEQ" : "ieee754equals", left, right);
            if (TypedOperator(call, locals) is not BirValueBlock block || block.Stmts.Value.Length != 2
                || ((BirVariable)block.Stmts.Value[0]).Type.Value != nullable
                || ((BirVariable)block.Stmts.Value[1]).Type.Value != (mixed ? element : nullable))
                throw new InvalidOperationException("Typed nullable float equality lost its exact once-only operand storage");
            var helpers = 0;
            var calls = 0;
            block.Visit(node => {
                if (node is BirCallStatic target)
                {
                    if (target.Method.OrDefault() is "leftOperand" or "rightOperand") calls++;
                    if (target.Method.OrDefault() == (name == "kotlin.Double" ? "clrDoubleEquals" : "clrFloatEquals")) helpers++;
                }
            });
            if (calls != 2 || helpers != (structural ? 1 : 0))
                throw new InvalidOperationException("Typed float equality duplicated an operand or confused IEEE and total order");
        }
        var sourceType = new TypeNode.Fqn("kotlin.Int");
        var castOperand = new BirCast { Type = sourceType, E = new BirLocal { Name = "boxed", Sty = new TypeNode.Fqn("kotlin.Any") } };
        if (TypedOperator(Intrinsic("EQEQ", castOperand, Local("number", sourceType)), locals)
            is not BirBinaryOperation { Lhs.Value: BirCast { Type.Value: TypeNode.Fqn { Name: "kotlin.Int" } } })
            throw new InvalidOperationException("Typed primitive equality ignored the cast's source-surface type");

        var body = new BirBlock { Body = new IBirStatement[] {
            new BirVariable { Name = "number", Type = sourceType, Init = Integer(1) },
            new BirExpressionStatement { Expr = Intrinsic("EQEQ", new BirLocal { Name = "number" }, Integer(1)) },
        } };
        var rewritten = (BirBlock)Apply(body);
        if (((BirExpressionStatement)rewritten.Body.Value[1]).Expr.Value is not BirBinaryOperation { Op.Value: "==" })
            throw new InvalidOperationException("Typed primitive traversal did not receive the preceding local's lexical type");
        Console.WriteLine("[typed primitive operators] self-test OK (shift parity, identity slots, nullable IEEE/total-order, lexical surface)");
    }
}
