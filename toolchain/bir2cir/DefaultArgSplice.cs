using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Text.Json.Nodes;
using DotKt.Bir;

// CROSS-MODULE DEFAULT-ARGUMENT SPLICE (#134/#146).
//
// A call that OMITS a defaulted argument reaches bir2cir with a POSITIONAL `{"k":"defaultArg"}` placeholder (kotc emits
// one for every omitted default whose VALUE the frontend dropped to an IrErrorExpression — the cross-module case — so a
// later provided arg keeps its slot). For a callee whose defaulted params carry `[kotlin.clr.KotlinDefault(index, bir)]`
// on the referenced .dll, this pass reads the default-expression BIR and SPLICES it into each placeholder / trailing
// omitted slot.
//
// PHASE 1 (#146): runs immediately AFTER InlineSplice — BEFORE ObjectSlotRename/ClosureSynthesis/MemberCallSubstitution/
// BirTypeLowering — so the spliced RAW payload re-lowers IN THIS app's context (owner attribution for a payload's own
// `callStatic owner:null`, @ClrIntrinsic binding, generic resolution), exactly like InlineSplice's body splice. Because
// the owner is NOT yet attributed here, the callee is resolved OWNERLESS by `method name | emitted-arity`
// (ReferenceMetadataIndex.KotlinDefaultsFor) — a name+arity carried by several owners with conflicting defaults is
// AMBIGUOUS and refused loudly rather than guessed.
//
// CLOSED CARRIER (#146): a NON-CONSTANT default that lifts a helper — a non-capturing lambda `= {}` (the Avalonia
// `configure: Panel.() -> Unit = {}` idiom) whose `newDelegate` points at a library-local `__lambdaN` — is carried as a
// `{"k":"defaultCarrier","expr":<newDelegate>,"lifted":[<method decls>]}` envelope (kotc BirEmitterDeclarations
// .defaultCarrierBir). At the splice we RE-HOIST each carried method into THIS file's file-class methods under a fresh
// per-splice name and rewrite the `newDelegate.method` reference, so ilemit's assembly-local `FindStatic` resolves it —
// no cross-assembly `ldftn` and no ilemit change. A constant / simple-call default (`= emptyList()`) carries its BIR
// verbatim (no envelope). A `{"k":"defaultUnsupported"}` poison carrier (a capturing/SAM/suspend lambda default kotc
// could not close) is refused loudly here.
//
// A `= this` (an extension receiver) default rides a `{k:this}` token -> the call's arg[0]; a default reading an EARLIER
// param rides `{k:defaultArgParam, idx}` -> the call's arg[idx] (Kotlin defaults reference only earlier params; lower
// indices fill first). Unconditional across builds (a ref/rt stdlib self-build simply carries no cross-module omission).
static class DefaultArgSplice
{
    static int _counter;   // global unique id for fresh re-hoisted lifted-method names (per splice instance)

