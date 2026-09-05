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
// STAR-PROJECTION COLLECTION CLASSIFIERS. List/Map/Iterable and MutableCollection have faithful non-generic BCL
// faces, so their `is` test and compiler-generated smart cast use those directly. Collection/Set/MutableSet do not:
// their operational aliases overlap, HashSet<T> has no non-generic collection face, and Dictionary/arrays expose CLR
// collection faces without being Kotlin Collections. Their `is` test is therefore a bir2cir-authored composite:
// compiler-owned nominal classifiers for emitted Kotlin implementations plus the actual generic BCL faces for BCL
// values, with dictionary/array exclusions. The following smart-cast's size/isEmpty/iterator remains on the original
// object; size dispatch uses the existing exact-token reflection runtime. Explicit standalone `as/as?` existential
// storage is not widened here. App build only, before MemberCallSubstitution while the Kotlin owner is still visible.
static class StarProjectionLowering
{
    internal const string ProjectedCollectionMarker = "dotktProjectedCollection";
    const string RuntimeOwner = "DotKt.Runtime.CompilerServices.StarProjectionRuntimeKt";
    static readonly TypeNode Any = new TypeNode.Fqn("kotlin.Any");
    static readonly TypeNode AnyN = new TypeNode.Nullable(Any);
    static readonly TypeNode Bool = new TypeNode.Fqn("kotlin.Boolean");
    static readonly TypeNode Int = new TypeNode.Fqn("kotlin.Int");
    static readonly TypeNode String = new TypeNode.Fqn("kotlin.String");
    static readonly TypeNode Type = new TypeNode.Fqn("System.Type");
    static readonly TypeNode TypeN = new TypeNode.Nullable(Type);
    public static bool UsedRuntimeFallback { get; private set; }

