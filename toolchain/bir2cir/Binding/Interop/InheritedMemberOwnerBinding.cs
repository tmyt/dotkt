using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;

// Bind an inherited instance member to its CONSTRUCTED declaring owner.
//
// BIR deliberately records the Kotlin receiver owner.  For example, a call through
// `Derived<T>` to `Base<T>.m()` remains ownerType=Derived<T>; that is the faithful
// Kotlin-IR projection and must not be polluted with a CLR MemberRef decision in kotc.
// A CLR MemberRef, however, cannot name the open `Base<>.m` when the call is made on
// `Base<T>`: doing so produces "containing type is not fully instantiated" at JIT time.
//
// Discovering the base declaration while emitting and reconstructing its generic
// instantiation makes emission semantic and loses
// the substitution on generic-method calls.  This pass performs the general hierarchy
// substitution in bir2cir and rewrites ownerType to the exact constructed declaration
// (`Base<T>`).  ilemit subsequently links that owner one-to-one.
//
// Resolution is intentionally conservative:
//   * a kotc `overrides` fact wins when it names one unambiguous reachable declaration;
//   * otherwise an exact name/generic-arity/parameter-signature match is required at the
//     nearest hierarchy depth;
//   * ambiguity or incomplete type information leaves the node untouched (never guesses
//     an overload).
// No library/member FQNs are special-cased.
static partial class InheritedMemberOwnerBinding
{
    class TypeShape
    {
        public string Name;
        public string Kind;
        public int TypeParamCount;
        public TypeNode.Fqn Base;
        public TypeNode.Fqn[] Interfaces = Array.Empty<TypeNode.Fqn>();
    }

    readonly record struct Reachable(TypeNode.Fqn Type, int Depth);

    // Reference hierarchy edges can use the index's semantic spelling while the selected declaration names
    // an exact CLR TypeDef. Match only through the recorded arity-aware identity map, never by stripping names
    // or borrowing argument types. Keep the selected owner spelling and the hierarchy's constructed arguments.
    static TypeNode.Fqn ProjectUseOwner<T>(TypeNode.Fqn owner, TypeNode.Fqn receiver, bool bareThis, bool super,
        TypeNode.Fqn enclosing, IReadOnlyDictionary<string, T> types, ReferenceMetadataIndex refs, bool sourceFrames) where T : TypeShape
    {
        var start = receiver ?? (bareThis || super ? enclosing : null);
        if (start == null || start.Name == owner.Name && !(owner.Args == null && start.Args != null)) return null;
        var candidates = ConstructedOwners(start, owner.Name, types, refs, sourceFrames);
        return candidates.Count == 1 ? candidates[0] : null;
    }

