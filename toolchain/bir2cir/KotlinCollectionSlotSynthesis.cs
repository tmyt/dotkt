using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// KOTLIN-ONLY collection-interface slots — the mirror image of CollectionBclSlotSynthesis.
//
// That pass fills the BCL members Kotlin's collection interfaces lack (`Contains`/`CopyTo`/`IsReadOnly`/`IndexOf`).
// This one fills the other direction: `MutableCollection<E>` IS `ICollection<E>` and `MutableList<E>` IS `IList<E>`,
// and those BCL interfaces carry NO slot for Kotlin's `removeAll`, `retainAll`, `addAll(elements)` or
// `addAll(index, elements)`. Without a slot there is nothing for a call to dispatch on, so a Kotlin class that
// OVERRIDES one of them could not be reached: the call site can only see the BCL face.
//
// Give every emitted class that declares such a member a real CLR interface slot for it. The class gains
// `DotKt.Runtime.CompilerServices.KotlinMutableCollectionSlots` (plus `KotlinMutableListSlots` for the indexed
// `addAll`) and one private `dotkt$slot$…` bridge per member, wired by an exact `clrInterfaceImpls` MethodImpl
// descriptor. Each bridge casts the erased `Any` parameter back to the member's own declared collection type and
// forwards VIRTUALLY, so a further-derived override still wins and a subclass needs no bridge of its own.
//
// The reconciliation of the two legitimate receiver categories lives in ONE place, the
// `kotlin.collections.ClrCollectionDefaults` dispatchers: they test for these interfaces and otherwise run a default
// written over the BCL slots. The interfaces are non-generic on purpose — see the note on KotlinCollectionSlots.kt:
// a constructed `Slots<E>` test would be defeated by an element type erased to `System.Object` at the call site and
// would then silently skip the override.
//
// Non-ref builds only (the ref surface stays pure Kotlin and states `kotlin.collections.MutableCollection`, from
// which every consumer re-derives the physical form). Modeled on CollectionBclSlotSynthesis.
static class KotlinCollectionSlotSynthesis
{
    const string ICollection = "System.Collections.Generic.ICollection";
    const string IList = "System.Collections.Generic.IList";
    const string CollectionSlots = "DotKt.Runtime.CompilerServices.KotlinMutableCollectionSlots";
    const string ListSlots = "DotKt.Runtime.CompilerServices.KotlinMutableListSlots";

    // The Kotlin member -> (slot interface, slot member, bridge name) mapping. Keyed by the member's Kotlin name and
    // parameter count, which is what distinguishes MutableList's two `addAll` declarations.
    static (string Iface, string Slot, string Bridge)? SlotFor(string member, int paramCount) => (member, paramCount) switch
    {
        ("removeAll", 1) => (CollectionSlots, "dotktRemoveAll", "dotkt$slot$removeAll"),
        ("retainAll", 1) => (CollectionSlots, "dotktRetainAll", "dotkt$slot$retainAll"),
        ("addAll", 1) => (CollectionSlots, "dotktAddAll", "dotkt$slot$addAll"),
        ("addAll", 2) => (ListSlots, "dotktAddAllAt", "dotkt$slot$addAllAt"),
        _ => null,
    };

    public static void Apply(JsonNode root)
    {
        if (root is not JsonObject o || o["types"] is not JsonArray types) return;
        foreach (var t in types) ApplyType(t as JsonObject);
    }

    static void ApplyType(JsonObject to)
    {
        if (to == null || Str(to["kind"]) != "class") return;
        if (to["interfaces"] is not JsonArray ifaces) return;
        if (to["methods"] is not JsonArray methods) return;

        // The BCL collection face this class presents. `IList<E>` implies the indexed `addAll` slot as well;
        // CollectionBclSlotSynthesis has already listed `ICollection<E>` explicitly beside it.
        var hasColl = ifaces.Any(i => TypeJson.Read(i) is TypeNode.Fqn { Name: ICollection, Args.Length: 1 });
        var hasList = ifaces.Any(i => TypeJson.Read(i) is TypeNode.Fqn { Name: IList, Args.Length: 1 });
        if (!hasColl && !hasList) return;

        var bridges = new System.Collections.Generic.List<JsonObject>();
        var wanted = new System.Collections.Generic.HashSet<string>();
        foreach (var m in methods.OfType<JsonObject>().ToList())
        {
            if (Bool(m["static"]) || m["typeParams"] is JsonArray { Count: > 0 }) continue;
            if (Str(m["name"]) is not string name || m["params"] is not JsonArray ps) continue;
            if (SlotFor(name, ps.Count) is not (string iface, string slotMember, string bridgeName)) continue;
            // A `MutableList`-only slot on a class that presents no `IList<E>` face has nothing to implement.
            if (iface == ListSlots && !hasList) continue;
            // Idempotency: the bridge is named after the member, so a second run finds it already present.
            if (methods.OfType<JsonObject>().Any(x => Str(x["name"]) == bridgeName)) continue;
            var declaredParams = ps.OfType<JsonObject>().Select(p => p["type"]).ToArray();
            if (declaredParams.Length != ps.Count || declaredParams.Any(p => p == null)) continue;
            bridges.Add(Bridge(bridgeName, iface, slotMember, to, m, declaredParams));
            wanted.Add(iface);
        }
        if (bridges.Count == 0) return;

        foreach (var iface in wanted)
            if (!ifaces.Any(i => TypeJson.Read(i) is TypeNode.Fqn f && f.Name == iface))
                ifaces.Add(TypeJson.Fqn(iface));
        foreach (var bridge in bridges) methods.Add(bridge);
    }

