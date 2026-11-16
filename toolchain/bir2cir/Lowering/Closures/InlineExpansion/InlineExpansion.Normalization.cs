using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class InlineExpansion
{
    static Dictionary<string, TypeNode> CollectVarTypes(BirGraphValue root)
    {
        var types = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        root?.Visit(node => {
            if (node is BirVariable { Name.HasValue: true, Type.HasValue: true } variable) types[variable.Name.Value] = variable.Type.Value;
        });
        return types;
    }

    static BirGraphValue RewriteOwning(BirGraphValue root, Func<BirGraphValue, BirGraphValue> rewrite, bool postOrder = false)
    {
        if (root == null || root is IBirExecutablePayload) return root;
        if (!postOrder) root = rewrite(root);
        root.VisitChildSlots(slot => slot.Replace(RewriteOwning(slot.Value, rewrite, postOrder)));
        return postOrder ? rewrite(root) : root;
    }

    BirGraphValue NormalizeConcretizedCasts(BirGraphValue root)
    {
        var types = CollectVarTypes(root);
        return RewriteOwning(root, node => {
            if (node is BirCast cast && cast.E.OrDefault() is BirLocal { Name.HasValue: true } local
                && cast.Type.OrDefault() is TypeNode.Fqn target && Pass.References.IsValueType(target)
                && types.TryGetValue(local.Name.Value, out var declared)
                && declared is TypeNode.Nullable { Of: TypeNode.Fqn element } && element == target)
                return new BirNullableValue { Elem = CopyType(target), E = cast.E };
            return node;
        });
    }

    void NormalizeImplicitNullableUnwrap(BirGraphValue root)
    {
        void Find(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return;
            if (HasBody(node)) NormalizeFlowsWith(node, CollectVarTypes(node));
            else node.VisitChildren(Find);
        }
        Find(root);
    }

    // The first body-bearing occurrence owns its whole subtree's flat var
    // inventory; nested bodies do not start independent retyping scopes.
    static bool HasBody(BirGraphValue node) => node switch {
        BirFunctionDeclaration value => value.Body.HasValue,
        BirConstructor value => value.Body.HasValue,
        BirClosureDefinition value => value.Body.HasValue,
        BirInlineLambda value => value.Body.HasValue,
        BirNewSuspendLambda value => value.Body.HasValue,
        BirWhile value => value.Body.HasValue,
        BirTry value => value.Body.HasValue,
        BirCatch value => value.Body.HasValue,
        BirBranch value => value.Body.HasValue,
        BirBlock value => value.Body.HasValue,
        BirForArray value => value.Body.HasValue,
        BirForIn value => value.Body.HasValue,
        BirFor value => value.Body.HasValue,
        BirForRange value => value.Body.HasValue,
        BirForEach value => value.Body.HasValue,
        BirRepeat value => value.Body.HasValue,
        _ => false,
    };

    void NormalizeFlowsWith(BirGraphValue root, Dictionary<string, TypeNode> types)
    {
        RewriteOwning(root, node => {
            switch (node)
            {
                case BirConditional conditional when VtFqnOf(conditional.Type.OrDefault()) is { } target:
                    if (conditional.Then.HasValue && UnwrapNullableLocal(conditional.Then.Value, target, types) is { } thenValue)
                        conditional.Then = OptionalField<IBirExpression>.Of(thenValue);
                    if (conditional.Else.HasValue && UnwrapNullableLocal(conditional.Else.Value, target, types) is { } elseValue)
                        conditional.Else = OptionalField<IBirExpression>.Of(elseValue);
                    break;
                case BirVariable variable when VtFqnOf(variable.Type.OrDefault()) is { } target:
                    if (variable.Init.HasValue && UnwrapNullableLocal(variable.Init.Value, target, types) is { } initial)
                        variable.Init = OptionalField<IBirExpression>.Of(initial);
                    break;
                case BirSetLocal { Name.HasValue: true, Value.HasValue: true } assignment when types.TryGetValue(assignment.Name.Value, out var slot)
                    && VtFqnOf(slot) is { } target:
                    if (UnwrapNullableLocal(assignment.Value.Value, target, types) is { } value)
                        assignment.Value = OptionalField<IBirExpression>.Of(value);
                    break;
                case BirCast { E.HasValue: true } cast when VtFqnOf(cast.Type.OrDefault()) is { } target:
                    if (UnwrapNullableLocal(cast.E.Value, target, types) != null)
                        return new BirNullableValue { Elem = CopyType(target), E = cast.E };
                    break;
            }
            return node;
        });
    }

    static IBirExpression ValueProducer(IBirExpression value)
    {
        while (true)
        {
            if (value is BirValueBlock { Result.HasValue: true } block) value = block.Result.Value;
            else if (value is BirCallEvaluation { Expr.HasValue: true } evaluation) value = evaluation.Expr.Value;
            else return value;
        }
    }

    static readonly HashSet<string> CovariantClasses = new(StringComparer.Ordinal) { "kotlin.Pair", "kotlin.Triple" };
    void WidenCovariantConstruction(BirGraphValue root) => root.Visit(node => {
        if (node is BirVariable { Init.HasValue: true } variable
            && variable.Type.OrDefault() is TypeNode.Fqn { Args: { } wide } declared
            && CovariantClasses.Contains(declared.Name) && !wide.Any(HasTvType)
            && ValueProducer(variable.Init.Value) is BirNew created
            && created.Type.OrDefault() is TypeNode.Fqn { Args: { } narrow } construction
            && construction.Name == declared.Name && narrow.Length == wide.Length
            && !narrow.Select((value, index) => TypeNodeDataEquality.Equal(value, wide[index])).All(equal => equal)
            && narrow.All(type => type is TypeNode.Fqn named && !Pass.References.IsValueType(named)))
            created.Type = construction with { Args = wide.Select(CopyType).ToArray() };
    });

    void RetypeReceiverToConcrete(BirFileDeclaration root)
    {
        foreach (var method in root.Methods.OrEmpty().Concat(root.Types.OrEmpty().SelectMany(type => type.Methods.OrEmpty())))
        {
            var scope = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            foreach (var parameter in method.Params.OrEmpty())
                if (parameter.Name.HasValue && parameter.Type.HasValue) scope[parameter.Name.Value] = parameter.Type.Value;
            var body = new BirValueBlock { Stmts = method.Body.OrEmpty() };
            foreach (var pair in CollectVarTypes(body)) scope[pair.Key] = pair.Value;
            RewriteOwning(body, node => {
                if (node is BirVariable { Name.HasValue: true, Type.HasValue: true, Init.HasValue: true } variable
                    && CondArmsAgree(variable.Init.Value, scope)
                    && InvariantCollArgs(variable.Type.Value) is { } declared
                    && BirGraphStaticType.Surface(variable.Init.Value, scope) is { } argument
                    && InvariantCollArgs(argument) is { } arguments && arguments.Length == declared.Length
                    && !HasTvType(argument) && !arguments.SequenceEqual(declared))
                {
                    variable.Type = argument;
                    scope[variable.Name.Value] = argument;
                }
                return node;
            }, postOrder: true);
        }
    }

    static bool CondArmsAgree(IBirExpression expression, IReadOnlyDictionary<string, TypeNode> scope)
    {
        if (ValueProducer(expression) is not BirConditional conditional) return true;
        var first = BirGraphStaticType.Surface(conditional.Then.OrDefault(), scope);
        var second = BirGraphStaticType.Surface(conditional.Else.OrDefault(), scope);
        return first != null && second != null && first == second;
    }
    static TypeNode[] InvariantCollArgs(TypeNode type) => type switch {
        TypeNode.Nullable value => InvariantCollArgs(value.Of), TypeNode.Oblivious value => InvariantCollArgs(value.Of),
        TypeNode.Fqn { Args: { } arguments } named when MapVarianceRealign.InvariantCollections.Contains(named.Name) => arguments,
        _ => null,
    };
    internal static bool HasTvType(TypeNode type) => type switch {
        TypeNode.Tv => true,
        TypeNode.Nullable value => HasTvType(value.Of), TypeNode.Oblivious value => HasTvType(value.Of),
        TypeNode.Projection value => HasTvType(value.Of), TypeNode.Array value => HasTvType(value.Elem),
        TypeNode.ByRef value => HasTvType(value.Of), TypeNode.Ptr value => HasTvType(value.Of),
        TypeNode.Mod value => HasTvType(value.M) || HasTvType(value.Of),
        TypeNode.Fn value => HasTvType(value.Ret) || value.DelegateParams.Any(HasTvType),
        TypeNode.Fqn value => value.Args?.Any(HasTvType) == true, _ => false,
    };
    TypeNode.Fqn VtFqnOf(TypeNode type) => type is TypeNode.Fqn named && Pass.References.IsValueType(named) ? named : null;

    static IBirExpression UnwrapNullableLocal(IBirExpression value, TypeNode.Fqn target, IReadOnlyDictionary<string, TypeNode> types)
    {
        if (value is not BirLocal { Name.HasValue: true } local || !local.Name.Value.Contains("__inl", StringComparison.Ordinal)
            || !types.TryGetValue(local.Name.Value, out var declared)
            || declared is not TypeNode.Nullable { Of: TypeNode.Fqn element } || element != target) return null;
        return new BirNullableValue { Elem = CopyType(target), E = OptionalField<IBirExpression>.Of(Copy(value)) };
    }
}
