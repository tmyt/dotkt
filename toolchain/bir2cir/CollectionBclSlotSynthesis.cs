using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// BCL-only collection-interface slots. A Kotlin `MutableCollection<E>` is `@ClrTypeAlias("System.Collections.Generic.
// ICollection")` and `MutableList<E>` is `IList` — but `ICollection<T>`/`IList<T>` carry members Kotlin's collection
// interfaces do NOT: `Contains`, `CopyTo`, `IsReadOnly` (ICollection) and `IndexOf` (IList). Kotlin has the
// value-returning `contains`/`indexOf` under LOWERCASE names (not @ClrIntrinsic-renamed → DeclarationRename leaves them
// lowercase), and has NO equivalent for `CopyTo`/`IsReadOnly` at all. So a Kotlin class implementing the aliased
// interface (kotlin.collections.AbstractMutable{Collection,List,Set}, a MutableMap keys/values view, a user class) is
// missing those BCL slots → the CLR loader rejects any CONCRETE type in the hierarchy ("Method 'Contains' ... does not
// have an implementation" → TypeLoadException). Concrete Kotlin-backed collections need these native slots regardless
// of whether other collection factories happen to allocate native BCL storage.
//
// Fill each missing slot with an ordinary public forwarding member, following the constructed interface closure.
// A class base already supplies its native faces; inherit those implementations rather than replacing their policy.
// An IList face also reaches ICollection through that same graph. Contains and
// IndexOf dispatch through the already-resolved Kotlin query slots, which own the ordinary/storage argument
// boundary and the selected implementation (including renamed overrides). CopyTo consumes the exact CLR IEnumerable<E> face.
// IsReadOnly returns false. The return-DROPPING slots
// (Add/set_Item/RemoveAt) join the common late KotlinOverrideSlotBridge allocation. Non-ref builds only (the ref surface
// stays pure Kotlin). Modeled on ComparableBridgeSynthesis.
static class CollectionBclSlotSynthesis
{
    const string ICollection = "System.Collections.Generic.ICollection";
    const string IList = "System.Collections.Generic.IList";

    public static void ApplyAll(IEnumerable<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        // Snapshot the whole module before synthesis: a user interface/base can live in a later input file.
        var definitions = SupertypeGraph.Collect(roots);
        foreach (var definition in definitions.Values)
        {
            if (definition.Kind != "class") continue; // interfaces carry no bodies
            var to = definition.Node;
            if (to["interfaces"] is not JsonArray ifaces) continue;

            var faces = SupertypeGraph.Reachable(
                    new SupertypeGraph.Def { Interfaces = definition.Interfaces }, definitions, refs)
                .Where(edge => edge.isInterface && IsCollectionFace(edge.spec))
                .Select(edge => edge.spec).ToList();
            if (faces.Count == 0) continue;
            var inherited = definition.Base == null ? new HashSet<string>()
                : SupertypeGraph.Reachable(new SupertypeGraph.Def { Base = definition.Base }, definitions, refs)
                    .Where(edge => edge.isInterface && IsCollectionFace(edge.spec))
                    .Select(edge => SupertypeGraph.TypeKey(edge.spec)).ToHashSet();

            // Each edge has already been substituted into this class's exact physical generic frame.
            JsonNode collElem = null, listElem = null;
            foreach (var face in faces)
            {
                if (inherited.Contains(SupertypeGraph.TypeKey(face))) continue;
                if (face.Name == IList) listElem ??= TypeJson.Write(face.Args[0]);
                else collElem ??= TypeJson.Write(face.Args[0]);
                // State the native face so subsequent slot and read-only-sibling synthesis consume the same facts.
                if (!ifaces.Any(i => SupertypeGraph.TypeKey(TypeJson.Read(i)) == SupertypeGraph.TypeKey(face)))
                    ifaces.Add(TypeJson.Write(face));
            }
            if (collElem == null && listElem == null) continue;

            if (to["methods"] is not JsonArray methods) { methods = new JsonArray(); to["methods"] = methods; }
            bool Has(string name) => methods.OfType<JsonObject>().Any(m => (m["name"] as JsonValue)?.GetValue<string>() == name);

            // ICollection<E> face: Contains / CopyTo / get_IsReadOnly.
            if (collElem != null)
            {
                if (!Has("Contains")) methods.Add(QuerySlotForward("Contains", collElem, "System.Boolean",
                    "KotlinCollectionDefaultSlots", "dotktContains"));
                if (!Has("get_IsReadOnly")) methods.Add(ConstBoolGetter("get_IsReadOnly"));
                if (!Has("CopyTo")) methods.Add(CopyTo(collElem));
            }
            // IList<E> face additionally needs IndexOf.
            if (listElem != null && !Has("IndexOf"))
                methods.Add(QuerySlotForward("IndexOf", listElem, "System.Int32",
                    "KotlinListDefaultSlots", "dotktIndexOf"));
        }
    }

    static bool IsCollectionFace(TypeNode.Fqn face) =>
        face.Name is ICollection or IList && face.Args is { Length: 1 };

