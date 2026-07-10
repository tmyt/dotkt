using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// FOR-LOOP SOURCE CLASSIFICATION (#73/#72/#73-w3). kotc no longer decides ANYTHING about a non-array for-loop source
// — whether it is a counted RANGE, an `a downTo b` counter, a stdlib collection, a `kotlin.sequences.Sequence`, or a
// facadegen-injected .NET enumerable are each a `kotlin.ranges.*`/`kotlin.collections.*` FQN, a `downTo` operator
// identity, or a `@Clr`/.NET-type resolution against the reference assemblies — a Kotlin<->CLR relation that lives
// HERE. kotc emits ONE faithful node for every non-array source:
//
//   forIn{elem, src, srcType, var, body, fallback}
//
// carrying the source's runtime TYPE TOKEN (`srcType`) + the element type (`elem`). This pass dispatches on it:
//
//   forIn whose srcType is a counted range (IntRange always; IntProgression in a stdlib self-build) -> `forRange`.
//   forIn whose src is a stdlib `a downTo b` (consumer build) -> a counted `for` (>=, step -1) with temp bounds.
//   forIn whose srcType is `kotlin.sequences.Sequence` OR resolves to a referenced .NET type (any build) -> a
//     `forEachInline` (GetEnumerator). This is the exact set kotc's retired `forInEnumerable` gate routed
//     (`clrName(src) != null` — a facadegen-injected .NET enumerable — OR the source's static type being exactly
//     `kotlin.sequences.Sequence`), moved here (#73-w3) because it is a CLR-representation decision keyed on
//     `@Clr`/.NET-type knowledge. Without it a .NET/Sequence source would fall to the Kotlin iterator protocol
//     (iterator()/hasNext) and a consumer calling it hits EntryPointNotFound.
//   forIn whose srcType is a stdlib collection (stdlib self-build) -> `forEachInline` (GetEnumerator). Recognized by a
//     supertype walk over the compilation's own type defs (a concrete stdlib subtype such as ArrayList : MutableList
//     matches even though its own FQN is not a collection interface); a concrete `Sequence`-implementing class arrives
//     as a forIn and resolves through this walk (Sequence is in CollectionFqns).
//   forIn otherwise -> the `fallback` block (the FIR-desugared iterator protocol kotc used to emit by returning null).
//
// Runs FIRST in the per-file loop (before RangeForLowering / RangeConstructionLowering / SequenceForEachLowering) so
// the produced forms flow through every downstream pass exactly as the equivalent kotc-emitted forms did — byte-
// identical in a consumer build (a range's forRange, a .NET forEachInline, a Kotlin-collection's iterator fallback).
static class ForInLowering
{
    const string IntRangeFqn = "kotlin.ranges.IntRange";
    const string IntProgressionFqn = "kotlin.ranges.IntProgression";
    static int _tmp;

    // The iterable FQNs whose for-loop enumerates via GetEnumerator (IEnumerable) in a stdlib self-build — the exact
    // set kotc's retired isStdlibCollectionIterable walked. A source whose static type is EXACTLY
    // `kotlin.sequences.Sequence` is caught earlier by IsNetOrSequenceEnumerable (string-keyed, all builds), but a
    // CONCRETE Sequence-implementing class (a `DropTakeSequence` etc.) arrives as a `forIn` with its own FQN and must
    // resolve to Sequence through this supertype walk — so Sequence is in the set too (it never matches a real forIn
    // in an app build, which does not run this walk).
    static readonly HashSet<string> CollectionFqns = new(StringComparer.Ordinal)
    {
        "kotlin.collections.Iterable", "kotlin.collections.MutableIterable",
        "kotlin.collections.Collection", "kotlin.collections.MutableCollection",
        "kotlin.collections.List", "kotlin.collections.MutableList",
        "kotlin.collections.Set", "kotlin.collections.MutableSet",
        "kotlin.sequences.Sequence",
    };

    // The stdlib progression FQNs a consumer-build `a downTo b` produces. The counted `for` is an Int32 counter (as
    // ilemit's `for` always was); Long/Char match kotc's old operator-name coverage unchanged (kotc direct-lowered any
    // 2-arg call named `downTo` to the same Int32 `for` node).
    // ONLY IntProgression: ilemit's counted `for` uses an int32 counter (Emitter.Statements.cs), so lowering a
    // Long/Char `downTo` to a counted for would emit width-mismatched IL. Long/CharProgression `downTo` therefore
    // falls through to the ordinary iterator path (correct, just not the counted-for fast path).
    static readonly HashSet<string> DownToProgressionFqns = new(StringComparer.Ordinal)
    {
        "kotlin.ranges.IntProgression",
    };

