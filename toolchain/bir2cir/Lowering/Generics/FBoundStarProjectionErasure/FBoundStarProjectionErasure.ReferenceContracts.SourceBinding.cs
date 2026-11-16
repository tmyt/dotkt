using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static IBirExpression BindReferenceContractSource(IBirExpression call, SourceReferenceContract contract,
        IReadOnlyList<TypeNode> arguments, bool propertyCall, string sourcePropertyName, string accessorKind,
        TypeNode.Fqn constructedOwner = null)
    {
        if (arguments == null) throw new InvalidOperationException("Managed-reference call has no selected owner frame");
        var owner = constructedOwner ?? (TypeNode.Fqn)SubstituteDeclarationTypeArguments(
            contract.Owner, arguments, Array.Empty<TypeNode>());
        if (call is BirCallInstance instance)
        {
            // All arguments are evaluated before the static invoker checks its
            // receiver. Managed locations remain native arguments throughout.
            var values = new[] { instance.Recv.Value }.Concat(instance.Args.Value).ToArray();
            var signature = new[] { (TypeNode)new TypeNode.Fqn("System.Object") }
                .Concat(contract.Slot.Params.Value.Select(parameter => BirGraphValue.CopyType(parameter.Type.Value))).ToArray();
            var replacement = BirCallConversion.MoveInstanceToStatic(instance);
            replacement.Owner = BirGraphValue.CopyType(owner);
            replacement.CalleeOwner = BirGraphValue.CopyType(owner);
            replacement.Method = contract.Invoke;
            replacement.Args = values;
            replacement.Sig = signature;
            replacement.CalleeParams = signature.Select(BirGraphValue.CopyType).ToArray();
            return AlignCallResultSource(replacement, SubstituteMethodTypeArguments(contract.Slot.Ret.OrDefault(),
                replacement.TypeArgs.OrEmpty()), protectExactCast: true);
        }
        if (call is BirNewBoundDelegate bound)
        {
            bound.OwnerType = BirGraphValue.CopyType(owner);
            bound.Recv = OptionalField<IBirExpression>.Of(new BirCast {
                Type = BirGraphValue.CopyType(owner), E = OptionalField<IBirExpression>.Of(bound.Recv.Value),
                _exactBridgeCast = true,
            });
            bound.Method = contract.Slot.Name;
            bound.Sig = contract.Slot.Params.Value.Select(FunctionSignatureIdentity.SignatureType).ToArray();
            bound.CalleeParams = bound.Sig.Value.Select(BirGraphValue.CopyType).ToArray();
            bound.Virtual = true;
            bound.MemberOwnerTypeParams = default;
            bound.MemberVisibility = default;
            MarkPhysicalPropertyCallSource(bound, propertyCall, sourcePropertyName, accessorKind, contract.Slot.DeclarationId.OrDefault());
            bound.CalleeOwner = BirGraphValue.CopyType(owner);
            return bound;
        }
        throw new InvalidOperationException("Reference contract binding requires an owning invocation or bound delegate");
    }
}
