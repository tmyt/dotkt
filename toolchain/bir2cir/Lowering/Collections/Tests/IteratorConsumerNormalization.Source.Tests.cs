using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class IteratorConsumerNormalization
{
    internal static void SelfTestSource()
    {
        var text = new TypeNode.Fqn("kotlin.String");
        var integer = new TypeNode.Fqn("kotlin.Int");
        TypeNode Iterator(TypeNode element, bool mutable = false) => new TypeNode.Fqn(
            mutable ? "kotlin.collections.MutableIterator" : "kotlin.collections.Iterator", new[] { element });
        var entry = new TypeNode.Fqn("kotlin.collections.MutableMap.MutableEntry", new TypeNode[] { text, integer });
        var receiver = new BirCallStatic { Owner = new TypeNode.Fqn("probe.SideEffects"), Method = "map",
            Args = Array.Empty<IBirExpression>() };
        var mapInit = new BirCallStatic { Owner = new TypeNode.Fqn("kotlin.collections.MapsKt"), Method = "iterator",
            Args = new IBirExpression[] { receiver } };
        var map = new BirVariable { Name = "entries", Type = Iterator(entry, true), Init = mapInit };
        var bridge = new BirVariable { Name = "bridge", Init = new BirCallStatic {
            Owner = new TypeNode.Fqn(Bridge), Method = "iteratorOverEnumerable", TypeArgs = new TypeNode[] { integer },
            Args = new IBirExpression[] { new BirLocal { Name = "source" } },
        } };
        var ordinaryInit = new BirCallInstance { OwnerType = new TypeNode.Fqn("probe.UserIterator"), Method = "iterator" };
        var ordinary = new BirVariable { Name = "ordinary", Type = Iterator(integer, true), Init = ordinaryInit };
        var helperInit = new BirCallStatic { Owner = new TypeNode.Fqn("dotkt$ClrH_kotlin_collections_ArrayList"),
            Method = "iterator", Args = Array.Empty<IBirExpression>() };
        var helper = new BirVariable { Name = "helper", Type = Iterator(text, true), Init = helperInit };
        var next = new BirCallInstance { OwnerType = Iterator(text, true), Method = "next", Sty = integer,
            Args = Array.Empty<IBirExpression>(), Recv = new BirLocal { Name = "helper" },
            DeclarationId = "selected-next", MemberSignature = Array.Empty<TypeNode>() };
        var hasNext = next with { Method = "hasNext", Sty = new TypeNode.Fqn("kotlin.Boolean") };
        var file = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration {
            Body = new IBirStatement[] { bridge, map, ordinary, helper,
                new BirExpressionStatement { Expr = next }, new BirExpressionStatement { Expr = hasNext } },
        } } };
        Apply(file);
        if (bridge.Type.OrDefault() != Iterator(integer) || map.Type.OrDefault() != Iterator(entry)
            || ordinary.Type.OrDefault() != Iterator(integer, true) || !ReferenceEquals(ordinary.Init.Value, ordinaryInit)
            || helper.Type.OrDefault() != Iterator(text, true) || !ReferenceEquals(helper.Init.Value, helperInit)
            || next.OwnerType.OrDefault() != Iterator(text) || next.Ret.OrDefault() != text || next.Sty.IsPresent
            || !next.Virtual.OrDefault() || next.Sig.Value.Length != 0 || next.DeclarationId.Value != "selected-next"
            || hasNext.Ret.OrDefault() != new TypeNode.Fqn("kotlin.Boolean") || !hasNext.Sty.HasValue)
            throw new InvalidOperationException("Owning iterator normalization changed selection, stamps or the user protocol");
        if (map.Init.Value is not BirCallStatic { Args.Value: [BirCallStatic entries] } outer
            || outer.Method.Value != "iteratorOverEnumerable" || outer.TypeArgs.Value[0] != entry
            || entries.Method.Value != "clrMapMutableEntries" || entries.TypeArgs.Value is not [var key, var value]
            || key != text || value != integer || entries.Args.Value.Length != 2
            || ReferenceEquals(entries.Args.Value[0], receiver)
            || !((BirGraphValue)entries.Args.Value[0]).StructurallyEquals(receiver))
            throw new InvalidOperationException("Owning map iteration lost its single cloned receiver or exact entry type");
        var absent = new BirVariable { Name = "absent" };
        var explicitNull = new BirVariable { Name = "null", Init = OptionalField<IBirExpression>.Null };
        ProcessSource(absent); ProcessSource(explicitNull);
        if (absent.Init.IsPresent || !explicitNull.Init.IsNull)
            throw new InvalidOperationException("Owning iterator normalization changed field presence");
        Console.WriteLine("[owning iterator consumers] self-test OK (selected protocol, mutable entries, clone/effects, stamps, presence)");
    }
}
