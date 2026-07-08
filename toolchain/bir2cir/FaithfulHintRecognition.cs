using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// FAITHFUL-HINT RECOGNITION (#52 Phase 4b / #59): kotc stopped routing four Kotlin-SEMANTIC recognition families to
// the stdlib helpers (ClrCollectionDefaultsKt / ClrMapDefaultsKt / NumbersKt / LibraryKt) by hardcoded FQN. It emits
// the FAITHFUL op (`objMethod ToString/Equals`, `concat` with parts, `callStatic println/print`, `callInstance
// compareTo`) with NO type hint (#59 — the transient recvType/argType/argTypes/partTypes hints are RETIRED). bir2cir
// does ALL the recognition off the operand's RECOVERED static type (StaticType — StaticTypeResolver.cs), reproducing
// the EXACT SAME helper `callStatic` node kotc used to synthesize. Final IL is byte-identical: only the RECOGNITION
// moved, the helper bodies are unchanged.
//
// The EQEQ family is handled inside PrimitiveOperatorLowering.LowerIntrinsic (it already owns the EQEQ arm); this pass
// handles the remaining four sites (objMethod ToString/Equals, println/print, concat, Double/Float compareTo). Both
// run EARLY — before MemberCallSubstitution / factory / BirTypeLowering — so the inner value nodes still carry the
// pure kotlin.* / listOf shapes and flow through the normal downstream lowering, exactly as when kotc wrapped them.
// Each pass threads a BirScope (the declaration-scoped local/param type environment) so StaticType can recover a
// bare operand `local`'s declared type — the early-pass twin of MemberCallSubstitution's SubstCtx.VarTypes.

// Shared recognition primitives used by BOTH this pass and PrimitiveOperatorLowering's EQEQ arm.
static class FaithfulHints
{
    // Owner FQNs of the (unchanged) stdlib helpers.
    const string CollDefaults = "kotlin.collections.ClrCollectionDefaultsKt";
    const string MapDefaults = "kotlin.collections.ClrMapDefaultsKt";
    const string NestedToString = "kotlin.collections.ClrNestedToStringKt";
    const string Numbers = "kotlin.NumbersKt";
    const string Library = "kotlin.LibraryKt";

    public enum CollKind { Map, Set, Coll }

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
    // then requires a `kotlin.collections.*` Fqn. name contains "Map" -> Map; else contains "Set" -> Set; else contains
    // "List" or endsWith "Collection" -> Coll. Null when the type is not a recognized collection.
    public static (CollKind kind, TypeNode[] args)? ClassifyColl(TypeNode t)
    {
        if (Unwrap(t) is not TypeNode.Fqn f) return null;
        if (!f.Name.StartsWith("kotlin.collections.", StringComparison.Ordinal)) return null;
        var args = f.Args ?? Array.Empty<TypeNode>();
        if (f.Name.Contains("Map", StringComparison.Ordinal)) return (CollKind.Map, args);
        if (f.Name.Contains("Set", StringComparison.Ordinal)) return (CollKind.Set, args);
        if (f.Name.Contains("List", StringComparison.Ordinal) || f.Name.EndsWith("Collection", StringComparison.Ordinal))
            return (CollKind.Coll, args);
        return null;
    }

    public static TypeNode Unwrap(TypeNode t) => t is TypeNode.Nullable n ? n.Of : t;

    public static bool IsNullable(TypeNode t) => t is TypeNode.Nullable;

    // True iff the hint is the bare (non-null) primitive Fqn `name` — a nullable/reference operand fails, matching
    // kotc's former floatTotalEqRoute gate (a boxed Double `==` is only total-order when BOTH sides are non-null Double).
    public static bool IsNonNullFqn(TypeNode t, string name) => t is TypeNode.Fqn f && f.Name == name;

    // "Cast-stripped operand": drop a leading `{k:cast, type:<kotlin.Any/System.Object/object>, e:X}` (an IMPLICIT_CAST
    // to Any renders as such a node) so the operand matches kotc's former `expr(unwrapped)`. Returns the inner `e` (a
    // still-parented child — callers DeepClone it into the helper), else the node itself.
    public static JsonNode StripAnyCast(JsonNode n)
    {
        if (n is JsonObject o && (o["k"] as JsonValue)?.GetValue<string>() == "cast"
            && TypeJson.Read(o["type"]) is TypeNode.Fqn f
            && (f.Name == "kotlin.Any" || f.Name == "System.Object" || f.Name == "object")
            && o["e"] is JsonNode inner)
            return inner;
        return n;
    }

