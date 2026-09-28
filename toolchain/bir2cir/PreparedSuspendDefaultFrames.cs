using System.Text.Json.Nodes;
using DotKt.Bir;

// A materialized source suspend lambda owns a declaration frame distinct from its application.
// Stage that declaration for the ordinary generic-frame pass, then return its transformed fields
// to the lambda before suspend lowering. The temporary method is never emitted or published.
static class PreparedSuspendDefaultFrames
{
    static int _next;

    public static Action Stage(JsonObject file, IEnumerable<JsonNode> inputs)
    {
        var staged = new List<(JsonObject Node, JsonObject Declaration, int Captures)>();
        var seen = new HashSet<JsonNode>();
        var methods = (JsonArray)file["methods"];
        void Walk(JsonNode value)
        {
            if (value == null || !seen.Add(value)) return;
            if (value is JsonArray array)
            {
                foreach (var child in array.ToArray()) Walk(child);
                return;
            }
            if (value is not JsonObject node) return;
            if (node["k"]?.GetValue<string>() == "newSuspendLambda"
                && node[SuspendLambdaLowering.SplicedDeclarationFrameKey] is JsonArray origins)
            {
                var names = (JsonArray)node["typeParams"];
                if (origins.Count != names.Count)
                    throw new InvalidOperationException("Prepared suspend default has inconsistent source frame");
                var owner = new JsonArray();
                var method = new JsonArray();
                for (var i = 0; i < origins.Count; i++)
                {
                    if (TypeJson.Read(origins[i]) is not TypeNode.Tv variable)
                        throw new InvalidOperationException("Suspend declaration frame requires authored type variables");
                    var target = variable.Scope == "type" ? owner : method;
                    var index = node["typeFrame"]?.GetValue<string>() == "dense" ? i : variable.I;
                    while (target.Count <= index) target.Add((JsonNode)null);
                    target[index] = TypeJson.Write(new TypeNode.Tv("method", i));
                }
                var captures = node["captures"] as JsonArray ?? new JsonArray();
                var parameters = node["params"] as JsonArray ?? new JsonArray();
                var serial = System.Threading.Interlocked.Increment(ref _next);
                var declaration = new JsonObject {
                    ["name"] = "__preparedSuspendDefault$" + serial,
                    [DeclarationIdentityBinding.Key] = DeclarationIdentityBinding.PhysicalOnlyId("prepared-suspend-default", serial.ToString()),
                    ["generated"] = true, ["static"] = true,
                    ["typeParams"] = (node["typeParamDecls"] ?? names).DeepClone(),
                    ["params"] = new JsonArray(captures.Concat(parameters).Select(p => p.DeepClone()).ToArray()),
                    ["ret"] = node["suspendRet"]?.DeepClone(),
                    ["body"] = node["body"]?.DeepClone(),
                };
                InlineSplice.SubstTvIn(declaration, method, method.Count, owner);
                foreach (var key in new[] { "captures", "params", "suspendRet", "body", "typeParams", "typeParamDecls",
                    SuspendLambdaLowering.SplicedDeclarationFrameKey }) node.Remove(key);
                node[DeclarationIdentityBinding.Key] = declaration[DeclarationIdentityBinding.Key].DeepClone();
                methods.Add(declaration);
                staged.Add((node, declaration, captures.Count));
                Walk(declaration);
            }
            foreach (var (key, child) in node.ToArray()) if (key != "attrs") Walk(child);
        }
        foreach (var input in inputs.ToArray()) Walk(input);
        return () => {
            // Inner declarations are restored before their containing bodies are copied back.
            foreach (var (node, declaration, captures) in staged.AsEnumerable().Reverse())
            {
                var parameters = (JsonArray)declaration["params"];
                node["captures"] = new JsonArray(parameters.Take(captures).Select(p => p.DeepClone()).ToArray());
                node["params"] = new JsonArray(parameters.Skip(captures).Select(p => p.DeepClone()).ToArray());
                node["suspendRet"] = declaration["ret"].DeepClone();
                node["body"] = declaration["body"].DeepClone();
                var typeParameters = (JsonArray)declaration["typeParams"];
                node["typeParamDecls"] = typeParameters.DeepClone();
                node["typeParams"] = new JsonArray(typeParameters.Select(p => p is JsonObject parameter
                    ? parameter["name"].DeepClone() : p.DeepClone()).ToArray());
                node["typeFrame"] = "dense";
                node[SuspendLambdaLowering.SplicedDeclarationFrameKey] = new JsonArray(
                    Enumerable.Range(0, typeParameters.Count).Select(i => TypeJson.Write(new TypeNode.Tv("method", i))).ToArray());
                node.Remove(DeclarationIdentityBinding.Key);
                methods.Remove(declaration);
            }
        };
    }
}
