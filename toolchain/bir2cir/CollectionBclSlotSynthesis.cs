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
// needs only IndexOf here; the ICollection face comes from a base or the direct ICollection listing. Contains→`contains`
// / IndexOf→`indexOf` self-forward (the alias mandates the class declare
// them). CopyTo iterates via ClrIteratorBridgeKt.iteratorOverEnumerable(this) — a static resolvable regardless of whether
// THIS class declares iterator() (AbstractMutableSet inherits it). IsReadOnly returns false. The return-DROPPING slots
// (Add/set_Item/RemoveAt) join the common late KotlinOverrideSlotBridge allocation. Non-ref builds only (the ref surface
// stays pure Kotlin). Modeled on ComparableBridgeSynthesis.
static class CollectionBclSlotSynthesis
{
    const string ICollection = "System.Collections.Generic.ICollection";
    const string IList = "System.Collections.Generic.IList";
    const string IteratorBridge = "kotlin.collections.ClrIteratorBridgeKt";

    public static void Apply(JsonNode root)
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

            // The `this.<contains/indexOf>()` self-forward target must reference the CONSTRUCTED self `Owner<!0,…>`, not
            // the OPEN `Owner`1`: this pass runs AFTER GenericSelfInstantiation (which would otherwise construct a bare
            // self ownerType), so a generic self-forward carrying only the bare name resolves the callee on the open def
            // and mismatches the constructed `this` (ilverify StackUnexpected [found Owner<T0>][expected Owner`1]).
            var selfOwner = SelfOwnerType(owner, TypeParameterFrame.Count(to));

            // ICollection<E> face: Contains / CopyTo / get_IsReadOnly.
            if (!Has("Contains")) methods.Add(SelfForward("Contains", elem, "System.Boolean", "contains", selfOwner));
            if (!Has("get_IsReadOnly")) methods.Add(ConstBoolGetter("get_IsReadOnly"));
            if (!Has("CopyTo")) methods.Add(CopyTo(elem));
            // IList<E> face additionally needs IndexOf.
            if (listElem != null && !Has("IndexOf"))
                methods.Add(SelfForward("IndexOf", listElem, "System.Int32", "indexOf", selfOwner));
        }
    }

    // The constructed self owner `Owner<!0,…,!n-1>` (the type-scope generic params by position) for a generic class,
    // else the bare `Owner` node for a non-generic one — mirrors GenericSelfInstantiation's constructed-self derivation.
    static JsonNode SelfOwnerType(string owner, int n)
    {
        if (n == 0) return TypeJson.Fqn(owner);
        var args = new JsonArray();
        for (var i = 0; i < n; i++) args.Add(new JsonObject { ["t"] = "tv", ["scope"] = "type", ["i"] = i });
        return new JsonObject { ["t"] = "fqn", ["name"] = owner, ["args"] = args };
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

    // `return this.<target>(element)` — Contains→contains, IndexOf→indexOf. Virtual dispatch covers a base impl.
    static JsonObject SelfForward(string name, JsonNode elem, string ret, string target, JsonNode ownerType) =>
        Method(name,
            new JsonArray(new JsonObject { ["name"] = "element", ["type"] = Clone(elem) }),
            TypeJson.Fqn(ret),
            new JsonArray(new JsonObject
            {
                ["k"] = "return",
                ["value"] = new JsonObject
                {
                    ["k"] = "callInstance",
                    ["ownerType"] = Clone(ownerType),
                    ["virtual"] = true,
                    ["recv"] = This(),
                    ["method"] = target,
                    ["sig"] = new JsonArray(Clone(elem)),
                    ["ret"] = TypeJson.Fqn(ret),
                    ["args"] = new JsonArray(Local("element")),
                },
            }));

    static JsonObject ConstBoolGetter(string name) =>
        Method(name, new JsonArray(), TypeJson.Fqn("System.Boolean"),
            new JsonArray(new JsonObject
            {
                ["k"] = "return",
                ["value"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("System.Boolean"), ["value"] = false },
            }));

    // `CopyTo(array: E[], arrayIndex: Int)` = `var it = iteratorOverEnumerable(this); var i = arrayIndex;
    // while (it.hasNext()) { array[i] = it.next(); i = i + 1 }`. iteratorOverEnumerable is the stdlib's own IEnumerable->
    // Kotlin-iterator bridge (a static resolvable from any assembly, unlike a virtual iterator() this class may inherit).
    // It returns the BASE kotlin.collections.Iterator<T> (NOT MutableIterator) — typing the local as the exact return keeps
    // the `stloc` verifiable; hasNext()/next() are Iterator's own members (remove() is never used).
    static JsonObject CopyTo(JsonNode elem)
    {
        JsonObject IterType() => new() { ["t"] = "fqn", ["name"] = "kotlin.collections.Iterator", ["args"] = new JsonArray(Clone(elem)) };
        var body = new JsonArray
        {
            new JsonObject
            {
                ["k"] = "var", ["name"] = "it", ["type"] = IterType(),
                ["init"] = new JsonObject
                {
                    ["k"] = "callStatic", ["owner"] = TypeJson.Fqn(IteratorBridge), ["method"] = "iteratorOverEnumerable",
                    ["sig"] = new JsonArray { TypeJson.Write(new TypeNode.Fqn("System.Collections.Generic.IEnumerable",
                        new TypeNode[] { new TypeNode.Tv("method", 0) })) },
                    ["args"] = new JsonArray(This()), ["typeArgs"] = new JsonArray(Clone(elem)),
                },
            },
            new JsonObject { ["k"] = "var", ["name"] = "i", ["type"] = TypeJson.Fqn("System.Int32"), ["init"] = Local("arrayIndex") },
            new JsonObject
            {
                ["k"] = "while",
                ["cond"] = new JsonObject
                {
                    ["k"] = "callInstance", ["ownerType"] = IterType(), ["virtual"] = true,
                    ["recv"] = Local("it"), ["method"] = "hasNext", ["sig"] = new JsonArray(), ["ret"] = TypeJson.Fqn("System.Boolean"), ["args"] = new JsonArray(),
                },
                ["body"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["k"] = "exprStmt",
                        ["expr"] = new JsonObject
                        {
                            ["k"] = "arraySet", ["array"] = Local("array"), ["index"] = Local("i"), ["elem"] = Clone(elem),
                            ["value"] = new JsonObject
                            {
                                ["k"] = "callInstance", ["ownerType"] = IterType(), ["virtual"] = true,
                                ["recv"] = Local("it"), ["method"] = "next", ["sig"] = new JsonArray(), ["ret"] = Clone(elem), ["args"] = new JsonArray(),
                            },
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
        return Method("CopyTo",
            new JsonArray(
                new JsonObject { ["name"] = "array", ["type"] = new JsonObject { ["t"] = "array", ["elem"] = Clone(elem) } },
                new JsonObject { ["name"] = "arrayIndex", ["type"] = TypeJson.Fqn("System.Int32") }),
            TypeJson.Fqn("void"), body);
    }
}
