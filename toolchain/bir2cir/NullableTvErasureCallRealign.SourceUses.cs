using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableTvErasureCallRealign
{
    internal static BirFileDeclaration ApplySourceUses(BirFileDeclaration file, DeclIndex index,
        ValueTypeOracle isValue, ReferenceMetadataIndex references)
    {
        _isValue = isValue ?? (_ => false);
        _refs = references;
        _sourceApplicationOrder = index.SourceApplicationOrder;
        foreach (var method in file.Methods.OrEmpty()) SourceUseMethod(method, index, null);
        foreach (var owner in file.Types.OrEmpty())
        {
            var frame = NullableRepresentationViews.TypeFacts((BirGraphValue)owner)?.NullableFrame;
            foreach (var constructor in owner.Ctors.OrEmpty())
            {
                var context = SourceUseContext(index, constructor.Params.OrEmpty(), null, frame, null);
                var baseOwner = owner.Base.OrDefault() as TypeNode.Fqn;
                Dictionary<int, TypeNode[]> baseParameters = null, ownParameters = null;
                if (baseOwner != null && index.IsVisibleOwner(baseOwner.Name)) index.Ctors.TryGetValue(baseOwner.Name, out baseParameters);
                if (owner.Name.HasValue && index.IsVisibleOwner(owner.Name.Value)) index.Ctors.TryGetValue(owner.Name.Value, out ownParameters);
                SourceDelegation(constructor.BaseArgs.OrDefault(), baseParameters, OwnerArguments(baseOwner), context);
                var arity = owner.TypeParams.OrEmpty().Length;
                var ownArguments = arity == 0 ? null : Enumerable.Range(0, arity)
                    .Select(position => (TypeNode)new TypeNode.Tv("type", position)).ToArray();
                SourceDelegation(constructor.ThisArgs.OrDefault(), ownParameters, ownArguments, context);
                SourceUseStatements(constructor.Body.OrEmpty(), context);
            }
            foreach (var method in owner.Methods.OrEmpty()) SourceUseMethod(method, index, frame);
        }
        return file;
    }

    static Ctx SourceUseContext(DeclIndex index, BirParameter[] parameters, TypeNode result,
        NullableRepresentationFrame owner, NullableRepresentationFrame method)
    {
        var context = new Ctx { Idx = index, Ret = result, OwnerNullableFrame = owner, MethodNullableFrame = method };
        foreach (var parameter in parameters)
            if (parameter.Name.HasValue && parameter.Type.HasValue)
                context.Env[parameter.Name.Value] = parameter.Type.Value;
        return context;
    }

    static void SourceUseMethod(BirFunctionDeclaration method, DeclIndex index, NullableRepresentationFrame owner)
        => SourceUseStatements(method.Body.OrEmpty(), SourceUseContext(index, method.Params.OrEmpty(),
            method.Ret.OrDefault(), owner, method.NullableRepresentationFrame.OrDefault()));

    static void SourceUseStatements(IBirStatement[] statements, Ctx context)
    {
        for (var index = 0; index < statements.Length; index++)
            statements[index] = (IBirStatement)SourceUseEval((BirGraphValue)statements[index], context, out _);
    }

    static OptionalField<IBirExpression> SourceUseEdge(OptionalField<IBirExpression> edge, Ctx context, out TypeNode type)
    {
        type = null;
        return edge.HasValue ? OptionalField<IBirExpression>.Of((IBirExpression)SourceUseEval(
            (BirGraphValue)edge.Value, context, out type)) : edge;
    }

    static void SourceUseChildren(BirGraphValue node, Ctx context, params string[] excluded)
    {
        node.VisitSourceChildren((field, role, slot) => {
            if (excluded.Contains(field) || field is "attrs" or "overrides"
                || role is BirGenericFrameRole.ReferencedDeclaration or BirGenericFrameRole.ResolvedDeclaration
                    or BirGenericFrameRole.OverrideDeclaration or BirGenericFrameRole.FrozenSourceFact
                    or BirGenericFrameRole.StorageSourceType) return;
            slot.Replace(SourceUseEval(slot.Value, context, out _));
        }, region => {
            // Suspend-lambda bodies still exist at this clock. Like the
            // remaining declared-use capability, flow their executable regions
            // as well as construction operands; later synthesis does not
            // replace this source-use analysis.
            SourceUseStatements(region.Statements, context);
            if (region.Result is { } result) result.Replace(SourceUseEval(result.Value, context, out _));
        });
    }

    static BirGraphValue SourceUseEval(BirGraphValue node, Ctx context, out TypeNode type)
    {
        type = null;
        if (node == null || node is IBirExecutablePayload or BirMemberReference or BirAttribute
            or BirParameter or BirTypeParameter or BirFunctionDeclaration or BirConstructor or IBirTypeDeclaration)
            return node;
        switch (node)
        {
            case BirVariable variable:
            {
                variable.Init = SourceUseEdge(variable.Init, context, out var initializer);
                if (!variable.Name.HasValue) return node;
                var declared = variable.Type.OrDefault();
                if (declared != null && initializer != null && !initializer.Equals(declared))
                {
                    if (declared is TypeNode.Oblivious platform && (initializer.Equals(platform.Of)
                        || initializer is TypeNode.Nullable nullable && nullable.Of.Equals(platform.Of)))
                        variable.Type = BirGraphValue.CopyType(initializer);
                    else if (SourceCoerce(variable.Init.OrDefault(), initializer, declared) is { } conversion)
                        variable.Init = OptionalField<IBirExpression>.Of(conversion);
                    else if (IsObjectErasureOf(initializer, declared)) variable.Type = BirGraphValue.CopyType(initializer);
                }
                context.Env[variable.Name.Value] = variable.Type.OrDefault() ?? initializer;
                return node;
            }
            case BirLocal local:
            {
                type = local.Name.HasValue ? context.Env.GetValueOrDefault(local.Name.Value) : null;
                if (type != null) SourceRestamp(local, type);
                type ??= local.ResultTypeStamp;
                return node;
            }
            case BirConstant constant:
            {
                type = constant.Type.OrDefault(); return node;
            }
            case BirCast cast:
            {
                cast.E = SourceUseEdge(cast.E, context, out _);
                type = cast.Type.OrDefault(); return node;
            }
            case BirNew construction:
            {
                return SourceUseNew(construction, context, out type);
            }
            case BirCallStatic call:
            {
                return SourceUseStaticCall(call, context, out type);
            }
            case BirCallInstance call:
            {
                return SourceUseInstanceCall(call, context, out type);
            }
            case BirGraphNode operation when IsClrBoundKind(operation.Tag):
            {
                return SourceUseClrCall(operation, context, out type);
            }
            case BirGraphNode operation when ClrBoundNode.IsMemberAccess(operation.Tag):
            {
                return SourceUseClrAccess(operation, context, out type);
            }
            case BirArrayGet access:
            {
                access.Array = SourceUseEdge(access.Array, context, out var array);
                access.Index = SourceUseEdge(access.Index, context, out _);
                if (array is TypeNode.Array flowed)
                {
                    if (access.Elem.HasValue && !access.Elem.Value.Equals(flowed.Elem) && IsObjectErasureOf(flowed.Elem, access.Elem.Value))
                    { access.Elem = BirGraphValue.CopyType(flowed.Elem); SourceRestamp(access, flowed.Elem); }
                    type = flowed.Elem;
                }
                else type = access.Elem.OrDefault();
                return node;
            }
            case BirDelegateInvoke invoke:
            {
                return SourceUseDelegate(invoke, context, out type);
            }
            case BirFieldGet field:
            {
                field.Recv = SourceUseEdge(field.Recv, context, out _);
                type = SourceSlotType(field.OwnerType.OrDefault(), field.Name.OrDefault(), context);
                if (type == null) { type = field.Type.OrDefault(); return node; }
                if (field.Type.HasValue && !field.Type.Value.Equals(type) && IsObjectErasureOf(type, field.Type.Value))
                { field.Type = BirGraphValue.CopyType(type); SourceRestamp(field, type); }
                else if (field.Sty.HasValue && !field.Sty.Value.Equals(type) && IsObjectErasureOf(type, field.Sty.Value)) SourceRestamp(field, type);
                return node;
            }
            case BirSetLocal set:
            {
                set.Value = SourceUseEdge(set.Value, context, out var value);
                if (set.Name.HasValue && context.Env.GetValueOrDefault(set.Name.Value) is { } target
                    && SourceCoerce(set.Value.OrDefault(), value, target) is { } wrapped)
                    set.Value = OptionalField<IBirExpression>.Of(wrapped);
                return node;
            }
            case BirSetField set:
            {
                set.Recv = SourceUseEdge(set.Recv, context, out _);
                set.Value = SourceStore(set.Value, SourceSlotType(set.OwnerType.OrDefault(), set.Name.OrDefault(), context), context);
                return node;
            }
            case BirFieldSetExpression set:
            {
                set.Recv = SourceUseEdge(set.Recv, context, out _);
                set.Value = SourceStore(set.Value, SourceSlotType(set.OwnerType.OrDefault(), set.Name.OrDefault(), context), context);
                return node;
            }
            case BirStaticFieldSet set:
            {
                set.Value = SourceStore(set.Value, SourceSlotType(set.OwnerType.OrDefault(), set.Name.OrDefault(), context), context);
                return node;
            }
            case BirArraySet set:
            {
                set.Array = SourceUseEdge(set.Array, context, out var array);
                set.Index = SourceUseEdge(set.Index, context, out _);
                set.Value = SourceUseEdge(set.Value, context, out var value);
                if (array is TypeNode.Array flowed)
                {
                    if (set.Elem.HasValue && !set.Elem.Value.Equals(flowed.Elem) && IsObjectErasureOf(flowed.Elem, set.Elem.Value))
                        set.Elem = BirGraphValue.CopyType(flowed.Elem);
                    if (SourceCoerce(set.Value.OrDefault(), value, flowed.Elem) is { } wrapped)
                        set.Value = OptionalField<IBirExpression>.Of(wrapped);
                }
                return node;
            }
            case BirReturn result:
            {
                result.Value = SourceStore(result.Value, context.Ret, context); return node;
            }
            case BirReturnExpression result:
            {
                result.Value = SourceStore(result.Value, context.Ret, context); return node;
            }
            case BirConditional conditional:
            {
                conditional.Cond = SourceUseEdge(conditional.Cond, context, out _);
                conditional.Then = SourceUseEdge(conditional.Then, context, out var thenType);
                conditional.Else = SourceUseEdge(conditional.Else, context, out var elseType);
                type = conditional.Type.OrDefault();
                if (type == null) { type = thenType ?? elseType; return node; }
                if (SourceCoerce(conditional.Then.OrDefault(), thenType, type) is { } thenValue)
                    conditional.Then = OptionalField<IBirExpression>.Of(thenValue);
                if (SourceCoerce(conditional.Else.OrDefault(), elseType, type) is { } elseValue)
                    conditional.Else = OptionalField<IBirExpression>.Of(elseValue);
                return node;
            }
            case BirNewArray array:
            {
                SourceArrayElements(array.Elems.OrDefault(), array.Elem.OrDefault(), context); type = array.Elem.HasValue ? new TypeNode.Array(array.Elem.Value) : null; return node;
            }
            case BirNewArraySized array:
            {
                array.Size = SourceUseEdge(array.Size, context, out _);
                type = array.Elem.HasValue ? new TypeNode.Array(array.Elem.Value) : null; return node;
            }
            case BirNewArrayInit array:
            {
                array.Size = SourceUseEdge(array.Size, context, out _);
                array.Init = SourceUseEdge(array.Init, context, out _);
                type = array.Elem.HasValue ? new TypeNode.Array(array.Elem.Value) : null; return node;
            }
            case BirForEach loop:
            {
                loop.Src = SourceUseEdge(loop.Src, context, out _);
                if (loop.Var.HasValue && loop.Elem.HasValue) context.Env[loop.Var.Value] = loop.Elem.Value;
                SourceUseStatements(loop.Body.OrEmpty(), context); return node;
            }
            case BirForArray loop:
            {
                loop.Array = SourceUseEdge(loop.Array, context, out var array);
                if (array is TypeNode.Array flowed && loop.Elem.HasValue && !loop.Elem.Value.Equals(flowed.Elem)
                    && IsObjectErasureOf(flowed.Elem, loop.Elem.Value)) loop.Elem = BirGraphValue.CopyType(flowed.Elem);
                if (loop.Var.HasValue && loop.Elem.HasValue) context.Env[loop.Var.Value] = loop.Elem.Value;
                SourceUseStatements(loop.Body.OrEmpty(), context); return node;
            }
            case BirNullableHasValue nullable:
            {
                nullable.E = SourceNullableRead(nullable.E, nullable.Elem.OrDefault(), context);
                type = new TypeNode.Fqn("kotlin.Boolean"); return node;
            }
            case BirNullableValue nullable:
            {
                nullable.E = SourceNullableRead(nullable.E, nullable.Elem.OrDefault(), context);
                type = nullable.Elem.OrDefault(); return node;
            }
            case BirValueBlock block:
            {
                SourceUseStatements(block.Stmts.OrEmpty(), context);
                block.Result = SourceUseEdge(block.Result, context, out type); return node;
            }
            default:
            {
                SourceUseChildren(node, context);
                type = SourceOwnType(node, "type") ?? SourceOwnType(node, "dynRet") ?? SourceOwnType(node, "ret");
                return node;
            }
        }
    }

    static IBirExpression SourceCoerce(IBirExpression value, TypeNode source, TypeNode target, bool exactProperty = false)
        => CoerceForFixedSlot(value, source, target, _isValue, _refs, exactProperty);

    static OptionalField<IBirExpression> SourceStore(OptionalField<IBirExpression> value, TypeNode target, Ctx context)
    {
        value = SourceUseEdge(value, context, out var source);
        return SourceCoerce(value.OrDefault(), source, target) is { } wrapped ? OptionalField<IBirExpression>.Of(wrapped) : value;
    }

    static TypeNode SourceSlotType(TypeNode type, string name, Ctx context)
        => type is TypeNode.Fqn owner && name != null && context.Idx.IsVisibleOwner(owner.Name)
            && context.Idx.Slots.TryGetValue(owner.Name, out var slots)
            && slots.GetValueOrDefault(name) is { } declaration
                ? Subst(NullableGenericErasure.EraseNullableTv(declaration, _isValue), OwnerArguments(owner), null) : null;

    static void SourceArrayElements(IBirExpression[] elements, TypeNode element, Ctx context)
    {
        if (elements == null) return;
        for (var index = 0; index < elements.Length; index++)
        {
            elements[index] = (IBirExpression)SourceUseEval((BirGraphValue)elements[index], context, out var source);
            elements[index] = SourceCoerce(elements[index], source, element) ?? elements[index];
        }
    }

    static OptionalField<IBirExpression> SourceNullableRead(OptionalField<IBirExpression> value, TypeNode element, Ctx context)
    {
        value = SourceUseEdge(value, context, out var source);
        if (element == null || value.OrDefault() is not BirGraphNode graph) return value;
        var selection = SelectObjectSlotCoercion(graph.Tag, graph is BirCast cast ? cast.Type.OrDefault() : null,
            source, new TypeNode.Nullable(element), false, _isValue);
        return selection.Kind == SlotCoercionKind.Cast ? new BirCast { Type = selection.Type,
            E = OptionalField<IBirExpression>.Of((IBirExpression)graph.DeepClone()) } : value;
    }
}
