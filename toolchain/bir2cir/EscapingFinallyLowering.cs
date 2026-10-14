using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;
using static ProtectedRegionTransfers;

// CLR finally handlers cannot override a pending exception/return by branching or returning outside the handler.
// Only a finally with such an escape is realized as ordinary cleanup code. Its try/catch completion records an
// exception or a continuation, leaves to ONE cleanup site, then resumes that completion if cleanup falls through.
// An escaping cleanup transfer naturally discards the pending completion. Native finally remains for every other
// case, including loops wholly inside a handler. ExceptionDispatchInfo preserves the original exception and stack.
static class EscapingFinallyLowering
{
    static int _tmp;

    public static void Apply(JsonArray[] parts, JsonNode returnType, ReferenceMetadataIndex refs, bool physicalMembers = true)
    {
        // Recompute physical ownership after each innermost rewrite: an outer handler may contain the newly
        // authored continuation of an inner cleanup. The finite list of original try nodes bounds this work.
        var tries = new List<JsonObject>();
        foreach (var part in parts) Collect(part, tries);
        var nextLabel = LabelIds(parts).DefaultIfEmpty(-1).Max() + 1;
        foreach (var statement in tries)
        {
            if (statement["finally"] is not JsonArray finallyBody) continue;
            var edges = Edges(parts);
            if (!edges.Any(edge => edge.Source.Contains(finallyBody) && !edge.Target.Contains(finallyBody))) continue;
            Rewrite(statement, finallyBody, edges, returnType, refs, nextLabel++, physicalMembers);
        }
    }

    static void Collect(JsonNode node, List<JsonObject> tries)
    {
        if (node is JsonArray array) { foreach (var child in array) Collect(child, tries); return; }
        if (node is not JsonObject obj || TypeJson.IsType(obj)
            || obj["params"] is JsonArray && obj["body"] is JsonArray) return;
        foreach (var child in obj) Collect(child.Value, tries);
        if (K(obj) == "try") tries.Add(obj);
    }

