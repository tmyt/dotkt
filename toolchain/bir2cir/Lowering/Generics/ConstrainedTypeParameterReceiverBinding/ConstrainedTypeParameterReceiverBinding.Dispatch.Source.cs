using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using static ConstrainedReceiverPolicy;

static partial class ConstrainedTypeParameterReceiverBinding
{
    // Phase two consumes the CURRENT physical frames, after inherited owner
    // selection. It must not reuse the earlier source-declaration inventory.
    internal static void ApplyAll(IReadOnlyList<BirFileDeclaration> roots, ValueTypeOracle isValue,
        ReferenceMetadataIndex refs, IReadOnlyDictionary<string, string> localExistentialOwners = null)
    {
        var arities = CollectSourceTypeArity(roots);
        foreach (var file in roots)
        {
            foreach (var method in file.Methods.OrEmpty()) BindMethod(method, Array.Empty<ConstraintSlot>());
            foreach (var owner in file.Types.OrEmpty())
            {
                var frame = ContinuationOwnerFrame((BirGraphValue)owner);
                foreach (var constructor in owner.Ctors.OrEmpty())
                    BindDispatchSourceBody(constructor.Params.OrEmpty(), constructor.Body.OrEmpty(), frame,
                        Array.Empty<ConstraintSlot>(), arities, isValue, refs, localExistentialOwners);
                foreach (var method in owner.Methods.OrEmpty()) BindMethod(method, frame);
            }
        }
        void BindMethod(BirFunctionDeclaration method, ConstraintSlot[] owner)
        {
            var frame = ResolvedConstraintFrame(method.TypeParams.OrEmpty(), method._ownerConstraintDispatchBounds.OrDefault(),
                method.MethodTypeParameterBoundsPre.OrDefault()?.Bounds, null);
            BindDispatchSourceBody(method.Params.OrEmpty(), method.Body.OrEmpty(), owner, frame,
                arities, isValue, refs, localExistentialOwners);
        }
    }

