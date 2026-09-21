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
// STAR-PROJECTION COLLECTION CLASSIFIERS. Map/Iterable use non-generic BCL
// faces for their physical tests, after KotlinCollectionClassifierLowering's independent nominal guard preserves
// Kotlin classifier identity and mutability. Collection/Set/List need additional physical discrimination:
// their operational aliases overlap, HashSet<T> has no non-generic collection face, and Dictionary/arrays expose CLR
// collection faces without being Kotlin Collections. Their `is` test is therefore a bir2cir-authored composite:
// compiler-owned nominal classifiers for emitted Kotlin implementations plus the actual generic BCL faces for BCL
// values. The common eligibility guard owns storage exclusions; this physical test must not discard independent
// List/Set contracts merely because a dictionary interface is also present. Kotlin List implementations need not
// implement the non-generic IList face. The following smart-cast's size/isEmpty/iterator remains on the original
// object; size dispatch uses the existing exact-token reflection runtime. Collection/Set/List `is/as?/as` share this
// classifier, without selecting one element closure from an existential collection. App build only, before
// MemberCallSubstitution while the Kotlin owner is still visible.
static class StarProjectionLowering
{
    internal const string ProjectedCollectionMarker = "dotktProjectedCollection";
    const string RuntimeOwner = "DotKt.Runtime.CompilerServices.StarProjectionRuntimeKt";
    static readonly TypeNode Any = new TypeNode.Fqn("kotlin.Any");
    static readonly TypeNode AnyN = new TypeNode.Nullable(Any);
    static readonly TypeNode Bool = new TypeNode.Fqn("kotlin.Boolean");
    static readonly TypeNode Int = new TypeNode.Fqn("kotlin.Int");
    static readonly TypeNode Type = new TypeNode.Fqn("System.Type");
    public static bool UsedRuntimeFallback { get; private set; }

    // Kotlin generic aliases with a faithful non-generic BCL classifier, regardless of element type.
    static readonly Dictionary<string, string> NonGenericIface = new(StringComparer.Ordinal)
    {
        ["kotlin.collections.Iterable"] = "System.Collections.IEnumerable",
        ["kotlin.collections.MutableIterable"] = "System.Collections.IEnumerable",
        ["kotlin.collections.Map"] = "System.Collections.IDictionary",
        ["kotlin.collections.MutableMap"] = "System.Collections.IDictionary",
    };

    internal static bool IsNonGenericCollectionFacade(TypeNode type) =>
        type is TypeNode.Fqn { Args: null } fqn && NonGenericIface.Values.Contains(fqn.Name);