    // The Kotlin `[a, b]` / `{a=1, b=2}` renderer: clrCollToString (1 type-arg) for List/Set/Collection, clrMapToString
    // (2 type-args) for Map. `op` is DeepCloned; key order = k,owner,method,args,typeArgs (byte-identical to kotc).
    public static JsonObject CollToString(JsonNode op, CollKind kind, TypeNode[] args) =>
        kind == CollKind.Map
            ? Helper(MapDefaults, "clrMapToString", new JsonArray { op.DeepClone() }, CollTypeArgs(kind, args))
            : Helper(CollDefaults, "clrCollToString", new JsonArray { op.DeepClone() }, CollTypeArgs(kind, args));

    // Kotlin STRUCTURAL `==` on the SAME collection kind: clrCollStructEquals / clrSetStructEquals / clrMapStructEquals.
    // type-args come from the LEFT operand's kind/args.
    public static JsonObject StructEquals(JsonNode lu, JsonNode ru, CollKind kind, TypeNode[] leftArgs)
    {
        var (owner, method) = kind switch
        {
            CollKind.Map => (MapDefaults, "clrMapStructEquals"),
            CollKind.Set => (CollDefaults, "clrSetStructEquals"),
            _ => (CollDefaults, "clrCollStructEquals"),
        };
        return Helper(owner, method, new JsonArray { lu.DeepClone(), ru.DeepClone() }, CollTypeArgs(kind, leftArgs));
    }

    // Double/Float total-order helpers on kotlin.NumbersKt (NO type-args). `method` = clrDoubleCompare/clrFloatCompare
    // (compareTo) or clrDoubleEquals/clrFloatEquals (boxed `==` / explicit `.equals`).
    public static JsonObject FloatCall(string method, JsonNode a, JsonNode b) =>
        Helper(Numbers, method, new JsonArray { a.DeepClone(), b.DeepClone() }, null);

    // Null-safe `Any?.toString()` (renders null as "null") on kotlin.LibraryKt (NO type-args).
    public static JsonObject LibraryToString(JsonNode op) =>
        Helper(Library, "toString", new JsonArray { op.DeepClone() }, null);

    // Runtime-erased Kotlin renderer `clrElemToString(x: Any?)`: detects a collection/map at RUNTIME via the non-generic
    // BCL facades (ICollection/IDictionary) and renders `[a, b]` / `{k=v}`, else plain `toString()`. The star-projected /
    // `Any`-erased path (a value-type dict smart-cast to `Map<*,*>`) has no bindable generic helper, so it routes here.
    public static JsonObject ElemToString(JsonNode op) =>
        Helper(NestedToString, "clrElemToString", new JsonArray { op.DeepClone() }, null);

    static JsonObject Helper(string owner, string method, JsonArray args, JsonArray typeArgs)
    {
        // Fixed key insertion order: k, owner, method, args, typeArgs (typeArgs omitted when null).
        var o = new JsonObject
        {
            ["k"] = "callStatic",
            ["owner"] = TypeJson.Fqn(owner),
            ["method"] = method,
            ["args"] = args,
        };
        if (typeArgs != null) o["typeArgs"] = typeArgs;
        return o;
    }

    static JsonArray CollTypeArgs(CollKind kind, TypeNode[] args)
    {
        var arr = new JsonArray { TypeArgOr(args, 0) };
        if (kind == CollKind.Map) arr.Add(TypeArgOr(args, 1));
        return arr;
    }

    // The i-th type-arg, or `kotlin.Any` when absent (matches kotc's default for a missing generic argument).
    static JsonNode TypeArgOr(TypeNode[] args, int i) =>
        i < args.Length ? TypeNode.Write(args[i]) : TypeJson.Fqn("kotlin.Any");
}

