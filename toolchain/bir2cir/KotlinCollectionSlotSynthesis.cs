using System;
using System.Collections.Generic;
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
// Give every emitted class whose Kotlin supertypes reach such a member a real CLR interface slot for it. The class
// gains `DotKt.Runtime.CompilerServices.KotlinMutableCollectionSlots` (plus `KotlinMutableListSlots` for the indexed
// `addAll`) and one private `dotkt$slot$…` bridge per member, wired by an exact `clrInterfaceImpls` MethodImpl
// descriptor. Each bridge casts the erased `Any` parameter back to the overridden member's own declared collection
// type and forwards VIRTUALLY, so a further-derived override still wins and a subclass needs no bridge of its own.
//
// THE MEMBER IS IDENTIFIED BY THE FRONTEND'S OVERRIDE FACTS, NEVER BY NAME AND ARITY. A declaration qualifies only
// when its `overrides` chain names `kotlin.collections.MutableCollection` / `MutableList` with the slot member and
// arity. The frontend flattens that chain, so an INDIRECT implementer (`class C : I<E>` for a user
// `interface I<E> : MutableCollection<E>`) carries the same entry and needs no interface-closure walk of our own.
// Name+arity keying would have been wrong four ways: an unrelated same-name overload (`fun addAll(other: Bag<E>)`)
// produced a second bridge with a colliding physical signature, a wrong-shaped same-name member
// (`fun removeAll(n: Int)`) got an InterfaceImpl row with no matching MethodImpl (TypeLoadException), a member
// extension function's lowered receiver could satisfy the arity test, and an `@ClrName`-renamed override was missed
// entirely — silently falling back to the BCL default.
//
// The bridge's parameter typing likewise comes from the OVERRIDDEN declaration, substituted into the implementer's
// frame — never from guessing which parameter "looks like" the collection.
//
// Runs in the Kotlin-vocabulary phase, right after CovariantInterfaceReturnBridge: that is where `overrides` still
// exists (it does not survive to CIR) and where the supertype graph is still Kotlin's own. Non-ref builds only —
// the ref surface stays pure Kotlin and every consumer re-derives the physical form from it.
static class KotlinCollectionSlotSynthesis
{
    const string MutableCollection = "kotlin.collections.MutableCollection";
    const string MutableList = "kotlin.collections.MutableList";
    const string CollectionSlots = "DotKt.Runtime.CompilerServices.KotlinMutableCollectionSlots";
    const string ListSlots = "DotKt.Runtime.CompilerServices.KotlinMutableListSlots";

    /// <summary>One Kotlin member with no BCL slot, and the compiler-owned interface slot that carries it.</summary>
    sealed class Slot
    {
        public string DeclaringInterface;   // the Kotlin interface that declares the member (override identity)
        public string Member;               // its Kotlin name
        public int Arity;                   // its parameter count
        public string SlotInterface;        // the compiler-owned interface carrying the physical slot
        public string SlotMember;           // the slot's member name
        public string Bridge;               // the synthesized forwarding method's name
    }

    static readonly Slot[] Slots =
    {
        new() { DeclaringInterface = MutableCollection, Member = "removeAll", Arity = 1,
                SlotInterface = CollectionSlots, SlotMember = "dotktRemoveAll", Bridge = "dotkt$slot$removeAll" },
        new() { DeclaringInterface = MutableCollection, Member = "retainAll", Arity = 1,
                SlotInterface = CollectionSlots, SlotMember = "dotktRetainAll", Bridge = "dotkt$slot$retainAll" },
        new() { DeclaringInterface = MutableCollection, Member = "addAll", Arity = 1,
                SlotInterface = CollectionSlots, SlotMember = "dotktAddAll", Bridge = "dotkt$slot$addAll" },
        new() { DeclaringInterface = MutableList, Member = "addAll", Arity = 2,
                SlotInterface = ListSlots, SlotMember = "dotktAddAllAt", Bridge = "dotkt$slot$addAllAt" },
    };

    sealed class Def
    {
        public string Name;
        public string Kind;
        public JsonObject Node;
        public JsonArray Methods;
        public TypeNode.Fqn[] Interfaces = Array.Empty<TypeNode.Fqn>();
        public TypeNode.Fqn Base;
        public int Arity;
    }

    public static void ApplyAll(IEnumerable<JsonNode> roots)
    {
        var defs = new Dictionary<string, Def>(StringComparer.Ordinal);
        foreach (var root in roots) Collect(root, defs);
        foreach (var def in defs.Values.Where(d => d.Kind == "class").ToList()) ApplyClass(def, defs);
    }

