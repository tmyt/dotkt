using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    // The final construction owner runs after all member/interface resolution.
    // Pending owning slots are captured before appending adapter declarations:
    // newly authored adapters are final, not additional materialization inputs.
    public static void MaterializeDelegateSlots(BirFileDeclaration document, ReferenceMetadataIndex refs,
        IReadOnlySet<string> localTypes, IReadOnlyDictionary<string, string> localCarriers = null)
    {
        _refs = refs;
        _localCarriers = localCarriers;
        _localTypes = localTypes ?? new HashSet<string>();
        var adapters = new TypedPhysicalDelegateAdapter(refs, () => UnitSingletonRead(refs));
        var pending = new List<BirGraphSlot>();
        Collect(document);
        foreach (var slot in pending)
        {
            var original = slot.Read();
            var replacement = Materialize(original, document, refs, adapters);
            if (!ReferenceEquals(original, replacement)) slot.Replace(replacement);
        }

        void Collect(BirGraphValue value)
        {
            if (value is IBirExecutablePayload) return;
            value.VisitChildSlots(slot => {
                var child = slot.Read();
                Collect(child);
                if (child is BirSamConversion || Construction(child)?.Slot.IsPresent == true)
                    pending.Add(slot);
            });
        }
    }

    static BirGraphValue Materialize(BirGraphValue value, BirFileDeclaration document,
        ReferenceMetadataIndex refs, TypedPhysicalDelegateAdapter adapters)
    {
        if (value is BirSamConversion conversion)
        {
            if (conversion.Type.OrDefault() is not TypeNode.Fqn slot) return conversion;
            if (conversion.FuncType.OrDefault() is not TypeNode.Fn function
                || PhysicalFunctionShape(slot, includeNominal: true) is not TypeNode.Fn signature)
                throw new InvalidOperationException("CLR delegate SAM conversion has no physical Invoke signature");
            // A function-bounded source TV is a generic value, so the declared
            // function view is crossed explicitly before natural delegate storage.
            var captured = new BirCast { Type = function, E = conversion.E };
            return Adapt(captured, slot, BoxedDelegateAdapterPlan(function, signature), alwaysArguments: true);
        }

        var construction = Construction(value);
        if (construction == null) return value;
        var target = construction.Slot.OrDefault() as TypeNode.Fqn;
        construction.Clear();
        if (target == null || construction.Natural is not TypeNode.Fn natural) return value;
        var naturalDelegate = BirTypeLowering.DelegateFqnOf(natural);
        if (naturalDelegate == null || SameDelegate(naturalDelegate, target)) return value;
        if (IsVoidNode(natural.Ret) && SlotInvokeReturn(target) is TypeNode result && !IsVoidNode(result))
            return Adapt((IBirExpression)value, target, VoidDelegateAdapterPlan(natural, target, result), alwaysArguments: false);
        if (PhysicalFunctionShape(target, includeNominal: true) is TypeNode.Fn signatureType
            && NeedsSlotAdapter(natural, signatureType))
            return Adapt((IBirExpression)value, target, BoxedDelegateAdapterPlan(natural, signatureType), alwaysArguments: true);

        // Only the constructor binding follows this retarget operation. Target
        // method/Invoke facts already selected for the natural callable stay intact.
        construction.SetType(target);
        construction.SetConstructor(BirMemberReference.FromPhysical(DelegateConstructorReference(refs, target)));
        return value;

        BirNewClosure Adapt(IBirExpression captured, TypeNode.Fqn slot, DelegateAdapterPlan plan, bool alwaysArguments)
        {
            var name = adapters.AdapterClass(document, plan);
            var closure = new BirNewClosure {
                ClosureType = new TypeNode.Fqn(name), Method = "invoke",
                Captures = new[] { captured }, FuncType = slot,
                DelegateCtorRef = BirMemberReference.FromPhysical(DelegateConstructorReference(refs, slot)),
            };
            if (alwaysArguments || plan.Parameters != null || plan.Arguments.Length != 0)
                closure.TypeArgs = plan.Arguments.ToArray();
            return closure;
        }
    }

    // The supported construction variants expose the same authored fact, but
    // their member bindings remain their own typed properties, never a key bag.
    sealed record DelegateConstruction(OptionalField<TypeNode> Slot, TypeNode Natural,
        Action Clear, Action<TypeNode> SetType, Action<BirMemberReference> SetConstructor);

    static DelegateConstruction Construction(BirGraphValue node) => node switch {
        BirNewClosure n => new(n.DotktDelegateSlot, n.FuncType.OrDefault(),
            () => n.DotktDelegateSlot = default, t => n.FuncType = t, c => n.DelegateCtorRef = c),
        BirNewDelegate n => new(n.DotktDelegateSlot, n.FuncType.OrDefault(),
            () => n.DotktDelegateSlot = default, t => n.FuncType = t, c => n.DelegateCtorRef = c),
        BirNewBoundDelegate n => new(n.DotktDelegateSlot, n.FuncType.OrDefault(),
            () => n.DotktDelegateSlot = default, t => n.FuncType = t, c => n.DelegateCtorRef = c),
        BirBoundClrDelegate n => new(n.DotktDelegateSlot, n.FuncType.OrDefault(),
            () => n.DotktDelegateSlot = default, t => n.FuncType = t, c => n.DelegateCtorRef = c),
        BirClrStaticDelegate n => new(n.DotktDelegateSlot, n.FuncType.OrDefault(),
            () => n.DotktDelegateSlot = default, t => n.FuncType = t, c => n.DelegateCtorRef = c),
        _ => null,
    };
}
