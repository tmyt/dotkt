using System;
using System.Collections.Generic;
using DotKt.Bir;
using DotKt.Bir.Lowering;

// Select the CLR representation of Kotlin rendering and floating-point ordering.
// kotc supplies ordinary Kotlin operations, not helper names or representation
// hints. The owning source reader recovers lexical operand types before
// member/factory/type substitution consumes them. Equality is normalized at
// representation entry by KotlinEqualityLowering; primitive EQEQ remains owned
// by PrimitiveOperatorLowering.

// Shared recognition primitives used by BOTH this pass and PrimitiveOperatorLowering's EQEQ arm.
static partial class FaithfulHints
{
    static Func<BirCallStatic, BirCallStatic> _bindHelper;

    internal static void WithHelperBinding(Func<BirCallStatic, BirCallStatic> bind, Action lower)
    {
        var previous = _bindHelper;
        _bindHelper = bind;
        try { lower(); }
        finally { _bindHelper = previous; }
    }
    // Owner FQNs of the (unchanged) stdlib helpers.
    const string CollDefaults = "kotlin.collections.ClrCollectionDefaultsKt";
    const string MapDefaults = "kotlin.collections.ClrMapDefaultsKt";
    const string NestedToString = "kotlin.collections.ClrNestedToStringKt";
    const string Numbers = "kotlin.NumbersKt";
    const string Library = "kotlin.LibraryKt";

    public enum CollKind { Map, Set, Coll }

    // Exact Kotlin classifier identity owns the family. A name that merely contains Map/Set/List (Map.Entry,
    // ListIterator, or a future declaration) carries no collection semantics. Keep the public stdlib interfaces,
    // abstract bases, and concrete implementations explicit so adding a new declaration is a reviewed ABI decision.
    static readonly IReadOnlyDictionary<string, CollKind> CollectionKinds =
        new Dictionary<string, CollKind>(StringComparer.Ordinal)
        {
            ["kotlin.collections.Map"] = CollKind.Map,
            ["kotlin.collections.MutableMap"] = CollKind.Map,
            ["kotlin.collections.AbstractMap"] = CollKind.Map,
            ["kotlin.collections.AbstractMutableMap"] = CollKind.Map,
            ["kotlin.collections.HashMap"] = CollKind.Map,
            ["kotlin.collections.LinkedHashMap"] = CollKind.Map,

            ["kotlin.collections.Set"] = CollKind.Set,
            ["kotlin.collections.MutableSet"] = CollKind.Set,
            ["kotlin.collections.AbstractSet"] = CollKind.Set,
            ["kotlin.collections.AbstractMutableSet"] = CollKind.Set,
            ["kotlin.collections.HashSet"] = CollKind.Set,
            ["kotlin.collections.LinkedHashSet"] = CollKind.Set,

            ["kotlin.collections.Collection"] = CollKind.Coll,
            ["kotlin.collections.MutableCollection"] = CollKind.Coll,
            ["kotlin.collections.AbstractCollection"] = CollKind.Coll,
            ["kotlin.collections.AbstractMutableCollection"] = CollKind.Coll,
            ["kotlin.collections.List"] = CollKind.Coll,
            ["kotlin.collections.MutableList"] = CollKind.Coll,
            ["kotlin.collections.AbstractList"] = CollKind.Coll,
            ["kotlin.collections.AbstractMutableList"] = CollKind.Coll,
            ["kotlin.collections.ArrayList"] = CollKind.Coll,
            ["kotlin.collections.ArrayDeque"] = CollKind.Coll,
        };

    // The known collection/map @ClrTypeAlias FQNs whose STAR-projection / `Any?`-erasure has no usable generic BCL form
    // (invariant + reified on the CLR — a value-type-arg `Dictionary<int,int>` is NOT an `IDictionary<object,object>`).
    static readonly HashSet<string> StarProjectableColls = new(StringComparer.Ordinal)
    {
        "kotlin.collections.Collection", "kotlin.collections.MutableCollection",
        "kotlin.collections.List", "kotlin.collections.MutableList",
        "kotlin.collections.Set", "kotlin.collections.MutableSet",
        "kotlin.collections.Iterable", "kotlin.collections.MutableIterable",
        "kotlin.collections.Map", "kotlin.collections.MutableMap",
    };

