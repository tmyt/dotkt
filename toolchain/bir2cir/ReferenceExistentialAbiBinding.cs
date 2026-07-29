using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// dll2klib restores a compiler-generated existential's Kotlin surface (`G$dotkt_star` -> `G<*>`) before frontend
// analysis. That is the correct source signature, but a cross-module CIR call still has to name the referenced DLL's
// physical parameter/return slots. Rebind only provenance-verified existential signatures from the reference index,
// then align directly initialized locals with the physical result. Metadata remains the frontend authority; this pass
// is the Kotlin-to-CLR ABI boundary and ilemit receives an exact signature.
static class ReferenceExistentialAbiBinding
{
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs)
    {
        BindCalls(root, refs);
        AlignLocals(root);
    }

    static void BindCalls(JsonNode node, ReferenceMetadataIndex refs)
    {
        switch (node)
        {
            case JsonObject call:
                var kind = Str(call["k"]);
                if (kind is "callInstance" or "callStatic"
                    && Owner(call, kind) is string owner
                    && Str(call["method"]) is string method)
                {
                    var parameters = call["args"] as JsonArray;
                    var paramCount = parameters?.Count ?? -1;
                    var methodArity = (call["typeArgs"] as JsonArray)?.Count ?? 0;
                    if (paramCount >= 0 && refs.TryExistentialAbiMember(owner, method,
                        kind == "callStatic", methodArity, paramCount, out var physicalParams, out var physicalResult))
                    {
                        call["sig"] = new JsonArray(physicalParams.Select(TypeJson.Write).ToArray());
                        call["ret"] = TypeJson.Write(physicalResult);
                    }
                }
                foreach (var value in call.Select(kv => kv.Value).ToList())
                    if (value != null) BindCalls(value, refs);
                break;
            case JsonArray array:
                foreach (var value in array)
                    if (value != null) BindCalls(value, refs);
                break;
        }
    }

    static string Owner(JsonObject call, string kind)
    {
        if (kind == "callInstance")
            return (TypeJson.Read(call["ownerType"]) as TypeNode.Fqn)?.Name;
        return (TypeJson.Read(call["owner"]) as TypeNode.Fqn)?.Name
            ?? (TypeJson.Read(call["ownerType"]) as TypeNode.Fqn)?.Name
            ?? (TypeJson.Read(call["calleeOwner"]) as TypeNode.Fqn)?.Name;
    }

    static void AlignLocals(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (Str(obj["k"]) == "var"
                    && ExprType(obj["init"]) is TypeNode.Fqn physical
                    && physical.Name.EndsWith("$dotkt_star", StringComparison.Ordinal))
                    obj["type"] = TypeJson.Write(physical);
                foreach (var value in obj.Select(kv => kv.Value).ToList())
                    if (value != null) AlignLocals(value);
                break;
            case JsonArray array:
                foreach (var value in array)
                    if (value != null) AlignLocals(value);
                break;
        }
    }

    static TypeNode ExprType(JsonNode node)
    {
        if (node is not JsonObject obj) return null;
        if (TypeJson.Read(obj["ret"]) is TypeNode ret) return ret;
        if (Str(obj["k"]) == "valueBlock") return ExprType(obj["result"]);
        return TypeJson.Read(obj["type"]) ?? TypeJson.Read(obj["sty"]);
    }

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
