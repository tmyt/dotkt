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
static partial class ValueElementIterableCoercion
{
    // The one Kotlin collection head whose CLR form is `IEnumerable<E>` — the type the wrap produces.
    const string IterableFqn = "kotlin.collections.Iterable";

    // What `System.Linq.Enumerable.Cast<object>(IEnumerable)` produces — the STATIC TYPE the wrap below stamps on
    // itself. Spelled in the CLR vocabulary the wrap is already written in (its `resolvedMemberParams` names the non-generic
    // `System.Collections.IEnumerable` the same way); BirTypeLowering passes a resolved BCL FQN through unchanged.
    static readonly TypeNode CastResultTn =
        new TypeNode.Fqn("System.Collections.Generic.IEnumerable", new TypeNode[] { new TypeNode.Fqn("object") });

    // A fully stated Enumerable.Cast<T> physical adapter. Besides this pass's nullable-value Iterable seam,
    // spread normalization uses it to expose the exact IEnumerable<TTarget> element contract.
    // The non-generic source slot is what makes value arrays legal: enumeration boxes each value before Cast<T>.
    internal static JsonObject CastElements(JsonNode source, TypeNode target)
    {
        var result = CastResultType(target);
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

    static TypeNode CastResultType(TypeNode target) => target is TypeNode.Fqn { Args: null, Name: "object" }
        ? CastResultTn : new TypeNode.Fqn("System.Collections.Generic.IEnumerable", new[] { target });

    // Shared selected-slot decision, independent of the owning executable representation.
    static TypeNode CastTarget(TypeNode signature, TypeNode[] methodArguments, TypeNode.Fqn source,
        IReadOnlyDictionary<string, SupertypeGraph.Def> definitions, ReferenceMetadataIndex refs, ValueTypeOracle isValue)
    {
        if (signature is not TypeNode.Fqn { Name: IterableFqn, Args: { Length: 1 } arguments }) return null;
        var target = arguments[0];
        if (target is TypeNode.Tv { Scope: "method" } variable)
        {
            if (methodArguments == null || variable.I < 0 || variable.I >= methodArguments.Length) return null;
            target = methodArguments[variable.I];
        }
        target = NullableGenericErasure.EraseArgument(target, isValue);
        var application = new SupertypeGraph.Def { Base = source };
        var elements = SupertypeGraph.Reachable(application, definitions, refs)
            .Select(edge => edge.spec)
            .Where(spec => spec.Name is IterableFqn or "System.Collections.Generic.IEnumerable"
                    or "System.Collections.Generic.IEnumerable`1" && spec.Args is { Length: 1 })
            .Select(spec => NullableGenericErasure.EraseArgument(spec.Args[0], isValue)).Distinct().ToArray();
        if (elements.Length != 1) return null;
        var element = elements[0];
        return element != target && (element is TypeNode.Tv || element is TypeNode.Fqn value && isValue(value))
            ? target : null;
    }

}
