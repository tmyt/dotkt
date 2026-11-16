using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;

// Declaration demand and physical shaping use the same scalar frame rules.
static partial class NullableRepresentationMaterialization
{
    static void RequireCovered(IEnumerable<int> indices, NullableRepresentationFrame frame, string declaration)
    {
        var missing = indices.Except(frame.NullableIndices).ToArray();
        if (missing.Length != 0)
            throw new InvalidOperationException($"Nullable frame materialization requires body specialization first: {declaration}, slots {string.Join(",", missing)}");
    }

    static NullableRepresentationFrame CaptureFrame(TypeNode[] captures, NullableRepresentationTypes mapping)
    {
        var companions = captures.OfType<TypeNode.Tv>().Distinct().SelectMany(variable =>
            (variable.Scope == "type" ? mapping.OwnerFrame : mapping.MethodFrame)?.Companions
                .Where(slot => slot.SourceIndex == variable.I)
                .Select(slot => new NullableRepresentationFrame.Slot(Array.IndexOf(captures, variable), slot.Representation))
                ?? Enumerable.Empty<NullableRepresentationFrame.Slot>()).ToArray();
        IEnumerable<int> Indices(NullableRepresentationFrame.Role role) => companions.Where(slot => slot.Representation == role)
            .Select(slot => slot.SourceIndex).OrderBy(index => index);
        return new NullableRepresentationFrame(captures.Length, Indices(NullableRepresentationFrame.Role.Nullable),
            storageIndices: Indices(NullableRepresentationFrame.Role.Storage),
            nullableStorageIndices: Indices(NullableRepresentationFrame.Role.NullableStorage));
    }

}
