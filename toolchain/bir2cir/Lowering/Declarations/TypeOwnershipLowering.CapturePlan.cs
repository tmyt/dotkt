using DotKt.Bir;

static partial class TypeOwnershipLowering
{
    // A plan contains positional facts only. Applying it to typed source or a
    // late DOM splice must not choose a second generic ownership policy.
    sealed record CapturePlan(int[] Remaining, int[] OldToNew, int[] SourceMap, NullableRepresentationFrame Frame);

    static CapturePlan PlanCapture(IReadOnlyList<TypeNode> arguments,
        NullableRepresentationFrame owner, NullableRepresentationFrame capture)
    {
        static bool OwnerSlot(TypeNode argument, int slot) => argument is TypeNode.Tv { Scope: "type" } variable && variable.I == slot;
        var sourceMap = new int[capture.SourceArity];
        var reused = new HashSet<int>();
        var sourceArity = owner.SourceArity;
        for (var source = 0; source < capture.SourceArity; source++)
        {
            var capturedSlots = Enumerable.Range(0, capture.PhysicalArity)
                .Where(index => capture.PhysicalSlot(index).SourceIndex == source).ToArray();
            var shared = Enumerable.Range(0, owner.SourceArity).FirstOrDefault(candidate => !reused.Contains(candidate)
                && OwnerSlot(arguments[capture.SourcePosition(source)], owner.SourcePosition(candidate))
                && capturedSlots.All(index => {
                    var role = capture.PhysicalSlot(index).Representation;
                    var ownerSlot = Enumerable.Range(0, owner.PhysicalArity)
                        .FirstOrDefault(slot => owner.PhysicalSlot(slot) == new NullableRepresentationFrame.Slot(candidate, role), -1);
                    return ownerSlot < 0 || OwnerSlot(arguments[index], ownerSlot);
                }), -1);
            sourceMap[source] = shared < 0 ? sourceArity++ : shared;
            if (shared >= 0) reused.Add(shared);
        }
        var consumed = new HashSet<int>();
        var oldToNew = new int[arguments.Count];
        for (var slot = 0; slot < owner.PhysicalArity; slot++)
        {
            var existing = Enumerable.Range(0, arguments.Count).FirstOrDefault(index => !consumed.Contains(index)
                && OwnerSlot(arguments[index], slot)
                && sourceMap[capture.PhysicalSlot(index).SourceIndex] == owner.PhysicalSlot(slot).SourceIndex
                && capture.PhysicalSlot(index).Representation == owner.PhysicalSlot(slot).Representation, -1);
            if (existing >= 0) { consumed.Add(existing); oldToNew[existing] = slot; }
        }
        var remaining = Enumerable.Range(0, arguments.Count).Where(index => !consumed.Contains(index)).ToArray();
        for (var index = 0; index < remaining.Length; index++) oldToNew[remaining[index]] = owner.PhysicalArity + index;
        var slots = Enumerable.Range(0, owner.PhysicalArity).Select(owner.PhysicalSlot)
            .Concat(remaining.Select(index => {
                var slot = capture.PhysicalSlot(index);
                return new NullableRepresentationFrame.Slot(sourceMap[slot.SourceIndex], slot.Representation);
            })).ToArray();
        int[] Indices(NullableRepresentationFrame.Role role) => slots.Where(slot => slot.Representation == role)
            .Select(slot => slot.SourceIndex).Distinct().OrderBy(index => index).ToArray();
        var nullable = Indices(NullableRepresentationFrame.Role.Nullable);
        var storage = Indices(NullableRepresentationFrame.Role.Storage);
        var nullableStorage = Indices(NullableRepresentationFrame.Role.NullableStorage);
        var canonical = new NullableRepresentationFrame(sourceArity, nullable, storageIndices: storage, nullableStorageIndices: nullableStorage);
        var order = slots.Select(slot => canonical.Variable(new TypeNode.Tv("type", slot.SourceIndex), slot.Representation).I).ToArray();
        return new(remaining, oldToNew, sourceMap, new NullableRepresentationFrame(sourceArity, nullable, order, storage, nullableStorage));
    }

    static int[] Inverse(int[] newToOld)
    {
        var inverse = new int[newToOld.Length];
        for (var index = 0; index < newToOld.Length; index++) inverse[newToOld[index]] = index;
        return inverse;
    }

    // These are the existing source ownership permutation rules, not a new
    // physical-signature type mapper. In particular, foreign CLR leaves remain
    // outside the source frame permutation.
    static TypeNode RemapOwnerSlots(TypeNode type, int[] oldToNew) => type switch {
        TypeNode.Tv { Scope: "type" } variable when variable.I >= 0 && variable.I < oldToNew.Length => new TypeNode.Tv("type", oldToNew[variable.I]),
        TypeNode.Fqn value => value with { Args = value.Args?.Select(item => RemapOwnerSlots(item, oldToNew)).ToArray() },
        TypeNode.Nullable value => new TypeNode.Nullable(RemapOwnerSlots(value.Of, oldToNew)),
        TypeNode.Oblivious value => new TypeNode.Oblivious(RemapOwnerSlots(value.Of, oldToNew)),
        TypeNode.Projection value => new TypeNode.Projection(value.Variance, RemapOwnerSlots(value.Of, oldToNew)),
        TypeNode.Array value => new TypeNode.Array(RemapOwnerSlots(value.Elem, oldToNew)),
        TypeNode.ByRef value => new TypeNode.ByRef(RemapOwnerSlots(value.Of, oldToNew)),
        TypeNode.Fn value => new TypeNode.Fn(value.Suspend, RemapOwnerSlots(value.Ret, oldToNew),
            value.Params.Select(item => RemapOwnerSlots(item, oldToNew)).ToArray(),
            value.Recv == null ? null : RemapOwnerSlots(value.Recv, oldToNew), value.Clr,
            value.Ctx?.Select(item => RemapOwnerSlots(item, oldToNew)).ToArray()),
        _ => type,
    };
}
