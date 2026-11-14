using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FunctionValueRepresentation
{
    // Declaration records and child vectors retain their actual identity. Only
    // a value-producing invocation needs a new owning expression around it.
    public static BirGraphValue Apply(BirGraphValue root, ValueTypeOracle isByRefLike)
    {
        FunctionSignatureIdentity.Capture(root);
        return WalkSource(root, isByRefLike);
    }

    static BirGraphValue WalkSource(BirGraphValue node, ValueTypeOracle isByRefLike)
    {
        if (node is IBirExecutablePayload) return node;
        CaptureCallSignature(node);
        var invocationResult = node is BirDelegateInvoke { FuncType.HasValue: true } invoke
            && invoke.FuncType.Value is TypeNode.Fn { Suspend: false, Clr: null } function
                ? function.Ret : null;
        var declaredDelegate = node.HasDeclaredDelegateSlot;
        node.RewriteOwnTypeFieldsInPlace((field, _, type) =>
            PreserveField(field) || field == "funcType" && declaredDelegate ? type : RepresentType(type, isByRefLike));
        node.VisitOwnChildSlots((field, _, slot) => {
            if (PreserveField(field)) return;
            var child = slot.Value;
            var replacement = WalkSource(child, isByRefLike);
            if (!ReferenceEquals(child, replacement)) slot.Replace(replacement);
        });
        // Do not visit this freshly-created cast again in this pass. Its inner
        // invocation is the same evaluated occurrence, not a duplicated call.
        return invocationResult == null ? node : new BirCast {
            Type = RepresentType(invocationResult, isByRefLike),
            _exactBridgeCast = true, FunctionValueRestoration = true,
            E = OptionalField<IBirExpression>.Of((IBirExpression)node),
        };
    }

    static bool PreserveField(string field) => field is
        DeclarationIdentityBinding.SemanticSignatureKey or FunctionSignatureIdentity.Key or FunctionSignatureIdentity.CallKey
        or "memberSignature" or "delegationSig" or "inheritedClassMethods" or "attrs" or "retAttrs";

    static void CaptureCallSignature(BirGraphValue node)
    {
        void Capture(OptionalField<TypeNode[]> signature, OptionalField<TypeNode[]> previous,
            Action<OptionalField<TypeNode[]>> assign)
        {
            if (!previous.HasValue && signature.HasValue && signature.Value.Any(ContainsOrdinaryFunction))
                assign(signature.Value.Select(BirGraphValue.CopyType).ToArray());
        }
        switch (node)
        {
            case BirCallStatic call: Capture(call.Sig, call.FunctionCallSignature, value => call.FunctionCallSignature = value); break;
            case BirCallInstance call: Capture(call.Sig, call.FunctionCallSignature, value => call.FunctionCallSignature = value); break;
            case BirConstrainedCall call: Capture(call.Sig, call.FunctionCallSignature, value => call.FunctionCallSignature = value); break;
        }
    }
}
