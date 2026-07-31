using System.Text.Json.Nodes;
using DotKt.Bir;

// NULLABLE IS-TEST (`x is T?`) — null IS a member of a nullable type.
//
// Kotlin's `is` against a NULLABLE type operand accepts null: `null is String?`, `null is Int?` and `null is Any?`
// are all `true`, and the frontend RELIES on it — the `else` branch of `when { x is CharSequence? -> … }` carries a
// smart-cast to a NON-null `x`, so a call resolved there is the `kotlin.Any` MEMBER `toString()`/`hashCode()`, not the
// null-safe `Any?.toString()` extension. kotc emits the faithful `isInst` with the type operand's `?` intact, but the
// CLR has no `isinst` that matches null (a null reference is an instance of nothing), so lowering the type alone
// silently drops the `?` and the test goes FALSE for null. The frontend's smart-cast then dereferences a null (the
// stdlib's `appendElement` -> `element.toString()` -> `callvirt Object::ToString()` NREs on a null join element).
//
// This is the layer that fixes the physical CLR representation of the Kotlin meaning, so the decision is made HERE:
// mark the node `nullMatches` and ilemit projects the one extra `dup; brtrue` that makes null answer true. The
// operand is still evaluated exactly ONCE (an `x == null || x is T` rewrite would need a temp and would change the
// evaluation shape), and a NON-nullable type operand is untouched.
//
// Runs in BIR-space BEFORE BirTypeLowering, which is where the `nullable:` wrapper on the type operand is erased
// (every CLR reference is nullable, so the lowered type cannot carry the signal). `!is T?` needs nothing extra: kotc
// emits it as `unaryOp !` over this same `isInst`.
static class NullableIsInstMatch
{
    public static void Apply(JsonNode root) => Walk(root);

    static void Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                Mark(obj);
                foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value);
                break;
            case JsonArray arr:
                foreach (var it in arr) if (it != null) Walk(it);
                break;
        }
    }

    static void Mark(JsonObject obj)
    {
        if ((obj["k"] as JsonValue)?.TryGetValue<string>(out var k) != true || k != "isInst") return;
        if (TypeJson.Read(obj["type"]) is not TypeNode.Nullable) return;
        obj["nullMatches"] = true;
    }
}
