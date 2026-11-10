using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A non-reified Kotlin scalar parameter/result transports a value, not a request to
// extract the caller's exact CLR T. Keep that witness in the declaration's generic
// frame; native signatures, managed locations and concrete scalar slots remain exact.
static class KotlinGenericScalarSlots
{
    internal sealed record Projection(Dictionary<string, int[]> Parameters, HashSet<string> Returns,
        GenericRepresentationPolicy Policy, HashSet<string> Intrinsics, ReferenceMetadataIndex References)
    {
        internal void ProjectMethod(JsonObject method)
        {
            if (Policy.HasNativeMethodSignature(method)
                || (method["overrides"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Any(edge =>
                    edge[DeclarationIdentityBinding.Key]?.GetValue<string>() is string id
                    && (Intrinsics.Contains(id) || References?.TryDeclarationIdentity(id,
                        out _, out _, out var intrinsic, out _) == true && intrinsic != null))) return;
            var projected = new List<int>();
            var slots = method["params"] as JsonArray ?? new JsonArray();
            // Query entry barriers belong to their selected collection contract;
            // its dedicated lowering retains concrete/representative-bound guards.
            var query = KotlinCollectionSlotSynthesis.ErasedQueryOverride(method) != null;
            for (var index = 0; index < slots.Count; index++)
            {
                var parameter = slots[index].AsObject();
                if (!query && TypeJson.Read(parameter["type"]) is TypeNode.Tv variable)
                {
                    parameter["kotlinType"] ??= parameter["nullableGeneric"]?.DeepClone() ?? JsonValue.Create(TypeNode.ToJson(variable));
                    parameter["type"] = TypeJson.Fqn("object");
                    projected.Add(index);
                }
            }
            if (TypeJson.Read(method["ret"]) is TypeNode.Tv result)
            {
                method["retKotlinType"] ??= method["nullableGenericRet"]?.DeepClone() ?? JsonValue.Create(TypeNode.ToJson(result));
                method["ret"] = TypeJson.Fqn("object");
            }
            if (method[DeclarationIdentityBinding.Key]?.GetValue<string>() is string identity)
            {
                if (projected.Count != 0) Parameters[identity] = projected.ToArray();
                if (method["retKotlinType"]?.GetValue<string>() is string sourceReturn
                    && TypeNode.Parse(sourceReturn) is TypeNode.Tv)
                    Returns.Add(identity);
            }
        }

        internal TypeNode.Tv Result(JsonObject call, ReferenceMetadataIndex refs) =>
            call["k"]?.GetValue<string>() is "callStatic" or "callInstance"
            && TypeJson.Read(call["sty"] ?? call["dynRet"]) is TypeNode.Tv variable
            && call[DeclarationIdentityBinding.Key]?.GetValue<string>() is string identity
            && (Returns.Contains(identity) || refs.IsGenericScalarReturn(identity)) ? variable : null;
    }

    internal static Projection ApplyAll(IEnumerable<JsonNode> roots, GenericRepresentationPolicy policy,
        ReferenceMetadataIndex references)
    {
        var files = roots.ToArray();
        var intrinsics = new HashSet<string>();
        void NativeBindings(JsonObject owner)
        {
            foreach (var method in (owner["methods"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (method[DeclarationIdentityBinding.Key]?.GetValue<string>() is string id
                    && (method["attrs"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                        .Any(attribute => TypeJson.OwnerName(attribute["attr"]) == "kotlin.clr.ClrIntrinsic"))
                    intrinsics.Add(id);
            foreach (var type in (owner["types"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()) NativeBindings(type);
        }
        foreach (var root in files.OfType<JsonObject>()) NativeBindings(root);
        var declarations = new Dictionary<string, JsonObject>();
        var projection = new Projection(new Dictionary<string, int[]>(), new HashSet<string>(), policy, intrinsics, references);
        void Method(JsonObject method)
        {
            projection.ProjectMethod(method);
            if (method[DeclarationIdentityBinding.Key]?.GetValue<string>() is string identity)
                declarations[identity] = method;
        }
        void Owner(JsonObject owner)
        {
            var name = (owner["name"] ?? owner["fileClass"])?.GetValue<string>();
            if (!policy.IsBindingAlias(name))
            {
                foreach (var method in (owner["methods"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    Method(method);
                foreach (var property in (owner["properties"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    if (!policy.HasNativeMethodSignature(property)
                        && TypeJson.Read(property["type"]) is TypeNode.Tv variable)
                    {
                        property["kotlinType"] ??= TypeNode.ToJson(variable);
                        property["type"] = TypeJson.Fqn("object");
                    }
            }
            foreach (var type in (owner["types"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()) Owner(type);
        }
        foreach (var root in files.OfType<JsonObject>()) Owner(root);
        void Calls(JsonNode node)
        {
            if (node is JsonArray array) { foreach (var child in array) Calls(child); return; }
            if (node is not JsonObject operation) return;
            foreach (var (key, child) in operation.ToArray())
                if (key is not ("attrs" or "overrides" or DeclarationIdentityBinding.SemanticSignatureKey)) Calls(child);
            if (operation["k"]?.GetValue<string>() is not ("callStatic" or "callInstance" or "constrainedCall")
                || operation[DeclarationIdentityBinding.Key]?.GetValue<string>() is not string identity
                || !declarations.TryGetValue(identity, out var selected)) return;
            if (projection.Parameters.TryGetValue(identity, out var indices) && operation["sig"] is JsonArray signature)
                foreach (var index in indices) signature[index] = TypeJson.Fqn("object");
            // The invocation retains its caller-facing value type. The selected
            // declaration supplies the erased physical return independently;
            // overwriting this hint would lose concrete arithmetic/nullable uses.
            if (projection.Returns.Contains(identity))
                operation["ret"] ??= operation["sty"]?.DeepClone() ?? operation["dynRet"]?.DeepClone();
        }
        foreach (var root in files) Calls(root);
        return projection;
    }
}
