using System;
using System.Linq;
using DotKt.Bir;

sealed partial class ReferenceMetadataIndex
{
    static void SelfTestSourceHierarchyFrames()
    {
        var index = Build(Array.Empty<string>());
        void Declare(string semantic, string physical, int arity, string owner = null, int captured = 0)
        {
            index._physicalTypeBySemanticName[semantic] = physical;
            index._referenceTypeShapesByPhysicalOwner[physical] =
                new ReferenceTypeShape(arity, "class", null, Array.Empty<TypeNode.Fqn>());
            if (owner != null)
            {
                index._innerCapturedCount[semantic] = captured;
                index._innerSemanticOwner[semantic] = owner;
            }
        }
        Declare("probe.Outer", "probe.Outer`1", 1);
        Declare("probe.Outer.Middle", "probe.Outer`1+Middle`1", 2, "probe.Outer", 1);
        Declare("probe.Outer.Middle.Inner", "probe.Outer`1+Middle`1+Inner`1", 3, "probe.Outer.Middle", 2);
        Declare("probe.Outer.Captured", "probe.Outer`1+Captured", 1, "probe.Outer", 1);
        var middle = index.SourceHierarchyFrame("probe.Outer.Middle");
        var inner = index.SourceHierarchyFrame("probe.Outer`1+Middle`1+Inner`1");
        if (!middle.PhysicalOrder.SequenceEqual(new[] { 1, 0 })
            || !inner.PhysicalOrder.SequenceEqual(new[] { 2, 1, 0 })
            || !index.SourceHierarchyFrame("probe.Outer.Captured").PhysicalOrder.SequenceEqual(new[] { 0 }))
            throw new InvalidOperationException("Source hierarchy lost declared inner capture ordering");
        var companionFrame = new NullableRepresentationFrame(3, new[] { 0, 2 }, new[] { 2, 4, 1, 0, 3 });
        index._ownerNullableFrames["probe.Outer`1+Middle`1+Inner`1"] = companionFrame;
        if (!ReferenceEquals(companionFrame, index.SourceHierarchyFrame("probe.Outer.Middle.Inner")))
            throw new InvalidOperationException("Source hierarchy replaced an explicit representation frame");
    }
}
