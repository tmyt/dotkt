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
    static ReferenceMetadataIndex References;
    internal static void Reset(ReferenceMetadataIndex references)
    {
        Methods.Clear();
        References = references;
    }

    internal static bool HasMethodDependentBounds(JsonObject method) =>
        method["typeParams"] is JsonArray parameters && parameters.Select((parameter, index) =>
            parameter is JsonObject declaration && declaration["constraints"] is JsonArray constraints
                && constraints.Any(bound => HasConstructedDependency(TypeJson.Read(bound), "method", index))).Any(found => found);

    // A self F-bound keeps the exact same generic argument. A constructed bound depending on another parameter
    // can instead be satisfied through Kotlin variance without satisfying that exact closed CLR construction.
    internal static bool HasConstructedDependency(TypeNode bound, string scope, int self)
    {
        if (bound is TypeNode.Tv) return false;
        // An invariant foreign construction is already an exact CLR contract. Unlike a Kotlin existential
        // carrier or a variant foreign interface, satisfying I<U> cannot mean implementing I<V> for another V.
        // Keep this proof: foreign constrained methods and delegate types require the same physical row.
        var nominal = bound;
        while (nominal is TypeNode.Nullable or TypeNode.Oblivious)
            nominal = nominal is TypeNode.Nullable nullable ? nullable.Of : ((TypeNode.Oblivious)nominal).Of;
        if (nominal is TypeNode.Fqn named
            && References.ResolveForeignProjectionType(named.Name, named.Args) is { IsGenericTypeDefinition: true } foreign
            && foreign.GetGenericArguments().All(parameter =>
                (parameter.GenericParameterAttributes & System.Reflection.GenericParameterAttributes.VarianceMask) == 0)
            && named.Args?.Any(argument => argument is TypeNode.Projection or TypeNode.Star) != true)
            return false;
        var changed = false;
        MapVariables(bound, tv => {
            if (tv.Scope == scope && tv.I != self) changed = true;
            return tv;
        });
        return changed;
    }

    static TypeNode MapVariables(TypeNode type, Func<TypeNode.Tv, TypeNode> map) => type switch
    {
        TypeNode.Tv tv => map(tv),
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args?.Select(arg => MapVariables(arg, map)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(MapVariables(n.Of, map)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(MapVariables(o.Of, map)),
        TypeNode.Projection p => new TypeNode.Projection(p.Variance, MapVariables(p.Of, map)),
        TypeNode.Array a => new TypeNode.Array(MapVariables(a.Elem, map), a.Rank, a.SzArray),
        TypeNode.ByRef b => new TypeNode.ByRef(MapVariables(b.Of, map)),
        TypeNode.Ptr p => new TypeNode.Ptr(MapVariables(p.Of, map)),
        TypeNode.Mod m => new TypeNode.Mod(m.Req, MapVariables(m.M, map), MapVariables(m.Of, map)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, MapVariables(fn.Ret, map),
            fn.Params.Select(arg => MapVariables(arg, map)).ToArray(),
            fn.Recv == null ? null : MapVariables(fn.Recv, map), fn.Clr,
            fn.Ctx?.Select(arg => MapVariables(arg, map)).ToArray()),
        _ => type,
    };

    static TypeNode ProjectMethodVariables(TypeNode bound, int self) =>
        MapVariables(bound, tv => tv.Scope == "method" && tv.I != self ? new TypeNode.Star() : tv);

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

    internal static Dictionary<string, HashSet<int>> PropagateCapturedBounds(
        IReadOnlyDictionary<string, JsonObject> definitions, Func<TypeNode, bool> dependsOnOwner,
        Func<TypeNode, TypeNode> projectedBound)
    {
        var changed = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var pending = new Queue<(JsonObject Method, JsonObject Owner, JsonArray Parameters)>();
        foreach (var (method, owner) in Methods)
        {
            var parameters = method["typeParams"].DeepClone().AsArray();
            RewriteConstraints(method, owner, parameters, dependsOnOwner, projectedBound);
            pending.Enqueue((method, owner, parameters));
        }
        while (pending.TryDequeue(out var context))
        {
            void Walk(JsonNode node)
            {
                if (node is JsonArray array)
                {
                    foreach (var child in array) Walk(child);
                    return;
                }
                if (node is not JsonObject obj) return;
                var kind = obj["k"]?.GetValue<string>();
                var constructionType = kind switch {
                    "newClosure" => obj["closureType"],
                    "newSam" => obj["samType"],
                    "new" => obj["type"],
                    _ => null,
                };
                if (TypeJson.Read(constructionType) is TypeNode.Fqn constructed
                    && definitions.TryGetValue(constructed.Name, out var target)
                    && target["generated"]?.GetValue<bool>() == true
                    && target["typeParams"] is JsonArray targetParameters)
                {
                    var arguments = obj["typeArgs"] is JsonArray explicitArguments
                        ? explicitArguments.Select(TypeJson.Read).ToArray() : constructed.Args;
                    if (arguments != null && arguments.Length == targetParameters.Count)
                    {
                        var positions = new Dictionary<(string, int), int>();
                        for (var i = 0; i < arguments.Length; i++)
                            if (arguments[i] is TypeNode.Tv tv) positions.TryAdd((tv.Scope, tv.I), i);
                        JsonArray Rebind(JsonArray bounds) => new(bounds.Select(bound => TypeJson.Write(
                            MapVariables(TypeJson.Read(bound), tv => positions.TryGetValue((tv.Scope, tv.I), out var index)
                                ? new TypeNode.Tv("type", index) : tv))).ToArray());
                        var targetChanged = false;
                        for (var i = 0; i < arguments.Length; i++)
                        {
                            if (arguments[i] is not TypeNode.Tv sourceTv) continue;
                            var sourceParameters = sourceTv.Scope == "method" ? context.Parameters
                                : context.Owner["typeParams"] as JsonArray;
                            if (sourceParameters == null || sourceTv.I >= sourceParameters.Count
                                || sourceParameters[sourceTv.I] is not JsonObject source
                                || source[FBoundStarProjectionErasure.ErasedInnerConstraintKey] is not JsonArray erased
                                || targetParameters[i] is not JsonObject parameter) continue;
                            var constraints = Rebind(source["constraints"] as JsonArray ?? new JsonArray());
                            var dispatch = Rebind(erased);
                            if (JsonNode.DeepEquals(parameter["constraints"], constraints)
                                && JsonNode.DeepEquals(parameter[FBoundStarProjectionErasure.ErasedInnerConstraintKey], dispatch)) continue;
                            // The construction arguments are the explicit lexical-to-generated frame relation.
                            // Preserve the source row before replacing the copied physical obligation, even when
                            // an override substituted a concrete bound and no owner TV remains in that row.
                            KotlinSupertypesRecord.Merge(target, new JsonObject { ["bounds"] = new JsonObject {
                                [i.ToString()] = parameter["constraints"]?.DeepClone() ?? new JsonArray() } });
                            parameter["constraints"] = constraints;
                            parameter[FBoundStarProjectionErasure.ErasedInnerConstraintKey] = dispatch;
                            var plans = target[DispatchBoundsKey] is JsonValue encoded
                                ? JsonNode.Parse(encoded.GetValue<string>()).AsObject() : new JsonObject();
                            plans[i.ToString()] = dispatch.DeepClone();
                            target[DispatchBoundsKey] = plans.ToJsonString();
                            if (!changed.TryGetValue(constructed.Name, out var slots))
                                changed[constructed.Name] = slots = new HashSet<int>();
                            slots.Add(i);
                            targetChanged = true;
                        }
                        if (targetChanged && target["methods"] is JsonArray methods)
                            foreach (var method in methods.OfType<JsonObject>())
                                pending.Enqueue((method, target, method["typeParams"] as JsonArray ?? new JsonArray()));
                    }
                }
                foreach (var child in obj) if (child.Value != null) Walk(child.Value);
            }
            Walk(context.Method["body"]);
        }
        return changed;
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
                else if (dependsOnOwner(bound) || HasConstructedDependency(bound, "method", index))
                {
                    removed.Add(TypeJson.Write(projectedBound(ProjectMethodVariables(bound, index))));
                    foreach (var consequence in dependsOnOwner(bound)
                        ? IndependentBounds(bound, owner["typeParams"] as JsonArray) : Enumerable.Empty<TypeNode>())
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
