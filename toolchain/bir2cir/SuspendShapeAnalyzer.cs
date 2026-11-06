using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

sealed class SuspendShapeAnalyzer
{
    public static SuspendShapeAnalysis Analyze(JsonNode root)
    {
        return new SuspendShapeAnalysis(CountFunctions(root));
    }

    static int CountFunctions(JsonNode node)
    {
        if (node is not JsonObject obj) return 0;
        var count = obj["methods"] is JsonArray methods
            ? methods.OfType<JsonObject>().Count(method => ModFlag(method, "suspend"))
            : 0;
        if (obj["types"] is JsonArray types)
            count += types.Sum(CountFunctions);
        return count;
    }

    // Structured declaration modifier (spec §2.1): `decl.mods.<key> == true` (absent object/key = false).
    static bool ModFlag(JsonObject obj, string name) => obj["mods"] is JsonObject m && (m[name] as JsonValue)?.GetValue<bool>() == true;
}

sealed record SuspendShapeAnalysis(int FunctionCount)
{
    public static SuspendShapeAnalysis Combine(IEnumerable<SuspendShapeAnalysis> analyses) =>
        new(analyses.Sum(a => a.FunctionCount));
}

