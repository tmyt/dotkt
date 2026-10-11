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
                && constraints.Any(bound => HasConstructedDependency(TypeJson.Read(bound), "method", index,
                    ReadMethodFrame(method)))).Any(found => found);

    internal static NullableRepresentationFrame ReadMethodFrame(JsonObject method) =>
        method[NullableRepresentationTypes.MethodFrameKey] is JsonValue encoded
            ? NullableRepresentationFrame.Read(JsonNode.Parse(encoded.GetValue<string>())) : null;

    static bool SameSourceParameter(int first, int second, NullableRepresentationFrame frame) =>
        frame == null ? first == second
            : frame.PhysicalSlot(first).SourceIndex == frame.PhysicalSlot(second).SourceIndex;

    // A self F-bound keeps the exact same generic argument. A constructed bound depending on another parameter
    // can instead be satisfied through Kotlin variance without satisfying that exact closed CLR construction.
    internal static bool HasConstructedDependency(TypeNode bound, string scope, int self,
        NullableRepresentationFrame frame = null)
    {
        if (bound is TypeNode.Tv) return false;
        // An invariant foreign construction is already an exact CLR contract. Unlike a Kotlin existential
        // carrier or a variant foreign interface, satisfying I<U> cannot mean implementing I<V> for another V.
        // Keep this proof: foreign constrained methods and delegate types require the same physical row.
        var nominal = bound;
        while (nominal is TypeNode.Nullable or TypeNode.Oblivious)
            nominal = nominal is TypeNode.Nullable nullable ? nullable.Of : ((TypeNode.Oblivious)nominal).Of;
        // A binding that discards the entire generic application has no physical argument dependency.
        // In particular, representation companions must not turn its source self-bound into a
        // dependent bound and discard the non-generic CLR constraint that remains after lowering.
        if (nominal is TypeNode.Fqn erased
            && BirTypeLowering.ErasesGenericApplicationToNonGenericClassifier(erased.Name))
            return false;
        if (nominal is TypeNode.Fqn named
            && References.ResolveForeignProjectionType(named.Name, named.Args) is { IsGenericTypeDefinition: true } foreign
            && foreign.GetGenericArguments().All(parameter =>
                (parameter.GenericParameterAttributes & System.Reflection.GenericParameterAttributes.VarianceMask) == 0)
            && named.Args?.Any(argument => argument is TypeNode.Projection or TypeNode.Star) != true)
            return false;
        var changed = false;
        MapVariables(bound, tv => {
            // Representation companions are views of the same source parameter, not
            // independent Kotlin variables that make a self-bound variant-dependent.
            if (tv.Scope == scope && !SameSourceParameter(tv.I, self, frame)) changed = true;
            return tv;
        });
        return changed;
    }

    internal static void SelfTest()
    {
        var bound = new TypeNode.Fqn("kotlin.Enum", new TypeNode[] {
            new TypeNode.Tv("method", 0), new TypeNode.Tv("method", 1),
        });
        if (HasConstructedDependency(bound, "method", 0)
            || HasConstructedDependency(new TypeNode.Nullable(bound), "method", 0))
            throw new InvalidOperationException("Non-generic physical bound acquired a companion dependency");
        if (!HasConstructedDependency(new TypeNode.Array(new TypeNode.Tv("method", 1)), "method", 0))
            throw new InvalidOperationException("Constructed bound lost its physical argument dependency");
        var frame = new NullableRepresentationFrame(2, new[] { 0, 1 }, new[] { 2, 1, 0, 3, 4, 5 },
            storageIndices: new[] { 0 }, nullableStorageIndices: new[] { 0 });
        foreach (var companion in new[] { 0, 4, 5 })
            if (HasConstructedDependency(new TypeNode.Array(new TypeNode.Tv("method", companion)), "method", 2, frame))
                throw new InvalidOperationException("Self-bound companion became an independent source parameter");
        foreach (var other in new[] { 1, 3 })
            if (!HasConstructedDependency(new TypeNode.Array(new TypeNode.Tv("method", other)), "method", 2, frame))
                throw new InvalidOperationException("Different source parameter lost its constructed dependency");
        var parameter = new JsonObject { ["name"] = "T", ["constraints"] = new JsonArray(TypeJson.Write(bound)) };
        var parameters = new JsonArray(parameter, JsonValue.Create("$storage0"));
        RewriteConstraints(new JsonObject(), new JsonObject(), parameters, _ => false, type => type);
        if (parameter["constraints"].AsArray().Count != 1
            || TypeJson.Read(parameter["constraints"][0]) != bound)
            throw new InvalidOperationException("Non-generic physical bound was removed from its method parameter");
        var captured = JsonNode.Parse("""
          {"name":"Capture","generated":true,"typeParams":["P","T"]}
          """).AsObject();
        var method = JsonNode.Parse("""
          {"typeParams":["T",{"name":"P","constraints":[]}],"body":[
            {"k":"newClosure","closureType":{"t":"fqn","name":"Capture"},"typeArgs":[
              {"t":"tv","scope":"method","i":1},{"t":"tv","scope":"method","i":0}]}]}
          """).AsObject();
        method["typeParams"][1][FBoundStarProjectionErasure.ErasedInnerConstraintKey] =
            new JsonArray(TypeJson.Write(new TypeNode.Array(new TypeNode.Tv("method", 0))));
        captured["typeParams"][0] = new JsonObject {
            ["name"] = "P", ["constraints"] = new JsonArray(TypeJson.Write(new TypeNode.Array(new TypeNode.Tv("type", 1)))),
        };
        var entry = (method, new JsonObject());
        Methods.Add(entry);
        try
        {
            PropagateCapturedBounds(new Dictionary<string, JsonObject> { ["Capture"] = captured },
                _ => false, type => type);
            if (TypeJson.Read(captured["typeParams"][0][FBoundStarProjectionErasure.ErasedInnerConstraintKey][0])
                    != new TypeNode.Array(new TypeNode.Tv("type", 1))
                || captured["typeParams"][0]["name"].GetValue<string>() != "P"
                || captured["typeParams"][0]["constraints"].AsArray().Count != 0)
                throw new InvalidOperationException("Named capture slot lost its declaration-owned dispatch bound");
            method["typeParams"][1].AsObject().Remove(FBoundStarProjectionErasure.ErasedInnerConstraintKey);
            method["typeParams"][1]["constraints"] =
                new JsonArray(TypeJson.Write(new TypeNode.Tv("method", 0)));
            captured["typeParams"][0] = new JsonObject {
                ["name"] = "P", ["constraints"] = new JsonArray(TypeJson.Write(new TypeNode.Tv("type", 1))),
            };
            captured.Remove(DispatchBoundsKey);
            PropagateCapturedBounds(new Dictionary<string, JsonObject> { ["Capture"] = captured },
                _ => false, type => type);
            if (TypeJson.Read(captured["typeParams"][0]["constraints"][0]) != new TypeNode.Tv("type", 1))
                throw new InvalidOperationException("Named capture slot lost its retained physical bound");
        }
        finally { Methods.Remove(entry); }
        var shared = new JsonObject {
            ["name"] = "Shared", ["generated"] = true, ["typeParams"] = new JsonArray("T"),
        };
        var constrainedCaller = new JsonObject {
            ["typeParams"] = new JsonArray(new JsonObject {
                ["name"] = "T", ["constraints"] = new JsonArray(TypeJson.Fqn("kotlin.Enum"),
                    TypeJson.Write(new TypeNode.Tv("method", 1))),
            }, JsonValue.Create("N")),
            ["body"] = new JsonArray(new JsonObject {
                ["k"] = "new", ["type"] = TypeJson.Write(new TypeNode.Fqn("Shared",
                    new TypeNode[] { new TypeNode.Tv("method", 0) })),
            }),
        };
        var unrelated = (constrainedCaller, new JsonObject());
        Methods.Add(unrelated);
        try
        {
            foreach (var carriesUncapturedVariable in new[] { true, false })
            {
                if (!carriesUncapturedVariable) constrainedCaller["typeParams"][0]["constraints"].AsArray().RemoveAt(1);
                PropagateCapturedBounds(new Dictionary<string, JsonObject> { ["Shared"] = shared },
                    _ => false, type => type);
                if (shared["typeParams"][0] is not JsonValue)
                    throw new InvalidOperationException("Caller strengthened an unrelated shared generated declaration");
            }
        }
        finally { Methods.Remove(unrelated); }
        Console.WriteLine("[method bound dependencies] self-test OK (non-generic binding, storage companion, retained constraint, named capture)");
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

    static TypeNode ProjectMethodVariables(TypeNode bound, int self, NullableRepresentationFrame frame) =>
        MapVariables(bound, tv => tv.Scope == "method" && !SameSourceParameter(tv.I, self, frame)
            ? new TypeNode.Star() : tv);

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
                    {
                        var closedBound = SupertypeGraph.SubstOwnerTvs(bound, ownerArgs);
                        var frame = ReadMethodFrame(implementation);
                        var positions = frame == null ? new[] { index } : Enumerable.Range(0, frame.PhysicalArity)
                            .Where(position => position == index
                                || frame.PhysicalSlot(position).SourceIndex == frame.PhysicalSlot(index).SourceIndex
                                    && implementation["typeParams"][position] is JsonObject companion
                                    && companion["constraints"] is JsonArray companionBounds
                                    && companionBounds.Select(TypeJson.Read).Contains(closedBound));
                        foreach (var position in positions)
                            rows.Add(new JsonObject {
                                ["index"] = position,
                                ["bound"] = TypeJson.Write(closedBound),
                                ["template"] = constraint.DeepClone(),
                                ["consequences"] = new JsonArray(IndependentBounds(bound, ownerParameters)
                                    .Select(TypeJson.Write).ToArray()),
                                ["ownerArgs"] = new JsonArray(ownerArgs.Select(TypeJson.Write).ToArray())
                            });
                    }
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
        var pending = new Queue<(JsonObject Method, JsonObject Owner, JsonArray Parameters, JsonArray OriginalParameters)>();
        foreach (var (method, owner) in Methods)
        {
            var parameters = (method["typeParams"] as JsonArray)?.DeepClone().AsArray() ?? new JsonArray();
            var original = (JsonArray)parameters.DeepClone();
            RewriteConstraints(method, owner, parameters, dependsOnOwner, projectedBound);
            pending.Enqueue((method, owner, parameters, original));
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
                                || sourceParameters[sourceTv.I] is not JsonObject source) continue;
                            var sourceConstraints = source["constraints"] as JsonArray ?? new JsonArray();
                            var erased = source[FBoundStarProjectionErasure.ErasedInnerConstraintKey] as JsonArray;
                            if (sourceConstraints.Count == 0 && erased == null) continue;
                            var originalParameters = sourceTv.Scope == "method" ? context.OriginalParameters
                                : context.Owner["typeParams"] as JsonArray;
                            var originalConstraints = originalParameters?[sourceTv.I] is JsonObject originalParameter
                                ? new JsonArray((originalParameter["constraints"] as JsonArray ?? new JsonArray())
                                    .Concat(originalParameter[FBoundStarProjectionErasure.ErasedInnerConstraintKey]
                                        as JsonArray ?? new JsonArray()).Select(bound => bound.DeepClone()).ToArray())
                                : new JsonArray();
                            var declaredConstraints = targetParameters[i] is JsonObject targetParameter
                                ? targetParameter["constraints"] as JsonArray ?? new JsonArray() : new JsonArray();
                            // A capture map rebinds an existing declaration obligation; it does not
                            // authorize strengthening a shared inline class from one use site's bounds.
                            var complete = true;
                            foreach (var bound in originalConstraints)
                                MapVariables(TypeJson.Read(bound), tv => {
                                    if (!positions.ContainsKey((tv.Scope, tv.I))) complete = false;
                                    return tv;
                                });
                            if (!complete || !Rebind(originalConstraints).Select(TypeJson.Read).ToHashSet()
                                .SetEquals(declaredConstraints.Select(TypeJson.Read))) continue;
                            var parameter = targetParameters[i] as JsonObject;
                            if (parameter == null)
                                targetParameters[i] = parameter = new JsonObject {
                                    ["name"] = targetParameters[i]?.DeepClone(),
                                };
                            var constraints = Rebind(sourceConstraints);
                            var dispatch = Rebind(erased ?? new JsonArray());
                            if (JsonNode.DeepEquals(parameter["constraints"], constraints)
                                && (erased == null && parameter[FBoundStarProjectionErasure.ErasedInnerConstraintKey] == null
                                    || JsonNode.DeepEquals(parameter[FBoundStarProjectionErasure.ErasedInnerConstraintKey], dispatch))) continue;
                            // Preserve the declaration row before replacing its physical obligation, even when
                            // an override substituted a concrete bound and no owner TV remains in that row.
                            KotlinSupertypesRecord.Merge(target, new JsonObject { ["bounds"] = new JsonObject {
                                [i.ToString()] = parameter["constraints"]?.DeepClone() ?? new JsonArray() } });
                            parameter["constraints"] = constraints;
                            if (erased != null)
                                parameter[FBoundStarProjectionErasure.ErasedInnerConstraintKey] = dispatch;
                            else parameter.Remove(FBoundStarProjectionErasure.ErasedInnerConstraintKey);
                            var plans = target[DispatchBoundsKey] is JsonValue encoded
                                ? JsonNode.Parse(encoded.GetValue<string>()).AsObject() : new JsonObject();
                            if (erased != null) plans[i.ToString()] = dispatch.DeepClone();
                            else plans.Remove(i.ToString());
                            target[DispatchBoundsKey] = plans.ToJsonString();
                            if (!changed.TryGetValue(constructed.Name, out var slots))
                                changed[constructed.Name] = slots = new HashSet<int>();
                            slots.Add(i);
                            targetChanged = true;
                        }
                        if (targetChanged && target["methods"] is JsonArray methods)
                            foreach (var method in methods.OfType<JsonObject>())
                            {
                                var parameters = method["typeParams"] as JsonArray ?? new JsonArray();
                                pending.Enqueue((method, target, parameters, (JsonArray)parameters.DeepClone()));
                            }
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
            CloseOwnerViews(method, owner, roots, dependsOnOwner, projectedBound);
    }

    internal static void CloseOwnerViews(JsonObject method, JsonObject owner, IEnumerable<JsonNode> roots,
        Func<TypeNode, bool> dependsOnOwner, Func<TypeNode, TypeNode> projectedBound)
    {
        var parameters = (method["typeParams"] as JsonArray)?.DeepClone().AsArray() ?? new JsonArray();
        RewriteConstraints(method, owner, parameters, dependsOnOwner, projectedBound);
        ConstrainedTypeParameterReceiverBinding.CloseMethodOwners(method, owner, roots, parameters, References);
    }

    internal static void Apply(Func<TypeNode, bool> dependsOnOwner, Func<TypeNode, TypeNode> projectedBound)
    {
        foreach (var (method, owner) in Methods)
        {
            RewriteConstraints(method, owner, method["typeParams"].AsArray(), dependsOnOwner, projectedBound);
            method.Remove(OverrideBoundsKey);
            PreserveDispatchBounds(method, method["typeParams"].AsArray());
        }
    }

    internal static void PreserveDispatchBounds(JsonObject declaration, JsonArray parameters)
    {
        var bounds = new JsonObject();
        for (var index = 0; index < parameters.Count; index++)
            if (parameters[index] is JsonObject parameter
                && parameter[FBoundStarProjectionErasure.ErasedInnerConstraintKey] is JsonArray removed)
                bounds[index.ToString()] = removed.DeepClone();
        // These are dispatch plans, not value/storage types. Preserve their projection masks across ordinary
        // type lowering until the constrained-receiver pass consumes the exact planned bound.
        declaration[DispatchBoundsKey] = bounds.ToJsonString();
    }

    static void RewriteConstraints(JsonObject method, JsonObject owner, JsonArray parameters, Func<TypeNode, bool> dependsOnOwner,
        Func<TypeNode, TypeNode> projectedBound)
    {
        var frame = ReadMethodFrame(method);
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
                else if (dependsOnOwner(bound) || HasConstructedDependency(bound, "method", index, frame))
                {
                    removed.Add(TypeJson.Write(projectedBound(ProjectMethodVariables(bound, index, frame))));
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
