using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

// BUG-1 Part A (bundle-6 value-type dual-representation): the CALL-SITE receiver conversion for a value-type-element
// collection passed to a nullable-generic collection extension (`Iterable<T?>.filterNotNull()` / `requireNoNulls()`).
//
// kotc erases the extension receiver `Iterable<T?>` to the token `@kotlin.collections.Iterable[nullable:gp:T]`, which
// type-lowering turns into the CLR param `IEnumerable<object>` (the boxed/erased nullable rep — see
// NullableGenericReturnErasure). For a REFERENCE element (`List<String?>`) the arg `IReadOnlyList<String>` IS
// covariantly `IEnumerable<object>`, so the plain arg flows fine. For a VALUE element (`List<Int?>` =
// `IReadOnlyList<Nullable<Int32>>`) .NET reified generics give NO value-type covariance — the collection does NOT
// implement `IEnumerable<object>`, so the call passes a value the callee can't `GetEnumerator` (NRE / ilverify
// StackUnexpected). Wrap the receiver arg in `System.Linq.Enumerable.Cast<object>(this IEnumerable)`: every collection
// implements the NON-generic `IEnumerable`, and `Cast<object>` boxes each element (a `Nullable<V>` with no value boxes
// to a genuine `null`), yielding a real `IEnumerable<object>`. The callee's loop-var is object-erased (BUG-1 Part B).
//
// Scope: ONLY a `kotlin.collections.*` collection receiver (Iterable/Collection/List/Set/...) whose element is
// `nullable:gp:` and whose concrete element type arg (`typeArgs[0]`) is a VALUE type. Excludes the array overload
// (`array:nullable:gp:` — an array param is not an `IEnumerable<object>` slot) and pure-Kotlin `kotlin.sequences.*`
// (Sequence is not @ClrTypeAlias'd to IEnumerable). Reference elements are left untouched (covariance already works).
// Runs on the substituted BIR BEFORE type lowering (the `nullable:gp:` token + kotlin.* typeArgs are still present)
// and self-gates to concrete value instantiations, so it is a no-op in the rt-stdlib self-build (open `gp:T` args).
static class ValueTypeNullableCollectionArg
{
    static readonly HashSet<string> ValueTypeTokens = new(StringComparer.Ordinal)
    {
        "kotlin.Boolean", "kotlin.Byte", "kotlin.Char", "kotlin.Double", "kotlin.Float", "kotlin.Int",
        "kotlin.Long", "kotlin.Short", "kotlin.UByte", "kotlin.UInt", "kotlin.ULong", "kotlin.UShort",
        "bool", "byte", "char", "double", "float", "int", "long", "short", "ubyte", "uint", "ulong", "ushort",
    };

    public static void Apply(JsonNode root) => Walk(root);

    static void Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                MaybeWrap(obj);
                foreach (var kv in obj) Walk(kv.Value);
                break;
            case JsonArray arr:
                foreach (var it in arr) Walk(it);
                break;
        }
    }

    static void MaybeWrap(JsonObject call)
    {
        if ((call["k"] as JsonValue)?.TryGetValue<string>(out var k) != true || k != "callStatic") return;
        if ((call["sig"] as JsonValue)?.TryGetValue<string>(out var sig) != true || sig == null) return;
        // A kotlin.collections collection receiver whose element is `nullable:gp:` — NOT an array (`array:`) param.
        if (!sig.Contains("kotlin.collections.", StringComparison.Ordinal)
            || !sig.Contains("[nullable:gp:", StringComparison.Ordinal)
            || sig.Contains("array:", StringComparison.Ordinal)) return;
        if (call["typeArgs"] is not JsonArray ta || ta.Count == 0) return;
        if ((ta[0] as JsonValue)?.TryGetValue<string>(out var elem) != true || !ValueTypeTokens.Contains(elem)) return;
        if (call["args"] is not JsonArray args || args.Count == 0) return;
        // Idempotence: never re-wrap an already-cast receiver.
        if (args[0] is JsonObject ro && (ro["k"] as JsonValue)?.GetValue<string>() == "clrGenericStatic"
            && (ro["method"] as JsonValue)?.GetValue<string>() == "Cast") return;
        args[0] = new JsonObject
        {
            ["k"] = "clrGenericStatic",
            ["type"] = "System.Linq.Enumerable",
            ["method"] = "Cast",
            ["typeArgs"] = new JsonArray { "object" },
            ["shapes"] = new JsonArray { "IEnumerable" },
            ["args"] = new JsonArray { args[0].DeepClone() },
        };
    }
}
