using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ForeignStarProjectionBinding
{
    static IBirExpression RewriteSourceStaticHelper(BirGraphValue node, ReferenceMetadataIndex refs)
    {
        if (node is not BirCallStatic call) return null;
        if (ExactMutableViewDispatch.TryCount(call, refs, out var count)) return count;
        if (ExactMutableViewDispatch.TryListMutation(call, refs, out var mutation)) return mutation;
        if (!call.ProjectedHelperReceiver.HasValue) return null;
        var receiverIndex = call.ProjectedHelperReceiver.Value;
        var arguments = call.Args.OrDefault();
        var typeArguments = call.TypeArgs.OrDefault();
        var signature = call.Sig.OrDefault();
        var owner = (call.Owner.OrDefault() as TypeNode.Fqn)?.Name;
        if (arguments == null || typeArguments == null || signature == null
            || receiverIndex < 0 || receiverIndex >= arguments.Length
            || !refs.TryResolveStaticMemberSignature(owner, call.Method.OrDefault(), typeArguments.Length, true,
                signature, null, out _, out var declaration, out var declaringOwner))
            throw new InvalidOperationException("Projected alias helper has no exact static declaration");
        var result = call.Ret.OrDefault()
            ?? throw new InvalidOperationException("Projected alias helper has no declared result");
        if (declaration.ReturnType.IsByRef || signature.Any(type => type is TypeNode.ByRef)
            || signature.Any(type => ContainsByRefLike(type, refs)))
            throw new NotSupportedException("Projected helper reflection cannot preserve managed-reference arguments or results");
        var returnsVoid = declaration.ReturnType.FullName == "System.Void";
        var invocation = SourceCall(returnsVoid ? "starProjectionInvokeStaticHelperUnit" : "starProjectionInvokeStaticHelper",
            new TypeNode[] { Type, Int, String, new TypeNode.Array(String), new TypeNode.Array(Type), Int, new TypeNode.Array(AnyN) },
            returnsVoid ? new TypeNode.Fqn("kotlin.Unit") : AnyN,
            SourceClassRef(new TypeNode.Fqn(declaringOwner.FullName)), SourceInteger(declaration.MetadataToken),
            SourceText(declaration.Name),
            SourceArray(String, declaration.GetParameters().Select(parameter =>
                SourceText(ReferenceMetadataIndex.ForeignStarRuntimeTypeKey(parameter.ParameterType)))),
            SourceArray(Type, typeArguments.Select(SourceClassRef)), SourceInteger(receiverIndex), SourceArray(AnyN, arguments));
        return returnsVoid || IsObjectish(result) ? invocation : SourceCast(result, invocation);
    }
}
