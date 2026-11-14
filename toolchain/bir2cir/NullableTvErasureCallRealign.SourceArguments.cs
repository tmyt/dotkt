using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableTvErasureCallRealign
{
    static TypeNode[] SourceRealignArguments(BirGraphValue call, TypeNode[] declarations, bool[] refusals,
        TypeNode[] ownerArguments, TypeNode[] methodArguments, Ctx context, bool exactProperty = false)
    {
        var arguments = SourceArguments(call);
        if (arguments == null) return null;
        var clrBound = call is BirGraphNode operation && IsClrBoundKind(operation.Tag);
        var descriptor = SourceDescriptor(call);
        if (descriptor?.Length != arguments.Length) descriptor = null;
        var haveDeclaration = declarations?.Length == arguments.Length;
        var haveRefusals = refusals?.Length == arguments.Length;
        var types = new TypeNode[arguments.Length];
        // Settle every method argument first. The second phase must not derive
        // an earlier scalar argument against an instantiation later changed by
        // a flowed array or generic argument. This is the same scalar unifier
        // used by the materialized/referenced DOM capabilities.
        if (haveDeclaration && methodArguments != null)
            for (var index = 0; index < arguments.Length; index++)
            {
                if (declarations[index] == null || haveRefusals && refusals[index]
                    || arguments[index] is not BirGraphNode argument
                    || BuildsItsOwnElement(argument.Tag, SourceOwnType(argument, "elem"))) continue;
                arguments[index] = (IBirExpression)SourceUseEval(argument, context, out var flowed);
                if (flowed != null && UnifyMethodArgs(declarations[index], flowed, methodArguments))
                    call.ApplicationTypeArguments = methodArguments.Select(BirGraphValue.CopyType).ToArray();
            }
        for (var index = 0; index < arguments.Length; index++)
        {
            var refused = haveRefusals && refusals[index];
            var target = !refused && haveDeclaration && declarations[index] != null
                ? Subst(NullableGenericErasure.EraseNullableTv(declarations[index], _isValue), ownerArguments, methodArguments) : null;
            SourceRetypeConstruction(arguments[index], target);
            arguments[index] = (IBirExpression)SourceUseEval((BirGraphValue)arguments[index], context, out types[index]);
            if (target == null && !refused && descriptor != null && descriptor[index] is { } slot
                && IsBareObject(types[index]) && Subst(slot, ownerArguments, methodArguments) is { } closed
                && (clrBound || NeedsObjectSeam(closed))) target = closed;
            // A construction's argument descriptor is its use-site target, not
            // a generic method's open MethodDef signature. Only this exact
            // nullable/object seam changes that constructor descriptor here.
            if (call is BirNew && target != null && descriptor?[index] is { } authored && IsObjectErasureOf(target, authored))
                descriptor[index] = BirGraphValue.CopyType(target);
            if (SourceCoerce(arguments[index], types[index], target, exactProperty) is { } wrapped)
                arguments[index] = wrapped;
        }
        return types;
    }

    static void SourceRetypeConstruction(IBirExpression expression, TypeNode target)
    {
        switch (expression)
        {
            case BirNew construction when target is TypeNode.Fqn to && RetypeObject(construction.Type.OrDefault(), to):
                construction.Type = BirGraphValue.CopyType(to);
                break;
            case BirNewArray array when RetypeArrayElement(array.Elem.OrDefault(), target):
                array.Elem = BirGraphValue.CopyType(((TypeNode.Array)target).Elem); break;
            case BirNewArraySized array when RetypeArrayElement(array.Elem.OrDefault(), target):
                array.Elem = BirGraphValue.CopyType(((TypeNode.Array)target).Elem); break;
            case BirNewArrayInit array when RetypeArrayElement(array.Elem.OrDefault(), target):
                array.Elem = BirGraphValue.CopyType(((TypeNode.Array)target).Elem); break;
            case BirNewDelegate construction when !construction.Sig.HasValue
                && RetypeDelegate(construction.FuncType.OrDefault(), target):
                construction.FuncType = BirGraphValue.CopyType(target); break;
            case BirNewClosure construction when RetypeDelegate(construction.FuncType.OrDefault(), target):
                construction.FuncType = BirGraphValue.CopyType(target); break;
        }
    }

    static bool RetypeObject(TypeNode source, TypeNode target)
        => source is TypeNode.Fqn from && target is TypeNode.Fqn to
            && SameClassifier(from, to) && !from.Equals(to) && IsObjectErasureOf(to, from);

    static bool RetypeArrayElement(TypeNode element, TypeNode target)
        => target is TypeNode.Array to && IsBareObject(to.Elem) && element != null && !element.Equals(to.Elem)
            && IsObjectErasureOf(to.Elem, element) && NeedsObjectSeam(element);

    static bool RetypeDelegate(TypeNode function, TypeNode target)
        => target is TypeNode.Fn to && function is TypeNode.Fn from
            && !(IsVoidish(from.Ret) && !IsVoidish(to.Ret)) && !from.Equals(to) && IsObjectErasureOf(to, from);

    static void SourceDelegation(IBirExpression[] arguments, Dictionary<int, TypeNode[]> constructors,
        TypeNode[] ownerArguments, Ctx context)
    {
        if (arguments == null) return;
        var types = new TypeNode[arguments.Length];
        for (var index = 0; index < arguments.Length; index++)
            arguments[index] = (IBirExpression)SourceUseEval((BirGraphValue)arguments[index], context, out types[index]);
        if (constructors == null || !constructors.TryGetValue(arguments.Length, out var parameters)
            || parameters?.Length != arguments.Length) return;
        for (var index = 0; index < arguments.Length; index++)
            if (parameters[index] != null && Subst(NullableGenericErasure.EraseNullableTv(parameters[index], _isValue),
                    ownerArguments, null) is { } target && SourceCoerce(arguments[index], types[index], target) is { } wrapped)
                arguments[index] = wrapped;
    }
}