    public static void Apply(JsonNode root, bool stdlibBuild, IReadOnlyDictionary<string, List<string>> typeSupers,
        HashSet<string> localTopLevelFns, ReferenceMetadataIndex refs) => Walk(root, stdlibBuild, typeSupers, localTopLevelFns, refs);

    static void Walk(JsonNode node, bool stdlibBuild, IReadOnlyDictionary<string, List<string>> typeSupers,
        HashSet<string> localTopLevelFns, ReferenceMetadataIndex refs)
    {
        switch (node)
        {
            case JsonObject obj:
                Rewrite(obj, stdlibBuild, typeSupers, localTopLevelFns, refs);
                foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value, stdlibBuild, typeSupers, localTopLevelFns, refs);
                break;
            case JsonArray arr:
                foreach (var it in arr) if (it != null) Walk(it, stdlibBuild, typeSupers, localTopLevelFns, refs);
                break;
        }
    }

    static void Rewrite(JsonObject o, bool stdlibBuild, IReadOnlyDictionary<string, List<string>> typeSupers,
        HashSet<string> localTopLevelFns, ReferenceMetadataIndex refs)
    {
        if (Str(o["k"]) != "forIn") return;
        if (IsCountedRange(o["srcType"], stdlibBuild)) ReplaceWith(o, BuildForRange(o, o["src"]));
        else if (!stdlibBuild && TryBuildDownTo(o, localTopLevelFns) is JsonObject dt) ReplaceWith(o, dt);
        else if (IsNetOrSequenceEnumerable(o["srcType"], refs)) ReplaceWith(o, BuildForEachInline(o));
        else if (stdlibBuild && IsStdlibCollection(o["srcType"], typeSupers)) ReplaceWith(o, BuildForEachInline(o));
        else if (o["fallback"] is JsonObject fb) ReplaceWith(o, fb);
    }

    // The kotc-retired `forInEnumerable` gate, moved here (#73-w3): a for-loop source enumerates via GetEnumerator
    // (`forEachInline`) when its static type is EXACTLY `kotlin.sequences.Sequence`, OR it resolves to a referenced
    // .NET type (a facadegen-injected `@Clr` owner — the faithful equivalent of kotc's old `clrName(src) != null`,
    // since ResolveNetType returns null for every kotlin.*/kotlinx.*/dotkt*/app-local FQN and non-null exactly for a
    // reachable .NET type). Applies in ALL builds (the gate was build-agnostic). A concrete `Sequence`-implementing
    // class is NOT matched here (its FQN is not `Sequence`) — it reaches `forEachInline` via the stdlib supertype walk.
    static bool IsNetOrSequenceEnumerable(JsonNode srcType, ReferenceMetadataIndex refs)
    {
        var t = TypeJson.Read(srcType);
        while (t is TypeNode.Nullable nu) t = nu.Of;   // a for-in source is non-null in valid Kotlin; unwrap defensively
        if (t is not TypeNode.Fqn f) return false;
        var fqn = ReferenceMetadataIndex.BareOwnerFqn(f.Name);
        if (fqn == "kotlin.sequences.Sequence") return true;
        return refs != null && refs.ResolveNetType(fqn, f.Args?.Length ?? 0) != null;
    }

    // A counted range whose for-loop is realized as a get_first/get_last counter: IntRange in any build; IntProgression
    // only in a stdlib self-build (there it is emitted locally, so RangeForLowering's stdlib form can read get_step).
    static bool IsCountedRange(JsonNode srcType, bool stdlibBuild) =>
        TypeJson.Read(srcType) is TypeNode.Fqn f
        && (f.Name == IntRangeFqn || (stdlibBuild && f.Name == IntProgressionFqn));

    // A stdlib collection source (stdlib self-build only): its srcType FQN is — or transitively derives from — a
    // kotlin.collections iterable, walking the compilation's own type defs (the same supertype walk kotc's retired
    // isStdlibCollectionIterable did over the IR hierarchy).
    static bool IsStdlibCollection(JsonNode srcType, IReadOnlyDictionary<string, List<string>> typeSupers)
    {
        var t = TypeJson.Read(srcType);
        while (t is TypeNode.Nullable nu) t = nu.Of;   // a for-in source is non-null in valid Kotlin; unwrap defensively
        if (t is not TypeNode.Fqn f) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool Walk(string name)
        {
            if (!seen.Add(name)) return false;
            if (CollectionFqns.Contains(name)) return true;
            return typeSupers.TryGetValue(name, out var sup) && sup.Any(Walk);
        }
        return Walk(f.Name);
    }

    // A consumer-build `for (i in a downTo b)`: FQN-keyed off the progression srcType + the stdlib `downTo` call
    // identity (owner-less callStatic, 2 args, not a user-shadowed local `downTo`). Emits the counted `for` kotc used
    // to emit directly (>=, step -1) but with side-effect-safe temp bounds — ilemit's `for` re-evaluates the `to`
    // bound each iteration, so a side-effecting bound must be snapshot first. A user `infix fun X.downTo` (shadowing)
    // or an outer `... step k` call (method != "downTo") falls through to the iterator fallback (correct semantics —
    // the old name-only match miscompiled the shadow case). Returns null when it is not a recognized stdlib downTo.
    static JsonObject TryBuildDownTo(JsonObject o, HashSet<string> localTopLevelFns)
    {
        if (TypeJson.Read(o["srcType"]) is not TypeNode.Fqn f || !DownToProgressionFqns.Contains(f.Name)) return null;
        if (o["src"] is not JsonObject src) return null;
        if (Str(src["k"]) != "callStatic" || src["owner"] != null || Str(src["method"]) != "downTo") return null;
        if (src["args"] is not JsonArray args || args.Count != 2) return null;
        if (src["sig"] is not JsonArray sig || sig.Count != 2) return null;
        if (localTopLevelFns.Contains("downTo")) return null;   // a same-assembly user downTo -> real iterator semantics

        var id = System.Threading.Interlocked.Increment(ref _tmp);
        var fromName = "$downTo$" + id + "$from";
        var toName = "$downTo$" + id + "$to";
        JsonObject VarDecl(string name, JsonNode type, JsonNode init) => new()
        {
            ["k"] = "var", ["name"] = name, ["type"] = type?.DeepClone(), ["init"] = init?.DeepClone(),
        };
        var forStmt = new JsonObject
        {
            ["k"] = "for",
            ["label"] = o["label"]?.DeepClone(),
            ["var"] = o["var"]?.DeepClone(),
            ["from"] = new JsonObject { ["k"] = "local", ["name"] = fromName },
            ["to"] = new JsonObject { ["k"] = "local", ["name"] = toName },
            ["cmp"] = ">=",
            ["step"] = -1,
            ["body"] = o["body"]?.DeepClone(),
        };
        return new JsonObject
        {
            ["k"] = "block",
            ["body"] = new JsonArray
            {
                VarDecl(fromName, sig[0], args[0]),
                VarDecl(toName, sig[1], args[1]),
                forStmt,
            },
        };
    }

    // forIn / forEachInline -> the faithful forRange kotc used to emit (k, label, var, range, rangeType, body).
    static JsonObject BuildForRange(JsonObject o, JsonNode range) => new()
    {
        ["k"] = "forRange",
        ["label"] = o["label"]?.DeepClone(),
        ["var"] = o["var"]?.DeepClone(),
        ["range"] = range?.DeepClone(),
        ["rangeType"] = o["srcType"]?.DeepClone(),
        ["body"] = o["body"]?.DeepClone(),
    };

    // A stdlib-collection forIn -> the forEachInline (GetEnumerator) kotc used to emit for it directly, keys in the
    // same post-strip order (k, label, elem, src, var, body) — the transient srcType/fallback are dropped.
    static JsonObject BuildForEachInline(JsonObject o) => new()
    {
        ["k"] = "forEachInline",
        ["label"] = o["label"]?.DeepClone(),
        ["elem"] = o["elem"]?.DeepClone(),
        ["src"] = o["src"]?.DeepClone(),
        ["var"] = o["var"]?.DeepClone(),
        ["body"] = o["body"]?.DeepClone(),
    };

    static void ReplaceWith(JsonObject o, JsonObject repl)
    {
        foreach (var key in o.Select(kv => kv.Key).ToList()) o.Remove(key);
        foreach (var kv in repl) o[kv.Key] = kv.Value?.DeepClone();
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
