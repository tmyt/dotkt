using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// STRING -> CharSequence adapter bridge. `kotlin.String` is @ClrTypeAlias("System.String") — a SEALED BCL type whose
// CharSequence face is bound in-place (@ClrIntrinsic Length/get_Chars). `kotlin.CharSequence` has NO BCL equivalent, so
// bir2cir's SharedSyntheticSynthesis synthesizes the monomorphic interface `dotkt$CharSequence` (get_length/get/subSequence). A `System.String`
// (sealed) cannot implement that interface, so a bare String flowing into a `@dotkt$CharSequence` slot crashes
// (InvalidProgram / InvalidCast). This pass MATERIALIZES the coercion: wherever a value whose STATIC type is String
// flows into a CharSequence slot, it inserts `new dotkt$StringCharSequence(theString)` — an App-local adapter class
// this pass ALSO injects, modeled on the proven user `class S : CharSequence` shape (String-backed length/get/
// subSequence delegating to get_Length/get_Chars/Substring). Five sites — a call's CharSequence-typed arg (covers an
// extension receiver, which is arg[0] + sig[0], AND an ordinary CharSequence param), a return into a CharSequence
// return type, a store into a CharSequence-typed local, and an `as CharSequence` cast. It wraps ONLY when the value is
// POSITIVELY a bare String (const string literal, a String-typed local/param read, a String cast, or a String-returning
// call) — never when the value is already a dotkt$CharSequence (StringBuilder / a user CharSequence / another
// wrapper), so it is purely additive: genuine intra-assembly polymorphism (`val cs: CharSequence = "abc"; cs.length`)
// now works, and no existing statically-String-receiver path (kotc's STRING_OPS lowering, which dispatches on the
// String directly) is touched.
//
// WHY app-LOCAL (not a stdlib class): the synthetic `dotkt$CharSequence` is emitted PER-ASSEMBLY — the app defines
// its OWN copy, distinct from the one in the rt stdlib dll. A stdlib adapter would implement the rt-dll copy, which the
// app's interface dispatch (`callvirt <app>::dotkt$CharSequence::get_length`) can't find on it -> EntryPointNotFound.
// So the adapter MUST implement the app's own synthetic -> it is injected into the app assembly, exactly where kotc
// injects the synthetic interface. (This same per-assembly boundary is why calling a *stdlib* CharSequence-extension
// with an app value is a SEPARATE, deeper blocker for the retire-B follow-up — see docs/master-task-inventory.md 4-A.)
//
// APP builds ONLY (gated on attributeTopLevelOwner at the call site — StdlibMode == App), so the ref/rt stdlib
// self-builds stay byte-identical. Runs AFTER MemberCallSubstitution (its emitted `new` is never re-substituted — the
// adapter is not @ClrTypeAlias) and BEFORE BirTypeLowering (so it still sees the kotlin.* / @dotkt$CharSequence type
// vocabulary; the injected type's kotlin.* signature tokens and the wrap node's `type`/`argTypes` are lowered
// afterwards — the injected method bodies are already in CLR-call form, exactly as kotc emits them for `class S`).
// CROSS-MODULE DEFAULT-ARGUMENT SPLICE. A call that OMITS a defaulted argument reaches bir2cir with fewer args than
// the callee's signature (kotc emitted only the provided args — correct). For a callee whose defaulted params carry
// @KotlinDefault (a non-null object/CharSequence default the frontend jar dropped + .NET [DefaultParameterValue]
// metadata cannot carry), this pass reads the default-expression BIR from the ref.dll and SPLICES it as each trailing
// omitted argument. Runs in the app build AFTER MemberCallSubstitution (owner attributed, so the ref.dll callee is
// identifiable) and BEFORE StringCharSequenceBridge + BirTypeLowering (so a spliced String default is CharSequence-
// coerced and type-lowered exactly like an explicit argument). Mirrors the [KotlinInline] body-splice mechanism, but
// for default arguments. Callees with only metadata-representable defaults carry no @KotlinDefault -> untouched (their
// omitted args still ride ilemit's [DefaultParameterValue] backfill). Omission is TRAILING (kotc emits positional
// cross-module calls); a default expression that references earlier params is out of scope (the stdlib RC1 defaults
// are all self-contained constants) — a mixed/gap map bails, leaving the call unchanged.
static class DefaultArgSplice
{
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs) => Walk(root, refs);

    static void Walk(JsonNode node, ReferenceMetadataIndex refs)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value, refs);
            TrySplice(obj, refs);
        }
        else if (node is JsonArray arr) foreach (var it in arr) if (it != null) Walk(it, refs);
    }

    static void TrySplice(JsonObject node, ReferenceMetadataIndex refs)
    {
        var k = Str(node["k"]);
        if (k != "callStatic" && k != "callInstance") return;
        if (node["args"] is not JsonArray args || node["sig"] is not JsonArray sig) return;
        var sigCount = sig.Count;
        var hasPlaceholder = false;
        for (var j = 0; j < args.Count; j++) if (IsPlaceholder(args[j])) { hasPlaceholder = true; break; }
        if (!hasPlaceholder && args.Count >= sigCount) return;           // no omitted arg to fill
        var owner = TypeJson.OwnerName(node["owner"]) ?? TypeJson.OwnerName(node["ownerType"]);
        var method = Str(node["method"]);
        if (owner == null || method == null) return;
        var defaults = refs.KotlinDefaultsFor(owner, method, sigCount);
        if (defaults == null) return;
        // An extension receiver rides args[0] (the `__self` first arg of an emitted extension fun). A `= this` default
        // (substringAfter's missingDelimiterValue, a data-class copy) references it — bind the callee's `this` to it.
        var receiver = args.Count > 0 ? args[0] : null;
        // 1) Replace POSITIONAL `defaultArg` placeholders in place (kotc keeps a later provided arg's slot). Fill by array
        //    index — which equals the @KotlinDefault index (extension receiver counted first, matching kotc's stamp).
        //    A default reading an EARLIER param (`b = a * 10`) rides a `{param N}` token → this call's already-filled args[N]
        //    (Kotlin defaults reference only earlier params, and the loop fills lower indices first, so args[N] is resolved).
        for (var j = 0; j < args.Count; j++)
        {
            if (!IsPlaceholder(args[j])) continue;
            if (!defaults.TryGetValue(j, out var bir)) continue;         // no @KotlinDefault at this slot -> leave it (loud downstream)
            if (SpliceOne(bir, receiver, args) is JsonNode fill) args[j] = fill;
        }
        // 2) Append any purely-TRAILING omitted args (callee carries @KotlinDefault but kotc dropped the tail).
        for (var pos = args.Count; pos < sigCount; pos++)
        {
            if (!defaults.TryGetValue(pos, out var bir)) return;         // gap -> bail (leave the call unchanged)
            if (SpliceOne(bir, receiver, args) is JsonNode fill) args.Add(fill); else return;
        }
    }

    static bool IsPlaceholder(JsonNode n) => n is JsonObject o && Str(o["k"]) == "defaultArg";

    // Parse a @KotlinDefault BIR-json string and bind the callee's default-expression tokens to THIS call's args: `{this}`
    // (an extension receiver) -> the call's receiver, and `{param N}` (a read of another value param) -> the call's arg at
    // index N. A fresh deep clone per occurrence, so each filled value is a self-contained subtree.
    static JsonNode SpliceOne(string bir, JsonNode receiver, JsonArray args)
    {
        JsonNode parsed; try { parsed = JsonNode.Parse(bir); } catch { return null; }
        return SubstituteTokens(parsed, receiver, args);
    }

    // Rebuild `node`, replacing every `{"k":"this"}` with a deep clone of `receiver` and every
    // `{"k":"defaultArgParam","idx":N}` with a deep clone of `args[N]` (the callee's default-scope reads, resolved to this
    // call's values). Rebuilds fresh so no node is attached to two parents.
    static JsonNode SubstituteTokens(JsonNode node, JsonNode receiver, JsonArray args)
    {
        switch (node)
        {
            case JsonObject obj when Str(obj["k"]) == "this":
                return receiver == null ? obj.DeepClone() : receiver.DeepClone();
            case JsonObject obj when Str(obj["k"]) == "defaultArgParam":
            {
                var idx = (obj["idx"] as JsonValue)?.GetValue<int>() ?? -1;
                return idx >= 0 && idx < args.Count && args[idx] is JsonNode a ? a.DeepClone() : obj.DeepClone();
            }
            case JsonObject obj:
            {
                var res = new JsonObject();
                foreach (var kv in obj) res[kv.Key] = kv.Value == null ? null : SubstituteTokens(kv.Value, receiver, args);
                return res;
            }
            case JsonArray arr:
            {
                var res = new JsonArray();
                foreach (var it in arr) res.Add(it == null ? null : SubstituteTokens(it, receiver, args));
                return res;
            }
            default: return node.DeepClone();
        }
    }

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();
}

