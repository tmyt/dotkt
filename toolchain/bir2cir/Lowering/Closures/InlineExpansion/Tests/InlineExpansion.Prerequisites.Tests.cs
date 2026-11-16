using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class InlineExpansion
{
    internal static void SelfTestPrerequisites()
    {
        var boolean = new TypeNode.Fqn("kotlin.Boolean");
        var integer = new TypeNode.Fqn("kotlin.Int");
        var actionType = new TypeNode.Fn(false, new TypeNode.Fqn("kotlin.Unit"), new TypeNode[] { integer });
        BirCallStatic Helper(string name, params IBirExpression[] arguments) => new() {
            Owner = OptionalField<TypeNode>.Null, Method = name, Args = arguments,
        };
        BirExpressionStatement Statement(IBirExpression expression) => new() { Expr = OptionalField<IBirExpression>.Of(expression) };
        var todo = Statement(Helper("TODO", new BirLocal { Name = "ignoredReason" }));
        var require = Helper("require", new BirLocal { Name = "condition" }, new BirLocal { Name = "ignoredLazyMessage" });
        require.Sig = new TypeNode[] { boolean, actionType };
        var required = Statement(require);
        var unsigned = Helper("requireNotNull", new BirLocal { Name = "unsigned" });
        unsigned.TypeArgs = new TypeNode[] { new TypeNode.Fqn("kotlin.UInt") };
        var nonnull = Statement(unsigned);
        var absentOwner = Helper("TODO"); absentOwner.Owner = default;
        var property = Helper("TODO"); property.Prop = OptionalField<string>.Null;
        var excludedAbsent = Statement(absentOwner);
        var excludedProperty = Statement(property);
        var repeat = Helper("repeat", new BirCallStatic { Method = "countSource" }, new BirCallStatic { Method = "actionSource" });
        repeat.Sig = new TypeNode[] { integer, actionType };
        var repeated = Statement(repeat);
        var parameter = new BirParameter { Name = "parameter", Type = integer };
        var method = new BirFunctionDeclaration {
            Name = "method", Static = true, Params = new[] { parameter }, Ret = new TypeNode.Fqn("kotlin.Unit"),
            Body = new IBirStatement[] { todo, required, nonnull, excludedAbsent, excludedProperty, repeated },
        };
        var type = new BirClassDefinition { Kind = "class", Name = "Owner", Methods = new[] { method } };
        var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { type } };
        var methods = type.Methods.Value;
        var parameters = method.Params.Value;
        var body = method.Body.Value;
        var names = new HashSet<string>(StringComparer.Ordinal);
        PreconditionLowering.Apply(file, names, false);
        RepeatInlineLowering.Apply(file, names, false);
        if (!ReferenceEquals(file.Types.Value[0], type) || !ReferenceEquals(type.Methods.Value, methods)
            || !ReferenceEquals(method.Params.Value, parameters) || !ReferenceEquals(method.Params.Value[0], parameter)
            || !ReferenceEquals(method.Body.Value, body))
            throw new InvalidOperationException("Typed inline prerequisite recognition replaced continuing declarations");
        if (todo.Expr.Value is not BirThrowExpression { Value.HasValue: true } todoThrow
            || todoThrow.Value.Value is not BirNew todoException
            || todoException.Type.Value != new TypeNode.Fqn("kotlin.NotImplementedError")
            || todoException.ArgTypes.Value[0] != new TypeNode.Fqn("kotlin.String")
            || todoException.Args.Value[0] is not BirConstant { Value.Value: BirLiteral.TextLiteral { Value: "An operation is not implemented." } }
            || required.Expr.Value is not BirConditional { Cond.Value: BirLocal { Name.Value: "condition" } }
            || nonnull.Expr.Value is not BirValueBlock { Stmts.Value: [BirVariable], Result.Value: BirConditional { Cond.Value: BirNullableHasValue, Then.Value: BirNullableValue } }
            || !ReferenceEquals(excludedAbsent.Expr.Value, absentOwner) || !ReferenceEquals(excludedProperty.Expr.Value, property))
            throw new InvalidOperationException("Typed preconditions changed helper selection, TODO message or nullable unsigned representation");
        if (repeated.Expr.Value is not BirValueBlock loop || loop.Stmts.Value is not [BirVariable count, BirVariable action]
            || count.Init.Value is not BirCallStatic { Method.Value: "countSource" }
            || action.Init.Value is not BirCallStatic { Method.Value: "actionSource" }
            || loop.Result.Value is not BirRepeat { Count.Value: BirLocal } repeatedLoop
            || ((BirLocal)repeatedLoop.Count.Value).Name.Value != count.Name.Value
            || repeatedLoop.Body.Value is not [BirExpressionStatement { Expr.Value: BirDelegateInvoke invoke }]
            || ((BirLocal)invoke.Recv.Value).Name.Value != action.Name.Value
            || ((BirLocal)invoke.Args.Value[0]).Name.Value != repeatedLoop.Var.Value)
            throw new InvalidOperationException("Typed repeat recognition lost count-before-action single evaluation or selected function shape");
        var shadowed = Statement(Helper("TODO"));
        var shadowFile = new BirFileDeclaration { Methods = new[] {
            new BirFunctionDeclaration { Name = "shadowed", Static = true, Params = Array.Empty<BirParameter>(),
                Ret = new TypeNode.Fqn("kotlin.Unit"), Body = new IBirStatement[] { shadowed } },
        } };
        PreconditionLowering.Apply(shadowFile, new HashSet<string> { "TODO" }, true);
        if (shadowed.Expr.Value is not BirCallStatic)
            throw new InvalidOperationException("Typed preconditions ignored an application-local helper shadow");
        Console.WriteLine("[typed inline prerequisites] self-test OK (owner presence, declaration identity, unsigned nullability, evaluation order)");
    }
}
