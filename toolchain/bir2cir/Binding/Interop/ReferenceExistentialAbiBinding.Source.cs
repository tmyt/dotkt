using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ReferenceExistentialAbiBinding
{
    internal static void Apply(BirFileDeclaration root, ReferenceMetadataIndex refs) => BindSourceCalls(root, refs);

    static void BindSourceCalls(BirGraphValue node, ReferenceMetadataIndex refs)
    {
        if (node is IBirExecutablePayload) return;
        var (owner, method, isStatic, arguments, methodArguments, signature, previous) = node switch {
            BirCallStatic call => (call.Owner.OrDefault() as TypeNode.Fqn ?? call.OwnerType.OrDefault() as TypeNode.Fqn
                    ?? call.CalleeOwner.OrDefault() as TypeNode.Fqn, call.Method.OrDefault(), true,
                call.Args, call.TypeArgs.OrEmpty(), call.Sig.OrDefault(), call.Ret.OrDefault()),
            BirCallInstance call => (call.OwnerType.OrDefault() as TypeNode.Fqn, call.Method.OrDefault(), false,
                call.Args, call.TypeArgs.OrEmpty(), call.Sig.OrDefault(), call.Ret.OrDefault()),
            _ => (null, (string)null, false, default(OptionalField<IBirExpression[]>), Array.Empty<TypeNode>(),
                (TypeNode[])null, (TypeNode)null),
        };
        if (owner != null && method != null && arguments.HasValue
            && refs.TryExistentialAbiMember(owner.Name, method, isStatic, methodArguments.Length, arguments.Value.Length,
                out var physicalParams, out var physicalResult))
        {
            var selected = physicalParams.Select((parameter, index) =>
                BirGraphValue.CopyType(ContainsPhysicalExistential(parameter, refs)
                    ? parameter : signature?[index] ?? parameter)).ToArray();
            var hasPhysicalResult = ContainsPhysicalExistential(physicalResult, refs);
            var result = ProjectAliasValueResult(CloseResult(physicalResult, owner.Args ?? Array.Empty<TypeNode>(), methodArguments),
                alias => AliasVarianceRepresentation.RequiresErasure(alias, refs));
            switch (node)
            {
                case BirCallStatic call:
                    call.Sig = selected;
                    if (hasPhysicalResult) call.Ret = BirGraphValue.CopyType(result);
                    break;
                case BirCallInstance call:
                    call.Sig = selected;
                    if (hasPhysicalResult) call.Ret = BirGraphValue.CopyType(result);
                    break;
            }
            // This phase clears a stale existing stamp only when IT changed
            // the physical return; preserve missing/null and unrelated stamps.
            if (hasPhysicalResult && !result.Equals(previous))
            {
                var stamp = node switch {
                    BirCallStatic call => call.Sty.OrDefault(),
                    BirCallInstance call => call.Sty.OrDefault(),
                    _ => null,
                };
                var dynamicResult = node switch {
                    BirCallStatic call => call.DynRet.OrDefault(),
                    BirCallInstance call => call.DynRet.OrDefault(),
                    _ => null,
                };
                if (stamp != null && (!IrSanity.StampAgrees(stamp, result)
                    || dynamicResult != null && !IrSanity.StampAgrees(stamp, dynamicResult)))
                    node.ClearResultTypeStamp();
            }
        }
        node.VisitChildren(child => BindSourceCalls(child, refs));
    }
}
