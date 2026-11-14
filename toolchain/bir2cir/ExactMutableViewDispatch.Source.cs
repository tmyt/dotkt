using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ExactMutableViewDispatch
{
    // One guard/evaluation plan, two owning renderers. The receiver is captured
    // before eligibility and the native test. The source renderer can then
    // capture ordinary arguments once before the selected branch reads them.
    static T RenderView<T>(ViewRenderer<T> render, T receiver, TypeNode.Fqn view, int family, TypeNode result,
        Func<T, T> direct, Func<T, T> fallback, TypeNode authoredSlot)
    {
        var name = CallEvalLowering.FreshBindingId();
        T Local() => render.Local(name);
        T Is(TypeNode type) => render.Is(type, Local());
        var native = Is(view);
        if (family == 4)
            native = render.Binary("&&", Is(new TypeNode.Fqn("System.Collections.Generic.ICollection`1", view.Args)),
                render.Binary("||", native, Is(new TypeNode.Fqn("DotKt.Runtime.CompilerServices.KotlinMutableSetClassifier"))));
        if (authoredSlot != null) native = render.Binary("&&", render.Not(Is(authoredSlot)), native);
        var condition = render.Binary("&&", render.NonNull(Local()),
            render.Binary("&&", render.Eligible(Local(), view, family), native));
        condition = render.CaptureCondition(condition);
        var isVoid = result is TypeNode.Fqn { Name: "kotlin.Unit" or "void" or "System.Void" };
        return render.Finish(name, receiver, condition, result, isVoid, direct(Local()), fallback(Local()));
    }

    abstract class ViewRenderer<T>
    {
        internal abstract T Local(string name);
        internal abstract T Is(TypeNode type, T value);
        internal abstract T Binary(string op, T left, T right);
        internal abstract T Not(T value);
        internal abstract T NonNull(T value);
        internal abstract T Eligible(T receiver, TypeNode.Fqn view, int family);
        internal virtual T CaptureCondition(T condition) => condition;
        internal abstract T Finish(string name, T receiver, T condition, TypeNode result, bool isVoid, T direct, T fallback);
    }


    internal static BirValueBlock Wrap(IBirExpression receiver, TypeNode.Fqn view, int family, TypeNode resultType,
        Func<BirLocal, IBirExpression> direct, Func<BirLocal, IBirExpression> fallback, TypeNode authoredSlot = null)
        => (BirValueBlock)RenderView(new SourceViewRenderer(), receiver, view, family, resultType,
            value => direct((BirLocal)value), value => fallback((BirLocal)value), authoredSlot);

    // Both dispatch branches use the same actual argument initializers. Cache
    // the already-evaluated guard before boxing these ordinary value-ABI slots;
    // moving them before the guard would change failure/evaluation order.
    internal static BirValueBlock Wrap(IBirExpression receiver, TypeNode.Fqn view, int family, TypeNode resultType,
        IReadOnlyList<IBirExpression> arguments,
        Func<BirLocal, IReadOnlyList<IBirExpression>, IBirExpression> direct,
        Func<BirLocal, IReadOnlyList<IBirExpression>, IBirExpression> fallback, TypeNode authoredSlot = null)
    {
        var renderer = new SourceViewRenderer(arguments);
        return (BirValueBlock)RenderView(renderer, receiver, view, family, resultType,
            value => direct((BirLocal)value, renderer.ArgumentReads()),
            value => fallback((BirLocal)value, renderer.ArgumentReads()), authoredSlot);
    }

    sealed class SourceViewRenderer : ViewRenderer<IBirExpression>
    {
        readonly IReadOnlyList<IBirExpression> _arguments;
        readonly List<IBirStatement> _postGuard = new();
        string[] _argumentNames;
        internal SourceViewRenderer(IReadOnlyList<IBirExpression> arguments = null) => _arguments = arguments;
        internal IReadOnlyList<IBirExpression> ArgumentReads()
            => _argumentNames.Select(name => (IBirExpression)new BirLocal { Name = name }).ToArray();
        static OptionalField<IBirExpression> E(IBirExpression value) => OptionalField<IBirExpression>.Of(value);
        static TypeNode Copy(TypeNode type) => BirGraphValue.CopyType(type);
        internal override IBirExpression Local(string name) => new BirLocal { Name = name };
        internal override IBirExpression Is(TypeNode type, IBirExpression value) => new BirIsInstance { Type = Copy(type), E = E(value) };
        internal override IBirExpression Binary(string op, IBirExpression left, IBirExpression right)
            // This owner runs after source short-circuit lowering. Its guard
            // therefore authors the explicit conditional directly, like the
            // retained DOM renderer, instead of leaving &&/|| for ilemit.
            => new BirConditional { Type = Copy(Bool), Cond = E(left),
                Then = E(op == "&&" ? right : new BirConstant { Type = Copy(Bool), Value = new BirLiteral.BooleanLiteral(true) }),
                Else = E(op == "||" ? right : new BirConstant { Type = Copy(Bool), Value = new BirLiteral.BooleanLiteral(false) }),
            };
        internal override IBirExpression Not(IBirExpression value) => new BirUnaryOperation { Op = "!", E = E(value) };
        internal override IBirExpression NonNull(IBirExpression value) => new BirBinaryOperation { Op = "!=", Lhs = E(value),
            Rhs = E(new BirConstant { Type = new TypeNode.Nullable(Copy(Any)), Value = new BirLiteral.NullLiteral() }) };
        internal override IBirExpression Eligible(IBirExpression receiver, TypeNode.Fqn view, int family)
            => new BirCallStatic { Owner = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.ExactMutableViewCacheKt"),
                Method = "canUseExactMutableView", TypeArgs = Array.Empty<TypeNode>(),
                Sig = new[] { Copy(Any), new TypeNode.Nullable(Copy(Any)), new TypeNode.Fqn("kotlin.Int") }, Ret = Copy(Bool),
                Args = new[] { receiver, new BirClassReference { Type = Copy(view) },
                    new BirConstant { Type = new TypeNode.Fqn("kotlin.Int"), Value = new BirLiteral.IntegerLiteral(family) } },
            };
        internal override IBirExpression CaptureCondition(IBirExpression condition)
        {
            if (_arguments == null) return condition;
            var guard = CallEvalLowering.FreshBindingId();
            _postGuard.Add(new BirVariable { Name = guard, Type = Copy(Bool), Init = E(condition) });
            _argumentNames = new string[_arguments.Count];
            for (var index = 0; index < _arguments.Count; index++)
            {
                var name = _argumentNames[index] = CallEvalLowering.FreshBindingId();
                // The reflection ABI already owns this boxed object slot; no
                // foreign declaration TV is interpreted as a caller variable.
                // The direct branch also reads this explicit boxed slot. Its
                // extra boxing cost belongs in the eventual paired benchmark;
                // it is not hidden by guessing an unstamped argument's type.
                _postGuard.Add(new BirVariable { Name = name, Type = new TypeNode.Nullable(Copy(Any)), Init = E(_arguments[index]) });
            }
            return new BirLocal { Name = guard };
        }
        internal override IBirExpression Finish(string name, IBirExpression receiver, IBirExpression condition,
            TypeNode result, bool isVoid, IBirExpression direct, IBirExpression fallback)
        {
            // A nested registered declaration remains the same occurrence; it
            // moves into this sole initializer rather than being cloned.
            var statements = new List<IBirStatement> { new BirVariable { Name = name, Type = Copy(Any), Init = E(receiver) } };
            statements.AddRange(_postGuard);
            IBirExpression expression;
            if (isVoid)
            {
                statements.Add(new BirIf { Branches = new[] {
                    new BirBranch { Cond = E(condition), Body = new IBirStatement[] { new BirExpressionStatement { Expr = E(direct) } } },
                    new BirBranch { Else = true, Body = new IBirStatement[] { new BirExpressionStatement { Expr = E(fallback) } } },
                } });
                expression = new BirStaticFieldGet { OwnerType = new TypeNode.Fqn("kotlin.Unit"), Name = "INSTANCE" };
            }
            else expression = new BirConditional { Type = Copy(result), Cond = E(condition),
                Then = E(new BirCast { Type = Copy(result), E = E(direct) }), Else = E(fallback) };
            return new BirValueBlock { Stmts = statements.ToArray(), Result = E(expression) };
        }
    }
}
