using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableTvErasureCallRealign
{
    static BirGraphValue SourceUseInstanceCall(BirCallInstance call, Ctx context, out TypeNode result)
    {
        call.Recv = SourceUseEdge(call.Recv, context, out var receiver);
        result = call.ResultTypeStamp;
        if (!call.Method.HasValue) { SourceRealignArguments(call, null, null, null, null, context); return call; }
        var declared = call.OwnerType.OrDefault() as TypeNode.Fqn;
        var flowed = receiver is TypeNode.Fqn { Name: "object", Args: null } ? null : receiver as TypeNode.Fqn;
        var owner = flowed != null && (declared == null || flowed.Name == declared.Name) ? flowed : declared;
        if (owner == null) { SourceRealignArguments(call, null, null, null, null, context); return call; }
        if (declared != null && !owner.Equals(declared) && IsObjectErasureOf(owner, declared))
            call.OwnerType = BirGraphValue.CopyType(owner);
        if (SourceCoerce(call.Recv.OrDefault(), receiver, owner) is { } narrowed)
            call.Recv = OptionalField<IBirExpression>.Of(narrowed);
        var selection = ReadUseSelection(call);
        var arguments = call.TypeArgs.OrDefault()?.Select(BirGraphValue.CopyType).ToArray();
        var count = call.Args.OrEmpty().Length;
        var selected = SelectUseDeclaration(selection, owner, call.Method.Value, count,
            arguments?.Length ?? 0, false, context.Idx);
        var declaration = selected.Declaration;
        SourceRealignArguments(call, declaration?.Params, declaration?.ParamsRefused, selected.OwnerArguments, arguments,
            context, selected.ExactProperty);
        if (declaration?.Ret == null) return call;
        var erased = NullableGenericErasure.EraseNullableTv(declaration.Ret, _isValue);
        var derived = Subst(erased, OwnerArguments(owner), arguments);
        return SourceDerivedCall(call, derived, result, !erased.Equals(declaration.Ret),
            declaration.NullableErasureOwnershipKnown, context.IsNullableCompanion(derived), out result);
    }

    static BirGraphValue SourceUseStaticCall(BirCallStatic call, Ctx context, out TypeNode result)
    {
        result = call.ResultTypeStamp;
        var methodArguments = call.TypeArgs.OrDefault()?.Select(BirGraphValue.CopyType).ToArray();
        var count = call.Args.OrEmpty().Length;
        var selection = ReadUseSelection(call);
        var selected = SelectUseDeclaration(selection, SourceStaticOwner(call) as TypeNode.Fqn,
            call.Method.OrDefault(), count, methodArguments?.Length ?? 0, true, context.Idx);
        var declaration = selected.Declaration;
        var ownerArguments = selected.OwnerArguments;
        SourceRealignArguments(call, declaration?.Params, declaration?.ParamsRefused, ownerArguments, methodArguments,
            context, selected.ExactProperty);
        if (declaration?.Ret == null) return call;
        var erased = NullableGenericErasure.EraseNullableTv(declaration.Ret, _isValue);
        var derived = Subst(erased, ownerArguments, methodArguments);
        return SourceDerivedCall(call, derived, result, !erased.Equals(declaration.Ret),
            declaration.NullableErasureOwnershipKnown, context.IsNullableCompanion(derived), out result);
    }

    static BirGraphValue SourceDerivedCall(BirGraphValue call, TypeNode derived, TypeNode stamped, bool erased,
        bool ownershipKnown, bool companion, out TypeNode result)
    {
        var plan = SelectDerivedResult(derived, stamped, erased, ownershipKnown, companion);
        result = plan.Type;
        switch (plan.Action)
        {
            case DerivedResultAction.Return:
                SourceWriteReturn(call, plan.Type);
                if (plan.Dynamic) SourceWriteDynamicReturn(call, plan.Type);
                SourceRestamp(call, plan.Type);
                break;
            case DerivedResultAction.Stamp: SourceRestamp(call, plan.Type); break;
            case DerivedResultAction.Cast:
                var hadStamp = SourceOwnType(call, "sty") != null;
                SourceWriteReturn(call, derived);
                SourceWriteDynamicReturn(call, derived);
                SourceRestamp(call, derived);
                return new BirCast { Type = BirGraphValue.CopyType(plan.Type),
                    E = OptionalField<IBirExpression>.Of((IBirExpression)call),
                    Sty = hadStamp ? OptionalField<TypeNode>.Of(BirGraphValue.CopyType(plan.Type)) : default };
        }
        return call;
    }

    static BirGraphValue SourceUseNew(BirNew construction, Ctx context, out TypeNode result)
    {
        var count = construction.Args.OrEmpty().Length;
        var owner = construction.Type.OrDefault() as TypeNode.Fqn;
        var selected = SelectConstructorUse(owner, count, context.Idx);
        SourceRealignArguments(construction, selected.Parameters, selected.Refusals, OwnerArguments(owner), null, context);
        result = construction.Type.OrDefault();
        return construction;
    }

    static OptionalField<IBirExpression> SourceReceiver(BirGraphValue node) => node switch {
        BirNativeStatic call => call.Recv, BirNativeInstance call => call.Recv, BirGenericInstance call => call.Recv,
        BirBoundClrDelegate call => call.Recv, BirPropertyGet call => call.Recv, BirPropertySet call => call.Recv,
        BirEventAdd call => call.Recv, BirEventRemove call => call.Recv, _ => default,
    };

    static void SourceWriteReceiver(BirGraphValue node, OptionalField<IBirExpression> receiver)
    {
        switch (node) {
            case BirNativeStatic call: call.Recv = receiver; break;
            case BirNativeInstance call: call.Recv = receiver; break;
            case BirGenericInstance call: call.Recv = receiver; break;
            case BirBoundClrDelegate call: call.Recv = receiver; break;
            case BirPropertyGet call: call.Recv = receiver; break;
            case BirPropertySet call: call.Recv = receiver; break;
            case BirEventAdd call: call.Recv = receiver; break;
            case BirEventRemove call: call.Recv = receiver; break;
        }
    }

    static void SourceUseClrReceiver(BirGraphValue call, Ctx context)
    {
        var edge = SourceUseEdge(SourceReceiver(call), context, out var receiver);
        var owner = SourceClrOwner(call);
        if (owner != null && receiver is not TypeNode.Fqn { Name: "object" or "System.Object" }
            && SourceCoerce(edge.OrDefault(), receiver, owner) is { } narrowed)
            edge = OptionalField<IBirExpression>.Of(narrowed);
        SourceWriteReceiver(call, edge);
    }

    static BirGraphValue SourceUseClrCall(BirGraphValue call, Ctx context, out TypeNode result)
    {
        SourceUseClrReceiver(call, context);
        SourceRealignArguments(call, null, null, OwnerArguments(SourceClrOwner(call)),
            call.ApplicationTypeArguments.OrDefault()?.Select(BirGraphValue.CopyType).ToArray(), context);
        SourceUseChildren(call, context, "recv", "args");
        result = call switch {
            BirNewClr construction => construction.Type.OrDefault(),
            BirBoundClrDelegate construction => construction.FuncType.OrDefault(),
            BirClrStaticDelegate construction => construction.FuncType.OrDefault(),
            _ => SourceOwnType(call, "ret"),
        };
        return call;
    }

    static BirGraphValue SourceUseClrAccess(BirGraphValue call, Ctx context, out TypeNode result)
    {
        SourceUseClrReceiver(call, context);
        SourceUseChildren(call, context, "recv");
        result = call is BirPropertyGet get ? get.Ret.OrDefault() : null;
        return call;
    }

    static BirGraphValue SourceUseDelegate(BirDelegateInvoke invoke, Ctx context, out TypeNode result)
    {
        invoke.Recv = SourceUseEdge(invoke.Recv, context, out var receiver);
        var function = invoke.FuncType.OrDefault() as TypeNode.Fn;
        if (receiver is TypeNode.Fn flowed && function != null && !flowed.Equals(function) && IsObjectErasureOf(flowed, function))
        { invoke.FuncType = BirGraphValue.CopyType(flowed); function = flowed; }
        TypeNode[] parameters = null;
        if (function is { Suspend: false } && invoke.Args.HasValue)
            parameters = function.DelegateParams.Length == invoke.Args.Value.Length ? function.DelegateParams
                : function.Params.Length == invoke.Args.Value.Length ? function.Params : null;
        if (parameters == null)
        {
            if (invoke.Args.HasValue)
                for (var index = 0; index < invoke.Args.Value.Length; index++)
                    invoke.Args.Value[index] = (IBirExpression)SourceUseEval((BirGraphValue)invoke.Args.Value[index], context, out _);
        }
        else SourceRealignArguments(invoke, parameters, null, null, null, context);
        result = function?.Ret;
        return invoke;
    }
}