    // Kotlin generic aliases with a faithful non-generic BCL classifier, regardless of element type.
    static readonly Dictionary<string, string> NonGenericIface = new(StringComparer.Ordinal)
    {
        ["kotlin.collections.MutableCollection"] = "System.Collections.ICollection",
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
        while (true)
        {
            if (read is TypeNode.Nullable n) { nullable = true; read = n.Of; continue; }
            if (read is TypeNode.Oblivious o) { read = o.Of; continue; }
            break;
        }
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

    public static void Apply(JsonNode node, ReferenceMetadataIndex refs)
    {
        if (node is JsonObject obj)
        {
            AdaptProjectedCollectionArguments(obj);
            // The overlapping Collection/Set classifiers use their compiler-owned nominal identity for emitted Kotlin
            // values and the BCL's real generic faces for BCL-backed values. The checked value remains the original
            // object; member access below projects only the operation it needs.
            if (Str(obj["k"]) == "callInstance"
                && IsIdentityCollection(obj["ownerType"], out _, out _)
                && obj["recv"] is JsonObject identityRecv && Str(identityRecv["k"]) == "cast"
                && IsIdentityCollection(identityRecv["type"], out var identityKind, out _)
                && LowerIdentityMember(obj, identityRecv, identityKind, refs) is JsonObject identityMember)
            {
                UsedRuntimeFallback = true;
                Replace(obj, identityMember);
                foreach (var child in obj.Select(kv => kv.Value).Where(v => v != null).ToList()) Apply(child, refs);
                return;
            }
            // Smart-cast member access: `callInstance` on a star-collection alias whose receiver is a `cast` to that
            // same star-collection -> a non-generic BCL member. Rewrite in place so the cast recv is lowered too.
            if (Str(obj["k"]) == "callInstance"
                && IsStarCollection(obj["ownerType"], out _)
                && obj["recv"] is JsonObject recv && Str(recv["k"]) == "cast"
                && IsStarCollection(recv["type"], out var recvIface)
                && LowerMember(obj, recv, recvIface, IsMutableStarCollection(obj["ownerType"])) is JsonObject rewritten)
            {
                foreach (var kv in rewritten) obj[kv.Key] = kv.Value?.DeepClone();
                foreach (var stale in obj.Select(kv => kv.Key).Where(k => !rewritten.ContainsKey(k)).ToList())
                    obj.Remove(stale);
                // The rewritten node's recv/args are already final; recurse only into them (not the stale members).
                if (obj["recv"] != null) Apply(obj["recv"], refs);
                if (obj["args"] is JsonArray ra) foreach (var a in ra) if (a != null) Apply(a, refs);
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
            {
                obj["type"] = TypeJson.Fqn(castNg);
                // MemberCallSubstitution runs after this pass. Preserve the semantic fact that a local initialized
                // from this physical non-generic facade came from a star-projected Kotlin collection; the facade's
                // element type is otherwise intentionally absent and a later local read cannot reconstruct it.
                obj[ProjectedCollectionMarker] = true;
            }
            var kind = Str(obj["k"]);
            if (kind == "isInst"
                && obj["e"] is JsonNode operand
                && IsIdentityCollection(obj["type"], out var classifierKind, out var nullable))
            {
                UsedRuntimeFallback = true;
                Replace(obj, LowerIdentityClassifier(kind, operand, classifierKind,
                    nullable || Flag(obj["nullMatches"])));
            }
            foreach (var kv in obj) if (kv.Value != null) Apply(kv.Value, refs);
        }
        else if (node is JsonArray arr)
            foreach (var it in arr) if (it != null) Apply(it, refs);
    }

    // Kotlin collection covariance permits a star-projected value to fill a Collection<T> parameter selected by
    // ordinary overload resolution. The CLR call boundary is reified: a value-element collection in particular
    // cannot be cast to IReadOnlyCollection<object>. Materialize the compiler/runtime-owned live view while both the
    // source projection and the selected Kotlin parameter are still explicit. This is a rule for every Collection
    // argument edge, not for any particular extension such as `plus`.
    static void AdaptProjectedCollectionArguments(JsonObject call)
    {
        var kind = Str(call["k"]);
        if (kind is not ("callStatic" or "callInstance")) return;
        if (call["sig"] is not JsonArray signature || call["args"] is not JsonArray arguments
            || signature.Count != arguments.Count) return;
        var methodArguments = call["typeArgs"] is JsonArray typeArgs
            ? typeArgs.Select(TypeJson.Read).ToArray()
            : Array.Empty<TypeNode>();
        if (methodArguments.Any(type => type == null)) return;

        for (var index = 0; index < signature.Count; index++)
        {
            if (arguments[index] is not JsonObject argument || !IsProjectedCollectionValue(argument)) continue;
            var parameter = TypeJson.Read(signature[index]);
            if (parameter == null) continue;
            var closed = FBoundStarProjectionErasure.SubstituteMethodTypeArguments(parameter, methodArguments);
            if (closed is not TypeNode.Fqn { Name: "kotlin.collections.Collection" } collection
                || collection.Args is not { Length: 1 } elementArgs || ContainsProjection(elementArgs[0])) continue;
            arguments[index] = new JsonObject
            {
                ["k"] = "callStatic",
                ["owner"] = TypeJson.Fqn("kotlin.collections.ClrCollectionDefaultsKt"),
                ["method"] = "clrProjectedCollectionView",
                ["sig"] = new JsonArray(TypeJson.Write(Any)),
                ["typeArgs"] = new JsonArray(TypeJson.Write(elementArgs[0])),
                ["ret"] = TypeJson.Write(closed),
                ["args"] = new JsonArray(argument.DeepClone()),
            };
        }
    }

    static bool IsProjectedCollectionValue(JsonObject expression)
    {
        foreach (var slot in new[] { expression["type"], expression["sty"] })
            if (slot != null && (IsStarCollection(slot, out _) || IsIdentityCollection(slot, out _, out _)))
                return true;
        return false;
    }

    static bool ContainsProjection(TypeNode type) => type switch
    {
        TypeNode.Star or TypeNode.Projection => true,
        TypeNode.Fqn f => f.Args?.Any(ContainsProjection) == true,
        TypeNode.Nullable nullable => ContainsProjection(nullable.Of),
        TypeNode.Oblivious oblivious => ContainsProjection(oblivious.Of),
        TypeNode.Array array => ContainsProjection(array.Elem),
        _ => false,
    };

    static JsonObject LowerIdentityClassifier(string nodeKind, JsonNode operand, int classifierKind, bool nullable)
    {
        var method = nodeKind switch
        {
            "isInst" when nullable => "starProjectionKotlinNullableCollectionIsInstance",
            "isInst" => "starProjectionKotlinCollectionIsInstance",
            _ => "starProjectionKotlinCollectionCast",
        };
        var result = nodeKind == "isInst" ? Bool : Any;
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

    static JsonObject LowerIdentityMember(JsonObject call, JsonObject cast, int classifierKind,
        ReferenceMetadataIndex refs)
    {
        var checkedReceiver = LowerIdentityClassifier("cast", cast["e"], classifierKind, nullable: false);
        var member = Str(call["method"]);
        var propertyAccess = Str(call["prop"]);
        JsonObject Count() => ExactCount(checkedReceiver.DeepClone(), refs);
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
                if (classifierKind == 2)
                    return new JsonObject
                    {
                        ["k"] = "callStatic", ["owner"] = TypeJson.Fqn("kotlin.collections.ClrCollectionDefaultsKt"),
                        ["method"] = "clrMutableIteratorErased",
                        ["sig"] = new JsonArray(TypeJson.Write(Any)),
                        ["args"] = new JsonArray(checkedReceiver),
                        ["ret"] = TypeJson.Write(new TypeNode.Fqn("kotlin.collections.MutableIterator",
                            new TypeNode[] { AnyN })),
                    };
                return new JsonObject
                {
                    ["k"] = "callStatic", ["owner"] = TypeJson.Fqn("kotlin.collections.ClrIteratorBridgeKt"),
                    ["method"] = "iteratorOverRawEnumerable",
                    ["sig"] = new JsonArray(TypeJson.Write(Any)),
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

    // The Kotlin `size` slot physically lives on IReadOnlyCollection<T>. Supply that exact BCL declaration identity to
    // the existing star dispatcher, which maps it onto the receiver's unique closed view, unwraps getter exceptions,
    // and refuses multiple constructed witnesses instead of choosing reflection order.
    static JsonObject ExactCount(JsonNode receiver, ReferenceMetadataIndex refs)
    {
        const string ownerName = "System.Collections.Generic.IReadOnlyCollection";
        var open = refs.ResolveNetType(ownerName, 1)
            ?? throw new InvalidOperationException("bir2cir: cannot resolve IReadOnlyCollection<> for collection size");
        if (open.IsConstructedGenericType) open = open.GetGenericTypeDefinition();
        var getters = open.GetMethods().Where(m => m.Name == "get_Count" && m.GetParameters().Length == 0).ToList();
        if (getters.Count != 1)
            throw new InvalidOperationException($"bir2cir: expected one IReadOnlyCollection<>.get_Count, got {getters.Count}");
        var getter = getters[0];
        var emptyStrings = NewArray(String);
        var emptyTypes = NewArray(Type);
        var emptyArgs = NewArray(AnyN);
        var invoke = Call("starProjectionInvoke",
            new TypeNode[] { Any, Type, TypeN, Int, String, Int, new TypeNode.Array(String),
                new TypeNode.Array(Type), new TypeNode.Array(AnyN) },
            AnyN,
            receiver, ClassRef("System.Collections.Generic.IReadOnlyCollection`1"), Null(TypeN),
            ConstInt(getter.MetadataToken), ConstString("get_Count"), ConstInt(0),
            emptyStrings, emptyTypes, emptyArgs);
        return new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Write(Int), ["e"] = invoke };
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

    static JsonObject ConstString(string value) => new()
    {
        ["k"] = "const", ["type"] = TypeJson.Write(String), ["value"] = value,
    };

    static JsonObject Null(TypeNode type) => new()
    {
        ["k"] = "const", ["type"] = TypeJson.Write(type), ["value"] = null,
    };

    static JsonObject NewArray(TypeNode element) => new()
    {
        ["k"] = "newArray", ["elem"] = TypeJson.Write(element), ["elems"] = new JsonArray(),
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
    static bool IsMutableStarCollection(JsonNode slot)
    {
        var read = TypeJson.Read(slot);
        while (read is TypeNode.Nullable n) read = n.Of;
        while (read is TypeNode.Oblivious o) read = o.Of;
        return read is TypeNode.Fqn f && f.Name is "kotlin.collections.MutableIterable"
            or "kotlin.collections.MutableCollection" or "kotlin.collections.MutableList";
    }

    static JsonObject LowerMember(JsonObject call, JsonObject cast, string iface, bool mutable)
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
                if (mutable)
                    // Keep the original star cast observable before entering the erased helper. Passing recvInner
                    // directly would let e.g. `(aSet as MutableList<*>).iterator()` succeed merely because the set
                    // is enumerable, even though the explicit MutableList cast must fail.
                    return new JsonObject { ["k"] = "callStatic", ["owner"] = TypeJson.Fqn("kotlin.collections.ClrCollectionDefaultsKt"), ["method"] = "clrMutableIteratorErased", ["sig"] = new JsonArray(TypeJson.Write(Any)), ["args"] = new JsonArray { CastTo(iface) }, ["ret"] = TypeJson.Write(new TypeNode.Fqn("kotlin.collections.MutableIterator", new TypeNode[] { new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")) })) };
                // `.iterator()` -> the rt bridge `ClrIteratorBridgeKt.iteratorOverRawEnumerable` (#74b(ii)), NOT a raw
                // `IEnumerable.GetEnumerator()` clrInstance: the consumer var this call initializes stays declared
                // `kotlin.collections.Iterator<Any?>` (StarProjectionLowering never touches that decl slot), and
                // IteratorConsumerNormalization re-points its hasNext/next dispatch at the REAL referenced generic
                // `kotlin.collections.Iterator<E>` interface — Kotlin's `hasNext` is idempotent while `MoveNext` is
                // NOT, so a raw IEnumerator can never correctly BACK that dispatch directly. The bridge's
                // `KotlinIteratorOverRawEnumerator` DOES implement the real `Iterator<Any?>`, closing the gap: the
                // owner FQN starts with "kotlin." so IteratorConsumerNormalization's existing re-typing recognizes it
                // exactly like the generic `iteratorOverEnumerable` bridge.
                return new JsonObject { ["k"] = "callStatic", ["owner"] = TypeJson.Fqn("kotlin.collections.ClrIteratorBridgeKt"), ["method"] = "iteratorOverRawEnumerable", ["sig"] = new JsonArray(TypeJson.Write(Any)), ["args"] = new JsonArray { CastTo("System.Collections.IEnumerable") }, ["ret"] = TypeJson.Write(new TypeNode.Fqn("kotlin.collections.Iterator", new TypeNode[] { new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")) })) };
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
