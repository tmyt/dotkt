using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// TOP-LEVEL REIFIED ENUM INTRINSICS (#73). kotc emits the FAITHFUL top-level call for `enumValues<T>()` /
// `enumValueOf<T>(name)` / `enums.enumEntries<T>()` / `enumEntriesIntrinsic<T>()` — a plain
// `callStatic owner:null method:"enumValues" typeArgs:[T] args:[…]` (the Kotlin fact; it names no CLR shape and
// carries no `kotlin.*` FQN of the intrinsic). On the CLR every type arg is REIFIED, so these lower at the call
// site exactly like `T.values()` / `T.valueOf(name)` — a Kotlin<->CLR equivalence, so it lives HERE:
//
//   enumValues<T>() / enumEntries<T>()  -> rich enum: callStatic <T>.values()          ; basic/gp: {k:enumValues,type:T}
//   enumValueOf<T>(name)                -> rich enum: callStatic <T>.valueOf(name)      ; basic/gp: {k:enumParse,type:T,arg}
//
// RICH vs BASIC is derived from the enum type's EMITTED SHAPE: a local enum whose declaration is a plain class
// carrying `enumRich:true` (ctor params / user methods / per-entry bodies -> a singleton class, invisible to
// System.Enum reflection) takes the synthesized static values()/valueOf(); every other T (a local `kind:"enum"`,
// a generic-param `gp:T`, a referenced enum) takes the semantic node (System.Enum.GetValues / Enum.Parse). A
// referenced RICH enum cannot be distinguished here (no enumRich on the ref.dll) — but a top-level enumValues over
// a cross-module rich enum is not a shape this project emits (rich enums are consumed via `.entries`/`.values()`).
//
// Runs EARLY (grouped with the range/array/char faithful recognitions, before MemberCallSubstitution / BirTypeLowering)
// so the produced enumValues/enumParse/values nodes flow through every downstream pass exactly as kotc's retired
// call-site interception used to — byte-identical CIR.
static class EnumIntrinsicLowering
{
    static readonly HashSet<string> Names = new(System.StringComparer.Ordinal)
    { "enumValues", "enumValueOf", "enumEntries", "enumEntriesIntrinsic" };

    // Collect the local RICH-enum type names (a `kind:"class"` decl carrying the faithful `enumRich:true` marker),
    // across ALL input files (a cross-file `enumValues<E>()` may name an enum declared in another input).
    public static HashSet<string> CollectRichEnums(IEnumerable<JsonNode> roots)
    {
        var set = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var root in roots)
            if (root is JsonObject ro && ro["types"] is JsonArray ts)
                foreach (var t in ts)
                    if (t is JsonObject to && Str(to["kind"]) == "class"
                        && (to["enumRich"] as JsonValue)?.TryGetValue<bool>(out var r) == true && r
                        && Str(to["name"]) is string n)
                        set.Add(n);
        return set;
    }

    public static void Apply(JsonNode root, ISet<string> richEnums, ISet<string> localTopLevelFns, bool appBuild)
        => Walk(root, richEnums, localTopLevelFns, appBuild);

    static void Walk(JsonNode node, ISet<string> rich, ISet<string> local, bool app)
    {
        switch (node)
        {
            case JsonObject obj:
                Rewrite(obj, rich, local, app);
                foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value, rich, local, app);
                break;
            case JsonArray arr:
                foreach (var it in arr) if (it != null) Walk(it, rich, local, app);
                break;
        }
    }

    static void Rewrite(JsonObject o, ISet<string> rich, ISet<string> local, bool app)
    {
        // The Kotlin frontend may preserve the synthesized `EnumClass.entries` property as an ownerful static
        // property read. Its semantic identity is structural: a zero-argument `entries` getter on E returning
        // EnumEntries<E>. Realize that Kotlin enum operation here, alongside the top-level enumEntries intrinsic,
        // rather than asking ilemit to find a physical `E.get_entries` method (a CLR enum has no such member).
        if (app && Str(o["k"]) == "callStatic"
            && Str(o["prop"]) == "get"
            && Str(o["method"]) is "entries" or "get_entries"
            && o["ownerType"] is JsonNode ownerNode
            && (o["args"] as JsonArray)?.Count == 0
            && TypeJson.Read(ownerNode) is TypeNode.Fqn owner
            && TypeJson.Read(o["ret"]) is TypeNode.Fqn ret
            && ret.Name == "kotlin.enums.EnumEntries"
            && ret.Args is { Length: 1 }
            && ret.Args[0] is TypeNode.Fqn enumType
            && enumType.Name == owner.Name)
        {
            JsonNode replacement = rich.Contains(owner.Name)
                ? new JsonObject
                {
                    ["k"] = "callStatic",
                    ["owner"] = TypeJson.Write(enumType),
                    ["method"] = "values",
                    ["args"] = new JsonArray(),
                }
                : new JsonObject
                {
                    ["k"] = "enumValues",
                    ["type"] = TypeJson.Write(enumType),
                };
            Replace(o, replacement);
            return;
        }

        if (Str(o["k"]) != "callStatic" || o["owner"] != null) return;
        var method = Str(o["method"]);
        if (method == null || !Names.Contains(method)) return;
        if (o["typeArgs"] is not JsonArray ta || ta.Count != 1) return;
        var args = o["args"] as JsonArray ?? new JsonArray();
        var isValueOf = method == "enumValueOf";
        var isEntries = method is "enumEntries" or "enumEntriesIntrinsic";
        // Arity gate (matches the intrinsic signatures) — also disambiguates the non-reified `enumEntries(provider)`
        // stdlib helper (1 arg) from the 0-arg `enums.enumEntries<T>()` intrinsic.
        if (args.Count != (isValueOf ? 1 : 0)) return;
        // A user top-level `fun enumValues<T>()` shadow (app build) is NOT the kotlin.* intrinsic — leave it be.
        if (app && local.Contains(method)) return;
        // The entries family is NOT intercepted under a stdlib self-build (Metadata|Runtime): the rt-emitted
        // `enumEntries<T>` body would return `T[]` where its declared return is the `EnumEntries<T>` interface
        // (invalid IL) — its filler body stays; only App-build call sites are intercepted.
        if (isEntries && !app) return;

        var tArg = ta[0];
        var isRich = TypeJson.Read(tArg) is TypeNode.Fqn f && rich.Contains(f.Name);

        JsonNode repl;
        if (isRich)
            repl = new JsonObject
            {
                ["k"] = "callStatic",
                ["owner"] = tArg?.DeepClone(),
                ["method"] = isValueOf ? "valueOf" : "values",
                ["args"] = isValueOf ? new JsonArray(args[0]?.DeepClone()) : new JsonArray(),
            };
        else if (isValueOf)
            repl = new JsonObject { ["k"] = "enumParse", ["type"] = tArg?.DeepClone(), ["arg"] = args[0]?.DeepClone() };
        else
            repl = new JsonObject { ["k"] = "enumValues", ["type"] = tArg?.DeepClone() };

        Replace(o, repl);
    }

    static void Replace(JsonObject target, JsonNode replacement)
    {
        foreach (var key in target.Select(kv => kv.Key).ToList()) target.Remove(key);
        foreach (var kv in (JsonObject)replacement) target[kv.Key] = kv.Value?.DeepClone();
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
