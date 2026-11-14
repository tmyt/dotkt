using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class MemberCallSubstitution
{
    // Top-level fun names DEFINED in the current compilation (this assembly's file-class statics). A `callStatic
    // owner=null` to one of these stays owner-less (ilemit's FindStatic finds the local sibling) — only a name NOT
    // defined here is a candidate for referenced-stdlib owner attribution. Single-threaded per run, so static is fine.
    static IReadOnlySet<string> _localTopLevelFns = new HashSet<string>(StringComparer.Ordinal);
    // Whether to attribute referenced top-level stdlib funs to their file-class owner (APP build only; OFF for the
    // stdlib self-build, where every such fun is local — see the StdlibMode == App gate at the call site in the Driver).
    static bool _attributeTopLevelOwner;
    // Real Kotlin iterator declarations, including abstract and inherited slots, take precedence over the
    // IEnumerable adapter. The adapter cannot preserve an authored MutableIterator return contract.
    static HashSet<string> _typesWithDeclaredIterator = new(StringComparer.Ordinal);
    static IReadOnlyDictionary<string, SupertypeGraph.Def> _iteratorTypeDefinitions;
    internal readonly record struct LocalPropertyAccessorKey(
        string Owner, string Property, string Kind, int MethodArity, int ParameterCount);

    internal sealed record LocalPropertyAccessor(string PhysicalName, TypeNode[] Parameters);

    // Module-wide local property declarations, including their complete Kotlin accessor signature. A call on a local
    // Kotlin override must be recognized before an external ancestor PropertyInfo is considered (notably a Kotlin
    // newslot overriding a non-virtual CLR property), but its physical name is consumed only after the declaration
    // rename passes have actually run.
    static IReadOnlyDictionary<LocalPropertyAccessorKey, IReadOnlyList<LocalPropertyAccessor>> _localPropertyAccessors
        = new Dictionary<LocalPropertyAccessorKey, IReadOnlyList<LocalPropertyAccessor>>();
    static IReadOnlySet<string> _localPropertyOwners = new HashSet<string>(StringComparer.Ordinal);
    // #76: the four unsigned specialized array value classes -> their SIGNED backing-array element FQN. kotc emits
    // `kotlin.U*Array` as a faithful array identity (like signed IntArray) and STOPS emitting/decomposing the value
    // class; bir2cir OWNS both the native representation (via PrimArrayElem -> the UNSIGNED native array byte[]/uint[]/
    // ...) AND the value-class `.storage` erasure. The backing field `storage` is declared as the SIGNED array
    // (UByteArray.storage : ByteArray = sbyte[], UIntArray.storage : IntArray = int[], ...). Since same-size same-
    // underlying-primitive arrays are assignment-compatible (ECMA-335 array-element-compatible-with — byte[]<->sbyte[],
    // ushort[]<->short[], uint[]<->int[], ulong[]<->long[]), a `storage` read is a runtime-valid reinterpret cast of
    // the receiver to the signed array, and the wrap-ctor(storage: SignedArray) is the inverse reinterpret to the
    // unsigned native array — NOT a real field access / construction. These nodes appear ONLY in the runtime-stdlib
    // self-build (consumer code never touches `.storage`); the ref build squashes bodies so it needs nothing here, and
    // MemberCallSubstitution runs on the !RefBuild path only.
    static readonly IReadOnlyDictionary<string, string> UnsignedArraySignedElem = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["kotlin.UByteArray"] = "kotlin.Byte",
        ["kotlin.UShortArray"] = "kotlin.Short",
        ["kotlin.UIntArray"] = "kotlin.Int",
        ["kotlin.ULongArray"] = "kotlin.Long",
    };

    // A star-projection/erased type-arg token: `object`/`kotlin.Any`, possibly nullable/oblivious-wrapped (a star K/V
    // projects to `Any?`, i.e. `{t:nullable,of:kotlin.Any}` post-#48). Used by the Map<*,*> extension guard (#74a).
    static bool IsErasedAny(TypeNode t) => t switch
    {
        TypeNode.Nullable n => IsErasedAny(n.Of),
        TypeNode.Oblivious o => IsErasedAny(o.Of),
        TypeNode.Fqn { Args: null, Name: "object" or "kotlin.Any" } => true,
        _ => false,
    };

    // The struct-ness oracle, for the `Array<X?>` element canonicalization (#86 D2) the array factories below apply.
    static ValueTypeOracle _isValue = _ => false;
    static GenericRepresentationPolicy _representations;

    internal sealed record LocalFactory(string CollectionKind, string ArrayKind, string ArrayElementHint, int[] VarargPositions,
        NullableRepresentationFrame Frame);
    static IReadOnlyDictionary<string, LocalFactory> _localFactories;
    static IReadOnlySet<string> _localDeclarationIds;


    public static IReadOnlyDictionary<LocalPropertyAccessorKey, IReadOnlyList<LocalPropertyAccessor>>
        CollectLocalPropertyAccessors(IEnumerable<JsonNode> roots)
    {
        var collector = new LocalPropertyCollector();
        void Walk(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (LocalPropertyCollector.IsOwner(Str(obj["name"]), obj["kind"] != null) && obj["methods"] is JsonArray methods)
                    foreach (var method in methods.OfType<JsonObject>())
                    {
                        var parameters = method["params"] as JsonArray;
                        collector.Add(Str(obj["name"]), KotlinPropertyAccessors.IsPhysicalSlotBridge(method),
                            Str(method[KotlinPropertyAccessors.SourceNameKey]), Str(method[KotlinPropertyAccessors.KindKey]),
                            Str(method["name"]), (method["typeParams"] as JsonArray)?.Count ?? 0, parameters?.Count ?? 0,
                            () => parameters?.OfType<JsonObject>().Select(parameter => TypeJson.Read(parameter["type"])).ToArray());
                    }
                foreach (var child in obj.Select(pair => pair.Value).ToArray())
                    if (child != null) Walk(child);
            }
            else if (node is JsonArray array)
                foreach (var child in array.ToArray()) if (child != null) Walk(child);
        }
        foreach (var root in roots) Walk(root);
        return collector.Finish();
    }

    internal static bool TryResolveLocalPropertyAccessor(
        IReadOnlyDictionary<LocalPropertyAccessorKey, IReadOnlyList<LocalPropertyAccessor>> accessors,
        string owner, string property, string kind, int methodArity,
        int parameterCount, IReadOnlyList<TypeNode> signature, TypeNode[] ownerTypeArguments,
        out string physicalName)
    {
        physicalName = null;
        var key = new LocalPropertyAccessorKey(owner, property, kind, methodArity, parameterCount);
        if (accessors == null || !accessors.TryGetValue(key, out var declared)) return false;
        var matches = declared.Where(candidate => signature == null || candidate.Parameters
                .Select(parameter => ownerTypeArguments == null
                    ? parameter
                    : SupertypeGraph.SubstOwnerTvs(parameter, ownerTypeArguments))
                .Select((parameter, index) =>
                    ReferenceMetadataIndex.AccessorDeclarationDescribesCall(parameter, signature[index]))
                .All(match => match))
            .Select(candidate => candidate.PhysicalName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (matches.Length != 1) return false;
        physicalName = matches[0];
        return true;
    }

    internal static HashSet<string> CollectDeclaredIteratorTypes(BirFileDeclaration root)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        void Walk(BirGraphValue value)
        {
            if (value is IBirExecutablePayload) return;
            var (name, methods) = value switch {
                BirClassDefinition owner => (owner.Name.OrDefault(), owner.Methods.OrEmpty()),
                BirInterfaceDefinition owner => (owner.Name.OrDefault(), owner.Methods.OrEmpty()),
                BirEnumDefinition owner => (owner.Name.OrDefault(), owner.Methods.OrEmpty()),
                _ => ((string)null, Array.Empty<BirFunctionDeclaration>()),
            };
            if (name != null && methods.Any(method => IsDeclaredIterator(method.Name.OrDefault(),
                method.PropertyName.OrDefault(), method.PropertyAccessor.OrDefault(), method.Static.OrDefault(),
                method.TypeParams.OrEmpty().Length, method.Params.HasValue ? method.Params.Value.Length : null)))
                result.Add(name);
            value.VisitChildren(Walk);
        }
        Walk(root);
        return result;
    }

    static bool IsDeclaredIterator(string name, string property, string accessor, bool isStatic, int arity, int? parameters)
        => name == "iterator" && !KotlinPropertyAccessors.HasIdentity(property, accessor)
            && !isStatic && arity == 0 && parameters == 0;

    internal static bool HasDeclaredIterator(TypeNode.Fqn owner, IReadOnlySet<string> declaredTypes,
        IReadOnlyDictionary<string, SupertypeGraph.Def> definitions, ReferenceMetadataIndex references,
        IReadOnlyDictionary<string, string> aliases, bool sourceFrames = false)
    {
        foreach (var (candidate, _) in SupertypeGraph.Reachable(
            new SupertypeGraph.Def { Base = owner }, definitions, references, sourceFrames))
        {
            // An alias's source declaration is implemented through its native face, not a Kotlin MethodDef.
            if (aliases?.ContainsKey(candidate.Name) == true) continue;
            if (declaredTypes.Contains(candidate.Name)
                || references?.DeclaresExactInstanceMember(candidate.Name, "iterator", 0,
                    Array.Empty<TypeNode>(), candidate.Args, sourceNames: true) == true)
                return true;
        }
        return false;
    }

    static TypeNode CanonicalArrayElem(TypeNode elem)
        => elem == null ? null : NullableGenericErasure.EraseArgument(elem, _isValue);

    // Explicit property access bits are shared with the owning call renderer.
    const int ClrPropRead = 1, ClrPropWrite = 2;

    // The physical primitive compareTo target selected from the declared owner or receiver.
    static string CompareToBclTarget(string name) => name switch
    {
        "kotlin.Int" or "System.Int32" or "int" => "System.Int32",
        "kotlin.Long" or "System.Int64" or "long" => "System.Int64",
        "kotlin.Byte" or "System.SByte" or "sbyte" => "System.SByte",
        "kotlin.Short" or "System.Int16" or "short" => "System.Int16",
        "kotlin.Float" or "System.Single" or "float" => "System.Single",
        "kotlin.Double" or "System.Double" or "double" => "System.Double",
        "kotlin.Char" or "System.Char" or "char" => "System.Char",
        "kotlin.Boolean" or "System.Boolean" or "bool" => "System.Boolean",
        _ => null,
    };

    static readonly TypeNode ObjType = new TypeNode.Fqn("object");
    // See through a nullability wrapper (#37/#48): the `in`/`out` variance over-approximation `kotlin.Any` is emitted as
    // the nullable-wrapped `Any?` (`{t:nullable,of:kotlin.Any}`), so the object-ish test on a map/collection owner arg
    // must unwrap it to keep the CollElemArg/MapKvArgs constraint-recovery firing (pre-#48 it saw a bare Fqn `kotlin.Any`).
    static bool IsObjType(TypeNode t) => t switch
    {
        TypeNode.Nullable n => IsObjType(n.Of),
        TypeNode.Oblivious o => IsObjType(o.Of),
        TypeNode.Fqn { Args: null } f => f.Name == "object" || f.Name == "kotlin.Any",
        _ => false,
    };
    static string CollectionSlotHelper(string member, int argCount, string ownerFqn) => (member, argCount) switch
    {
        ("add" or "Add", 1) => "clrCollAdd",
        ("addAll", 1) => "clrCollAddAll",
        ("removeAll", 1) => "clrCollRemoveAll",
        ("retainAll", 1) => "clrCollRetainAll",
        ("addAll", 2) when ownerFqn == "kotlin.collections.MutableList" => "clrListAddAllAt",
        _ => null,
    };

    // The routing above must be COMPLETE for a CLR-bound INTERFACE owner: once this pass is done, no Kotlin-spelled
    // member may reach the BCL owner under a name that owner does not declare. The test is the DERIVED rule, not a
    // name list — ask the resolved physical type whether it declares a member of this exact name. If it does not, an
    // earlier rule was obliged to route the call, and failing to do so used to degrade into a runtime
    // `GetType().GetMethod(name).Invoke(...)` lookup. Refuse at compile time instead, naming owner.member.
    //
    // Scoped to a KOTLIN SPELLING (lowercase-camelCase), which is the same criterion the Rule-4 comment above states:
    // an already-renamed BCL spelling (`MoveNext`, `Add`) legitimately arrives here from an earlier binding and is
    // resolved by name on the BCL owner. A physical accessor name (`get_Count`, `add_Changed`) needs no exclusion of
    // its own — it is only ever produced by a binding that already proved the owner declares it, so the DERIVED test
    // below passes it; this rule never parses an accessor spelling (#397). An owner that does not resolve is not
    // evidence of a miss and is left alone.
    static void AssertInterfaceMemberRouted(string ownerFqn, TypeNode.Fqn ownerFqnNode, string member,
        ReferenceMetadataIndex refs)
    {
        if (!IsKotlinSpelledMember(member)
            || !refs.TryDeclaresAccessibleInstanceMethod(
                ownerFqn, ownerFqnNode?.Args?.Length ?? 0, member, out var declares)
            || declares)
            return;
        throw new InvalidOperationException(
            $"bir2cir: unrouted Kotlin member '{ownerFqn}.{member}' — the CLR interface its @ClrTypeAlias names "
            + $"declares no '{member}', so the member has no physical slot. A Kotlin member in that position must be "
            + "routed to a physical implementation (for the collection interfaces, a kotlin.collections."
            + "ClrCollectionDefaults dispatcher plus the DotKt.Runtime.CompilerServices Kotlin slot interface that "
            + "makes an override reachable). Add the routing or the missing helper; do not let it fall through to a "
            + "member nothing can link.");
    }

    // A Kotlin source spelling as opposed to an already-bound CLR member name: lowercase-camelCase, and not one of
    // the compiler's own `dotkt`-marked synthetics (whose owner is always already resolved).
    static bool IsKotlinSpelledMember(string member) =>
        !string.IsNullOrEmpty(member) && char.IsLower(member[0])
        && !member.StartsWith("dotkt", StringComparison.Ordinal);

    // Kotlin collection-interface member -> the rt ClrCollectionDefaults static (recv-first, generic over elem).
    // iterator() and listIterator() are handled separately (different owner / default index).
    static readonly Dictionary<string, string> CollectionDefaults = new(StringComparer.Ordinal)
    {
        ["isEmpty"] = "clrCollIsEmpty",
        ["contains"] = "clrCollContains",
        ["containsAll"] = "clrCollContainsAll",
        ["indexOf"] = "clrListIndexOf",
        ["lastIndexOf"] = "clrListLastIndexOf",
        ["subList"] = "clrListSubList",
    };

    internal static void BindAuthoredHelper(BirCallStatic call, ReferenceMetadataIndex refs,
        CollectionHelperBinding.CollectionSourceContext context, ValueTypeOracle isValue = null)
    {
        var owner = ((TypeNode.Fqn)call.Owner.Value).Name;
        var arguments = call.TypeArgs.Value;
        var helper = refs.AuthoredKotlinHelper(owner, call.Method.Value, arguments.Length, call.Sig.Value);
        call.TypeArgs = CloseAuthoredHelperArguments(arguments, helper.NullableFrame, context.OwnerFrame,
            context.MethodFrame, isValue ?? _isValue, owner, call.Method.Value);
        call.Sig = helper.ParamTypeNodes.Select(BirTypeLowering.CanonicalPhysicalSlotType).ToArray();
        call.DeclarationId = helper.DeclarationId;
    }

    static TypeNode[] CloseAuthoredHelperArguments(TypeNode[] arguments, NullableRepresentationFrame helperFrame,
        NullableRepresentationFrame ownerFrame, NullableRepresentationFrame methodFrame, ValueTypeOracle isValue,
        string owner, string method)
    {
        TypeNode NullablePhysicalArgument(TypeNode argument)
        {
            if (argument is TypeNode.Tv variable)
            {
                var frame = variable.Scope == "type" ? ownerFrame : methodFrame;
                if (frame == null) throw new InvalidOperationException("Compiler helper requires a missing caller nullable frame");
                return frame.SemanticVariable(variable) is TypeNode.Nullable
                    ? variable : frame.NullableVariable((TypeNode.Tv)frame.SemanticVariable(variable));
            }
            return NullableGenericErasure.EraseArgument(new TypeNode.Nullable(argument), isValue);
        }
        try
        {
            // Late alias arguments already name the selected storage element;
            // the helper's roles must close from that same element, not another ABI projection.
            return helperFrame == null ? arguments : helperFrame.Close(arguments, argument => argument,
                NullablePhysicalArgument, argument => argument, NullablePhysicalArgument);
        }
        catch (ArgumentException error)
        {
            throw new InvalidOperationException($"Compiler helper {owner}.{method} frame closure failed: {error.Message}", error);
        }
    }

    internal enum CollectionOperationPhase { Source, Physical }

    static TypeNode[] CollectionPreferenceArguments(TypeNode[] arguments, NullableRepresentationFrame frame,
        CollectionOperationPhase phase) => arguments == null ? null
        : phase == CollectionOperationPhase.Source ? arguments : frame?.OrdinaryArguments(arguments) ?? arguments;

    readonly record struct MapPreferenceArgument(TypeNode Type, int Variance);
    readonly record struct MapPreferencePlan(MapPreferenceArgument Key, MapPreferenceArgument Value)
    {
        internal bool Unknown => Key.Type == null && Value.Type == null;
    }

    const string MapPreferenceOwner = "kotlin.collections.ClrNativeMapAccessKt";
    const string MapPreferenceMethod = "clrMapViewPreference";
    static TypeNode[] MapPreferenceSignature() => new TypeNode[] {
        new TypeNode.Nullable(ObjType), new TypeNode.Fqn("kotlin.Int"),
        new TypeNode.Nullable(ObjType), new TypeNode.Fqn("kotlin.Int"),
    };

    static MapPreferencePlan SelectMapPreference(TypeNode.Fqn owner, ReferenceMetadataIndex refs, CollectionOperationPhase phase)
    {
        var arguments = CollectionPreferenceArguments(owner.Args, refs?.NullableTypeFrames.GetValueOrDefault(owner.Name), phase);
        if (arguments is not { Length: 2 }) return default;
        MapPreferenceArgument Argument(TypeNode argument, int variance)
        {
            if (argument is TypeNode.Projection projection)
            {
                variance = projection.Variance == "out" ? 1 : -1;
                argument = projection.Of;
            }
            if (HasProjection(argument)) return default;
            var physical = phase == CollectionOperationPhase.Source ? argument
                : NullableGenericErasure.EraseArgument(argument, refs == null ? _isValue : refs.IsValueType);
            return new(physical, variance);
        }
        return new(Argument(arguments[0], 0), Argument(arguments[1], owner.Name == "kotlin.collections.MutableMap" ? 0 : 1));
    }

    internal static IBirExpression StaticMapPreferenceValue(TypeNode.Fqn owner, ReferenceMetadataIndex refs,
        CollectionOperationPhase phase = CollectionOperationPhase.Physical)
    {
        BirConstant Unknown() => new() { Type = new TypeNode.Nullable(ObjType), Value = new BirLiteral.NullLiteral() };
        IBirExpression Token(MapPreferenceArgument argument) => argument.Type == null ? Unknown()
            : new BirClassReference { Type = argument.Type };
        BirConstant Variance(int value) => new() { Type = new TypeNode.Fqn("kotlin.Int"), Value = new BirLiteral.IntegerLiteral(value) };
        var plan = SelectMapPreference(owner, refs, phase);
        return plan.Unknown ? Unknown() : new BirCallStatic {
            Owner = new TypeNode.Fqn(MapPreferenceOwner), Method = MapPreferenceMethod, TypeArgs = Array.Empty<TypeNode>(),
            Sig = MapPreferenceSignature(), Ret = ObjType,
            Args = new[] { Token(plan.Key), Variance(plan.Key.Variance), Token(plan.Value), Variance(plan.Value.Variance) },
        };
    }

    static bool HasProjection(TypeNode type) => type switch {
        TypeNode.Star or TypeNode.Projection => true,
        TypeNode.Nullable n => HasProjection(n.Of),
        TypeNode.Oblivious o => HasProjection(o.Of),
        TypeNode.Fqn f => f.Args?.Any(HasProjection) == true,
        TypeNode.Array a => HasProjection(a.Elem),
        _ => false,
    };

    internal static void StaticCollectionPreferenceSelfTest()
    {
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var variable = new TypeNode.Tv("method", 1);
        var companionFrame = new NullableRepresentationFrame(1, Array.Empty<int>(), new[] { 1, 0 }, new[] { 0 });
        var sourceArgument = new TypeNode.Tv("method", 3);
        if (CollectionPreferenceArguments(new TypeNode[] { sourceArgument }, companionFrame, CollectionOperationPhase.Source)
                is not [TypeNode.Tv { Scope: "method", I: 3 }]
            || CollectionPreferenceArguments(new TypeNode[] { new TypeNode.Tv("method", 4), variable }, companionFrame,
                CollectionOperationPhase.Physical) is not [TypeNode.Tv { Scope: "method", I: 1 }])
            throw new InvalidOperationException("Static collection preference confused source arguments with a companion frame");
        TypeNode Preference(TypeNode element) => CollectionPreferenceType(
            new TypeNode.Fqn("kotlin.collections.Set", new[] { element }), refs,
            "System.Collections.Generic.IReadOnlySet`1", CollectionOperationPhase.Physical);
        BirCallStatic Render(CollectionOperationPlan plan, string member, int count = 0) =>
            CollectionHelperBinding.RenderCollectionOperation(plan, new BirCallInstance {
                Method = member, Recv = new BirLocal { Name = "source" },
                Args = Enumerable.Range(0, count).Select(index => (IBirExpression)new BirConstant {
                    Type = new TypeNode.Fqn("kotlin.Int"), Value = new BirLiteral.IntegerLiteral(index),
                }).ToArray(),
            }, refs, CollectionOperationPhase.Source);
        int Integer(IBirExpression expression) => checked((int)((BirLiteral.IntegerLiteral)((BirConstant)expression).Value.Value).Value);
        var set = new TypeNode.Fqn("kotlin.collections.Set", new[] { variable });
        var count = Render(SelectStaticCollectionOperation("size", "get", 0, true, set, variable, null), "size");
        var bare = new TypeNode.Fqn("kotlin.collections.Set");
        var bareCount = Render(SelectStaticCollectionOperation("size", "get", 0, true, bare, ObjType, null), "size");
        if (count.TypeArgs.Value.Length != 0 || count.Sig.Value.Length != 3
            || bareCount.Args.Value[1] is not BirConstant { Value.Value: BirLiteral.NullLiteral })
            throw new InvalidOperationException("Static collection helper lost its explicit non-generic vector or bare-view role");
        if (Preference(new TypeNode.Star()) != null
            || Preference(variable) is not TypeNode.Fqn { Args: [TypeNode.Tv { Scope: "method", I: 1 }] }
            || Preference(ObjType) is not TypeNode.Fqn { Name: "System.Collections.Generic.IReadOnlySet`1", Args.Length: 1 }
            || Preference(new TypeNode.Projection("out", ObjType)) != null)
            throw new InvalidOperationException("Static collection preference lost exact/star or caller-frame ownership");
        foreach (var element in new TypeNode[] {
            new TypeNode.Array(new TypeNode.Star()),
            new TypeNode.Nullable(new TypeNode.Array(new TypeNode.Star())),
            new TypeNode.Array(new TypeNode.Array(new TypeNode.Projection("out", ObjType))),
        })
        {
            if (Preference(element) != null)
                throw new InvalidOperationException("An array-nested projection manufactured a closed collection witness");
            var map = StaticMapPreferenceValue(new TypeNode.Fqn("kotlin.collections.Map", new[] { element, ObjType }),
                refs, CollectionOperationPhase.Source) as BirCallStatic;
            if (map == null || map.Method.Value != "clrMapViewPreference"
                || map.Args.Value[0] is not BirConstant { Value.Value: BirLiteral.NullLiteral }
                || map.Args.Value[2] is not BirClassReference)
                throw new InvalidOperationException("An array-nested projection lost the independent Map witness roles");
        }
        if (Preference(new TypeNode.Array(new TypeNode.Fqn("kotlin.String"))) is not TypeNode.Fqn {
                Args: [TypeNode.Array { Elem: TypeNode.Fqn { Name: "kotlin.String" } }] })
            throw new InvalidOperationException("A concrete array lost its closed collection witness");
        var projectedList = new TypeNode.Fqn("kotlin.collections.MutableList", new TypeNode[] { new TypeNode.Projection("in", ObjType) });
        var append = Render(SelectCollectionMutation("add", 1, projectedList, OwnerElemArg(projectedList), true,
            new TypeNode.Fqn(projectedList.Name, new[] { variable }), null), "add", 1);
        if (append.Method.Value != "clrProjectedListAdd" || append.Args.Value.Length != 3
            || append.Args.Value[1] is not BirClassReference { Type.Value: TypeNode.Fqn {
                Name: "System.Collections.Generic.IList`1", Args: [TypeNode.Tv { Scope: "method", I: 1 }] } })
            throw new InvalidOperationException("Projected append lost the selected List witness");
        var mutableOwner = new TypeNode.Fqn("kotlin.collections.MutableCollection", new TypeNode[] { new TypeNode.Star() });
        var mutableCount = Render(SelectStaticCollectionOperation("size", "get", 0, true, mutableOwner,
            OwnerElemArg(mutableOwner), new TypeNode.Fqn(mutableOwner.Name, new[] { variable })), "size");
        if (mutableCount.Method.Value != "projectedViewCountErased"
            || mutableCount.Args.Value[1] is not BirClassReference { Type.Value: TypeNode.Fqn {
                Name: "System.Collections.Generic.ICollection`1", Args: [TypeNode.Tv { Scope: "method", I: 1 }] } }
            || Integer(mutableCount.Args.Value[2]) != 5)
            throw new InvalidOperationException("Mutable collection Count lost its selected source view before classifier erasure");
        foreach (var (member, arity, helper) in new[] {
            ("get", 1, "clrProjectedViewGet"), ("isEmpty", 0, "clrProjectedViewIsEmpty"),
            ("contains", 1, "clrProjectedViewContains"), ("containsAll", 1, "clrProjectedViewContainsAll"),
            ("iterator", 0, "clrProjectedViewIterator"), ("indexOf", 1, "clrProjectedViewIndexOf"),
            ("lastIndexOf", 1, "clrProjectedViewLastIndexOf"), ("listIterator", 0, "clrProjectedViewListIterator"),
            ("listIterator", 1, "clrProjectedViewListIteratorAt"), ("subList", 2, "clrProjectedViewSubList"),
        })
        {
            var owner = new TypeNode.Fqn("kotlin.collections.List", new[] { variable });
            var operation = Render(SelectStaticCollectionOperation(member, null, arity, true, owner, variable, null), member, arity);
            if (operation.Method.Value != helper || Integer(operation.Args.Value[2]) != 3
                || operation.Args.Value.Length != arity + 3
                || operation.Args.Value[1] is not BirClassReference { Type.Value: TypeNode.Fqn {
                    Name: "System.Collections.Generic.IReadOnlyList`1", Args: [TypeNode.Tv { Scope: "method", I: 1 }] } }
                || CollectionHelperTypes("kotlin.collections.ClrCollectionDefaultsKt", helper).Length != operation.Args.Value.Length)
                throw new InvalidOperationException("Static List operation lost its shared family, caller view or exact helper shape: " + member);
        }
        foreach (var (owner, family, native) in new[] {
            ("kotlin.collections.MutableSet", 4, "System.Collections.Generic.ISet`1"),
            ("kotlin.collections.MutableCollection", 5, "System.Collections.Generic.ICollection`1"),
            ("kotlin.collections.MutableList", 6, "System.Collections.Generic.IList`1"),
        })
        {
            var declaration = new TypeNode.Fqn(owner, new TypeNode[] { new TypeNode.Star() });
            var operation = Render(SelectStaticCollectionOperation("isEmpty", null, 0, true, declaration,
                OwnerElemArg(declaration), new TypeNode.Fqn(owner, new[] { variable })), "isEmpty");
            if (operation.Method.Value != "clrProjectedViewIsEmpty" || Integer(operation.Args.Value[2]) != family
                || operation.Args.Value[1] is not BirClassReference { Type.Value: TypeNode.Fqn selected }
                || selected.Name != native || selected.Args is not [TypeNode.Tv { Scope: "method", I: 1 }])
                throw new InvalidOperationException("Mutable emptiness lost its exact source witness or family");
        }
        foreach (var owner in new[] { "kotlin.collections.Collection", "kotlin.collections.Set", "kotlin.collections.List" })
        {
            var declaration = new TypeNode.Fqn(owner, new TypeNode[] { new TypeNode.Fqn("kotlin.Any") });
            var operation = Render(SelectStaticCollectionOperation("isEmpty", null, 0, true, declaration,
                OwnerElemArg(declaration), new TypeNode.Fqn(owner, new[] { variable })), "isEmpty");
            if (operation.Args.Value[1] is not BirClassReference { Type.Value: TypeNode.Fqn { Args: [TypeNode.Fqn { Name: "kotlin.Any" }] } })
                throw new InvalidOperationException("Variant collection operation borrowed an initializer's exact view");
        }
        var mapOwner = new TypeNode.Fqn("kotlin.collections.Map", new TypeNode[] { variable, ObjType });
        var mapPreference = (BirCallStatic)StaticMapPreferenceValue(mapOwner, refs, CollectionOperationPhase.Source);
        var partialMapPreference = (BirCallStatic)StaticMapPreferenceValue(new TypeNode.Fqn(mapOwner.Name,
            new TypeNode[] { variable, new TypeNode.Star() }), refs, CollectionOperationPhase.Source);
        var projectedMapPreference = (BirCallStatic)StaticMapPreferenceValue(new TypeNode.Fqn(mapOwner.Name,
            new TypeNode[] { new TypeNode.Projection("out", variable), ObjType }), refs, CollectionOperationPhase.Source);
        var bounded = new CollectionHelperBinding.CollectionSourceContext();
        bounded.Constraints["method:0"] = new() { new TypeNode.Tv("method", 2) };
        bounded.Constraints["method:2"] = new() { mapOwner };
        var boundedOwner = CollectionMapBound(bounded.Receiver(new BirLocal { Name = "source", Sty = new TypeNode.Tv("method", 0) }), bounded.Constraints);
        var boundedMapPreference = (BirCallStatic)StaticMapPreferenceValue(boundedOwner, refs, CollectionOperationPhase.Source);
        if (mapPreference.Method.Value != "clrMapViewPreference"
            || ((BirClassReference)mapPreference.Args.Value[0]).Type.Value != variable || Integer(mapPreference.Args.Value[3]) != 1
            || partialMapPreference.Args.Value[2] is not BirConstant
            || ((BirClassReference)partialMapPreference.Args.Value[0]).Type.Value != variable
            || Integer(projectedMapPreference.Args.Value[1]) != 1
            || ((BirClassReference)boundedMapPreference.Args.Value[0]).Type.Value != variable)
            throw new InvalidOperationException("Static dictionary preference lost partial/projection/bound source facts");
        Console.WriteLine("[static collection preference] self-test OK (exact, star, caller frame, dictionary pair, no origin carrier)");
    }

    internal static string ProjectedIsEmptyHelper(string owner) => owner switch
    {
        "kotlin.collections.List" => "clrProjectedListIsEmpty",
        "kotlin.collections.MutableList" => "clrProjectedMutableListIsEmpty",
        "kotlin.collections.Set" => "clrProjectedSetIsEmpty",
        "kotlin.collections.MutableSet" => "clrProjectedMutableSetIsEmpty",
        "kotlin.collections.MutableCollection" => "clrProjectedMutableCollIsEmpty",
        _ => "clrProjectedCollIsEmpty",
    };

    internal static TypeNode[] CollectionHelperTypes(string owner, string method)
    {
        var tv = new TypeNode.Tv("method", 0);
        TypeNode Gen(string name) => new TypeNode.Fqn(name, new TypeNode[] { tv });
        var ps = (owner, method) switch
        {
            ("kotlin.collections.ClrIteratorBridgeKt", "iteratorOverEnumerable") => new[] { Gen("kotlin.collections.ClrEnumerable") },
            (_, "clrCollAdd") => new TypeNode[] { Gen("kotlin.collections.MutableCollection"), tv },
            (_, "clrProjectedCollAdd") => new TypeNode[] { new TypeNode.Fqn("kotlin.Any"), tv },
            (_, "clrProjectedListAdd") => new TypeNode[]
                { new TypeNode.Fqn("kotlin.Any"), new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")), tv },
            (_, "clrProjectedCollAddAll" or "clrProjectedCollRemoveAll" or "clrProjectedCollRetainAll") =>
                new TypeNode[] { new TypeNode.Fqn("kotlin.Any"), Gen("kotlin.collections.Collection") },
            (_, "clrProjectedListAddAllAt") => new TypeNode[]
                { new TypeNode.Fqn("kotlin.Any"), new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")),
                    new TypeNode.Fqn("kotlin.Int"), Gen("kotlin.collections.Collection") },
            (_, "clrCollAddAll" or "clrCollRemoveAll" or "clrCollRetainAll") =>
                new TypeNode[] { Gen("kotlin.collections.MutableCollection"), Gen("kotlin.collections.Collection") },
            (_, "clrListAddAllAt") => new TypeNode[]
                { Gen("kotlin.collections.MutableList"), new TypeNode.Fqn("kotlin.Int"), Gen("kotlin.collections.Collection") },
            (_, "clrCollContains") => new TypeNode[] { Gen("kotlin.collections.Collection"), tv },
            (_, "clrCollContainsAll") => new TypeNode[] { Gen("kotlin.collections.Collection"), Gen("kotlin.collections.Collection") },
            (_, "clrCollIsEmpty") => new[] { Gen("kotlin.collections.Collection") },
            (_, "clrProjectedCollContains") => new TypeNode[] { new TypeNode.Fqn("kotlin.Any"), tv },
            (_, "clrProjectedCollContainsAll") => new TypeNode[]
                { new TypeNode.Fqn("kotlin.Any"), Gen("kotlin.collections.Collection") },
            (_, "clrProjectedCollIsEmpty" or "clrProjectedListIsEmpty" or "clrProjectedMutableListIsEmpty" or "clrProjectedSetIsEmpty"
                or "clrProjectedMutableSetIsEmpty" or "clrProjectedMutableCollIsEmpty") =>
                new[] { new TypeNode.Fqn("kotlin.Any") },
            (_, "clrListSet") => new TypeNode[] { Gen("kotlin.collections.MutableList"), new TypeNode.Fqn("kotlin.Int"), tv },
            (_, "clrListRemoveAt") => new TypeNode[] { Gen("kotlin.collections.MutableList"), new TypeNode.Fqn("kotlin.Int") },
            (_, "clrProjectedListSet" or "clrProjectedListAddAt") => new TypeNode[]
                { new TypeNode.Fqn("kotlin.Any"), new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")), new TypeNode.Fqn("kotlin.Int"), tv },
            (_, "clrProjectedListRemoveAt") => new TypeNode[]
                { new TypeNode.Fqn("kotlin.Any"), new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")), new TypeNode.Fqn("kotlin.Int") },
            // MutableIterable is declaration-site covariant. A legal Kotlin view such as MutableIterable<Any?> over
            // MutableList<Int> cannot be passed as CLR IEnumerable<object>: CLR generic variance does not apply to
            // value-type arguments. The helper therefore receives the physical object erased and adapts its exact
            // runtime iterator while retaining T only in the return type.
            (_, "clrMutableIterator") => new[] { new TypeNode.Fqn("kotlin.Any") },
            (_, "clrProjectedIterator") => new[] { new TypeNode.Fqn("kotlin.Any") },
            (_, "clrProjectedViewIterator" or "clrProjectedViewIsEmpty" or "clrProjectedViewListIterator") =>
                new TypeNode[] { new TypeNode.Fqn("kotlin.Any"), new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")), new TypeNode.Fqn("kotlin.Int") },
            (_, "clrProjectedViewContains" or "clrProjectedViewIndexOf" or "clrProjectedViewLastIndexOf") => new TypeNode[] {
                new TypeNode.Fqn("kotlin.Any"), new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")), new TypeNode.Fqn("kotlin.Int"),
                new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")) },
            (_, "clrProjectedViewGet" or "clrProjectedViewListIteratorAt") => new TypeNode[] {
                new TypeNode.Fqn("kotlin.Any"), new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")),
                new TypeNode.Fqn("kotlin.Int"), new TypeNode.Fqn("kotlin.Int") },
            (_, "clrProjectedViewSubList") => new TypeNode[] {
                new TypeNode.Fqn("kotlin.Any"), new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")),
                new TypeNode.Fqn("kotlin.Int"), new TypeNode.Fqn("kotlin.Int"), new TypeNode.Fqn("kotlin.Int") },
            (_, "clrProjectedViewContainsAll") => new TypeNode[] {
                new TypeNode.Fqn("kotlin.Any"), new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")), new TypeNode.Fqn("kotlin.Int"), Gen("kotlin.collections.Collection") },
            (_, "clrMutableListListIterator") => new TypeNode[] { Gen("kotlin.collections.MutableList"), new TypeNode.Fqn("kotlin.Int") },
            (_, "clrProjectedMutableListIterator") =>
                new TypeNode[] { new TypeNode.Fqn("kotlin.Any"), new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")) },
            (_, "clrProjectedMutableListListIterator") => new TypeNode[]
                { new TypeNode.Fqn("kotlin.Any"), new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")), new TypeNode.Fqn("kotlin.Int") },
            (_, "clrProjectedMutableListSubList") => new TypeNode[]
                { new TypeNode.Fqn("kotlin.Any"), new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")),
                    new TypeNode.Fqn("kotlin.Int"), new TypeNode.Fqn("kotlin.Int") },
            (_, "clrProjectedListIterator") =>
                new TypeNode[] { new TypeNode.Fqn("kotlin.Any") },
            (_, "clrProjectedListListIterator") => new TypeNode[]
                { new TypeNode.Fqn("kotlin.Any"), new TypeNode.Fqn("kotlin.Int") },
            (_, "clrListIndexOf" or "clrListLastIndexOf") => new TypeNode[] { Gen("kotlin.collections.List"), tv },
            (_, "clrProjectedListIndexOf" or "clrProjectedListLastIndexOf") =>
                new TypeNode[] { new TypeNode.Fqn("kotlin.Any"), tv },
            (_, "clrListListIterator") => new TypeNode[] { Gen("kotlin.collections.List"), new TypeNode.Fqn("kotlin.Int") },
            (_, "clrListSubList") => new TypeNode[] { Gen("kotlin.collections.List"), new TypeNode.Fqn("kotlin.Int"), new TypeNode.Fqn("kotlin.Int") },
            (_, "clrMutableListSubList") => new TypeNode[] { Gen("kotlin.collections.MutableList"), new TypeNode.Fqn("kotlin.Int"), new TypeNode.Fqn("kotlin.Int") },
            (_, "clrProjectedListSubList") => new TypeNode[]
                { new TypeNode.Fqn("kotlin.Any"), new TypeNode.Fqn("kotlin.Int"), new TypeNode.Fqn("kotlin.Int") },
            _ => throw new InvalidOperationException($"bir2cir: no authored descriptor for collection helper {owner}.{method}"),
        };
        return ps;
    }

    internal static string MapDefaultHelper(string member, string propertyAccess, int count, bool mutable) =>
        (member, propertyAccess, count, mutable) switch
    {
        ("get", _, 1, _) => "clrMapGet",
        // size / containsKey are UNBOUND (no @ClrIntrinsic) — a direct Count/ContainsKey reads through the
        // INVARIANT generic IDictionary<K,V> and throws EntryPointNotFound on a value-type-mismatched map (a
        // groupBy result). Route to the covariance-safe non-generic helpers (ICollection.Count / IDictionary
        // .Contains). This also makes mapValues' transitive `mapCapacity(this.size)` covariance-safe.
        ("size", "get", 0, _) => "clrMapSize",
        ("containsKey", _, 1, _) => "clrMapContainsKey",
        ("isEmpty", _, 0, _) => "clrMapIsEmpty",
        ("containsValue", _, 1, _) => "clrMapContainsValue",
        ("getOrDefault", _, 2, _) => "clrMapGetOrDefault",
        ("keys", "get", 0, false) => "clrMapKeys",
        ("values", "get", 0, false) => "clrMapValues",
        ("entries", "get", 0, false) => "clrMapEntries",
        ("keys", "get", 0, true) => "clrMapMutableKeys",
        ("values", "get", 0, true) => "clrMapMutableValues",
        ("entries", "get", 0, true) => "clrMapMutableEntries",
        ("put", _, 2, true) => "clrMapPut",
        ("remove", _, 1, true) => "clrMapRemove",
        ("remove", _, 2, true) => "clrMapRemoveKV",
        ("putAll", _, 1, true) => "clrMapPutAll",
        ("clear", _, 0, true) => "clrMapClear",
        ("putIfAbsent", _, 2, true) => "clrMapPutIfAbsent",
        ("replace", _, 2, true) => "clrMapReplace",
        ("replace", _, 3, true) => "clrMapReplaceKVV",
        ("merge", _, 3, true) => "clrMapMerge",
        _ => null,
    };

    internal static TypeNode[] MapHelperTypes(string method)
    {
        TypeNode any = new TypeNode.Fqn("kotlin.Any");
        TypeNode k = new TypeNode.Tv("method", 0);
        TypeNode v = new TypeNode.Tv("method", 1);
        TypeNode query = new TypeNode.Nullable(any);
        TypeNode[] ps = method switch
        {
            "clrMapIsEmpty" or "clrMapSize" or "clrMapKeys" or "clrMapValues" or "clrMapEntries"
                or "clrMapMutableKeys" or "clrMapMutableValues" or "clrMapMutableEntries" or "clrMapClear" => new[] { any },
            "clrMapGet" or "clrMapContainsKey" or "clrMapRemove" or "clrMapContainsValue" => new[] { any, query },
            "clrMapGetOrDefault" or "clrMapReplace" => new[] { any, query, v },
            "clrMapRemoveKV" => new[] { any, query, query },
            "clrMapPut" or "clrMapPutIfAbsent"
                => new[] { any, k, v },
            "clrMapMerge" => new TypeNode[]
            {
                any, k, v,
                new TypeNode.Fn(false, new TypeNode.Nullable(v), new[] { v, v }, null, "System.Func"),
            },
            "clrMapPutAll" => new[] { any, any },
            "clrMapReplaceKVV" => new[] { any, query, query, v },
            _ => throw new InvalidOperationException($"bir2cir: no authored descriptor for map helper {method}"),
        };
        return ps.Concat(method == "clrMapPutAll"
            ? new TypeNode[] { new TypeNode.Nullable(any), new TypeNode.Nullable(any) }
            : new TypeNode[] { new TypeNode.Nullable(any) }).ToArray();
    }

    // The first TWO top-level type arguments of a map owner token (`kotlin.collections.Map[gp:K,gp:V]`); `object` when
    // erased/unbound.
    static (TypeNode, TypeNode) OwnerKvArgs(TypeNode.Fqn ownerFqn)
    {
        var args = ownerFqn.Args;
        return (args is { Length: >= 1 } && args[0] != null ? args[0] : ObjType,
                args is { Length: >= 2 } && args[1] != null ? args[1] : ObjType);
    }

    // The first top-level type argument of an owner Fqn (`kotlin.collections.List<E>` -> E); `object` if none.
    static TypeNode OwnerElemArg(TypeNode.Fqn ownerFqn) =>
        ownerFqn.Args is { Length: >= 1 } args && args[0] != null ? args[0] : ObjType;

    // Close only method-scope variables from the call's explicit argument vector.
    static TypeNode SubstMethodTv(TypeNode t, TypeNode[] methodArgs) => t switch
    {
        TypeNode.Tv { Scope: "method" } tv when methodArgs != null && tv.I >= 0 && tv.I < methodArgs.Length && methodArgs[tv.I] != null => methodArgs[tv.I],
        TypeNode.Fqn { Args: { } fa } f => new TypeNode.Fqn(f.Name, fa.Select(a => SubstMethodTv(a, methodArgs)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstMethodTv(n.Of, methodArgs)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstMethodTv(o.Of, methodArgs)),
        TypeNode.Array a => new TypeNode.Array(SubstMethodTv(a.Elem, methodArgs)),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstMethodTv(b.Of, methodArgs)),
        TypeNode.Fn fnv => new TypeNode.Fn(fnv.Suspend, SubstMethodTv(fnv.Ret, methodArgs), fnv.Params.Select(p => SubstMethodTv(p, methodArgs)).ToArray(), fnv.Recv == null ? null : SubstMethodTv(fnv.Recv, methodArgs)),
        _ => t,
    };

    // The receiver-type key of a call's first-arg type (mirrors ReferenceMetadataIndex.RecvKey on the ref.dll side).
    // A specialized primitive-array Fqn maps to "[]" via RecvKeyOfFqn (same collapse the ref-side applies to a real
    // int[]) — see the helper for why generic `Array<T>` already reaches "[]" and the primitive variants need it (#153).
    static string RecvKeyOf(TypeNode sig0) => sig0 switch
    {
        TypeNode.Array => "[]",
        TypeNode.Tv => "gp",
        TypeNode.Nullable n => RecvKeyOf(n.Of),
        TypeNode.ByRef b => RecvKeyOf(b.Of),
        TypeNode.Fqn f => ReferenceMetadataIndex.RecvKeyOfFqn(f.Name),
        _ => "",
    };

    // An ordinary variant alias receiver does not prove a closed CLR interface, even when it is a type variable
    // constrained by that source alias. Retain its semantic owner for foreign projection binding. Constructors and
    // genuine native descriptors still use ClrOwnerType directly and keep their exact CLR construction.
    static TypeNode ClrValueOwnerType(ReferenceMetadataIndex refs, TypeNode.Fqn ownerFqn) =>
        AliasVarianceRepresentation.RequiresErasure(ownerFqn, refs) ? ownerFqn : ClrOwnerType(refs, ownerFqn);

    // A CLR-bound owner token's ClrRef-resolvable BCL type: a non-generic alias is its bare BCL FQN ("System.String"
    // -- NOT the "string" shorthand, which ilemit ClrRef can't resolve as a clr* `type`); a generic alias keeps its
    // element args (clrg:<bcl>[<args>], or [object x arity] when the token erased them). Null if not CLR-bound.
    internal static TypeNode ClrOwnerType(ReferenceMetadataIndex refs, TypeNode.Fqn ownerFqn)
    {
        if (!refs.TryResolveClrOwner(ownerFqn.Name, out var bcl, out _)) return null;
        // A collapsing generic alias must keep its source identity until PhysicalHead selects the final classifier;
        // authoring its non-generic BCL head with source arguments would manufacture a nonexistent CLR construction.
        var head = BirTypeLowering.AliasHeadRequiresSourceIdentity(ownerFqn.Name, bcl)
            ? ownerFqn.Name
            : bcl;
        var arity = refs.OwnerArity(ownerFqn.Name);
        if (ownerFqn.Args != null || arity > 0)
        {
            // Pad a PARTIALLY-erased arg list to the alias's declared arity (a star-projection `Map<K, *>` reaches here
            // as `kotlin.collections.Map<K>` — 1 of IDictionary's 2 args; the incomplete constructed CLR type would
            // fail to resolve. The trailing/all erased args become `object`.
            var kept = (ownerFqn.Args ?? Array.Empty<TypeNode>()).Where(a => a != null).ToList();
            for (var i = kept.Count; i < arity; i++) kept.Add(ObjType);
            // The Kotlin reference declaration may carry nullable companion parameters for its helper
            // implementation. They are not parameters of the fixed CLR alias TypeDef. Select ordinary
            // arguments by the explicit frame correspondence, not by truncating to the CLR arity.
            // A deferred semantic head still owns its complete frame until type lowering selects its head.
            var ownerArguments = head != ownerFqn.Name
                && refs.NullableTypeFrames.TryGetValue(ReferenceMetadataIndex.BareOwnerFqn(ownerFqn.Name), out var frame)
                    ? frame.OrdinaryArguments(kept) : kept.ToArray();
            if (ownerArguments.Length > 0) return new TypeNode.Fqn(head, ownerArguments);
        }
        return new TypeNode.Fqn(head);
    }

    static TypeNode UnwrapNullableOblivious(TypeNode t) => t switch
    {
        TypeNode.Nullable n => UnwrapNullableOblivious(n.Of),
        TypeNode.Oblivious o => UnwrapNullableOblivious(o.Of),
        _ => t,
    };

    // Value-representation slot synthesis still consumes this late DOM renderer.
    // Selection and frame transfer are the same scalar plan as owning call substitution.
    internal static JsonNode Rule3HelperCall(JsonObject node, ReferenceMetadataIndex refs, TypeNode.Fqn ownerFqn, string member, JsonArray args, bool instance,
        IReadOnlySet<string> selectedLocalIds = null)
    {
        var hargs = new JsonArray();
        if (instance && node["recv"] != null) hargs.Add(node["recv"].DeepClone());
        foreach (var a in args) hargs.Add(a?.DeepClone());
        var knownReturn = TypeJson.Read(RetToken(node));
        var projectedReturn = knownReturn;
        if (instance && ForeignStarProjectionBinding.IsForeignStarType(ownerFqn, refs))
            projectedReturn ??= NodeType.Of(node);
        var plan = PlanRule3Helper(refs, ownerFqn, member, Str(node[DeclarationIdentityBinding.Key]),
            instance, node["recv"] != null, args.Count,
            (node["sig"] as JsonArray)?.Select(TypeJson.Read).ToArray(),
            (node["typeArgs"] as JsonArray)?.Select(TypeJson.Read).ToArray(), knownReturn, projectedReturn,
            selectedLocalIds ?? _localDeclarationIds, _representations);
        var call = new JsonObject
        {
            ["k"] = "callStatic",
            ["owner"] = TypeJson.Write(plan.Owner),
            ["method"] = plan.Method,
            ["args"] = hargs,
        };
        if (plan.DeclarationId != null) call[DeclarationIdentityBinding.Key] = plan.DeclarationId;
        if (plan.TypeArgs != null) call["typeArgs"] = new JsonArray(plan.TypeArgs.Select(TypeJson.Write).ToArray());
        if (plan.Signature != null) call["sig"] = new JsonArray(plan.Signature.Select(TypeJson.Write).ToArray());
        if (plan.CalleeParams != null) call["calleeParams"] = new JsonArray(plan.CalleeParams.Select(TypeJson.Write).ToArray());
        if (plan.CalleeReturn != null) call["calleeRet"] = TypeJson.Write(plan.CalleeReturn);
        if (plan.Return != null) call["ret"] = TypeJson.Write(plan.Return);
        if (plan.ProjectedHelperReceiver.HasValue) call["projectedHelperReceiver"] = plan.ProjectedHelperReceiver.Value;
        return call;
    }

    static TypeNode RemapHoistedTypeVars(TypeNode type, int classArity) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv => new TypeNode.Tv("method", tv.I),
        TypeNode.Tv { Scope: "method" } tv => new TypeNode.Tv("method", classArity + tv.I),
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args?.Select(a => RemapHoistedTypeVars(a, classArity)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(RemapHoistedTypeVars(n.Of, classArity)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(RemapHoistedTypeVars(o.Of, classArity)),
        TypeNode.Array a => new TypeNode.Array(RemapHoistedTypeVars(a.Elem, classArity)),
        TypeNode.ByRef b => new TypeNode.ByRef(RemapHoistedTypeVars(b.Of, classArity)),
        TypeNode.Mod m => new TypeNode.Mod(m.Req, RemapHoistedTypeVars(m.M, classArity), RemapHoistedTypeVars(m.Of, classArity)),
        TypeNode.Fn fn => new TypeNode.Fn(
            fn.Suspend,
            RemapHoistedTypeVars(fn.Ret, classArity),
            fn.Params.Select(p => RemapHoistedTypeVars(p, classArity)).ToArray(),
            fn.Recv == null ? null : RemapHoistedTypeVars(fn.Recv, classArity),
            fn.Clr),
        _ => type,
    };

    // The actual late value-representation call retains the same dynRet-before-ret choice.
    static JsonNode RetToken(JsonObject node)
    {
        foreach (var key in new[] { "dynRet", "ret" })
            if (node[key] is JsonNode n && TypeJson.Read(n) is TypeNode) return n.DeepClone();
        return null;
    }

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;

}
