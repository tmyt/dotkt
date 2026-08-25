using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// The TRANSFORM-SIDE twin of NullableGenericErasure: erase a nullable FUNCTION-TYPE return
// (`(T) -> R?`, kotc-tokenized `func:nullable:<ret>:<args>`) to a `Func<…, object>` slot. Rationale: the open
// stdlib view (`nullable:gp:R`) and a caller's value instantiation (`nullable:int`) must lower to the SAME
// delegate type or the passed delegate is reinterpreted through a foreign Invoke signature (Func<int,int> read
// as Func<int,object> — the il-collmore mapNotNull InvalidProgram / il-sort sortedBy AccessViolation). `object`
// is the one rep every instantiation agrees on: value/generic returns box (null stays a real null); a REFERENCE
// instantiation is never nullable-marked by kotc and keeps its bare Func<…, T>, which flows into the object slot
// via Func's `out TResult` covariance. Three coordinated rewrites:
//   1. every `func:` TOKEN whose return segment is `nullable:`-marked (param slots, call sig strings,
//      newDelegate/newClosure/delegateInvoke funcTypes, nested occurrences) — ret segment -> `object`;
//   2. the backing lambda method of an erased newDelegate/newClosure — its `ret` -> `object` (+ the return-value
//      expression types, mirroring NullableGenericErasure.RetypeReturns);
//   3. local dataflow repair where an erased delegateInvoke result lands in a typed var: a `gp:X` var is retyped
//      to `object` (it must still hold the null); a `nullable:V`/reference var keeps its type and the init is
//      wrapped in a `cast` (ilemit's universal unbox.any/castclass); a later var re-narrowing an object-retyped
//      local into a typed slot (the post-null-check `gp:R` copy) gets the same cast wrap.
// CATCH-CLAUSE WIDENING (bundle-6 ④): a Kotlin `catch (e: IndexOutOfBoundsException)` @ClrTypeAlias-es to a SINGLE .NET
// type, but .NET raises TWO unrelated out-of-range exceptions — `System.ArgumentOutOfRangeException` (List<T>.get_Item /
// most BCL collection indexers) and `System.IndexOutOfRangeException` (raw array access). Neither is a subtype of the
// other, so a single-type catch misses half the cases. Kotlin's semantics are "one IndexOutOfBoundsException catches
// any out-of-range access", so widen each such clause into TWO consecutive clauses (same body + var) covering both .NET
// types. Emits `clr:` tokens that pass through type-lowering unchanged. Keyed on the pure-Kotlin type name (runs before
// type lowering), so it is independent of whichever single .NET type the alias picks.
// STAR-PROJECTION IS-TEST (bundle-6 ④): `x is Collection<*>` / `is Map<*,*>` lowers (via the @ClrTypeAlias type map)
// to a REIFIED generic isinst — `isinst IReadOnlyCollection<object>` / `IDictionary<object,object>`. On .NET, reified
// generics have NO covariance on VALUE-type args (and IDictionary is invariant), so `List<int> is IReadOnlyCollection<object>`
// is FALSE — the check silently fails for every value-type collection. Kotlin's `is` on a star-projected type is a pure
// runtime shape test (the args are erased), so lower it to the NON-generic BCL interface, which a `List<int>`/`Dictionary<int,int>`
// DOES implement regardless of element type. A concrete-arg generic is-check is a Kotlin compile error, so every
// `is Collection<...>` here is necessarily `<*>` — keying on the alias FQN alone is sufficient. Only the isinst node's
// type token is rewritten (a Collection-typed VARIABLE keeps its generic form for member access). Runs before type
// lowering; emits `clr:` tokens that pass through unchanged. Non-ref only. (Set/MutableSet are intentionally absent:
// .NET HashSet<T> implements no non-generic collection interface beyond IEnumerable, so no faithful single token exists.)
// The COMPLETE star-projection lowering (bundle-6 `iscoll`). Lowering the isinst alone (Fix #6) made the is-test true
// for a value-type collection, but the guarded SMART-CAST member access (`(x as Collection<*>).size` in
// collectionSizeOrDefault) still castclassed the REIFIED `IReadOnlyCollection<object>` -> InvalidCast, regressing
// map/filter. The fix routes the WHOLE chain to the non-generic BCL interface: the `isinst`, the smart-cast `cast`,
// AND the member access on that star-cast (`.size` -> ICollection.Count, `.iterator()` -> IEnumerable.GetEnumerator,
// `[i]` -> IList.get_Item, `.contains` -> IList.Contains, `.isEmpty()` -> Count == 0). Runs BEFORE MemberCallSubstitution
// (so it sees the raw `callInstance get_size` on the kotlin.collections.* alias, not the already-substituted reified
// clrPropGet) and is gated on the APP build (attributeTopLevelOwner) — the ref/rt stdlib self-build keeps the reified
// form (its collectionSizeOrDefault is-test stays false -> the harmless capacity-hint default), which is exactly why
// this does NOT reintroduce the Fix #6 map/filter regression. A concrete-arg generic `is`-check is a Kotlin compile
// error, so every `is Collection<...>` is necessarily `<*>`; keying on the alias FQN is sufficient for the isinst,
// and the smart-cast + member rewrite is gated to all-`object` (star / erased) type args to leave a genuine
// `as List<String>` unchecked cast alone. Emits final CLR/`clr:` tokens that pass through type-lowering unchanged.
static class StarProjectionLowering
{
    const string RuntimeOwner = "DotKt.Runtime.CompilerServices.StarProjectionRuntimeKt";
    static readonly TypeNode Any = new TypeNode.Fqn("kotlin.Any");
    static readonly TypeNode AnyN = new TypeNode.Nullable(Any);
    static readonly TypeNode Bool = new TypeNode.Fqn("kotlin.Boolean");
    static readonly TypeNode Int = new TypeNode.Fqn("kotlin.Int");
    static readonly TypeNode Type = new TypeNode.Fqn("System.Type");