    static JsonNode Clone(JsonNode n) => n == null ? null : JsonNode.Parse(n.ToJsonString());

    static JsonObject Method(string name, JsonArray parameters, JsonNode ret, JsonArray body) => new()
    {
        ["name"] = name,
        ["static"] = false,
        ["override"] = false,
        ["virtual"] = true,
        ["abstract"] = false,
        ["objectOverride"] = false,
        ["vis"] = "public",
        ["params"] = parameters,
        ["ret"] = ret,
        ["body"] = body,
    };

    static JsonObject This() => new() { ["k"] = "this" };
    static JsonObject Local(string name) => new() { ["k"] = "local", ["name"] = name };

    // The semantic slot accepts object and already carries the selected Kotlin override and its type-safe barrier.
    // Do not reconstruct a target by lowercase name or use the incoming storage type as its ordinary signature.
    static JsonObject QuerySlotForward(string name, JsonNode elem, string ret, string slotOwner, string target) =>
        Method(name,
            new JsonArray(new JsonObject { ["name"] = "element", ["type"] = Clone(elem) }),
            TypeJson.Fqn(ret),
            new JsonArray(new JsonObject
            {
                ["k"] = "return",
                ["value"] = new JsonObject
                {
                    ["k"] = "callInstance",
                    ["ownerType"] = TypeJson.Fqn("DotKt.Runtime.CompilerServices." + slotOwner),
                    ["virtual"] = true,
                    ["recv"] = This(),
                    ["method"] = target,
                    ["sig"] = new JsonArray(TypeJson.Fqn("System.Object")),
                    ["ret"] = TypeJson.Fqn(ret),
                    ["args"] = new JsonArray(new JsonObject {
                        ["k"] = "cast", ["type"] = TypeJson.Fqn("System.Object"), ["e"] = Local("element"),
                    }),
                },
            }));

    static JsonObject ConstBoolGetter(string name) =>
        Method(name, new JsonArray(), TypeJson.Fqn("System.Boolean"),
            new JsonArray(new JsonObject
            {
                ["k"] = "return",
                ["value"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("System.Boolean"), ["value"] = false },
            }));

    // This slot is synthesized after type lowering: E is an exact CLR element, not a Kotlin type argument.
    // Enumerate that physical face directly and dispose even when an array write throws.
    internal static JsonObject CopyTo(JsonNode elem)
    {
        var element = TypeJson.Read(elem);
        var iterator = new TypeNode.Fqn("System.Collections.Generic.IEnumerator", new[] { element });
        JsonObject Call(TypeNode owner, string method, JsonNode receiver, TypeNode result) => new() {
            ["k"] = "callInstance", ["ownerType"] = TypeJson.Write(owner), ["method"] = method,
            ["virtual"] = true,
            ["recv"] = receiver, ["sig"] = new JsonArray(), ["args"] = new JsonArray(), ["ret"] = TypeJson.Write(result),
        };
        var body = new JsonArray
        {
            new JsonObject
            {
                ["k"] = "var", ["name"] = "it", ["type"] = TypeJson.Write(iterator),
                ["init"] = Call(new TypeNode.Fqn("System.Collections.Generic.IEnumerable", new[] { element }),
                    "GetEnumerator", This(), iterator),
            },
            new JsonObject { ["k"] = "var", ["name"] = "i", ["type"] = TypeJson.Fqn("System.Int32"), ["init"] = Local("arrayIndex") },
            new JsonObject
            {
                ["k"] = "while",
                ["cond"] = Call(new TypeNode.Fqn("System.Collections.IEnumerator"), "MoveNext", Local("it"), new TypeNode.Fqn("System.Boolean")),
                ["body"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["k"] = "exprStmt",
                        ["expr"] = new JsonObject
                        {
                            ["k"] = "arraySet", ["array"] = Local("array"), ["index"] = Local("i"), ["elem"] = Clone(elem),
                            ["value"] = Call(iterator, "get_Current", Local("it"), element),
                        },
                    },
                    new JsonObject
                    {
                        ["k"] = "setLocal", ["name"] = "i",
                        ["value"] = new JsonObject
                        {
                            ["k"] = "binOp", ["op"] = "+", ["lhs"] = Local("i"),
                            ["rhs"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("System.Int32"), ["value"] = 1 },
                        },
                    },
                },
            },
        };
        var loop = body[2];
        body.RemoveAt(2);
        body.Add(new JsonObject {
            ["k"] = "try", ["body"] = new JsonArray(loop), ["catches"] = new JsonArray(),
            ["finally"] = new JsonArray(new JsonObject { ["k"] = "exprStmt",
                ["expr"] = Call(new TypeNode.Fqn("System.IDisposable"), "Dispose", Local("it"), new TypeNode.Fqn("void")) }),
        });
        return Method("CopyTo",
            new JsonArray(
                new JsonObject { ["name"] = "array", ["type"] = new JsonObject { ["t"] = "array", ["elem"] = Clone(elem) } },
                new JsonObject { ["name"] = "arrayIndex", ["type"] = TypeJson.Fqn("System.Int32") }),
            TypeJson.Fqn("void"), body);
    }
}
