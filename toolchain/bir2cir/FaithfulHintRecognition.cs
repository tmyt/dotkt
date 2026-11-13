using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Select the CLR representation of Kotlin rendering and floating-point ordering.
// kotc supplies ordinary Kotlin operations, not helper names or representation
// hints. StaticType recovers source types in the declaration's BirScope before
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
    // Returns the inner `e` (a still-parented child — callers DeepClone it into the helper), else the node itself.
    public static JsonNode StripAnyCast(JsonNode n)
    {
        if (n is JsonObject o && (o["k"] as JsonValue)?.GetValue<string>() == "cast"
            && IsAnyCastType(TypeJson.Read(o["type"]))
            && o["e"] is JsonNode inner)
            return inner;
        return n;
    }

    public static IBirExpression StripAnyCast(IBirExpression value) => value is BirCast { E.HasValue: true } cast
        && IsAnyCastType(cast.Type.OrDefault()) ? cast.E.Value : value;

    static bool IsAnyCastType(TypeNode type) => type is TypeNode.Fqn { Name: "kotlin.Any" or "System.Object" or "object" };

    // The Kotlin `[a, b]` / `{a=1, b=2}` renderer: clrCollToString (1 type-arg) for List/Set/Collection, clrMapToString
    // (2 type-args) for Map. `op` is DeepCloned because the helper node takes ownership of its argument tree.
    public static JsonObject CollToString(JsonNode op, CollKind kind, TypeNode[] args)
    {
        var specification = CollectionRenderer(kind, args);
        var operands = new JsonArray(op.DeepClone());
        if (specification.MapPreference)
            operands.Add(MemberCallSubstitution.StaticMapPreference(new TypeNode.Fqn("kotlin.collections.Map", args), null,
                MemberCallSubstitution.CollectionOperationPhase.Source));
        return Helper(specification, operands);
    }

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
    public static JsonObject FloatCall(string method, JsonNode a, JsonNode b) =>
        Helper(FloatHelper(method), new JsonArray { a.DeepClone(), b.DeepClone() });

    internal static BirCallStatic FloatCall(string method, IBirExpression a, IBirExpression b)
        => Helper(FloatHelper(method), new[] { a, b });

    // The remaining DOM producers cross only the binding-fact boundary, not their executable arguments.
    internal static void BindHelper(JsonObject call, Func<BirCallStatic, BirCallStatic> bind)
    {
        var arguments = call["typeArgs"] as JsonArray;
        var facts = new BirCallStatic {
            Owner = TypeJson.Read(call["owner"]), Method = call["method"].GetValue<string>(),
            Sig = ((JsonArray)call["sig"]).Select(TypeJson.Read).ToArray(),
            TypeArgs = arguments == null ? default : OptionalField<TypeNode[]>.Of(arguments.Select(TypeJson.Read).ToArray()),
        };
        var bound = bind(facts);
        call[DeclarationIdentityBinding.Key] = bound.DeclarationId.Value;
        if (bound.TypeArgs.HasValue)
            call["typeArgs"] = new JsonArray(bound.TypeArgs.Value.Select(TypeJson.Write).ToArray());
    }

    // Null-safe `Any?.toString()` (renders null as "null") on kotlin.LibraryKt (NO type-args).
    public static JsonObject LibraryToString(JsonNode op) =>
        Helper(NullableRenderer(), new JsonArray { op.DeepClone() });

    // Runtime-erased Kotlin renderer `clrElemToString(x: Any?)`: detects a collection/map at RUNTIME via the non-generic
    // BCL facades (ICollection/IDictionary) and renders `[a, b]` / `{k=v}`, else plain `toString()`. The star-projected /
    // `Any`-erased path (a value-type dict smart-cast to `Map<*,*>`) has no bindable generic helper, so it routes here.
    public static JsonObject ElemToString(JsonNode op) =>
        Helper(ElementRenderer(), new JsonArray { op.DeepClone() });

    static JsonObject Helper(HelperSpecification specification, JsonArray args)
    {
        // `typeArgs` is omitted when the helper is non-generic; consumers bind every field by key.
        var o = new JsonObject
        {
            ["k"] = "callStatic",
            ["owner"] = TypeJson.Fqn(specification.Owner),
            ["method"] = specification.Method,
            ["sig"] = new JsonArray(specification.Signature.Select(TypeJson.Write).ToArray()),
            ["args"] = args,
        };
        if (specification.TypeArguments != null)
            o["typeArgs"] = new JsonArray(specification.TypeArguments.Select(TypeJson.Write).ToArray());
        if (_bindHelper != null) BindHelper(o, _bindHelper);
        return o;
    }

}