    // `private bool dotkt$slot$<member>(object p0[, int index]) { return this.<member>((<declared>)p0, …) }`.
    //
    // The erased `Any`/`System.Object` parameter is what makes the capability test instantiation-independent; the
    // cast re-establishes this class's own element instantiation, so a genuinely mismatched argument fails LOUD
    // (InvalidCastException) rather than silently taking the BCL default. The forward is VIRTUAL: the bridge is
    // inherited by subclasses and must reach the most-derived override.
    static JsonObject Bridge(string bridgeName, string iface, string slotMember, JsonObject owner,
        JsonObject member, JsonNode[] declaredParams)
    {
        var ownerType = SelfOwnerType(Str(owner["name"]), (owner["typeParams"] as JsonArray)?.Count ?? 0);
        var bridgeParams = new JsonArray();
        var callArgs = new JsonArray();
        var callSig = new JsonArray();
        var slotParams = new JsonArray();
        for (var i = 0; i < declaredParams.Length; i++)
        {
            // The indexed `addAll(index, elements)` keeps its `Int` index verbatim; only a collection-shaped
            // parameter is erased, and it is the ONLY parameter whose type mentions the element instantiation.
            var declared = declaredParams[i];
            var erase = TypeJson.Read(declared) is not TypeNode.Fqn { Name: "System.Int32" };
            var pname = "p" + i;
            var slotParamType = erase ? TypeJson.Fqn("System.Object") : Clone(declared);
            bridgeParams.Add(new JsonObject { ["name"] = pname, ["type"] = Clone(slotParamType) });
            slotParams.Add(Clone(slotParamType));
            callSig.Add(Clone(declared));
            callArgs.Add(erase
                ? new JsonObject { ["k"] = "cast", ["type"] = Clone(declared), ["e"] = Local(pname) }
                : Local(pname));
        }
        var ret = Clone(member["ret"]) ?? TypeJson.Fqn("System.Boolean");
        return new JsonObject
        {
            ["name"] = bridgeName,
            ["static"] = false,
            ["override"] = false,
            ["virtual"] = true,
            ["abstract"] = false,
            ["objectOverride"] = false,
            ["vis"] = "private",
            ["params"] = bridgeParams,
            ["ret"] = Clone(ret),
            ["body"] = new JsonArray(new JsonObject
            {
                ["k"] = "return",
                ["value"] = new JsonObject
                {
                    ["k"] = "callInstance",
                    ["ownerType"] = Clone(ownerType),
                    ["virtual"] = true,
                    ["recv"] = new JsonObject { ["k"] = "this" },
                    ["method"] = Str(member["name"]),
                    ["sig"] = callSig,
                    ["ret"] = Clone(ret),
                    ["args"] = callArgs,
                },
            }),
            ["attrs"] = new JsonArray(),
            [KotlinPropertyAccessors.PhysicalSlotBridgeKey] = true,
            ["clrInterfaceImpls"] = new JsonArray(new JsonObject
            {
                ["owner"] = TypeJson.Fqn(iface),
                ["member"] = slotMember,
                ["arity"] = 0,
                ["params"] = slotParams,
                ["ret"] = Clone(ret),
            }),
        };
    }

    // The constructed self owner `Owner<!0,…,!n-1>` for a generic class, else the bare `Owner` node — mirrors
    // CollectionBclSlotSynthesis.SelfOwnerType (this pass runs after GenericSelfInstantiation for the same reason).
    static JsonNode SelfOwnerType(string owner, int n)
    {
        if (n == 0) return TypeJson.Fqn(owner);
        var args = new JsonArray();
        for (var i = 0; i < n; i++) args.Add(new JsonObject { ["t"] = "tv", ["scope"] = "type", ["i"] = i });
        return new JsonObject { ["t"] = "fqn", ["name"] = owner, ["args"] = args };
    }

    static JsonObject Local(string name) => new() { ["k"] = "local", ["name"] = name };
    static JsonNode Clone(JsonNode n) => n == null ? null : JsonNode.Parse(n.ToJsonString());
    static bool Bool(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
