using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Keep the source dispatch slot fixed and choose nullable representations only for its private body.
// Each step adds one companion to a private method, so N demands generate N helpers, not 2^N bodies.
static class NullableBodyDispatch
{
    public static void Build(JsonObject owner, JsonObject source, JsonObject entry,
        NullableRepresentationFrame ownerFrame, NullableRepresentationFrame entryFrame,
        IEnumerable<int> bodyDemand, Action<JsonObject, NullableRepresentationFrame> prepare)
    {
        var missing = bodyDemand.Except(entryFrame.NullableIndices).OrderBy(i => i).ToArray();
        if (missing.Length == 0) return;
        var ownerName = Text(owner["name"]) ?? Text(owner["fileClass"]);
        var ownerType = new TypeNode.Fqn(ownerName, ownerFrame.PhysicalArity == 0 ? null
            : Enumerable.Range(0, ownerFrame.PhysicalArity).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray());
        var methods = (JsonArray)owner["methods"];
        var names = methods.OfType<JsonObject>().Select(m => Text(m["name"])).ToHashSet(StringComparer.Ordinal);
        var chain = new List<(JsonObject Method, NullableRepresentationFrame Frame)> { (entry, entryFrame) };
        var indices = entryFrame.NullableIndices.ToList();
        foreach (var index in missing)
        {
            indices.Add(index);
            var frame = new NullableRepresentationFrame(entryFrame.SourceArity, indices.OrderBy(i => i));
            var suffix = 0;
            string name;
            do { name = "$nullableBody$" + suffix++; } while (!names.Add(name));
            var helper = new JsonObject {
                ["name"] = name, ["vis"] = "private", ["static"] = source["static"]?.DeepClone() ?? JsonValue.Create(false),
                ["virtual"] = false, ["override"] = false, ["abstract"] = false,
                ["params"] = source["params"].DeepClone(), ["ret"] = source["ret"].DeepClone(),
                ["typeParams"] = source["typeParams"].DeepClone(), ["attrs"] = new JsonArray(),
                ["body"] = index == missing[^1] ? source["body"].DeepClone() : new JsonArray(),
                [DeclarationIdentityBinding.Key] = DeclarationIdentityBinding.PhysicalOnlyId(
                    Text(source[DeclarationIdentityBinding.Key]) ?? "nullable-body:" + ownerName, name),
            };
            foreach (var key in new[] { "suspend", "suspendRet", "suspendResult" })
                if (source[key] != null) helper[key] = source[key].DeepClone();
            prepare(helper, frame);
            methods.Add(helper);
            chain.Add((helper, frame));
        }
        for (var step = 0; step < missing.Length; step++)
        {
            var (current, currentFrame) = chain[step];
            var (target, targetFrame) = chain[step + 1];
            var index = missing[step];
            JsonObject Call(TypeNode chosen)
            {
                var arguments = Enumerable.Range(0, entryFrame.SourceArity)
                    .Select(i => (TypeNode)new TypeNode.Tv("method", i))
                    .Concat(targetFrame.NullableIndices.Select(i => i == index ? chosen
                        : currentFrame.NullableVariable(new TypeNode.Tv("method", i)))).ToArray();
                var call = new JsonObject {
                    ["k"] = "callInstance", ["ownerType"] = TypeJson.Write(ownerType), ["virtual"] = false,
                    ["recv"] = new JsonObject { ["k"] = "this" }, ["method"] = Text(target["name"]),
                    [DeclarationIdentityBinding.Key] = target[DeclarationIdentityBinding.Key].DeepClone(),
                    ["typeArgs"] = new JsonArray(arguments.Select(TypeJson.Write).ToArray()),
                    ["sig"] = new JsonArray(((JsonArray)target["params"]).OfType<JsonObject>()
                        .Select(p => p["type"].DeepClone()).ToArray()),
                    ["args"] = new JsonArray(((JsonArray)current["params"]).OfType<JsonObject>()
                        .Select(p => (JsonNode)new JsonObject { ["k"] = "local", ["name"] = Text(p["name"]) }).ToArray()),
                    ["dynRet"] = current["ret"].DeepClone(),
                };
                if (source["suspend"] is JsonValue suspend && suspend.TryGetValue<bool>(out var isSuspend) && isSuspend)
                    call["suspendCall"] = true;
                return call;
            }
            current["body"] = new JsonArray(new JsonObject {
                ["k"] = "return", ["value"] = new JsonObject {
                    ["k"] = "cond", ["type"] = current["ret"].DeepClone(),
                    ["cond"] = new JsonObject {
                        ["k"] = "clrPropGet", ["type"] = TypeJson.Fqn("System.Type"),
                        ["name"] = "IsValueType", ["static"] = false, ["ret"] = TypeJson.Fqn("kotlin.Boolean"),
                        ["recv"] = new JsonObject { ["k"] = "classRef", ["type"] = TypeJson.Write(new TypeNode.Tv("method", index)) },
                    },
                    ["then"] = Call(new TypeNode.Fqn("object")),
                    ["else"] = Call(new TypeNode.Tv("method", index)),
                },
            });
        }
    }

    static string Text(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var text) == true ? text : null;
}
