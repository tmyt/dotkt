using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class ClrMemberResolution
{
    internal static bool MarkDelegateSlot(BirGraphValue construction, TypeNode slotType,
        ReferenceMetadataIndex refs, IReadOnlySet<string> localTypes)
    {
        var previous = construction switch {
            BirNewDelegate value => value.DotktDelegateSlot,
            BirNewClosure value => value.DotktDelegateSlot,
            BirNewBoundDelegate value => value.DotktDelegateSlot,
            BirBoundClrDelegate value => value.DotktDelegateSlot,
            BirClrStaticDelegate value => value.DotktDelegateSlot,
            _ => default,
        };
        if (construction is not (BirNewDelegate or BirNewClosure or BirNewBoundDelegate
            or BirBoundClrDelegate or BirClrStaticDelegate)) return false;
        // Share the construction owner's physical slot selection, including its
        // configured aliases, reference universe and last-writer-wins contract.
        if (DelegateFqnOfSlot(slotType, refs, localTypes) is not TypeNode.Fqn slot) return previous.IsPresent;
        switch (construction)
        {
            case BirNewDelegate value: value.DotktDelegateSlot = slot; break;
            case BirNewClosure value: value.DotktDelegateSlot = slot; break;
            case BirNewBoundDelegate value: value.DotktDelegateSlot = slot; break;
            case BirBoundClrDelegate value: value.DotktDelegateSlot = slot; break;
            case BirClrStaticDelegate value: value.DotktDelegateSlot = slot; break;
        }
        return true;
    }
}
