using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinMapStorageSynthesis
{
    static void AppendNativeFacesSource(ReferenceMetadataIndex refs, Func<TypeNode, TypeNode> projectReferencedValueType, bool declarationOnly,
        List<BirFunctionDeclaration> methods)
    {
        BirCallStatic RawHelper(string name, TypeNode ret, TypeNode[] arguments,
            TypeNode[] signature, params IBirExpression[] operands) =>
            SourceHelperOn(refs, projectReferencedValueType, "kotlin.collections.ClrAuthoredMapNativeFacesKt", name, ret, arguments, signature, operands);
        BirConstant Null() => new() { Type = SourceType(Any), Value = new BirLiteral.NullLiteral() };
        BirConditional Accept(TypeNode type, string name) => SourceCondition(
            SourceBinary("==", SourceLocal(name), Null()),
            SourceBinary("==", SourceCast(Any, new BirDefaultValue { Type = SourceType(type) }), Null()),
            SourceIs(type, SourceLocal(name)));
        BirIsInstance Mutable() => SourceIs(F(KotlinCollectionSlotSynthesis.MutableMapSlots), SourceSelf());
        BirCallInstance Contains() => SourceSlot("dotktMapContainsKey", Bool, SourceCast(Any, SourceLocal("key")));
        BirCallInstance Get() => SourceSlot("dotktMapGet", Any, SourceCast(Any, SourceLocal("key")));
        void Define(TypeNode owner, string member, TypeNode ret, BirParameter[] parameters, Func<IBirStatement[]> body) =>
            methods.Add(SourceMethod(owner, member, ret, parameters, declarationOnly
                ? new IBirStatement[] { SourceThrow("System.NotSupportedException") } : body()));
        BirCallStatic Helper(string name, TypeNode ret, params IBirExpression[] args) => SourceHelper(refs, projectReferencedValueType, name, ret,
            Array.Empty<TypeNode>(), new[] { (TypeNode)new TypeNode.Fqn("kotlin.Any") }
                .Concat(args.Select(_ => (TypeNode)new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")))).ToArray(),
            new[] { (IBirExpression)SourceSelf() }.Concat(args).ToArray());
        var readOnly = F("System.Collections.Generic.IReadOnlyDictionary", K, V);
        Define(readOnly, "get_Item", V, new[] { SourceParameter("key", K) }, () => new IBirStatement[] {
            SourceIf(Contains(), SourceReturn(SourceCast(V, Get()))), SourceThrow("System.Collections.Generic.KeyNotFoundException") });
        Define(readOnly, "ContainsKey", Bool, new[] { SourceParameter("key", K) }, () => new IBirStatement[] { SourceReturn(Contains()) });
        BirExpressionStatement Store(IBirExpression value) => SourceExpr(new BirByRefStore {
            Ptr = SourceLocal("value"), Elem = SourceType(V), Value = OptionalField<IBirExpression>.Of(value),
        });
        Define(readOnly, "TryGetValue", Bool, new[] { SourceParameter("key", K), SourceParameter("value", new TypeNode.ByRef(V)) },
            () => new IBirStatement[] { SourceIf(Contains(), Store(SourceCast(V, Get())), SourceReturn(SourceBool(true))),
                Store(new BirDefaultValue { Type = SourceType(V) }), SourceReturn(SourceBool(false)) });
        Define(F("System.Collections.Generic.IReadOnlyCollection", Pair), "get_Count", Int,
            Array.Empty<BirParameter>(), () => new IBirStatement[] { SourceReturn(SourceSlot("dotktMapSize", Int)) });
        foreach (var (member, slot, element) in new[] { ("get_Keys", "dotktMapKeys", K), ("get_Values", "dotktMapValues", V) })
            Define(readOnly, member, F(Enumerable, element), Array.Empty<BirParameter>(), () => new IBirStatement[] {
                SourceReturn(SourceCast(F(Enumerable, element), SourceHelper(refs, projectReferencedValueType, "clrAuthoredMapCollection", Any, new[] { element },
                    new TypeNode[] { new TypeNode.Fqn("kotlin.Any") }, SourceSlot(slot, Any)))) });

        var raw = F("System.Collections.IDictionary");
        var collection = F("System.Collections.ICollection");
        BirParameter[] Key() => new[] { SourceParameter("key", Any) };
        Define(raw, "Contains", Bool, Key(), () => new IBirStatement[] {
            SourceIf(Accept(K, "key"), SourceReturn(Contains())), SourceReturn(SourceBool(false)) });
        Define(raw, "get_Item", Any, Key(), () => new IBirStatement[] {
            SourceIf(Accept(K, "key"), SourceIf(Contains(), SourceReturn(Get()))), SourceReturn(Null()) });
        foreach (var (member, helper) in new[] { ("set_Item", "clrAuthoredMapSet"), ("Add", "clrAuthoredMapAdd") })
            Define(raw, member, Void, new[] { SourceParameter("key", Any), SourceParameter("value", Any) }, () => new IBirStatement[] {
                SourceIf(SourceNot(Accept(K, "key")), SourceThrow("System.ArgumentException")),
                SourceIf(SourceNot(Accept(V, "value")), SourceThrow("System.ArgumentException")),
                SourceExpr(Helper(helper, Void, SourceLocal("key"), SourceLocal("value"))) });
        Define(raw, "Remove", Void, Key(), () => new IBirStatement[] {
            SourceExpr(Helper("clrAuthoredMapCheckMutable", Void)),
            SourceIf(Accept(K, "key"), SourceExpr(Helper("clrAuthoredMapRemove", Bool, SourceLocal("key")))) });
        Define(raw, "Clear", Void, Array.Empty<BirParameter>(), () => new IBirStatement[] { SourceExpr(Helper("clrAuthoredMapClear", Void)) });
        foreach (var member in new[] { "get_IsReadOnly", "get_IsFixedSize" })
            Define(raw, member, Bool, Array.Empty<BirParameter>(), () => new IBirStatement[] { SourceReturn(SourceNot(Mutable())) });
        Define(collection, "get_Count", Int, Array.Empty<BirParameter>(), () => new IBirStatement[] { SourceReturn(SourceSlot("dotktMapSize", Int)) });
        Define(collection, "get_IsSynchronized", Bool, Array.Empty<BirParameter>(), () => new IBirStatement[] { SourceReturn(SourceBool(false)) });
        Define(collection, "get_SyncRoot", Any, Array.Empty<BirParameter>(), () => new IBirStatement[] { SourceReturn(SourceSelf()) });
        foreach (var (member, slot, element) in new[] { ("get_Keys", "dotktMapKeys", K), ("get_Values", "dotktMapValues", V) })
            Define(raw, member, collection, Array.Empty<BirParameter>(), () => new IBirStatement[] {
                SourceReturn(SourceCast(collection, RawHelper("clrAuthoredRawCollection", Any, Array.Empty<TypeNode>(),
                    new TypeNode[] { new TypeNode.Fqn("kotlin.Any"), new TypeNode.Fqn("kotlin.Any"), new TypeNode.Fqn("kotlin.Any") },
                    SourceSlot(slot, Any), SourceSelf(), new BirClassReference { Type = SourceType(element) }))) });
        Define(raw, "GetEnumerator", F("System.Collections.IDictionaryEnumerator"), Array.Empty<BirParameter>(), () => new IBirStatement[] {
            SourceReturn(SourceCast(F("System.Collections.IDictionaryEnumerator"), RawHelper("clrAuthoredMapRawEnumerator", Any,
                Array.Empty<TypeNode>(), new TypeNode[] { new TypeNode.Fqn("kotlin.Any") }, SourceSelf()))) });
        Define(collection, "CopyTo", Void, new[] { SourceParameter("array", F("System.Array")), SourceParameter("index", Int) },
            () => new IBirStatement[] { SourceExpr(RawHelper("clrAuthoredMapRawCopy", Void, new[] { K, V },
                new TypeNode[] { new TypeNode.Fqn("kotlin.Any"), new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")),
                    new TypeNode.Fqn("kotlin.Int"), new TypeNode.Fqn("kotlin.Any") },
                SourceSelf(), SourceCast(Any, SourceLocal("array")), SourceLocal("index"), new BirClassReference { Type = SourceType(Pair) })) });
    }
}
