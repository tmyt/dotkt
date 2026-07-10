using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

sealed class SuspendShapeAnalyzer
{
    public static SuspendShapeAnalysis Analyze(JsonNode root)
    {
        var functions = new List<SuspendFunctionShape>();
        CollectFileMethods(root, owner: null, functions);
        return new SuspendShapeAnalysis(functions);
    }

    static void CollectFileMethods(JsonNode node, string owner, List<SuspendFunctionShape> functions)
    {
        if (node is not JsonObject obj) return;

        if (obj["methods"] is JsonArray methods)
            foreach (var method in methods)
                CollectMethod(method, owner, functions);

        if (obj["types"] is JsonArray types)
            foreach (var type in types)
                CollectType(type, functions);
    }

    static void CollectType(JsonNode type, List<SuspendFunctionShape> functions)
    {
        if (type is not JsonObject obj) return;

        var owner = StringProp(obj, "name");
        if (obj["methods"] is JsonArray methods)
            foreach (var method in methods)
                CollectMethod(method, owner, functions);

        if (obj["types"] is JsonArray nested)
            foreach (var child in nested)
                CollectType(child, functions);
    }

    static void CollectMethod(JsonNode method, string owner, List<SuspendFunctionShape> functions)
    {
        if (method is not JsonObject obj || !ModFlag(obj, "suspend")) return;

        var awaits = CountKind(obj, "coSuspend");
        var intrinsicAwaits = CountKind(obj, "coSuspendIntrinsic");
        var returns = CountKind(obj, "coReturn");
        var cpsFields = obj["cpsFields"] is JsonArray fields ? fields.Count : 0;
        functions.Add(new SuspendFunctionShape(
            owner,
            StringProp(obj, "name") ?? "<anonymous>",
            StringProp(obj, "suspendRet") ?? StringProp(obj, "ret") ?? "void",
            awaits,
            intrinsicAwaits,
            returns,
            cpsFields));
    }

    static int CountKind(JsonNode node, string kind)
    {
        if (node is JsonObject obj)
        {
            var self = StringProp(obj, "k") == kind ? 1 : 0;
            return self + obj.Sum(kv => CountKind(kv.Value, kind));
        }

        if (node is JsonArray arr)
            return arr.Sum(item => CountKind(item, kind));

        return 0;
    }

    static string StringProp(JsonObject obj, string name) => (obj[name] as JsonValue)?.GetValue<string>();
    static bool BoolProp(JsonObject obj, string name) => (obj[name] as JsonValue)?.GetValue<bool>() == true;
    // Structured declaration modifier (spec §2.1): `decl.mods.<key> == true` (absent object/key = false).
    static bool ModFlag(JsonObject obj, string name) => obj["mods"] is JsonObject m && (m[name] as JsonValue)?.GetValue<bool>() == true;
}

sealed record SuspendShapeAnalysis(IReadOnlyList<SuspendFunctionShape> Functions)
{
    public int FunctionCount => Functions.Count;
    public int AwaitCount => Functions.Sum(f => f.Awaits + f.IntrinsicAwaits);

    public static SuspendShapeAnalysis Combine(IEnumerable<SuspendShapeAnalysis> analyses) =>
        new(analyses.SelectMany(a => a.Functions).ToList());

    public JsonObject ToJson() => new()
    {
        ["suspendFunctions"] = new JsonArray(Functions.Select(f => f.ToJson()).Cast<JsonNode>().ToArray()),
        ["totalSuspendFunctions"] = FunctionCount,
        ["totalAwaits"] = AwaitCount,
    };
}

sealed record SuspendFunctionShape(
    string Owner,
    string Name,
    string ResultType,
    int Awaits,
    int IntrinsicAwaits,
    int Returns,
    int CpsFields)
{
    public JsonObject ToJson() => new()
    {
        ["owner"] = Owner,
        ["name"] = Name,
        ["suspendRet"] = ResultType,
        ["awaits"] = Awaits,
        ["intrinsicAwaits"] = IntrinsicAwaits,
        ["returns"] = Returns,
        ["cpsFields"] = CpsFields,
    };
}