    static List<TypeNode.Fqn> ConstructedOwners<T>(TypeNode.Fqn start, string selectedOwner,
        IReadOnlyDictionary<string, T> types, ReferenceMetadataIndex refs, bool sourceFrames = false) where T : TypeShape =>
        ReachableTypes(start, types, refs, sourceFrames)
            .Where(candidate => candidate.Type.Name == selectedOwner
                || !types.ContainsKey(candidate.Type.Name) && !types.ContainsKey(selectedOwner)
                    && refs.TryExactPhysicalTypeName(candidate.Type.Name, candidate.Type.Args?.Length ?? 0,
                        out var physicalOwner) && physicalOwner == selectedOwner)
            .Select(candidate => new TypeNode.Fqn(selectedOwner, candidate.Type.Args))
            .GroupBy(SupertypeGraph.TypeKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

    static IEnumerable<Reachable> ReachableTypes<T>(TypeNode.Fqn start, IReadOnlyDictionary<string, T> types,
        ReferenceMetadataIndex refs, bool sourceFrames = false) where T : TypeShape
    {
        var queue = new Queue<Reachable>();
        var seen = new HashSet<TypeNode.Fqn>();
        queue.Enqueue(new Reachable(start, 0));
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current.Type)) continue;
            yield return current;
            TypeNode.Fqn baseType;
            TypeNode.Fqn[] interfaces;
            int typeParamCount;
            if (types.TryGetValue(current.Type.Name, out var def))
            {
                typeParamCount = def.TypeParamCount;
                baseType = def.Base;
                interfaces = def.Interfaces;
            }
            else if (sourceFrames && refs.TryReferenceSourceTypeShape(current.Type, out typeParamCount, out baseType,
                         out interfaces)) { }
            else if (!sourceFrames && refs.TryReferenceTypeShape(current.Type, out typeParamCount, out _, out baseType,
                         out interfaces)) { }
            else continue;
            var args = EffectiveArgs(current.Type, typeParamCount);
            if (args == null) continue;
            if (baseType is not null)
                queue.Enqueue(new Reachable((TypeNode.Fqn)SubstOwnerTvs(baseType, args), current.Depth + 1));
            foreach (var iface in interfaces)
                queue.Enqueue(new Reachable((TypeNode.Fqn)SubstOwnerTvs(iface, args), current.Depth + 1));
        }
    }

    // The constructed base-CLASS chain only, including start. Each edge is expressed in its parent's type-parameter
    // frame, so close it through the current construction before continuing (`Middle<T> : Base<List<T>>`).
    static IEnumerable<TypeNode.Fqn> BaseClassChain<T>(TypeNode.Fqn start,
        IReadOnlyDictionary<string, T> types, ReferenceMetadataIndex refs) where T : TypeShape
    {
        var current = start;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (current != null && seen.Add(SupertypeGraph.TypeKey(current)))
        {
            yield return current;
            TypeNode.Fqn baseType;
            int typeParamCount;
            if (types.TryGetValue(current.Name, out var def))
            {
                typeParamCount = def.TypeParamCount;
                baseType = def.Base;
            }
            else if (refs.TryReferenceTypeShape(current, out typeParamCount, out _, out baseType, out _)) { }
            else yield break;
            if (baseType == null) yield break;
            var args = EffectiveArgs(current, typeParamCount);
            if (args == null) yield break;
            current = (TypeNode.Fqn)SubstOwnerTvs(baseType, args);
        }
    }

    static bool IsInterface<T>(TypeNode.Fqn owner, IReadOnlyDictionary<string, T> types, ReferenceMetadataIndex refs) where T : TypeShape
    {
        if (types.GetValueOrDefault(owner.Name)?.Kind == "interface") return true;
        return refs.TryReferenceTypeShape(owner, out _, out var kind, out _, out _) && kind == "interface";
    }

    static TypeNode[] EffectiveArgs(TypeNode.Fqn type, int count)
    {
        if (count == 0) return Array.Empty<TypeNode>();
        return type.Args is { } args && args.Length == count ? args : null;
    }

    static bool IsNestedObjectErasureOf(TypeNode candidate, TypeNode source)
    {
        if (candidate == null || source == null || candidate.Equals(source)) return candidate != null;
        return (candidate, source) switch
        {
            (TypeNode.Fqn { Args: { } ca } cf, TypeNode.Fqn { Args: { } sa } sf)
                when cf.Name == sf.Name && ca.Length == sa.Length
                => ca.Zip(sa, IsObjectErasureOf).All(x => x),
            (TypeNode.Array c, TypeNode.Array s) => IsObjectErasureOf(c.Elem, s.Elem),
            (TypeNode.Nullable c, TypeNode.Nullable s) => IsObjectErasureOf(c.Of, s.Of),
            (TypeNode.Oblivious c, TypeNode.Oblivious s) => IsObjectErasureOf(c.Of, s.Of),
            (TypeNode.ByRef c, TypeNode.ByRef s) => IsObjectErasureOf(c.Of, s.Of),
            (TypeNode.Fn c, TypeNode.Fn s)
                when c.Params.Length == s.Params.Length && c.Suspend == s.Suspend
                     && (c.Recv == null) == (s.Recv == null)
                => IsObjectErasureOf(c.Ret, s.Ret)
                   && c.Params.Zip(s.Params, IsObjectErasureOf).All(x => x)
                   && (c.Recv == null || IsObjectErasureOf(c.Recv, s.Recv)),
            _ => false,
        };
    }

    static bool IsObjectErasureOf(TypeNode candidate, TypeNode source)
    {
        if (candidate.Equals(source)) return true;
        if (candidate is TypeNode.Fqn { Name: "object", Args: null }) return true;
        return IsNestedObjectErasureOf(candidate, source);
    }

    static TypeNode SubstOwnerTvs(TypeNode type, TypeNode[] args) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < args.Length => args[tv.I],
        TypeNode.Fqn f when f.Args is not null => new TypeNode.Fqn(f.Name, f.Args.Select(a => SubstOwnerTvs(a, args)).ToArray()),
        TypeNode.Nullable n => SubstOwnerTvs(n.Of, args) switch {
            TypeNode.Nullable nullable => nullable,
            var inner => new TypeNode.Nullable(inner),
        },
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstOwnerTvs(o.Of, args)),
        TypeNode.Array a => new TypeNode.Array(SubstOwnerTvs(a.Elem, args)),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstOwnerTvs(b.Of, args)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, SubstOwnerTvs(fn.Ret, args),
            fn.Params.Select(p => SubstOwnerTvs(p, args)).ToArray(),
            fn.Recv == null ? null : SubstOwnerTvs(fn.Recv, args)),
        _ => type,
    };

}
