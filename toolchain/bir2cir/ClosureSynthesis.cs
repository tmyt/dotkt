using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

// #52 (kotc-purity): SYNTHESIZE the capturing-lambda closure CLASS here, in the Kotlin<->CLR layer, instead of in the
// kotc frontend. A capturing lambda `{ … }` lowers to a closure class (fields = captured vars, instance `invoke`
// method = the body). kotc used to BUILD that class JSON directly into its `liftedTypes`; it is a CLR-REPRESENTATION
// type (there is no such class in the Kotlin source), so its synthesis belongs below the frontend boundary.
//
// kotc now emits the raw build-INGREDIENTS as a transient `synthClass` fact on the `newClosure` node:
//   { "k":"newClosure", "closureType":<fqn cname>, "captures":[<value exprs>], "method":"invoke",
//     "funcType":<type>, "typeArgs":[…]?,
//     "synthClass": { "name":"<cname>", "fields":[{name,type}…], "params":[…invoke params],
//                     "ret":<type>, "body":[…invoke body], "typeParams":[…]? } }
// This pass reads `synthClass`, ASSEMBLES the actual closure class (the class/base/interfaces wrapper + the ctor
// field-init body), appends it to the file `types`, and STRIPS `synthClass` — leaving the lean `newClosure`
// (closureType + capture VALUE exprs + funcType + typeArgs) that ilemit already consumes for the `new`. Byte-identical
// to the old kotc-emitted output.
//
// Runs FIRST in the Phase-1 per-file loop — before EVERY other transform — so the synthesized class is present in
// `types` exactly as kotc's `liftedTypes` closure class used to be (downstream passes see it verbatim). Critically it
// runs before Phase 1.5 SuspendColdLowering, which builds its `closures` lookup from `types` to inline a
// `suspendCoroutineUninterceptedOrReturn { c -> … }` intrinsic's closure body; that class must exist by then.
// Nested closures are handled bottom-up (a closure body's inner `newClosure` is synthesized before the outer wrapper).
// Unconditional (ref + rt + app): kotc emits `synthClass` in every build (RefBodySquash later squashes the ref build's
// invoke/ctor bodies exactly as it did the old liftedTypes closure).
static class ClosureSynthesis
{
    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    public static void Apply(JsonNode root)
    {
        if (root is not JsonObject file) return;
        var newTypes = new List<JsonNode>();

        if (file["methods"] is JsonArray methods)
            foreach (var m in methods) Walk(m, newTypes);
        if (file["fields"] is JsonArray fields)
            foreach (var f in fields) Walk(f, newTypes);
        if (file["types"] is JsonArray types)
            // ToList: the walk appends closure classes to `newTypes` (added below), but a closure can also live inside
            // an already-declared type's member body — walk the pre-existing types without mutating while enumerating.
            foreach (var t in types.ToList()) Walk(t, newTypes);

        if (newTypes.Count > 0)
        {
            var ts = file["types"] as JsonArray;
            if (ts == null) { ts = new JsonArray(); file["types"] = ts; }
            // Dedup by name: a cross-module SPLICED `newSam`/`newClosure` synthClass carries a FIXED origin name (e.g.
            // `dotkt$…$Sam102`) that can recur when the same inline fn is spliced at multiple sites — append each unique
            // synthesized type once (a duplicate type name is a hard ilemit error).
            var have = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in ts) if (t is JsonObject to && Str(to["name"]) is string tn) have.Add(tn);
            foreach (var nt in newTypes)
                if (nt is JsonObject no && Str(no["name"]) is string nn && !have.Add(nn)) continue;
                else ts.Add(nt);
        }
    }

    static void Walk(JsonNode node, List<JsonNode> newTypes)
    {
        switch (node)
        {
            case JsonObject o:
                if (Str(o["k"]) == "newClosure" && o["synthClass"] is JsonObject sc)
                {
                    // Bottom-up: synthesize any nested closures inside THIS closure's invoke body first, so the outer
                    // class is assembled over an already-lean body (inner `newClosure`s stripped, inner classes queued).
                    if (sc["body"] is JsonNode body) Walk(body, newTypes);
                    newTypes.Add(BuildClosureClass(RebindSyntheticTypeVariables(sc, o["typeArgs"] as JsonArray)));
                    o.Remove("synthClass");
                    return;   // the invoke `body` (above) was recursed for NESTED closures; return WITHOUT descending into
                              // this node's other children (the just-removed synthClass; the capture-value exprs are leaf reads)
                }
                // A `newSam` carrying an embedded `synthClass` (the fun-interface class): a CROSS-MODULE SPLICED `newSam`
                // (e.g. `compareBy{}`'s Comparator) references a `dotkt$…$SamN` class that lives in the ORIGIN/stdlib file,
                // not the consuming file — so kotc travels the class WITH the node (like newClosure) and we synthesize it
                // HERE. The synthClass is a FULL class def (implements the interface + the SAM override); walk its method
                // bodies for nested closures first (bottom-up), then append it. Dedup is handled at append time (a fixed
                // origin name can recur across splices).
                if (Str(o["k"]) == "newSam" && o["synthClass"] is JsonObject scSam)
                {
                    if (scSam["methods"] is JsonArray sms)
                        foreach (var m in sms) if (m is JsonObject mo && mo["body"] is JsonNode mb) Walk(mb, newTypes);
                    newTypes.Add(RebindSyntheticTypeVariables(scSam, o["typeArgs"] as JsonArray));
                    o.Remove("synthClass");
                    return;
                }
                foreach (var kv in o.ToList())
                    if (kv.Value != null) Walk(kv.Value, newTypes);
                break;
            case JsonArray a:
                foreach (var it in a)
                    if (it != null) Walk(it, newTypes);
                break;
        }
    }

    // A synthClass is born inside a generic METHOD, so kotc faithfully describes its ingredients using that lexical
    // method's `{tv,scope:"method",i}` tokens. Once bir2cir lifts those ingredients into a CLR CLASS, the captured
    // generic variables belong to the class generic-parameter space instead. `new(Sam|Closure).typeArgs[j]` is the
    // exact outer-type -> synthesized-class-param correspondence (and already orders the synthClass.typeParams).
    // Rebind direct outer TVs before publishing the class definition; leaving method-scope tokens in a class field,
    // ctor, or non-generic invoke/emit method creates an unbound `!!i` signature.
    static JsonObject RebindSyntheticTypeVariables(JsonObject source, JsonArray typeArgs)
    {
        var clone = source.DeepClone() as JsonObject
                    ?? throw new InvalidOperationException("synthetic class must be an object");
        var tps = clone["typeParams"] as JsonArray;
        if (tps == null || tps.Count == 0) return clone;
        if (typeArgs == null || typeArgs.Count != tps.Count)
            throw new InvalidOperationException(
                $"generic synthetic class `{Str(clone["name"])}` has {tps.Count} type params but "
                + $"{typeArgs?.Count ?? 0} construction type args");
        // Transient bir2cir fact consumed by SharedSyntheticSynthesis: a bare Ref-cell identity inside this lifted
        // class must construct its generic arguments in the NEW class scope, using the same outer-TV correspondence.
        clone["_syntheticTypeArgs"] = typeArgs.DeepClone();

        var positions = new Dictionary<(string Scope, int Index), int>();
        for (var i = 0; i < typeArgs.Count; i++)
        {
            // Inline specialization may already have replaced an outer TV with a closed type. In that case the
            // synthClass payload is specialized to the same closed type too; its otherwise-redundant generic slot
            // remains harmless and there is no lexical TV to re-scope. Only direct TV arguments form a scope map.
            if (typeArgs[i] is not JsonObject tv || Str(tv["t"]) != "tv"
                || Str(tv["scope"]) is not string scope
                || tv["i"] is not JsonValue iv || !iv.TryGetValue<int>(out var index))
                continue;
            // Inline specialization can leave two redundant synthesized slots fed by the same outer TV. They are
            // constructed with the same argument; bind payload occurrences to the first canonical slot.
            positions.TryAdd((scope, index), i);
        }

        void Walk(JsonNode node)
        {
            switch (node)
            {
                case JsonObject o:
                    if (Str(o["t"]) == "tv" && Str(o["scope"]) is string scope
                        && o["i"] is JsonValue iv && iv.TryGetValue<int>(out var index)
                        && positions.TryGetValue((scope, index), out var position))
                    {
                        o["scope"] = "type";
                        o["i"] = position;
                        return;
                    }
                    foreach (var kv in o)
                        if (kv.Key != "_syntheticTypeArgs" && kv.Value != null) Walk(kv.Value);
                    break;
                case JsonArray a:
                    foreach (var item in a)
                        if (item != null) Walk(item);
                    break;
            }
        }

        Walk(clone);
        return clone;
    }

    // Assemble the closure class from the raw ingredients. Mirrors the JSON kotc's BirEmitter.lambda() used to add to
    // liftedTypes: fields = capture (name,type); a single ctor whose body sets each field from its like-named param; an
    // instance `invoke` (non-virtual, non-override) carrying the lambda body; optional generic `typeParams` (the
    // enclosing free type params the reified closure is generic over).
    static JsonObject BuildClosureClass(JsonObject sc)
    {
        var name = Str(sc["name"]);
        var fields = sc["fields"] as JsonArray ?? new JsonArray();
        var fqName = new JsonObject { ["t"] = "fqn", ["name"] = name };

        var ctorBody = new JsonArray();
        foreach (var f in fields)
            if (f is JsonObject fo && Str(fo["name"]) is string fn)
                ctorBody.Add(new JsonObject
                {
                    ["k"] = "setField",
                    ["ownerType"] = fqName.DeepClone(),
                    ["recv"] = new JsonObject { ["k"] = "this" },
                    ["name"] = fn,
                    ["value"] = new JsonObject { ["k"] = "local", ["name"] = fn },
                });

        var ctor = new JsonObject
        {
            ["params"] = fields.DeepClone(),
            ["baseArgs"] = null,
            ["body"] = ctorBody,
        };

        var invokeBody = (sc["body"] as JsonArray)?.DeepClone() ?? new JsonArray();
        // #122: an INLINE-MATERIALIZED closure's body is bir2cir-synthesized (InlineSplice), so its capture-field reads
        // never passed through kotc's expr() `sty` stamp. Stamp each `{k:field}` read of one of THIS closure's capture
        // fields with that field's declared type, so a downstream StaticType consumer (e.g. ArrayConstructionLowering
        // deriving `arrayGet.elem` off a captured array field) recovers the type WITHOUT re-resolving it from a decl.
        StampCaptureFieldSty(invokeBody, fields);
        var invoke = new JsonObject
        {
            ["name"] = "invoke",
            ["static"] = false,
            ["override"] = false,
            ["virtual"] = false,
            ["params"] = (sc["params"] as JsonArray)?.DeepClone() ?? new JsonArray(),
            ["ret"] = sc["ret"]?.DeepClone(),
            ["body"] = invokeBody,
        };

        var cls = new JsonObject
        {
            ["name"] = name,
            ["kind"] = "class",
            // #68: a capturing-lambda closure is compiler-generated — flag it so ilemit stamps [CompilerGenerated].
            ["generated"] = true,
        };
        // Emit `typeParams` only when non-empty — matches kotc (typeParamsJson omitted the key entirely for a
        // non-generic closure), so the shape is byte-identical for the common case.
        if (sc["typeParams"] is JsonArray tps && tps.Count > 0) cls["typeParams"] = tps.DeepClone();
        if (sc["_syntheticTypeArgs"] is JsonArray origins) cls["_syntheticTypeArgs"] = origins.DeepClone();
        cls["base"] = null;
        cls["interfaces"] = new JsonArray();
        cls["fields"] = fields.DeepClone();
        cls["ctors"] = new JsonArray { ctor };
        cls["methods"] = new JsonArray { invoke };
        return cls;
    }

    // Walk a synthesized closure `invoke` body and stamp `sty` (the frontend static-type contract, #122) on every
    // capture-field read `{k:field, recv:{k:this}, name:<f>}` whose `f` is one of the closure's capture fields, using
    // that field's declared type. Idempotent (a read that already carries `sty` — a kotc-emitted closure body — is left
    // untouched). No re-resolution: the type is read straight off the closure's own `fields` decl.
    static void StampCaptureFieldSty(JsonNode body, JsonArray fields)
    {
        var fieldTypes = new Dictionary<string, JsonNode>(System.StringComparer.Ordinal);
        foreach (var f in fields)
            if (f is JsonObject fo && Str(fo["name"]) is string fn && fo["type"] is JsonNode ft)
                fieldTypes[fn] = ft;
        if (fieldTypes.Count == 0) return;
        void Walk(JsonNode n)
        {
            if (n is JsonObject o)
            {
                if (Str(o["k"]) == "field" && o["sty"] == null
                    && o["recv"] is JsonObject rc && Str(rc["k"]) == "this"
                    && Str(o["name"]) is string nm && fieldTypes.TryGetValue(nm, out var t))
                    o["sty"] = t.DeepClone();
                // Do NOT descend into a NESTED closure's own invoke body (`synthClass`) — its `this` is a different
                // closure, processed by its own BuildClosureClass. A nested closure's CAPTURE value exprs are evaluated
                // in THIS scope and stay reachable (they are not under `synthClass`), so they still get stamped.
                foreach (var kv in o) if (kv.Value != null && kv.Key != "synthClass") Walk(kv.Value);
            }
            else if (n is JsonArray a) foreach (var c in a) if (c != null) Walk(c);
        }
        Walk(body);
    }
}
