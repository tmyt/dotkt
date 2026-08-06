using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// STAR-PROJECTION BOUND LOWERING (#2): a `T<*>` on a SELF-REF-BOUNDED generic `interface Key<E : Element>` is lowered by
// kotc to `Key<kotlin.Any>` (its `at == null -> OBJ` star-projection rule discards the bound), which then becomes
// `Key<System.Object>` at BirTypeLowering. But `System.Object` does NOT satisfy `E : Element`, so the reified generic
// instantiation is illegal on the CLR:
//   - a stdlib `get_key(): Key<*>` methodimpl signature no longer matches its interface declaration ("Signature of the
//     body and declaration in a method implementation do not match"), surfacing when an app subclasses
//     AbstractCoroutineContextElement and the loader realizes the type;
//   - an app `override val key: CoroutineContext.Key<*>` emits `Key<object>` directly ("GenericArguments[0] System.Object
//     violates the constraint of type E").
// Both are ONE root: the star projection dropped the bound. This layer reads the type-param CONSTRAINT metadata (bir2cir's
// lane — precedent: BirTypeLowering's `Comparable<*>` -> IComparable, MapVarianceRealign's typeParams[].constraints) and
// substitutes the BOUND: `Key<*>` -> `Key<Element>`. `Key<kotlin.Any>` is not valid Kotlin (Any violates `E : Element`),
// so an objectish arg on a bounded param UNAMBIGUOUSLY came from a star projection — the rewrite is safe. A genuine
// `List<Any>` is untouched (List's param is unconstrained -> no bound).
//
// It resolves the bound for BOTH a REFERENCED owner (app build, via ReferenceMetadataIndex.TvBound — the stdlib.ref.dll
// generic-parameter constraint) AND the stdlib's OWN in-assembly owner (its self-build, via the type declarations'
// typeParams[].constraints collected across all input BIR files). Runs in ALL builds, in BIR-space (dotted Kotlin FQNs,
// kotlin.Any) BEFORE BirTypeLowering, so the ref.dll + rt.dll + app views of `Key<E>` agree.
//
// F-BOUND TERMINATION: for a self-referential bound (`Enum<E : Enum<E>>`) there is no valid closed generic to substitute
// to, and expanding the bound would only push the same `object` violation one level deeper. So a bound that REFERENCES a
// type var (`Enum<E>` — ContainsTypeVar) is NOT substituted: the objectish arg is left exactly as kotc produced it
// (`Enum<*>` stays `Enum<object>`, finite). The referenced-owner path skips it symmetrically — GenericParamBound returns
// null for a gp-dependent constraint. Only a CONCRETE bound (`Element`, which does not reference `E`) is substituted, so
// `Key<*>` -> `Key<Element>`. (Enum is separately collapsed to the non-generic `System.Enum` by BirTypeLowering, so its
// residual `Enum<object>` never reaches a real reified instantiation.)
static class StarProjectionBoundLowering
{
    // Collect in-assembly generic type-param BOUNDS: owner FQN (dotted) -> per-param bound TypeNode (null for an
    // unconstrained / objectish-bounded param). Across ALL input roots (a star-projected owner's declaration may live in
    // a sibling .bir.json — e.g. AbstractCoroutineContextElement uses Key<*> whose interface is in another file).
    public static Dictionary<string, TypeNode[]> CollectTypeParamBounds(IEnumerable<JsonNode> roots)
    {
        var map = new Dictionary<string, TypeNode[]>(StringComparer.Ordinal);
        foreach (var root in roots)
            if (root is JsonObject o && o["types"] is JsonArray types)
                foreach (var t in types)
                    if (t is JsonObject to) CollectType(to, map);
        return map;
    }

    static void CollectType(JsonObject t, Dictionary<string, TypeNode[]> map)
    {
        var tps = TypeParameterFrame.CloneDeclarations(t);
        if (Str(t["name"]) is string name && tps.Count > 0)
        {
            var bounds = new TypeNode[tps.Count];
            var any = false;
            for (var i = 0; i < tps.Count; i++)
                if (tps[i] is JsonObject to && to["constraints"] is JsonArray cs)
                    foreach (var c in cs)
                        if (TypeJson.Read(c) is TypeNode ct && !IsObjectish(ct)) { bounds[i] = ct; any = true; break; }
            if (any) map.TryAdd(name, bounds);
        }
        // A nested type declaration (`CoroutineContext.Key` inside CoroutineContext) carries its own typeParams.
        if (t["types"] is JsonArray nested)
            foreach (var n in nested) if (n is JsonObject no) CollectType(no, map);
    }

    public static void Apply(JsonNode root, IReadOnlyDictionary<string, TypeNode[]> localBounds, ReferenceMetadataIndex refs)
        => Walk(root, localBounds, refs);

