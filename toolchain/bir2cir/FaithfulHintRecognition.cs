using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// FAITHFUL-HINT RECOGNITION (#52 Phase 4b): kotc stopped routing four Kotlin-SEMANTIC recognition families to the
// stdlib helpers (ClrCollectionDefaultsKt / ClrMapDefaultsKt / NumbersKt / LibraryKt) by hardcoded FQN. It now emits
// the FAITHFUL op (`objMethod ToString/Equals`, `concat` with parts, `callStatic EQEQ`, `callStatic println/print`,
// `callInstance compareTo`) plus a TRANSIENT cast-stripped static-TYPE HINT (recvType / argType / argValueTypes /
// argTypes / partTypes). bir2cir does ALL the recognition off those hints — reproducing the EXACT SAME helper
// `callStatic` node kotc used to synthesize — then STRIPS every consumed hint so the CIR is clean. Final IL is
// byte-identical: only the RECOGNITION moved, the helper bodies are unchanged.
//
// The EQEQ family is handled inside PrimitiveOperatorLowering.LowerIntrinsic (it already owns the EQEQ arm); this pass
// handles the remaining four sites (objMethod ToString/Equals, println/print, concat, Double/Float compareTo). Both
// run EARLY — before MemberCallSubstitution / factory / BirTypeLowering — so the inner value nodes still carry the
// pure kotlin.* / listOf shapes and flow through the normal downstream lowering, exactly as when kotc wrapped them.

// Shared recognition primitives used by BOTH this pass and PrimitiveOperatorLowering's EQEQ arm.
static class FaithfulHints
{
    // Owner FQNs of the (unchanged) stdlib helpers.
    const string CollDefaults = "kotlin.collections.ClrCollectionDefaultsKt";
    const string MapDefaults = "kotlin.collections.ClrMapDefaultsKt";
    const string Numbers = "kotlin.NumbersKt";
    const string Library = "kotlin.LibraryKt";

    public enum CollKind { Map, Set, Coll }

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
    public static void Apply(JsonNode root) => Walk(root);

    // Bottom-up: recurse into a node's children (so an inner site is recognized first), THEN transform the node itself,
    // replacing it in its parent when the transform produced a fresh node.
    static void Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    var child = o[key];
                    if (child != null) Walk(child);
                    if (o[key] is JsonObject co && Transform(co) is JsonNode r && !ReferenceEquals(r, co)) o[key] = r;
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    var child = a[i];
                    if (child != null) Walk(child);
                    if (a[i] is JsonObject co && Transform(co) is JsonNode r && !ReferenceEquals(r, co)) a[i] = r;
                }
                break;
        }
    }

    static JsonNode Transform(JsonObject o) => (o["k"] as JsonValue)?.GetValue<string>() switch
    {
        "objMethod" => TransformObjMethod(o),
        "callStatic" => TransformPrintln(o),
        "concat" => TransformConcat(o),
        "callInstance" => TransformCompareTo(o),
        _ => o,
    };

    // `objMethod ToString`+recvType: a collection/Map receiver -> clrCollToString/clrMapToString (Kotlin `[a, b]`);
    // else DROP recvType and keep the plain .NET ToString. `objMethod Equals`+recvType+argType: SAME collection kind ->
    // struct-eq helper; both non-null Double/Float -> float-equals helper; else DROP the hints, keep Object.Equals.
    static JsonNode TransformObjMethod(JsonObject o)
    {
        var method = (o["method"] as JsonValue)?.GetValue<string>();
        if (method == "ToString" && o["recvType"] is JsonNode rtNode)
        {
            if (TypeJson.Read(rtNode) is TypeNode rt && FaithfulHints.ClassifyColl(rt) is { } c)
                return FaithfulHints.CollToString(FaithfulHints.StripAnyCast(o["recv"]), c.kind, c.args);
            o.Remove("recvType");
            return o;
        }
        if (method == "Equals" && o["recvType"] is JsonNode ertNode && o["argType"] is JsonNode eatNode)
        {
            var recvT = TypeJson.Read(ertNode);
            var argT = TypeJson.Read(eatNode);
            var rc = recvT != null ? FaithfulHints.ClassifyColl(recvT) : null;
            var ac = argT != null ? FaithfulHints.ClassifyColl(argT) : null;
            if (rc is { } rk && ac is { } ak && rk.kind == ak.kind)
                return FaithfulHints.StructEquals(
                    FaithfulHints.StripAnyCast(o["recv"]), FaithfulHints.StripAnyCast(o["arg"]), rk.kind, rk.args);
            if (recvT != null && argT != null && FloatEqualsMethod(recvT, argT) is string fm)
                return FaithfulHints.FloatCall(fm, FaithfulHints.StripAnyCast(o["recv"]), FaithfulHints.StripAnyCast(o["arg"]));
            o.Remove("recvType");
            o.Remove("argType");
            return o;
        }
        return o;
    }

    // `callStatic owner=null method∈{println,print}`+argTypes: wrap each collection/Map arg in clrCollToString/
    // clrMapToString IN PLACE, then DROP argTypes. The println/print callStatic itself is LEFT for the later Console
    // substitution (MemberCallSubstitution).
    static JsonNode TransformPrintln(JsonObject o)
    {
        if (o["owner"] != null) return o;
        var method = (o["method"] as JsonValue)?.GetValue<string>();
        if (method != "println" && method != "print") return o;
        if (o["argTypes"] is not JsonArray argTypes) return o;
        if (o["args"] is JsonArray args)
            for (var i = 0; i < args.Count && i < argTypes.Count; i++)
                if (TypeJson.Read(argTypes[i]) is TypeNode t && FaithfulHints.ClassifyColl(t) is { } c)
                    args[i] = FaithfulHints.CollToString(FaithfulHints.StripAnyCast(args[i]), c.kind, c.args);
        o.Remove("argTypes");
        return o;
    }

    // `concat`+partTypes: a collection part -> collToString/mapToString; else a NULLABLE part -> LibraryKt.toString
    // (null -> "null"); else leave. Then DROP partTypes.
    static JsonNode TransformConcat(JsonObject o)
    {
        if (o["partTypes"] is not JsonArray partTypes) return o;
        if (o["parts"] is JsonArray parts)
            for (var i = 0; i < parts.Count && i < partTypes.Count; i++)
            {
                if (TypeJson.Read(partTypes[i]) is not TypeNode t) continue;
                if (FaithfulHints.ClassifyColl(t) is { } c)
                    parts[i] = FaithfulHints.CollToString(FaithfulHints.StripAnyCast(parts[i]), c.kind, c.args);
                else if (FaithfulHints.IsNullable(t))
                    // The null-safe stringifier keeps the ORIGINAL part (kotc's former concatOperand passed the
                    // un-unwrapped `expr(op)` — only the collToString path unwrapped). Stripping an Any-box off a
                    // nullable value operand would feed a raw value to LibraryKt.toString's Any? param.
                    parts[i] = FaithfulHints.LibraryToString(parts[i]);
            }
        o.Remove("partTypes");
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
