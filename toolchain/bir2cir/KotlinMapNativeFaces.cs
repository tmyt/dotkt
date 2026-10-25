using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// The same Kotlin-owned map exposes all native dictionary contracts. None of
// these faces sees its private hash keys, copies entries, or changes Kotlin views.
static partial class KotlinMapStorageSynthesis
{
    static void AppendNativeFaces(ReferenceMetadataIndex refs, bool declarationOnly, JsonArray methods)
    {
        JsonObject RawHelper(string name, TypeNode ret, TypeNode[] arguments, TypeNode[] signature, params JsonNode[] operands) =>
            HelperOn(refs, "kotlin.collections.ClrAuthoredMapNativeFacesKt", name, ret, arguments, signature, operands);
        JsonObject Throw(string type) => new() { ["k"] = "throw", ["value"] = new JsonObject {
            ["k"] = "new", ["type"] = TypeJson.Fqn(type), ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
        } };
        JsonObject If(JsonNode condition, params JsonNode[] body) => new() {
            ["k"] = "if", ["branches"] = new JsonArray(new JsonObject {
                ["cond"] = condition, ["body"] = new JsonArray(body),
            }),
        };
        JsonObject Equal(JsonNode left, JsonNode right) => new() {
            ["k"] = "binOp", ["op"] = "==", ["lhs"] = left, ["rhs"] = right,
        };
        JsonObject Null() => new() { ["k"] = "const", ["type"] = TypeJson.Write(Any), ["value"] = null };
        JsonObject Accept(TypeNode type, string name) => new() {
            ["k"] = "cond", ["type"] = TypeJson.Write(Bool),
            ["cond"] = Equal(Local(name), Null()),
            ["then"] = Equal(Cast(Any, new JsonObject { ["k"] = "default", ["type"] = TypeJson.Write(type) }), Null()),
            ["else"] = new JsonObject { ["k"] = "isInst", ["type"] = TypeJson.Write(type), ["e"] = Local(name) },
        };
        JsonObject Mutable() => new() { ["k"] = "isInst",
            ["type"] = TypeJson.Fqn(KotlinCollectionSlotSynthesis.MutableMapSlots), ["e"] = Self() };
        JsonObject ReadOnly() => new() { ["k"] = "unaryOp", ["op"] = "!", ["e"] = Mutable() };
        JsonObject Contains() => Slot("dotktMapContainsKey", Bool, Cast(Any, Local("key")));
        JsonObject Get() => Slot("dotktMapGet", Any, Cast(Any, Local("key")));
        void Define(TypeNode owner, string member, TypeNode ret, JsonArray parameters, Func<JsonArray> body) =>
            methods.Add(Method(owner, member, ret, parameters, declarationOnly
                ? new JsonArray(Throw("System.NotSupportedException")) : body()));
        JsonObject H(string name, TypeNode ret, params JsonNode[] args) => Helper(refs, name, ret,
            Array.Empty<TypeNode>(), new[] { (TypeNode)new TypeNode.Fqn("kotlin.Any") }
                .Concat(args.Select(_ => (TypeNode)new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")))).ToArray(),
            new[] { (JsonNode)Self() }.Concat(args).ToArray());
        var ro = F("System.Collections.Generic.IReadOnlyDictionary", K, V);
        Define(ro, "get_Item", V, new JsonArray(Parameter("key", K)), () => new JsonArray(
            If(Contains(), Return(Cast(V, Get()))), Throw("System.Collections.Generic.KeyNotFoundException")));
        Define(ro, "ContainsKey", Bool, new JsonArray(Parameter("key", K)), () => new JsonArray(Return(Contains())));
        JsonObject Store(JsonNode value) => Expr(new JsonObject { ["k"] = "byrefStore",
            ["ptr"] = Local("value"), ["elem"] = TypeJson.Write(V), ["value"] = value });
        Define(ro, "TryGetValue", Bool, new JsonArray(Parameter("key", K), Parameter("value", new TypeNode.ByRef(V))),
            () => new JsonArray(If(Contains(), Store(Cast(V, Get())), Return(Constant(Bool, true))),
                Store(new JsonObject { ["k"] = "default", ["type"] = TypeJson.Write(V) }), Return(Constant(Bool, false))));
        Define(F("System.Collections.Generic.IReadOnlyCollection", Pair), "get_Count", Int,
            new JsonArray(), () => new JsonArray(Return(Slot("dotktMapSize", Int))));
        foreach (var (member, slot, element) in new[] { ("get_Keys", "dotktMapKeys", K), ("get_Values", "dotktMapValues", V) })
            Define(ro, member, F(Enumerable, element), new JsonArray(), () => new JsonArray(Return(
                Cast(F(Enumerable, element), Helper(refs, "clrAuthoredMapCollection", Any, new[] { element },
                    new TypeNode[] { new TypeNode.Fqn("kotlin.Any") }, Slot(slot, Any))))));

        var raw = F("System.Collections.IDictionary");
        var collection = F("System.Collections.ICollection");
        JsonArray Key() => new(Parameter("key", Any));
        Define(raw, "Contains", Bool, Key(), () => new JsonArray(
            If(Accept(K, "key"), Return(Contains())), Return(Constant(Bool, false))));
        Define(raw, "get_Item", Any, Key(), () => new JsonArray(
            If(Accept(K, "key"), If(Contains(), Return(Get()))), Return(Null())));
        foreach (var (member, helper) in new[] { ("set_Item", "clrAuthoredMapSet"), ("Add", "clrAuthoredMapAdd") })
            Define(raw, member, Void, new JsonArray(Parameter("key", Any), Parameter("value", Any)), () => new JsonArray(
                If(new JsonObject { ["k"] = "unaryOp", ["op"] = "!", ["e"] = Accept(K, "key") }, Throw("System.ArgumentException")),
                If(new JsonObject { ["k"] = "unaryOp", ["op"] = "!", ["e"] = Accept(V, "value") }, Throw("System.ArgumentException")),
                Expr(H(helper, Void, Local("key"), Local("value")))));
        Define(raw, "Remove", Void, Key(), () => new JsonArray(
            Expr(H("clrAuthoredMapCheckMutable", Void)), If(Accept(K, "key"), Expr(H("clrAuthoredMapRemove", Bool, Local("key"))))));
        Define(raw, "Clear", Void, new JsonArray(), () => new JsonArray(Expr(H("clrAuthoredMapClear", Void))));
        foreach (var member in new[] { "get_IsReadOnly", "get_IsFixedSize" })
            Define(raw, member, Bool, new JsonArray(), () => new JsonArray(Return(ReadOnly())));
        Define(collection, "get_Count", Int, new JsonArray(), () => new JsonArray(Return(Slot("dotktMapSize", Int))));
        Define(collection, "get_IsSynchronized", Bool, new JsonArray(), () => new JsonArray(Return(Constant(Bool, false))));
        Define(collection, "get_SyncRoot", Any, new JsonArray(), () => new JsonArray(Return(Self())));
        foreach (var (member, slot, element) in new[] { ("get_Keys", "dotktMapKeys", K), ("get_Values", "dotktMapValues", V) })
            Define(raw, member, collection, new JsonArray(), () => new JsonArray(Return(Cast(collection,
                RawHelper("clrAuthoredRawCollection", Any, Array.Empty<TypeNode>(),
                    new TypeNode[] { new TypeNode.Fqn("kotlin.Any"), new TypeNode.Fqn("kotlin.Any"), new TypeNode.Fqn("kotlin.Any") },
                    Slot(slot, Any), Self(), new JsonObject { ["k"] = "classRef", ["type"] = TypeJson.Write(element) })))));
        Define(raw, "GetEnumerator", F("System.Collections.IDictionaryEnumerator"), new JsonArray(), () => new JsonArray(Return(
            Cast(F("System.Collections.IDictionaryEnumerator"), RawHelper("clrAuthoredMapRawEnumerator", Any,
                Array.Empty<TypeNode>(), new TypeNode[] { new TypeNode.Fqn("kotlin.Any") }, Self())))));
        Define(collection, "CopyTo", Void, new JsonArray(Parameter("array", F("System.Array")), Parameter("index", Int)),
            () => new JsonArray(Expr(RawHelper("clrAuthoredMapRawCopy", Void, new[] { K, V },
                new TypeNode[] { new TypeNode.Fqn("kotlin.Any"), new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")),
                    new TypeNode.Fqn("kotlin.Int"), new TypeNode.Fqn("kotlin.Any") },
                Self(), Cast(Any, Local("array")), Local("index"),
                new JsonObject { ["k"] = "classRef", ["type"] = TypeJson.Write(Pair) }))));
    }
}
