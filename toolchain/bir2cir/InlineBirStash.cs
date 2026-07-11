using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using DotKt.Bir;

// INLINE-BIR STASH (#71/#75 S1). Runs FIRST — before EVERY lowering pass — over every input file. For each `mods.inline`
// method it deep-clones the RAW pre-lowering facts {v,fqn,owner,recv,typeParams,params,ret,body} and stores them as ONE
// OPAQUE STRING field `"inlineBir"` = base64(BirCarrier.EncodeBody(JsonV1, payload)). Encoding AT STASH TIME is load-
// bearing: every downstream walker (BirTypeLowering, RefBodySquash, …) then sees an inert JsonValue string and cannot
// descend into / rewrite the captured body. ilemit later stamps that string VERBATIM as the [KotlinInline] carrier
// (base64-decode -> the (version, byte[]) ctor args) — the payload is now RAW BIR (re-lowerable in the app context),
// not the post-lowering/post-squash CIR the old ilemit ApplyKotlinInline built from `params`+`body`.
//
// Also feeds the in-memory index `owner|name|pc|ga -> [raw decl facts]` for SAME-module splices (InlineSplice reads it once
// kotc emits `inlineSpliceCallSameModule` in #75 S4b). RefBodySquash is UNTOUCHED: it squashes `body` to the throw sentinel;
// the opaque `inlineBir` string rides through unmodified (the [KotlinInline] carrier survives on the squashed ref decl).
//
// OWNER: a TOP-LEVEL fun lives in `root.methods` — its owner is the file-facade class `root.fileClass` (the .NET type
// ilemit defines and stamps [KotlinInline] on, and the type callInline.owner names). A MEMBER fun lives in a
// `root.types[].methods` — owner = that type's `name`. `pc` = params.Count (kotc emits an extension receiver as a
// leading `__self` param, so this already counts it); `ga` = typeParams.Count.
//
// OVERLOAD KEY (§4.2, #75 S4b): the key is `owner|name|pc|ga` and maps to a LIST of candidate payloads — same-name inline
// OVERLOADS collide on it (Duration.toComponents ×4 differ in lambda arity; flatMap/maxOf differ in the lambda RETURN type;
// the retired recv0 = first-param FQN could see NONE of those). The call site disambiguates by a STRUCTURAL match of each
// candidate's declared `params[i].type` against the callInline's `paramSig[i]` (see SelectByParamSig). The cross-module read
// (ReferenceMetadataIndex) keys + disambiguates identically.
static class InlineBirStash
{
    // owner|name|pc|ga -> candidate raw-decl-fact payloads (one per overload sharing that key). Spans all files of ONE run.
    public static readonly Dictionary<string, List<JsonObject>> Index = new(StringComparer.Ordinal);

    // OWNER-LESS same-module index: "name|pc|ga" -> the `kotlin.*` candidate payloads across owners (the SAME-MODULE twin of
    // ReferenceMetadataIndex._ownerlessInlineCandidates). In the stdlib SELF-BUILD a `kotlin.*` scope-fn/@InlineOnly call
    // (apply/let/also/run/with, forwarded map->mapTo, …) is emitted OWNER-LESS by kotc (it does not name the klib file class)
    // but its target is being compiled THIS run — so it is in the stash, NOT the ref.dll. InlineSplice consults this to
    // resolve/forward such a call same-module (the winner's own `owner` names the host). Restricted to `kotlin.*` (a user
    // owner-less inline fn cannot exist — kotc names user owners).
    public static readonly Dictionary<string, List<JsonObject>> OwnerlessIndex = new(StringComparer.Ordinal);

    public static void Reset() { Index.Clear(); OwnerlessIndex.Clear(); }

    // The same-module owner-less candidates for name|pc|ga (kotlin.* across owners), or null. InlineSplice selects the
    // unique paramSig match and reads the winner's `owner`.
    public static List<JsonObject> OwnerlessCandidates(string name, int pc, int ga) =>
        name != null && OwnerlessIndex.TryGetValue($"{name}|{pc}|{ga}", out var lst) && lst.Count > 0 ? lst : null;

    public static void Stash(JsonNode root)
    {
        if (root is not JsonObject o) return;
        var fileClass = Str(o["fileClass"]);
        if (fileClass != null && o["methods"] is JsonArray topMethods)
            foreach (var m in topMethods) if (m is JsonObject mo) StashMethod(fileClass, mo);
        if (o["types"] is JsonArray types)
            foreach (var t in types) if (t is JsonObject to) StashType(to);
    }

    static void StashType(JsonObject type)
    {
        if (Str(type["name"]) is string owner && type["methods"] is JsonArray methods)
            foreach (var m in methods) if (m is JsonObject mo) StashMethod(owner, mo);
        if (type["types"] is JsonArray nested)
            foreach (var t in nested) if (t is JsonObject to) StashType(to);
    }

