using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// FOR-LOOP SOURCE CLASSIFICATION (#73). kotc no longer decides whether a for-loop source is a counted RANGE vs a
// collection — that needs the `kotlin.ranges.*` FQN, a Kotlin<->CLR relation that lives HERE. kotc emits:
//
//   forIn{src, srcType, var, body, fallback}   — any recovered non-array/non-downTo source it can't classify
//   forEachInline{elem, src, srcType, var, body} — a .NET/Sequence/stdlib-collection enumerable
//
// carrying the source's runtime TYPE TOKEN (`srcType`). This pass dispatches on it:
//
//   srcType is a counted range (kotlin.ranges.IntRange always; IntProgression in a stdlib self-build, where it is
//     emitted locally) -> `forRange` (RangeForLowering then realizes the get_first/get_last[/get_step] counter loop).
//   forIn otherwise -> the `fallback` block (the FIR-desugared iterator protocol kotc used to emit by returning null).
//   forEachInline otherwise -> stays forEachInline (GetEnumerator), with the transient `srcType` stripped.
//
// Runs FIRST in the per-file loop (before RangeForLowering / RangeConstructionLowering / SequenceForEachLowering) so
// the produced forRange / forEachInline / iterator-block flow through every downstream pass exactly as the equivalent
// kotc-emitted forms did — byte-identical in a consumer build (a range's forRange, a .NET forEachInline, a Kotlin-
// collection's iterator fallback all match what kotc used to emit directly).
static class ForInLowering
{
    const string IntRangeFqn = "kotlin.ranges.IntRange";
    const string IntProgressionFqn = "kotlin.ranges.IntProgression";

    public static void Apply(JsonNode root, bool stdlibBuild) => Walk(root, stdlibBuild);

    static void Walk(JsonNode node, bool stdlibBuild)
    {
        switch (node)
        {
            case JsonObject obj:
                Rewrite(obj, stdlibBuild);
                foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value, stdlibBuild);
                break;
            case JsonArray arr:
                foreach (var it in arr) if (it != null) Walk(it, stdlibBuild);
                break;
        }
    }

    static void Rewrite(JsonObject o, bool stdlibBuild)
    {
        var k = Str(o["k"]);
        if (k == "forIn")
        {
            if (IsCountedRange(o["srcType"], stdlibBuild)) ReplaceWith(o, BuildForRange(o, o["src"]));
            else if (o["fallback"] is JsonObject fb) ReplaceWith(o, fb);
            return;
        }
        if (k == "forEachInline" && o["srcType"] is JsonNode)
        {
            if (IsCountedRange(o["srcType"], stdlibBuild)) ReplaceWith(o, BuildForRange(o, o["src"]));
            else o.Remove("srcType");   // a genuine .NET/Sequence enumerable — drop the transient hint, keep forEachInline
        }
    }

    // A counted range whose for-loop is realized as a get_first/get_last counter: IntRange in any build; IntProgression
    // only in a stdlib self-build (there it is emitted locally, so RangeForLowering's stdlib form can read get_step).
    static bool IsCountedRange(JsonNode srcType, bool stdlibBuild) =>
        TypeJson.Read(srcType) is TypeNode.Fqn f
        && (f.Name == IntRangeFqn || (stdlibBuild && f.Name == IntProgressionFqn));

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

    static void ReplaceWith(JsonObject o, JsonObject repl)
    {
        foreach (var key in o.Select(kv => kv.Key).ToList()) o.Remove(key);
        foreach (var kv in repl) o[kv.Key] = kv.Value?.DeepClone();
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
