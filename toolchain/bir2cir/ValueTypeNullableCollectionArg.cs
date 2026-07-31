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
// NullableGenericErasure). For a REFERENCE element (`List<String?>`) the arg `IReadOnlyList<String>` IS
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
    // The struct-ness ORACLE (ReferenceMetadataIndex.IsValueTypeFqn + the local enum/struct types), not a hardcoded
    // primitive list: a Kotlin `value class` over a struct, a projected .NET struct and a local enum are value
    // elements for exactly the same CLR reason as `Int`, and a list that names only the primitives answers "no" for
    // them and silently drops the conversion.
    static Func<string, bool> _isValue = _ => false;

    public static void Apply(JsonNode root, Func<string, bool> isValue)
    {
        _isValue = isValue ?? (_ => false);
        Walk(root);
    }

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
        if (call["sig"] is not JsonArray sig || sig.Count == 0) return;   // sig is a structured TypeNode array (#37 m3b)
        // The subject is the RECEIVER — `sig[0]`, the extension's `this` — and ONLY it. Accumulating the predicates
        // across every parameter lets an unrelated one decide: `fun <C, T> Iterable<C>.f(box: Box<T?>)` would see the
        // `T?` in its second parameter and wrap a perfectly ordinary `Iterable<String>` receiver, and an unrelated
        // array parameter would suppress a conversion the receiver genuinely needs.
        if (TypeJson.Read(sig[0]) is not TypeNode recv) return;
        if (!IsKotlinCollection(recv) || !HasNullableTvDirectArg(recv) || HasArray(recv)) return;
        // WHICH type argument is the element: the index of the `Tv` under the receiver's own `Nullable(Tv)`.
        // `filterNotNull()` declares `<T : Any>` so it is `typeArgs[0]`, but `filterNotNullTo(destination: C)`
        // declares `<C, T>` and it is `typeArgs[1]`. Reading position 0 unconditionally answers about `C` — a
        // collection type, never a value — so the conversion never fired at all for the two-parameter form.
        if (FindNullableTv(recv) is not TypeNode.Tv { Scope: "method" } tv) return;
        if (call["typeArgs"] is not JsonArray ta || tv.I < 0 || tv.I >= ta.Count) return;
        if (!IsValueTypeArg(ta[tv.I])) return;
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

    // The receiver's own head is a `kotlin.collections.*` type. Its ARGUMENTS are not consulted: an
    // `Iterable<Map<K, V?>>` receiver is not a nullable-element collection, and reading the head alone says so.
    static bool IsKotlinCollection(TypeNode t)
        => t is TypeNode.Fqn f && f.Name.StartsWith("kotlin.collections.", StringComparison.Ordinal);

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

    // The `Tv` under a `Nullable(Tv)` somewhere in the type, else null.
    static TypeNode.Tv FindNullableTv(TypeNode t) => t switch
    {
        TypeNode.Nullable { Of: TypeNode.Tv tv } => tv,
        TypeNode.Nullable n => FindNullableTv(n.Of),
        TypeNode.Fqn { Args: { } args } => args.Select(FindNullableTv).FirstOrDefault(x => x != null),
        TypeNode.Array a => FindNullableTv(a.Elem),
        TypeNode.ByRef b => FindNullableTv(b.Of),
        _ => null,
    };

    // Is this type argument a value type, per the struct-ness oracle, on the pre-lowering structured Type node?
    static bool IsValueTypeArg(JsonNode n)
        => TypeJson.Read(n) is TypeNode.Fqn { Args: null } f && _isValue(f.Name);
}
