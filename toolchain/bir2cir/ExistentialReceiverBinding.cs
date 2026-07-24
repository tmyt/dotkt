using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// FBoundStarProjectionErasure gives Kotlin's G<*> / erased G<T> cast a physical non-generic
// G$dotkt_star interface. GenericDowncastRealignment subsequently aligns a local declaration
// with such a cast. Calls through that local must then target the exact existential interface
// slot, including its deterministic owner-T-dependent bridge name. Leaving the original G<T>
// MemberRef on a G$dotkt_star receiver is invalid CLR IL.
//
// This is an explicit CIR binding pass: it consumes the synthesized interface's actual method
// table (or reference metadata), and ilemit merely emits the owner/member recorded here.
static class ExistentialReceiverBinding
{
    const string Suffix = "$dotkt_star";

    public sealed class Index
    {
        internal readonly Dictionary<string, List<Member>> Members = new(StringComparer.Ordinal);
    }

    internal sealed record Member(string Name, int ParamCount, int GenericArity);

    public static Index Collect(IEnumerable<JsonNode> roots)
    {
        var index = new Index();
        foreach (var root in roots) CollectTypes(root, index);
        return index;
    }

    static void CollectTypes(JsonNode node, Index index)
    {
        if (node is not JsonObject obj || obj["types"] is not JsonArray types) return;
        foreach (var type in types.OfType<JsonObject>())
        {
            var name = Str(type["name"]);
            if (name != null && name.EndsWith(Suffix, StringComparison.Ordinal)
                && type["methods"] is JsonArray methods)
            {
                var slots = new List<Member>();
                foreach (var method in methods.OfType<JsonObject>())
                    if (!Bool(method["static"]) && Str(method["name"]) is string mn)
                        slots.Add(new Member(
                            mn,
                            (method["params"] as JsonArray)?.Count ?? 0,
                            (method["typeParams"] as JsonArray)?.Count ?? 0));
                index.Members[name] = slots;
            }
            CollectTypes(type, index);
        }
    }

    public static void Apply(JsonNode root, Index index, ReferenceMetadataIndex refs)
    {
        VisitDeclarations(root, index, refs);
    }

    static void VisitDeclarations(JsonNode node, Index index, ReferenceMetadataIndex refs)
    {
        if (node is not JsonObject obj) return;

        if (obj["body"] is JsonArray body && obj["params"] is JsonArray)
        {
            var vars = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            foreach (var p in (obj["params"] as JsonArray).OfType<JsonObject>())
                if (Str(p["name"]) is string pn && TypeJson.Read(p["type"]) is TypeNode pt)
                    vars[pn] = pt;
            VisitStatements(body, vars, index, refs);
        }

        if (obj["types"] is JsonArray types)
            foreach (var type in types.OfType<JsonObject>()) VisitDeclarations(type, index, refs);
        if (obj["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>()) VisitDeclarations(method, index, refs);
        if (obj["ctors"] is JsonArray ctors)
            foreach (var ctor in ctors.OfType<JsonObject>()) VisitDeclarations(ctor, index, refs);
    }

    static void VisitStatements(JsonNode node, Dictionary<string, TypeNode> vars,
        Index index, ReferenceMetadataIndex refs)
    {
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
                if (item != null) VisitStatements(item, vars, index, refs);
            return;
        }
        if (node is not JsonObject obj) return;

        var kind = Str(obj["k"]);
        if (kind == "var")
        {
            if (obj["init"] != null) VisitStatements(obj["init"], vars, index, refs);
            if (Str(obj["name"]) is string vn && TypeJson.Read(obj["type"]) is TypeNode vt)
                vars[vn] = vt;
            return;
        }

        if (kind == "callInstance")
            BindCall(obj, vars, index, refs);

        // A nested carrier owns its own parameters/locals. It is visited as a declaration
        // elsewhere if materialized; do not leak the enclosing lexical environment into it.
        if (kind is "lambda" or "inlineLambda" or "newClosure" or "newDelegate"
            or "newSuspendLambda" or "forEachInline" or "repeatInline")
            return;

        foreach (var value in obj.Select(kv => kv.Value).ToList())
            if (value != null) VisitStatements(value, vars, index, refs);
    }

    static void BindCall(JsonObject call, IReadOnlyDictionary<string, TypeNode> vars,
        Index index, ReferenceMetadataIndex refs)
    {
        if (Str(call["method"]) is not string sourceMethod || sourceMethod.StartsWith("$dotkt_star$", StringComparison.Ordinal))
            return;
        var receiverType = ReceiverType(call["recv"], vars) as TypeNode.Fqn;
        if (receiverType == null || !receiverType.Name.EndsWith(Suffix, StringComparison.Ordinal)) return;

        var pc = (call["sig"] as JsonArray)?.Count
            ?? (call["argTypes"] as JsonArray)?.Count
            ?? (call["args"] as JsonArray)?.Count ?? 0;
        var ga = (call["typeArgs"] as JsonArray)?.Count ?? 0;
        string physicalMethod = null;

        if (index.Members.TryGetValue(receiverType.Name, out var members))
        {
            var prefix = "$dotkt_star$" + sourceMethod + "$";
            var candidates = members
                .Where(m => m.ParamCount == pc && m.GenericArity == ga
                    && (m.Name == sourceMethod || m.Name.StartsWith(prefix, StringComparison.Ordinal)))
                .Select(m => m.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (candidates.Count == 1) physicalMethod = candidates[0];
        }
        else
        {
            var sourceOwner = receiverType.Name[..^Suffix.Length];
            if (refs.TryStarProjectionMember(sourceOwner, sourceMethod, pc, out var erasedOwner, out var erasedMethod)
                && erasedOwner == receiverType.Name)
                physicalMethod = erasedMethod;
        }

        if (physicalMethod == null) return; // ambiguous or absent: never guess a physical slot
        call["ownerType"] = TypeJson.Write(receiverType);
        call["method"] = physicalMethod;
        call["virtual"] = true;
    }

    static TypeNode ReceiverType(JsonNode receiver, IReadOnlyDictionary<string, TypeNode> vars)
    {
        if (receiver is not JsonObject obj) return null;
        if (Str(obj["k"]) == "cast") return TypeJson.Read(obj["type"]);
        if (Str(obj["k"]) == "local" && Str(obj["name"]) is string name
            && vars.TryGetValue(name, out var type)) return type;
        return TypeJson.Read(obj["sty"]) ?? TypeJson.Read(obj["ret"]) ?? TypeJson.Read(obj["type"]);
    }

    static bool Bool(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
