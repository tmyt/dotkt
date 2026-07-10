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
// Also feeds the in-memory index `owner|name|pc|ga -> raw decl facts` for SAME-module splices. That index is dormant in
// S1 (kotc mechanism-1 still splices same-module inline at BIR time; only the CROSS-module path — kotc `callInline` +
// bir2cir InlineSplice reading [KotlinInline] off the --ref'd assembly — is re-homed here). It is forward infra for the
// S4 same-module retirement. RefBodySquash is UNTOUCHED: it squashes `body` to the throw sentinel; the opaque
// `inlineBir` string rides through unmodified (the [KotlinInline] carrier survives on the squashed ref decl).
//
// OWNER: a TOP-LEVEL fun lives in `root.methods` — its owner is the file-facade class `root.fileClass` (the .NET type
// ilemit defines and stamps [KotlinInline] on, and the type callInline.owner names). A MEMBER fun lives in a
// `root.types[].methods` — owner = that type's `name`. `pc` = params.Count (kotc emits an extension receiver as a
// leading `__self` param, so this already counts it — the SAME overload key ilemit's old splice used); `ga` =
// typeParams.Count. The cross-module read (ReferenceMetadataIndex) keys off the SAME owner|name|pc|ga.
static class InlineBirStash
{
    // owner|name|pc|ga -> the raw decl facts payload. Spans all files of ONE run.
    public static readonly Dictionary<string, JsonObject> Index = new(StringComparer.Ordinal);

    public static void Reset() => Index.Clear();

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
        // instance member = a `{k:this}` dispatch receiver (S1 never splices these — the splicer falls back); else none.
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

        // D6: the overload key owner|name|pc|ga is NOT unique — `IntArray.forEach`/`LongArray.forEach`/… on one facade
        // all key `forEach|2|0`. A last-wins overwrite would silently splice a wrong-typed body. POISON a colliding key
        // (mark it null) so the resolver returns no payload -> plain-call fallback, never a mis-typed splice. (The real
        // fix — widening the key with the first-param type — lands with the S3 same-module retirement.) We still stamp
        // the per-method `inlineBir` (the .NET overload IS distinguished by full signature on the ref.dll; only OUR
        // coarse index collides), so the cross-module reader (ReferenceMetadataIndex) mirrors the same poisoning.
        var key = $"{owner}|{name}|{pc}|{ga}";
        if (Index.ContainsKey(key)) Index[key] = null;          // collision -> poisoned (null)
        else Index[key] = (JsonObject)payload.DeepClone();

        byte[] enc = BirCarrier.EncodeBody(BirCarrier.JsonV1, payload);
        mo["inlineBir"] = Convert.ToBase64String(enc);
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
    static bool? Bool(JsonNode n) => (n as JsonValue)?.TryGetValue<bool>(out var b) == true ? b : (bool?)null;
}