static class FaithfulHintRecognition
{
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs = null)
    {
        StaticType.Refs = refs;
        StaticType.LocalTypes = StaticType.CollectTypes(root);
        Walk(root, BirScope.Empty);
    }

    // Bottom-up: recurse into a node's children (so an inner site is recognized first), THEN transform the node itself,
    // replacing it in its parent when the transform produced a fresh node. A declaration node extends `scope` with its
    // params + body locals so StaticType can recover a bare operand `local`'s declared type (#59 — the former hints).
    static void Walk(JsonNode node, BirScope scope)
    {
        switch (node)
        {
            case JsonObject o:
                var child = scope.Extend(o);
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    var c = o[key];
                    if (c != null) Walk(c, child);
                    if (o[key] is JsonObject co && Transform(co, child) is JsonNode r && !ReferenceEquals(r, co)) o[key] = r;
                }
                break;
            case JsonArray a:
                // A statement sequence: a `var` enters scope for the SUBSEQUENT siblings only (lexical block scoping —
                // two loops each destructuring to `v` of different element types must not collide via a flat dict).
                var cur = scope;
                for (var i = 0; i < a.Count; i++)
                {
                    var c = a[i];
                    if (c != null) Walk(c, cur);
                    if (a[i] is JsonObject co && Transform(co, cur) is JsonNode r && !ReferenceEquals(r, co)) a[i] = r;
                    if (a[i] is JsonObject vo && (vo["k"] as JsonValue)?.GetValue<string>() == "var")
                    {
                        if (ReferenceEquals(cur, scope)) cur = scope.Child();
                        cur.Declare(vo);
                    }
                }
                break;
        }
    }

    static JsonNode Transform(JsonObject o, BirScope scope) => (o["k"] as JsonValue)?.GetValue<string>() switch
    {
        "objMethod" => TransformObjMethod(o, scope),
        "callStatic" => TransformPrintln(o, scope),
        "concat" => TransformConcat(o, scope),
        "callInstance" => TransformCompareTo(o),
        _ => o,
    };

    // `objMethod ToString`: a collection/Map receiver (StaticType.Value of `recv`) -> clrCollToString/clrMapToString
    // (Kotlin `[a, b]`); else keep the plain .NET ToString. `objMethod Equals`: SAME collection kind -> struct-eq
    // helper; both non-null Double/Float -> float-equals helper; else keep Object.Equals. (#59 — the former
    // recvType/argType hints, recovered structurally.)
    static JsonNode TransformObjMethod(JsonObject o, BirScope scope)
    {
        var method = (o["method"] as JsonValue)?.GetValue<string>();
        if (method == "ToString" && o["recv"] is JsonNode recv)
        {
            if (StaticType.Value(recv, scope) is TypeNode rt && FaithfulHints.ClassifyColl(rt) is { } c)
                return FaithfulHints.CollToString(FaithfulHints.StripAnyCast(o["recv"]), c.kind, c.args);
            return o;
        }
        if (method == "Equals" && o["recv"] is JsonNode erecv && o["arg"] is JsonNode earg)
        {
            var recvT = StaticType.Value(erecv, scope);
            var argT = StaticType.Value(earg, scope);
            var rc = recvT != null ? FaithfulHints.ClassifyColl(recvT) : null;
            var ac = argT != null ? FaithfulHints.ClassifyColl(argT) : null;
            if (rc is { } rk && ac is { } ak && rk.kind == ak.kind)
                return FaithfulHints.StructEquals(
                    FaithfulHints.StripAnyCast(o["recv"]), FaithfulHints.StripAnyCast(o["arg"]), rk.kind, rk.args);
            if (recvT != null && argT != null && FloatEqualsMethod(recvT, argT) is string fm)
                return FaithfulHints.FloatCall(fm, FaithfulHints.StripAnyCast(o["recv"]), FaithfulHints.StripAnyCast(o["arg"]));
            return o;
        }
        return o;
    }

    // `callStatic owner=null method∈{println,print}`: wrap each collection/Map arg (StaticType.Value) in
    // clrCollToString/clrMapToString IN PLACE. The println/print callStatic itself is LEFT for the later Console
    // substitution (MemberCallSubstitution). (#59 — the former `argTypes` hint.)
    static JsonNode TransformPrintln(JsonObject o, BirScope scope)
    {
        if (o["owner"] != null) return o;
        var method = (o["method"] as JsonValue)?.GetValue<string>();
        if (method != "println" && method != "print") return o;
        if (o["args"] is JsonArray args)
            for (var i = 0; i < args.Count; i++)
                // A star-projected / `Any`-erased collection cast (a `Map<*,*>` smart-cast, an `as List<*>`): its SURFACE
                // (cast-target) type is the collection, but its VALUE peels to the erased `Any` local — so the generic
                // clrCollToString/clrMapToString below can't bind (no value-type covariance on the CLR). Route to
                // clrElemToString(Any?), which detects the collection at runtime via the non-generic BCL facades. The
                // cast node is kept (StarProjectionLowering re-points it to the non-generic interface, assignable to Any?).
                if (FaithfulHints.IsStarProjectedColl(StaticType.Surface(args[i], scope)))
                    args[i] = FaithfulHints.ElemToString(args[i]);
                else if (StaticType.Value(args[i], scope) is TypeNode t && FaithfulHints.ClassifyColl(t) is { } c)
                    args[i] = FaithfulHints.CollToString(FaithfulHints.StripAnyCast(args[i]), c.kind, c.args);
        return o;
    }

    // `concat`: a collection part (StaticType.Value) -> collToString/mapToString; else a NULLABLE part ->
    // LibraryKt.toString (null -> "null"); else leave. (#59 — the former `partTypes` hint, for both the string
    // template and the String.plus-lowered concat.)
    static JsonNode TransformConcat(JsonObject o, BirScope scope)
    {
        if (o["parts"] is JsonArray parts)
            for (var i = 0; i < parts.Count; i++)
            {
                if (StaticType.Value(parts[i], scope) is not TypeNode t) continue;
                if (FaithfulHints.ClassifyColl(t) is { } c)
                    parts[i] = FaithfulHints.CollToString(FaithfulHints.StripAnyCast(parts[i]), c.kind, c.args);
                else if (FaithfulHints.IsNullable(t))
                    // The null-safe stringifier keeps the ORIGINAL part (kotc's former concatOperand passed the
                    // un-unwrapped `expr(op)` — only the collToString path unwrapped). Stripping an Any-box off a
                    // nullable value operand would feed a raw value to LibraryKt.toString's Any? param.
                    parts[i] = FaithfulHints.LibraryToString(parts[i]);
            }
        return o;
    }

    // `callInstance ownerType∈{kotlin.Double,kotlin.Float} method=compareTo` (recv + 1 arg) -> clrDoubleCompare/
    // clrFloatCompare (Kotlin total order: `-0.0 < 0.0`, NaN largest, `NaN.compareTo(NaN)==0`). MUST run before
    // MemberCallSubstitution's primitive-compareTo -> System.Double.CompareTo routing.
    static JsonNode TransformCompareTo(JsonObject o)
    {
        if ((o["method"] as JsonValue)?.GetValue<string>() != "compareTo") return o;
        if (TypeJson.Read(o["ownerType"]) is not TypeNode.Fqn f) return o;
        var method = ReferenceMetadataIndex.BareOwnerFqn(f.Name) switch
        {
            "kotlin.Double" => "clrDoubleCompare",
            "kotlin.Float" => "clrFloatCompare",
            _ => null,
        };
        if (method == null) return o;
        if (o["args"] is not JsonArray args || args.Count != 1 || o["recv"] == null) return o;
        return FaithfulHints.FloatCall(method, o["recv"], args[0]);
    }

    // The float-equals helper method iff BOTH hints are the SAME non-null primitive (kotlin.Double / kotlin.Float).
    static string FloatEqualsMethod(TypeNode a, TypeNode b) =>
        FaithfulHints.IsNonNullFqn(a, "kotlin.Double") && FaithfulHints.IsNonNullFqn(b, "kotlin.Double") ? "clrDoubleEquals"
        : FaithfulHints.IsNonNullFqn(a, "kotlin.Float") && FaithfulHints.IsNonNullFqn(b, "kotlin.Float") ? "clrFloatEquals"
        : null;
}
