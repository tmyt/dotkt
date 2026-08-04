// bir2cir — SuspendLambdaLowering (bundle-6 P3 wave-2b, Part B): the LIVE consumer of the
// `newSuspendLambda` BIR node. Replaces each such node with `new <mangled>_lambdaN$sm(captures..., null)`
// and synthesizes the SuspendLambda state machine (via SuspendColdLowering's FunGen lambda mode — the SAME
// invokeSuspend/label/spill/field machinery the named-fun cold lowering uses).
//
// kotc emits `newSuspendLambda` for every `suspend` lambda literal (BirEmitter.kt, ~:1912); this pass is the
// producer's counterpart and is exercised by the gate (cases/il-lam1, il-lam2 — a capturing suspend lambda
// with a real suspend call). The consumer landed BEFORE the producer during the rollout, so an unrecognized
// node could never reach ilemit as an unknown-node break.
//
// The `newSuspendLambda` contract (v1; the spec kotc step 2 emits to):
//   { "k":"newSuspendLambda",
//     "arity": N,                                // the lambda's OWN param count (0/1 = fixed create() slots; >=2 = array create)
//     "captures":[{"name","type"}],              // captured vars -> SM ctor params + fields
//     "params":  [{"name","type"}],              // the lambda's own params (create() sets them on the fresh SM)
//     "suspendRet":"kotlin.X",                   // the lambda's result type ("void"/"kotlin.Unit" -> Unit)
//     "typeParams":[<tp-name>,...],              // enclosing generic type-param NAME decls (open SM instantiation)
//     "body":[ ...structured, suspendCall-tagged... ],
//     "funcType":{t:"fn",suspend:true,recv?:T,...} } // canonical Kotlin function type; recv is not in params
//
// Node -> value: the suspend-lambda VALUE is the cold, unstarted SM instance
//   new <mangled>_lambdaN$sm(<captureVals>..., /*completion*/ null)
// (create() rebinds the completion when a builder/intrinsic starts it). Runs in every NON-REFERENCE build — the
// app build and the rt-stdlib build alike (Program.cs gates it on `!RefBuild`, the same gate as the cold
// lowering; a reference build is metadata-only and its bodies are squashed) — right after SuspendColdLowering and
// BEFORE BirTypeLowering (its kotlin.* type tokens flow through the type lowering).

using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

static class SuspendLambdaLowering
{
    static readonly TypeNode ContAnyTn = new TypeNode.Fqn("kotlin.coroutines.Continuation", new TypeNode[] { new TypeNode.Fqn("kotlin.Any") });
    static JsonNode ContAny() => TypeJson.Write(ContAnyTn);
    const string SuspendLambdaFqn = "kotlin.coroutines.clr.internal.SuspendLambda";
    const string RestrictedSuspendLambdaFqn = "kotlin.coroutines.clr.internal.RestrictedSuspendLambda";

    // The ref.dll index — consulted to check whether a suspend lambda's RECEIVER is a @RestrictsSuspension scope
    // (e.g. SequenceScope), which selects the RestrictedSuspendLambda SM base. Static, single-threaded per run.
    static ReferenceMetadataIndex _refs;
    static bool _restrictedBaseIsLocal;

