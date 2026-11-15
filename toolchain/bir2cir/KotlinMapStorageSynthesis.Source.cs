using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinMapStorageSynthesis
{
    internal static IReadOnlyList<string> ApplyAll(IReadOnlyList<BirFileDeclaration> roots,
        ReferenceMetadataIndex refs, bool defineCarrier, Func<TypeNode, TypeNode> projectReferencedValueType,
        Action<BirFileDeclaration, IReadOnlyList<IBirTypeDeclaration>> publishTypes)
    {
        ArgumentNullException.ThrowIfNull(publishTypes);
        ArgumentNullException.ThrowIfNull(projectReferencedValueType);
        var definitions = SupertypeGraph.CollectSource(roots);
        var declarations = new Dictionary<string, IBirTypeDeclaration>(StringComparer.Ordinal);
        foreach (var declaration in roots.SelectMany(root => root.Types.OrEmpty()))
            if (declaration.Name.OrDefault() is string name) declarations[name] = declaration;
        foreach (var definition in definitions.Values)
        {
            if (definition.Kind != "class") continue;
            var reached = SupertypeGraph.Reachable(definition, definitions, refs).Select(edge => edge.spec).ToArray();
            foreach (var face in StorageFaces(reached))
            {
                var actual = declarations[definition.Name];
                actual.Interfaces = actual.Interfaces.OrEmpty().Append((TypeNode)BirGraphValue.CopyType(face)).ToArray();
            }
        }
        if (!defineCarrier) return Array.Empty<string>();
        var host = roots.First();
        // These are fresh declarations; publish only the attached records, in
        // the same carrier-then-enumerator order as the physical definitions.
        IBirTypeDeclaration[] appended = { CarrierSource(refs, projectReferencedValueType),
            ReverseEnumeratorBridgeSynthesis.AdapterSource(EnumeratorName, refs) };
        host.Types = host.Types.OrEmpty().Concat(appended).ToArray();
        publishTypes(host, appended);
        return new[] { CarrierName, EnumeratorName };
    }

    internal static string EmitReferenceSurface(IReadOnlyList<BirFileDeclaration> roots,
        Action<BirFileDeclaration, IReadOnlyList<IBirTypeDeclaration>> publishTypes)
    {
        ArgumentNullException.ThrowIfNull(publishTypes);
        var host = roots.First();
        IBirTypeDeclaration[] appended = { CarrierSource(null, null, declarationOnly: true) };
        host.Types = host.Types.OrEmpty().Concat(appended).ToArray();
        publishTypes(host, appended);
        return CarrierName;
    }

    static TypeNode SourceType(TypeNode type) => BirGraphValue.CopyType(type);
    static BirThis SourceSelf() => new();
    static BirLocal SourceLocal(string name) => new() { Name = name };
    static BirCast SourceCast(TypeNode type, IBirExpression value) => new() {
        Type = SourceType(type), E = OptionalField<IBirExpression>.Of(value),
    };
    static BirReturn SourceReturn(IBirExpression value) => new() { Value = OptionalField<IBirExpression>.Of(value) };
    static BirExpressionStatement SourceExpr(IBirExpression value) => new() { Expr = OptionalField<IBirExpression>.Of(value) };
    static BirConstant SourceBool(bool value) => new() { Type = SourceType(Bool), Value = new BirLiteral.BooleanLiteral(value) };
    static BirParameter SourceParameter(string name, TypeNode type) => new() { Name = name, Type = SourceType(type) };
    static BirCallInstance SourceCall(TypeNode owner, string member, TypeNode ret, IBirExpression receiver,
        TypeNode[] signature, params IBirExpression[] args) => new() {
        OwnerType = SourceType(owner), Method = member, Virtual = true,
        Recv = OptionalField<IBirExpression>.Of(receiver), Ret = SourceType(ret),
        Sig = signature.Select(SourceType).ToArray(), Args = args,
    };
    static BirCallInstance SourceSlot(string member, TypeNode ret, params IBirExpression[] args) =>
        SourceCall(F(KotlinCollectionSlotSynthesis.MapSlots), member, ret,
            SourceCast(F(KotlinCollectionSlotSynthesis.MapSlots), SourceSelf()),
            args.Select(_ => Any).ToArray(), args);
    static BirIf SourceIf(IBirExpression condition, params IBirStatement[] body) => new() {
        Branches = new[] { new BirBranch { Cond = OptionalField<IBirExpression>.Of(condition), Body = body } },
    };
    static BirThrow SourceThrow(string type) => new() { Value = new BirNew {
        Type = F(type), ArgTypes = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>(),
    } };
    static BirBinaryOperation SourceBinary(string op, IBirExpression left, IBirExpression right) => new() {
        Op = op, Lhs = OptionalField<IBirExpression>.Of(left), Rhs = OptionalField<IBirExpression>.Of(right),
    };
    static BirUnaryOperation SourceNot(IBirExpression value) => new() { Op = "!", E = OptionalField<IBirExpression>.Of(value) };
    static BirIsInstance SourceIs(TypeNode type, IBirExpression value) => new() {
        Type = SourceType(type), E = OptionalField<IBirExpression>.Of(value),
    };
    static BirConditional SourceCondition(IBirExpression condition, IBirExpression whenTrue, IBirExpression whenFalse) => new() {
        Type = SourceType(Bool), Cond = OptionalField<IBirExpression>.Of(condition),
        Then = OptionalField<IBirExpression>.Of(whenTrue), Else = OptionalField<IBirExpression>.Of(whenFalse),
    };
    static BirFunctionDeclaration SourceMethod(TypeNode owner, string member, TypeNode ret,
        BirParameter[] parameters, IBirStatement[] body) => new() {
        Name = "dotkt$map$" + ((TypeNode.Fqn)owner).Name.Replace('.', '$') + "$" + member,
        Static = false, Virtual = true, Abstract = false, Override = false,
        Vis = "private", Generated = true, ClrInterfaceSlotBridge = true,
        Params = parameters, Ret = SourceType(ret), Body = body,
        ClrInterfaceImpls = new[] { new BirMethodImpl {
            Owner = SourceType(owner), Member = member, Arity = 0,
            Params = parameters.Select(parameter => SourceType(parameter.Type.Value)).ToArray(), Ret = SourceType(ret),
        } },
    };

    static BirCallStatic SourceHelper(ReferenceMetadataIndex refs, Func<TypeNode, TypeNode> projectReferencedValueType,
        string name, TypeNode ret,
        TypeNode[] typeArgs, TypeNode[] sourceSignature, params IBirExpression[] args) =>
        SourceHelperOn(refs, projectReferencedValueType, Helpers, name, ret, typeArgs, sourceSignature, args);

    static BirCallStatic SourceHelperOn(ReferenceMetadataIndex refs, Func<TypeNode, TypeNode> projectReferencedValueType,
        string owner, string name, TypeNode ret,
        TypeNode[] typeArgs, TypeNode[] sourceSignature, params IBirExpression[] args)
    {
        var plan = PlanHelper(refs, owner, name, typeArgs, sourceSignature, projectReferencedValueType);
        return new() { Owner = F(owner), Method = name, DeclarationId = plan.DeclarationId,
            TypeArgs = plan.TypeArguments.Select(SourceType).ToArray(), Sig = plan.Signature.Select(SourceType).ToArray(),
            Ret = SourceType(ret), Args = args };
    }

    static BirInterfaceDefinition CarrierSource(ReferenceMetadataIndex refs,
        Func<TypeNode, TypeNode> projectReferencedValueType, bool declarationOnly = false)
    {
        var dictionary = F(Dictionary, K, V);
        var collection = F(Collection, Pair);
        var enumerable = F(Enumerable, Pair);
        var iterator = F("System.Collections.Generic.IEnumerator", Pair);
        BirCallInstance Contains() => SourceSlot("dotktMapContainsKey", Bool, SourceCast(Any, SourceLocal("key")));
        BirCast Get() => SourceCast(V, SourceSlot("dotktMapGet", Any, SourceCast(Any, SourceLocal("key"))));
        BirCallStatic NativeHelper(string name, TypeNode ret, params IBirExpression[] args) => SourceHelper(refs, projectReferencedValueType, name, ret,
            Array.Empty<TypeNode>(), new[] { new TypeNode.Fqn("kotlin.Any") }
                .Concat(args.Select(_ => (TypeNode)new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")))).ToArray(),
            new[] { (IBirExpression)SourceSelf() }.Concat(args).ToArray());
        BirNativeInstance PairPart(string part, TypeNode type)
        {
            if (!refs.TryExternalPropertyAccessor("System.Collections.Generic.KeyValuePair", part, "get",
                    0, 0, Array.Empty<TypeNode>(), new[] { K, V }, out var owner, out _, out var accessor))
                throw new InvalidOperationException($"Native map entry has no declared '{part}' getter");
            return new() { Type = F(owner, SourceType(K), SourceType(V)), Method = accessor,
                Ret = SourceType(type), Recv = SourceLocal("pair"),
                ArgTypes = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>() };
        }
        BirConditional PairMatches()
        {
            var comparer = F("System.Collections.Generic.EqualityComparer", V);
            var eq = SourceCall(comparer, "Equals", Bool,
                new BirCallStatic { Owner = SourceType(comparer), Method = "get_Default",
                    Sig = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>(), Ret = SourceType(comparer) },
                new[] { V, V }, SourceCast(V, SourceSlot("dotktMapGet", Any, SourceCast(Any, PairPart("Key", K)))), PairPart("Value", V));
            return SourceCondition(SourceSlot("dotktMapContainsKey", Bool, SourceCast(Any, PairPart("Key", K))), eq, SourceBool(false));
        }
        BirExpressionStatement Store(IBirExpression value) => SourceExpr(new BirByRefStore {
            Ptr = SourceLocal("value"), Elem = SourceType(V), Value = OptionalField<IBirExpression>.Of(value),
        });
        BirParameter[] KeyParams() => new[] { SourceParameter("key", K) };
        BirParameter[] PairParams() => new[] { SourceParameter("pair", Pair) };
        BirFunctionDeclaration Define(TypeNode owner, string member, TypeNode ret, BirParameter[] parameters,
            Func<IBirStatement[]> body) => SourceMethod(owner, member, ret, parameters,
                declarationOnly ? new IBirStatement[] { SourceThrow("System.NotSupportedException") } : body());
        var methods = new List<BirFunctionDeclaration> {
            Define(dictionary, "get_Item", V, KeyParams(), () => new IBirStatement[] {
                SourceIf(Contains(), SourceReturn(Get())), SourceThrow("System.Collections.Generic.KeyNotFoundException") }),
            Define(dictionary, "ContainsKey", Bool, KeyParams(), () => new IBirStatement[] { SourceReturn(Contains()) }),
            Define(dictionary, "set_Item", Void, new[] { SourceParameter("key", K), SourceParameter("value", V) },
                () => new IBirStatement[] { SourceExpr(NativeHelper("clrAuthoredMapSet", Void,
                    SourceCast(Any, SourceLocal("key")), SourceCast(Any, SourceLocal("value")))) }),
            Define(dictionary, "Add", Void, new[] { SourceParameter("key", K), SourceParameter("value", V) },
                () => new IBirStatement[] { SourceExpr(NativeHelper("clrAuthoredMapAdd", Void,
                    SourceCast(Any, SourceLocal("key")), SourceCast(Any, SourceLocal("value")))) }),
            Define(dictionary, "Remove", Bool, KeyParams(), () => new IBirStatement[] {
                SourceReturn(NativeHelper("clrAuthoredMapRemove", Bool, SourceCast(Any, SourceLocal("key")))) }),
            Define(dictionary, "TryGetValue", Bool, new[] { SourceParameter("key", K), SourceParameter("value", new TypeNode.ByRef(V)) },
                () => new IBirStatement[] { SourceIf(Contains(), Store(Get()), SourceReturn(SourceBool(true))),
                    Store(new BirDefaultValue { Type = SourceType(V) }), SourceReturn(SourceBool(false)) }),
            Define(collection, "get_Count", Int, Array.Empty<BirParameter>(), () => new IBirStatement[] { SourceReturn(SourceSlot("dotktMapSize", Int)) }),
            Define(collection, "get_IsReadOnly", Bool, Array.Empty<BirParameter>(), () => new IBirStatement[] {
                SourceReturn(SourceNot(SourceIs(F(KotlinCollectionSlotSynthesis.MutableMapSlots), SourceSelf()))) }),
            Define(collection, "Add", Void, PairParams(), () => new IBirStatement[] {
                SourceExpr(NativeHelper("clrAuthoredMapAdd", Void, SourceCast(Any, PairPart("Key", K)), SourceCast(Any, PairPart("Value", V)))) }),
            Define(collection, "Clear", Void, Array.Empty<BirParameter>(), () => new IBirStatement[] { SourceExpr(NativeHelper("clrAuthoredMapClear", Void)) }),
            Define(collection, "Contains", Bool, PairParams(), () => new IBirStatement[] { SourceReturn(PairMatches()) }),
            Define(collection, "Remove", Bool, PairParams(), () => new IBirStatement[] {
                SourceExpr(NativeHelper("clrAuthoredMapCheckMutable", Void)),
                SourceIf(PairMatches(), SourceReturn(NativeHelper("clrAuthoredMapRemove", Bool, SourceCast(Any, PairPart("Key", K))))),
                SourceReturn(SourceBool(false)) }),
        };
        foreach (var (member, slot, element) in new[] { ("get_Keys", "dotktMapKeys", K), ("get_Values", "dotktMapValues", V) })
            methods.Add(Define(dictionary, member, F(Collection, element), Array.Empty<BirParameter>(), () => new IBirStatement[] {
                SourceReturn(SourceCast(F(Collection, element), SourceHelper(refs, projectReferencedValueType, "clrAuthoredMapCollection", Any, new[] { element },
                    new TypeNode[] { new TypeNode.Fqn("kotlin.Any") }, SourceSlot(slot, Any)))) }));
        var copy = CollectionBclSlotSynthesis.CopyToSource(Pair);
        var copyBody = new List<IBirStatement>(copy.Body.OrEmpty());
        copyBody.Insert(0, SourceIf(SourceBinary(">", SourceSlot("dotktMapSize", Int), SourceBinary("-",
            SourceCall(F("System.Array"), "get_Length", Int, SourceLocal("array"), Array.Empty<TypeNode>()), SourceLocal("arrayIndex"))),
            SourceThrow("System.ArgumentException")));
        copyBody.Insert(0, SourceIf(SourceBinary("<", SourceLocal("arrayIndex"), new BirConstant {
            Type = SourceType(Int), Value = new BirLiteral.IntegerLiteral(0) }), SourceThrow("System.ArgumentOutOfRangeException")));
        copyBody.Insert(0, SourceIf(SourceBinary("==", SourceCast(Any, SourceLocal("array")), new BirConstant {
            Type = SourceType(Any), Value = new BirLiteral.NullLiteral() }), SourceThrow("System.ArgumentNullException")));
        var copyParameters = copy.Params.OrEmpty();
        copy.Params = default; copy.Body = default;
        methods.Add(Define(collection, "CopyTo", Void, copyParameters, () => copyBody.ToArray()));
        BirNew Enumerator() => new() { Type = F(EnumeratorName, Pair), LocalCtorIndex = 0,
            ArgTypes = new[] { SourceType(KotlinIteratorPhysicalProtocol.Carrier(refs)) },
            Args = new IBirExpression[] { SourceCast(KotlinIteratorPhysicalProtocol.Carrier(refs), SourceHelper(refs, projectReferencedValueType,
                "clrAuthoredMapIterator", Any, new[] { K, V }, new TypeNode[] { new TypeNode.Fqn("kotlin.Any") },
                SourceSlot("dotktMapEntries", Any))) } };
        methods.Add(Define(enumerable, "GetEnumerator", iterator, Array.Empty<BirParameter>(), () => new IBirStatement[] { SourceReturn(Enumerator()) }));
        methods.Add(Define(F(RawEnumerable), "GetEnumerator", F("System.Collections.IEnumerator"),
            Array.Empty<BirParameter>(), () => new IBirStatement[] { SourceReturn(Enumerator()) }));
        AppendNativeFacesSource(refs, projectReferencedValueType, declarationOnly, methods);
        return new() { Name = CarrierName, Kind = "interface", Vis = "public", Generated = true,
            TypeParams = new[] { new BirTypeParameter { Name = "K" }, new BirTypeParameter { Name = "V" } },
            Base = OptionalField<TypeNode>.Null, Fields = Array.Empty<BirFieldDeclaration>(), Ctors = Array.Empty<BirConstructor>(),
            Methods = methods.ToArray(), Interfaces = new TypeNode[] { SourceType(dictionary),
                F("System.Collections.Generic.IReadOnlyDictionary", SourceType(K), SourceType(V)),
                F("System.Collections.IDictionary"), F(KotlinCollectionSlotSynthesis.MapSlots) } };
    }
}
