using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Native dictionary methods and Kotlin semantic operations are separate contracts.
// In particular, the native indexer throws on a missing key while Kotlin get returns null.
static class MapBclSlotSynthesis
{
    const string MapSlots = "DotKt.Runtime.CompilerServices.KotlinMapSlots";
    const string MutableSlots = "DotKt.Runtime.CompilerServices.KotlinMutableMapSlots";
    static readonly TypeNode Any = new TypeNode.Fqn("System.Object");
    static readonly TypeNode Bool = new TypeNode.Fqn("System.Boolean");
    static readonly TypeNode Void = new TypeNode.Fqn("void");
    static JsonObject Local(string name) => new() { ["k"] = "local", ["name"] = name };
    static JsonObject Self() => new() { ["k"] = "this" };
    static JsonObject Cast(TypeNode type, JsonNode value) => new() { ["k"] = "cast", ["type"] = TypeJson.Write(type), ["e"] = value };
    static JsonObject Return(JsonNode value) => new() { ["k"] = "return", ["value"] = value };
    static JsonObject Constant(bool value) => new() { ["k"] = "const", ["type"] = TypeJson.Write(Bool), ["value"] = value };
    static JsonObject Param(string name, TypeNode type) => new() { ["name"] = name, ["type"] = TypeJson.Write(type) };
    static JsonObject Statement(JsonNode expression) => new() { ["k"] = "exprStmt", ["expr"] = expression };
    static JsonObject Throw(string type) => new() {
        ["k"] = "throw", ["value"] = new JsonObject {
            ["k"] = "new", ["type"] = TypeJson.Fqn(type), ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
        },
    };
    static JsonObject If(JsonNode condition, params JsonNode[] body) => new() {
        ["k"] = "if", ["branches"] = new JsonArray(new JsonObject {
            ["cond"] = condition, ["body"] = new JsonArray(body),
        }),
    };
    static JsonObject Call(string member, TypeNode result, bool mutable = false, params JsonNode[] arguments) => new() {
        ["k"] = "callInstance", ["ownerType"] = TypeJson.Fqn(mutable ? MutableSlots : MapSlots),
        ["virtual"] = true, ["recv"] = mutable ? Cast(new TypeNode.Fqn(MutableSlots), Self()) : Self(), ["method"] = member,
        ["sig"] = new JsonArray(arguments.Select(_ => TypeJson.Write(Any)).ToArray()),
        ["args"] = new JsonArray(arguments), ["ret"] = TypeJson.Write(result),
    };
    static JsonObject HasKey() => Call("dotktMapContainsKey", Bool, false, Cast(Any, Local("key")));
    static JsonObject GetValue(TypeNode value) => Cast(value, Call("dotktMapGet", Any, false, Cast(Any, Local("key"))));
    static JsonObject RequireMutable() => If(new JsonObject {
        ["k"] = "unaryOp", ["op"] = "!", ["e"] = new JsonObject { ["k"] = "isInst", ["type"] = TypeJson.Fqn(MutableSlots), ["e"] = Self() },
    }, Throw("System.NotSupportedException"));

    static JsonNode PhysicalTypeKey(TypeNode type)
    {
        var node = TypeJson.Write(type);
        void Normalize(JsonNode current)
        {
            if (current is JsonObject obj)
            {
                if (obj["t"]?.GetValue<string>() == "fqn")
                    obj["name"] = ReferenceMetadataIndex.BareOwnerFqn(obj["name"].GetValue<string>());
                foreach (var property in obj.ToArray()) Normalize(property.Value);
            }
            else if (current is JsonArray array)
                foreach (var item in array) Normalize(item);
        }
        Normalize(node);
        return node;
    }

    static bool SamePhysicalType(JsonNode node, TypeNode type) => node != null
        && JsonNode.DeepEquals(PhysicalTypeKey(TypeJson.Read(node)), PhysicalTypeKey(type));

