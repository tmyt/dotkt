using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// THE RUNTIME CLASSIFIER of an argument-abandoning collection projection — `x is List<*>`, `x as Map<*,*>`.
//
// A classifier is NOT a slot, and the two want opposite things. StarProjectionView answers "what type may this
// value be STORED as", and its answer is the LOOSEST sound one (the non-generic ancestor every instantiation is
// compatible with). An `is`/`as` asks "is this value one of these", and there the loosest answer is useless:
// `is Map<*,*>` lowered to `isinst IEnumerable` would say true of a `List` and of a `String`. So the classifier
// takes the TIGHTEST non-generic identity a value of the projected Kotlin type actually carries.
//
// WHY NON-GENERIC AT ALL: `is Collection<*>` lowered through the @ClrTypeAlias becomes a REIFIED
// `isinst IReadOnlyCollection<object>`, and reified generics have no value-type covariance (and IDictionary is
// invariant outright), so `List<int> is Collection<*>` answers FALSE and `x as Map<*,*>` throws InvalidCast — the
// check silently fails for every value-element collection. On the JVM both erase to a raw classifier and pass.
// The non-generic BCL interface is the classifier a `List<int>`/`Dictionary<int,int>` DOES carry regardless of
// element type. A concrete-arg generic `is` is a Kotlin compile error, so every `is Collection<...>` reaching here
// is necessarily projected — keying on the alias FQN is sufficient.
//
// The MEMBER half of the star projection is not here: it lives in MemberCallSubstitution's pre-Rule-2 override,
// which routes every member of an abandoning receiver to the `Any`-taking ClrStarProjection statics. This pass
// used to carry a second copy of that routing (only for a receiver that happened to be a `cast`), plus a
// downstream `Count` re-pointer; both were special cases of the general rule and were absorbed there.
//
// Runs before type lowering and emits final CLR tokens that pass through it unchanged. Non-reference builds only:
// the reference surface stays pure Kotlin.
//
// SET AND MUTABLESET ARE DELIBERATELY ABSENT. They used to map to the non-generic `System.Collections.ICollection`,
// which identifies a set in NEITHER direction: `setOf(1)` is a `HashSet<int>`, and HashSet<T> implements only the
// GENERIC ICollection<T>/ISet<T> (so a real set answered FALSE), while `List<T>` DOES implement the non-generic one
// (so `listOf(1) is Set<*>` answered TRUE — an unsound smart-cast, the worse of the two errors). Leaving them out
// keeps the reified `IReadOnlyCollection<object>` test, which is false for both, so the check is merely incomplete
// rather than wrong. A correct test needs an identity a Kotlin set HAS on the CLR, and it currently has none:
// `Set` is @ClrTypeAlias'd to the SAME `IReadOnlyCollection<T>` as `Collection` (and `MutableSet` to the same
// `ICollection<T>` as `MutableCollection`), so the two Kotlin types are ONE CLR type and no runtime check —
// reflection included, for user implementations as much as for HashSet — can separate them. Giving Set/MutableSet a
// distinct CLR identity is a stdlib collection-ABI decision, not a lowering one; see docs/dotkt-semantics.md §2
// (the star-projection corollary), which is where the star-projected collection lowering is written up.
static class StarProjectionClassifier
{
    // Kotlin generic collection alias -> the non-generic BCL interface a `List<int>`/`Dictionary<int,int>` carries
    // regardless of element type.
    static readonly Dictionary<string, string> NonGenericIface = new(StringComparer.Ordinal)
    {
        ["kotlin.collections.Collection"] = "System.Collections.ICollection",
        ["kotlin.collections.MutableCollection"] = "System.Collections.ICollection",
        ["kotlin.collections.List"] = "System.Collections.IList",
        ["kotlin.collections.MutableList"] = "System.Collections.IList",
        ["kotlin.collections.Iterable"] = "System.Collections.IEnumerable",
        ["kotlin.collections.MutableIterable"] = "System.Collections.IEnumerable",
        ["kotlin.collections.Map"] = "System.Collections.IDictionary",
        ["kotlin.collections.MutableMap"] = "System.Collections.IDictionary",
    };

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    // True for an argument-abandoning generic collection type: owner is a known collection alias and at least one
    // type arg is abandoned. A NULLABLE slot (`x is Collection<*>?`, `x as Map<*,*>?`) names the same classifier —
    // the `?` is carried by the node's own `nullMatches` (is) or by CLR reference nullability (cast), and dropping
    // it here is what lets the non-generic rewrite reach a nullable star test at all. Unwrap it first.
    static bool IsStarCollection(JsonNode slot, out string iface)
    {
        iface = null;
        var read = TypeJson.Read(slot);
        while (read is TypeNode.Nullable nn) read = nn.Of;
        if (read is not TypeNode.Fqn f) return false;
        if (!NonGenericIface.TryGetValue(f.Name, out iface)) return false;
        if (f.Args == null) return true;                            // raw / bare collection alias
        return f.Args.Any(IsObjectArg);
    }

    // An abandoned type arg: an explicit `*`, or `object`/`kotlin.Any`, possibly nullable/oblivious-wrapped
    // (`Map<*,*>` projects each arg to `Any?`, i.e. `{t:nullable,of:kotlin.Any}` post-#48).
    static bool IsObjectArg(TypeNode a) => a switch
    {
        TypeNode.Star => true,
        TypeNode.Nullable n => IsObjectArg(n.Of),
        TypeNode.Oblivious o => IsObjectArg(o.Of),
        TypeNode.Fqn { Args: null, Name: "object" or "kotlin.Any" } => true,
        _ => false,
    };

    public static void Apply(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            // Standalone star-projection `is`-test -> the non-generic interface (always safe: a boolean shape test).
            if (Str(obj["k"]) == "isInst" && IsStarCollection(obj["type"], out var ng))
                obj["type"] = TypeJson.Fqn(ng);
            // Star-projection `cast` (a smart-cast value flowing on, e.g. into `println(Any?)`, or an explicit
            // `as Map<*,*>`) -> the non-generic interface. Its generic form (`IDictionary<object,object>`) is
            // INVARIANT + reified on the CLR, so a value-type-arg `Dictionary<int,int>` does NOT implement it ->
            // castclass InvalidCast (the JVM erases both to `Map`, hiding it). The non-generic `IDictionary` it DOES
            // implement, and the resulting value inhabits the projection's own physical view without a further
            // conversion: every one of these interfaces derives from `System.Collections.IEnumerable`.
            if (Str(obj["k"]) == "cast" && IsStarCollection(obj["type"], out var castNg))
                obj["type"] = TypeJson.Fqn(castNg);
            foreach (var kv in obj) if (kv.Value != null) Apply(kv.Value);
        }
        else if (node is JsonArray arr)
            foreach (var it in arr) if (it != null) Apply(it);
    }
}
