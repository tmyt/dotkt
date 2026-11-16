using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static IBirExpression AlignCallResultSource(IBirExpression call, TypeNode physicalResult,
        bool protectExactCast)
    {
        var node = (BirGraphValue)call;
        var semantic = node.ResultTypeStamp;
        if (physicalResult == null || semantic == null || physicalResult.Equals(semantic)
            || IsVoidResult(physicalResult) && IsVoidResult(semantic)) return call;
        var hadStamp = SourceResultStamp(node).HasValue;
        SetSourceCallResult(node, physicalResult);
        var restored = StringCharSequenceBridge.RestoreMaterializedValue(call, physicalResult, semantic);
        if (restored != null)
        {
            if (hadStamp) SetSourceResultStamp((BirGraphValue)restored, semantic);
            return restored;
        }
        var cast = new BirCast { Type = BirGraphValue.CopyType(semantic),
            E = OptionalField<IBirExpression>.Of(call) };
        if (hadStamp) cast.Sty = BirGraphValue.CopyType(semantic);
        if (protectExactCast) cast._exactBridgeCast = true;
        return cast;
    }

    static OptionalField<TypeNode> SourceResultStamp(BirGraphValue node) => node switch {
        BirCallInstance value => value.Sty, BirCallStatic value => value.Sty,
        BirCast value => value.Sty, BirValueBlock value => value.Sty,
        _ => default,
    };

    static void SetSourceResultStamp(BirGraphValue node, TypeNode result)
    {
        switch (node)
        {
            case BirCallInstance value: value.Sty = BirGraphValue.CopyType(result); break;
            case BirCallStatic value: value.Sty = BirGraphValue.CopyType(result); break;
            case BirCast value: value.Sty = BirGraphValue.CopyType(result); break;
            case BirValueBlock value: value.Sty = BirGraphValue.CopyType(result); break;
        }
    }

    static void SetSourceCallResult(BirGraphValue node, TypeNode result)
    {
        switch (node)
        {
            case BirCallInstance value:
                value.Ret = BirGraphValue.CopyType(result);
                if (value.DynRet.HasValue) value.DynRet = BirGraphValue.CopyType(result);
                if (value.Sty.HasValue) value.Sty = BirGraphValue.CopyType(result);
                break;
            case BirCallStatic value:
                value.Ret = BirGraphValue.CopyType(result);
                if (value.DynRet.HasValue) value.DynRet = BirGraphValue.CopyType(result);
                if (value.Sty.HasValue) value.Sty = BirGraphValue.CopyType(result);
                break;
        }
    }

    internal static void MarkPhysicalPropertyCallSource(BirGraphValue call, bool propertyCall,
        string sourcePropertyName, string accessorKind, string selectedID = null)
    {
        var identity = selectedID == null ? default : OptionalField<string>.Of(selectedID);
        switch (call)
        {
            case BirCallStatic value:
                value.DeclarationId = identity;
                if (propertyCall) { KotlinPropertyAccessors.PreserveCallIdentity(value, sourcePropertyName, accessorKind); value.Prop = default; }
                break;
            case BirCallInstance value:
                value.DeclarationId = identity;
                if (propertyCall) { value.PropertyName = sourcePropertyName; value.PropertyAccessor = accessorKind; value.Prop = default; }
                break;
            case BirNewBoundDelegate value:
                value.DeclarationId = identity;
                // This vocabulary owns a selected MethodDef and function type,
                // not invocation result/property slots. No missing old fields
                // are reconstructed on an unproduced early delegate shape.
                break;
        }
    }
}
