using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// KOTLIN COVARIANCE OVER A VALUE ELEMENT, AT AN OBJECT-ELEMENTED `Iterable` SLOT.
//
// Kotlin's `List<out E>` is covariant and `Int <: Int?`, so `List<Int>` IS an `Iterable<Int?>` and the frontend
// accepts `countNullable(listOf(1, 2, 3))` for `fun <T> countNullable(xs: Iterable<T?>)`. The CLR has no such
// relation: a reified generic argument is invariant for a value type, so an `IReadOnlyList<int32>` does not
// implement the `IEnumerable<object>` that slot erases to (#86), and the callee's `GetEnumerator` is not found.
//
// The conversion is `System.Linq.Enumerable.Cast<object>(this IEnumerable)`: every collection implements the
// NON-generic `IEnumerable`, and `Cast<object>` boxes each element — a `Nullable<V>` with no value boxing to a
// genuine `null` — yielding a real `IEnumerable<object>`.
//
// THE SLOT DECIDES, AND ONLY AN `Iterable` SLOT QUALIFIES. What the wrap produces is an `IEnumerable<object>` and
// nothing more, so it may only fill a slot that IS one: `kotlin.collections.Iterable<T?>`. A `List<T?>` slot is an
// `IReadOnlyList<object>` and a `Collection<T?>` slot an `IReadOnlyCollection<object>` — the wrap inhabits neither,
// and filling them with it is #324, where the conversion fired on a user generic's `List<A?>` parameter and the
// result did not inhabit the parameter at all.
//
// PER POSITION, judged by its own slot. The predicate is never accumulated across a parameter list: an unrelated
// `Box<T?>` parameter must not make an ordinary `Iterable<String>` argument convert, and an `Iterable<T?>` parameter
// in second place must not be missed because the first one is not.
//
// Runs after nullable-frame materialization and before type lowering. Read the selected slot through its physical
// method arguments, including nullable companions, and compare it with the operand's own element type. A concrete
// reference element needs nothing: CLR covariance already works there. An open element may be a value at runtime.
static class ValueElementIterableCoercion
{
    // The struct-ness ORACLE (ReferenceMetadataIndex.IsValueType + the local enum/struct types), not a hardcoded
    // primitive list: a Kotlin `value class` over a struct, a projected .NET struct and a local enum are value
    // elements for exactly the same CLR reason as `Int`, and a list that names only the primitives answers "no" for
    // them and silently drops the conversion.
    static ValueTypeOracle _isValue = _ => false;

    // The one Kotlin collection head whose CLR form is `IEnumerable<E>` — the type the wrap produces.
    const string IterableFqn = "kotlin.collections.Iterable";

    // What `System.Linq.Enumerable.Cast<object>(IEnumerable)` produces — the STATIC TYPE the wrap below stamps on
    // itself. Spelled in the CLR vocabulary the wrap is already written in (its `resolvedMemberParams` names the non-generic
    // `System.Collections.IEnumerable` the same way); BirTypeLowering passes a resolved BCL FQN through unchanged.
    static readonly TypeNode CastResultTn =
        new TypeNode.Fqn("System.Collections.Generic.IEnumerable", new TypeNode[] { new TypeNode.Fqn("object") });

    public static void Apply(JsonNode root, ValueTypeOracle isValue)
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
        if (call["sig"] is not JsonArray sig || call["args"] is not JsonArray args) return;   // sig is a structured TypeNode array (#37 m3b)
        if (sig.Count != args.Count) return;
        for (var i = 0; i < sig.Count; i++)
        {
            // Only Iterable produces precisely the interface implemented by Enumerable.Cast<TResult>.
            if (TypeJson.Read(sig[i]) is not TypeNode.Fqn { Name: IterableFqn, Args: { Length: 1 } sa }) continue;
            var target = sa[0];
            if (target is TypeNode.Tv { Scope: "method" } tv)
            {
                if (call["typeArgs"] is not JsonArray ta || tv.I < 0 || tv.I >= ta.Count) continue;
                target = TypeJson.Read(ta[tv.I]);
            }
            target = NullableGenericErasure.EraseArgument(target, _isValue);
            if (args[i] is not JsonObject argument
                || TypeJson.Read(argument["sty"]) is not TypeNode.Fqn { Args: { Length: 1 } sourceArgs } source
                || source.Name is not ("kotlin.collections.Iterable" or "kotlin.collections.Collection"
                    or "kotlin.collections.List" or "kotlin.collections.Set"
                    or "kotlin.collections.MutableIterable" or "kotlin.collections.MutableCollection"
                    or "kotlin.collections.MutableList" or "kotlin.collections.MutableSet"
                    or "kotlin.collections.ArrayList" or "kotlin.collections.HashSet"
                    or "kotlin.collections.LinkedHashSet")) continue;
            var sourceElement = NullableGenericErasure.EraseArgument(sourceArgs[0], _isValue);
            if (sourceElement == target
                || !(sourceElement is TypeNode.Tv || sourceElement is TypeNode.Fqn value && _isValue(value))) continue;
            // Idempotence: never re-wrap an already-cast argument.
            if (args[i] is JsonObject ro && (ro["k"] as JsonValue)?.GetValue<string>() == "clrGenericStatic"
                && (ro["method"] as JsonValue)?.GetValue<string>() == "Cast") continue;
            args[i] = CastElements(args[i], target);
        }
    }

    // A fully stated Enumerable.Cast<T> physical adapter. Besides this pass's nullable-value Iterable seam, array
    // factory spread normalization uses it to turn a differently reified source array into IEnumerable<TTarget>.
    // The non-generic source slot is what makes value arrays legal: enumeration boxes each value before Cast<T>.
    internal static JsonObject CastElements(JsonNode source, TypeNode target)
    {
        var result = target is TypeNode.Fqn { Args: null, Name: "object" }
            ? CastResultTn
            : new TypeNode.Fqn("System.Collections.Generic.IEnumerable", new[] { target });
        return new JsonObject
        {
            ["k"] = "clrGenericStatic",
            ["type"] = TypeJson.Fqn("System.Linq.Enumerable"),
            ["method"] = "Cast",
            // typeArgs is a document type slot (ilemit MapType-resolves it) -> a structured Type node.
            // resolvedMemberParams is Enumerable.Cast<TResult>(IEnumerable)'s exact declared parameter.
            ["typeArgs"] = new JsonArray { TypeJson.Write(target) },
            ["resolvedMemberParams"] = new JsonArray { TypeJson.Fqn("System.Collections.IEnumerable") },
            ["args"] = new JsonArray { source.DeepClone() },
            // The adapter changes the operand's static type; stamp the exact value it produces so suspend planning
            // and every later structural consumer see IEnumerable<TTarget>, not the input array.
            ["sty"] = TypeJson.Write(result),
        };
    }

}
