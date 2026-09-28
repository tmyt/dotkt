using System;
using System.Linq;
using System.Text.Json.Nodes;
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
        const string innerName = "probe.Outer`1+Middle`1+Inner`1";
        var variables = Enumerable.Range(0, 3).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray();
        var expectedBase = new TypeNode.Fqn("probe.Holder`1", new TypeNode[] {
            new TypeNode.Fqn(innerName, variables),
        });
        index._referenceTypeShapesByPhysicalOwner[innerName] = new ReferenceTypeShape(
            3, "class", expectedBase, Array.Empty<TypeNode.Fqn>());
        if (!index.TryReferenceSourceTypeShape(new TypeNode.Fqn(innerName), out var arity,
                out var restoredBase, out _) || arity != 3 || restoredBase != expectedBase)
            throw new InvalidOperationException("Source hierarchy lost nested constructed argument restoration");
        var sourceBase = new TypeNode.Fqn("probe.Holder`1", new TypeNode[] {
            new TypeNode.Fqn("probe.Outer.Middle.Inner", variables.Reverse().ToArray()),
        });
        index._referenceTypeShapesByPhysicalOwner[innerName] = new ReferenceTypeShape(
            3, "class", expectedBase, Array.Empty<TypeNode.Fqn>(),
            new JsonObject { ["base"] = TypeJson.Write(sourceBase) });
        if (!index.TryReferenceSourceTypeShape(new TypeNode.Fqn(innerName), out _, out restoredBase, out _)
            || restoredBase != expectedBase)
            throw new InvalidOperationException("Source hierarchy reordered an already-source carrier edge");
        Declare("probe.Wide", "probe.Wide`2", 2);
        Declare("probe.Wide.Middle", "probe.Wide`2+Middle`2", 4, "probe.Wide", 2);
        Declare("probe.Wide.Middle.Inner", "probe.Wide`2+Middle`2+Inner`2", 6, "probe.Wide.Middle", 4);
        if (!index.SourceHierarchyFrame("probe.Wide.Middle.Inner").PhysicalOrder
                .SequenceEqual(new[] { 4, 5, 2, 3, 0, 1 }))
            throw new InvalidOperationException("Source hierarchy reversed parameters within an enclosing owner");
        Declare("probe.NullableOuter", "probe.NullableOuter`1", 1);
        Declare("probe.NullableOuter.Inner", "probe.NullableOuter`1+Inner`2", 3, "probe.NullableOuter", 1);
        const string nullableName = "probe.NullableOuter`1+Inner`2";
        index._ownerNullableFrames[nullableName] = new NullableRepresentationFrame(2, new[] { 1 });
        var application = index.SourceHierarchyFrame(nullableName);
        var sourceUse = new TypeNode.Fqn(nullableName, new TypeNode[] {
            new TypeNode.Tv("method", 1), new TypeNode.Tv("method", 0),
        });
        var physicalUse = new TypeNode.Fqn("probe.NullableOuter.Inner", new TypeNode[] {
            new TypeNode.Tv("method", 0), new TypeNode.Tv("method", 1), new TypeNode.Tv("method", 2),
        });
        if (!index.SemanticDeclarationDescribesCall(sourceUse, physicalUse))
            throw new InvalidOperationException("Declaration validation lost nested source/application correspondence");
        if (!index.SemanticDeclarationDescribesCall(sourceUse,
                new TypeNode.Fqn("probe.NullableOuter.Inner", sourceUse.Args))
            || index.SemanticDeclarationDescribesCall(sourceUse,
                new TypeNode.Fqn(physicalUse.Name, new TypeNode[] {
                    new TypeNode.Tv("method", 0), new TypeNode.Tv("method", 3), new TypeNode.Tv("method", 2),
                })))
            throw new InvalidOperationException("Declaration validation confused a classifier spelling with a different argument");
        if (application.SemanticVariable(new TypeNode.Tv("type", 1)) != new TypeNode.Tv("type", 0)
            || application.SemanticVariable(new TypeNode.Tv("type", 2)) != new TypeNode.Nullable(new TypeNode.Tv("type", 0)))
            throw new InvalidOperationException("Source hierarchy lost explicit frame roles in application order");
        var carrierBase = new TypeNode.Fqn("probe.Holder`1", new TypeNode[] {
            new TypeNode.Nullable(new TypeNode.Tv("type", 1)),
        });
        index._referenceTypeShapesByPhysicalOwner[nullableName] = new ReferenceTypeShape(
            3, "class", null, Array.Empty<TypeNode.Fqn>(), new JsonObject { ["base"] = TypeJson.Write(carrierBase) });
        if (!index.TryReferenceSourceTypeShape(new TypeNode.Fqn(nullableName), out var sourceArity, out var nullableBase, out _)
            || sourceArity != 2 || nullableBase != new TypeNode.Fqn("probe.Holder`1", new TypeNode[] {
                new TypeNode.Nullable(new TypeNode.Tv("type", 0)),
            }))
            throw new InvalidOperationException("Source carrier retained declaration order instead of application order");
    }
}