    static void Collect(JsonNode node, Dictionary<string, Def> defs)
    {
        if (node is not JsonObject obj || obj["types"] is not JsonArray types) return;
        foreach (var type in types.OfType<JsonObject>())
        {
            if (Str(type["name"]) is string name)
                defs[name] = new Def
                {
                    Name = name,
                    Kind = Str(type["kind"]),
                    Node = type,
                    Methods = type["methods"] as JsonArray,
                    Interfaces = (type["interfaces"] as JsonArray)?.Select(TypeJson.Read)
                        .OfType<TypeNode.Fqn>().ToArray() ?? Array.Empty<TypeNode.Fqn>(),
                    Base = TypeJson.Read(type["base"]) as TypeNode.Fqn,
                    Arity = TypeParameterFrame.Count(type),
                };
            Collect(type, defs);
        }
    }

    static void ApplyClass(Def cls, IReadOnlyDictionary<string, Def> defs)
    {
        if (cls.Methods == null) return;
        // A class whose BASE CHAIN already carries a slot interface needs nothing of its own: that base's bridge
        // forwards VIRTUALLY and therefore already reaches this class's override. Re-implementing the interface here
        // would rebuild the interface map and silently drop the slots this class does not itself declare.
        var inherited = InheritedSlotInterfaces(cls, defs);

        var newBridges = new List<JsonObject>();
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in Slots)
        {
            if (inherited.Contains(slot.SlotInterface)) continue;
            // Does this class PARTICIPATE in the slot? Exactly when a declaration reachable from it overrides the
            // Kotlin member: its own first, else a concrete declaration in its local supertype closure (a base-class
            // body, or an interface DEFAULT method for which the frontend emits nothing on the implementing class).
            if (!TryResolveImplementation(cls, slot, defs, out var target)) continue;
            if (cls.Methods.OfType<JsonObject>().Any(m => Str(m["name"]) == slot.Bridge)) continue;
            newBridges.Add(Bridge(cls, slot, target));
            wanted.Add(slot.SlotInterface);
        }
        if (newBridges.Count == 0) return;