    // The callee-return-type map (cold-entry name -> Kotlin resultType) produced by SuspendColdLowering.
    // Consulted when building a lambda SM so an awaited suspend-call value gets its real type (+ unbox) —
    // NOT kotlin.Any. Single-threaded per bir2cir run, so a static binding is sufficient.
    static IReadOnlyDictionary<string, TypeNode> _calleeRet;

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();
    public static void ApplyAll(IReadOnlyList<JsonNode> roots, IReadOnlySet<string> localTypeFqns,
        IReadOnlyDictionary<string, TypeNode> calleeRet = null, ReferenceMetadataIndex refs = null)
    {
        _calleeRet = calleeRet;
        _refs = refs;
        // In the app build the SuspendLambda base is a REFERENCED type (clr: base + clrOverride linkage); in a
        // self-build that declares it, a LOCAL type (bare base + local slot override). Computed per-base because a
        // @RestrictsSuspension lambda uses RestrictedSuspendLambda, which may have a different locality.
        var baseIsLocal = localTypeFqns.Contains(SuspendLambdaFqn);
        _restrictedBaseIsLocal = localTypeFqns.Contains(RestrictedSuspendLambdaFqn);

        foreach (var r in roots)
        {
            if (r is not JsonObject file) continue;
            var fileClass = Str(file["fileClass"]) ?? "Kt";
            var newTypes = new List<JsonNode>();
            var counter = new int[1];

            if (file["methods"] is JsonArray methods)
                foreach (var m in methods)
                    if (m is JsonObject mo) WalkMethod(mo, fileClass, newTypes, counter, baseIsLocal);

            // Top-level `val`/`var` backing fields carry their initializer inline as `field.init` (BirEmitter.kt
            // :577) — a suspend-lambda VALUE stored in a top-level property lands HERE, not in a method body. A
            // static field initializer runs with no enclosing instance (outerSelf:false / no `this`).
            if (file["fields"] is JsonArray ffields)
                foreach (var f in ffields)
                    if (f is JsonObject fo) WalkFieldInit(fo, fileClass, newTypes, counter, baseIsLocal);

            if (file["types"] is JsonArray types)
                // Snapshot: SuspendColdLowering may have appended SM types; walk the pre-existing ones (a
                // ToList guards against mutating while enumerating when we add the lambda SMs below).
                foreach (var t in types.ToList())
                    if (t is JsonObject to && Str(to["name"]) is string owner)
                    {
                        if (to["methods"] is JsonArray tms)
                            foreach (var m in tms)
                                if (m is JsonObject mo) WalkMethod(mo, owner, newTypes, counter, baseIsLocal);
                        if (to["ctors"] is JsonArray tcs)
                            foreach (var c in tcs)
                                if (c is JsonObject co && co["body"] is JsonNode cb)
                                    Walk(cb, owner + "_ctor", newTypes, counter, baseIsLocal, HasSelfParam(co));
                        if (to["properties"] is JsonArray tps)
                            foreach (var p in tps)
                                if (p is JsonObject po) WalkAccessors(po, owner, newTypes, counter, baseIsLocal);
                        // A non-const `val`/`var` on an object/companion becomes a STATIC field whose initializer
                        // rides inline as `field.init` (BirEmitter.kt:1171) — the `object Holder { val h = { ... } }`
                        // suspend-lambda VALUE lands here (a static init has no enclosing instance -> outerSelf:false).
                        // INSTANCE field initializers are emitted into the ctor body instead (walked via the ctors
                        // path above), so `this`-capturing suspend lambdas in instance fields are already covered.
                        if (to["fields"] is JsonArray tfields)
                            foreach (var f in tfields)
                                if (f is JsonObject fo) WalkFieldInit(fo, owner, newTypes, counter, baseIsLocal);
                    }

            if (newTypes.Count > 0)
            {
                var ts = file["types"] as JsonArray;
                if (ts == null) { ts = new JsonArray(); file["types"] = ts; }
                foreach (var nt in newTypes) ts.Add(nt);
            }
        }
    }

    // Does the enclosing method carry a `__self` param (a static extension fun — its receiver rode a leading
    // `__self`)? Then a captured enclosing receiver (`__outer`) reads `local __self` at the construction site;
    // an instance method (no `__self` param) reads `this`.
    static bool HasSelfParam(JsonObject method) =>
        method["params"] is JsonArray ps && ps.OfType<JsonObject>().Any(p => Str(p["name"]) == "__self");

    static void WalkMethod(JsonObject method, string prefix, List<JsonNode> newTypes, int[] counter, bool baseIsLocal)
    {
        var mn = Str(method["name"]) ?? "m";
        var outerSelf = HasSelfParam(method);
        if (method["body"] is JsonNode body) Walk(body, prefix + "_" + mn, newTypes, counter, baseIsLocal, outerSelf);
    }

    // Lower a `newSuspendLambda` stored as a STATIC field's inline initializer (a top-level/object/companion
    // property backing field). A static initializer has no enclosing instance, so a captured `__outer` cannot
    // arise here (nothing to capture) -> outerSelf:false. `field.init` is replaced in place with the `new <SM>`.
    static void WalkFieldInit(JsonObject field, string prefix, List<JsonNode> newTypes, int[] counter, bool baseIsLocal)
    {
        var fn = Str(field["name"]) ?? "f";
        if (field["init"] is JsonObject init && Str(init["k"]) == "newSuspendLambda")
            field["init"] = BuildLambda(init, prefix + "_" + fn, newTypes, counter, baseIsLocal, outerSelf: false);
        else if (field["init"] is JsonNode body)
            Walk(body, prefix + "_" + fn, newTypes, counter, baseIsLocal, outerSelf: false);
    }

    static void WalkAccessors(JsonObject prop, string prefix, List<JsonNode> newTypes, int[] counter, bool baseIsLocal)
    {
        var pn = Str(prop["name"]) ?? "p";
        foreach (var acc in new[] { "getter", "setter" })
            if (prop[acc] is JsonObject a && a["body"] is JsonNode b)
                Walk(b, prefix + "_" + pn + "_" + acc, newTypes, counter, baseIsLocal, HasSelfParam(a));
    }

