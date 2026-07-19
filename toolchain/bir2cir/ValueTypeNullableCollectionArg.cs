using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

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
        "bool", "sbyte", "char", "double", "float", "int", "long", "short", "byte", "uint", "ulong", "ushort",
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
        if (call["sig"] is not JsonArray sig) return;   // sig is a structured TypeNode array (#37 m3b)
        // A kotlin.collections collection receiver whose element is a nullable type variable (`X<T?>`) — NOT an array
        // param. Walk the sig's structured TypeNodes: the old string checks (`kotlin.collections.`, `[nullable:gp:`,
        // `array:`) become structural predicates over the parameter type tree.
        bool hasColl = false, hasNullableTvArg = false, hasArray = false;
        foreach (var el in sig)
            if (TypeJson.Read(el) is TypeNode tn)
            {
                if (HasKotlinCollections(tn)) hasColl = true;
                if (HasNullableTvDirectArg(tn)) hasNullableTvArg = true;
                if (HasArray(tn)) hasArray = true;
            }
        if (!hasColl || !hasNullableTvArg || hasArray) return;
        if (call["typeArgs"] is not JsonArray ta || ta.Count == 0) return;
        if (!IsValueTypeArg(ta[0])) return;
        if (call["args"] is not JsonArray args || args.Count == 0) return;
        // Idempotence: never re-wrap an already-cast receiver.
        if (args[0] is JsonObject ro && (ro["k"] as JsonValue)?.GetValue<string>() == "clrGenericStatic"
            && (ro["method"] as JsonValue)?.GetValue<string>() == "Cast") return;
        args[0] = new JsonObject
        {
            ["k"] = "clrGenericStatic",
            ["type"] = TypeJson.Fqn("System.Linq.Enumerable"),
            ["method"] = "Cast",
            // typeArgs is a document type slot (ilemit MapType-resolves it) -> a structured `{t:fqn}` node. `memberSig`
            // (W1-S1 #46) is the FIR-resolved member descriptor: `Enumerable.Cast<TResult>(this IEnumerable source)`'s
            // DECLARED param is the non-generic `System.Collections.IEnumerable` — a structured TypeNode ilemit exact-matches.
            ["typeArgs"] = new JsonArray { new JsonObject { ["t"] = "fqn", ["name"] = "object" } },
            ["memberSig"] = new JsonArray { new JsonObject { ["t"] = "fqn", ["name"] = "System.Collections.IEnumerable" } },
            ["args"] = new JsonArray { args[0].DeepClone() },
        };
    }

    // Any Fqn in the type tree named `kotlin.collections.*` (the old `sig.Contains("kotlin.collections.")`).
    static bool HasKotlinCollections(TypeNode t) => t switch
    {
        TypeNode.Fqn f => f.Name.StartsWith("kotlin.collections.", StringComparison.Ordinal)
                          || (f.Args?.Any(HasKotlinCollections) ?? false),
        TypeNode.Nullable n => HasKotlinCollections(n.Of),
        TypeNode.Array a => HasKotlinCollections(a.Elem),
        TypeNode.ByRef b => HasKotlinCollections(b.Of),
        TypeNode.Fn fn => HasKotlinCollections(fn.Ret) || fn.DelegateParams.Any(HasKotlinCollections),   // incl. a `T.() -> R` receiver (#145)
        _ => false,
    };

    // A `Nullable(Tv)` sitting DIRECTLY in some Fqn's type-argument list (the old `sig.Contains("[nullable:gp:")` —
    // a `[` before `nullable:gp:` only comes from an Fqn's `[...]` arg list, and the tv must be its immediate arg).
    static bool HasNullableTvDirectArg(TypeNode t) => t switch
    {
        TypeNode.Fqn f => (f.Args?.Any(a => a is TypeNode.Nullable { Of: TypeNode.Tv }) ?? false)
                          || (f.Args?.Any(HasNullableTvDirectArg) ?? false),
        TypeNode.Nullable n => HasNullableTvDirectArg(n.Of),
        TypeNode.Array a => HasNullableTvDirectArg(a.Elem),
        TypeNode.ByRef b => HasNullableTvDirectArg(b.Of),
        TypeNode.Fn fn => HasNullableTvDirectArg(fn.Ret) || fn.DelegateParams.Any(HasNullableTvDirectArg),   // incl. a `T.() -> R` receiver (#145)
        _ => false,
    };

    // Any Array node anywhere in the type tree (the old `sig.Contains("array:")`).
    static bool HasArray(TypeNode t) => t switch
    {
        TypeNode.Array => true,
        TypeNode.Fqn f => f.Args?.Any(HasArray) ?? false,
        TypeNode.Nullable n => HasArray(n.Of),
        TypeNode.ByRef b => HasArray(b.Of),
        TypeNode.Fn fn => HasArray(fn.Ret) || fn.DelegateParams.Any(HasArray),   // incl. a `T.() -> R` receiver (#145)
        _ => false,
    };

    // A `typeArgs[0]` value-type test on the pre-lowering structured Type node (a bare-primitive Fqn), with a legacy
    // string fallback. ValueTypeTokens carries both the kotlin.* and the CLR-shorthand spellings.
    static bool IsValueTypeArg(JsonNode n)
    {
        if (TypeJson.Read(n) is TypeNode.Fqn { Args: null } f) return ValueTypeTokens.Contains(f.Name);
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return ValueTypeTokens.Contains(s);
        return false;
    }
}
