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
// RICH vs BASIC comes from an explicit producer fact: local BIR carries `enumRich:true`, while a referenced producer
// carries trusted [KotlinRichEnum]. A generic-param T or a basic enum takes the semantic node (System.Enum.GetValues /
// Enum.Parse); no values()/valueOf() name/signature convention is used to classify a referenced owner.
//
// Runs EARLY (grouped with the range/array/char faithful recognitions, before MemberCallSubstitution / BirTypeLowering)
// so the produced enumValues/enumParse/values nodes enter the ordinary CIR pipeline with their semantic enum identity
// still available.
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

    public static void Apply(JsonNode root, ISet<string> richEnums, ISet<string> localTopLevelFns, bool appBuild,
        ReferenceMetadataIndex refs)
        => Walk(root, richEnums, localTopLevelFns, appBuild, refs);

    static void Walk(JsonNode node, ISet<string> rich, ISet<string> local, bool app, ReferenceMetadataIndex refs)
    {
        switch (node)
        {
            case JsonObject obj:
                Rewrite(obj, rich, local, app, refs);
                foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value, rich, local, app, refs);
                break;
            case JsonArray arr:
                foreach (var it in arr) if (it != null) Walk(it, rich, local, app, refs);
                break;
        }
    }

    static void Rewrite(JsonObject o, ISet<string> rich, ISet<string> local, bool app, ReferenceMetadataIndex refs)
    {
        // A referenced rich enum is projected into KLIB as an enum, so kotc faithfully emits the ordinary enum
        // semantic nodes for its synthesized values()/valueOf() declarations. The trusted producer carrier is the
        // sole fact that those operations physically live on a class. Consume its exact API map here; a basic enum
        // remains an enumValues/enumParse node and no member name or class shape is used as a fallback.
        var semanticKind = Str(o["k"]);
        if (semanticKind is "enumValues" or "enumParse"
            && o["type"] is JsonNode semanticTypeNode
            && TypeJson.Read(semanticTypeNode) is TypeNode.Fqn semanticType
            && refs.TryKotlinRichEnumStaticApis(
                semanticType.Name, out var semanticValuesApi, out var semanticValueOfApi))
        {
            var replacement = RichEnumCall(
                semanticTypeNode,
                semanticKind == "enumParse" ? semanticValueOfApi : semanticValuesApi,
                semanticKind == "enumParse" ? o["arg"] : null);
            Replace(o, replacement);
            return;
        }

        // The Kotlin frontend may preserve the synthesized `EnumClass.entries` property as an ownerful static
        // property read. Its semantic identity is structural: a zero-argument `entries` getter on E returning
        // EnumEntries<E>. Realize that Kotlin enum operation here, alongside the top-level enumEntries intrinsic,
        // rather than asking ilemit to find a physical `E.get_entries` method (a CLR enum has no such member).
        if (app && Str(o["k"]) == "callStatic"
            && Str(o["prop"]) == "get"
            && Str(o["method"]) == "entries"
            && o["ownerType"] is JsonNode ownerNode
            && (o["args"] as JsonArray)?.Count == 0
            && TypeJson.Read(ownerNode) is TypeNode.Fqn owner
            && TypeJson.Read(o["ret"]) is TypeNode.Fqn ret
            && ret.Name == "kotlin.enums.EnumEntries"
            && ret.Args is { Length: 1 }
            && ret.Args[0] is TypeNode.Fqn enumType
            && enumType.Name == owner.Name)
        {
            JsonNode replacement;
            if (rich.Contains(owner.Name))
                replacement = RichEnumCall(TypeJson.Write(enumType), "values", null);
            else if (refs.TryKotlinRichEnumStaticApis(owner.Name, out var mappedValues, out _))
                replacement = RichEnumCall(TypeJson.Write(enumType), mappedValues, null);
            else
                replacement = new JsonObject
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
        var richName = TypeJson.Read(tArg) is TypeNode.Fqn f ? f.Name : null;
        var isLocalRich = richName != null && rich.Contains(richName);
        string mappedValuesApi = null;
        string mappedValueOfApi = null;
        var isReferencedRich = richName != null &&
            refs.TryKotlinRichEnumStaticApis(richName, out mappedValuesApi, out mappedValueOfApi);

        JsonNode repl;
        if (isLocalRich || isReferencedRich)
            repl = RichEnumCall(
                tArg,
                isValueOf
                    ? (isReferencedRich ? mappedValueOfApi : "valueOf")
                    : (isReferencedRich ? mappedValuesApi : "values"),
                isValueOf ? args[0] : null);
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

    // A rich enum's synthesized API is a real physical static method. State its exact declaration descriptor at the
    // semantic-to-physical boundary so the ordinary referenced-member resolver can select the MethodDef and attach
    // its durable memberRef; argument expression types are never an overload-resolution substitute.
    static JsonObject RichEnumCall(JsonNode owner, string method, JsonNode valueOfArg)
    {
        var valueOf = valueOfArg != null;
        return new JsonObject
        {
            ["k"] = "callStatic",
            ["owner"] = owner?.DeepClone(),
            ["method"] = method,
            ["args"] = valueOf ? new JsonArray(valueOfArg.DeepClone()) : new JsonArray(),
            ["sig"] = valueOf
                ? new JsonArray(TypeJson.Fqn("System.String"))
                : new JsonArray(),
        };
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