    static Dictionary<string, int> CollectSourceTypeArity(IEnumerable<BirFileDeclaration> roots)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in roots)
            foreach (var owner in file.Types.OrEmpty())
                if (owner.Name.HasValue) result[owner.Name.Value] = TypeParameterFrame.Count(owner);
        return result;
    }

    static void BindDispatchSourceBody(BirParameter[] parameters, IBirStatement[] body,
        ConstraintSlot[] owner, ConstraintSlot[] method, Dictionary<string, int> arities,
        ValueTypeOracle isValue, ReferenceMetadataIndex refs, IReadOnlyDictionary<string, string> localExistentialOwners)
    {
        TypeNode.Fqn Allocated(TypeNode.Fqn selected) =>
            localExistentialOwners != null && localExistentialOwners.TryGetValue(selected.Name, out var carrier)
                ? new TypeNode.Fqn(carrier)
                : refs?.TryExistentialPhysicalOwner(selected.Name, out var referenced) == true
                    ? new TypeNode.Fqn(referenced) : null;
        bool AllocatedConstraint(TypeNode.Tv variable, TypeNode.Fqn selected) =>
            Allocated(selected) is { } physical && ConstraintAt(variable, physical.Name, owner, method, out _) != null;
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
            if (node == null || node is IBirExecutablePayload or BirMemberReference) return node;
            if (node is BirNewSuspendLambda suspend)
            {
                if (suspend.CapValues.HasValue)
                    for (var index = 0; index < suspend.CapValues.Value.Length; index++)
                        if (suspend.CapValues.Value[index] is BirGraphValue capture)
                            suspend.CapValues.Value[index] = (IBirExpression)Bind(capture);
                return node;
            }
            var (fieldOwner, fieldReceiver, fieldStatic) = node switch {
                BirFieldGet field => (field.OwnerType.OrDefault(), field.Recv.OrDefault(), false),
                BirSetField field => (field.OwnerType.OrDefault(), field.Recv.OrDefault(), false),
                BirFieldSetExpression field => (field.OwnerType.OrDefault(), field.Recv.OrDefault(), false),
                BirLateinitGet field => (field.OwnerType.OrDefault(), field.Recv.OrDefault(), field.Static.OrDefault()),
                _ => ((TypeNode)null, (IBirExpression)null, false),
            };
            if (!fieldStatic && fieldOwner is TypeNode.Fqn fieldType && fieldReceiver != null
                && isValue != null && !isValue(fieldType) && Receiver(fieldReceiver) != null)
                SetSourceReceiver(node, SourceDispatchCast(fieldReceiver, fieldType));

            var (eventOwner, eventReceiver, eventDispatch) = node switch {
                BirEventAdd value => (value.AccessorOwner.OrDefault(), value.Recv.OrDefault(), value.Dispatch.OrDefault()),
                BirEventRemove value => (value.AccessorOwner.OrDefault(), value.Recv.OrDefault(), value.Dispatch.OrDefault()),
                _ => ((TypeNode)null, (IBirExpression)null, (string)null),
            };
            if (eventDispatch == "constrained" && eventOwner is TypeNode.Fqn eventType && eventReceiver != null
                && isValue != null && !isValue(eventType) && Receiver(eventReceiver) is { } eventVariable
                && ConstraintDeclarations(eventVariable, owner, method).Any(bound => bound.Erased))
            {
                var cast = SourceDispatchCast(eventReceiver, eventType);
                if (node is BirEventAdd add) { add.Recv = cast; add.Type = eventType; add.Dispatch = "callvirt"; }
                else { var remove = (BirEventRemove)node; remove.Recv = cast; remove.Type = eventType; remove.Dispatch = "callvirt"; }
            }

            if (node is BirConstrainedCall constrained && refs != null
                && constrained.Iface.OrDefault() is TypeNode.Fqn constrainedOwner
                && constrained.Recv.HasValue && constrained.RecvType.OrDefault() is TypeNode.Tv constrainedVariable
                && (ConstraintAtPhysical(constrainedVariable, constrainedOwner.Name, owner, method, refs, out var removed) != null
                    && removed || AllocatedConstraint(constrainedVariable, constrainedOwner)))
            {
                var source = ConstraintAtPhysical(constrainedVariable, constrainedOwner.Name, owner, method, refs, out _);
                var foreign = ProjectedForeignConstraint(source, refs);
                var local = Allocated(constrainedOwner);
                var dispatchOwner = local ?? foreign ?? constrainedOwner;
                if (foreign == null) constrained.Recv = SourceDispatchCast(constrained.Recv.Value, dispatchOwner);
                node = RestoreSourceDispatch(constrained, dispatchOwner, local != null);
            }

            var (propertyOwner, propertyReceiver) = node switch {
                BirPropertyGet get => (get.Type.OrDefault(), get.Recv.OrDefault()),
                BirPropertySet set => (set.Type.OrDefault(), set.Recv.OrDefault()),
                _ => ((TypeNode)null, (IBirExpression)null),
            };
            if (refs != null && propertyOwner is TypeNode.Fqn propertyType && propertyReceiver != null
                && Receiver(propertyReceiver) is { } propertyVariable
                && ConstraintAtPhysical(propertyVariable, propertyType.Name, owner, method, refs, out var propertyErased) is { } propertyConstraint)
            {
                var projected = propertyErased ? ProjectedForeignConstraint(propertyConstraint, refs) : null;
                if (node is BirPropertyGet get) { get._erasedConstraintDispatch = propertyErased; if (projected != null) get.Type = projected; }
                else { var set = (BirPropertySet)node; set._erasedConstraintDispatch = propertyErased; if (projected != null) set.Type = projected; }
            }

            var call = SourceDispatchCall.Read(node);
            if (call != null && call.Owner.OrDefault() is TypeNode.Fqn selected && call.Receiver.HasValue
                && Receiver(call.Receiver.Value) is { } variable && ClosedOwner(selected, arities) is { } iface)
            {
                var physicalErased = false;
                var source = refs == null ? null : ConstraintAtPhysical(variable, iface.Name, owner, method, refs, out physicalErased);
                var erased = call.Erased.OrDefault() || physicalErased
                    || ConstraintDispatchWasErased(variable, iface.Name, owner, method) || AllocatedConstraint(variable, iface);
                call.Erased = default;
                if (Allocated(iface) is { } physical && ConstraintAt(variable, physical.Name, owner, method, out _) != null)
                { iface = physical; erased = false; }
                if (erased)
                {
                    if (ProjectedForeignConstraint(source, refs) is { } foreign) node = call.ToForeign(foreign);
                    else
                    {
                        var dispatchOwner = Allocated(iface) ?? iface;
                        call.Owner = dispatchOwner;
                        call.Receiver = SourceDispatchCast(call.Receiver.Value, dispatchOwner);
                    }
                }
                else
                {
                    var replacement = call.ToConstrained(variable, iface);
                    AlignSourceArguments(replacement, iface, locals, isValue);
                    node = replacement;
                }
            }
            // Dispatch is selected before visiting its actual children, as in
            // the original physical binder. Replacement operands are moved.
            node.VisitChildSlots(slot => slot.Replace(Bind(slot.Value)));
            return node;
        }
        for (var index = 0; index < body.Length; index++) body[index] = (IBirStatement)Bind((BirGraphValue)body[index]);
    }

    static BirCast SourceDispatchCast(IBirExpression value, TypeNode type) => new() {
        Type = BirGraphValue.CopyType(type), E = OptionalField<IBirExpression>.Of(value),
    };

    static void SetSourceReceiver(BirGraphValue node, BirCast receiver)
    {
        switch (node)
        {
            case BirFieldGet value: value.Recv = receiver; break;
            case BirSetField value: value.Recv = receiver; break;
            case BirFieldSetExpression value: value.Recv = receiver; break;
            case BirLateinitGet value: value.Recv = receiver; break;
        }
    }

    static void AlignSourceArguments(BirConstrainedCall call, TypeNode.Fqn owner,
        Dictionary<string, TypeNode> locals, ValueTypeOracle isValue)
    {
        if (isValue == null || owner.Args == null || !call.Sig.HasValue || !call.Args.HasValue
            || call.Sig.Value.Length != call.Args.Value.Length) return;
        for (var index = 0; index < call.Args.Value.Length; index++)
        {
            var argument = call.Args.Value[index];
            var declared = call.Sig.Value[index];
            if (argument == null || declared == null
                || NullableTvErasureCallRealign.EraseAndSubstituteOwnerSlot(declared, owner.Args, isValue) is not { } target) continue;
            if (NullableTvErasureCallRealign.CastForErasedObjectSlot(argument,
                BirGraphStaticType.Surface(argument, locals), target, isValue) is { } converted)
                call.Args.Value[index] = converted;
        }
    }
}