    // Classifiers that need nominal or multiple physical faces rather than one raw CLR interface.
    static readonly Dictionary<string, int> IdentityKind = new(StringComparer.Ordinal)
    {
        ["kotlin.collections.Collection"] = 0,
        ["kotlin.collections.Set"] = 1,
        ["kotlin.collections.MutableSet"] = 2,
        ["kotlin.collections.List"] = 3,
        ["kotlin.collections.MutableList"] = 4,
        ["kotlin.collections.MutableCollection"] = 5,
    };

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    // Recognize star-projected and object-element collection applications. Any/Any? arguments can
    // also be concrete source types; value-producing casts must retain their physical result type.
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
        var closedViews = ForeignStarProjectionBinding.CollectClosedViewHints(new[] { node }, refs);
        Apply(node, refs, closedViews);
    }

    static void Apply(JsonNode node, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, TypeNode.Fqn> closedViews)
    {
        if (node is JsonObject obj)
        {
            AdaptProjectedCollectionArguments(obj);
            if (Str(obj["k"]) == "callInstance"
                && IsIdentityCollection(obj["ownerType"], out var collectionKind, out _)
                && collectionKind is 0 or 1 or 2 or 5
                && Str(obj["method"]) == "size" && Str(obj["prop"]) == "get"
                && LowerCollectionCount(obj, collectionKind, closedViews) is JsonObject collectionCount)
            {
                UsedRuntimeFallback = true;
                Replace(obj, collectionCount);
                foreach (var child in obj.Select(kv => kv.Value).Where(v => v != null).ToList()) Apply(child, refs, closedViews);
                return;
            }
            if (Str(obj["k"]) == "callInstance"
                && IsIdentityCollection(obj["ownerType"], out var listKind, out _)
                && listKind is 3 or 4
                && LowerListMember(obj, closedViews) is JsonObject listMember)
            {
                UsedRuntimeFallback = true;
                Replace(obj, listMember);
                foreach (var child in obj.Select(kv => kv.Value).Where(v => v != null).ToList()) Apply(child, refs, closedViews);
                return;
            }
            // The overlapping Collection/Set classifiers use their compiler-owned nominal identity for emitted Kotlin
            // values and the BCL's real generic faces for BCL-backed values. The checked value remains the original
            // object; member access below projects only the operation it needs.
            if (Str(obj["k"]) == "callInstance"
                && IsIdentityCollection(obj["ownerType"], out _, out _)
                && obj["recv"] is JsonObject identityRecv && Str(identityRecv["k"]) == "cast"
                && IsIdentityCollection(identityRecv["type"], out var identityKind, out _)
                && !HasConcreteTypeArguments(identityRecv["type"])
                && LowerIdentityMember(obj, identityRecv, identityKind) is JsonObject identityMember)
            {
                UsedRuntimeFallback = true;
                Replace(obj, identityMember);
                foreach (var child in obj.Select(kv => kv.Value).Where(v => v != null).ToList()) Apply(child, refs, closedViews);
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
                if (obj["recv"] != null) Apply(obj["recv"], refs, closedViews);
                if (obj["args"] is JsonArray ra) foreach (var a in ra) if (a != null) Apply(a, refs, closedViews);
                return;
            }
            // Iterable safe casts use the same erased enumeration protocol as their tests. They need no
            // unique constructed view: an object may expose several IEnumerable<T> faces. Other collection
            // safe casts keep their existing generic view (e.g. generic-only dictionaries have no raw IDictionary).
            if (IsStarCollection(obj["type"], out var ng)
                && (Str(obj["k"]) == "isInst"
                    || Str(obj["k"]) == "isInstRef" && ng == "System.Collections.IEnumerable"))
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
            if (kind is ("isInst" or "isInstRef" or "cast")
                && obj["e"] is JsonNode operand
                && IsIdentityCollection(obj["type"], out var classifierKind, out var nullable))
            {
                UsedRuntimeFallback = true;
                Replace(obj, LowerIdentityClassifier(kind, operand, classifierKind,
                    nullable || Flag(obj["nullMatches"]), obj["type"]));
            }
            foreach (var kv in obj) if (kv.Value != null) Apply(kv.Value, refs, closedViews);
        }
        else if (node is JsonArray arr)
            foreach (var it in arr) if (it != null) Apply(it, refs, closedViews);
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
            var core = StripOuterWrappers(closed);
            if (core is not TypeNode.Fqn collection || collection.Args is not { Length: 1 } elementArgs
                || ContainsProjection(elementArgs[0])) continue;
            var sourceSet = new[] { argument["type"], argument["sty"] }
                .Select(TypeJson.Read).Where(type => type != null).Select(StripOuterWrappers)
                .OfType<TypeNode.Fqn>().FirstOrDefault(type =>
                    type.Name is "kotlin.collections.Set" or "kotlin.collections.MutableSet");
            var setArgument = collection.Name == "kotlin.collections.Set"
                || sourceSet != null && collection.Name is "kotlin.collections.Collection" or "kotlin.collections.Iterable";
            // Keep a concrete witness when its storage already supplies the requested family. MutableSet uses
            // ICollection<T>, which supplies IEnumerable<T> but need not supply readonly Collection storage.
            if (setArgument && sourceSet?.Args is { Length: > 0 } && !ContainsProjection(sourceSet)
                && (sourceSet.Name == "kotlin.collections.Set" || collection.Name == "kotlin.collections.Iterable")) continue;
            var helper = collection.Name switch
            {
                "kotlin.collections.Iterable" => "clrProjectedIterableView",
                "kotlin.collections.MutableIterable" => "clrProjectedMutableIterableView",
                "kotlin.collections.Collection" => "clrProjectedCollectionView",
                "kotlin.collections.Set" => "clrProjectedSetView",
                "kotlin.collections.List" => "clrProjectedListView",
                _ => null,
            };
            if (helper == null) continue;
            var helperReturn = closed;
            var helperSignature = new JsonArray(TypeJson.Write(Any));
            var helperArguments = new JsonArray(argument.DeepClone());
            if (setArgument)
            {
                var nullable = closed is TypeNode.Nullable or TypeNode.Oblivious;
                var iterable = collection.Name == "kotlin.collections.Iterable";
                helper = iterable
                    ? nullable ? "clrProjectedNullableSetIterableView" : "clrProjectedSetIterableView"
                    : nullable ? "clrProjectedNullableSetView" : "clrProjectedSetView";
                helperReturn = new TypeNode.Fqn(iterable ? "kotlin.collections.Iterable" : "kotlin.collections.Set", elementArgs);
                if (nullable) helperReturn = new TypeNode.Nullable(helperReturn);
                helperSignature[0] = TypeJson.Write(nullable ? AnyN : Any);
                helperSignature.Add(TypeJson.Write(Any));
                helperArguments.Add(new JsonObject
                {
                    ["k"] = "classRef",
                    ["type"] = TypeJson.Write(new TypeNode.Fqn(
                        iterable ? "kotlin.collections.Iterable" : "kotlin.collections.Collection", elementArgs)),
                });
            }
            arguments[index] = new JsonObject
            {
                ["k"] = "callStatic",
                ["owner"] = TypeJson.Fqn("kotlin.collections.ClrCollectionDefaultsKt"),
                ["method"] = helper,
                ["sig"] = helperSignature,
                ["typeArgs"] = new JsonArray(TypeJson.Write(elementArgs[0])),
                ["ret"] = TypeJson.Write(helperReturn),
                ["args"] = helperArguments,
            };
        }
    }

    static TypeNode StripOuterWrappers(TypeNode type) => type switch
    {
        TypeNode.Nullable nullable => StripOuterWrappers(nullable.Of),
        TypeNode.Oblivious oblivious => StripOuterWrappers(oblivious.Of),
        _ => type,
    };

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

    static bool HasConcreteTypeArguments(JsonNode slot) =>
        StripOuterWrappers(TypeJson.Read(slot)) is TypeNode.Fqn { Args.Length: > 0 } type
        && !ContainsProjection(type);

    static JsonObject LowerIdentityClassifier(string nodeKind, JsonNode operand, int classifierKind, bool nullable,
        JsonNode target)
    {
        var method = nodeKind switch
        {
            "isInst" when nullable => "starProjectionKotlinNullableCollectionIsInstance",
            "isInst" => "starProjectionKotlinCollectionIsInstance",
            "isInstRef" => "starProjectionKotlinCollectionSafeCast",
            "cast" when nullable => "starProjectionKotlinNullableCollectionCast",
            _ => "starProjectionKotlinCollectionCast",
        };
        var result = nodeKind == "isInst" ? Bool : nodeKind == "isInstRef" || nullable ? AnyN : Any;
        var first = classifierKind switch
        {
            0 => "System.Collections.Generic.IReadOnlyCollection`1",
            1 => "System.Collections.Generic.IReadOnlySet`1",
            2 => "System.Collections.Generic.ISet`1",
            3 => "System.Collections.Generic.IReadOnlyList`1",
            4 => "System.Collections.Generic.IList`1",
            5 => "System.Collections.Generic.ICollection`1",
            _ => throw new InvalidOperationException("Unknown collection classifier"),
        };
        var second = classifierKind switch
        {
            0 or 5 => "System.Collections.Generic.ICollection`1",
            3 or 4 => "System.Collections.Generic.IList`1",
            _ => "System.Collections.Generic.ISet`1",
        };
        var call = Call(method,
            new TypeNode[] { AnyN, Int, Type, Type }, result,
            operand.DeepClone(), ConstInt(classifierKind), ClassRef(first), ClassRef(second));
        // Any is a concrete CLR type argument, not a star. Its typed interface result still needs
        // the original physical cast after the Kotlin classifier guard (safe casts stay safe).
        if (nodeKind != "isInst" && StripOuterWrappers(TypeJson.Read(target)) is TypeNode.Fqn { Args.Length: > 0 } type
            && !ContainsProjection(type))
            return new JsonObject { ["k"] = nodeKind, ["type"] = target.DeepClone(), ["e"] = call };
        if (nodeKind != "isInst") call[ProjectedCollectionMarker] = true;
        return call;
    }

    static JsonObject LowerIdentityMember(JsonObject call, JsonObject cast, int classifierKind)
    {
        var checkedReceiver = LowerIdentityClassifier("cast", cast["e"], classifierKind, nullable: false, cast["type"]);
        var member = Str(call["method"]);
        var propertyAccess = Str(call["prop"]);
        JsonObject Count() => classifierKind switch {
            0 => Call("projectedReadOnlyCollectionCountErased", new TypeNode[] { Any }, Int, checkedReceiver.DeepClone()),
            3 or 4 => Call("projectedListCountErased", new TypeNode[] { Any }, Int, checkedReceiver.DeepClone()),
            5 => Call("projectedMutableCollectionCountErased", new TypeNode[] { Any }, Int, checkedReceiver.DeepClone()),
            1 => Call("projectedSetCountErased", new TypeNode[] { Any }, Int, checkedReceiver.DeepClone()),
            2 => Call("projectedMutableSetCountErased", new TypeNode[] { Any }, Int, checkedReceiver.DeepClone()),
            _ => throw new InvalidOperationException("Unknown collection classifier"),
        };
        switch (member)
        {
            case "size" when propertyAccess == "get":
                return Count();
            case "isEmpty":
                return CollectionHelper(MemberCallSubstitution.ProjectedIsEmptyHelper(
                    IdentityKind.Single(pair => pair.Value == classifierKind).Key), Bool, checkedReceiver);
            case "iterator":
                if (classifierKind is 2 or 4 or 5)
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

    // Existential Collection locals/parameters need the same selected physical Count contract as
    // direct casts. A concrete source-authored closure remains owned by the exact foreign binder.
    static JsonObject LowerCollectionCount(JsonObject call, int kind,
        IReadOnlyDictionary<string, TypeNode.Fqn> closedViews)
    {
        var receiver = call["recv"];
        if (receiver == null || HasConcreteTypeArguments(call["ownerType"])
            || receiver is JsonObject concreteCast && Str(concreteCast["k"]) == "cast"
                && HasConcreteTypeArguments(concreteCast["type"])) return null;
        if (receiver is JsonObject local && Str(local["k"]) == "local"
            && Str(local["name"]) is string name && closedViews.ContainsKey(name)) return null;
        var helper = kind switch
        {
            1 => "projectedSetCountErased",
            2 => "projectedMutableSetCountErased",
            5 => "projectedMutableCollectionCountErased",
            _ => "projectedCollectionCountErased",
        };
        return Call(helper,
            new TypeNode[] { Any }, Int, receiver.DeepClone());
    }

    // An existential List can have only a raw IList or only a generic mutable list face. Count/Get
    // must follow that List face, not assume that every accepted value has IReadOnlyCollection<T>.
    static JsonObject LowerListMember(JsonObject call, IReadOnlyDictionary<string, TypeNode.Fqn> closedViews)
    {
        var member = Str(call["method"]);
        var count = member == "size" && Str(call["prop"]) == "get";
        var args = call["args"] as JsonArray;
        if (!count && !((member is "get" or "get_Item") && args?.Count == 1)) return null;
        var receiver = call["recv"];
        if (receiver == null) return null;
        // Concrete Any is a real closed interface, not an existential. Preserve its exact
        // selected CLR member rather than searching the receiver's unrelated generic views.
        if (HasConcreteTypeArguments(call["ownerType"])
            || receiver is JsonObject concreteCast && Str(concreteCast["k"]) == "cast"
                && HasConcreteTypeArguments(concreteCast["type"])) return null;
        // An immutable star local can retain a source-authored exact generic view. Leave that
        // member to the exact foreign binder; a runtime-only List helper would discard the view.
        if (receiver is JsonObject local && Str(local["k"]) == "local"
            && Str(local["name"]) is string name && closedViews.ContainsKey(name)) return null;
        var checkedReceiver = receiver is JsonObject cast && Str(cast["k"]) == "cast"
            && IsIdentityCollection(cast["type"], out var kind, out _)
            ? LowerIdentityClassifier("cast", cast["e"], kind, nullable: false, cast["type"])
            : receiver.DeepClone();
        return count
            ? Call("projectedListCountErased", new TypeNode[] { Any }, Int, checkedReceiver)
            : Call("projectedListGetErased", new TypeNode[] { Any, Int }, AnyN,
                checkedReceiver, args[0].DeepClone());
    }

    static JsonObject Call(string method, IReadOnlyList<TypeNode> signature, TypeNode result,
        params JsonNode[] args) => new()
    {
        ["k"] = "callStatic", ["owner"] = TypeJson.Write(new TypeNode.Fqn(RuntimeOwner)), ["method"] = method,
        ["sig"] = new JsonArray(signature.Select(TypeJson.Write).ToArray()), ["ret"] = TypeJson.Write(result),
        ["args"] = new JsonArray(args),
    };

    static JsonObject CollectionHelper(string method, TypeNode result, params JsonNode[] args) => new()
    {
        ["k"] = "callStatic",
        ["owner"] = TypeJson.Fqn("kotlin.collections.ClrCollectionDefaultsKt"),
        ["method"] = method,
        ["sig"] = new JsonArray(TypeJson.Write(Any)),
        ["typeArgs"] = new JsonArray(TypeJson.Write(AnyN)),
        ["ret"] = TypeJson.Write(result),
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
    static bool IsMutableStarCollection(JsonNode slot)
    {
        var read = TypeJson.Read(slot);
        while (read is TypeNode.Nullable n) read = n.Of;
        while (read is TypeNode.Oblivious o) read = o.Of;
        return read is TypeNode.Fqn f && f.Name == "kotlin.collections.MutableIterable";
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
                // `.size` -> IDictionary.Count.
                return new JsonObject { ["k"] = "clrPropGet", ["type"] = TypeJson.Fqn(iface), ["name"] = "Count", ["ret"] = TypeJson.Fqn("System.Int32"), ["static"] = false, ["recv"] = CastTo(iface) };
            case "isEmpty":
                // The non-generic facade has no IsEmpty slot, but a Kotlin implementer may override it. Preserve the
                // explicit star cast and let the compiler-owned capability dispatcher select the override or Count.
                return CollectionHelper("clrProjectedCollIsEmpty", Bool, CastTo(iface));
            case "iterator":
                if (mutable)
                    // Keep the original star cast observable before entering the erased helper. Passing recvInner
                    // directly could make a rejected collection cast succeed merely because the value is enumerable.
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
                // `map[key]` -> IDictionary.get_Item(object), preserving Kotlin's null-on-missing result.
                if (args == null || args.Count < 1) return null;
                if (iface == "System.Collections.IDictionary")
                    return new JsonObject { ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.Collections.IDictionary"), ["method"] = "get_Item", ["argTypes"] = new JsonArray { TypeJson.Fqn("System.Object") }, ["ret"] = TypeJson.Fqn("System.Object"), ["recv"] = CastTo("System.Collections.IDictionary"), ["args"] = new JsonArray { args[0].DeepClone() } };
                return null;
            case "containsKey":
                // `map.containsKey(k)` -> IDictionary.Contains(object) (#74a).
                if (args == null || args.Count < 1 || iface != "System.Collections.IDictionary") return null;
                return new JsonObject { ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.Collections.IDictionary"), ["method"] = "Contains", ["argTypes"] = new JsonArray { TypeJson.Fqn("System.Object") }, ["ret"] = TypeJson.Fqn("System.Boolean"), ["recv"] = CastTo("System.Collections.IDictionary"), ["args"] = new JsonArray { args[0].DeepClone() } };
            default:
                return null;
        }
    }
}
