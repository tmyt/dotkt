using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Toolchain;

sealed partial class ReferenceMetadataIndex
{
    static void SelfTestPhysicalTypeNameSnapshots()
    {
        var firstMetadata = new ReferenceDotKtMetadata();
        firstMetadata.PhysicalTypeBySemanticName["Published.Collision"] = "Physical.Collision`1";
        firstMetadata.PhysicalTypeBySemanticName["Published.Outer.Inner"] = "Physical.Outer`1+Inner`1";
        firstMetadata.PhysicalTypeBySemanticName["Published.Other"] = "Physical.Other";
        var secondMetadata = new ReferenceDotKtMetadata();
        secondMetadata.PhysicalTypeBySemanticName["Published.Last"] = "Physical.Last`2";
        secondMetadata.PhysicalTypeBySemanticName["published.collision"] = "Physical.LowerCase";
        ReferenceMetadataIndex NewIndex() => new(new List<ReferenceAssembly> {
            new("first.dll", "first", "1", firstMetadata),
            new("second.dll", "second", "1", secondMetadata),
        }, ManagedReferenceCatalog.Create(Array.Empty<string>(), "bir2cir"));

        var index = NewIndex();
        var original = index.PhysicalTypeNames;
        if (original.Count != 5 || !ReferenceEquals(original, index.PhysicalTypeNames)
            || original["Published.Last"] != "Physical.Last`2"
            || original["Published.Outer.Inner"] != "Physical.Outer`1+Inner`1")
            throw new InvalidOperationException("Physical catalog lost constructor population, exact nested names or stable-phase reuse");

        var localNames = new HashSet<string>(StringComparer.Ordinal) { "Published.Collision" };
        index.SetLocalEmittedTypes(localNames);
        var initial = index.PhysicalTypeNames;
        if (initial.Count != 4 || initial.ContainsKey("Published.Collision")
            || initial["published.collision"] != "Physical.LowerCase"
            || ReferenceEquals(original, initial) || !ReferenceEquals(initial, index.PhysicalTypeNames))
            throw new InvalidOperationException("Initial local inventory lost ordinal source precedence or snapshot reuse");
        localNames.Add("Published.Other");
        if (!initial.ContainsKey("Published.Other") || !ReferenceEquals(initial, index.PhysicalTypeNames))
            throw new InvalidOperationException("Caller mutation changed the copied local inventory");

        index.SetLocalEmittedTypes(new HashSet<string> {
            "Published.Collision", "Published.Outer`1+Inner`1",
        });
        var late = index.PhysicalTypeNames;
        if (!late.Keys.SequenceEqual(new[] { "Published.Other", "Published.Last", "published.collision" })
            || !initial.ContainsKey("Published.Outer.Inner") || !original.ContainsKey("Published.Collision")
            || ReferenceEquals(initial, late) || !ReferenceEquals(late, index.PhysicalTypeNames))
            throw new InvalidOperationException("Late local inventory lost nested normalization, enumeration order or prior snapshot isolation");

        var other = NewIndex();
        var independent = other.PhysicalTypeNames;
        index.SetLocalEmittedTypes(new HashSet<string>());
        if (index.PhysicalTypeNames.Count != 5 || independent.Count != 5
            || ReferenceEquals(original, independent) || ReferenceEquals(index.PhysicalTypeNames, independent)
            || !ReferenceEquals(independent, other.PhysicalTypeNames))
            throw new InvalidOperationException("Restoring local inventory or a separate index reused another phase's physical catalog");

        var empty = Build(Array.Empty<string>());
        var emptySnapshot = empty.PhysicalTypeNames;
        if (emptySnapshot.Count != 0 || !ReferenceEquals(emptySnapshot, empty.PhysicalTypeNames))
            throw new InvalidOperationException("Empty reference catalog was rebuilt on each lookup");
    }
}
