using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// ARRAY CONSTRUCTION + INTRINSIC ELEMENT DERIVATION (#73 Phase 2b-A). kotc emits the FAITHFUL array identity and
// nothing more — the array type token is the bare `kotlin.IntArray` FQN, the sized ctor is a plain
// `new kotlin.IntArray(size, init)` call, and the array intrinsics (arrayGet/arraySet/forArray) carry NO `elem`.
// Deciding "IntArray IS an array of Int" and materializing the sized array is the Kotlin<->CLR REPRESENTATION
// decision, so it lives here:
//
//   new kotlin.<Prim>Array(size, init) -> {k:newArrayInit, elem, size, init}   (newarr <elem> + fill loop)
//   new kotlin.<Prim>Array(size)       -> {k:newArraySized, elem, size}        (zero-filled)
//   arrayGet/arraySet/forArray         -> stamp `elem` = the array operand's element type
//
// The element is derived off StaticType (the array operand's recovered static type -> its element), so it also
// covers a reference `Array<E>` operand (Array(E) -> E) and an unsigned specialized array (Array(UByte) -> UByte).
// Runs EARLY (right after PrimitiveOperatorLowering) so every downstream consumer of `elem` — StaticType's
// arrayGet case, FaithfulHintRecognition, SuspendColdLowering's array-read spill, ilemit's ldelem opcode pick —
// sees the stamped element, and BEFORE BirTypeLowering (the `elem`/type tokens are still pure kotlin.* here, and
// the `new`-node type would otherwise decompose to a nonsensical `Array` under a `new`).
static class ArrayConstructionLowering
{
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs = null)
    {
        StaticType.Refs = refs;
        StaticType.LocalTypes = StaticType.CollectTypes(root);
        switch (root)
        {
            case JsonObject o: WalkObject(o, BirScope.Empty); break;
            case JsonArray a: WalkArray(a, BirScope.Empty); break;
        }
    }

    static void WalkArray(JsonArray arr, BirScope scope)
    {
        var cur = scope;
        for (var i = 0; i < arr.Count; i++)
        {
            switch (arr[i])
            {
                case JsonObject co: WalkObject(co, cur); if (Lower(co, cur) is JsonNode r) arr[i] = r; break;
                case JsonArray ca: WalkArray(ca, cur); break;
            }
            if (arr[i] is JsonObject vo && Str(vo["k"]) == "var")
            {
                if (ReferenceEquals(cur, scope)) cur = scope.Child();
                cur.Declare(vo);
            }
        }
    }

    static void WalkObject(JsonObject obj, BirScope scope)
    {
        var child = scope.Extend(obj);
        // A `forArray` binds its loop variable (typed = the array's element) for its BODY's scope, so a nested
        // `row[j]` over a loop var (`for (row in matrix)`) still recovers the element. The elem is stamped here.
        if (Str(obj["k"]) == "forArray")
        {
            var elem = DeriveArrayElem(obj["array"], child);
            if (obj["array"] is JsonObject ao) { WalkObject(ao, child); if (Lower(ao, child) is JsonNode ar) obj["array"] = ar; }
            var bodyScope = child;
            if (elem != null && Str(obj["var"]) is string lv) { bodyScope = child.Child(); bodyScope.VarTypes[lv] = elem; }
            if (obj["body"] is JsonArray body) WalkArray(body, bodyScope);
            if (elem != null) StampForArray(obj, elem);
            return;
        }
        foreach (var key in obj.Select(kv => kv.Key).ToList())
            switch (obj[key])
            {
                case JsonObject co: WalkObject(co, child); if (Lower(co, child) is JsonNode r) obj[key] = r; break;
                case JsonArray ca: WalkArray(ca, child); break;
            }
    }

    // A `new kotlin.<Prim>Array(...)` -> newArrayInit/newArraySized; an arrayGet/arraySet -> the same node with the
    // derived `elem` stamped in canonical position. Returns the replacement node, or null to keep `o` as-is.
    static JsonNode Lower(JsonObject o, BirScope scope)
    {
        var k = Str(o["k"]);
        if (k == "new" && TypeJson.Read(o["type"]) is TypeNode.Fqn tf && tf.Args == null
            && BirTypeLowering.PrimArrayElem.TryGetValue(tf.Name, out var elemFq))
        {
            var elem = new TypeNode.Fqn(elemFq);
            var args = o["args"] as JsonArray ?? new JsonArray();
            if (args.Count == 2)
                return new JsonObject { ["k"] = "newArrayInit", ["elem"] = TypeNode.Write(elem), ["size"] = args[0]?.DeepClone(), ["init"] = args[1]?.DeepClone() };
            if (args.Count == 1)
            {
                // #76 UNSIGNED-ARRAY WRAP-CTOR: the @PublishedApi `constructor(storage: SignedArray)` takes the SIGNED
                // backing array (kotlin.ByteArray/...), NOT an Int size. Distinguish it from the sized `constructor(size:
                // Int)` by the DECLARED ctor param type — an array-typed arg is the wrap-ctor, which is a same-underlying
                // reinterpret (handled by MemberCallSubstitution.TransformNew, non-ref only), not a `newArraySized`.
                // Defer (return null: keep the `new` node) so it never becomes a nonsensical sized array of an array.
                if (o["argTypes"] is JsonArray ats && ats.Count == 1 && TypeJson.Read(ats[0]) is TypeNode at && IsArrayTypeNode(at))
                    return null;
                return new JsonObject { ["k"] = "newArraySized", ["elem"] = TypeNode.Write(elem), ["size"] = args[0]?.DeepClone() };
            }
            return null;
        }
        if ((k == "arrayGet" || k == "arraySet") && o["elem"] == null && DeriveArrayElem(o["array"], scope) is TypeNode ge)
        {
            var n = new JsonObject { ["k"] = k, ["elem"] = TypeNode.Write(ge), ["array"] = o["array"]?.DeepClone(), ["index"] = o["index"]?.DeepClone() };
            if (k == "arraySet") n["value"] = o["value"]?.DeepClone();
            return n;
        }
        return null;
    }

    // Rebuild a forArray with `elem` in canonical position (k, label, var, elem, array, body).
    static void StampForArray(JsonObject o, TypeNode elem)
    {
        if (o["elem"] != null) return;
        var label = o["label"]?.DeepClone(); var v = o["var"]?.DeepClone();
        var array = o["array"]?.DeepClone(); var body = o["body"]?.DeepClone();
        foreach (var key in ((IDictionary<string, JsonNode>)o).Keys.ToList()) o.Remove(key);
        o["k"] = "forArray"; o["label"] = label; o["var"] = v;
        o["elem"] = TypeNode.Write(elem); o["array"] = array; o["body"] = body;
    }

    // The element type of the array VALUE `arrayNode`: recover its static type via StaticType and read the element.
    static TypeNode DeriveArrayElem(JsonNode arrayNode, BirScope scope) =>
        arrayNode is null ? null : ArrayElementOf(StaticType.Surface(arrayNode, scope));

    static TypeNode ArrayElementOf(TypeNode t) => t switch
    {
        TypeNode.Array a => a.Elem,
        TypeNode.Nullable n => ArrayElementOf(n.Of),
        // A flexible/platform array `int[]!` (a facadegen-injected NRT-oblivious .NET array return, #8) — peel the
        // oblivious wrapper exactly like Nullable, else the arrayGet/arraySet/forArray `elem` stamp is dropped and
        // ilemit KeyNotFounds on the missing `elem`.
        TypeNode.Oblivious o => ArrayElementOf(o.Of),
        // A signed primitive array identity (`kotlin.IntArray`) -> its element; a reference `kotlin.Array<E>` -> E.
        TypeNode.Fqn f when BirTypeLowering.PrimArrayElem.TryGetValue(f.Name, out var e) => new TypeNode.Fqn(e),
        TypeNode.Fqn { Name: "kotlin.Array", Args: { Length: 1 } fa } => fa[0],
        _ => null,
    };

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;

    // Whether a declared type token names an array: a structured `Array`, a specialized primitive array identity
    // (`kotlin.ByteArray` — a signed backing array of an unsigned value class, or any signed specialized array), or a
    // reference `kotlin.Array<E>`. Used to distinguish an unsigned array's wrap-ctor(storage: SignedArray) from its
    // sized ctor(size: Int).
    static bool IsArrayTypeNode(TypeNode t) => t switch
    {
        TypeNode.Array => true,
        TypeNode.Fqn f when f.Args == null && BirTypeLowering.PrimArrayElem.ContainsKey(f.Name) => true,
        TypeNode.Fqn { Name: "kotlin.Array" } => true,
        _ => false,
    };
}
