using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ReferenceExistentialAbiBinding
{
    internal static void Apply(BirFileDeclaration root, ReferenceMetadataIndex refs)
    {
        BindSourceCalls(root, refs);
        AlignSourceLocals(root, refs);
    }

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

    static void AlignSourceLocals(BirGraphValue node, ReferenceMetadataIndex refs)
    {
        if (node is IBirExecutablePayload) return;
        if (node is BirVariable variable && SourceAbiExpressionType(variable.Init.OrDefault() as BirGraphValue)
            is TypeNode.Fqn physical && refs.IsExistentialPhysicalOwner(physical.Name))
            variable.Type = BirGraphValue.CopyType(physical);
        node.VisitChildren(child => AlignSourceLocals(child, refs));
    }

    // This owner's initializer reader is intentionally NOT Sty-first Surface.
    // A valueBlock with no result does not fall through to its own type/sty.
    static TypeNode SourceAbiExpressionType(BirGraphValue node)
    {
        if (node == null) return null;
        // A classifier's operand names the tested/referenced type, not the value stored in the local.
        if (node is BirClassReference or BirIsInstance) return BirNodeType.Of((BirGraphNode)node);
        TypeNode result = null;
        node.VisitOwnTypeFields((field, type) => { if (field == "ret") result = type; });
        if (result != null) return result;
        if (node is BirValueBlock block) return SourceAbiExpressionType(block.Result.OrDefault() as BirGraphValue);
        TypeNode own = null, stamp = null;
        node.VisitOwnTypeFields((field, type) => { if (field == "type") own = type; else if (field == "sty") stamp = type; });
        return own ?? stamp;
    }
}