    // `root` is the CIR file object (has `methods` = the file-class methods a carrier's lifted decls re-hoist into).
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs)
    {
        var hoist = new JsonArray();
        Walk(root, refs, hoist);
        if (hoist.Count > 0)
        {
            // A carrier re-hoisted a lifted default lambda but this file has no file-class `methods` array to place it in
            // (kotc ALWAYS emits `methods` on a file root, so this is an internal invariant break, never a silent drop).
            if (root is not JsonObject fo || fo["methods"] is not JsonArray methods)
                throw new InvalidOperationException("bir2cir: DefaultArgSplice re-hoisted a carried default lambda but the file root has no `methods` array");
            foreach (var h in hoist.ToList()) { hoist.Remove(h); methods.Add(h); }
        }
        // CHOKEPOINT: kotc emits a `defaultArg` only for a cross-module omission it expects a @KotlinDefault to fill, and
        // ilemit cannot emit a raw placeholder — a survivor is a fill failure. Fail here with the callee, not opaquely there.
        AssertNoPlaceholder(root);
    }

    static void Walk(JsonNode node, ReferenceMetadataIndex refs, JsonArray hoist)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj.ToList()) if (kv.Value != null) Walk(kv.Value, refs, hoist);
            TrySplice(obj, refs, hoist);
        }
        else if (node is JsonArray arr) foreach (var it in arr.ToList()) if (it != null) Walk(it, refs, hoist);
    }

    static void TrySplice(JsonObject node, ReferenceMetadataIndex refs, JsonArray hoist)
    {
        var k = Str(node["k"]);
        if (k != "callStatic" && k != "callInstance") return;
        if (node["args"] is not JsonArray args || node["sig"] is not JsonArray sig) return;
        var sigCount = sig.Count;
        var hasPlaceholder = false;
        for (var j = 0; j < args.Count; j++) if (IsPlaceholder(args[j])) { hasPlaceholder = true; break; }
        // ONLY a POSITIONAL `defaultArg` placeholder marks a cross-module omission to fill. kotc emits a placeholder for
        // EVERY omitted arg of a @KotlinDefault-carrying callee (never a bare trailing DROP), so `args.Count == sig.Count`
        // here — a call merely SHORT of `sig` (a same-module trailing omit ilemit backfills, or any non-defaulted call
        // carrying `sig`) must NOT be touched: appending a default off an ownerless name match would corrupt it. This is
        // essential now the splice runs at phase 1 on EVERY build (a stdlib self-build call short of `sig` is not an omission).
        if (!hasPlaceholder) return;
        var method = Str(node["method"]);
        if (method == null) return;
        var defaults = refs.KotlinDefaultsFor(method, sigCount);
        if (defaults == null)
        {
            if (hasPlaceholder && refs.KotlinDefaultsAmbiguous(method, sigCount))
                throw new InvalidOperationException(
                    $"bir2cir: cannot fill an omitted default argument of '{method}' (arity {sigCount}) — the name+arity is " +
                    "carried by several referenced functions whose defaults disagree; pass the argument explicitly");
            return;
        }
        // An extension receiver rides args[0] (the emitted extension fun's `__self`). A `= this` default binds to it.
        var receiver = args.Count > 0 ? args[0] : null;
        // 1) Replace POSITIONAL placeholders in place (a later provided arg keeps its slot). Fill by array index = the
        //    @KotlinDefault index (extension receiver counted first, matching kotc's stamp).
        for (var j = 0; j < args.Count; j++)
        {
            if (!IsPlaceholder(args[j])) continue;
            if (!defaults.TryGetValue(j, out var bir)) continue;         // no @KotlinDefault at this slot -> caught by the chokepoint
            if (SpliceOne(bir, receiver, args, hoist, refs, method, j) is JsonNode fill) { args[j] = fill; Walk(fill, refs, hoist); }
        }
        // 2) Append any purely-TRAILING omitted args (callee carries @KotlinDefault but kotc dropped the tail).
        for (var pos = args.Count; pos < sigCount; pos++)
        {
            if (!defaults.TryGetValue(pos, out var bir)) return;         // gap -> bail (leave the call unchanged)
            if (SpliceOne(bir, receiver, args, hoist, refs, method, pos) is JsonNode fill) { args.Add(fill); Walk(fill, refs, hoist); } else return;
        }
    }

    static bool IsPlaceholder(JsonNode n) => n is JsonObject o && Str(o["k"]) == "defaultArg";

    // Parse a @KotlinDefault BIR-json string, unwrap a `defaultCarrier` (re-hoisting its lifted methods app-local), and
    // bind the callee's default-expression tokens (`{this}` / `{defaultArgParam idx}`) to THIS call's args. A deep-fresh
    // subtree per occurrence.
    static JsonNode SpliceOne(string bir, JsonNode receiver, JsonArray args, JsonArray hoist, ReferenceMetadataIndex refs, string method, int slot)
    {
        var parsed = MaterializeDefault(bir, hoist, refs, method, slot);
        return parsed == null ? null : SubstituteTokens(parsed, receiver, args);
    }

    // SHARED with InlineSplice (#34 — omitted defaulted param of an inline callee): parse a @KotlinDefault BIR string,
    // refuse a `defaultUnsupported` poison loudly, unwrap a `defaultCarrier` (re-hoisting its lifted methods into `hoist`),
    // and return the raw default EXPR (still carrying `{this}` / `{defaultArgParam idx}` tokens — the caller binds them to
    // its own arg/param frame via SubstituteTokens). Returns null on an unparseable string.
    internal static JsonNode MaterializeDefault(string bir, JsonArray hoist, ReferenceMetadataIndex refs, string method, int slot)
    {
        JsonNode parsed; try { parsed = JsonNode.Parse(bir, documentOptions: BirJson.DocOptions); } catch { return null; }
        if (parsed is JsonObject env)
        {
            var envK = Str(env["k"]);
            if (envK == "defaultUnsupported")
                throw new InvalidOperationException(
                    $"bir2cir: cannot fill the omitted default argument at slot {slot} of '{method}': " +
                    (Str(env["reason"]) ?? "the default is not representable at a cross-module call site"));
            if (envK == "defaultCarrier")
                parsed = UnwrapCarrier(env, hoist, refs);
        }
        return parsed;
    }

    // A `defaultCarrier` envelope: RE-HOIST each carried lifted method into this file (fresh per-splice name), rewrite the
    // `newDelegate.method` references (in the expr AND across the lifted bodies) to the fresh names, and return the expr.
    static JsonNode UnwrapCarrier(JsonObject env, JsonArray hoist, ReferenceMetadataIndex refs)
    {
        var expr = env["expr"];
        var lifted = env["lifted"] as JsonArray;
        if (expr == null || lifted == null) return expr;
        var rename = new Dictionary<string, string>(StringComparer.Ordinal);
        var clones = new List<JsonObject>();
        foreach (var m in lifted)
        {
            if (m is not JsonObject mo) continue;
            var old = Str(mo["name"]); if (old == null) continue;
            rename[old] = "__dflt$lambda$" + Interlocked.Increment(ref _counter);
            clones.Add((JsonObject)mo.DeepClone());
        }
        foreach (var c in clones)
        {
            if (Str(c["name"]) is string on && rename.TryGetValue(on, out var nn)) c["name"] = nn;
            RewriteDelegateNames(c, rename);
            // The carried lifted body may ITSELF contain a `defaultArg` placeholder (a default `= { crossModuleFn() }`
            // whose call omits a non-const default) — fill it before the method is hoisted (the clone is unparented here).
            Walk(c, refs, hoist);
            hoist.Add(c);
        }
        var exprClone = expr.DeepClone();
        RewriteDelegateNames(exprClone, rename);
        return exprClone;
    }

    // Rewrite every `{"k":"newDelegate","method":<old>}` whose method is in `map` to the renamed method.
    static void RewriteDelegateNames(JsonNode node, Dictionary<string, string> map)
    {
        if (node is JsonObject obj)
        {
            if (Str(obj["k"]) == "newDelegate" && Str(obj["method"]) is string m && map.TryGetValue(m, out var nn))
                obj["method"] = nn;
            foreach (var kv in obj.ToList()) if (kv.Value != null) RewriteDelegateNames(kv.Value, map);
        }
        else if (node is JsonArray arr) foreach (var it in arr.ToList()) if (it != null) RewriteDelegateNames(it, map);
    }

    // Rebuild `node`, replacing every `{"k":"this"}` with a deep clone of `receiver` and every
    // `{"k":"defaultArgParam","idx":N}` with a deep clone of `args[N]`. Rebuilds fresh so no node is double-parented.
    // Shared with InlineSplice (#34): there `args[N]` = the bound temp for the emitted-position-N param, `receiver` = the
    // bound extension/dispatch temp.
    internal static JsonNode SubstituteTokens(JsonNode node, JsonNode receiver, JsonArray args)
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

    static void AssertNoPlaceholder(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (Str(obj["k"]) == "defaultArg")
                throw new InvalidOperationException(
                    "bir2cir: an omitted cross-module default argument was not filled — no [kotlin.clr.KotlinDefault] " +
                    "carrier was found for its callee on the referenced assembly; the reference may be stale or the " +
                    "default not carryable. Pass the argument explicitly.");
            foreach (var kv in obj) if (kv.Value != null) AssertNoPlaceholder(kv.Value);
        }
        else if (node is JsonArray arr) foreach (var it in arr) if (it != null) AssertNoPlaceholder(it);
    }

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();
}
