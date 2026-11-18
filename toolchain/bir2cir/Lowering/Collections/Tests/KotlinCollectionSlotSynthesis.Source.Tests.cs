using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using SelectedMethodHeader = KotlinOverrideSlotBridge.SelectedMethodHeader;

static partial class KotlinCollectionSlotSynthesis
{
    internal static void SelfTestSource()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning collection slots: " + message); }
        static TypeNode T(string name) => new TypeNode.Fqn(name);
        static BirFunctionDeclaration Method(string owner, string member, string name, string kind, TypeNode result, params BirParameter[] parameters) => new() {
            Name = name, DeclarationId = "declaration:" + name, Ret = result, Params = parameters, Body = Array.Empty<IBirStatement>(),
            Overrides = new[] { new BirOverride { Owner = T(owner), Member = member, Kind = kind, Arity = parameters.Length } },
        };
        static BirParameter Parameter(TypeNode type, TypeNode source = null) => new() { Name = "value", Type = type,
            KotlinType = source == null ? default : OptionalField<TypeNode>.Of(source) };
        static BirFunctionDeclaration Bridge(IBirTypeDeclaration owner, string name) => owner.Methods.Value.Single(method => method.Name.OrDefault() == name);
        static BirCallInstance Forward(BirFunctionDeclaration bridge)
        {
            BirCallInstance result = null;
            bridge.Visit(node => { if (node is BirCallInstance call) result = call; });
            return result ?? throw new InvalidOperationException("Collection bridge has no forwarding call");
        }
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var any = T("kotlin.Any");
        var boolean = T("kotlin.Boolean");
        var integer = T("System.Int32");
        var text = T("System.String");
        var variable = new TypeNode.Tv("type", 1);
        var contains = Method(Collection, "contains", "selectedContains", "method", boolean, Parameter(integer));
        contains.ExplicitClrName = "physicalContains";
        var collection = new TypeNode.Fqn(Collection, new TypeNode[] { variable });
        var all = Method(Collection, "containsAll", "selectedContainsAll", "method", boolean, Parameter(any, collection));
        var indexed = Method(MutableList, "addAll", "selectedIndexed", "method", boolean, Parameter(integer), Parameter(any, collection));
        var queryOwner = new BirClassDefinition { Name = "Queries", Kind = "class",
            CapturedTypeParams = new[] { new BirTypeParameter { Name = "Outer" } },
            TypeParams = new[] { new BirTypeParameter { Name = "Element" } }, Methods = new[] { contains, all, indexed } };
        var originals = queryOwner.Methods.Value;
        var omitted = new BirClassDefinition { Name = "NoMethods", Kind = "class", Methods = OptionalField<BirFunctionDeclaration[]>.Null };
        BirInterfaceDefinition BooleanCarrier(string owner, params string[] members) => new() {
            Name = owner, Kind = "interface", Methods = members.Select(member => new BirFunctionDeclaration {
                Name = member, Ret = boolean, Abstract = true }).ToArray() };
        var collectionCarrier = BooleanCarrier(CollectionDefaultSlots, "dotktContains", "dotktContainsAll");
        var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { queryOwner, omitted, collectionCarrier,
            BooleanCarrier(ListSlots, "dotktAddAllAt") } };
        var types = file.Types.Value;
        var publications = 0;
        ApplyAll(new[] { file }, refs, _ => false, null, (owner, appended) => {
            publications++;
            Require(ReferenceEquals(owner, queryOwner) && appended.Count == 3
                && owner.Methods.Value.Skip(originals.Length).SequenceEqual(appended, ReferenceEqualityComparer.Instance),
                "append callback did not receive the attached method occurrences");
        });
        var queryBridge = Bridge(queryOwner, "dotkt$slot$contains");
        var query = (BirConditional)((BirReturn)queryBridge.Body.Value[0]).Value.Value;
        var guard = (BirConditional)query.Cond.Value;
        var target = Forward(queryBridge);
        Require(guard.Then.Value is BirConstant { Value.Value: BirLiteral.BooleanLiteral { Value: false } }
            && guard.Else.Value is BirIsInstance { Type.Value: TypeNode.Fqn { Name: "System.Int32" } }
            && query.Else.Value is BirConstant { Value.Value: BirLiteral.BooleanLiteral { Value: false } }
            && target.Args.Value.Single() is BirCast { Type.Value: TypeNode.Fqn { Name: "System.Int32" }, E.Value: BirLocal { Name.Value: "p0" } },
            "query compatibility must precede the typed argument cast and return the declared miss value");
        Require(target.Method.Value == "physicalContains" && target.DeclarationId.Value == contains.DeclarationId.Value
            && target.OwnerType.Value is TypeNode.Fqn { Name: "Queries", Args: [TypeNode.Tv { I: 0 }, TypeNode.Tv { I: 1 }] }
            && target.Virtual.Value && target.ClrOwnerResolved.Value && target.Sig.Value.Single() == integer
            && queryBridge.PhysicalSlotBridge.Value && queryBridge.Params.Value.Single().Type.Value == any
            && queryBridge.ClrInterfaceImpls.Value.Single().Member.Value == "dotktContains",
            "selected identity, complete captured/own frame or exact descriptor changed");
        var indexedBridge = Bridge(queryOwner, "dotkt$slot$addAllAt");
        Require(Forward(Bridge(queryOwner, "dotkt$slot$containsAll")).Args.Value.Single() is BirLocal { Name.Value: "p0" }
            && indexedBridge.Params.Value[0].Type.Value == integer && indexedBridge.Params.Value[1].Type.Value == any
            && Forward(indexedBridge).Args.Value is [BirLocal { Name.Value: "p0" }, BirLocal { Name.Value: "p1" }],
            "opaque collection values or the exact leading index acquired an unnecessary cast");
        var methods = queryOwner.Methods.Value;
        ApplyAll(new[] { file }, refs, _ => false, null, (_, _) => publications++);
        Require(publications == 1 && ReferenceEquals(queryOwner.Methods.Value, methods) && ReferenceEquals(file.Types.Value, types)
            && queryOwner.Methods.Value.Take(originals.Length).SequenceEqual(originals, ReferenceEqualityComparer.Instance)
            && omitted.Methods.IsNull && queryOwner.Interfaces.Value.Length == 2,
            "repeated execution replaced declarations, changed absence or duplicated interface obligations");

        var optionalAny = new TypeNode.Nullable(any);
        var remapping = new TypeNode.Fn(false, optionalAny, new TypeNode[] { optionalAny, optionalAny });
        var merge = Method(MutableMap, "merge", "selectedMerge", "method", optionalAny,
            Parameter(text), Parameter(integer), Parameter(remapping));
        var getter = Method(Map, "entries", "allocatedEntriesGetter", "getter", any);
        getter.PropertyName = "entries"; getter.PropertyAccessor = "get";
        var get = Method(Map, "get", "selectedGet", "method", text, Parameter(text));
        var getDefault = Method(Map, "getOrDefault", "selectedDefault", "method", text, Parameter(text), Parameter(text));
        var maps = new BirInterfaceDefinition { Name = "MapDefaults", Kind = "interface", Methods = new[] { merge, getter, get, getDefault } };
        ApplyAll(new[] { new BirFileDeclaration { Types = new IBirTypeDeclaration[] { maps } } }, refs, _ => false, null);
        var mergeBridge = Bridge(maps, "dotkt$slot$dotktMapMerge");
        Require(mergeBridge.ClrInterfaceSlotBridge.Value && TypeNodeDataEquality.Equal(mergeBridge.Params.Value[2].FunctionSignatureType.Value, remapping)
            && mergeBridge.ClrInterfaceImpls.Value.Single().Params.Value[2] is TypeNode.Mod { Req: false }
            && Forward(mergeBridge).Args.Value[2] is BirCast { E.Value: BirLocal { Name.Value: "p2" } },
            "map remapping lost its callable discriminator or declaration-owned input cast");
        var getterCall = Forward(Bridge(maps, "dotkt$slot$dotktMapEntries"));
        Require(getterCall.Method.Value == "entries" && getterCall.Prop.Value == "get"
            && getterCall.PropertyName.Value == "entries" && getterCall.PropertyAccessor.Value == "get",
            "map accessor forwarding lost its semantic property identity");
        Require(((BirConditional)((BirReturn)Bridge(maps, "dotkt$slot$dotktMapGet").Body.Value[0]).Value.Value).Else.Value
                is BirConstant { Value.Value: BirLiteral.NullLiteral }
            && ((BirConditional)((BirReturn)Bridge(maps, "dotkt$slot$dotktMapGetOrDefault").Body.Value[0]).Value.Value).Else.Value
                is BirLocal { Name.Value: "p1" }, "map query miss values lost null/default operand selection");

        var inheritedMethod = Method(Collection, "contains", "baseContains", "method", boolean, Parameter(variable));
        var baseType = new BirClassDefinition { Name = "Parent", Kind = "class", TypeParams = new[] {
                new BirTypeParameter { Name = "Outer" }, new BirTypeParameter { Name = "Element" } }, Methods = new[] { inheritedMethod } };
        var child = new BirClassDefinition { Name = "Child", Kind = "class", Base = new TypeNode.Fqn("Parent", new[] { text, integer }),
            Methods = Array.Empty<BirFunctionDeclaration>() };
        ApplyAll(new[] { new BirFileDeclaration { Types = new IBirTypeDeclaration[] { child, baseType, collectionCarrier } } }, refs, _ => false, null);
        Require(child.Methods.Value.Length == 0 && baseType.Methods.Value.Length == 2,
            "a not-yet-visited local base no longer supplies its exact inherited slot");

        var selected = Method(Collection, "contains", "selectedReference", "method", boolean, Parameter(any));
        var row = new SourceTarget(new TypeNode.Fqn("ReferenceOwner", new[] { text }), SelectedMethodHeader.FromSource(selected), true,
            selected.Overrides.Value, new TypeNode[] { new TypeNode.Tv("type", 0) }, "reference-exact-id", new TypeNode[] { new TypeNode.Nullable(text) });
        var closedBridge = SourceBridge(new SourceDefinition(queryOwner), Slots.Single(slot => slot.SlotMember == "dotktContains"), row,
            new Dictionary<string, SourceDefinition> { [CollectionDefaultSlots] = new(collectionCarrier) }, refs, null);
        var closedCall = Forward(closedBridge);
        var closedGuard = (BirConditional)((BirConditional)((BirReturn)closedBridge.Body.Value[0]).Value.Value).Cond.Value;
        Require(closedCall.CalleeParams.Value.Single() == new TypeNode.Tv("type", 0) && closedCall.DynRet.Value == boolean
            && closedCall.DeclarationId.Value == "reference-exact-id" && closedGuard.Then.Value is BirConstant { Value.Value: BirLiteral.BooleanLiteral { Value: true } }
            && closedGuard.Else.Value is BirIsInstance { Type.Value: TypeNode.Nullable { Of: TypeNode.Fqn { Name: "System.String" } } },
            "closed selected-header signature, identity or query source bound was reconstructed from physical slots");
        Console.WriteLine("[owning collection slots] self-test OK (selected declarations, query barriers, captured frames, opaque arguments, map callbacks, inherited slots, exact metadata)");
    }
}
