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
// `callStatic owner:null`, @ClrIntrinsic binding, generic resolution), exactly like InlineSplice's body splice.
// facadegen-injected cross-module calls already carry the callee's exact file-facade `ownerType`; use it. Only a truly
// ownerless Kotlin call falls back to `method name | emitted-arity`, where conflicting owners are refused loudly.
//
// CLOSED CARRIER (#146): a NON-CONSTANT default that lifts a helper — a non-capturing lambda `= {}` (the Avalonia
// `configure: Panel.() -> Unit = {}` idiom) whose `newDelegate` points at a library-local `__lambdaN` — is carried as a
// `{"k":"defaultCarrier","expr":<newDelegate>,"lifted":[<method decls>]}` envelope (kotc BirEmitterDeclarations
// .defaultCarrierBir). At the splice we RE-HOIST each carried method into THIS file's file-class methods under a fresh
// per-splice name and rewrite both `newDelegate.method` and its mandatory `calleeOwner` to this consuming file class —
// no cross-assembly `ldftn`. A constant / simple-call default (`= emptyList()`) carries its BIR
// verbatim (no envelope). A `{"k":"defaultUnsupported"}` poison carrier (a capturing/SAM/suspend lambda default kotc
// could not close) is refused loudly here.
//
// A `= this` (an extension receiver) default rides a `{k:this}` token -> the call's arg[0]; a default reading an EARLIER
// param rides `{k:defaultArgParam, idx}` -> the call's arg[idx] (Kotlin defaults reference only earlier params; lower
// indices fill first). Unconditional across builds (a ref/rt stdlib self-build simply carries no cross-module omission).
//
// CONSTRUCTORS (#235) come through the same machinery with two differences: a `{"k":"new"}` names its callee by TYPE, so
// it is always OWNERFUL (`<type>|.ctor|<declared param count>`, the count read off `argTypes`) and never falls back to the
// name+arity index; and it has no receiver, so a carrier that reads one is refused rather than bound to `args[0]` (which
// is a plain constructor argument). Two same-arity ctor overloads carrying DIFFERENT defaults make the key ambiguous and
// are refused (ReferenceMetadataIndex.KotlinDefaultsConflicted).
static class DefaultArgSplice
{
    static int _counter;   // global unique id for fresh re-hoisted lifted-method names (per splice instance)

