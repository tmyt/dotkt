using System;
using System.Text.Json.Nodes;
using DotKt.Bir;

// ARRAY-ELEMENT NULLABILITY realignment (C2 boxed-primitive dual-representation).
//
// kotc emits `arrayOfNulls<Int>(3)` (and `Array<Int?>(n){...}` / an `Array<Int?>` literal) with the array-creation node's
// `elem` set to the NON-null element token (`kotlin.Int`), even though the declaring slot is `Array<Int?>` =
// `array:nullable:int` = `Nullable<int>[]`. ilemit then `newarr int` (an `int[]`) while element stores emit
// `stelem Nullable<int>` (8-byte struct into a 4-byte slot) -> memory corruption / SIGSEGV. Realign the creation's
// `elem` to carry the declared array's `nullable:` element so a genuine `Nullable<int>[]` is allocated and the element
// stelem/ldelem agree.
//
// Scope: a `var`/`field` whose declared `type` is `array:nullable:<E>` and whose `init` is a `newArray` /
// `newArraySized` / `newArrayInit` whose `elem` lacks a `nullable:` (and isn't itself an `array:` — a nested array).
// Prepending `nullable:` to the creation's own element token is safe: the slot and its initializer share the element
// type, so an `arrayOf(1,2,3)` assigned to an `Array<Int?>` correctly becomes a `Nullable<int>[]` too (each element
// wraps at stelem). Runs in BIR-space BEFORE type lowering (elem tokens are still the `kotlin.*` FQN form).
static class ArrayNullableElemRealign
{
    public static void Apply(JsonNode root) => Walk(root);

    static void Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                Realign(obj);
                foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value);
                break;
            case JsonArray arr:
                foreach (var it in arr) if (it != null) Walk(it);
                break;
        }
    }

    static readonly string[] ArrayCreationKinds = { "newArray", "newArraySized", "newArrayInit" };

    static void Realign(JsonObject obj)
    {
        if ((obj["k"] as JsonValue)?.TryGetValue<string>(out var k) != true || (k != "var" && k != "field")) return;
        // The declared slot must be an `Array<E?>` — a TypeNode.Array whose element is a Nullable.
        if (TypeJson.Read(obj["type"]) is not TypeNode.Array { Elem: TypeNode.Nullable }) return;
        if (obj["init"] is not JsonObject init) return;
        if ((init["k"] as JsonValue)?.TryGetValue<string>(out var ik) != true || Array.IndexOf(ArrayCreationKinds, ik) < 0) return;
        // The creation's own element token dropped the `?` (non-null E) — re-wrap it in Nullable so a genuine
        // `Nullable<E>[]` is allocated (elem stelem/ldelem then agree). Skip an already-nullable / nested-array elem.
        if (TypeJson.Read(init["elem"]) is not TypeNode elem || elem is TypeNode.Nullable or TypeNode.Array) return;
        init["elem"] = TypeJson.Write(new TypeNode.Nullable(elem));
    }
}
