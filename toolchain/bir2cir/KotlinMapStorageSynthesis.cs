using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Kotlin Map operations and native IDictionary slots have different contracts. This physical
// canonical stdlib carrier supplies the native face while dispatching through the selected Kotlin slots.
// Its declared reference-twin surface and runtime definition share one identity across consuming modules.
static partial class KotlinMapStorageSynthesis
{
    const string CarrierName = "DotKt.Runtime.CompilerServices.dotkt$KotlinMapStorage";
    const string EnumeratorName = "dotkt$MapEnumeratorOverKotlinIterator";
    const string Dictionary = "System.Collections.Generic.IDictionary";
    const string Collection = "System.Collections.Generic.ICollection";
    const string Enumerable = "System.Collections.Generic.IEnumerable";
    const string RawEnumerable = "System.Collections.IEnumerable";
    const string Helpers = "kotlin.collections.ClrAuthoredMapAdaptersKt";
    static readonly TypeNode K = new TypeNode.Tv("type", 0);
    static readonly TypeNode V = new TypeNode.Tv("type", 1);
    static readonly TypeNode Any = new TypeNode.Fqn("System.Object");
    static readonly TypeNode Bool = new TypeNode.Fqn("System.Boolean");
    static readonly TypeNode Int = new TypeNode.Fqn("System.Int32");
    static readonly TypeNode Void = new TypeNode.Fqn("void");
    static TypeNode.Fqn F(string name, params TypeNode[] args) => new(name, args.Length == 0 ? null : args);
    static TypeNode Pair => F("System.Collections.Generic.KeyValuePair", K, V);
    static JsonObject Self() => new() { ["k"] = "this" };
    static JsonObject Local(string name) => new() { ["k"] = "local", ["name"] = name };
    static JsonObject Cast(TypeNode type, JsonNode value) => new() {
        ["k"] = "cast", ["type"] = TypeJson.Write(type), ["e"] = value,
    };
    static JsonObject Return(JsonNode value) => new() { ["k"] = "return", ["value"] = value };
    static JsonObject Expr(JsonNode value) => new() { ["k"] = "exprStmt", ["expr"] = value };
    static JsonObject Constant(TypeNode type, bool value) => new() {
        ["k"] = "const", ["type"] = TypeJson.Write(type), ["value"] = value,
    };
    static JsonObject Parameter(string name, TypeNode type) => new() {
        ["name"] = name, ["type"] = TypeJson.Write(type),
    };
    static JsonObject Call(TypeNode owner, string member, TypeNode ret, JsonNode receiver,
        TypeNode[] signature, params JsonNode[] args) => new() {
        ["k"] = "callInstance", ["ownerType"] = TypeJson.Write(owner), ["method"] = member,
        ["virtual"] = true, ["recv"] = receiver, ["ret"] = TypeJson.Write(ret),
        ["sig"] = new JsonArray(signature.Select(TypeJson.Write).ToArray()), ["args"] = new JsonArray(args),
    };
    static JsonObject Slot(string member, TypeNode ret, params JsonNode[] args) =>
        Call(F(KotlinCollectionSlotSynthesis.MapSlots), member, ret,
            Cast(F(KotlinCollectionSlotSynthesis.MapSlots), Self()),
            args.Select(_ => Any).ToArray(), args);

    internal static IReadOnlyList<string> ApplyAll(IReadOnlyList<JsonNode> roots, ReferenceMetadataIndex refs,
        bool defineCarrier)
    {
        var defs = SupertypeGraph.Collect(roots);
        foreach (var def in defs.Values)
        {
            if (def.Kind != "class") continue;
            var reached = SupertypeGraph.Reachable(def, defs, refs).Select(e => e.spec).ToArray();
            if (!reached.Any(f => f.Name == KotlinCollectionSlotSynthesis.MapSlots
                    || f.Name == KotlinCollectionSlotSynthesis.MutableMapSlots)) continue;
            foreach (var dictionary in reached.Where(f => ReferenceMetadataIndex.BareOwnerFqn(f.Name) == Dictionary
                         && f.Args is { Length: 2 }))
            {
                var carrier = F(CarrierName, dictionary.Args);
                if (reached.Any(f => ReferenceMetadataIndex.BareOwnerFqn(f.Name) == CarrierName
                        && f.Args is { Length: 2 }
                        && f.Args.Zip(dictionary.Args).All(pair =>
                            ClrMemberResolution.SameInterfaceSlotType(pair.First, pair.Second)))) continue;
                if (def.Node["interfaces"] is not JsonArray interfaces)
                    def.Node["interfaces"] = interfaces = new JsonArray();
                interfaces.Add(TypeJson.Write(carrier));
            }
        }
        if (!defineCarrier) return Array.Empty<string>();
        var host = roots.OfType<JsonObject>().First();
        if (host["types"] is not JsonArray types) host["types"] = types = new JsonArray();
        types.Add(Carrier(refs));
        types.Add(ReverseEnumeratorBridgeSynthesis.Adapter(EnumeratorName, refs));
        return new[] { CarrierName, EnumeratorName };
    }