    // True for a star-projected / `Any(?)`-erased collection SURFACE type (`Map<*,*>` / `List<*>` / an explicit
    // `as Map<Any?,Any?>`): a known collection alias whose every type-arg is `object`/`Any` (possibly nullable-wrapped).
    // Such a value can only be rendered non-generically — route it to clrElemToString(Any?), not the generic helpers.
    public static bool IsStarProjectedColl(TypeNode t)
    {
        if (Unwrap(t) is not TypeNode.Fqn f || f.Args is not { } args || args.Length == 0) return false;
        return StarProjectableColls.Contains(f.Name) && args.All(IsObjectArg);
    }

    static bool IsObjectArg(TypeNode a) => a switch
    {
        TypeNode.Nullable n => IsObjectArg(n.Of),
        TypeNode.Oblivious o => IsObjectArg(o.Of),
        TypeNode.Fqn { Args: null, Name: "object" or "kotlin.Any" } => true,
        _ => false,
    };

    // A collection HINT type -> (kind, type-args). Unwraps a nullable wrapper first (the receiver may be `List<Int>?`),
    // then recognizes only the exact stdlib classifier identities above. Null for every other type.
    public static (CollKind kind, TypeNode[] args)? ClassifyColl(TypeNode t)
    {
        if (Unwrap(t) is not TypeNode.Fqn f) return null;
        return CollectionKinds.TryGetValue(f.Name, out var kind)
            ? (kind, f.Args ?? Array.Empty<TypeNode>())
            : null;
    }

    public static TypeNode Unwrap(TypeNode t) => t is TypeNode.Nullable n ? n.Of : t;

    public static bool IsNullable(TypeNode t) => t is TypeNode.Nullable;

    // True iff the hint is the bare (non-null) primitive Fqn `name`. A nullable/reference operand fails because boxed
    // Double structural equality uses total order only when BOTH sides are statically non-null Double.
    public static bool IsNonNullFqn(TypeNode t, string name) => t is TypeNode.Fqn f && f.Name == name;

    // "Cast-stripped operand": drop a leading `{k:cast, type:<kotlin.Any/System.Object/object>, e:X}` (an IMPLICIT_CAST
    // to Any renders as such a node) so recognition sees the semantic operand below the representation-only box.
    // Returns the inner operand for transfer into the owning helper, else the original expression.
    public static IBirExpression StripAnyCast(IBirExpression value) => value is BirCast { E.HasValue: true } cast
        && IsAnyCastType(cast.Type.OrDefault()) ? cast.E.Value : value;

    static bool IsAnyCastType(TypeNode type) => type is TypeNode.Fqn { Name: "kotlin.Any" or "System.Object" or "object" };

    sealed record HelperSpecification(string Owner, string Method, TypeNode[] Signature, TypeNode[] TypeArguments = null,
        bool MapPreference = false);
    static TypeNode Any() => new TypeNode.Fqn("kotlin.Any");
    static TypeNode AnyNullable() => new TypeNode.Nullable(Any());
    static TypeNode TypeArgOr(TypeNode[] args, int index) => index < args.Length ? args[index] : Any();
    static HelperSpecification CollectionRenderer(CollKind kind, TypeNode[] args) => kind == CollKind.Map
        ? new(MapDefaults, "clrMapToString", new[] { Any(), AnyNullable() },
            new[] { TypeArgOr(args, 0), TypeArgOr(args, 1) }, MapPreference: true)
        : new(CollDefaults, "clrCollToString",
            new TypeNode[] { new TypeNode.Fqn("kotlin.collections.Collection", new TypeNode[] { new TypeNode.Tv("method", 0) }) },
            new[] { TypeArgOr(args, 0) });
    static HelperSpecification FloatHelper(string method)
    {
        TypeNode floating = new TypeNode.Fqn(method.Contains("Float", StringComparison.Ordinal) ? "kotlin.Float" : "kotlin.Double");
        return new(Numbers, method, new[] { floating, floating });
    }
    static HelperSpecification NullableRenderer() => new(Library, "toString", new[] { AnyNullable() });
    static HelperSpecification ElementRenderer() => new(NestedToString, "clrElemToString", new[] { AnyNullable() });

    // Double/Float total-order helpers on kotlin.NumbersKt (NO type-args). `method` = clrDoubleCompare/clrFloatCompare
    // (compareTo) or clrDoubleEquals/clrFloatEquals (boxed `==` / explicit `.equals`).
    internal static BirCallStatic FloatCall(string method, IBirExpression a, IBirExpression b)
        => Helper(FloatHelper(method), new[] { a, b });

}
