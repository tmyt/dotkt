using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// #29 ROUND-TRIP RECORD — capture the Kotlin READ-ONLY-vs-MUTABLE collection identity that BirTypeLowering's
// ARG-POSITION VARIANCE COLLAPSE (Root V, BirTypeLowering.InvariantSibling) erases at generic-arg depth >= 1.
//
// The collapse rewrites a NESTED read-only `kotlin.collections.List/Set/Collection` (a covariant `IReadOnlyList`/
// `IReadOnlyCollection` alias, unrescuable against a concrete invariant value at depth >= 1) to its INVARIANT CLR
// sibling `IList`/`ICollection` — the SAME token `MutableList`/`MutableSet`/`MutableCollection` lowers to directly.
// The collapse is LOAD-BEARING for runtime inhabitance (a reified `T := List<Int>` type variable must have ONE
// context-independent CLR lowering, else no consistent `MakeGenericMethod` instantiation exists — see the #29 design
// note) so it must NOT be narrowed. But the collapse makes a nested `IList<T>` in the emitted signature AMBIGUOUS:
// dll2klib's BCL reverse-map cannot tell a collapsed read-only `List<T>` from a genuine `MutableList<T>`, so it
// surfaces `Box<MutableList<T>>` cross-module and REJECTS a `Box<List<String>>` value.
//
// So — mirroring the #18/#147 [KotlinNullableGeneric] precedent (NullableGenericErasure's positional records)
// and the positional-fact model (suspendFnType, retNothing, nullableGenericRet) — record the PRE-collapse Kotlin
// type of every decl-surface slot (method return/param, ctor param, property, field) that nests a collapsing
// read-only collection, as the OPAQUE canonical TypeNode JSON STRING. Stored as a string (not a `{t:…}` node) so the
// intervening BirTypeLowering / ReferenceNullableStrip passes leave it untouched; RoundtripMetadata reads it back at
// stamp time into [KotlinCollectionIdentity(version, bytes)], and dll2klib restores `List` vs `MutableList` at every
// nested position from the recorded truth (the whole type — so a mixed `Pair<List<T>, MutableList<T>>` restores both).
//
// APP builds only (StdlibMode == App): the collapse only fires in a non-ref build, and only an app-emitted library is
// re-consumed cross-module via dll2klib. The stdlib ref surface keeps `kotlin.*` verbatim (no collapse -> no
// ambiguity); the rt.dll is never metadata-read (RoundtripMetadata.StripRuntimeAttrs drops attrs there).
static class CollectionIdentityRecord
{
    // The read-only collection tokens whose Root-V collapse target (IList/ICollection) collides with the mutable
    // sibling's direct @ClrTypeAlias — the ONLY source of the nested ambiguity. A nested MutableList/MutableSet/
    // MutableCollection lowers to IList/ICollection via its OWN alias (not the collapse) and dll2klib's reverse-map
    // already restores it correctly, so a slot nesting only mutable collections is left unstamped.
    static readonly HashSet<string> CollapsingReadonly = new(StringComparer.Ordinal)
    {
        "kotlin.collections.List",
        "kotlin.collections.Set",
        "kotlin.collections.Collection",
    };

    public static void Apply(JsonNode root)
    {
        if (root is JsonObject o) RecordDecls(o);
    }

    static void RecordDecls(JsonObject o)
    {
        if (o["methods"] is JsonArray methods)
            foreach (var m in methods) if (m is JsonObject mo) RecordMethod(mo);
        if (o["ctors"] is JsonArray ctors)
            foreach (var c in ctors) if (c is JsonObject co) RecordParams(co["params"]);
        RecordSimpleDecls(o["properties"]);
        RecordSimpleDecls(o["fields"]);
        if (o["types"] is JsonArray types)
            foreach (var t in types) if (t is JsonObject to) RecordDecls(to);
    }

    static void RecordMethod(JsonObject mo)
    {
        if (TypeJson.Read(mo["ret"]) is TypeNode ret && NestsCollapsingReadonly(ret))
            mo["collIdentityRet"] = TypeNode.ToJson(ret);
        RecordParams(mo["params"]);
    }

    static void RecordParams(JsonNode ps)
    {
        if (ps is not JsonArray a) return;
        foreach (var p in a)
            if (p is JsonObject po && TypeJson.Read(po["type"]) is TypeNode pt && NestsCollapsingReadonly(pt))
                po["collIdentity"] = TypeNode.ToJson(pt);
    }

    static void RecordSimpleDecls(JsonNode arr)
    {
        if (arr is not JsonArray a) return;
        foreach (var d in a)
            if (d is JsonObject dobj && TypeJson.Read(dobj["type"]) is TypeNode t && NestsCollapsingReadonly(t))
                dobj["collIdentity"] = TypeNode.ToJson(t);
    }

    // True iff a read-only List/Set/Collection appears where BirTypeLowering's Root-V collapse would REWRITE it — i.e.
    // reached with `typeArg == true`, which becomes true ONLY inside a Fqn's Args (BirTypeLowering line 209, sticky
    // across nested Args) and RESETS to false through an Array elem / ByRef / Nullable / Fn position (those recurse
    // with typeArg:false). This mirrors the collapse condition exactly, so a slot is stamped iff at least one nested
    // read-only collection genuinely collapses (a TOP-LEVEL / array-elem read-only collection stays the covariant
    // IReadOnlyList alias — dll2klib restores it without a stamp — and is deliberately NOT recorded).
    static bool NestsCollapsingReadonly(TypeNode t) => Scan(t, typeArg: false);

    static bool Scan(TypeNode t, bool typeArg) => t switch
    {
        TypeNode.Fqn f =>
            (typeArg && CollapsingReadonly.Contains(f.Name))
            || (f.Args?.Any(a => Scan(a, typeArg: true)) ?? false),
        TypeNode.Array a => Scan(a.Elem, typeArg: false),
        TypeNode.ByRef b => Scan(b.Of, typeArg: false),
        TypeNode.Nullable n => Scan(n.Of, typeArg: false),
        // Oblivious (`T!`) is a pure nullability annotation — BirTypeLowering lowers its inner with THIS node's incoming
        // typeArg (not a value-position reset), so a nested `Map<K, List!>` still collapses; propagate typeArg to match.
        TypeNode.Oblivious ob => Scan(ob.Of, typeArg),
        TypeNode.Fn fn =>
            Scan(fn.Ret, typeArg: false)
            || fn.Params.Any(p => Scan(p, typeArg: false))
            || (fn.Recv != null && Scan(fn.Recv, typeArg: false)),
        _ => false,
    };
}