    internal static string EmitReferenceSurface(IReadOnlyList<JsonNode> roots)
    {
        var host = roots.OfType<JsonObject>().First();
        if (host["types"] is not JsonArray types) host["types"] = types = new JsonArray();
        types.Add(Carrier(null, declarationOnly: true));
        return CarrierName;
    }

    static JsonObject Method(TypeNode owner, string member, TypeNode ret, JsonArray parameters, JsonArray body)
        => new() {
            ["name"] = "dotkt$map$" + ((TypeNode.Fqn)owner).Name.Replace('.', '$') + "$" + member,
            ["static"] = false, ["virtual"] = true, ["abstract"] = false, ["override"] = false,
            ["vis"] = "private", ["generated"] = true,
            [KotlinPropertyAccessors.ClrInterfaceSlotBridgeKey] = true,
            ["params"] = parameters, ["ret"] = TypeJson.Write(ret), ["body"] = body,
            ["clrInterfaceImpls"] = new JsonArray(new JsonObject {
                ["owner"] = TypeJson.Write(owner), ["member"] = member, ["arity"] = 0,
                ["params"] = new JsonArray(parameters.OfType<JsonObject>().Select(p => p["type"].DeepClone()).ToArray()),
                ["ret"] = TypeJson.Write(ret),
            }),
        };

    static JsonObject Helper(ReferenceMetadataIndex refs, string name, TypeNode ret,
        TypeNode[] typeArgs, TypeNode[] sourceSignature, params JsonNode[] args)
        => HelperOn(refs, Helpers, name, ret, typeArgs, sourceSignature, args);

    static JsonObject HelperOn(ReferenceMetadataIndex refs, string owner, string name, TypeNode ret,
        TypeNode[] typeArgs, TypeNode[] sourceSignature, params JsonNode[] args)
    {
        var helper = refs.AuthoredKotlinHelper(owner, name, typeArgs.Length, sourceSignature);
        TypeNode NoWitness(TypeNode _) => throw new InvalidOperationException("Native map storage has no nullable witness");
        var closed = helper.NullableFrame?.Close(typeArgs, t => t, NoWitness, t => t, NoWitness) ?? typeArgs;
        TypeNode Physical(TypeNode type) => BirTypeLowering.CanonicalPhysicalSlotType(BirTypeLowering.LowerPhysicalType(
            FBoundStarProjectionErasure.ProjectReferencedValueType(type, refs), refs.Aliases,
            refs.IsValueType, refs.PhysicalTypeNames, typeArg: false, nullableFrames: refs.NullableTypeFrames));
        return new JsonObject {
            ["k"] = "callStatic", ["owner"] = TypeJson.Fqn(owner), ["method"] = name,
            [DeclarationIdentityBinding.Key] = helper.DeclarationId,
            ["typeArgs"] = new JsonArray(closed.Select(TypeJson.Write).ToArray()),
            ["sig"] = new JsonArray(helper.ParamTypeNodes.Select(Physical).Select(TypeJson.Write).ToArray()),
            ["ret"] = TypeJson.Write(ret), ["args"] = new JsonArray(args),
        };
    }