    static JsonObject NativeCollectionView(ReferenceMetadataIndex refs, TypeNode element, string member)
    {
        const string owner = "kotlin.collections.ClrCollectionDefaultsKt";
        const string name = "clrNativeCollectionView";
        var helper = refs.AuthoredKotlinHelper(owner, name, 1, new[] { new TypeNode.Fqn("kotlin.Any") });
        // Like the read-only collection storage carrier, this boundary owns exact CLR storage
        // elements, not Kotlin nullable witnesses. Use the helper's stated physical frame.
        TypeNode NoNullableWitness(TypeNode _) => throw new InvalidOperationException(
            "Native dictionary collection view has no nullable representation witness");
        var arguments = helper.NullableFrame?.Close(new[] { element },
            type => type, NoNullableWitness, type => type, NoNullableWitness) ?? new[] { element };
        TypeNode Physical(TypeNode type) => BirTypeLowering.LowerPhysicalType(type, refs.Aliases,
            refs.IsValueType, refs.PhysicalTypeNames, typeArg: false, nullableFrames: refs.NullableTypeFrames);
        var call = new JsonObject {
            ["k"] = "callStatic", ["owner"] = TypeJson.Fqn(owner), ["method"] = name,
            [DeclarationIdentityBinding.Key] = helper.DeclarationId,
            ["typeArgs"] = new JsonArray(arguments.Select(TypeJson.Write).ToArray()),
            ["sig"] = new JsonArray(helper.ParamTypeNodes.Select(Physical).Select(TypeJson.Write).ToArray()),
            ["ret"] = TypeJson.Write(new TypeNode.Fqn(CollectionViewFaces.IReadOnlyCollection, new[] { element })),
            ["args"] = new JsonArray(Call(member, Any)),
        };
        return Cast(new TypeNode.Fqn(CollectionViewFaces.ICollection, new[] { element }), call);
    }

