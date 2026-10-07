using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// CLR enumerators expose KeyValuePair<K,V>; Kotlin maps expose Map.Entry<K,V>.
// This adapter changes only the element representation, retaining the original iterator.
static class MapEnumeratorSynthesis
{
    internal const string Name = "dotkt$MapEntryEnumerator";
    static readonly TypeNode Any = new TypeNode.Fqn("System.Object");
    static readonly TypeNode Void = new TypeNode.Fqn("void");
    static readonly TypeNode Bool = new TypeNode.Fqn("System.Boolean");
    static readonly TypeNode RawEnumerator = new TypeNode.Fqn("System.Collections.IEnumerator");
    static readonly TypeNode Disposable = new TypeNode.Fqn("System.IDisposable");
    static JsonObject Self() => new() { ["k"] = "this" };
    static JsonObject Local(string name) => new() { ["k"] = "local", ["name"] = name };
    static JsonObject Return(JsonNode value) => new() { ["k"] = "return", ["value"] = value };
    static JsonObject Cast(TypeNode type, JsonNode value) => new() {
        ["k"] = "cast", ["type"] = TypeJson.Write(type), ["e"] = value,
    };
    static JsonObject Call(TypeNode owner, string member, TypeNode result, JsonNode receiver) => new() {
        ["k"] = "callInstance", ["ownerType"] = TypeJson.Write(owner), ["method"] = member,
        ["virtual"] = true, ["recv"] = receiver, ["sig"] = new JsonArray(),
        ["args"] = new JsonArray(), ["ret"] = TypeJson.Write(result),
    };
    static JsonObject Method(string name, TypeNode owner, string member, TypeNode result, params JsonNode[] body) => new() {
        ["name"] = name, ["static"] = false, ["virtual"] = true, ["override"] = false,
        ["abstract"] = false, ["vis"] = "private", ["generated"] = true,
        [KotlinPropertyAccessors.PhysicalSlotBridgeKey] = true,
        ["params"] = new JsonArray(), ["ret"] = TypeJson.Write(result),
        ["body"] = new JsonArray(body), ["bodyTerminates"] = true,
        ["clrInterfaceImpls"] = new JsonArray(new JsonObject {
            ["owner"] = TypeJson.Write(owner), ["member"] = member, ["arity"] = 0,
            ["params"] = new JsonArray(), ["ret"] = TypeJson.Write(result),
        }),
    };

    static JsonObject EntryPart(ReferenceMetadataIndex refs, string name)
    {
        const string owner = "kotlin.collections.ClrMapDefaultsKt";
        var helper = refs.AuthoredKotlinHelper(owner, name, 0, new[] { new TypeNode.Fqn("kotlin.Any") });
        TypeNode Physical(TypeNode type) => BirTypeLowering.LowerPhysicalType(type, refs.Aliases,
            refs.IsValueType, refs.PhysicalTypeNames, typeArg: false, nullableFrames: refs.NullableTypeFrames);
        return new JsonObject {
            ["k"] = "callStatic", ["owner"] = TypeJson.Fqn(owner), ["method"] = name,
            [DeclarationIdentityBinding.Key] = helper.DeclarationId,
            ["sig"] = new JsonArray(helper.ParamTypeNodes.Select(Physical).Select(TypeJson.Write).ToArray()),
            ["args"] = new JsonArray(Local("entry")), ["ret"] = TypeJson.Write(Any),
        };
    }

    internal static JsonObject Create(ReferenceMetadataIndex refs)
    {
        var key = new TypeNode.Tv("type", 0);
        var value = new TypeNode.Tv("type", 1);
        var self = new TypeNode.Fqn(Name, new TypeNode[] { key, value });
        var pair = new TypeNode.Fqn("System.Collections.Generic.KeyValuePair", new TypeNode[] { key, value });
        var enumerator = new TypeNode.Fqn("System.Collections.Generic.IEnumerator", new TypeNode[] { pair });
        JsonObject Inner() => new() {
            ["k"] = "field", ["ownerType"] = TypeJson.Write(self), ["recv"] = Self(), ["name"] = "_inner",
        };
        var current = Method("dotkt$Current", enumerator, "get_Current", pair,
            // A custom Current getter can have side effects: observe it exactly once.
            new JsonObject { ["k"] = "var", ["name"] = "entry", ["type"] = TypeJson.Write(Any),
                ["init"] = Call(RawEnumerator, "get_Current", Any, Inner()) },
            Return(new JsonObject {
                ["k"] = "new", ["type"] = TypeJson.Write(pair),
                ["argTypes"] = new JsonArray(TypeJson.Write(key), TypeJson.Write(value)),
                ["args"] = new JsonArray(Cast(key, EntryPart(refs, "clrMapEntryKey")),
                    Cast(value, EntryPart(refs, "clrMapEntryValue"))),
            }));
        var rawCurrent = Method("dotkt$RawCurrent", RawEnumerator, "get_Current", Any,
            Return(Cast(Any, Call(enumerator, "get_Current", pair, Self()))));
        var move = Method("MoveNext", RawEnumerator, "MoveNext", Bool,
            Return(Call(RawEnumerator, "MoveNext", Bool, Inner())));
        var reset = Method("Reset", RawEnumerator, "Reset", Void,
            new JsonObject { ["k"] = "exprStmt", ["expr"] = Call(RawEnumerator, "Reset", Void, Inner()) },
            new JsonObject { ["k"] = "return" });
        var dispose = Method("Dispose", Disposable, "Dispose", Void,
            new JsonObject { ["k"] = "if", ["branches"] = new JsonArray(new JsonObject {
                ["cond"] = new JsonObject { ["k"] = "isInst", ["type"] = TypeJson.Write(Disposable), ["e"] = Inner() },
                ["body"] = new JsonArray(new JsonObject { ["k"] = "exprStmt",
                    ["expr"] = Call(Disposable, "Dispose", Void, Cast(Disposable, Inner())) }),
            }) }, new JsonObject { ["k"] = "return" });
        return new JsonObject {
            ["name"] = Name, ["kind"] = "class", ["generated"] = true,
            ["vis"] = "internal", ["abstract"] = false, ["final"] = true, ["base"] = null,
            ["typeParams"] = new JsonArray("K", "V"),
            ["interfaces"] = new JsonArray(TypeJson.Write(enumerator), TypeJson.Write(RawEnumerator), TypeJson.Write(Disposable)),
            ["fields"] = new JsonArray(new JsonObject {
                ["name"] = "_inner", ["type"] = TypeJson.Write(RawEnumerator), ["vis"] = "private", ["initOnly"] = true,
            }),
            ["ctors"] = new JsonArray(new JsonObject {
                ["vis"] = "public", ["baseArgs"] = null, ["thisArgs"] = null,
                ["params"] = new JsonArray(new JsonObject { ["name"] = "source", ["type"] = TypeJson.Write(RawEnumerator) }),
                ["body"] = new JsonArray(new JsonObject {
                    ["k"] = "setField", ["ownerType"] = TypeJson.Write(self), ["recv"] = Self(),
                    ["name"] = "_inner", ["value"] = Local("source"),
                }),
            }),
            ["methods"] = new JsonArray(current, rawCurrent, move, reset, dispose),
        };
    }
}
