using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// BCL-only collection-interface slots. A Kotlin `MutableCollection<E>` is `@ClrTypeAlias("System.Collections.Generic.
// ICollection")` and `MutableList<E>` is `IList` — but `ICollection<T>`/`IList<T>` carry members Kotlin's collection
// interfaces do NOT: `Contains`, `CopyTo`, `IsReadOnly` (ICollection) and `IndexOf` (IList). Kotlin has the
// value-returning `contains`/`indexOf` under LOWERCASE names (not @ClrIntrinsic-renamed → DeclarationRename leaves them
// lowercase), and has NO equivalent for `CopyTo`/`IsReadOnly` at all. So a Kotlin class DIRECTLY implementing the aliased
// interface (kotlin.collections.AbstractMutable{Collection,List,Set}, a MutableMap keys/values view, a user class) is
// missing those BCL slots → the CLR loader rejects any CONCRETE type in the hierarchy ("Method 'Contains' ... does not
// have an implementation" → TypeLoadException), which ilemit's ResolveType swallows and reports as "cannot resolve .NET
// type kotlin.collections.ArrayDeque`1". (Latent until ArrayDeque: every other runnable concrete collection is a BCL type
// — mutableListOf → List<T>, mutableMapOf → Dictionary<K,V> — never the Kotlin class.)
//
// Fill each missing slot with an ordinary public forwarding member, keyed on the DIRECTLY-listed alias. An IList face
// needs only IndexOf here; the ICollection face comes from a base or the direct ICollection listing. Contains and
// IndexOf dispatch through the already-resolved Kotlin query slots, which own the ordinary/storage argument
// boundary and the selected implementation (including renamed overrides). CopyTo consumes the exact CLR IEnumerable<E> face.
// IsReadOnly returns false. The return-DROPPING slots
// (Add/set_Item/RemoveAt) join the common late KotlinOverrideSlotBridge allocation. Non-ref builds only (the ref surface
// stays pure Kotlin). Modeled on ComparableBridgeSynthesis.
static class CollectionBclSlotSynthesis
{
    const string ICollection = "System.Collections.Generic.ICollection";
    const string IList = "System.Collections.Generic.IList";

    public static void Apply(JsonNode root, ReferenceMetadataIndex refs)
    {
        if (root is not JsonObject o || o["types"] is not JsonArray types) return;
        foreach (var t in types)
        {
            if (t is not JsonObject to) continue;
            if ((to["kind"] as JsonValue)?.GetValue<string>() != "class") continue;   // interfaces carry no bodies
            var owner = (to["name"] as JsonValue)?.GetValue<string>();
            if (string.IsNullOrEmpty(owner)) continue;
            if (to["interfaces"] is not JsonArray ifaces) continue;

            // The element type = the FIRST type-arg of a DIRECTLY-listed ICollection<E> / IList<E>.
            JsonNode collElem = null, listElem = null;
            foreach (var i in ifaces)
            {
                if (TypeJson.Read(i) is not TypeNode.Fqn f || f.Args is not { Length: 1 }) continue;
                if (f.Name == IList) listElem ??= ArgNode(i);
                else if (f.Name == ICollection) collElem ??= ArgNode(i);
            }
            if (collElem == null && listElem == null) continue;

            if (to["methods"] is not JsonArray methods) { methods = new JsonArray(); to["methods"] = methods; }
            bool Has(string name) => methods.OfType<JsonObject>().Any(m => (m["name"] as JsonValue)?.GetValue<string>() == name);

            // The CLR stdlib's mutable-collection abstract classes are FLAT (`AbstractMutableList : MutableList`, base
            // Object — NOT `: AbstractMutableCollection`), so an IList<E> implementer does NOT inherit the ICollection<E>
            // face from a base. List ICollection<E> EXPLICITLY so the common slot pass sees that face too — its `Add`
            // void-drop bridge, `Remove`/`Clear`/`Count` (the class's own renamed members), and the synthesized
            // `Contains`/`CopyTo`/`IsReadOnly`. (Redundant-but-legal: IList already implies ICollection.)
            var elem = listElem ?? collElem;
            if (listElem != null && !ifaces.Any(i => TypeJson.Read(i) is TypeNode.Fqn { Name: ICollection }))
                ifaces.Add(new JsonObject { ["t"] = "fqn", ["name"] = ICollection, ["args"] = new JsonArray(Clone(elem)) });

            // ICollection<E> face: Contains / CopyTo / get_IsReadOnly.
            if (!Has("Contains")) methods.Add(QuerySlotForward("Contains", elem, "System.Boolean",
                "KotlinCollectionDefaultSlots", "dotktContains"));
            if (!Has("get_IsReadOnly")) methods.Add(ConstBoolGetter("get_IsReadOnly"));
            if (!Has("CopyTo")) methods.Add(CopyTo(elem));
            // IList<E> face additionally needs IndexOf.
            if (listElem != null && !Has("IndexOf"))
                methods.Add(QuerySlotForward("IndexOf", listElem, "System.Int32",
                    "KotlinListDefaultSlots", "dotktIndexOf"));
        }
    }

    // The interface node's first type-arg, cloned as a fresh JsonNode (so it can be attached under several slots).
    static JsonNode ArgNode(JsonNode ifaceNode) =>
        (ifaceNode as JsonObject)?["args"] is JsonArray a && a.Count == 1 ? Clone(a[0]) : null;

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
