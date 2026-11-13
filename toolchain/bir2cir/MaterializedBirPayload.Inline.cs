using DotKt.Bir.Lowering;

static partial class MaterializedBirPayload
{
    // The envelope is intentionally opaque to ordinary whole-file visitors.
    // Admission explicitly opens its executable/declaration occurrences once.
    internal static BirInlinePayload Normalize(BirInlinePayload payload)
    {
        payload.VisitChildSlots(slot => slot.Replace(ObjectSlotRename.Apply(slot.Value)));
        return payload;
    }
}
