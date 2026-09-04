using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// dll2klib restores a compiler-generated existential's Kotlin surface (physical carrier -> `G<*>`) before frontend
// analysis. That is the correct source signature, but a cross-module CIR call still has to name the referenced DLL's
// physical parameter/return slots. Rebind only provenance-verified existential signatures from the reference index,
// then align directly initialized locals with the physical result. Metadata remains the frontend authority; this pass
// is the Kotlin-to-CLR ABI boundary and ilemit receives an exact signature.
static class ReferenceExistentialAbiBinding
{
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs)
    {
        BindCalls(root, refs);
        AlignLocals(root, refs);
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
                        var currentSignature = call["sig"] as JsonArray;
                        call["sig"] = new JsonArray(physicalParams.Select((parameter, index) =>
                            ContainsPhysicalExistential(parameter, refs)
                                ? TypeJson.Write(parameter)
                                : currentSignature?[index]?.DeepClone() ?? TypeJson.Write(parameter)).ToArray());
                        var previousResult = TypeJson.Read(call["ret"]);
                        // A member may enter this path solely because one parameter uses an existential carrier. Its
                        // unrelated result remains the frontend-instantiated caller fact (`List<T>`, for example);
                        // replacing that with the declaration reader's open frame would either lose arguments or put
                        // the callee's method-TV indexes into the caller's frame. Only a physically existential result
                        // needs this ABI projection.
                        if (ContainsPhysicalExistential(physicalResult, refs))
                            call["ret"] = TypeJson.Write(physicalResult);
                        // Spec §2.7: a pass that changes a node's RESULT TYPE rewrites or deletes its `sty`. Binding
                        // the referenced DLL's PHYSICAL result is such a change — the existential erasure can make it
                        // a type unrelated to the frontend's INSTANTIATED stamp, and that stamp is read FIRST by every
                        // deriver, so a slot declared from it would name a type the value does not have. The
                        // instantiation cannot be recovered from the physical signature, so the stamp is DROPPED (the
                        // other thing §2.7 permits) where the binding invalidated it, and kept where it did not.
                        //
                        // Gated on THIS pass having actually changed the result: §2.7 is an obligation on the pass
                        // that retypes, and a pass that silently laundered another pass's stale stamp would remove the
                        // evidence the chokepoint exists to surface.
                        if (ContainsPhysicalExistential(physicalResult, refs)
                            && !physicalResult.Equals(previousResult)) NodeType.DropStampIfStale(call);
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

    static bool ContainsPhysicalExistential(TypeNode type, ReferenceMetadataIndex refs) => type switch
    {
        TypeNode.Fqn f => refs.IsExistentialPhysicalOwner(f.Name)
            || f.Args?.Any(argument => ContainsPhysicalExistential(argument, refs)) == true,
        TypeNode.Nullable nullable => ContainsPhysicalExistential(nullable.Of, refs),
        TypeNode.Oblivious oblivious => ContainsPhysicalExistential(oblivious.Of, refs),
        TypeNode.Array array => ContainsPhysicalExistential(array.Elem, refs),
        TypeNode.ByRef byRef => ContainsPhysicalExistential(byRef.Of, refs),
        TypeNode.Fn function => ContainsPhysicalExistential(function.Ret, refs)
            || function.Params.Any(parameter => ContainsPhysicalExistential(parameter, refs))
            || function.Recv != null && ContainsPhysicalExistential(function.Recv, refs),
        _ => false,
    };

    static string Owner(JsonObject call, string kind)
    {
        if (kind == "callInstance")
            return (TypeJson.Read(call["ownerType"]) as TypeNode.Fqn)?.Name;
        return (TypeJson.Read(call["owner"]) as TypeNode.Fqn)?.Name
            ?? (TypeJson.Read(call["ownerType"]) as TypeNode.Fqn)?.Name
            ?? (TypeJson.Read(call["calleeOwner"]) as TypeNode.Fqn)?.Name;
    }

    static void AlignLocals(JsonNode node, ReferenceMetadataIndex refs)
    {
        switch (node)
        {
            case JsonObject obj:
                if (Str(obj["k"]) == "var"
                    && ExprType(obj["init"]) is TypeNode.Fqn physical
                    && refs.IsExistentialPhysicalOwner(physical.Name))
                    obj["type"] = TypeJson.Write(physical);
                foreach (var value in obj.Select(kv => kv.Value).ToList())
                    if (value != null) AlignLocals(value, refs);
                break;
            case JsonArray array:
                foreach (var value in array)
                    if (value != null) AlignLocals(value, refs);
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
