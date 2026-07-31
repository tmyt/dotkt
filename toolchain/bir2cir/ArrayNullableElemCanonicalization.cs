using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using DotKt.Bir;

// THE ARRAY-CREATION HALF OF `Array<X?>` IS `object[]` (#86 D2).
//
// The declaration axis (NullableGenericErasure) makes every `Array<X?>` SLOT `object[]` when `X` may be a value type —
// an open `Tv` or a value `Fqn`. An array CREATION that fills such a slot states its element type on its own `elem`,
// and kotc writes there whatever the source said: `arrayOf(1, 2, 3)` assigned to an `Array<Int?>` carries the NON-null
// `kotlin.Int`, and `Array<Int?>(n) { null }` does too. Neither is a `Nullable(...)` the erasure sweep could see, so
// the creation would allocate an `int32[]` under an `object[]` slot — a `stelem` of a boxed element into a 4-byte slot,
// which is memory corruption rather than a type error.
//
// So the creation's element FOLLOWS THE SLOT IT FILLS: a `var`/`field` whose declared type is `Array<E?>` with a
// possibly-value `E` makes its initializing creation `object[]`. A reference `E` is untouched — `Array<String?>` is
// `string[]` and its creation already says `kotlin.String`.
//
// Runs in BIR-space BEFORE type lowering (elem tokens are still the `kotlin.*` FQN form) and BEFORE the erasure sweep,
// which then finds slot and creation already agreeing.
static class ArrayNullableElemCanonicalization
{
    public static void Apply(JsonNode root, Func<string, bool> isValue) => Walk(root, isValue);

    static void Walk(JsonNode node, Func<string, bool> isValue)
    {
        switch (node)
        {
            case JsonObject obj:
                Canonicalize(obj, isValue);
                foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value, isValue);
                break;
            case JsonArray arr:
                foreach (var it in arr) if (it != null) Walk(it, isValue);
                break;
        }
    }

    static readonly string[] ArrayCreationKinds = { "newArray", "newArraySized", "newArrayInit" };

    static void Canonicalize(JsonObject obj, Func<string, bool> isValue)
    {
        if ((obj["k"] as JsonValue)?.TryGetValue<string>(out var k) != true || (k != "var" && k != "field")) return;
        // The declared slot must be an `Array<E?>` whose `E` may be a value type — the shape D2 canonicalizes.
        if (TypeJson.Read(obj["type"]) is not TypeNode.Array arrT
            || !NullableGenericErasure.IsNullableMaybeValue(arrT.Elem, isValue)) return;
        if (obj["init"] is not JsonObject init) return;
        if ((init["k"] as JsonValue)?.TryGetValue<string>(out var ik) != true || Array.IndexOf(ArrayCreationKinds, ik) < 0) return;
        init["elem"] = TypeJson.Fqn("object");
    }
}