    static void StashMethod(string owner, JsonObject mo)
    {
        if (mo["mods"] is not JsonObject mods || Bool(mods["inline"]) != true) return;
        if (Str(mo["name"]) is not string name || mo["body"] is not JsonArray) return;

        var typeParams = mo["typeParams"] as JsonArray;
        var prms = mo["params"] as JsonArray;
        int pc = prms?.Count ?? 0;
        int ga = typeParams?.Count ?? 0;

        // recv classification (drives the splicer's guard): a leading `__self` param = extension receiver; a non-static
        // instance member = a `{k:this}` dispatch receiver; else none.
        string firstParam = prms != null && prms.Count > 0 ? Str((prms[0] as JsonObject)?["name"]) : null;
        string recv = firstParam == "__self" ? "extensionParam"
                    : (Bool(mo["static"]) == false ? "dispatch" : "none");

        var payload = new JsonObject
        {
            ["v"] = 1,
            ["fqn"] = owner + "." + name,
            ["owner"] = owner,
            ["recv"] = recv,
            ["typeParams"] = typeParams?.DeepClone() ?? new JsonArray(),
            ["params"] = prms?.DeepClone() ?? new JsonArray(),
            ["ret"] = mo["ret"]?.DeepClone(),
            ["body"] = mo["body"].DeepClone(),
        };

        // §4.2 (#75 S4b): the overload key is `owner|name|pc|ga` -> a LIST of candidates (same-name inline OVERLOADS share
        // it). The call site picks the UNIQUE candidate whose declared `params[i].type` structurally equals the call's
        // `paramSig[i]` (SelectByParamSig). The cross-module reader (ReferenceMetadataIndex) keys + disambiguates the same.
        var key = $"{owner}|{name}|{pc}|{ga}";
        if (!Index.TryGetValue(key, out var lst)) Index[key] = lst = new List<JsonObject>();
        lst.Add((JsonObject)payload.DeepClone());

        // Same-module owner-less index (kotlin.* only): the twin lookup an owner-less self-build call resolves against.
        // Restricted to owner-less-ELIGIBLE fns (top-level statics + extensions — recv != "dispatch"): kotc names a MEMBER
        // owner, so a member never arrives owner-less, and admitting one only adds a false paramSig-tie/dispatch candidate.
        if (owner.StartsWith("kotlin.", StringComparison.Ordinal) && recv != "dispatch")
        {
            var npg = $"{name}|{pc}|{ga}";
            if (!OwnerlessIndex.TryGetValue(npg, out var olst)) OwnerlessIndex[npg] = olst = new List<JsonObject>();
            olst.Add((JsonObject)payload.DeepClone());
        }

        byte[] enc = BirCarrier.EncodeBody(BirCarrier.JsonV1, payload);
        mo["inlineBir"] = Convert.ToBase64String(enc);
    }

    // §4.2 (#75 S4b) — STRUCTURAL overload selection. From candidate decl-fact payloads sharing `owner|name|pc|ga`, return
    // the UNIQUE one whose declared `params[i].type` equals the call site's `paramSig[i]` for every i (JsonNode.DeepEquals).
    // Both sides are kotc-emitted type-node JSON (`birType(param.type)`) in the callee's OWN un-substituted type-param frame
    // — the callInline's `paramSig`, the call's `sig`, and the round-tripped ref payload's `params` all share that source —
    // so structural equality is exact and serializer-independent. `matchCount` reports how many matched; the caller fails
    // loud unless it is exactly 1 (0 = no signature match; >=2 = structurally-ambiguous overloads, e.g. differ only in
    // generic bounds like `ifEmpty`, which are never called with an escaping lambda so never reach the splicer).
    internal static JsonObject SelectByParamSig(IReadOnlyList<JsonObject> candidates, JsonArray paramSig, out int matchCount)
    {
        matchCount = 0;
        JsonObject hit = null;
        if (candidates != null)
            foreach (var c in candidates)
                if (ParamSigMatches(c?["params"] as JsonArray, paramSig)) { matchCount++; hit = c; }
        return matchCount == 1 ? hit : null;
    }

    static bool ParamSigMatches(JsonArray declParams, JsonArray paramSig)
    {
        if (declParams == null || paramSig == null || declParams.Count != paramSig.Count) return false;
        for (int i = 0; i < declParams.Count; i++)
            if (!JsonNode.DeepEquals((declParams[i] as JsonObject)?["type"], paramSig[i])) return false;
        return true;
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
    static bool? Bool(JsonNode n) => (n as JsonValue)?.TryGetValue<bool>(out var b) == true ? b : (bool?)null;
}