static partial class FaithfulHintRecognition
{
    // This assembly's own top-level fun names — so the M9 reinterpret recognition can distinguish a user function that
    // shadows `toByteArray`/`toUByteArray` from the stdlib extension whose identity this pass realizes.
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs = null, IReadOnlySet<string> localTopLevelFns = null)
    {
        Walk(root, BirScope.Empty, localTopLevelFns ?? new HashSet<string>(StringComparer.Ordinal));
    }

    // Bottom-up: recurse into a node's children (so an inner site is recognized first), THEN transform the node itself,
    // replacing it in its parent when the transform produced a fresh node. A declaration node extends `scope` with its
    // params + body locals so StaticType can recover a bare operand `local`'s declared type (#59 — the former hints).
    static void Walk(JsonNode node, BirScope scope, IReadOnlySet<string> localTopLevelFns)
    {
        switch (node)
        {
            case JsonObject o:
                var child = scope.Extend(o);
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    var c = o[key];
                    if (c != null) Walk(c, child, localTopLevelFns);
                    if (o[key] is JsonObject co && Transform(co, child, localTopLevelFns) is JsonNode r && !ReferenceEquals(r, co)) o[key] = r;
                }
                break;
            case JsonArray a:
                // A statement sequence: a `var` enters scope for the SUBSEQUENT siblings only (lexical block scoping —
                // two loops each destructuring to `v` of different element types must not collide via a flat dict).
                var cur = scope;
                for (var i = 0; i < a.Count; i++)
                {
                    var c = a[i];
                    if (c != null) Walk(c, cur, localTopLevelFns);
                    if (a[i] is JsonObject co && Transform(co, cur, localTopLevelFns) is JsonNode r && !ReferenceEquals(r, co)) a[i] = r;
                    if (a[i] is JsonObject vo && (vo["k"] as JsonValue)?.GetValue<string>() == "var")
                    {
                        if (ReferenceEquals(cur, scope)) cur = scope.Child();
                        cur.Declare(vo);
                    }
                }
                break;
        }
    }

    static JsonNode Transform(JsonObject o, BirScope scope, IReadOnlySet<string> localTopLevelFns) => (o["k"] as JsonValue)?.GetValue<string>() switch
    {
        "objMethod" => TransformObjMethod(o, scope),
        "callStatic" => TransformCallStatic(o, scope, localTopLevelFns),
        "concat" => TransformConcat(o, scope),
        "callInstance" => TransformCompareTo(o),
        _ => o,
    };

    // A top-level `callStatic owner=null`: try the unsigned<->signed byte-array reinterpret recognition (M9) first,
    // else the println/print collection-arg wrap.
    static JsonNode TransformCallStatic(JsonObject o, BirScope scope, IReadOnlySet<string> localTopLevelFns)
    {
        if (TransformUnsignedByteArrayReinterpret(o, scope, localTopLevelFns) is JsonNode r && !ReferenceEquals(r, o)) return r;
        return TransformPrintln(o, scope);
    }

    // M9 (#76 residue): `UByteArray.toByteArray()` / `ByteArray.toUByteArray()`. UByteArray IS a native byte[] and
    // ByteArray IS a native sbyte[]; the two are freely castclass-compatible (same 8-bit storage, ECMA reduced-type
    // array compatibility), so the stdlib's @InlineOnly `storage.copyOf()` extension (whose value-class `storage` has
    // no native form) lowers to a plain array REINTERPRET cast — a VIEW, not a defensive copy (noted in
    // dotkt-semantics). kotc emits the FAITHFUL top-level extension call (`callStatic owner=null method=toByteArray
    // args=[recv]`, no CLR knowledge); bir2cir recognizes it off the receiver's recovered static type and emits the CIR
    // reinterpret `cast` required by that documented view operation. Guarded on the EXACT receiver array identity so the
    // Collection<Byte>/Array<out Byte> overloads of the same names are untouched.
    static JsonNode TransformUnsignedByteArrayReinterpret(JsonObject o, BirScope scope, IReadOnlySet<string> localTopLevelFns)
    {
        // Only the plain stdlib-extension shape (`owner=null`, no `ownerType`): an `ownerType`-keyed callStatic is a
        // dll2klib .NET interop static (e.g. a user `SomeType.toByteArray(...)`), never the stdlib reinterpret.
        if (o["owner"] != null || o["ownerType"] != null) return o;
        var method = (o["method"] as JsonValue)?.GetValue<string>();
        if (o["args"] is not JsonArray args || args.Count != 1) return o;
        var elem = ReinterpretElement(method, StaticType.Surface(args[0], scope), localTopLevelFns);
        if (elem == null) return o;
        return new JsonObject
        {
            ["k"] = "cast",
            ["type"] = TypeNode.Write(new TypeNode.Array(new TypeNode.Fqn(elem))),
            ["e"] = args[0]?.DeepClone(),
        };
    }

    // `objMethod ToString`: a collection/Map receiver (StaticType.Value of `recv`) -> clrCollToString/clrMapToString
    // (Kotlin `[a, b]`); else keep the plain .NET ToString. Equality has already
    // been consumed at representation entry by KotlinEqualityLowering.
    static JsonNode TransformObjMethod(JsonObject o, BirScope scope)
    {
        var method = (o["method"] as JsonValue)?.GetValue<string>();
        if (method == "ToString" && o["recv"] is JsonNode recv)
        {
            var rendering = SelectRendering(RenderSite.Object, null, StaticType.Value(recv, scope));
            if (rendering.Kind != Renderer.None) return Render(recv, rendering);
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
            {
                var rendering = SelectRendering(RenderSite.Print, StaticType.Surface(args[i], scope), StaticType.Value(args[i], scope));
                if (rendering.Kind != Renderer.None) args[i] = Render(args[i], rendering);
            }
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
                var rendering = SelectRendering(RenderSite.Concat, null, StaticType.Value(parts[i], scope));
                if (rendering.Kind != Renderer.None)
                    // The null-safe stringifier keeps the ORIGINAL part (kotc's former concatOperand passed the
                    // un-unwrapped `expr(op)` — only the collToString path unwrapped). Stripping an Any-box off a
                    // nullable value operand would feed a raw value to LibraryKt.toString's Any? param.
                    parts[i] = Render(parts[i], rendering);
            }
        return o;
    }

    // `callInstance ownerType∈{kotlin.Double,kotlin.Float} method=compareTo` (recv + 1 arg) -> clrDoubleCompare/
    // clrFloatCompare (Kotlin total order: `-0.0 < 0.0`, NaN largest, `NaN.compareTo(NaN)==0`). MUST run before
    // MemberCallSubstitution's primitive-compareTo -> System.Double.CompareTo routing.
    static JsonNode TransformCompareTo(JsonObject o)
    {
        var method = CompareHelper((o["method"] as JsonValue)?.GetValue<string>(), TypeJson.Read(o["ownerType"]));
        if (method == null) return o;
        if (o["args"] is not JsonArray args || args.Count != 1 || o["recv"] == null) return o;
        return FaithfulHints.FloatCall(method, o["recv"], args[0]);
    }

}