    static void Walk(JsonNode node, IReadOnlyDictionary<string, TypeNode[]> localBounds, ReferenceMetadataIndex refs)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var val = obj[key];
                    if (val == null) continue;
                    // `name` is a declaration's own identity and `owner` is a call's static container — neither is a
                    // constructed-generic type slot to repoint (mirrors ContinuationErasure's identity-slot skip).
                    if (key == "name" || key == "owner") continue;
                    if (TypeJson.Read(val) is TypeNode tn)
                        obj[key] = TypeJson.Write(Subst(tn, localBounds, refs));
                    else
                        Walk(val, localBounds, refs);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var val = arr[i];
                    if (val == null) continue;
                    if (TypeJson.Read(val) is TypeNode tn)
                        arr[i] = TypeJson.Write(Subst(tn, localBounds, refs));
                    else
                        Walk(val, localBounds, refs);
                }
                break;
        }
    }

    // Repoint an objectish arg of a bounded generic to its type-param BOUND, recursively through nested generics /
    // nullable / array / byref / fn. NON-recursive on the bound itself (F-bound termination).
    static TypeNode Subst(TypeNode t, IReadOnlyDictionary<string, TypeNode[]> localBounds, ReferenceMetadataIndex refs)
    {
        switch (t)
        {
            case TypeNode.Fqn { Args: { } args } f:
            {
                var newArgs = args.Select(a => Subst(a, localBounds, refs)).ToArray();
                for (var i = 0; i < newArgs.Length; i++)
                    if (IsObjectish(newArgs[i]) && BoundFor(f.Name, i, localBounds, refs) is TypeNode bound)
                    {
                        // A dependent bound may refer to an EARLIER owner parameter (`<B : Element, E : B>`).
                        // By this point that earlier star has already been closed to its own bound, so substitute it
                        // into the dependent bound: `<Any,Any>` -> `<Element,Element>`. Self/forward references remain
                        // type variables and are rejected by ContainsTypeVar, preserving F-bound termination.
                        var closedBound = CloseEarlierOwnerTypeVars(bound, newArgs, i);
                        if (!ContainsTypeVar(closedBound)) newArgs[i] = closedBound;
                    }
                return new TypeNode.Fqn(f.Name, newArgs);
            }
            case TypeNode.Nullable n: return new TypeNode.Nullable(Subst(n.Of, localBounds, refs));
            case TypeNode.Oblivious o: return new TypeNode.Oblivious(Subst(o.Of, localBounds, refs));
            case TypeNode.Array a: return new TypeNode.Array(Subst(a.Elem, localBounds, refs));
            case TypeNode.ByRef b: return new TypeNode.ByRef(Subst(b.Of, localBounds, refs));
            case TypeNode.Fn fn: return new TypeNode.Fn(fn.Suspend, Subst(fn.Ret, localBounds, refs),
                fn.Params.Select(p => Subst(p, localBounds, refs)).ToArray(),
                fn.Recv == null ? null : Subst(fn.Recv, localBounds, refs));
            default: return t;
        }
    }

    // The non-objectish declared bound of `ownerFqn`'s type param at index `i`: the in-assembly declaration first (the
    // stdlib self-build), then the referenced ref.dll generic-parameter constraint (the app build). Null when neither
    // has a bound `object` would violate.
    static TypeNode BoundFor(string ownerFqn, int i, IReadOnlyDictionary<string, TypeNode[]> localBounds, ReferenceMetadataIndex refs)
    {
        if (localBounds.TryGetValue(ownerFqn, out var arr) && i >= 0 && i < arr.Length && arr[i] is TypeNode local)
            return local;
        return refs.TvBound(ownerFqn, i);
    }

    // True when a bound REFERENCES a type var (`E : Enum<E>` -> the `Enum<E>` bound contains a `tv`). Such an F-bound has
    // no valid closed generic to substitute to, so BoundFor's result is skipped (the objectish arg is left unchanged) —
    // this is the F-bound termination guard. A concrete bound (`Element`) contains no tv and IS substituted.
    static bool ContainsTypeVar(TypeNode t) => t switch
    {
        TypeNode.Tv => true,
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsTypeVar),
        TypeNode.Nullable n => ContainsTypeVar(n.Of),
        TypeNode.Oblivious o => ContainsTypeVar(o.Of),
        TypeNode.Array a => ContainsTypeVar(a.Elem),
        TypeNode.ByRef b => ContainsTypeVar(b.Of),
        TypeNode.Fn fn => ContainsTypeVar(fn.Ret) || fn.Params.Any(ContainsTypeVar) || (fn.Recv != null && ContainsTypeVar(fn.Recv)),
        _ => false,
    };

    // Substitute only owner type vars whose index is earlier than the parameter currently being closed. Those args
    // have already passed through this left-to-right loop and are constraint-compatible. A self-reference (`i`) or
    // forward reference (`> i`) intentionally survives, so ContainsTypeVar rejects the uncloseable bound.
    static TypeNode CloseEarlierOwnerTypeVars(TypeNode t, TypeNode[] args, int current) => t switch
    {
        TypeNode.Tv { Scope: "type", I: var i } when i >= 0 && i < current && i < args.Length => args[i],
        TypeNode.Fqn { Args: { } nested } f => new TypeNode.Fqn(f.Name,
            nested.Select(a => CloseEarlierOwnerTypeVars(a, args, current)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(CloseEarlierOwnerTypeVars(n.Of, args, current)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(CloseEarlierOwnerTypeVars(o.Of, args, current)),
        TypeNode.Array a => new TypeNode.Array(CloseEarlierOwnerTypeVars(a.Elem, args, current)),
        TypeNode.ByRef b => new TypeNode.ByRef(CloseEarlierOwnerTypeVars(b.Of, args, current)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, CloseEarlierOwnerTypeVars(fn.Ret, args, current),
            fn.Params.Select(p => CloseEarlierOwnerTypeVars(p, args, current)).ToArray(),
            fn.Recv == null ? null : CloseEarlierOwnerTypeVars(fn.Recv, args, current)),
        _ => t,
    };

    // A star-projection / erased arg: `kotlin.Any`/`object`/`System.Object`, possibly nullable/oblivious-wrapped (a
    // `Map<*,*>` projects each arg to `Any?`). `kotlin.Nothing` is deliberately EXCLUDED — a genuine `Foo<Nothing>` is
    // valid Kotlin and must not be repointed.
    static bool IsObjectish(TypeNode t) => t switch
    {
        TypeNode.Nullable n => IsObjectish(n.Of),
        TypeNode.Oblivious o => IsObjectish(o.Of),
        TypeNode.Fqn { Args: null } f => f.Name is "kotlin.Any" or "object" or "System.Object",
        _ => false,
    };

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
