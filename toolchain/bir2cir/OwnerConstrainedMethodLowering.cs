using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Kotlin owner-dependent bounds describe the source view, not the receiver's exact CLR construction.
// Preserve the complete Kotlin constraint list in metadata and retain only representable physical rows.
// Calls through the existential slot then use ordinary typed forwarding, without reflective generic binding.
static class OwnerConstrainedMethodLowering
{
    internal const string DispatchBoundsKey = "_ownerConstraintDispatchBounds";
    internal const string OverrideBoundsKey = "_ownerConstraintOverrideBounds";
    static readonly List<(JsonObject Method, JsonObject Owner)> Methods = new();
    internal static void Reset() => Methods.Clear();

    // Called only after the Kotlin override resolver has selected the exact source slot. In particular,
    // substituting Base<T> with Base<Animal> must not hide that its method bound originally depended on T.
    internal static void RecordOverride(JsonObject implementation, JsonArray slotParameters, TypeNode[] ownerArgs,
        JsonArray ownerParameters)
    {
        if (slotParameters == null) return;
        var rows = implementation[OverrideBoundsKey] is JsonValue encoded
            ? JsonNode.Parse(encoded.GetValue<string>()).AsArray() : new JsonArray();
        for (var index = 0; index < slotParameters.Count; index++)
            if (slotParameters[index] is JsonObject parameter && parameter["constraints"] is JsonArray constraints)
                foreach (var constraint in constraints)
                    if (TypeJson.Read(constraint) is TypeNode bound
                        && FBoundStarProjectionErasure.ContainsOwnerTv(bound))
                        rows.Add(new JsonObject {
                            ["index"] = index,
                            ["bound"] = TypeJson.Write(SupertypeGraph.SubstOwnerTvs(bound, ownerArgs)),
                            ["template"] = constraint.DeepClone(),
                            ["consequences"] = new JsonArray(IndependentBounds(bound, ownerParameters)
                                .Select(TypeJson.Write).ToArray()),
                            ["ownerArgs"] = new JsonArray(ownerArgs.Select(TypeJson.Write).ToArray())
                        });
        if (rows.Count > 0) implementation[OverrideBoundsKey] = rows.ToJsonString();
    }

    internal static void Record(JsonObject method, JsonObject owner)
    {
        var parameters = method["typeParams"].AsArray();
        var payload = method[NullableGenericErasure.MethodTypeParameterBoundsPre] is JsonValue encoded
            ? JsonNode.Parse(encoded.GetValue<string>()).AsObject() : new JsonObject();
        var bounds = payload["bounds"] as JsonObject ?? new JsonObject();
        if (payload["bounds"] == null) payload["bounds"] = bounds;
        for (var index = 0; index < parameters.Count; index++)
            if (parameters[index] is JsonObject parameter && parameter["constraints"] is JsonArray constraints
                && bounds[index.ToString()] == null)
                bounds[index.ToString()] = constraints.DeepClone();
        method[NullableGenericErasure.MethodTypeParameterBoundsPre] = payload.ToJsonString();
        Methods.Add((method, owner));
    }

    internal static void CloseOwners(IEnumerable<JsonNode> roots, Func<TypeNode, bool> dependsOnOwner,
        Func<TypeNode, TypeNode> projectedBound)
    {
        foreach (var (method, owner) in Methods)
        {
            var parameters = method["typeParams"].DeepClone().AsArray();
            RewriteConstraints(method, owner, parameters, dependsOnOwner, projectedBound);
            ConstrainedTypeParameterReceiverBinding.CloseMethodOwners(method, owner, roots, parameters);
        }
    }

    internal static void Apply(Func<TypeNode, bool> dependsOnOwner, Func<TypeNode, TypeNode> projectedBound)
    {
        foreach (var (method, owner) in Methods)
        {
            RewriteConstraints(method, owner, method["typeParams"].AsArray(), dependsOnOwner, projectedBound);
            method.Remove(OverrideBoundsKey);
            var parameters = method["typeParams"].AsArray();
            var bounds = new JsonObject();
            for (var index = 0; index < parameters.Count; index++)
                if (parameters[index] is JsonObject parameter
                    && parameter[FBoundStarProjectionErasure.ErasedInnerConstraintKey] is JsonArray removed)
                    bounds[index.ToString()] = removed.DeepClone();
            // These are dispatch plans, not value/storage types. Preserve their projection masks across ordinary
            // type lowering until the constrained-receiver pass consumes the exact planned bound.
            method[DispatchBoundsKey] = bounds.ToJsonString();
        }
    }

    static void RewriteConstraints(JsonObject method, JsonObject owner, JsonArray parameters, Func<TypeNode, bool> dependsOnOwner,
        Func<TypeNode, TypeNode> projectedBound)
    {
        var inherited = method[OverrideBoundsKey] is JsonValue encoded
            ? JsonNode.Parse(encoded.GetValue<string>()).AsArray() : new JsonArray();
        for (var index = 0; index < parameters.Count; index++)
        {
            if (parameters[index] is not JsonObject parameter) continue;
            if (parameter["constraints"] is not JsonArray constraints) continue;
            var retained = new JsonArray();
            var removed = new JsonArray();
            foreach (var constraint in constraints)
            {
                var bound = TypeJson.Read(constraint);
                var slot = inherited.OfType<JsonObject>().FirstOrDefault(row =>
                    row["index"].GetValue<int>() == index && TypeJson.Read(row["bound"]).Equals(bound));
                if (slot != null)
                {
                    removed.Add(TypeJson.Write(SupertypeGraph.SubstOwnerTvs(
                        projectedBound(TypeJson.Read(slot["template"])),
                        slot["ownerArgs"].AsArray().Select(TypeJson.Read).ToArray())));
                    foreach (var consequence in slot["consequences"].AsArray())
                        if (!retained.Any(row => JsonNode.DeepEquals(row, consequence)))
                            retained.Add(consequence.DeepClone());
                }
                else if (dependsOnOwner(bound))
                {
                    removed.Add(TypeJson.Write(projectedBound(bound)));
                    foreach (var consequence in IndependentBounds(bound, owner["typeParams"] as JsonArray))
                        if (!retained.Any(row => TypeJson.Read(row).Equals(consequence)))
                            retained.Add(TypeJson.Write(consequence));
                }
                else retained.Add(constraint.DeepClone());
            }
            parameter["constraints"] = retained;
            if (removed.Count > 0)
                parameter[FBoundStarProjectionErasure.ErasedInnerConstraintKey] = removed;
        }
    }

    internal static IEnumerable<TypeNode> IndependentBounds(TypeNode bound, JsonArray ownerParameters)
    {
        var visited = new HashSet<int>();
        IEnumerable<TypeNode> Visit(TypeNode current)
        {
            if (current is TypeNode.Tv { Scope: "type" } tv)
            {
                if (!visited.Add(tv.I) || ownerParameters == null || tv.I < 0 || tv.I >= ownerParameters.Count)
                    yield break;
                if (ownerParameters[tv.I] is JsonObject parameter && parameter["constraints"] is JsonArray constraints)
                    foreach (var constraint in constraints)
                        foreach (var result in Visit(TypeJson.Read(constraint))) yield return result;
            }
            else if (!FBoundStarProjectionErasure.ContainsOwnerTv(current)) yield return current;
        }
        return Visit(bound).Distinct();
    }
}
