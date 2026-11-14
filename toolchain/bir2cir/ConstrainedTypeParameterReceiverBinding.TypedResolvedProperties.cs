using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ConstrainedTypeParameterReceiverBinding
{
    // The arity snapshot belongs to this resolved file. Another input file
    // cannot change whether a bare local owner is already closed.
    internal static void ApplyResolvedProperties(BirFileDeclaration file)
    {
        var arities = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var owner in file.Types.OrEmpty())
            if (owner.Name.HasValue) arities[owner.Name.Value] = TypeParameterFrame.Count(owner);
        foreach (var method in file.Methods.OrEmpty()) BindResolvedSourceMethod(method, Array.Empty<ConstraintSlot>(), arities);
        foreach (var owner in file.Types.OrEmpty())
        {
            // Only live dispatch/source capabilities enter this frame. The
            // frozen RoundtripSupertypesPre header is deliberately not read.
            var facts = NullableRepresentationViews.TypeFacts((BirGraphValue)owner);
            var frame = ResolvedConstraintFrame(owner.CapturedTypeParams.OrEmpty().Concat(owner.TypeParams.OrEmpty()).ToArray(),
                DispatchBounds((BirGraphValue)owner), facts?.Bounds, facts?.NullableFrame);
            foreach (var constructor in owner.Ctors.OrEmpty())
                BindResolvedSourceBody(constructor.Params.OrEmpty(), constructor.Body.OrEmpty(), frame, Array.Empty<ConstraintSlot>(), arities);
            foreach (var method in owner.Methods.OrEmpty()) BindResolvedSourceMethod(method, frame, arities);
        }
        ClearResolvedConstraintFacts(file);
    }

    static OwnerConstraintDispatchBounds DispatchBounds(BirGraphValue node) => node switch {
        BirFunctionDeclaration method => method._ownerConstraintDispatchBounds.OrDefault(),
        BirClassDefinition owner => owner._ownerConstraintDispatchBounds.OrDefault(),
        BirInterfaceDefinition owner => owner._ownerConstraintDispatchBounds.OrDefault(),
        _ => null,
    };

    static ConstraintSlot[] ResolvedConstraintFrame(BirTypeParameter[] parameters, OwnerConstraintDispatchBounds dispatch,
        IReadOnlyDictionary<int, TypeNode[]> source, NullableRepresentationFrame frame)
    {
        var result = parameters.Select(parameter => new ConstraintSlot(parameter.Constraints.OrEmpty(), parameter.ErasedInnerConstraints.OrEmpty())).ToArray();
        if (dispatch != null)
            foreach (var (index, bounds) in dispatch.Bounds)
                if (index >= 0 && index < result.Length) result[index] = result[index] with { Erased = bounds };
        if (source != null)
            foreach (var (index, bounds) in ProjectedSourceBounds(source, result.Length, 0, frame))
                result[index] = result[index] with { Erased = bounds };
        return result;
    }

    static void BindResolvedSourceMethod(BirFunctionDeclaration method, ConstraintSlot[] owner, Dictionary<string, int> arities)
    {
        var frame = ResolvedConstraintFrame(method.TypeParams.OrEmpty(), method._ownerConstraintDispatchBounds.OrDefault(),
            method.MethodTypeParameterBoundsPre.OrDefault()?.Bounds, null);
        BindResolvedSourceBody(method.Params.OrEmpty(), method.Body.OrEmpty(), owner, frame, arities);
    }

    static void BindResolvedSourceBody(BirParameter[] parameters, IBirStatement[] body, ConstraintSlot[] owner,
        ConstraintSlot[] method, Dictionary<string, int> arities)
    {
        var locals = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters) RecordLocal(locals, ambiguous, parameter.Name.OrDefault(), parameter.Type.OrDefault());
        void Collect(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            if (node is BirVariable variable) RecordLocal(locals, ambiguous, variable.Name.OrDefault(), variable.Type.OrDefault());
            node.VisitChildren(Collect);
        }
        foreach (var statement in body) Collect((BirGraphValue)statement);

        TypeNode.Tv Receiver(IBirExpression expression)
        {
            var type = expression is BirLocal { Name.HasValue: true } local
                && locals.TryGetValue(local.Name.Value, out var declared)
                    ? declared : BirGraphStaticType.Surface(expression, locals);
            return Peel(type) as TypeNode.Tv;
        }
        BirGraphValue Bind(BirGraphValue node)
        {
            if (node is IBirExecutablePayload or BirMemberReference) return node;
            if (node is BirNewSuspendLambda suspend)
            {
                if (suspend.CapValues.HasValue)
                    for (var index = 0; index < suspend.CapValues.Value.Length; index++)
                        if (suspend.CapValues.Value[index] is BirGraphValue value)
                            suspend.CapValues.Value[index] = (IBirExpression)Bind(value);
                return node;
            }
            var (isProperty, accessor, isStatic, type, receiver, dispatch, erased) = node switch {
                BirPropertyGet get => (true, get.Member.OrDefault() == "accessor", get.Static.OrDefault(), get.Type.OrDefault(),
                    get.Recv.OrDefault(), get.Dispatch.OrDefault(), get._erasedConstraintDispatch.HasValue ? (bool?)get._erasedConstraintDispatch.Value : null),
                BirPropertySet set => (true, set.Member.OrDefault() == "accessor", set.Static.OrDefault(), set.Type.OrDefault(),
                    set.Recv.OrDefault(), set.Dispatch.OrDefault(), set._erasedConstraintDispatch.HasValue ? (bool?)set._erasedConstraintDispatch.Value : null),
                _ => (false, false, false, (TypeNode)null, (IBirExpression)null, (string)null, (bool?)null),
            };
            if (isProperty && accessor && !isStatic && type is TypeNode.Fqn named && receiver != null
                && Receiver(receiver) is { } variable && ClosedOwner(named, arities) is { } closed)
            {
                var routeErased = erased ?? ConstraintDispatchWasErased(variable, closed.Name, owner, method);
                if (!routeErased && dispatch == "callvirt") node = ConstrainSourceProperty(node, variable, closed);
                else
                {
                    var cast = new BirCast { Type = BirGraphValue.CopyType(closed), E = OptionalField<IBirExpression>.Of(receiver) };
                    if (node is BirPropertyGet get) get.Recv = cast;
                    else ((BirPropertySet)node).Recv = cast;
                }
            }
            node.VisitOwnChildSlots((_, role, slot) => {
                if (!NullableRepresentationViews.Frozen(role)
                    && role is not (BirGenericFrameRole.ReferencedDeclaration or BirGenericFrameRole.ResolvedDeclaration
                        or BirGenericFrameRole.OverrideDeclaration)) slot.Replace(Bind(slot.Value));
            });
            return node;
        }
        for (var index = 0; index < body.Length; index++) body[index] = (IBirStatement)Bind((BirGraphValue)body[index]);
    }

    static BirConstrainedCall ConstrainSourceProperty(BirGraphValue node, TypeNode.Tv receiverType, TypeNode.Fqn owner)
    {
        var result = node switch {
            BirPropertyGet get => new BirConstrainedCall {
                Method = get.Accessor, Ret = get.Ret, Recv = get.Recv, MemberRef = get.MemberRef,
                CalleeParams = get.CalleeParams, DeclarationId = get.DeclarationId,
                _capturedGenericArgument = get._capturedGenericArgument, _capturedGenericResult = get._capturedGenericResult,
                _foreignDeclarationSignature = get._foreignDeclarationSignature,
                ResolvedMemberParams = get.ResolvedMemberParams, ResolvedMemberReturn = get.ResolvedMemberReturn,
                Args = Array.Empty<IBirExpression>(), Sig = Array.Empty<TypeNode>(),
            },
            BirPropertySet set => new BirConstrainedCall {
                Method = set.Accessor, Recv = set.Recv, MemberRef = set.MemberRef,
                CalleeParams = set.CalleeParams, DeclarationId = set.DeclarationId,
                _capturedGenericArgument = set._capturedGenericArgument, _capturedGenericResult = set._capturedGenericResult,
                _foreignDeclarationSignature = set._foreignDeclarationSignature,
                ResolvedMemberParams = set.ResolvedMemberParams, ResolvedMemberReturn = set.ResolvedMemberReturn,
                Args = set.Value.HasValue ? new[] { set.Value.Value } : Array.Empty<IBirExpression>(),
                Sig = set.MemberRef.OrDefault()?.ParameterTypes.OrDefault() is { Length: > 0 } parameters
                    ? new[] { BirGraphValue.CopyType(parameters[^1]) } : Array.Empty<TypeNode>(),
            },
            _ => throw new InvalidOperationException("Resolved constrained property requires its actual owning accessor"),
        };
        // These fields had no constrainedCall projection in the old strict
        // owning seam either. Never silently discard an unconsumed fact.
        var unmapped = node switch {
            BirPropertyGet get => get.Volatile.IsPresent || get.LocalAccessor.IsPresent || get.AccessorOwner.IsPresent
                || get.ClrOwnerResolved.IsPresent || get.PropertyName.IsPresent || get.PropertyAccessor.IsPresent,
            BirPropertySet set => set.Volatile.IsPresent || set.LocalAccessor.IsPresent || set.AccessorOwner.IsPresent
                || set.ClrOwnerResolved.IsPresent || set.PropertyName.IsPresent || set.PropertyAccessor.IsPresent,
            _ => false,
        };
        if (unmapped) throw new InvalidOperationException("Resolved property retains a fact with no constrained-call projection");
        result.RecvType = BirGraphValue.CopyType(receiverType);
        result.Iface = BirGraphValue.CopyType(owner);
        return result;
    }

    static void ClearResolvedConstraintFacts(BirGraphValue node)
    {
        if (node is IBirExecutablePayload) return;
        switch (node)
        {
            case BirCallInstance value: value._erasedConstraintDispatch = default; break;
            case BirNativeInstance value: value._erasedConstraintDispatch = default; break;
            case BirGenericInstance value: value._erasedConstraintDispatch = default; break;
            case BirPropertyGet value: value._erasedConstraintDispatch = default; break;
            case BirPropertySet value: value._erasedConstraintDispatch = default; break;
            case BirTypeParameter value: value.ErasedInnerConstraints = default; break;
            case BirFunctionDeclaration value: value._ownerConstraintDispatchBounds = default; break;
            case BirClassDefinition value: value._ownerConstraintDispatchBounds = default; break;
            case BirInterfaceDefinition value: value._ownerConstraintDispatchBounds = default; break;
        }
        node.VisitChildren(ClearResolvedConstraintFacts);
    }
}