    // Kotlin generic collection alias -> the non-generic BCL interface a `List<int>`/`Dictionary<int,int>` implements
    // regardless of element type.
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
    static readonly Dictionary<string, string> NonGenericIface = new(StringComparer.Ordinal)
    {
        ["kotlin.collections.List"] = "System.Collections.IList",
        ["kotlin.collections.MutableList"] = "System.Collections.IList",
        ["kotlin.collections.Iterable"] = "System.Collections.IEnumerable",
        ["kotlin.collections.MutableIterable"] = "System.Collections.IEnumerable",
        ["kotlin.collections.Map"] = "System.Collections.IDictionary",
        ["kotlin.collections.MutableMap"] = "System.Collections.IDictionary",
    };

    // Classifiers whose operational aliases overlap. 0 = Collection, 1 = Set, 2 = MutableSet. MutableCollection keeps
    // its established non-generic ICollection classifier; #315 is the Collection-vs-Map and Set-vs-Collection hole.
    static readonly Dictionary<string, int> IdentityKind = new(StringComparer.Ordinal)
    {
        ["kotlin.collections.Collection"] = 0,
        ["kotlin.collections.Set"] = 1,
        ["kotlin.collections.MutableSet"] = 2,
    };

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    // True for a star-projected (or `object`-erased) generic collection type: owner is a known collection alias and
    // every type arg is `object`/`Any` (Kotlin allows only `<*>` in an is/as of these, so the args are always erased).
    // A NULLABLE slot (`x is Collection<*>?`, `x as Map<*,*>?`) names the same classifier — the `?` is carried by the
    // node's own `nullMatches` (is) or by CLR reference nullability (cast), and dropping it here is what lets the
    // non-generic rewrite below reach a nullable star test at all. Unwrap it before the classifier check.
    static bool IsStarCollection(JsonNode slot, out string iface)
    {
        iface = null;
        var read = TypeJson.Read(slot);
        while (read is TypeNode.Nullable nn) read = nn.Of;
        if (read is not TypeNode.Fqn f) return false;
        if (!NonGenericIface.TryGetValue(f.Name, out iface)) return false;
        if (f.Args == null) return true;                            // raw / bare collection alias
        return f.Args.All(IsObjectArg);
    }

    static bool IsIdentityCollection(JsonNode slot, out int kind, out bool nullable)
    {
        kind = -1;
        nullable = false;
        var read = TypeJson.Read(slot);
        while (read is TypeNode.Nullable n) { nullable = true; read = n.Of; }
        while (read is TypeNode.Oblivious o) read = o.Of;
        if (read is not TypeNode.Fqn f || !IdentityKind.TryGetValue(f.Name, out kind)) return false;
        return f.Args == null || f.Args.All(IsObjectArg);
    }

