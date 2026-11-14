using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ExactMutableViewDispatch
{
    internal static void SelfTestSource()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning exact view dispatch: " + message); }
        foreach (var family in new[] { 4, 5, 6 })
        foreach (var isVoid in new[] { false, true })
        {
            var text = new TypeNode.Fqn("kotlin.String");
            var view = new TypeNode.Fqn(family == 4 ? "System.Collections.Generic.ISet`1"
                : family == 5 ? "System.Collections.Generic.ICollection`1" : "System.Collections.Generic.IList`1", new[] { text });
            var declaration = new BirFunctionDeclaration { Name = "captured", DeclarationId = "exact-view-owned-local",
                Params = Array.Empty<BirParameter>(), Ret = text, Body = new IBirStatement[] {
                    new BirReturn { Value = OptionalField<IBirExpression>.Of(new BirConstant { Type = text, Value = new BirLiteral.TextLiteral("value") }) },
                } };
            var receiver = new BirCallStatic { Owner = new TypeNode.Fqn("probe.Calls"), Method = "receiver", Ret = Any,
                Sig = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>() };
            var first = new BirValueBlock { Stmts = new IBirStatement[] { new BirLocalFunction { Id = "captured", Decl = declaration } },
                Result = OptionalField<IBirExpression>.Of(new BirConstant { Type = text, Value = new BirLiteral.TextLiteral("argument") }) };
            // A throwing argument still occupies its original position AFTER
            // the cached guard and preceding argument. No executable is cloned.
            var second = new BirThrowExpression { Value = OptionalField<IBirExpression>.Of(new BirLocal { Name = "failure" }) };
            var resultType = isVoid ? new TypeNode.Fqn("kotlin.Unit") : text;
            BirCallStatic Branch(string name, BirLocal recv, IReadOnlyList<IBirExpression> args) => new() {
                Owner = new TypeNode.Fqn("probe.Calls"), Method = name, Ret = resultType,
                Args = new[] { (IBirExpression)recv }.Concat(args).ToArray(),
            };
            var block = Wrap(receiver, view, family, resultType, new IBirExpression[] { first, second },
                (recv, args) => Branch("direct", recv, args), (recv, args) => Branch("fallback", recv, args),
                new TypeNode.Fqn("probe.AuthoredSlot"));
            var statements = block.Stmts.Value;
            Require(statements.Length == (isVoid ? 5 : 4)
                && statements[0] is BirVariable capture && ReferenceEquals(capture.Init.Value, receiver)
                && statements[1] is BirVariable { Type.HasValue: true, Init.Value: BirBinaryOperation { Op.Value: "&&" } } guard
                && guard.Type.Value == Bool
                && statements[2] is BirVariable argument0 && ReferenceEquals(argument0.Init.Value, first)
                && statements[3] is BirVariable argument1 && ReferenceEquals(argument1.Init.Value, second),
                "receiver/guard/argument order or actual operand identity changed");
            var receiverName = ((BirVariable)statements[0]).Name.Value;
            var guardName = ((BirVariable)statements[1]).Name.Value;
            var argumentNames = statements.Skip(2).Take(2).Cast<BirVariable>().Select(v => v.Name.Value).ToArray();
            IBirExpression condition, direct, fallback;
            if (isVoid)
            {
                var branches = ((BirIf)statements[4]).Branches.Value;
                Require(branches.Length == 2 && branches[1].Else.Value && !branches[1].Cond.IsPresent,
                    "Unit dispatch lost its explicit discard branches");
                condition = branches[0].Cond.Value;
                direct = ((BirExpressionStatement)branches[0].Body.Value.Single()).Expr.Value;
                fallback = ((BirExpressionStatement)branches[1].Body.Value.Single()).Expr.Value;
                Require(block.Result.Value is BirStaticFieldGet { Name.Value: "INSTANCE" }, "void branch did not materialize Unit");
            }
            else
            {
                var conditional = (BirConditional)block.Result.Value;
                condition = conditional.Cond.Value;
                direct = ((BirCast)conditional.Then.Value).E.Value;
                fallback = conditional.Else.Value;
                Require(conditional.Type.Value == resultType, "value dispatch lost its result carrier");
            }
            Require(condition is BirLocal local && local.Name.Value == guardName,
                "dispatch reevaluated eligibility after evaluating an argument");
            foreach (var call in new[] { (BirCallStatic)direct, (BirCallStatic)fallback })
                Require(call.Args.Value[0] is BirLocal recv && recv.Name.Value == receiverName
                    && call.Args.Value.Skip(1).Cast<BirLocal>().Select(value => value.Name.Value).SequenceEqual(argumentNames),
                    "branches did not read the same ordered argument initializers");
            Require(!ReferenceEquals(((BirCallStatic)direct).Args.Value[1], ((BirCallStatic)fallback).Args.Value[1]),
                "branch local reads shared a mutable occurrence");
            var declarations = 0;
            var receivers = 0;
            var throwingArguments = 0;
            var classifiers = 0;
            block.Visit(node => {
                if (ReferenceEquals(node, declaration)) declarations++;
                if (ReferenceEquals(node, receiver)) receivers++;
                if (ReferenceEquals(node, second)) throwingArguments++;
                if (node is BirIsInstance) classifiers++;
            });
            Require(declarations == 1 && receivers == 1 && throwingArguments == 1,
                "a registered declaration, receiver, or throwing argument was cloned/multiplied");
            Require(classifiers == (family == 4 ? 4 : 2), "set-family or authored-slot eligibility guard changed");
        }
        Console.WriteLine("[owning exact mutable view] self-test OK (families4/5/6, Unit/value, guard-before-arguments, actual declaration identity)");
    }
}
