using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class DeclarationIdentityBinding
{
    enum ReferencedAction { Skip, ClrDelegate, StaticIntrinsicDelegate, MarkIntrinsic, MarkFactory, Bind }
    readonly record struct ReferencedPlan(ReferencedAction Action, string Name = null, string Owner = null, string Intrinsic = null);

    // Both owning transports consume exactly the same selected declaration.
    // No signature/overload search occurs at this boundary.
    static ReferencedPlan PlanReferencedBinding(string kind, string id, ReferenceMetadataIndex refs,
        IReadOnlySet<string> localIds, bool coroutineContext, bool factoryMarker, bool deferUnknown)
    {
        if (localIds.Contains(id) || coroutineContext) return new(ReferencedAction.Skip);
        if (!refs.TryDeclarationIdentity(id, out var name, out var owner, out var intrinsic, out _))
        {
            if (deferUnknown) return new(ReferencedAction.Skip);
            throw new InvalidOperationException($"bir2cir: referenced frontend declaration identity '{id}' has no trusted physical binding");
        }
        if (kind is "newBoundClrDelegate" or "newClrStaticDelegate") return new(ReferencedAction.ClrDelegate, name, owner, intrinsic);
        if (intrinsic != null) return new(kind == "newDelegate" ? ReferencedAction.StaticIntrinsicDelegate : ReferencedAction.MarkIntrinsic,
            name, owner, intrinsic);
        if (refs.TryDeclarationFactory(id, out _, out _, out _) && kind == "callStatic" && (!factoryMarker || deferUnknown))
            return new(ReferencedAction.MarkFactory);
        if (deferUnknown && kind is "callInstance" or "constrainedCall"
            && (refs.TryResolveClrOwner(owner, out _, out _) || AliasHelperHoist.IsNativeArrayOwner(owner))) return new(ReferencedAction.Skip);
        return new(ReferencedAction.Bind, name, owner);
    }

    static (string Owner, string Name) StaticIntrinsicTarget(string id, string intrinsic)
    {
        var dot = intrinsic.LastIndexOf('.');
        if (dot <= 0 || dot == intrinsic.Length - 1) throw new InvalidOperationException(
            $"bir2cir: selected intrinsic declaration '{id}' cannot be represented as a static callable reference: '{intrinsic}'");
        return (intrinsic[..dot], intrinsic[(dot + 1)..]);
    }

    static TypeNode[] StaticIntrinsicParameters(string id, TypeNode[] signature, TypeNode function)
    {
        var selected = signature ?? (function as TypeNode.Fn)?.DelegateParams;
        if (selected == null) throw new InvalidOperationException(
            $"bir2cir: selected intrinsic callable reference '{id}' has no semantic parameter signature");
        return selected.Select(BirGraphValue.CopyType).ToArray();
    }

    internal static BirFileDeclaration BindReferenced(BirFileDeclaration root, ReferenceMetadataIndex refs,
        IReadOnlySet<string> localIds, bool deferUnknown = false, bool preserveForScalarResolution = false)
    {
        BirGraphValue Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return node;
            node.VisitChildSlots(slot => slot.Replace(Walk(slot.Value)));
            if (node is not (BirCallStatic or BirCallInstance or BirConstrainedCall or BirNewDelegate or BirNewBoundDelegate)
                || !node.DeclarationIdentityFact.HasValue) return node;
            var id = node.DeclarationIdentityFact.Value;
            var kind = ((BirGraphNode)node).Tag;
            var context = node is BirCallStatic read && SuspendColdLowering.IsCoroutineContextRead(kind,
                read.PropertyName.OrDefault(), read.PropertyAccessor.OrDefault(), read.Method.OrDefault(), read.Prop.OrDefault(),
                read.Args.HasValue ? read.Args.Value.Length : null);
            var plan = PlanReferencedBinding(kind, id, refs, localIds, context,
                node is BirCallStatic factory && factory.DeclarationReferencedFactory.HasValue, deferUnknown);
            switch (plan.Action)
            {
                case ReferencedAction.Skip: return node;
                case ReferencedAction.StaticIntrinsicDelegate:
                    return BindStaticIntrinsicDelegate((BirNewDelegate)node, id, plan.Intrinsic);
                case ReferencedAction.MarkIntrinsic:
                    switch (node) {
                        case BirCallStatic call: call.DeclarationReferencedIntrinsic = true; break;
                        case BirCallInstance call: call.DeclarationReferencedIntrinsic = true; break;
                        case BirConstrainedCall call: call.DeclarationReferencedIntrinsic = true; break;
                        case BirNewBoundDelegate call: call.DeclarationReferencedIntrinsic = true; break;
                    }
                    return node;
                case ReferencedAction.MarkFactory:
                    ((BirCallStatic)node).DeclarationReferencedFactory = true;
                    return node;
            }
            var consumeIdentity = !deferUnknown && !preserveForScalarResolution;
            switch (node)
            {
                case BirCallStatic call:
                    call.Method = plan.Name; call.Owner = new TypeNode.Fqn(plan.Owner); call.CalleeOwner = new TypeNode.Fqn(plan.Owner);
                    call.Prop = default; call.DeclarationReferencedFactory = default;
                    if (consumeIdentity) call.DeclarationId = default;
                    break;
                case BirCallInstance call:
                    call.Method = plan.Name; call.Prop = default;
                    if (consumeIdentity) call.DeclarationId = default;
                    break;
                case BirConstrainedCall call:
                    call.Method = plan.Name;
                    if (consumeIdentity) call.DeclarationId = default;
                    break;
                case BirNewDelegate call:
                    call.Method = plan.Name; call.CalleeOwner = new TypeNode.Fqn(plan.Owner);
                    if (consumeIdentity) call.DeclarationId = default;
                    break;
                case BirNewBoundDelegate call:
                    call.Method = plan.Name;
                    if (consumeIdentity) call.DeclarationId = default;
                    break;
            }
            return node;
        }
        Walk(root);
        return root;
    }

    static BirClrStaticDelegate BindStaticIntrinsicDelegate(BirNewDelegate source, string id, string intrinsic)
    {
        var target = StaticIntrinsicTarget(id, intrinsic);
        return new BirClrStaticDelegate { ClrType = new TypeNode.Fqn(target.Owner), Method = target.Name,
            ArgTypes = StaticIntrinsicParameters(id, source.Sig.OrDefault(), source.FuncType.OrDefault()),
            FuncType = source.FuncType, TypeArgs = source.TypeArgs, DotktDelegateSlot = source.DotktDelegateSlot,
            _resolvedMethodTypeParams = source._resolvedMethodTypeParams,
            DelegateCtorRef = source.DelegateCtorRef, MemberRef = source.MemberRef, CalleeParams = source.CalleeParams,
            ResolvedMemberParams = source.ResolvedMemberParams, ResolvedMemberReturn = source.ResolvedMemberReturn };
    }
}