    // A star-projection/erased type arg: `object`/`kotlin.Any`, possibly nullable/oblivious-wrapped (`Map<*,*>` projects
    // each arg to `Any?`, i.e. `{t:nullable,of:kotlin.Any}` post-#48). Unwrap the wrappers before the bare-name check.
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
            // The overlapping Collection/Set classifiers use their compiler-owned nominal identity for emitted Kotlin
            // values and the BCL's real generic faces for BCL-backed values. The checked value remains the original
            // object; member access below projects only the operation it needs.
            if (Str(obj["k"]) == "callInstance"
                && IsIdentityCollection(obj["ownerType"], out _, out _)
                && obj["recv"] is JsonObject identityRecv && Str(identityRecv["k"]) == "cast"
                && IsIdentityCollection(identityRecv["type"], out var identityKind, out _)
                && LowerIdentityMember(obj, identityRecv, identityKind) is JsonObject identityMember)
            {
                Replace(obj, identityMember);
                foreach (var child in obj.Select(kv => kv.Value).Where(v => v != null).ToList()) Apply(child);
                return;
            }
            // Smart-cast member access: `callInstance` on a star-collection alias whose receiver is a `cast` to that
            // same star-collection -> a non-generic BCL member. Rewrite in place so the cast recv is lowered too.
            if (Str(obj["k"]) == "callInstance"
                && IsStarCollection(obj["ownerType"], out _)
                && obj["recv"] is JsonObject recv && Str(recv["k"]) == "cast"
                && IsStarCollection(recv["type"], out var recvIface)
                && LowerMember(obj, recv, recvIface) is JsonObject rewritten)
            {
                foreach (var kv in rewritten) obj[kv.Key] = kv.Value?.DeepClone();
                foreach (var stale in obj.Select(kv => kv.Key).Where(k => !rewritten.ContainsKey(k)).ToList())
                    obj.Remove(stale);
                // The rewritten node's recv/args are already final; recurse only into them (not the stale members).
                if (obj["recv"] != null) Apply(obj["recv"]);
                if (obj["args"] is JsonArray ra) foreach (var a in ra) if (a != null) Apply(a);
                return;
            }
            // Standalone star-projection `is`-test -> the non-generic interface (always safe: a boolean shape test).
            if (Str(obj["k"]) == "isInst" && IsStarCollection(obj["type"], out var ng))
                obj["type"] = TypeJson.Fqn(ng);
            // Standalone star-projection `cast` (a smart-cast value flowing on, e.g. into `println(Any?)`, or an
            // explicit `as Map<*,*>`) -> the non-generic interface. Its generic form (`IDictionary<object,object>`) is
            // INVARIANT + reified on the CLR, so a value-type-arg `Dictionary<int,int>` does NOT implement it ->
            // castclass InvalidCast (the JVM erases both to `Map`, hiding it). The non-generic `IDictionary` it DOES
            // implement covariantly, and a `<*>` value can only be used non-generically anyway. Mirrors the isInst branch.
            if (Str(obj["k"]) == "cast" && IsStarCollection(obj["type"], out var castNg))
                obj["type"] = TypeJson.Fqn(castNg);
            var kind = Str(obj["k"]);
            if (kind is "isInst" or "isInstRef" or "cast"
                && obj["e"] is JsonNode operand
                && IsIdentityCollection(obj["type"], out var classifierKind, out var nullable))
            {
                Replace(obj, LowerIdentityClassifier(kind, operand, classifierKind,
                    nullable || Flag(obj["nullMatches"])));
            }
            foreach (var kv in obj) if (kv.Value != null) Apply(kv.Value);
        }
        else if (node is JsonArray arr)
            foreach (var it in arr) if (it != null) Apply(it);
    }

    static JsonObject LowerIdentityClassifier(string nodeKind, JsonNode operand, int classifierKind, bool nullable)
    {
        var method = nodeKind switch
        {
            "isInst" when nullable => "starProjectionKotlinNullableCollectionIsInstance",
            "isInst" => "starProjectionKotlinCollectionIsInstance",
            "isInstRef" => "starProjectionKotlinCollectionSafeCast",
            "cast" when nullable => "starProjectionKotlinNullableCollectionCast",
            _ => "starProjectionKotlinCollectionCast",
        };
        var result = nodeKind == "isInst" ? Bool
            : nodeKind == "cast" && !nullable ? Any
            : AnyN;
        var first = classifierKind == 0
            ? "System.Collections.Generic.IReadOnlyCollection`1"
            : classifierKind == 1
                ? "System.Collections.Generic.IReadOnlySet`1"
                : "System.Collections.Generic.ISet`1";
        var second = classifierKind == 0
            ? "System.Collections.Generic.ICollection`1"
            : "System.Collections.Generic.ISet`1";
        return Call(method,
            new TypeNode[] { AnyN, Int, Type, Type, Type, Type }, result,
            operand.DeepClone(), ConstInt(classifierKind), ClassRef(first), ClassRef(second),
            ClassRef("System.Collections.Generic.IDictionary`2"),
            ClassRef("System.Collections.Generic.IReadOnlyDictionary`2"));
    }

    static JsonObject LowerIdentityMember(JsonObject call, JsonObject cast, int classifierKind)
    {
        var checkedReceiver = LowerIdentityClassifier("cast", cast["e"], classifierKind, nullable: false);
        var member = Str(call["method"]);
        var propertyAccess = Str(call["prop"]);
        JsonObject Count() => Call("starProjectionKotlinCollectionCount",
            new TypeNode[] { Any, Type, Type }, Int,
            checkedReceiver.DeepClone(), ClassRef("System.Collections.Generic.IReadOnlyCollection`1"),
            ClassRef("System.Collections.Generic.ICollection`1"));
        switch (member)
        {
            case "size" when propertyAccess == "get":
                return Count();
            case "isEmpty":
                return new JsonObject
                {
                    ["k"] = "binOp", ["op"] = "==", ["type"] = TypeJson.Write(Bool), ["lhs"] = Count(),
                    ["rhs"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Write(Int), ["value"] = 0 },
                };
            case "iterator":
                return new JsonObject
                {
                    ["k"] = "callStatic", ["owner"] = TypeJson.Fqn("kotlin.collections.ClrIteratorBridgeKt"),
                    ["method"] = "iteratorOverRawEnumerable",
                    ["args"] = new JsonArray(new JsonObject
                    {
                        ["k"] = "cast", ["type"] = TypeJson.Fqn("System.Collections.IEnumerable"),
                        ["e"] = checkedReceiver,
                    }),
                    ["ret"] = TypeJson.Write(new TypeNode.Fqn("kotlin.collections.Iterator",
                        new TypeNode[] { AnyN })),
                };
            default:
                return null;
        }
    }

    static JsonObject Call(string method, IReadOnlyList<TypeNode> signature, TypeNode result,
        params JsonNode[] args) => new()
    {
        ["k"] = "callStatic", ["owner"] = TypeJson.Write(new TypeNode.Fqn(RuntimeOwner)), ["method"] = method,
        ["sig"] = new JsonArray(signature.Select(TypeJson.Write).ToArray()), ["ret"] = TypeJson.Write(result),
        ["args"] = new JsonArray(args),
    };

    static JsonObject ClassRef(string openType) => new()
    {
        ["k"] = "classRef", ["type"] = TypeJson.Write(new TypeNode.Fqn(openType)),
    };

    static JsonObject ConstInt(int value) => new()
    {
        ["k"] = "const", ["type"] = TypeJson.Write(Int), ["value"] = value,
    };

    static bool Flag(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    static void Replace(JsonObject target, JsonObject replacement)
    {
        foreach (var key in target.Select(kv => kv.Key).ToList()) target.Remove(key);
        foreach (var pair in replacement.ToList())
        {
            replacement.Remove(pair.Key);
            target[pair.Key] = pair.Value;
        }
    }

    // Build the non-generic replacement for a star-cast member call. `iface` is the non-generic interface the receiver
    // is cast to. Returns null for an unmapped member (leave it reified — the guarding isinst stays whatever it is).
    static JsonObject LowerMember(JsonObject call, JsonObject cast, string iface)
    {
        var recvInner = cast["e"];
        JsonObject CastTo(string toIface) => new() { ["k"] = "cast", ["type"] = TypeJson.Fqn(toIface), ["e"] = recvInner.DeepClone() };
        var args = call["args"] as JsonArray;
        var member = Str(call["method"]);
        var propertyAccess = Str(call["prop"]);
        switch (member)
        {
            case "size" when propertyAccess == "get":
                // `.size` -> ICollection/IList/IDictionary.Count.
                return new JsonObject { ["k"] = "clrPropGet", ["type"] = TypeJson.Fqn(iface), ["name"] = "Count", ["ret"] = TypeJson.Fqn("System.Int32"), ["static"] = false, ["recv"] = CastTo(iface) };
            case "isEmpty":
                // `.isEmpty()` -> Count == 0 (non-generic interfaces expose no IsEmpty).
                return new JsonObject
                {
                    ["k"] = "binOp",
                    ["op"] = "==",
                    ["type"] = TypeJson.Fqn("System.Boolean"),
                    ["lhs"] = new JsonObject { ["k"] = "clrPropGet", ["type"] = TypeJson.Fqn(iface), ["name"] = "Count", ["ret"] = TypeJson.Fqn("System.Int32"), ["static"] = false, ["recv"] = CastTo(iface) },
                    ["rhs"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("System.Int32"), ["value"] = 0 },
                };
            case "iterator":
                // `.iterator()` -> the rt bridge `ClrIteratorBridgeKt.iteratorOverRawEnumerable` (#74b(ii)), NOT a raw
                // `IEnumerable.GetEnumerator()` clrInstance: the consumer var this call initializes stays declared
                // `kotlin.collections.Iterator<Any?>` (StarProjectionLowering never touches that decl slot), and
                // IteratorConsumerNormalization re-points its hasNext/next dispatch at the REAL referenced generic
                // `kotlin.collections.Iterator<E>` interface — Kotlin's `hasNext` is idempotent while `MoveNext` is
                // NOT, so a raw IEnumerator can never correctly BACK that dispatch directly. The bridge's
                // `KotlinIteratorOverRawEnumerator` DOES implement the real `Iterator<Any?>`, closing the gap: the
                // owner FQN starts with "kotlin." so IteratorConsumerNormalization's existing re-typing recognizes it
                // exactly like the generic `iteratorOverEnumerable` bridge.
                return new JsonObject { ["k"] = "callStatic", ["owner"] = TypeJson.Fqn("kotlin.collections.ClrIteratorBridgeKt"), ["method"] = "iteratorOverRawEnumerable", ["args"] = new JsonArray { CastTo("System.Collections.IEnumerable") }, ["ret"] = TypeJson.Write(new TypeNode.Fqn("kotlin.collections.Iterator", new TypeNode[] { new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")) })) };
            case "get":
            case "get_Item":
                // `list[i]` -> IList.get_Item(int) (returns object == Any); `map[key]` -> IDictionary.get_Item(object)
                // (#74a — null-on-missing, matching Kotlin `Map.get`'s null-on-missing exactly; both are returned
                // object == Any(?)).
                if (args == null || args.Count < 1) return null;
                if (iface == "System.Collections.IList")
                    return new JsonObject { ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.Collections.IList"), ["method"] = "get_Item", ["argTypes"] = new JsonArray { TypeJson.Fqn("System.Int32") }, ["ret"] = TypeJson.Fqn("System.Object"), ["recv"] = CastTo("System.Collections.IList"), ["args"] = new JsonArray { args[0].DeepClone() } };
                if (iface == "System.Collections.IDictionary")
                    return new JsonObject { ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.Collections.IDictionary"), ["method"] = "get_Item", ["argTypes"] = new JsonArray { TypeJson.Fqn("System.Object") }, ["ret"] = TypeJson.Fqn("System.Object"), ["recv"] = CastTo("System.Collections.IDictionary"), ["args"] = new JsonArray { args[0].DeepClone() } };
                return null;
            case "contains":
                // `list.contains(e)` -> IList.Contains(object) (only the non-generic IList carries a Contains).
                if (args == null || args.Count < 1 || iface != "System.Collections.IList") return null;
                return new JsonObject { ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.Collections.IList"), ["method"] = "Contains", ["argTypes"] = new JsonArray { TypeJson.Fqn("System.Object") }, ["ret"] = TypeJson.Fqn("System.Boolean"), ["recv"] = CastTo("System.Collections.IList"), ["args"] = new JsonArray { args[0].DeepClone() } };
            case "containsKey":
                // `map.containsKey(k)` -> IDictionary.Contains(object) (#74a).
                if (args == null || args.Count < 1 || iface != "System.Collections.IDictionary") return null;
                return new JsonObject { ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.Collections.IDictionary"), ["method"] = "Contains", ["argTypes"] = new JsonArray { TypeJson.Fqn("System.Object") }, ["ret"] = TypeJson.Fqn("System.Boolean"), ["recv"] = CastTo("System.Collections.IDictionary"), ["args"] = new JsonArray { args[0].DeepClone() } };
            default:
                return null;
        }
    }
}
