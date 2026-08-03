using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// ARRAY MEMBERS AND ENUMERATION ON AN ARGUMENT-ABANDONING RECEIVER — the intrinsic-node half of what
// MemberCallSubstitution's pre-Rule-2 override does for collection member CALLS.
//
// `Array<*>`'s physical view is `System.Array` (StarProjectionView: the base class of every array type, so every
// element instantiation is assignment-compatible with it). The array INTRINSICS cannot survive that: `ldelem` /
// `ldlen` need a real vector type, and the element they would name is unknown by construction. Emitting them
// against the erased receiver is what produced the measured AccessViolation — `object[]` read over an `int32[]`
// is a raw reinterpret of the element storage, not a failed cast, so it corrupts memory instead of throwing.
//
// `System.Array` implements the non-generic `IList`/`ICollection`/`IEnumerable`, so the SAME ClrStarProjection
// statics the collection projection uses serve it exactly: `clrStarSize` reads `ICollection.Count` and
// `clrStarGet` reads `IList.get_Item`, both O(1) on an array and both returning the element boxed to `Any?` —
// which is what an abandoned element type is. A `for (x in a)` keeps its `forArray` shape over a BOXED SNAPSHOT
// (`clrStarToArray`), the one `object[]` an erased array can legitimately produce; iteration is read-only in
// Kotlin, so the snapshot is observationally identical.
//
// Runs in BIR space, before type lowering, so the `sty` stamp still says `array(star)` — the frontend fact that
// identifies the receiver. Non-reference builds only (the reference surface keeps the pure-Kotlin array).
static class StarArrayMemberLowering
{
    const string Helpers = "kotlin.collections.ClrStarProjectionKt";

    public static void Apply(JsonNode root) => Walk(root);

    static void Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                    if (obj[key] is JsonNode child) Walk(child);
                Rewrite(obj);
                break;
            case JsonArray arr:
                foreach (var item in arr) if (item != null) Walk(item);
                break;
        }
    }

    // True when an operand's frontend static-type stamp says `Array<*>`.
    static bool IsStarArray(JsonNode operand) =>
        operand is JsonObject o && TypeJson.Read(o["sty"]) is TypeNode.Array { Elem: TypeNode.Star };

    // `for (x in s)` over a `Sequence<*>` / a projected .NET enumerable takes the GetEnumerator loop, whose
    // enumerator type CIR names as `IEnumerable<elem>` — a construction an abandoning source does not inhabit.
    // `System.Linq.Enumerable.Cast<object>` is the LAZY conversion that does: it reads the non-generic
    // `IEnumerable` every enumerable has and yields each element boxed, which is exactly what an abandoned
    // element type is. A snapshot would be wrong here specifically — a Sequence may be infinite.
    static readonly TypeNode CastResult =
        new TypeNode.Fqn("System.Collections.Generic.IEnumerable", new TypeNode[] { new TypeNode.Fqn("object") });

    static bool AbandonsProjection(JsonNode operand) =>
        operand is JsonObject o && TypeJson.Read(o["sty"]) is TypeNode sty
        && (sty is TypeNode.Array { Elem: TypeNode.Star }
            || (sty is TypeNode.Fqn { Args: { } args } && args.Any(a => a is TypeNode.Star)));

    static void Rewrite(JsonObject obj)
    {
        var kind = (obj["k"] as JsonValue)?.TryGetValue<string>(out var k) == true ? k : null;
        if (kind == "forEachInline" && AbandonsProjection(obj["src"]))
        {
            obj["src"] = new JsonObject
            {
                ["k"] = "clrGenericStatic",
                ["type"] = TypeJson.Fqn("System.Linq.Enumerable"),
                ["method"] = "Cast",
                ["typeArgs"] = new JsonArray { TypeJson.Fqn("object") },
                ["memberSig"] = new JsonArray { TypeJson.Fqn("System.Collections.IEnumerable") },
                ["args"] = new JsonArray { obj["src"].DeepClone() },
                ["sty"] = TypeJson.Write(CastResult),
            };
            obj["elem"] = TypeJson.Fqn("kotlin.Any");
            return;
        }
        if (kind is not ("arrayLen" or "arrayGet" or "forArray")) return;
        if (!IsStarArray(obj["array"])) return;

        if (kind == "forArray")
        {
            // Keep the loop; erase only its SUBJECT. The element slot is already `Any?`, which is what the
            // snapshot's `object[]` yields.
            obj["array"] = Call("clrStarToArray", obj["array"].DeepClone());
            obj["elem"] = TypeJson.Fqn("kotlin.Any");
            return;
        }

        var recv = obj["array"].DeepClone();
        var replacement = kind == "arrayLen"
            ? (JsonObject)Call("clrStarSize", recv)
            : (JsonObject)Call("clrStarGet", recv, obj["index"]?.DeepClone());
        foreach (var stale in obj.Select(kv => kv.Key).ToList()) obj.Remove(stale);
        foreach (var kv in replacement) obj[kv.Key] = kv.Value?.DeepClone();
    }

    static JsonNode Call(string method, params JsonNode[] args)
    {
        var a = new JsonArray();
        foreach (var x in args) if (x != null) a.Add(x);
        return new JsonObject
        {
            ["k"] = "callStatic",
            ["owner"] = TypeJson.Fqn(Helpers),
            ["method"] = method,
            ["args"] = a,
        };
    }
}
