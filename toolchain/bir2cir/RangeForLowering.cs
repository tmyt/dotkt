using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Range for-loop CLR realization (#52 Phase 5, the "range partial"). kotc emits a FAITHFUL `forRange` node carrying
// ONLY the range VALUE (`range`), the loop var, and the range's own Kotlin type (`rangeType`) — NO CLR accessor
// names/owner. This pass OWNS the Kotlin<->CLR range realization (bir2cir is the Kotlin<->CLR layer, where CLR
// knowledge belongs): the IntProgression accessor getters (get_first/get_last/get_step) + the owner FQN are DERIVED
// here. Two forms, dispatched by build mode:
//
//   stdlib build (StdlibMode != App — ref + rt): IntProgression is emitted LOCALLY, so keep the `forRange` and
//     inject accessOwner/firstM/lastM/stepM. ilemit's forRange resolves `_types[accessOwner].Methods[firstM]`
//     generically, holding NO hardcoded kotlin.ranges knowledge in the IL backend.
//
//   app build: IntProgression is only REFERENCED (not emitted), so ilemit cannot resolve it off `_types`. Rewrite to
//     a counter loop reading first/last as ordinary cross-module property getters:
//         block{ var __rng = <range>; for(i = __rng.get_first(); i <= __rng.get_last(); i += 1) { <body> } }
//     An IntRange is always step-1 ascending, so `<=`/step 1 is exact (an empty range has first > last -> the body
//     never runs). Spilling the range into `__rng` reads first/last off the SAME (possibly side-effecting) value.
//
// Runs FIRST in the per-file loop, BEFORE every other pass — so the app-build callInstance and the forRange body flow
// through the SAME downstream passes (MemberCallSubstitution, BirTypeLowering, ...) the equivalent kotc-emitted forms
// used to, keeping the final IL byte-identical. `rangeType` is a transient hint: consumed by the app form (the `__rng`
// local's type) and dropped by the stdlib form.
static class RangeForLowering
{
    static int _tmp;

    public static void Apply(JsonNode node, bool stdlibBuild)
    {
        if (node is JsonObject o)
        {
            // A kotc-emitted faithful forRange has a `range` value and NO `accessOwner` (distinguishing it from a
            // forRange this pass already realized — idempotent, though it runs once).
            if (Str(o["k"]) == "forRange" && o["range"] != null && o["accessOwner"] == null)
            {
                if (stdlibBuild) StdlibForm(o);
                else AppForm(o);
            }
            foreach (var kv in o) if (kv.Value != null) Apply(kv.Value, stdlibBuild);
        }
        else if (node is JsonArray a)
            foreach (var it in a) if (it != null) Apply(it, stdlibBuild);
    }

    // stdlib build: keep `forRange`, inject the IntProgression accessor owner + getter names; drop the transient
    // rangeType hint (ilemit's forRange declares the range local off `range` and the loop var as int).
    static void StdlibForm(JsonObject o)
    {
        o.Remove("rangeType");
        o["accessOwner"] = "kotlin.ranges.IntProgression";
        o["firstM"] = "get_first";
        o["lastM"] = "get_last";
        o["stepM"] = "get_step";
    }

    // app build: rewrite the `forRange` in place into block{ var __rng; for{ get_first .. get_last, step 1 } }.
    static void AppForm(JsonObject o)
    {
        var id = System.Threading.Interlocked.Increment(ref _tmp);
        var rng = "__rng$" + id;
        var label = o["label"];
        var loopVar = Str(o["var"]);
        var rangeType = o["rangeType"];
        var range = o["range"];
        var body = o["body"] as JsonArray ?? new JsonArray();

        JsonObject Acc(string m) => new JsonObject
        {
            ["k"] = "callInstance",
            ["ownerType"] = TypeJson.Fqn("kotlin.ranges.IntProgression"),
            ["virtual"] = true,
            ["recv"] = new JsonObject { ["k"] = "local", ["name"] = rng },
            ["method"] = m,
            ["args"] = new JsonArray(),
        };

        var varStmt = new JsonObject
        {
            ["k"] = "var", ["name"] = rng, ["type"] = rangeType?.DeepClone(), ["init"] = range?.DeepClone(),
        };
        var forStmt = new JsonObject
        {
            ["k"] = "for", ["label"] = label?.DeepClone(), ["var"] = loopVar,
            ["from"] = Acc("get_first"), ["to"] = Acc("get_last"),
            ["cmp"] = "<=", ["step"] = 1, ["body"] = body.DeepClone(),
        };

        foreach (var key in o.Select(kv => kv.Key).ToList()) o.Remove(key);
        o["k"] = "block";
        o["body"] = new JsonArray { varStmt, forStmt };
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
