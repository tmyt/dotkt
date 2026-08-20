using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// COMPREHENSIVE reference-nullable strip (#37/#48). ilemit's MapType asserts that a `{t:nullable}` node it maps has a
// VALUE inner (a reference `Nullable<referenceType>` is not a real CLR type — a reference is nullable in IL regardless).
// So NO `{t:nullable,of:<reference>}` may reach ilemit in ANY position: not just decl `type`/`ret`/param slots (which
// BirTypeLowering.LowerType visits), but also owner generic type-args (ilemit reaches these via ParseOwnerT), call
// `argTypes`/`typeArgs`, expression `cast`/`type` fields, and any node child. LowerNode only routes the KNOWN type keys
// through LowerType, so a Type object under a non-type key (or an owner's nested arg) can slip past — this crashed the
// ref-stdlib emit on a `Continuation<Any?>`-shaped owner.
//
// This sweep is position-INDEPENDENT: it walks the ENTIRE BIR JSON tree, recognizes ANY structured Type node by its
// `{t:…}` discriminator (regardless of the key it sits under), and recursively strips its reference nullables. A VALUE
// `{t:nullable,of:<value/struct/enum>}` STAYS `{t:nullable}` (ilemit builds `System.Nullable<T>`); a `Tv` inner is
// non-value -> bare `Tv` (the object-erasure lifeline passes already converted the dataflow-critical unconstrained
// `T?` to `object` upstream, so a surviving `Nullable(Tv)` is a non-erased usage that lowers to the bare tv).
//
// Runs on the SEMANTIC tree (kotlin.* names) AFTER DeclNullableFlags (so the NRT byte walk still saw the nullability)
// and BEFORE BirTypeLowering — the oracle is unambiguous on the semantic names, and USAGE positions get ONLY the bare
// strip, never an NRT byte (nullability at a type usage is compile-time-only, not NRT-annotated).
static class ReferenceNullableStrip
{
    public static void Apply(JsonNode node, ValueTypeOracle isValue)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    var child = o[key];
                    if (child == null) continue;
                    if (TypeJson.Read(child) is TypeNode t) o[key] = TypeJson.Write(Strip(t, isValue));
                    else Apply(child, isValue);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    var child = a[i];
                    if (child == null) continue;
                    if (TypeJson.Read(child) is TypeNode t) a[i] = TypeJson.Write(Strip(t, isValue));
                    else Apply(child, isValue);
                }
                break;
        }
    }

    // Recursively strip reference nullables within a Type: a `Nullable` with a value inner keeps its wrapper; a
    // `Nullable` with a reference/tv/generic/array/fn inner collapses to the (recursively-stripped) bare inner.
    static TypeNode Strip(TypeNode t, ValueTypeOracle isValue) => t switch
    {
        TypeNode.Nullable n => IsValueInner(n.Of, isValue)
            ? new TypeNode.Nullable(Strip(n.Of, isValue))
            : Strip(n.Of, isValue),
        TypeNode.Oblivious o => new TypeNode.Oblivious(Strip(o.Of, isValue)),
        TypeNode.Fqn { Args: null } f => f,
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args.Select(x => Strip(x, isValue)).ToArray()),
        TypeNode.Array a => new TypeNode.Array(Strip(a.Elem, isValue)),
        TypeNode.ByRef b => new TypeNode.ByRef(Strip(b.Of, isValue)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, Strip(fn.Ret, isValue),
            fn.Params.Select(x => Strip(x, isValue)).ToArray(),
            fn.Recv == null ? null : Strip(fn.Recv, isValue)),
        _ => t,
    };

    // A CONSTRUCTED name is a struct if the oracle says so: `ArraySegment<String>?` is a `Nullable<ArraySegment<String>>`
    // and keeps its wrapper exactly as `Int?` does. Matching only the argument-LESS shape stripped it to the bare
    // struct, so a `null` element read out of an `Array<ArraySegment<String>?>` was unboxed as a NON-nullable struct —
    // a NullReferenceException, or silently no null at all. The oracle answers false for a constructed REFERENCE
    // generic (`List<String>?`), which is what keeps that side stripping as before.
    static bool IsValueInner(TypeNode of, ValueTypeOracle isValue) =>
        of is TypeNode.Fqn f && isValue(f);
}
