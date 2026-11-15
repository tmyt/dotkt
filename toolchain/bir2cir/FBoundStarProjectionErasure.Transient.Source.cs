using DotKt.Bir.Lowering;

static partial class FBoundStarProjectionErasure
{
    // These nine transport facts have finished their source consumers. Consume
    // each fact on its owning kind; no field-name walk or type inference occurs.
    internal static void RemoveTransientFacts(IEnumerable<BirFileDeclaration> roots)
    {
        foreach (var root in roots) ConsumeCarrierTransportFacts(root);
    }

    static void ConsumeCarrierTransportFacts(BirGraphValue node)
    {
        if (node is IBirExecutablePayload) return;
        switch (node)
        {
            case BirCast value:
                value.ReifiedTypeOperand = default;
                value._physicalDeclarationSignature = default;
                value._exactBridgeCast = default;
                break;
            case BirInstanceOrNull value: value.ReifiedTypeOperand = default; break;
            case BirNullableWrap value: value._physicalDeclarationSignature = default; break;
            case BirCallStatic value: value._innerFactoryArguments = default; break;
            case BirCallInstance value:
                value._innerFactoryArguments = default;
                value._exactExistentialBridgeOwnerCall = default;
                value._physicalDeclarationSignature = default;
                break;
            case BirFieldGet value:
                value.Outer = default;
                value._nativeLocation = default;
                value._nativeFieldAddress = default;
                break;
            case BirStaticFieldGet value:
                value._nativeLocation = default;
                value._nativeFieldAddress = default;
                break;
            case BirFieldSetExpression value: value.Outer = default; break;
            case BirParameter value: value.Outer = default; break;
            case BirFunctionDeclaration value: value._physicalDeclarationSignature = default; break;
            case BirConstructor value: value.DelegationOuterSlot = default; break;
            case BirFieldDeclaration value:
                value.Outer = default;
                value._nativeLocation = default;
                break;
            case BirRefTypeDeclaration value: value._nativeLocation = default; break;
            case BirCaptureDescriptor value: value.Outer = default; break;
            case BirLambdaParameter value: value.Outer = default; break;
            case BirInlineCapture value: value.Outer = default; break;
            case BirNullableValue value: value._physicalDeclarationSignature = default; break;
            case BirSetField value: value.Outer = default; break;
            case BirVariable value: value.Outer = default; break;
            case BirLocal value: value.Outer = default; break;
            case BirThis value: value.Outer = default; break;
            case BirConstrainedCall value: value._exactExistentialBridgeOwnerCall = default; break;
        }
        node.VisitChildren(ConsumeCarrierTransportFacts);
    }
}