        if (cls.Node["interfaces"] is not JsonArray ifaces)
        {
            ifaces = new JsonArray();
            cls.Node["interfaces"] = ifaces;
        }
        foreach (var iface in wanted)
            if (!ifaces.Any(i => TypeJson.Read(i) is TypeNode.Fqn f && f.Name == iface))
                ifaces.Add(TypeJson.Fqn(iface));
        foreach (var bridge in newBridges) cls.Methods.Add(bridge);
    }

    /// <summary>The slot interfaces a LOCAL base class already provides, directly or by declaration.</summary>
    ///
    /// An EXTERNAL base needs no query: if it carried the implementation it was compiled by this same pass and
    /// already has the slot interface, and its bridge forwards virtually — while this class, declaring nothing
    /// itself, resolves no local target below and is skipped anyway. Both roads lead to "do nothing here".
    static HashSet<string> InheritedSlotInterfaces(Def cls, IReadOnlyDictionary<string, Def> defs)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { cls.Name };
        for (var baseSpec = cls.Base; baseSpec != null && seen.Add(baseSpec.Name);)
        {
            if (!defs.TryGetValue(baseSpec.Name, out var local)) break;
            foreach (var i in local.Interfaces)
                if (i.Name == CollectionSlots || i.Name == ListSlots) found.Add(i.Name);
            // A local base not yet visited still ANSWERS for its own declarations: the decision below is
            // declaration-driven, so a base that WILL receive the interface is detected by the same predicate
            // rather than by visit order.
            foreach (var slot in Slots)
                if (DeclaresConcretely(local, slot) != null) found.Add(slot.SlotInterface);
            baseSpec = local.Base;
        }
        return found;
    }

    /// <summary>Where the slot's Kotlin member is really implemented, expressed in this class's own frame.</summary>
    static bool TryResolveImplementation(Def cls, Slot slot, IReadOnlyDictionary<string, Def> defs,
        out (TypeNode.Fqn Owner, JsonObject Method) target)
    {
        target = default;
        // 1. This class's own declaration wins, and it is the common case.
        if (DeclaresConcretely(cls, slot) is JsonObject own)
        {
            target = (SelfOwner(cls), own);
            return true;
        }
        // 2. Otherwise the implementation is inherited. Walk the LOCAL supertype closure — base classes and
        //    interfaces alike — for a concrete declaration overriding the same member: a base-class body, or an
        //    interface DEFAULT method (`interface I<E> : MutableCollection<E> { override fun removeAll(…) = … }`).
        //    An EXTERNAL supertype is never searched: one carrying the implementation also carries the slot
        //    interface (the same pass compiled it), so InheritedSlotInterfaces has already skipped us.
        foreach (var (spec, def) in LocalSupertypeClosure(cls, defs))
        {
            if (DeclaresConcretely(def, slot) is not JsonObject inheritedMethod) continue;
            target = (spec, inheritedMethod);
            return true;
        }
        return false;
    }

    /// <summary>A CONCRETE declaration of the slot's Kotlin member, identified by the frontend override chain.</summary>
    static JsonObject DeclaresConcretely(Def def, Slot slot)
    {
        if (def.Methods == null) return null;
        var matches = def.Methods.OfType<JsonObject>()
            .Where(m => !Bool(m["static"]) && !Bool(m["abstract"]) && Overrides(m, slot))
            .ToList();
        if (matches.Count > 1)
            throw new InvalidOperationException(
                $"bir2cir: '{def.Name}' has {matches.Count} concrete declarations overriding "
                + $"'{slot.DeclaringInterface}.{slot.Member}' — the Kotlin slot to implement is ambiguous. That is a "
                + "frontend-fact defect, not something to resolve by picking one.");
        return matches.Count == 1 ? matches[0] : null;
    }

    static bool Overrides(JsonObject method, Slot slot) =>
        method["overrides"] is JsonArray overrides && overrides.OfType<JsonObject>().Any(o =>
            TypeJson.Read(o["owner"]) is TypeNode.Fqn f && f.Name == slot.DeclaringInterface
            && Str(o["member"]) == slot.Member
            && Str(o["kind"]) == "method"
            && (o["arity"] as JsonValue) is JsonValue a && a.TryGetValue<int>(out var arity) && arity == slot.Arity);

    /// <summary>Base classes then interfaces, transitively, each constructed in the starting class's own frame.</summary>
    static IEnumerable<(TypeNode.Fqn Spec, Def Def)> LocalSupertypeClosure(Def cls,
        IReadOnlyDictionary<string, Def> defs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { cls.Name };
        var queue = new Queue<TypeNode.Fqn>();
        void Enqueue(Def from, TypeNode[] args)
        {
            if (from.Base != null) queue.Enqueue((TypeNode.Fqn)Subst(from.Base, args));
            foreach (var i in from.Interfaces) queue.Enqueue((TypeNode.Fqn)Subst(i, args));
        }
        Enqueue(cls, OwnArgs(cls));
        while (queue.Count > 0)
        {
            var spec = queue.Dequeue();
            if (!seen.Add(spec.Name) || !defs.TryGetValue(spec.Name, out var def)) continue;
            yield return (spec, def);
            var args = spec.Args ?? Array.Empty<TypeNode>();
            if (args.Length == def.Arity) Enqueue(def, args);
        }
    }

    /// <summary>
    /// `private bool dotkt$slot$&lt;member&gt;(object p0[, int index]) { return this.&lt;member&gt;((&lt;declared&gt;)p0, …) }`.
    ///
    /// The erased `Any` parameter is what makes the capability test instantiation-independent; the cast re-establishes
    /// the OVERRIDDEN member's own declared type at this class's instantiation, so a genuinely mismatched argument
    /// fails LOUD (InvalidCastException) rather than silently taking the BCL default. The forward is VIRTUAL: the
    /// bridge is inherited by subclasses and must reach the most-derived override.
    /// </summary>
    static JsonObject Bridge(Def cls, Slot slot, (TypeNode.Fqn Owner, JsonObject Method) target)
    {
        var declaredParams = (target.Method["params"] as JsonArray)?.OfType<JsonObject>()
            .Select(p => TypeJson.Read(p["type"])).ToArray() ?? Array.Empty<TypeNode>();
        if (declaredParams.Length != slot.Arity || declaredParams.Any(p => p == null))
            throw new InvalidOperationException(
                $"bir2cir: the declaration implementing '{slot.DeclaringInterface}.{slot.Member}' on '{cls.Name}' has "
                + $"{declaredParams.Length} readable parameter(s), not the {slot.Arity} the Kotlin slot declares.");
        var ownerArgs = target.Owner.Args ?? Array.Empty<TypeNode>();

        var bridgeParams = new JsonArray();
        var slotParams = new JsonArray();
        var callSig = new JsonArray();
        var callArgs = new JsonArray();
        for (var i = 0; i < declaredParams.Length; i++)
        {
            var declared = Subst(declaredParams[i], ownerArgs);
            // The ERASED positions are the ones the SLOT INTERFACE declares as `Any`; `MutableList.addAll(index,
            // elements)`'s leading `Int` index stays verbatim. Both facts come from the Kotlin slot declaration, not
            // from inspecting what a parameter type happens to look like.
            var erase = !(slot.Arity == 2 && i == 0);
            var name = "p" + i;
            var slotParam = erase ? TypeJson.Fqn("kotlin.Any") : TypeJson.Write(declared);
            bridgeParams.Add(new JsonObject { ["name"] = name, ["type"] = Clone(slotParam) });
            slotParams.Add(Clone(slotParam));
            callSig.Add(TypeJson.Write(declared));
            callArgs.Add(erase
                ? new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Write(declared), ["e"] = Local(name) }
                : Local(name));
        }

        var ret = Subst(TypeJson.Read(target.Method["ret"]) ?? new TypeNode.Fqn("kotlin.Boolean"), ownerArgs);
        return new JsonObject
        {
            ["name"] = slot.Bridge,
            ["static"] = false,
            ["override"] = false,
            ["virtual"] = true,
            ["abstract"] = false,
            ["objectOverride"] = false,
            ["vis"] = "private",
            ["generated"] = true,
            ["params"] = bridgeParams,
            ["ret"] = TypeJson.Write(ret),
            ["body"] = new JsonArray(new JsonObject
            {
                ["k"] = "return",
                ["value"] = new JsonObject
                {
                    ["k"] = "callInstance",
                    ["ownerType"] = TypeJson.Write(target.Owner),
                    ["virtual"] = true,
                    // bir2cir authored this call with its exact declaration owner; no later pass may reinterpret it
                    // as an ordinary Kotlin receiver call and bind it back to the slot this bridge implements.
                    ["clrOwnerResolved"] = true,
                    ["recv"] = new JsonObject { ["k"] = "this" },
                    ["method"] = Str(target.Method["name"]),
                    ["sig"] = callSig,
                    ["ret"] = TypeJson.Write(ret),
                    ["args"] = callArgs,
                },
            }),
            ["attrs"] = new JsonArray(),
            [KotlinPropertyAccessors.PhysicalSlotBridgeKey] = true,
            ["clrInterfaceImpls"] = new JsonArray(new JsonObject
            {
                ["owner"] = TypeJson.Fqn(slot.SlotInterface),
                ["member"] = slot.SlotMember,
                ["arity"] = 0,
                ["params"] = slotParams,
                ["ret"] = TypeJson.Write(ret),
            }),
        };
    }

    static TypeNode.Fqn SelfOwner(Def cls)
    {
        var args = OwnArgs(cls);
        return new TypeNode.Fqn(cls.Name, args.Length == 0 ? null : args);
    }

    // The COMPLETE CLR generic frame (captured enclosing prefix + own declarations), never `typeParams` alone: an
    // inner class or a lifted local/object implementer would otherwise get a partially-open self owner.
    static TypeNode[] OwnArgs(Def cls) =>
        Enumerable.Range(0, cls.Arity).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray();

    static TypeNode Subst(TypeNode type, TypeNode[] args) => args.Length == 0 ? type : type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < args.Length => args[tv.I],
        TypeNode.Fqn f when f.Args is not null => new TypeNode.Fqn(f.Name, f.Args.Select(a => Subst(a, args)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(Subst(n.Of, args)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(Subst(o.Of, args)),
        TypeNode.Array a => new TypeNode.Array(Subst(a.Elem, args)),
        TypeNode.ByRef b => new TypeNode.ByRef(Subst(b.Of, args)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, Subst(fn.Ret, args),
            fn.Params.Select(p => Subst(p, args)).ToArray(),
            fn.Recv == null ? null : Subst(fn.Recv, args)),
        _ => type,
    };

    static JsonObject Local(string name) => new() { ["k"] = "local", ["name"] = name };
    static JsonNode Clone(JsonNode n) => n == null ? null : JsonNode.Parse(n.ToJsonString());
    static bool Bool(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