    internal static bool Apply(JsonNode root, ReferenceMetadataIndex refs)
    {
        if (root is not JsonObject obj || obj["types"] is not JsonArray types) return false;
        var used = false;
        foreach (var definition in types.OfType<JsonObject>())
        {
            if (definition["kind"]?.GetValue<string>() != "class"
                || definition["interfaces"] is not JsonArray interfaces
                || !interfaces.Any(face => TypeJson.Read(face) is TypeNode.Fqn { Name: MapSlots })) continue;
            var faces = interfaces.Select(TypeJson.Read).OfType<TypeNode.Fqn>().Where(face =>
                ReferenceMetadataIndex.BareOwnerFqn(face.Name) == "System.Collections.Generic.IDictionary"
                    && face.Args is { Length: 2 }).ToArray();
            if (definition["methods"] is not JsonArray methods) continue;
            foreach (var face in faces)
            {
                used = true;
                var key = face.Args[0]; var value = face.Args[1];
                void AddSlot(TypeNode.Fqn slotOwner, string member, TypeNode ret, JsonArray parameters, params JsonNode[] body)
                {
                    // An authored getter or override may already own this exact native slot.
                    // Keep that implementation; a second MethodImpl would be ambiguous in CIR.
                    if (methods.OfType<JsonObject>().Any(method =>
                        method["clrInterfaceImpls"] is JsonArray impls && impls.OfType<JsonObject>().Any(impl =>
                            impl["member"]?.GetValue<string>() == member
                            && impl["arity"]?.GetValue<int>() == 0
                            && SamePhysicalType(impl["owner"], slotOwner)
                            && SamePhysicalType(impl["ret"], ret)
                            && impl["params"] is JsonArray ps && ps.Count == parameters.Count
                            && ps.Zip(parameters).All(pair =>
                                SamePhysicalType(pair.First, TypeJson.Read(pair.Second["type"])))))) return;
                    methods.Add(new JsonObject {
                        ["name"] = "dotkt$map$" + member + "$" + methods.Count, ["vis"] = "private", ["static"] = false,
                        ["virtual"] = true, ["abstract"] = false, ["override"] = false, ["generated"] = true,
                        [KotlinPropertyAccessors.PhysicalSlotBridgeKey] = true,
                        ["params"] = parameters, ["ret"] = TypeJson.Write(ret), ["body"] = new JsonArray(body),
                        ["clrInterfaceImpls"] = new JsonArray(new JsonObject {
                            ["owner"] = TypeJson.Write(slotOwner), ["member"] = member, ["arity"] = 0,
                            ["params"] = new JsonArray(parameters.OfType<JsonObject>().Select(p => p["type"].DeepClone()).ToArray()),
                            ["ret"] = TypeJson.Write(ret),
                        }),
                    });
                }
                void Add(string member, TypeNode ret, JsonArray parameters, params JsonNode[] body) =>
                    AddSlot(face, member, ret, parameters, body);
                void DeclareInterface(TypeNode.Fqn contract)
                {
                    if (!interfaces.Any(node => SamePhysicalType(node, contract)))
                        interfaces.Add(TypeJson.Write(contract));
                }
                Add("get_Item", value, new JsonArray(Param("key", key)),
                    If(HasKey(), Return(GetValue(value))), Throw("System.Collections.Generic.KeyNotFoundException"));
                Add("ContainsKey", Bool, new JsonArray(Param("key", key)), Return(HasKey()));
                Add("get_Keys", new TypeNode.Fqn(CollectionViewFaces.ICollection, new[] { key }), new JsonArray(),
                    Return(NativeCollectionView(refs, key, "dotktMapKeys")));
                Add("get_Values", new TypeNode.Fqn(CollectionViewFaces.ICollection, new[] { value }), new JsonArray(),
                    Return(NativeCollectionView(refs, value, "dotktMapValues")));
                JsonObject Put() => Statement(Call("dotktMapPut", Any, true, Cast(Any, Local("key")), Cast(Any, Local("value"))));
                Add("set_Item", Void, new JsonArray(Param("key", key), Param("value", value)),
                    RequireMutable(), Put(), new JsonObject { ["k"] = "return" });
                Add("Add", Void, new JsonArray(Param("key", key), Param("value", value)),
                    RequireMutable(), If(HasKey(), Throw("System.ArgumentException")), Put(), new JsonObject { ["k"] = "return" });
                Add("Remove", Bool, new JsonArray(Param("key", key)), RequireMutable(),
                    If(HasKey(), Statement(Call("dotktMapRemove", Any, true, Cast(Any, Local("key")))), Return(Constant(true))),
                    Return(Constant(false)));
                JsonObject Store(JsonNode result) => Statement(new JsonObject {
                    ["k"] = "byrefStore", ["ptr"] = Local("value"), ["elem"] = TypeJson.Write(value), ["value"] = result,
                });
                Add("TryGetValue", Bool, new JsonArray(Param("key", key), Param("value", new TypeNode.ByRef(value))),
                    If(HasKey(), Store(GetValue(value)), Return(Constant(true))),
                    Store(new JsonObject { ["k"] = "default", ["type"] = TypeJson.Write(value) }), Return(Constant(false)));

                // IDictionary's inherited collection is a collection of CLR pairs, not
                // the Kotlin entries Set. Its methods still dispatch to this same map.
                var pair = new TypeNode.Fqn("System.Collections.Generic.KeyValuePair", new[] { key, value });
                var collection = new TypeNode.Fqn(CollectionViewFaces.ICollection, new TypeNode[] { pair });
                DeclareInterface(collection);
                DeclareInterface(new TypeNode.Fqn("System.Collections.Generic.IEnumerable", new TypeNode[] { pair }));
                DeclareInterface(new TypeNode.Fqn("System.Collections.IEnumerable"));
                var integer = new TypeNode.Fqn("System.Int32");
                AddSlot(collection, "get_Count", integer, new JsonArray(), Return(Call("dotktMapSize", integer)));
                AddSlot(collection, "get_IsReadOnly", Bool, new JsonArray(), Return(new JsonObject {
                    ["k"] = "unaryOp", ["op"] = "!", ["e"] = new JsonObject {
                        ["k"] = "isInst", ["type"] = TypeJson.Fqn(MutableSlots), ["e"] = Self(),
                    },
                }));
                AddSlot(collection, "Clear", Void, new JsonArray(), RequireMutable(),
                    Statement(Call("dotktMapClear", Void, true)), new JsonObject { ["k"] = "return" });
                var readOnlyCollection = new TypeNode.Fqn(CollectionViewFaces.IReadOnlyCollection, new TypeNode[] { pair });
                DeclareInterface(readOnlyCollection);
                AddSlot(readOnlyCollection, "get_Count", integer, new JsonArray(), Return(Call("dotktMapSize", integer)));

                JsonObject PairPart(string member, TypeNode result) => new() {
                    ["k"] = "clrPropGet", ["type"] = TypeJson.Write(pair), ["name"] = member,
                    ["static"] = false, ["recv"] = Local("item"),
                    ["ret"] = TypeJson.Write(result),
                };
                JsonObject PairKey() => PairPart("Key", key);
                JsonObject PairValue() => PairPart("Value", value);
                JsonObject PairPresent() => Call("dotktMapContainsKey", Bool, false, Cast(Any, PairKey()));
                var comparer = new TypeNode.Fqn("System.Collections.Generic.EqualityComparer", new[] { value });
                JsonObject PairValueEquals() => new() {
                    ["k"] = "callInstance", ["ownerType"] = TypeJson.Write(comparer), ["method"] = "Equals",
                    ["virtual"] = true,
                    ["recv"] = new JsonObject {
                        ["k"] = "callStatic", ["owner"] = TypeJson.Write(comparer), ["method"] = "get_Default",
                        ["sig"] = new JsonArray(), ["args"] = new JsonArray(), ["ret"] = TypeJson.Write(comparer),
                    },
                    ["sig"] = new JsonArray(TypeJson.Write(value), TypeJson.Write(value)),
                    ["args"] = new JsonArray(Cast(value, Call("dotktMapGet", Any, false, Cast(Any, PairKey()))), PairValue()),
                    ["ret"] = TypeJson.Write(Bool),
                };
                AddSlot(collection, "Add", Void, new JsonArray(Param("item", pair)), RequireMutable(),
                    If(PairPresent(), Throw("System.ArgumentException")),
                    Statement(Call("dotktMapPut", Any, true, Cast(Any, PairKey()), Cast(Any, PairValue()))),
                    new JsonObject { ["k"] = "return" });
                AddSlot(collection, "Contains", Bool, new JsonArray(Param("item", pair)),
                    If(PairPresent(), Return(PairValueEquals())), Return(Constant(false)));
                AddSlot(collection, "Remove", Bool, new JsonArray(Param("item", pair)), RequireMutable(),
                    If(PairPresent(), If(PairValueEquals(),
                        Statement(Call("dotktMapRemove", Any, true, Cast(Any, PairKey()))), Return(Constant(true)))),
                    Return(Constant(false)));
                var copy = CollectionBclSlotSynthesis.CopyTo(TypeJson.Write(pair));
                var copyBody = (JsonArray)copy["body"].DeepClone();
                JsonObject Binary(string op, JsonNode left, JsonNode right) => new() {
                    ["k"] = "binOp", ["op"] = op, ["lhs"] = left, ["rhs"] = right,
                };
                JsonObject Length() => new() { ["k"] = "arrayLen", ["array"] = Local("array") };
                copyBody.Insert(0, If(Binary("<", Binary("-", Length(), Local("arrayIndex")), Call("dotktMapSize", integer)),
                    Throw("System.ArgumentException")));
                copyBody.Insert(0, If(Binary(">", Local("arrayIndex"), Length()), Throw("System.ArgumentOutOfRangeException")));
                copyBody.Insert(0, If(Binary("<", Local("arrayIndex"), new JsonObject {
                    ["k"] = "const", ["type"] = TypeJson.Write(integer), ["value"] = 0,
                }), Throw("System.ArgumentOutOfRangeException")));
                copyBody.Insert(0, If(Binary("==", Local("array"), new JsonObject {
                    ["k"] = "const", ["type"] = TypeJson.Write(Any), ["value"] = null,
                }), Throw("System.ArgumentNullException")));
                AddSlot(collection, "CopyTo", Void, (JsonArray)copy["params"].DeepClone(),
                    copyBody.Select(statement => statement.DeepClone()).ToArray());

                var enumerable = new TypeNode.Fqn("System.Collections.Generic.IEnumerable", new TypeNode[] { pair });
                var rawEnumerable = new TypeNode.Fqn("System.Collections.IEnumerable");
                var rawEnumerator = new TypeNode.Fqn("System.Collections.IEnumerator");
                var enumerator = new TypeNode.Fqn("System.Collections.Generic.IEnumerator", new TypeNode[] { pair });
                JsonObject NewEnumerator() => new() {
                    ["k"] = "new", ["type"] = TypeJson.Write(new TypeNode.Fqn(MapEnumeratorSynthesis.Name, new[] { key, value })),
                    ["localCtorIndex"] = 0, ["argTypes"] = new JsonArray(TypeJson.Write(rawEnumerator)),
                    ["args"] = new JsonArray(new JsonObject {
                        ["k"] = "callInstance", ["ownerType"] = TypeJson.Write(rawEnumerable),
                        ["method"] = "GetEnumerator", ["virtual"] = true,
                        ["recv"] = Cast(rawEnumerable, Call("dotktMapEntries", Any)),
                        ["sig"] = new JsonArray(), ["args"] = new JsonArray(), ["ret"] = TypeJson.Write(rawEnumerator),
                    }),
                };
                AddSlot(enumerable, "GetEnumerator", enumerator, new JsonArray(), Return(NewEnumerator()));
                AddSlot(rawEnumerable, "GetEnumerator", rawEnumerator, new JsonArray(), Return(NewEnumerator()));
            }
        }
        return used;
    }
}
