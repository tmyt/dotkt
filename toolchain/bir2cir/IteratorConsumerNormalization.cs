using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// GAP A — the for-loop iterator protocol over a referenced (rt-dll) collection. kotc desugars `for (x in xs)` to a
// `<iterator>` var initialized by the stdlib bridge `kotlin.collections.ClrIteratorBridgeKt.iteratorOverEnumerable`
// (which RETURNS the real generic `kotlin.collections.Iterator<E>`), then routes hasNext/next to that same real
// generic `kotlin.collections.Iterator<E>` (the rt dll defines `Iterator`1`). In an APP build that owner (and
// the `@kotlin.collections.Iterator` var type) KeyNotFounds in ilemit's `_types` (they're referenced, not emitted).
// Re-point BOTH at the real referenced generic `clrg:kotlin.collections.Iterator[E]` so ilemit resolves hasNext/next
// by reflection against the runtime stdlib — symmetric to how the List local already lowers to IReadOnlyList. The
// element type comes from the bridge call's typeArgs (still in the source vocabulary; the later type-lowering pass
// lowers the inner). Scoped per method (the `<iterator>` name is per-loop synthetic); the stdlib self-build is gated
// OFF at the call site (it emits Iterator itself). Producer-side (`class C : Iterator<T>`) is a separate, deeper gap
// and is intentionally not touched here.
static class IteratorConsumerNormalization
{
    const string Bridge = "kotlin.collections.ClrIteratorBridgeKt";

    public static void Apply(JsonNode root) => Process(root);

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    // The referenced-generic Iterator<elem> type node (the canonical CLR consumer target).
    static JsonNode IterType(TypeNode elem) => TypeJson.Write(new TypeNode.Fqn("kotlin.collections.Iterator", new[] { elem }));