    static IEnumerable<int> LabelIds(IEnumerable<JsonArray> parts)
    {
        var ids = new List<int>();
        void Walk(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (K(obj) == "label") ids.Add(obj["id"].GetValue<int>());
                foreach (var child in obj) Walk(child.Value);
            }
            else if (node is JsonArray array) foreach (var child in array) Walk(child);
        }
        foreach (var part in parts) Walk(part);
        return ids;
    }

    static void Rewrite(JsonObject statement, JsonArray finallyBody, List<Edge> edges, JsonNode returnType,
        ReferenceMetadataIndex refs, int cleanupLabel, bool physicalMembers)
    {
        var id = System.Threading.Interlocked.Increment(ref _tmp);
        var state = "cir$completion" + id;
        var exception = "cir$exception" + id;
        var caught = "cir$caught" + id;
        var result = "cir$return" + id;
        var intType = TypeJson.Fqn("System.Int32");
        var exceptionType = TypeJson.Fqn("System.Runtime.ExceptionServices.ExceptionDispatchInfo");
        var continuations = new JsonArray();
        bool hasResult = false;
        var owned = new HashSet<JsonArray> { (JsonArray)statement["body"] };
        if (statement["catches"] is JsonArray catches)
            foreach (var handler in catches.OfType<JsonObject>()) owned.Add((JsonArray)handler["body"]);
        foreach (var edge in edges.Where(edge => edge.Source.Any(owned.Contains) && !edge.Target.Any(owned.Contains)).AsEnumerable().Reverse())
        {
            var original = (JsonObject)edge.Node.DeepClone();
            var expressionReturn = K(original) == "returnExpr";
            if (expressionReturn) original["k"] = "return";
            var pre = new JsonArray();
            if (K(original) == "return" && original["value"] is JsonNode value)
            {
                if (Str(returnType["name"]) is "void" or "System.Void")
                    pre.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = value.DeepClone() });
                else
                {
                    hasResult = true;
                    pre.Add(Set(result, value.DeepClone()));
                    original["value"] = Local(result);
                }
            }
            var continuation = continuations.Count + 1;
            pre.Add(Set(state, Number(continuation)));
            pre.Add(new JsonObject { ["k"] = "goto", ["id"] = cleanupLabel });
            // A conditional physical exit still evaluates its condition once at its original guarded site.
            if (K(original) == "brIf")
            {
                var condition = original["cond"].DeepClone();
                if (!original["on"].GetValue<bool>())
                    condition = new JsonObject { ["k"] = "unaryOp", ["op"] = "!", ["e"] = condition };
                Replace(edge.Node, If(condition, pre));
                original.Remove("cond"); original.Remove("on"); original["k"] = "goto";
            }
            else if (expressionReturn) Replace(edge.Node, new JsonObject { ["k"] = "valueBlock", ["stmts"] = pre,
                ["result"] = new JsonObject { ["k"] = "throwExpr", ["value"] = new JsonObject { ["k"] = "const",
                    ["type"] = TypeJson.Fqn("System.Exception"), ["value"] = null } } });
            else Replace(edge.Node, new JsonObject { ["k"] = "block", ["body"] = pre });
            continuations.Add(If(new JsonObject { ["k"] = "binOp", ["op"] = "==", ["lhs"] = Local(state),
                ["rhs"] = Number(continuation) }, new JsonArray(original)));
        }
        var originalBody = statement["body"].DeepClone();
        var originalCatches = statement["catches"]?.DeepClone() as JsonArray ?? new();
        if (originalCatches.Count > 0)
            originalBody = new JsonArray(new JsonObject { ["k"] = "try", ["body"] = originalBody, ["catches"] = originalCatches });
        var body = new JsonArray(
            new JsonObject { ["k"] = "var", ["name"] = state, ["type"] = intType, ["init"] = Number(0) },
            new JsonObject { ["k"] = "var", ["name"] = exception, ["type"] = exceptionType.DeepClone(),
                ["init"] = new JsonObject { ["k"] = "const", ["type"] = exceptionType.DeepClone(), ["value"] = null } });
        if (hasResult) body.Add(new JsonObject { ["k"] = "var", ["name"] = result, ["type"] = returnType.DeepClone() });
        body.Add(new JsonObject { ["k"] = "try", ["body"] = originalBody,
            ["catches"] = new JsonArray(new JsonObject { ["excType"] = TypeJson.Fqn("System.Exception"), ["var"] = caught,
                ["body"] = new JsonArray(Set(exception, Capture(caught, refs, physicalMembers))) }) });
        body.Add(new JsonObject { ["k"] = "label", ["id"] = cleanupLabel });
        foreach (var cleanup in finallyBody) body.Add(cleanup.DeepClone());
        body.Add(If(new JsonObject { ["k"] = "binOp", ["op"] = "!=", ["lhs"] = Local(exception),
            ["rhs"] = new JsonObject { ["k"] = "const", ["type"] = exceptionType.DeepClone(), ["value"] = null } },
            new JsonArray(Rethrow(exception, refs, physicalMembers))));
        foreach (var continuation in continuations) body.Add(continuation.DeepClone());
        Replace(statement, new JsonObject { ["k"] = "block", ["body"] = body });
    }

    static JsonObject Capture(string exception, ReferenceMetadataIndex refs, bool physicalMembers)
    {
        // The pre-suspend use still precedes ordinary CLR member resolution. Author its explicit CLR call
        // vocabulary there; the existing member-resolution phase binds it together with the surrounding body.
        var call = new JsonObject { ["k"] = "clrStatic",
            ["type"] = TypeJson.Fqn("System.Runtime.ExceptionServices.ExceptionDispatchInfo"),
            ["method"] = "Capture", ["args"] = new JsonArray(Local(exception)),
            ["argTypes"] = new JsonArray(TypeJson.Fqn("System.Exception")),
            ["ret"] = TypeJson.Fqn("System.Runtime.ExceptionServices.ExceptionDispatchInfo") };
        if (!physicalMembers) return call;
        var owner = refs.ResolveRefType("System.Runtime.ExceptionServices.ExceptionDispatchInfo");
        var capture = owner.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(method => method.Name == "Capture"
            && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType.FullName == "System.Exception");
        call["memberRef"] = ClrMemberResolution.MemberRefJson(capture, "method", owner, null);
        return call;
    }

    static JsonObject Rethrow(string exception, ReferenceMetadataIndex refs, bool physicalMembers)
    {
        var call = new JsonObject { ["k"] = "clrInstance",
            ["type"] = TypeJson.Fqn("System.Runtime.ExceptionServices.ExceptionDispatchInfo"),
            ["method"] = "Throw", ["recv"] = Local(exception),
            ["args"] = new JsonArray(), ["argTypes"] = new JsonArray(), ["ret"] = TypeJson.Fqn("void") };
        if (physicalMembers)
        {
            var owner = refs.ResolveRefType("System.Runtime.ExceptionServices.ExceptionDispatchInfo");
            var throwMethod = owner.GetMethods(BindingFlags.Public | BindingFlags.Instance).Single(method => method.Name == "Throw"
                && method.GetParameters().Length == 0);
            call["dispatch"] = "callvirt";
            call["memberRef"] = ClrMemberResolution.MemberRefJson(throwMethod, "method", owner, null);
        }
        return new JsonObject { ["k"] = "block", ["body"] = new JsonArray(
            new JsonObject { ["k"] = "exprStmt", ["expr"] = call },
            new JsonObject { ["k"] = "throw", ["value"] = new JsonObject { ["k"] = "const",
                ["type"] = TypeJson.Fqn("System.Exception"), ["value"] = null } }) };
    }

    static JsonObject Local(string name) => new() { ["k"] = "local", ["name"] = name };
    static JsonObject Number(int value) => new() { ["k"] = "const", ["type"] = TypeJson.Fqn("System.Int32"), ["value"] = value };
    static JsonObject Set(string name, JsonNode value) => new() { ["k"] = "setLocal", ["name"] = name, ["value"] = value };
    static JsonObject If(JsonNode condition, JsonArray body) => new() { ["k"] = "if",
        ["branches"] = new JsonArray(new JsonObject { ["cond"] = condition, ["body"] = body }) };
    static void Replace(JsonObject target, JsonObject replacement)
    {
        target.Clear();
        foreach (var item in replacement.ToArray()) target[item.Key] = item.Value?.DeepClone();
    }
}
