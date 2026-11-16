using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    static IBirExpression SourceArrayReceiver(BirGraphValue node) => node switch {
        BirArrayGet value => value.Array.OrDefault(), BirArraySet value => value.Array.OrDefault(),
        BirArrayLength value => value.Array.OrDefault(), BirForArray value => value.Array.OrDefault(), _ => null,
    };

    static TypeNode SourceArrayElement(BirGraphValue node) => node switch {
        BirNewArray value => value.Elem.OrDefault(), BirNewArrayInit value => value.Elem.OrDefault(),
        BirNewArraySized value => value.Elem.OrDefault(), BirSpreadConcat value => value.Elem.OrDefault(),
        BirArrayGet value => value.Elem.OrDefault(), BirArraySet value => value.Elem.OrDefault(),
        BirForArray value => value.Elem.OrDefault(), _ => null,
    };

    static void SetSourceArrayElement(BirGraphValue node, TypeNode type)
    {
        var copy = BirGraphValue.CopyType(type);
        switch (node)
        {
            case BirNewArray value: value.Elem = copy; break;
            case BirNewArrayInit value: value.Elem = copy; break;
            case BirNewArraySized value: value.Elem = copy; break;
            case BirSpreadConcat value: value.Elem = copy; break;
            case BirArrayGet value: value.Elem = copy; break;
            case BirArraySet value: value.Elem = copy; break;
            case BirForArray value: value.Elem = copy; break;
        }
    }

    static BirGraphValue BindProjectedArrayReadSource(BirGraphValue node, SourceRewriteState state)
    {
        if (node is not BirArrayGet read || state.ProjectedRead(read)) return node;
        TypeNode semantic, physical;
        if (state.ArrayProjection(read.Array.OrDefault() as BirGraphValue) is { } flowed
            && SourceCarrierExpressionType(read.Array.OrDefault() as BirGraphValue) is TypeNode.Array flowArray)
        { semantic = flowed; physical = flowArray.Elem; }
        else if (SourceCarrierExpressionType(read.Array.OrDefault() as BirGraphValue) is TypeNode.Array { Elem: TypeNode.Projection projection })
        { semantic = state.Projection.Rewrite(projection.Of); physical = state.Projection.Rewrite(projection); }
        else return node;
        if (semantic.Equals(physical) || !IsExistentialPhysicalCarrier(physical, state.Owners, state.References)) return node;
        state.MarkProjectedRead(read);
        var result = new BirCast { Type = BirGraphValue.CopyType(physical),
            E = OptionalField<IBirExpression>.Of(read), _exactBridgeCast = true };
        state.ProjectionFact(result, semantic);
        return result;
    }

    static BirGraphValue BindProjectedArrayGenericCallSource(BirGraphValue node, SourceRewriteState state)
    {
        var (signature, arguments, typeArguments, result) = node switch {
            BirCallStatic value => (value.Sig.OrDefault(), value.Args.OrDefault(), value.TypeArgs.OrDefault(), value.Ret.OrDefault()),
            BirCallInstance value => (value.Sig.OrDefault(), value.Args.OrDefault(), value.TypeArgs.OrDefault(), value.Ret.OrDefault()),
            _ => (null, null, null, (TypeNode)null),
        };
        if (signature == null || arguments == null || typeArguments == null || signature.Length != arguments.Length) return node;
        TypeNode semanticResult = null, physicalResult = null;
        for (var index = 0; index < signature.Length; index++)
        {
            if (signature[index] is not TypeNode.Array declaration) continue;
            var variable = declaration.Elem switch { TypeNode.Projection { Of: TypeNode.Tv { Scope: "method" } value } => value,
                TypeNode.Tv { Scope: "method" } value => value, _ => null };
            if (variable == null || variable.I < 0 || variable.I >= typeArguments.Length) continue;
            var exact = StripSourceNullability(typeArguments[variable.I]);
            TypeNode semantic, physical;
            if (state.ArrayProjection((BirGraphValue)arguments[index]) is { } flowed
                && SourceCarrierExpressionType((BirGraphValue)arguments[index]) is TypeNode.Array flowedArray)
            { semantic = StripSourceNullability(flowed); physical = flowedArray.Elem; }
            else if (SourceCarrierExpressionType((BirGraphValue)arguments[index]) is TypeNode.Array { Elem: TypeNode.Projection projection })
            { semantic = StripSourceNullability(projection.Of); physical = state.Projection.Rewrite(projection); }
            else continue;
            if (exact == null || semantic == null || SupertypeGraph.TypeKey(exact) != SupertypeGraph.TypeKey(semantic)
                || !IsExistentialPhysicalCarrier(physical, state.Owners, state.References)) continue;
            typeArguments[variable.I] = BirGraphValue.CopyType(physical);
            semanticResult ??= result;
            var projectedResult = ReplaceEquivalentType(semanticResult, exact, physical);
            if (projectedResult != semanticResult) physicalResult = projectedResult;
        }
        if (semanticResult == null || physicalResult == null) return node;
        if (physicalResult is TypeNode.Array { Elem: var physicalElement } && semanticResult is TypeNode.Array semanticArray
            && IsExistentialPhysicalCarrier(physicalElement, state.Owners, state.References))
        {
            SetSourceCallResult(node, physicalResult);
            state.ArrayProjection(node, semanticArray.Elem is TypeNode.Projection projection ? projection.Of : semanticArray.Elem);
            return node;
        }
        return state.AlignExistential((IBirExpression)node, physicalResult, semanticResult);
    }

    static void RewriteSourceVariantArrays(BirGraphValue node, bool writable,
        TypeNode.Fqn writableSemantic, TypeNode.Fqn writablePhysical, SourceRewriteState state)
    {
        if (node is BirNewArray array && array.Elems.HasValue && state.TryArrayOwner(array, out var arrayOwner)
            && state.Projection.TryExistentialCarrier(arrayOwner.Name, out var carrier))
        { array.Elem = new TypeNode.Fqn(carrier); state.ArrayProjection(array, arrayOwner); }
        if (node is BirSpreadConcat spread && state.TrySpreadOwner(spread, out var spreadOwner)
            && state.Projection.TryExistentialCarrier(spreadOwner.Name, out var spreadCarrier))
            RewriteSourceSpread(spread, spreadOwner, new TypeNode.Fqn(spreadCarrier), state);
        if (node is BirNewArrayInit initializer && state.TryInitializerOwner(initializer, out var initializerOwner)
            && state.Projection.TryExistentialCarrier(initializerOwner.Name, out var initializerCarrier))
            RewriteSourceInitializer(initializer, initializerOwner, new TypeNode.Fqn(initializerCarrier), state);
        if (!writable) return;
        if (node is BirNewArrayInit writableInitializer) RewriteSourceInitializer(writableInitializer, writableSemantic, writablePhysical, state);
        else if (node is BirSpreadConcat writableSpread) RewriteSourceSpread(writableSpread, writableSemantic, writablePhysical, state);
        else
        {
            SetSourceArrayElement(node, writablePhysical);
            if (node is BirNewArray or BirNewArraySized) state.ArrayProjection(node, writableSemantic);
            if (node is BirForArray loop && loop.Var.HasValue && loop.Body.HasValue)
            {
                void Mark(BirGraphValue value)
                {
                    if (value is IBirExecutablePayload) return;
                    if (value is BirLocal local && local.Name.OrDefault() == loop.Var.Value)
                    { local.Sty = BirGraphValue.CopyType(writablePhysical); state.ProjectionFact(local, writableSemantic); }
                    value.VisitChildren(Mark);
                }
                foreach (var statement in loop.Body.Value) Mark((BirGraphValue)statement);
            }
        }
    }

    static BirFunctionDeclaration SourceLocalDelegateTarget(BirNewArrayInit initializer, SourceRewriteState state)
    {
        var selected = initializer.Init.OrDefault() switch {
            BirNewDelegate value => (value.CalleeOwner.OrDefault() as TypeNode.Fqn, value.Method.OrDefault()),
            BirNewClosure value => (value.ClosureType.OrDefault() as TypeNode.Fqn, value.Method.OrDefault()), _ => default,
        };
        return selected.Item1 != null && selected.Item2 != null
            && state.Realization.Bindings.SourceLocalMethods.TryGetValue(selected.Item1.Name + "\0" + selected.Item2, out var methods)
            ? UniqueLocalDelegateTarget(methods)?.Source as BirFunctionDeclaration : null;
    }

    static void RewriteSourceInitializer(BirNewArrayInit array, TypeNode.Fqn semantic, TypeNode.Fqn physical, SourceRewriteState state)
    {
        array.Elem = BirGraphValue.CopyType(physical);
        state.ArrayProjection(array, semantic);
        if (array.Init.OrDefault() is not BirGraphValue initializer) return;
        TypeNode functionType = null;
        initializer.VisitOwnTypeFields((field, type) => { if (field == "funcType") functionType = type; });
        if (functionType is not TypeNode.Fn function) return;
        var projectedFunction = new TypeNode.Fn(function.Suspend, physical, function.Params, function.Recv, function.Clr, function.Ctx);
        initializer.RewriteOwnTypeFieldsInPlace((field, _, type) => field == "funcType" ? BirGraphValue.CopyType(projectedFunction) : type);
        array.FuncType = BirGraphValue.CopyType(projectedFunction);
        if (SourceLocalDelegateTarget(array, state) is not { } method) return;
        if (method.Ret.HasValue) PreserveNormalizedSourceReturn(method, method.Ret.Value, physical, state);
        else { method.Ret = BirGraphValue.CopyType(physical); state.MarkNormalized(method); }
        foreach (var statement in method.Body.OrEmpty())
            if (statement is BirReturn result && result.Value.HasValue)
                result.Value = OptionalField<IBirExpression>.Of(RetypeSourceCovariantResult(result.Value.Value, semantic, physical, state));
    }

    static IBirExpression RetypeSourceCovariantResult(IBirExpression expression, TypeNode.Fqn semantic,
        TypeNode.Fqn physical, SourceRewriteState state)
    {
        if (expression is BirValueBlock block)
        {
            block.Type = BirGraphValue.CopyType(physical);
            if (block.Result.HasValue) block.Result = OptionalField<IBirExpression>.Of(RetypeSourceCovariantResult(block.Result.Value, semantic, physical, state));
            return block;
        }
        if (expression is BirConditional conditional)
        {
            conditional.Type = BirGraphValue.CopyType(physical);
            if (conditional.Then.HasValue) conditional.Then = OptionalField<IBirExpression>.Of(RetypeSourceCovariantResult(conditional.Then.Value, semantic, physical, state));
            if (conditional.Else.HasValue) conditional.Else = OptionalField<IBirExpression>.Of(RetypeSourceCovariantResult(conditional.Else.Value, semantic, physical, state));
            return conditional;
        }
        if (expression is BirCast cast)
        {
            if (StripSourceNullability(SourceCarrierExpressionType(cast.E.OrDefault() as BirGraphValue)) is TypeNode.Fqn source
                && state.ProjectArguments(source, semantic.Name) is { } arguments && !state.ClrVariance(semantic, arguments))
                cast.Type = BirGraphValue.CopyType(physical);
            return cast;
        }
        if (StripSourceNullability(SourceCarrierExpressionType((BirGraphValue)expression)) is TypeNode.Fqn value
            && value.Name != physical.Name && state.ProjectArguments(value, semantic.Name) != null)
            return new BirCast { Type = BirGraphValue.CopyType(physical), E = OptionalField<IBirExpression>.Of(expression), _exactBridgeCast = true };
        return expression;
    }

    static void RewriteSourceSpread(BirSpreadConcat spread, TypeNode.Fqn semantic, TypeNode physical, SourceRewriteState state)
    {
        void Rewrite(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            node.RewriteOwnFieldsInPlace((_, _, type) => ReplaceEquivalentType(type, semantic, physical), (_, _, slot) => Rewrite(slot.Value));
        }
        Rewrite(spread);
        spread.Elem = BirGraphValue.CopyType(physical);
        state.ArrayProjection(spread, semantic);
    }
}