    static void Walk(JsonNode node, string ctx, List<JsonNode> newTypes, int[] counter, bool baseIsLocal, bool outerSelf)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    var child = o[key];
                    if (child is JsonObject co && Str(co["k"]) == "newSuspendLambda")
                        o[key] = BuildLambda(co, ctx, newTypes, counter, baseIsLocal, outerSelf);
                    else if (child != null)
                        Walk(child, ctx, newTypes, counter, baseIsLocal, outerSelf);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    var child = a[i];
                    if (child is JsonObject co && Str(co["k"]) == "newSuspendLambda")
                        a[i] = BuildLambda(co, ctx, newTypes, counter, baseIsLocal, outerSelf);
                    else if (child != null)
                        Walk(child, ctx, newTypes, counter, baseIsLocal, outerSelf);
                }
                break;
        }
    }

    static JsonNode BuildLambda(JsonObject node, string ctx, List<JsonNode> newTypes, int[] counter, bool baseIsLocal, bool outerSelf)
    {
        // Bottom-up: lower any nested suspend lambdas inside THIS lambda's body first (their SMs + `new`
        // replacements land before this lambda's SM is built over the already-lowered body).
        var body = node["body"] as JsonArray ?? new JsonArray();
        Walk(body, ctx, newTypes, counter, baseIsLocal, outerSelf);

        var arity = IntOf(node["arity"]);
        var captures = ReadNameTypes(node["captures"]);
        var lambdaParams = (node["params"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        var resultType = TypeJson.Read(node["suspendRet"]);
        var funcType = TypeJson.Read(node["funcType"]) as TypeNode.Fn;
        var typeArgs = ReadStrings(node["typeParams"]);
        var ctorTypeArgs = node["typeArgs"] as JsonArray;
        var smName = ctx + "_lambda" + (++counter[0]) + "$sm";

        // Restricted suspension is a property of the Kotlin EXTENSION RECEIVER, not of an arbitrary parameter.
        // `funcType.recv` is the canonical semantic channel; the physical leading entry in node.params only supplies
        // the state-machine field name/create argument.
        var restricted = _refs != null && funcType?.Recv != null
            && _refs.HasRestrictsSuspension(TypeJson.OwnerName(TypeNode.Write(funcType.Recv)));
        var effBaseIsLocal = restricted ? _restrictedBaseIsLocal : baseIsLocal;

        var sm = SuspendColdLowering.BuildLambdaSm(
            smName, arity, captures, lambdaParams, body, resultType, typeArgs, effBaseIsLocal,
            _calleeRet, restricted, _refs);
        if (sm == null) return node;   // arity < 0 (never) -> keep the node; arbitrary N is now expressible

        newTypes.Add(sm);

        // CONSTRUCTION type args (#75 Batch B, 2A): a materialized suspend carrier renumbers its enclosing tvs to a
        // dense 0-based SM param space and carries the ORIGINAL enclosing tvs (any scope/index) on `typeArgs` — the
        // construction channel, distinct from `typeParams` (the SM's own name declarations). Instantiate the open SM
        // with THOSE originals. When absent (kotc's own source-lambda emission), fall back to the positional
        // `smName<tv{type,0..N-1}>` — keeping source-lambda output BYTE-IDENTICAL.
        TypeNode smInst;
        if (ctorTypeArgs != null && ctorTypeArgs.Count != typeArgs.Count)
            throw new NotSupportedException(
                $"bir2cir: suspend-lambda lowering: `{smName}` carries {ctorTypeArgs.Count} construction type "
                + $"argument(s) for {typeArgs.Count} state-machine type parameter(s) — an earlier lowering "
                + "dropped or added a construction type argument.");
        if (typeArgs.Count == 0)
            smInst = new TypeNode.Fqn(smName);
        else if (ctorTypeArgs == null)
            smInst = new TypeNode.Fqn(smName, Enumerable.Range(0, typeArgs.Count).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray());
        else
        {
            smInst = new TypeNode.Fqn(smName, ctorTypeArgs.Select((ta, i) =>
                RequiredType(ta, $"construction type argument {i} of `{smName}`")).ToArray());
        }

        // The lambda VALUE: `new SM(captureVals..., null)` — captures read at the emit site, a null completion (a
        // cold, unstarted lambda; create() rebinds the completion when a builder starts it).
        // GAP 2 — when the construction site is INSIDE a cold state machine (a suspend fun that builds this lambda),
        // SuspendColdLowering already resolved each capture's value into the SM's vocabulary and attached them as
        // `capValues` (a spilled local -> an SM field, `__outer` -> the member SM's `$this`). Use those verbatim; a
        // naive `this`/`local` here would denote the SM, not the captured enclosing instance/local.
        var capValues = node["capValues"] as JsonArray;
        var args = new JsonArray();
        var argTypes = new JsonArray();
        for (var ci = 0; ci < captures.Count; ci++)
        {
            var (n, t) = captures[ci];
            if (capValues != null && ci < capValues.Count && capValues[ci] != null)
                args.Add(capValues[ci].DeepClone());
            else
                // `__outer` is kotc's name for a captured enclosing `<this>`/extension-receiver (BirEmitter.kt:2929).
                // Its VALUE at an ORDINARY (non-SM) construction site is the enclosing method's receiver: an instance
                // method reads `this`; a STATIC extension fun (receiver rode a leading `__self` param) reads
                // `local __self`. `outerSelf` carries which. Every other capture is a real local.
                args.Add(n == "__outer"
                    ? (outerSelf ? new JsonObject { ["k"] = "local", ["name"] = "__self" }
                                 : new JsonObject { ["k"] = "this" })
                    : new JsonObject { ["k"] = "local", ["name"] = n });
            // BuildLambdaSm moves the enclosing method's generic parameters onto the synthesized SM type. Its ctor
            // therefore declares capture slots with those variables in TYPE scope. InlineSplice has already flattened
            // both enclosing method and owner variables into the SM's single dense index space; preserve that index
            // while changing only the lexical scope, then view the declaration through this construction's type args.
            // Adding an owner-count offset here would rebase that already-flattened index a second time (`M` -> `E`).
            var ctorParam = RebindMethodTvsToSm(t);
            if (smInst is TypeNode.Fqn { Args: { } smArgs })
                ctorParam = SupertypeGraph.SubstOwnerTvs(ctorParam, smArgs);
            argTypes.Add(TypeJson.Write(ctorParam));
        }
        args.Add(new JsonObject { ["k"] = "const", ["type"] = ContAny(), ["value"] = null });
        argTypes.Add(ContAny());

        return new JsonObject { ["k"] = "new", ["type"] = TypeJson.Write(smInst), ["argTypes"] = argTypes, ["args"] = args };
    }

    static TypeNode RebindMethodTvsToSm(TypeNode type) => type switch
    {
        TypeNode.Tv { Scope: "method" } tv => new TypeNode.Tv("type", tv.I),
        TypeNode.Nullable n => new TypeNode.Nullable(RebindMethodTvsToSm(n.Of)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(RebindMethodTvsToSm(o.Of)),
        TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(f.Name,
            args.Select(RebindMethodTvsToSm).ToArray()),
        TypeNode.Array a => new TypeNode.Array(RebindMethodTvsToSm(a.Elem)),
        TypeNode.ByRef b => new TypeNode.ByRef(RebindMethodTvsToSm(b.Of)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend,
            RebindMethodTvsToSm(fn.Ret),
            fn.Params.Select(RebindMethodTvsToSm).ToArray(),
            fn.Recv == null ? null : RebindMethodTvsToSm(fn.Recv),
            fn.Clr,
            fn.Ctx?.Select(RebindMethodTvsToSm).ToArray()),
        _ => type,
    };

    static int IntOf(JsonNode n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    static List<(string name, TypeNode type)> ReadNameTypes(JsonNode arr)
    {
        var list = new List<(string, TypeNode)>();
        if (arr is JsonArray a)
            foreach (var it in a)
                if (it is JsonObject o && Str(o["name"]) is string n)
                    list.Add((n, RequiredType(o["type"], $"capture `{n}` of a suspend lambda")));
        return list;
    }

    // Both channels are mandatory BIR facts: captures are parameter declarations and `typeArgs` entries are type
    // nodes by schema. Inventing Any here would turn an earlier producer/schema violation into a differently-shaped
    // CLR state machine, so keep the failure at the ownership boundary and name the missing fact.
    static TypeNode RequiredType(JsonNode node, string slot) =>
        TypeJson.Read(node) ?? throw new NotSupportedException(
            $"bir2cir: suspend-lambda lowering: the {slot} carries no type — an earlier lowering dropped it.");

    static List<string> ReadStrings(JsonNode arr)
    {
        var list = new List<string>();
        if (arr is JsonArray a)
            foreach (var it in a)
                if (it is JsonValue v && v.TryGetValue<string>(out var s)) list.Add(s);
                else if (it is JsonObject o && Str(o["name"]) is string n) list.Add(n);
        return list;
    }
}
