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
// Also feeds the in-memory index `owner|name|pc|ga|recv0 -> raw decl facts` for SAME-module splices. That index is dormant in
// S1 (kotc mechanism-1 still splices same-module inline at BIR time; only the CROSS-module path — kotc `callInline` +
// bir2cir InlineSplice reading [KotlinInline] off the --ref'd assembly — is re-homed here). It is forward infra for the
// S4 same-module retirement. RefBodySquash is UNTOUCHED: it squashes `body` to the throw sentinel; the opaque
// `inlineBir` string rides through unmodified (the [KotlinInline] carrier survives on the squashed ref decl).
//
// OWNER: a TOP-LEVEL fun lives in `root.methods` — its owner is the file-facade class `root.fileClass` (the .NET type
// ilemit defines and stamps [KotlinInline] on, and the type callInline.owner names). A MEMBER fun lives in a
// `root.types[].methods` — owner = that type's `name`. `pc` = params.Count (kotc emits an extension receiver as a
// leading `__self` param, so this already counts it — the SAME overload key ilemit's old splice used); `ga` =
// typeParams.Count. The cross-module read (ReferenceMetadataIndex) keys off the SAME owner|name|pc|ga|recv0 (§4.2).
static class InlineBirStash
{
    // owner|name|pc|ga|recv0 -> the raw decl facts payload. Spans all files of ONE run.
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
        string recv0 = Recv0Of(prms);

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

        // D6 / §4.2 (#75 S4a): the overload key is `owner|name|pc|ga|recv0` (recv0 = first param's type FQN, "-" when
        // none). The bare owner|name|pc|ga collides — `IntArray.forEach`/`LongArray.forEach`/`Iterable.forEach` on one
        // facade all key `forEach|1|1` — and under #95 a collision that is HIT is a fail-loud miscompile (the fallback
        // slot is gone), so we DISAMBIGUATE by the receiver-param FQN rather than merely poisoning. A residual collision
        // AFTER widening (a genuine same-recv0 overload) still poisons to null -> the splicer fails loud on that key. The
        // cross-module reader (ReferenceMetadataIndex) keys off the SAME `owner|name|pc|ga|recv0`.
        var key = $"{owner}|{name}|{pc}|{ga}|{recv0}";
        if (Index.ContainsKey(key)) Index[key] = null;          // residual collision -> poisoned (null)
        else Index[key] = (JsonObject)payload.DeepClone();

        byte[] enc = BirCarrier.EncodeBody(BirCarrier.JsonV1, payload);
        mo["inlineBir"] = Convert.ToBase64String(enc);
    }

    // recv0 (§4.2 overload-key disambiguator): the FQN name of the first param's type — for an extension fn that is the
    // leading `__self` receiver type, so `Iterable.forEach`/`IntArray.forEach` split. "-" when there is no first param or
    // its type is not a plain fqn (a bare type-param receiver — no overload family keys off a tv). A pure Kotlin FQN,
    // layer-clean. Computed identically by kotc (call site) and ReferenceMetadataIndex (cross-module) so the keys agree.
    internal static string Recv0Of(JsonArray prms)
    {
        if (prms == null || prms.Count == 0) return "-";
        if (prms[0] is JsonObject p0 && p0["type"] is JsonObject t && Str(t["t"]) == "fqn" && Str(t["name"]) is string fq)
            return fq;
        return "-";
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
    static bool? Bool(JsonNode n) => (n as JsonValue)?.TryGetValue<bool>(out var b) == true ? b : (bool?)null;
}