    // `root` is the CIR file object (has `methods` = the file-class methods a carrier's lifted decls re-hoist into).
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs)
    {
        var hoist = new JsonArray();
        var localOwner = root is JsonObject ro && Str(ro["fileClass"]) is string fc ? TypeJson.Fqn(fc) : null;
        Walk(root, refs, hoist, localOwner);
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

    static void Walk(JsonNode node, ReferenceMetadataIndex refs, JsonArray hoist, JsonNode localOwner)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj.ToList()) if (kv.Value != null) Walk(kv.Value, refs, hoist, localOwner);
            TrySplice(obj, refs, hoist, localOwner);
        }
        else if (node is JsonArray arr) foreach (var it in arr.ToList()) if (it != null) Walk(it, refs, hoist, localOwner);
    }

    static void TrySplice(JsonObject node, ReferenceMetadataIndex refs, JsonArray hoist, JsonNode localOwner)
    {
        var k = Str(node["k"]);
        var isNew = k == "new";
        if (!isNew && k != "callStatic" && k != "callInstance") return;
        if (node["args"] is not JsonArray args) return;
        var hasPlaceholder = false;
        for (var j = 0; j < args.Count; j++) if (IsPlaceholder(args[j])) { hasPlaceholder = true; break; }
        // ONLY a POSITIONAL `defaultArg` placeholder marks a cross-module omission to fill. kotc emits a placeholder for
        // EVERY omitted arg of a @KotlinDefault-carrying callee (never a bare trailing DROP), so `args.Count == sig.Count`
        // here — a call merely SHORT of `sig` (a same-module trailing omit ilemit backfills, or any non-defaulted call
        // carrying `sig`) must NOT be touched: appending a default off an ownerless name match would corrupt it. This is
        // essential now the splice runs at phase 1 on EVERY build (a stdlib self-build call short of `sig` is not an omission).
        if (!hasPlaceholder) return;
        string method, owner, sigKey = null;
        int sigCount;
        if (isNew)
        {
            // A `new` names its callee by TYPE — there is no `method`/`sig` to read. The ctor's splice identity is
            // `<type>|.ctor|<declared parameter count>` (#235), and `argTypes` IS that declared vector (kotc emits the
            // resolved ctor's own parameter types). The args array must line up with it one-for-one: kotc fills every
            // omitted slot with a placeholder, so each stamped @KotlinDefault index indexes that same array. A mismatch
            // means an arg was dropped rather than placeheld — refuse instead of filling the wrong slot.
            owner = TypeJson.OwnerName(node["type"]);
            if (owner == null) return;
            method = ReferenceMetadataIndex.CtorKeyName;
            if (node["argTypes"] is not JsonArray ctorParamTypes) return;
            sigCount = ctorParamTypes.Count;
            // `argTypes` IS the resolved ctor's declared parameter vector, in the same ParamKey space the reference scan
            // keys by — so same-arity ctor overloads resolve to the RIGHT one instead of being refused as ambiguous.
            sigKey = string.Join(",", ctorParamTypes.Select(t => ReferenceMetadataIndex.ParamKey(t)));
            if (args.Count != sigCount)
                throw new InvalidOperationException(
                    $"bir2cir: cannot fill an omitted default argument of '{owner}''s constructor — the call emits " +
                    $"{args.Count} argument(s) for {sigCount} parameter(s), so the omitted slot is not identifiable; " +
                    "pass the argument explicitly");
        }
        else
        {
            // A non-generic call carries `sig`; a generic facade call carries the same declared parameter vector as
            // `shapeTypes` so later CLR binding can close the method. Both are structural callee signatures and therefore
            // equally authoritative for the KotlinDefault owner+name+arity lookup.
            var sig = node["sig"] as JsonArray ?? node["shapeTypes"] as JsonArray;
            if (sig == null) return;
            sigCount = sig.Count;
            method = Str(node["method"]);
            if (method == null) return;
            owner = TypeJson.OwnerName(node["ownerType"] ?? node["calleeOwner"] ?? node["owner"]);
        }
        // The callee as a DIAGNOSTIC names itself: `.ctor` is a key component, not something to show a reader.
        var label = isNew ? owner + " constructor" : method;
        var defaults = refs.KotlinDefaultsFor(owner, method, sigCount, sigKey);
        if (defaults == null)
        {
            if (hasPlaceholder && refs.KotlinDefaultsAmbiguous(owner, method, sigCount))
                throw new InvalidOperationException(
                    $"bir2cir: cannot fill an omitted default argument of '{label}' (arity {sigCount}) — that name+arity is " +
                    "carried by several referenced declarations whose defaults disagree; pass the argument explicitly");
            return;
        }
        // An extension receiver rides args[0] (the emitted extension fun's `__self`). A `= this` default binds to it.
        // A `new` has NO receiver: args[0] is the ctor's first ARGUMENT, so a `{k:this}` in a ctor carrier binds to
        // nothing (kotc refuses to carry one — `defaultReadsDispatch` poisons it — and `SpliceOne` asserts it here).
        var receiver = isNew ? null : (args.Count > 0 ? args[0] : null);
        // 1) Replace POSITIONAL placeholders in place (a later provided arg keeps its slot). Fill by array index = the
        //    @KotlinDefault index (extension receiver counted first, matching kotc's stamp).
        for (var j = 0; j < args.Count; j++)
        {
            if (!IsPlaceholder(args[j])) continue;
            if (!defaults.TryGetValue(j, out var bir)) continue;         // no @KotlinDefault at this slot -> caught by the chokepoint
            if (SpliceOne(bir, receiver, args, hoist, refs, label, j, localOwner, ctorNoReceiver: isNew) is JsonNode fill) { args[j] = fill; Walk(fill, refs, hoist, localOwner); }
        }
        // 2) Append any purely-TRAILING omitted args (callee carries @KotlinDefault but kotc dropped the tail). Unreachable
        //    for a `new`: its `args.Count == sigCount` is asserted above.
        for (var pos = args.Count; pos < sigCount; pos++)
        {
            if (!defaults.TryGetValue(pos, out var bir)) return;         // gap -> bail (leave the call unchanged)
            if (SpliceOne(bir, receiver, args, hoist, refs, label, pos, localOwner) is JsonNode fill) { args.Add(fill); Walk(fill, refs, hoist, localOwner); } else return;
        }
    }

    // A `{"k":"this"}` in the CARRIER — the callee's own default expression, before this call's args are substituted in.
    // (The substituted result may legitimately contain `this`: an argument the CONSUMER wrote reading its own instance.)
    static bool CarrierReadsReceiver(JsonNode node) => node switch
    {
        JsonObject obj => Str(obj["k"]) == "this" || obj.Any(kv => kv.Value != null && CarrierReadsReceiver(kv.Value)),
        JsonArray arr => arr.Any(it => it != null && CarrierReadsReceiver(it)),
        _ => false,
    };

    static bool IsPlaceholder(JsonNode n) => n is JsonObject o && Str(o["k"]) == "defaultArg";

    // Parse a @KotlinDefault BIR-json string, unwrap a `defaultCarrier` (re-hoisting its lifted methods app-local), and
    // bind the callee's default-expression tokens (`{this}` / `{defaultArgParam idx}`) to THIS call's args. A deep-fresh
    // subtree per occurrence.
    static JsonNode SpliceOne(string bir, JsonNode receiver, JsonArray args, JsonArray hoist, ReferenceMetadataIndex refs, string method, int slot, JsonNode localOwner, bool ctorNoReceiver = false)
    {
        var parsed = MaterializeDefault(bir, hoist, refs, method, slot, localOwner);
        if (parsed == null) return null;
        // A ctor call site has no receiver to bind a `{k:this}` to. Checked on the CARRIER, never on the substituted
        // result — that legitimately carries `this` whenever the CONSUMER passed an argument reading its own instance
        // (`Rect(this.w)`), which is the shape the same-module path already binds by symbol rather than by token.
        if (ctorNoReceiver && CarrierReadsReceiver(parsed))
            throw new InvalidOperationException(
                $"bir2cir: cannot fill the omitted default argument at slot {slot} of {method}: its default reads an " +
                "enclosing instance, which a constructor call site cannot bind; pass the argument explicitly");
        return SubstituteTokens(parsed, receiver, args);
    }

    // SHARED with InlineSplice (#34 — omitted defaulted param of an inline callee): parse a @KotlinDefault BIR string,
    // refuse a `defaultUnsupported` poison loudly, unwrap a `defaultCarrier` (re-hoisting its lifted methods into `hoist`),
    // and return the raw default EXPR (still carrying `{this}` / `{defaultArgParam idx}` tokens — the caller binds them to
    // its own arg/param frame via SubstituteTokens). Returns null on an unparseable string.
    internal static JsonNode MaterializeDefault(string bir, JsonArray hoist, ReferenceMetadataIndex refs, string method, int slot, JsonNode localOwner)
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
                parsed = UnwrapCarrier(env, hoist, refs, localOwner);
        }
        return parsed;
    }

    // A `defaultCarrier` envelope: RE-HOIST each carried lifted method into this file (fresh per-splice name), rewrite the
    // `newDelegate.method` references (in the expr AND across the lifted bodies) to the fresh names, and return the expr.
    static JsonNode UnwrapCarrier(JsonObject env, JsonArray hoist, ReferenceMetadataIndex refs, JsonNode localOwner)
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
            RewriteDelegateNames(c, rename, localOwner);
            // The carried lifted body may ITSELF contain a `defaultArg` placeholder (a default `= { crossModuleFn() }`
            // whose call omits a non-const default) — fill it before the method is hoisted (the clone is unparented here).
            Walk(c, refs, hoist, localOwner);
            hoist.Add(c);
        }
        var exprClone = expr.DeepClone();
        RewriteDelegateNames(exprClone, rename, localOwner);
        return exprClone;
    }

    // Rewrite every `{"k":"newDelegate","method":<old>}` whose method is in `map` to the renamed method.
    static void RewriteDelegateNames(JsonNode node, Dictionary<string, string> map, JsonNode localOwner)
    {
        if (node is JsonObject obj)
        {
            if (Str(obj["k"]) == "newDelegate" && Str(obj["method"]) is string m && map.TryGetValue(m, out var nn))
            {
                obj["method"] = nn;
                // The target method was moved out of the referenced carrier into THIS consuming file class.
                // Retarget #204's mandatory dispatch identity together with the fresh method name.
                obj["calleeOwner"] = localOwner?.DeepClone();
            }
            foreach (var kv in obj.ToList()) if (kv.Value != null) RewriteDelegateNames(kv.Value, map, localOwner);
        }
        else if (node is JsonArray arr) foreach (var it in arr.ToList()) if (it != null) RewriteDelegateNames(it, map, localOwner);
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
