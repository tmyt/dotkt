using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;

// THE PHYSICAL VIEW OF AN ARGUMENT-ABANDONING PROJECTION — the ONE place bir2cir decides it.
//
// THE RULE. Wherever the set of instantiations Kotlin's subtyping admits for a slot differs from the set its
// physical CLR type admits under assignment compatibility, the lowering is wrong. The physical form of a
// projection that abandons knowledge of a type argument must be a type that EVERY admitted instantiation is
// assignment-compatible with.
//
// ECMA-335 §I.8.7.1 rule 8 is the authority: a covariant generic argument requires *compatible-with*, and a
// VALUE type reaches `object` only by BOXING — never by a reference conversion. So `List<*>` cannot lower to
// `IReadOnlyList<object>`: a `List<int32>` is not assignment-compatible with it, the store is unverifiable IL,
// and the first interface dispatch on the slot fails at run time (EntryPointNotFound / InvalidCast) because
// reified generics make the two constructions unrelated types.
//
// STAR IS NOT THE VARIABLE. `List<*>`, `List<out Any?>` and `List<Any?>` are ONE Kotlin type (an out-projection
// to the parameter's own bound IS the type), and every one of them admits `List<Int>`. What this class keys on
// is therefore the ABANDONMENT — a type argument the slot no longer knows — not the `*` spelling. kotc carries
// the star faithfully into BIR; an objectish argument at a DECLARATION-SITE COVARIANT parameter is the same
// abandonment written differently, and [AbandonsArgument] answers true for both. An objectish argument at an
// INVARIANT parameter is NOT an abandonment: Kotlin then admits exactly that one instantiation, which the
// reified `G<object>` admits exactly too, so the lowering is already right and must not change.
//
// THE VIEW IS DERIVED, NOT TABULATED. For a type whose CLR form is a constructed generic, the view is its
// most-derived NON-GENERIC ancestor — an ancestor is assignment-compatible with every instantiation by
// construction, which is precisely what the rule demands. `IReadOnlyList<T>`/`IDictionary<K,V>`/`ICollection<T>`
// all reach `System.Collections.IEnumerable`; an array reaches `System.Array`; a DotKt generic has no such
// ancestor, so FBoundStarProjectionErasure SYNTHESIZES one (`G$dotkt_star`) and makes every closed `G<X>`
// implement it. Three providers, one rule.
//
// Members are NOT the view's problem: a non-generic view exposes only erased members, so a star-projected
// member call is routed by StarProjectionLowering to an `Any`-taking CLR-stdlib helper (the established
// ClrMapDefaults idiom) that reads the value through the non-generic BCL facades. That keeps the physical view
// free to be the loosest sound type instead of the tightest guessable one.
//
// The `is`/`as` CLASSIFIER is a different question and is not decided here: a runtime shape test wants the
// TIGHTEST sound classifier (`IList`/`IDictionary`/`ICollection`), while a slot wants the LOOSEST sound type.
// StarProjectionLowering owns the classifier table; this class owns the slot.
static class StarProjectionView
{
    public const string RawEnumerable = "System.Collections.IEnumerable";
    public const string ArrayView = "System.Array";
    public const string ObjectView = "object";

    // The CLR type resolver (ReferenceMetadataIndex.ResolveNetType), bound per bir2cir run. Single-threaded, so a
    // static binding is sufficient — the same shape BirTypeLowering's `_aliases`/`_isValueFqn` oracles use.
    static Func<string, int, Type> _resolveClr = (_, _) => null;
    static Func<string, string> _alias = _ => null;
    static readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);

    public static void Bind(Func<string, int, Type> resolveClr, Func<string, string> alias)
    {
        _resolveClr = resolveClr ?? ((_, _) => null);
        _alias = alias ?? (_ => null);
        _cache.Clear();
    }

    /// True for a type ARGUMENT that abandons knowledge of the instantiation: an explicit `*`, or — at a
    /// covariant parameter — an objectish argument, which Kotlin treats as the very same type.
    public static bool IsAbandoned(TypeNode arg, bool covariantParam) =>
        arg is TypeNode.Star || (covariantParam && IsObjectish(arg));

    /// True when ANY argument of a constructed type is abandoned. `covariant` answers, per parameter index,
    /// whether the declaration site made that parameter `out`; a null oracle means "star spelling only".
    public static bool AbandonsArgument(TypeNode.Fqn f, Func<int, bool> covariant = null)
    {
        if (f.Args == null) return false;
        for (var i = 0; i < f.Args.Length; i++)
            if (IsAbandoned(f.Args[i], covariant != null && covariant(i))) return true;
        return false;
    }

    /// The physical view of a Kotlin type whose CLR form is the constructed generic `clrFqn<args>`: the
    /// most-derived non-generic ancestor of that CLR type, or `object` when it has none. Cached per CLR name.
    public static string ViewOfClrGeneric(string clrFqn, int arity)
    {
        var key = clrFqn + "`" + arity;
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var view = ComputeView(clrFqn, arity);
        _cache[key] = view;
        return view;
    }

    static string ComputeView(string clrFqn, int arity)
    {
        var t = _resolveClr(clrFqn, arity);
        if (t == null) return ObjectView;
        // A non-generic ancestor is assignment-compatible with EVERY instantiation, so any of them satisfies the
        // rule; prefer the most derived one so the view keeps as much of the surface as it soundly can.
        Type best = null;
        foreach (var i in t.GetInterfaces())
        {
            if (i.IsGenericType || i.IsGenericTypeDefinition) continue;
            if (best == null || best.IsAssignableFrom(i)) best = i;
        }
        if (best != null) return best.FullName;
        // A generic CLASS (a projected .NET `List<T>`, a DotKt generic that reached here) may still have a
        // non-generic BASE. Walk it before giving up on `object`.
        for (var b = t.BaseType; b != null; b = b.BaseType)
            if (!b.IsGenericType && b != typeof(object)) return b.FullName;
        return ObjectView;
    }

    /// The physical view of an abandoning projection over `f`, or null when `f` is not one. `f` is in BIR
    /// vocabulary: a Kotlin FQN that may carry a `@ClrTypeAlias` (the alias target decides the CLR shape), or a
    /// projected .NET FQN kotc already wrote in CLR vocabulary.
    public static TypeNode ViewOf(TypeNode.Fqn f, Func<int, bool> covariant = null)
    {
        if (!AbandonsArgument(f, covariant)) return null;
        var clr = _alias(f.Name) ?? f.Name;
        // `Comparable<*>` keeps its established non-generic answer: `System.IComparable<in T>` has no non-generic
        // ancestor, yet every boxed value DOES implement the non-generic `System.IComparable`, so the derivation's
        // `object` fallback would throw away a real, sound identity.
        if (clr == "System.IComparable") return new TypeNode.Fqn("System.IComparable");
        return new TypeNode.Fqn(ViewOfClrGeneric(clr, f.Args.Length));
    }

    public static bool IsObjectish(TypeNode t) => t switch
    {
        TypeNode.Nullable n => IsObjectish(n.Of),
        TypeNode.Oblivious o => IsObjectish(o.Of),
        TypeNode.Fqn { Args: null, Name: "kotlin.Any" or "object" or "System.Object" } => true,
        _ => false,
    };
}
