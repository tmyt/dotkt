using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;

// A generic Kotlin iterator read yields an erased value. The actual consumer, not the iterator adapter,
// owns its extraction into a native T. Select only the standard declaration/override relation; unrelated
// next/previous methods, super dispatch, and already-bound CLR bridges remain exact.
sealed partial class KotlinIteratorValueBinding
{
    static readonly TypeNode ObjectType = new TypeNode.Fqn("object");

    static TypeNode.Tv CandidateElement(bool instanceCall, bool super, bool resolved, int? arguments,
        int typeArguments, TypeNode source) => instanceCall && !super && !resolved && arguments == 0 && typeArguments == 0
            ? source as TypeNode.Tv : null;

    readonly record struct ProtocolOverride(string Identity, string Owner, string Member, int? Arity);

    static string SelectOperation(string identity, string owner, string member, IEnumerable<ProtocolOverride> overrides)
    {
        static bool Protocol(string owner, string member) => member switch {
            "next" => owner is "kotlin.collections.Iterator" or "kotlin.collections.MutableIterator"
                or "kotlin.collections.ListIterator" or "kotlin.collections.MutableListIterator",
            "previous" => owner is "kotlin.collections.ListIterator" or "kotlin.collections.MutableListIterator",
            _ => false,
        };
        if (identity != null && Protocol(owner, member)) return member;
        return overrides?.Where(edge => edge.Identity != null && edge.Arity == 0 && Protocol(edge.Owner, edge.Member))
            .Select(edge => edge.Member).Distinct().SingleOrDefault();
    }

}