    static JsonObject Carrier(ReferenceMetadataIndex refs, bool declarationOnly = false)
    {
        var dictionary = F(Dictionary, K, V);
        var collection = F(Collection, Pair);
        var enumerable = F(Enumerable, Pair);
        var iterator = F("System.Collections.Generic.IEnumerator", Pair);
        var key = Local("key");
        JsonObject Contains() => Slot("dotktMapContainsKey", Bool, Cast(Any, key.DeepClone()));
        JsonObject Get() => Cast(V, Slot("dotktMapGet", Any, Cast(Any, key.DeepClone())));
        JsonObject NativeHelper(string name, TypeNode ret, params JsonNode[] args) => Helper(refs, name, ret,
            Array.Empty<TypeNode>(), new[] { new TypeNode.Fqn("kotlin.Any") }
                .Concat(args.Select(_ => (TypeNode)new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")))).ToArray(),
            new[] { (JsonNode)Self() }.Concat(args).ToArray());
        JsonObject PairPart(string part, TypeNode type)
        {
            if (!refs.TryExternalPropertyAccessor("System.Collections.Generic.KeyValuePair", part, "get",
                    0, 0, Array.Empty<TypeNode>(), new[] { K, V },
                    out var owner, out _, out var accessor))
                throw new InvalidOperationException($"Native map entry has no declared '{part}' getter");
            return new JsonObject {
                ["k"] = "clrInstance", ["type"] = TypeJson.Write(F(owner, K, V)),
                ["method"] = accessor, ["ret"] = TypeJson.Write(type), ["recv"] = Local("pair"),
                ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
            };
        }
        JsonObject PairMatches() {
            var comparer = F("System.Collections.Generic.EqualityComparer", V);
            var eq = Call(comparer, "Equals", Bool,
                new JsonObject { ["k"] = "callStatic", ["owner"] = TypeJson.Write(comparer),
                    ["method"] = "get_Default", ["sig"] = new JsonArray(), ["args"] = new JsonArray(),
                    ["ret"] = TypeJson.Write(comparer) },
                new[] { V, V }, Cast(V, Slot("dotktMapGet", Any, Cast(Any, PairPart("Key", K)))), PairPart("Value", V));
            return new JsonObject { ["k"] = "cond", ["type"] = TypeJson.Write(Bool),
                ["cond"] = Slot("dotktMapContainsKey", Bool, Cast(Any, PairPart("Key", K))),
                ["then"] = eq, ["else"] = Constant(Bool, false) };
        }
        JsonObject If(JsonNode condition, params JsonNode[] body) => new() {
            ["k"] = "if", ["branches"] = new JsonArray(new JsonObject {
                ["cond"] = condition, ["body"] = new JsonArray(body),
            }),
        };
        JsonObject ThrowMissing() => new() { ["k"] = "throw", ["value"] = new JsonObject {
            ["k"] = "new", ["type"] = TypeJson.Fqn("System.Collections.Generic.KeyNotFoundException"),
            ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
        } };
        JsonObject Store(JsonNode value) => Expr(new JsonObject { ["k"] = "byrefStore",
            ["ptr"] = Local("value"), ["elem"] = TypeJson.Write(V), ["value"] = value });
        JsonArray KeyParams() => new(Parameter("key", K));
        JsonArray PairParams() => new(Parameter("pair", Pair));
        JsonObject Define(TypeNode owner, string member, TypeNode ret, JsonArray parameters, Func<JsonArray> body) =>
            Method(owner, member, ret, parameters, declarationOnly
                ? new JsonArray(new JsonObject { ["k"] = "throw", ["value"] = new JsonObject {
                    ["k"] = "new", ["type"] = TypeJson.Fqn("System.NotSupportedException"),
                    ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
                } }) : body());
        var methods = new JsonArray {
            Define(dictionary, "get_Item", V, KeyParams(), () => new JsonArray(If(Contains(), Return(Get())), ThrowMissing())),
            Define(dictionary, "ContainsKey", Bool, KeyParams(), () => new JsonArray(Return(Contains()))),
            Define(dictionary, "set_Item", Void, new JsonArray(Parameter("key", K), Parameter("value", V)),
                () => new JsonArray(Expr(NativeHelper("clrAuthoredMapSet", Void, Cast(Any, key.DeepClone()), Cast(Any, Local("value")))))),
            Define(dictionary, "Add", Void, new JsonArray(Parameter("key", K), Parameter("value", V)),
                () => new JsonArray(Expr(NativeHelper("clrAuthoredMapAdd", Void, Cast(Any, key.DeepClone()), Cast(Any, Local("value")))))),
            Define(dictionary, "Remove", Bool, KeyParams(), () => new JsonArray(Return(
                NativeHelper("clrAuthoredMapRemove", Bool, Cast(Any, key.DeepClone()))))),
            Define(dictionary, "TryGetValue", Bool, new JsonArray(Parameter("key", K), Parameter("value", new TypeNode.ByRef(V))),
                () => new JsonArray(If(Contains(), Store(Get()), Return(Constant(Bool, true))),
                    Store(new JsonObject { ["k"] = "default", ["type"] = TypeJson.Write(V) }), Return(Constant(Bool, false)))),
            Define(collection, "get_Count", Int, new JsonArray(), () => new JsonArray(Return(Slot("dotktMapSize", Int)))),
            Define(collection, "get_IsReadOnly", Bool, new JsonArray(), () => new JsonArray(Return(new JsonObject {
                ["k"] = "unaryOp", ["op"] = "!", ["e"] = new JsonObject { ["k"] = "isInst",
                    ["type"] = TypeJson.Fqn(KotlinCollectionSlotSynthesis.MutableMapSlots), ["e"] = Self() },
            }))),
            Define(collection, "Add", Void, PairParams(), () => new JsonArray(Expr(NativeHelper("clrAuthoredMapAdd", Void,
                Cast(Any, PairPart("Key", K)), Cast(Any, PairPart("Value", V)))))),
            Define(collection, "Clear", Void, new JsonArray(), () => new JsonArray(Expr(NativeHelper("clrAuthoredMapClear", Void)))),
            Define(collection, "Contains", Bool, PairParams(), () => new JsonArray(Return(PairMatches()))),
            Define(collection, "Remove", Bool, PairParams(), () => new JsonArray(
                Expr(NativeHelper("clrAuthoredMapCheckMutable", Void)),
                If(PairMatches(), Return(NativeHelper("clrAuthoredMapRemove", Bool, Cast(Any, PairPart("Key", K))))),
                Return(Constant(Bool, false)))),
        };
        foreach (var (member, slot, element) in new[] { ("get_Keys", "dotktMapKeys", K), ("get_Values", "dotktMapValues", V) })
            methods.Add(Define(dictionary, member, F(Collection, element), new JsonArray(), () => new JsonArray(Return(
                Cast(F(Collection, element), Helper(refs, "clrAuthoredMapCollection", Any, new[] { element },
                    new TypeNode[] { new TypeNode.Fqn("kotlin.Any") }, Slot(slot, Any)))))));
        var copy = CollectionBclSlotSynthesis.CopyTo(TypeJson.Write(Pair));
        JsonObject Binary(string op, JsonNode left, JsonNode right) => new() {
            ["k"] = "binOp", ["op"] = op, ["lhs"] = left, ["rhs"] = right,
        };
        JsonObject Throw(string type) => new() { ["k"] = "throw", ["value"] = new JsonObject {
            ["k"] = "new", ["type"] = TypeJson.Fqn(type), ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
        } };
        var copyBody = (JsonArray)copy["body"].DeepClone();
        copyBody.Insert(0, If(Binary(">", Slot("dotktMapSize", Int), Binary("-",
            Call(F("System.Array"), "get_Length", Int, Local("array"), Array.Empty<TypeNode>()), Local("arrayIndex"))),
            Throw("System.ArgumentException")));
        copyBody.Insert(0, If(Binary("<", Local("arrayIndex"), new JsonObject {
            ["k"] = "const", ["type"] = TypeJson.Write(Int), ["value"] = 0,
        }), Throw("System.ArgumentOutOfRangeException")));
        copyBody.Insert(0, If(Binary("==", Cast(Any, Local("array")), new JsonObject {
            ["k"] = "const", ["type"] = TypeJson.Write(Any), ["value"] = null,
        }), Throw("System.ArgumentNullException")));
        methods.Add(Define(collection, "CopyTo", Void, (JsonArray)copy["params"].DeepClone(), () => copyBody));
        JsonObject Enumerator() => new() {
            ["k"] = "new", ["type"] = TypeJson.Write(F(EnumeratorName, Pair)),
            ["localCtorIndex"] = 0,
            ["argTypes"] = new JsonArray(TypeJson.Write(KotlinIteratorPhysicalProtocol.Carrier(refs))),
            ["args"] = new JsonArray(Cast(KotlinIteratorPhysicalProtocol.Carrier(refs), Helper(refs,
                "clrAuthoredMapIterator", Any, new[] { K, V }, new TypeNode[] { new TypeNode.Fqn("kotlin.Any") },
                Slot("dotktMapEntries", Any)))),
        };
        methods.Add(Define(enumerable, "GetEnumerator", iterator, new JsonArray(), () => new JsonArray(Return(Enumerator()))));
        methods.Add(Define(F(RawEnumerable), "GetEnumerator", F("System.Collections.IEnumerator"),
            new JsonArray(), () => new JsonArray(Return(Enumerator()))));
        AppendNativeFaces(refs, declarationOnly, methods);
        return new JsonObject {
            ["name"] = CarrierName, ["kind"] = "interface", ["vis"] = "public", ["generated"] = true,
            ["typeParams"] = new JsonArray("K", "V"), ["base"] = null,
            ["fields"] = new JsonArray(), ["ctors"] = new JsonArray(), ["methods"] = methods,
            ["interfaces"] = new JsonArray(TypeJson.Write(dictionary),
                TypeJson.Write(F("System.Collections.Generic.IReadOnlyDictionary", K, V)),
                TypeJson.Fqn("System.Collections.IDictionary"), TypeJson.Fqn(KotlinCollectionSlotSynthesis.MapSlots)),
        };
    }
}
