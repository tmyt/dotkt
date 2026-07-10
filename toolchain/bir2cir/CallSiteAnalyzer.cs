using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

sealed class CallSiteAnalyzer
{
    static readonly HashSet<string> InterestingKinds = new(StringComparer.Ordinal)
    {
        "callStatic",
        "callInstance",
        "new",
        "field",
        "staticField",
        "setFieldExpr",
        "staticFieldSet",
        "clrStatic",
        "clrGenericStatic",
        "clrInstance",
        "clrGenericInstance",
        "newClr",
        "clrPropGet",
        "clrPropSet",
        "clrStaticField",
    };

    public static CallSiteAnalysis Analyze(JsonNode root)
    {
        var sites = new List<CallSite>();
        Collect(root, owner: null, method: null, path: "$", sites);
        return new CallSiteAnalysis(sites);
    }

    static void Collect(JsonNode node, string owner, string method, string path, List<CallSite> sites)
    {
        if (node is JsonObject obj)
        {
            var nextOwner = owner;
            var nextMethod = method;

            if (obj["kind"]?.GetValue<string>() is "class" or "interface")
                nextOwner = StringProp(obj, "name") ?? owner;
            if ((obj["params"] is JsonArray && obj["body"] is JsonArray) || obj["steps"] is JsonArray)
                nextMethod = StringProp(obj, "name") ?? method;

            var kind = StringProp(obj, "k");
            if (kind != null && InterestingKinds.Contains(kind))
                sites.Add(CallSite.From(kind, path, nextOwner, nextMethod, obj));

            foreach (var child in obj)
                if (child.Value != null)
                    Collect(child.Value, nextOwner, nextMethod, path + "." + EscapePathSegment(child.Key), sites);
        }
        else if (node is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                var item = arr[i];
                if (item != null)
                    Collect(item, owner, method, path + "[" + i + "]", sites);
            }
        }
    }

    static string EscapePathSegment(string segment) =>
        segment.Replace("~", "~0", StringComparison.Ordinal).Replace(".", "~1", StringComparison.Ordinal);

    static string StringProp(JsonObject obj, string name) => (obj[name] as JsonValue)?.GetValue<string>();
}

sealed record CallSiteAnalysis(IReadOnlyList<CallSite> Sites)
{
    public JsonObject ToJson()
    {
        var byStatus = Sites
            .GroupBy(s => s.Status)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());

        return new JsonObject
        {
            ["total"] = Sites.Count,
            ["byStatus"] = new JsonObject(byStatus.ToDictionary(kv => kv.Key, kv => (JsonNode)JsonValue.Create(kv.Value))),
            ["sites"] = new JsonArray(Sites.Select(s => s.ToJson()).Cast<JsonNode>().ToArray()),
        };
    }
}

sealed record CallSite(
    string Kind,
    string Path,
    string Status,
    string Owner,
    string Method,
    string TargetOwner,
    string TargetName,
    string Signature,
    int ArgCount,
    IReadOnlyList<string> ArgTypes)
{
    public static CallSite From(string kind, string path, string owner, string method, JsonObject node)
    {
        var targetOwner = StringProp(node, "owner")
            ?? StringProp(node, "ownerType")
            ?? StringProp(node, "type")
            ?? "";
        var targetName = StringProp(node, "method")
            ?? StringProp(node, "name")
            ?? "";
        var argTypes = ArgumentTypes(node);
        var signature = string.Join(",", argTypes);

        return new CallSite(
            kind,
            path,
            StatusFor(kind, targetOwner),
            owner ?? "",
            method ?? "",
            targetOwner,
            targetName,
            signature,
            node["args"] is JsonArray args ? args.Count : -1,
            argTypes);
    }

    public JsonObject ToJson() => new()
    {
        ["kind"] = Kind,
        ["path"] = Path,
        ["status"] = Status,
        ["owner"] = Owner,
        ["method"] = Method,
        ["targetOwner"] = TargetOwner,
        ["targetName"] = TargetName,
        ["signature"] = Signature,
        ["argCount"] = ArgCount,
        ["argTypes"] = new JsonArray(ArgTypes.Select(t => JsonValue.Create(t)).Cast<JsonNode>().ToArray()),
    };

    static string StatusFor(string kind, string targetOwner)
    {
        if (kind.StartsWith("clr", StringComparison.Ordinal)) return "already-clr";
        return "kotlin-symbol";
    }

    static string StringProp(JsonObject obj, string name) => (obj[name] as JsonValue)?.GetValue<string>();

    static IReadOnlyList<string> ArgumentTypes(JsonObject node)
    {
        // `sig` is a STRUCTURED TypeNode array (#37 m3b) — render each param's canonical ParamKey for the diagnostic.
        if (node["sig"] is JsonArray sig && sig.Count > 0)
            return sig.Select(el => ReferenceMetadataIndex.ParamKey(el)).ToList();

        if (node["args"] is not JsonArray args) return Array.Empty<string>();

        var inferred = new List<string>();
        foreach (var arg in args)
            inferred.Add(InferExpressionType(arg));
        return inferred;
    }

    static string InferExpressionType(JsonNode node)
    {
        if (node is not JsonObject obj) return "";
        return StringProp(obj, "type")
            ?? StringProp(obj, "ret")
            ?? StringProp(obj, "suspendRet")
            ?? "";
    }
}

