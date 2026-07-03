// bir2cir — SuspendLambdaLowering (bundle-6 P3 wave-2b STEP 1, Part B): the DORMANT consumer of the
// `suspendLambdaNew` BIR node. Replaces each such node with `new <mangled>_lambdaN$sm(captures..., null)`
// and synthesizes the SuspendLambda state machine (via SuspendColdLowering's FunGen lambda mode — the SAME
// invokeSuspend/label/spill/field machinery the named-fun cold lowering uses).
//
// kotc does NOT emit `suspendLambdaNew` yet (Part B lands the consumer FIRST, before the producer, so an
// unrecognized node can never reach ilemit as an unknown-node break). Against every current input this pass
// is a verified NO-OP (no node matches). It is exercised by a hand-crafted fixture — see the report.
//
// The `suspendLambdaNew` contract (v1; the spec kotc step 2 emits to):
//   { "k":"suspendLambdaNew",
//     "arity": 0|1,                              // the lambda's OWN param count (v1: 0 or 1; >=2 refused)
//     "captures":[{"name","type"}],              // captured vars -> SM ctor params + fields
//     "params":  [{"name","type"}],              // the lambda's own params (arity-1: create(value) sets it)
//     "resultType":"kotlin.X",                   // the lambda's result type ("void"/"kotlin.Unit" -> Unit)
//     "typeArgs":[<tp-name>,...],                // enclosing generic type params (open SM instantiation)
//     "body":[ ...structured, suspendCall-tagged... ],
//     "funcType":"sfunc:<ret>:<args>" }          // informational (the delegate view; not consumed here)
//
// Node -> value: the suspend-lambda VALUE is the cold, unstarted SM instance
//   new <mangled>_lambdaN$sm(<captureVals>..., /*completion*/ null)
// (create() rebinds the completion when a builder/intrinsic starts it). Runs in APP builds only, right after
// SuspendColdLowering and BEFORE BirTypeLowering (its kotlin.* type tokens flow through the type lowering).

using System.Text.Json.Nodes;

static class SuspendLambdaLowering
{
    const string ContinuationOfAny = "kotlin.coroutines.Continuation[kotlin.Any]";
    const string SuspendLambdaFqn = "kotlin.coroutines.clr.internal.SuspendLambda";

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    public static void ApplyAll(IReadOnlyList<JsonNode> roots, IReadOnlySet<string> localTypeFqns)
    {
        // In the app build SuspendLambda is a REFERENCED type (clr: base + clrOverride linkage); in a self-build
        // that declares it, a LOCAL type (bare base + local slot override). This pass is app-only in practice.
        var baseIsLocal = localTypeFqns.Contains(SuspendLambdaFqn);

        foreach (var r in roots)
        {
            if (r is not JsonObject file) continue;
            var fileClass = Str(file["fileClass"]) ?? "Kt";
            var newTypes = new List<JsonNode>();
            var counter = new int[1];

            if (file["methods"] is JsonArray methods)
                foreach (var m in methods)
                    if (m is JsonObject mo) WalkMethod(mo, fileClass, newTypes, counter, baseIsLocal);

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
                                    Walk(cb, owner + "_ctor", newTypes, counter, baseIsLocal);
                        if (to["properties"] is JsonArray tps)
                            foreach (var p in tps)
                                if (p is JsonObject po) WalkAccessors(po, owner, newTypes, counter, baseIsLocal);
                    }

            if (newTypes.Count > 0)
            {
                var ts = file["types"] as JsonArray;
                if (ts == null) { ts = new JsonArray(); file["types"] = ts; }
                foreach (var nt in newTypes) ts.Add(nt);
            }
        }
    }

    static void WalkMethod(JsonObject method, string prefix, List<JsonNode> newTypes, int[] counter, bool baseIsLocal)
    {
        var mn = Str(method["name"]) ?? "m";
        if (method["body"] is JsonNode body) Walk(body, prefix + "_" + mn, newTypes, counter, baseIsLocal);
    }

    static void WalkAccessors(JsonObject prop, string prefix, List<JsonNode> newTypes, int[] counter, bool baseIsLocal)
    {
        var pn = Str(prop["name"]) ?? "p";
        foreach (var acc in new[] { "getter", "setter" })
            if (prop[acc] is JsonObject a && a["body"] is JsonNode b)
                Walk(b, prefix + "_" + pn + "_" + acc, newTypes, counter, baseIsLocal);
    }

    static void Walk(JsonNode node, string ctx, List<JsonNode> newTypes, int[] counter, bool baseIsLocal)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    var child = o[key];
                    if (child is JsonObject co && Str(co["k"]) == "suspendLambdaNew")
                        o[key] = BuildLambda(co, ctx, newTypes, counter, baseIsLocal);
                    else if (child != null)
                        Walk(child, ctx, newTypes, counter, baseIsLocal);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    var child = a[i];
                    if (child is JsonObject co && Str(co["k"]) == "suspendLambdaNew")
                        a[i] = BuildLambda(co, ctx, newTypes, counter, baseIsLocal);
                    else if (child != null)
                        Walk(child, ctx, newTypes, counter, baseIsLocal);
                }
                break;
        }
    }

    static JsonNode BuildLambda(JsonObject node, string ctx, List<JsonNode> newTypes, int[] counter, bool baseIsLocal)
    {
        // Bottom-up: lower any nested suspend lambdas inside THIS lambda's body first (their SMs + `new`
        // replacements land before this lambda's SM is built over the already-lowered body).
        var body = node["body"] as JsonArray ?? new JsonArray();
        Walk(body, ctx, newTypes, counter, baseIsLocal);

        var arity = IntOf(node["arity"]);
        var captures = ReadNameTypes(node["captures"]);
        var lambdaParams = (node["params"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        var resultType = Str(node["resultType"]);
        var typeArgs = ReadStrings(node["typeArgs"]);
        var smName = ctx + "_lambda" + (++counter[0]) + "$sm";

        var sm = SuspendColdLowering.BuildLambdaSm(
            smName, arity, captures, lambdaParams, body, resultType, typeArgs, baseIsLocal);
        if (sm == null) return node;   // arity >= 2 -> not expressible v1; keep the node (surfaces as a report)

        newTypes.Add(sm);

        var smInst = typeArgs.Count == 0
            ? smName
            : smName + "[" + string.Join(",", typeArgs.Select(t => "gp:" + t)) + "]";

        // The lambda VALUE: `new SM(captureVals..., null)` — captures read as locals at the emit site, a null
        // completion (a cold, unstarted lambda; create() rebinds the completion when a builder starts it).
        var args = new JsonArray();
        var argTypes = new JsonArray();
        foreach (var (n, t) in captures)
        {
            args.Add(new JsonObject { ["k"] = "local", ["name"] = n });
            argTypes.Add(t);
        }
        args.Add(new JsonObject { ["k"] = "const", ["type"] = ContinuationOfAny, ["value"] = null });
        argTypes.Add(ContinuationOfAny);

        return new JsonObject { ["k"] = "new", ["type"] = smInst, ["argTypes"] = argTypes, ["args"] = args };
    }

    static int IntOf(JsonNode n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    static List<(string name, string type)> ReadNameTypes(JsonNode arr)
    {
        var list = new List<(string, string)>();
        if (arr is JsonArray a)
            foreach (var it in a)
                if (it is JsonObject o && Str(o["name"]) is string n)
                    list.Add((n, Str(o["type"]) ?? "kotlin.Any"));
        return list;
    }

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
