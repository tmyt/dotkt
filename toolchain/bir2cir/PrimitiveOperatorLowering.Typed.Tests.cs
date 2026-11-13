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
            var before = call.DeepClone();
            var actual = (BirGraphValue)TypedOperator(call, locals);
            var mask = owner is "kotlin.Long" or "kotlin.ULong" ? 63 : 31;
            var symbol = member == "shl" ? "<<" : member == "ushr" || owner is "kotlin.UInt" or "kotlin.ULong" ? ">>>" : ">>";
            if (actual is not BirBinaryOperation shifted || shifted.Op.Value != symbol
                || !((BirGraphValue)shifted.Lhs.Value).StructurallyEquals((BirGraphValue)call.Recv.Value)
                || shifted.Rhs.Value is not BirBinaryOperation { Op.Value: "&" } masked
                || !((BirGraphValue)masked.Lhs.Value).StructurallyEquals(distance)
                || masked.Rhs.Value is not BirConstant { Type.Value: TypeNode.Fqn { Name: "kotlin.Int" }, Value.Value: BirLiteral.IntegerLiteral literal }
                || literal.Value != mask || ReferenceEquals(masked.Lhs.Value, distance)
                || !call.StructurallyEquals(before) || !ReferenceEquals(call.Args.Value[0], distance))
                throw new InvalidOperationException("Typed primitive shift changed masking, evaluation count, or source ownership");
            var distances = 0;
            actual.Visit(node => { if (node is BirCallStatic { Method.Value: "distance" }) distances++; });
            if (distances != 1) throw new InvalidOperationException("Typed primitive shift evaluates its distance more than once");
        }
        var integer = new TypeNode.Fqn("kotlin.Int");
        var floating = new TypeNode.Fqn("kotlin.Double");
        void Check(BirGraphValue call, Func<BirGraphValue, bool> expected, string context)
        {
            var before = call.DeepClone();
            var actual = (BirGraphValue)TypedOperator(call, locals);
            if (actual == null || !expected(actual) || !call.StructurallyEquals(before))
                throw new InvalidOperationException("Typed primitive recognition changed " + context);
        }
        Check(new BirCallInstance { OwnerType = new TypeNode.Fqn("kotlin.String"), Method = "plus",
                Recv = new BirLocal { Name = "text" }, Args = new IBirExpression[] { Integer(1) } },
            node => node is BirConcat { Parts.Value: [BirLocal { Name.Value: "text" }, BirConstant { Value.Value: BirLiteral.IntegerLiteral { Value: 1 } }] },
            "string concatenation");
        Check(new BirCallInstance { OwnerType = new TypeNode.Fqn("kotlin.Byte"), Method = "unaryMinus", Recv = new BirLocal { Name = "byte" }, DynRet = integer },
            node => node is BirConvert { To.Value: TypeNode.Fqn { Name: "kotlin.Int" }, E.Value: BirUnaryOperation { Op.Value: "-", E.Value: BirLocal { Name.Value: "byte" } } },
            "narrow unary return conversion");
        Check(new BirCallInstance { OwnerType = new TypeNode.Fqn("kotlin.Byte"), Method = "inc", Recv = new BirLocal { Name = "byte" }, DynRet = new TypeNode.Fqn("kotlin.Byte") },
            node => node is BirConvert { To.Value: TypeNode.Fqn { Name: "kotlin.Byte" }, E.Value: BirBinaryOperation { Op.Value: "+", Lhs.Value: BirLocal { Name.Value: "byte" },
                Rhs.Value: BirConstant { Value.Value: BirLiteral.IntegerLiteral { Value: 1 } } } }, "narrow increment conversion");
        Check(Intrinsic("less", Local("nullable", new TypeNode.Nullable(integer)), Integer(1)),
            node => node is BirBinaryOperation { Op.Value: "<", Lhs.Value: BirNullableValue { Elem.Value: TypeNode.Fqn { Name: "kotlin.Int" }, E.Value: BirLocal { Name.Value: "nullable" } } },
            "nullable comparison operand");
        Check(Intrinsic("less", Local("boxed", new TypeNode.Fqn("kotlin.Any")), Integer(1)),
            node => node is BirBinaryOperation { Op.Value: "<", Lhs.Value: BirCast { Type.Value: TypeNode.Fqn { Name: "kotlin.Int" }, E.Value: BirLocal { Name.Value: "boxed" } } },
            "boxed comparison operand");
        Check(Intrinsic("EQEQ", Local("left", integer), Local("right", integer)),
            node => node is BirBinaryOperation { Op.Value: "==", Lhs.Value: BirCast { Type.Value: TypeNode.Fqn { Name: "kotlin.Int" }, E.Value: BirLocal { Name.Value: "left" } },
                Rhs.Value: BirCast { Type.Value: TypeNode.Fqn { Name: "kotlin.Int" }, E.Value: BirLocal { Name.Value: "right" } } }, "primitive equality boundaries");
        Check(Intrinsic("EQEQ", Local("left", floating), Local("right", floating)),
            node => node is BirCallStatic { Method.Value: "clrDoubleEquals", Args.Value: [BirLocal { Name.Value: "left" }, BirLocal { Name.Value: "right" }] }, "floating structural equality");
        Check(Intrinsic("EQEQ", Local("left", new TypeNode.Fqn("kotlin.Any")), Local("right", integer)),
            node => node is BirCallStatic { Method.Value: "kotlinEquals", Args.Value: [BirLocal { Name.Value: "left" }, BirLocal { Name.Value: "right" }] }, "null-safe object equality");
        Check(Intrinsic("ieee754equals", Local("left", floating), Local("right", floating)),
            node => node is BirBinaryOperation { Op.Value: "==", Lhs.Value: BirLocal { Name.Value: "left" }, Rhs.Value: BirLocal { Name.Value: "right" } }, "direct IEEE equality");
        foreach (var right in new TypeNode[] { new TypeNode.Tv("method", 0), new TypeNode.Tv("method", 1),
                     new TypeNode.Tv("type", 0), new TypeNode.Nullable(new TypeNode.Tv("method", 0)) })
        {
            var left = new BirCallStatic { Method = "value", Sty = new TypeNode.Tv("method", 0), Ret = new TypeNode.Fqn("object") };
            var identity = Intrinsic("EQEQEQ", left, Local("other", right));
            if (TypedOperator(identity, locals) is not BirBinaryOperation { Op.Value: "===" } lowered
                || !((BirGraphValue)lowered.Lhs.Value).StructurallyEquals(left)
                || !((BirGraphValue)lowered.Rhs.Value).StructurallyEquals((BirGraphValue)identity.Args.Value[1])
                || ReferenceEquals(lowered.Lhs.Value, left) || ReferenceEquals(lowered.Rhs.Value, identity.Args.Value[1]))
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
        Console.WriteLine("[typed primitive operators] self-test OK (shift masking, identity slots, primitive shapes, nullable IEEE/total-order, lexical surface)");
    }
}
