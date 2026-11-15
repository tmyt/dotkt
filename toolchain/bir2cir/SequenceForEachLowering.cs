using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;

// Opaque Kotlin enumerable values cannot be dispatched as one reified IEnumerable<elem> construction. ForInLowering
// records that physical boundary before ordinary alias erasure discards the source owner; source expressions that
// still state an erased Sequence/collection view use the same boundary. Dispatch through the non-generic
// IEnumerable / IEnumerator shared by every IEnumerable<T>, then convert each Current value to the declared element.
//
// Replaces the owning forEachInline slot with a `block`:
//     var $seqIt$N : System.Collections.IEnumerator = (<src>).GetEnumerator()          // non-generic
//     while ($seqIt$N.MoveNext()) { var <x> : elem = (elem) $seqIt$N.get_Current(); <body...> }
// The `while` retains the loop label and element conversion, including value-element unboxing. Exact native
// enumerable values keep their typed path. This runs before BirTypeLowering in executable builds.
static partial class SequenceForEachLowering
{
    static int _tmp;

    // The non-generic BCL interfaces StarProjectionLowering rewrites a star-projected/erased collection `cast` onto
    // (#74b) — a `for`-loop source landing here already wearing one of these needs the SAME non-generic dispatch a
    // Sequence does (its underlying runtime value, e.g. a `List<int>`, has no typed `IEnumerable<object>` slot).
    static readonly HashSet<string> NonGenericIfaces = new(System.StringComparer.Ordinal)
    {
        "System.Collections.ICollection", "System.Collections.IList",
        "System.Collections.IEnumerable", "System.Collections.IDictionary",
    };

    static TypeNode Unwrap(TypeNode t) => t is TypeNode.Nullable nu ? Unwrap(nu.Of) : t;

}
