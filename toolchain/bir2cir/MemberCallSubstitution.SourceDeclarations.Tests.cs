using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class MemberCallSubstitution
{
    internal static void SourceDeclarationSnapshotsSelfTest()
    {
        var text = new TypeNode.Fqn("kotlin.String");
        var sourceArguments = new TypeNode[] { text };
        var parameterType = new TypeNode.Fqn("Container", sourceArguments);
        BirFunctionDeclaration Accessor(string name) => new() {
            Name = name, PropertyName = "value", PropertyAccessor = "get",
            Params = new[] { new BirParameter { Name = "index", Type = parameterType } }, Ret = text,
        };
        var first = Accessor("first"); var second = Accessor("second");
        var bridge = Accessor("bridge"); bridge.PhysicalSlotBridge = true;
        var generic = Accessor("generic"); generic.TypeParams = new[] { new BirTypeParameter { Name = "T" } };
        var owner = new BirClassDefinition { Kind = "class", Name = "Owner", Methods = new[] { first, bridge, second, generic } };
        var nested = new BirClassDefinition { Kind = "class", Name = "Nested", Methods = new[] { Accessor("nested") } };
        var factoryFrame = new NullableRepresentationFrame(1, new[] { 0 });
        BirFunctionDeclaration Factory(string id, string marker, string kind) => new() {
            Name = id, DeclarationId = id, Ret = new TypeNode.Array(text), NullableRepresentationFrame = factoryFrame,
            Params = new[] { new BirParameter { Name = "before", Type = text },
                new BirParameter { Name = "values", Type = new TypeNode.Array(text), Mods = new BirModifiers { Vararg = true } } },
            Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn(marker), Args = new IBirAttributeValue[] {
                new BirAttributeLiteral { Type = text, Value = new BirLiteral.TextLiteral(kind) },
            } } },
        };
        var arrayFactory = Factory("array-factory", "kotlin.clr.ClrArrayFactory", "array");
        var collectionFactory = Factory("collection-factory", "kotlin.clr.ClrCollectionFactory", "list");
        var nestedFactory = Factory("nested-factory", "kotlin.clr.ClrCollectionFactory", "list");
        nested.Methods = nested.Methods.Value.Append(nestedFactory).ToArray();
        var host = new BirFunctionDeclaration { Name = "host", Body = new IBirStatement[] {
            new BirExpressionStatement { Expr = new BirNewSam { SynthClass = nested } },
        } };
        owner.Methods = owner.Methods.Value.Append(collectionFactory).ToArray();
        var root = new BirFileDeclaration { FileClass = "Snapshots", Methods = new[] { arrayFactory, host, Accessor("top-level") },
            Types = new IBirTypeDeclaration[] { owner } };
        var properties = CollectLocalPropertyAccessors(new[] { root });
        var domProperties = CollectLocalPropertyAccessors(new[] { root.ToDom() });
        if (properties.Count != domProperties.Count || properties.Count != 3
            || properties.Any(pair => !domProperties.TryGetValue(pair.Key, out var counterparts)
                || !pair.Value.Select(item => item.PhysicalName).SequenceEqual(counterparts.Select(item => item.PhysicalName))
                || pair.Value.Zip(counterparts).Any(items => !items.First.Parameters.Zip(items.Second.Parameters)
                    .All(types => TypeNodeDataEquality.Equal(types.First, types.Second)))))
            throw new InvalidOperationException("Property declaration snapshots differ between source and live late owners");
        var key = new LocalPropertyAccessorKey("Owner", "value", "get", 0, 1);
        if (!properties[key].Select(item => item.PhysicalName).SequenceEqual(new[] { "first", "second" })
            || !properties.ContainsKey(new("Nested", "value", "get", 0, 1)))
            throw new InvalidOperationException("Property snapshot lost candidate order, bridge exclusion, or nested executable owners");
        var factories = CollectLocalFactories(new[] { root });
        if (factories.Count != 2 || factories.ContainsKey("nested-factory")
            || factories["array-factory"].ArrayKind != "array" || factories["array-factory"].CollectionKind != null
            || factories["collection-factory"].CollectionKind != "list" || factories["collection-factory"].ArrayKind != null
            || factories["array-factory"].ArrayElementHint != "kotlin.String"
            || !factories["array-factory"].VarargPositions.SequenceEqual(new[] { 1 })
            || !ReferenceEquals(factories["array-factory"].Frame, factoryFrame))
            throw new InvalidOperationException("Factory snapshot lost exact declaration identity, varargs, frame, or declaration-only traversal");
        sourceArguments[0] = new TypeNode.Fqn("kotlin.Int");
        first.Name = "renamed"; arrayFactory.DeclarationId = "changed"; arrayFactory.Params.Value[1].Mods.Value.Vararg = false;
        if (properties[key][0].PhysicalName != "first"
            || properties[key][0].Parameters[0] is not TypeNode.Fqn { Args: [TypeNode.Fqn { Name: "kotlin.String" }] }
            || !factories.ContainsKey("array-factory") || factories["array-factory"].VarargPositions[0] != 1)
            throw new InvalidOperationException("Declaration snapshots retain mutable declaration/type storage");
    }
}