    // A single document-order walk. A `var <name>` initialized by the bridge (or a kotlin.*-owner iterator call) is
    // retyped to the referenced generic `Iterator<elem>` in place, and each hasNext/next dispatch reads its element
    // straight off the (real) iterator owner's own type arg — the two are independent, so any traversal order works.
    static void Process(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var k = Str(obj["k"]);
            if (k == "var" && Str(obj["name"]) is string && obj["init"] is JsonObject init &&
                Str(init["k"]) == "callStatic" && TypeJson.OwnerName(init["owner"]) == Bridge &&
                Str(init["method"]) == "iteratorOverEnumerable" &&
                init["typeArgs"] is JsonArray ta && ta.Count == 1 && TypeJson.Read(ta[0]) is TypeNode elem)
            {
                obj["type"] = IterType(elem);
            }
            // An `Iterator[elem]`-typed var initialized by a call INTO THE RT STDLIB (a `kotlin.*` owner — an unaliased
            // kotlin.collections interface like Set.iterator(), or an attributed top-level like MapsKt.iterator(map)
            // — or the ALREADY-SUBSTITUTED rule-3 helper `dotkt$ClrH_kotlin_*`: MemberCallSubstitution runs BEFORE
            // this pass, so a concrete alias receiver's `ArrayList<Int>().iterator()` arrives as a callStatic on the
            // rt helper owner, and its ArrayListIterator likewise implements the REAL Iterator, not the synthetic):
            // the runtime iterator is an rt-dll type implementing the REAL kotlin.collections.Iterator — so its
            // hasNext/next consumers must be re-pointed exactly like the bridge case above. A USER-owned init
            // (Countdown.iterator() returning an app-emitted `object : Iterator<Int>`) is deliberately NOT registered —
            // app-internal producer/consumer stay consistent on the app-emitted iterator's own type.
            else if (k == "var" && Str(obj["name"]) is string && obj["init"] is JsonObject init2 &&
                IteratorVarElem(TypeJson.Read(obj["type"])) is (string head, TypeNode elem2) &&
                (TypeJson.OwnerName(init2["owner"]) ?? TypeJson.OwnerName(init2["ownerType"]) ?? "") is string initOwner &&
                (initOwner.StartsWith("kotlin.", StringComparison.Ordinal)
                    || initOwner.StartsWith("dotkt$ClrH_kotlin_", StringComparison.Ordinal)))
            {
                // MUTABLE-MAP for-in REROUTE (bundle-6 BUG-2): `for ((k,v) in mm)` desugars to
                // `MutableMap.iterator(): MutableIterator<MutableEntry>`, which lowers to the SAME signature
                // `MapsKt.iterator(IDictionary<K,V>)` as the immutable `Map.iterator(): Iterator<Map.Entry>` — a genuine
                // COLLISION. ilemit binds the app's `iterator` call by name to the IMMUTABLE overload (the mutable one
                // is emitted as `iterator$dup2`), whose runtime iterator is `Iterator<Map.Entry>` — so hasNext/next
                // (typed MutableEntry from kotc) dispatch on a generic instantiation the object doesn't implement ->
                // EntryPointNotFound. Sidestep the collision: reroute the init to the SAME entries-based iterator that
                // `for (e in mm.entries)` already uses successfully — `iteratorOverEnumerable(clrMapMutableEntries(mm))`
                // — which yields a genuine `Iterator<MutableEntry>` (KotlinIteratorOverEnumerator over the live
                // ClrMutableMapEntry snapshot). Everything then stays consistently typed on MutableEntry (ilverify-clean),
                // and the read Iterator matches the wrapper's implemented interface. Only the MUTABLE entry element is
                // rerouted; the immutable `Map.iterator()` path already works and is left untouched.
                if (elem2 is TypeNode.Fqn { Args: { } } mutEntry
                    && mutEntry.Name is "kotlin.collections.MutableMap.MutableEntry" or "kotlin.collections.MutableMap$MutableEntry"
                    && TypeJson.OwnerName(init2["owner"]) == "kotlin.collections.MapsKt" && Str(init2["method"]) == "iterator"
                    && init2["args"] is JsonArray iargs && iargs.Count == 1 && iargs[0] is JsonNode recv0)
                {
                    var (ek, ev) = EntryKvArgs(mutEntry);
                    obj["init"] = new JsonObject
                    {
                        ["k"] = "callStatic",
                        ["owner"] = TypeJson.Fqn(Bridge),
                        ["method"] = "iteratorOverEnumerable",
                        ["args"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["k"] = "callStatic",
                                ["owner"] = TypeJson.Fqn("kotlin.collections.ClrMapDefaultsKt"),
                                ["method"] = "clrMapMutableEntries",
                                ["sig"] = new JsonArray { TypeJson.Fqn("kotlin.Any") },
                                ["args"] = new JsonArray { recv0.DeepClone() },
                                ["typeArgs"] = new JsonArray { TypeJson.Write(ek), TypeJson.Write(ev) },
                            },
                        },
                        ["sig"] = new JsonArray { TypeJson.Write(new TypeNode.Fqn("kotlin.collections.ClrEnumerable",
                            new TypeNode[] { new TypeNode.Tv("method", 0) })) },
                        ["typeArgs"] = new JsonArray { TypeJson.Write(elem2) },
                    };
                    obj["type"] = IterType(elem2);
                }
                else
                {
                    obj["type"] = TypeJson.Write(new TypeNode.Fqn(head, new[] { elem2 }));
                }
            }
            // A hasNext/next `callInstance` on a Kotlin-iterator owner -> a `clrInstance` on the REAL referenced generic
            // `kotlin.collections.Iterator<elem>`, where BOTH members are DECLARED. This is required for the real
            // `kotlin.collections.MutableIterator<elem>` — hasNext/next are INHERITED from Iterator, so a
            // callInstance on MutableIterator resolves nowhere (reflection does not walk interface bases) ->
            // EntryPointNotFound. Every `for (x in aMutableList)` and `class C : MutableIterable` hits this.
            // callInstance routes through ResolveMethod/ParseOwner (an EMITTED-type `_types` lookup that KeyNotFounds on
            // a referenced generic); the CLR-bound member path is `clrInstance` (EmitClrCall), exactly how the substituted
            // IReadOnlyList's get_Item/get_Count resolve. next() returns the element, hasNext() Boolean; argTypes empty.
            // The element comes from the owner's own type arg.
            // `type`/`ret` stay in the source vocabulary — the later type-lowering pass lowers them.
            else if (k == "callInstance" && (Str(obj["method"]) is "hasNext" or "next")
                && IteratorDispatchElem(TypeJson.Read(obj["ownerType"])) is TypeNode e)
            {
                var method = Str(obj["method"]);
                obj["k"] = "clrInstance";
                obj.Remove("ownerType");
                obj.Remove("virtual");
                obj["type"] = IterType(e);
                obj["method"] = method;
                obj["argTypes"] = new JsonArray();
                obj["ret"] = method == "next" ? TypeJson.Write(e) : TypeJson.Fqn("kotlin.Boolean");
                // Spec §2.7 — this rewrote the node's result. `IteratorDispatchElem` answers `object` for an element
                // token it cannot parse or that is already erased, so a `for ((k, v) in map)` over a `Map<String,Int>`
                // can land a `Map$Entry<object,object>` result under a stamp still naming `Map$Entry<String,Int>`;
                // the erased owner is what the value actually is, so the stamp goes where it contradicts that.
                NodeType.DropStampIfStale(obj);
            }
            foreach (var kv in obj) if (kv.Value != null) Process(kv.Value);
        }
        else if (node is JsonArray arr)
            foreach (var it in arr) if (it != null) Process(it);
    }

    // The (K, V) type args of a Map.Entry / MutableEntry element token (`@kotlin.collections.MutableMap$MutableEntry[
    // string,int]` -> ("string","int")); ("object","object") when erased/unparseable. Used to instantiate the
    // clrMapMutableEntries<K,V> reroute target.
    static readonly TypeNode ObjT = new TypeNode.Fqn("object");
    static (TypeNode, TypeNode) EntryKvArgs(TypeNode.Fqn elem)
    {
        var a = elem.Args;
        return (a is { Length: >= 1 } && a[0] != null ? a[0] : ObjT,
                a is { Length: >= 2 } && a[1] != null ? a[1] : ObjT);
    }

    // `kotlin.collections.Iterator<elem>` / `kotlin.collections.MutableIterator<elem>` -> (head name, elem); null
    // otherwise. The elem may itself be a constructed type (`kotlin.collections.Map$Entry<K,V>`).
    static (string, TypeNode)? IteratorVarElem(TypeNode vt)
    {
        if (vt is TypeNode.Fqn { Args: { Length: 1 } args } f
            && f.Name is "kotlin.collections.Iterator" or "kotlin.collections.MutableIterator")
            return (f.Name, args[0]);
        return null;
    }

    // The element type for a hasNext/next dispatch whose owner should be normalized to `kotlin.collections.Iterator<E>`:
    // a real `kotlin.collections.(Mutable)Iterator<E>` owner yields E from its own type arg. Null = do not rewrite.
    static TypeNode IteratorDispatchElem(TypeNode owner)
    {
        if (owner is TypeNode.Fqn { Args: { Length: 1 } a } f
            && f.Name is "kotlin.collections.Iterator" or "kotlin.collections.MutableIterator")
            return a[0];
        return null;
    }
}
