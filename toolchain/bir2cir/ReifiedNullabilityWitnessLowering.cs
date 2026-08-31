using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A CLR generic argument cannot distinguish `String` from `String?` (or `Int` from `Int?` after the Kotlin
// instantiation has selected the same open `!!T` body). Preserve that one lost Kotlin fact as an explicit Boolean ABI
// witness for each METHOD type parameter whose body transitively performs a nullable-sensitive operation. Kotlin
// `reified` is retained separately as declaration metadata; it never selects this physical ABI. This layer threads
// structurally-derived demand through exact declaration identities and explicit lifted-frame correspondences, while
// ilemit merely emits the resulting CIR expression.
static class ReifiedNullabilityWitnessLowering
{
    internal const string SemanticIndicesKey = "semanticReifiedIndices";
    internal const string WitnessIndicesKey = "nullableWitnessIndices";
    const string CallWitnessCountKey = "reifiedWitnessCount";
    const string PendingWitnessesKey = "reifiedPendingWitnesses";
    const string Prefix = "dotkt$reifiedNullability$";
    sealed record GeneratedTarget(JsonObject Method, string ClosureName, string Owner, int[] Indices);
    sealed record GeneratedType(JsonObject Type, int[] Indices);
    sealed record WitnessFrame(
        IReadOnlyDictionary<int, JsonNode> Method,
        IReadOnlyDictionary<int, JsonNode> Type);

    public static void Apply(
        JsonNode root,
        NullableWitnessDemand demand,
        ReferenceMetadataIndex refs)
    {
        if (root is not JsonObject file) return;
        var generatedFrames = demand.AnalyzeGeneratedFrames(file);
        var generatedTargets = CollectGeneratedTargets(file, generatedFrames.TargetDemands);
        var generatedTypes = CollectGeneratedTypes(file, generatedFrames.TypeDemands);

        void Owner(JsonObject owner)
        {
            var typeWitnesses = generatedTypes.TryGetValue(Str(owner["name"]) ?? "", out var typeIndices)
                && ReferenceEquals(owner, typeIndices.Type)
                ? MaterializeGeneratedType(owner, typeIndices.Indices)
                : null;
            if (owner["methods"] is JsonArray methods)
            {
                foreach (var method in methods.OfType<JsonObject>().ToList())
                    if (!generatedTargets.Values.Any(target => ReferenceEquals(target.Method, method)))
                        Method(method, typeWitnesses);
                foreach (var target in generatedTargets.Values
                    .Where(target => methods.Any(method => ReferenceEquals(method, target.Method))).ToList())
                    methods.Remove(target.Method);
            }
            if (owner["ctors"] is JsonArray ctors)
                foreach (var ctor in ctors.OfType<JsonObject>()) Walk(ctor, new WitnessFrame(null, typeWitnesses));
            if (owner["fields"] is JsonArray fields)
                foreach (var field in fields.OfType<JsonObject>()) Walk(field, new WitnessFrame(null, typeWitnesses));
            if (owner["properties"] is JsonArray properties)
                foreach (var property in properties.OfType<JsonObject>()) Walk(property, new WitnessFrame(null, typeWitnesses));
            if (owner["types"] is JsonArray types)
                foreach (var type in types.OfType<JsonObject>()) Owner(type);
        }

        void Method(JsonObject method, IReadOnlyDictionary<int, JsonNode> typeWitnesses)
        {
            var semanticIndices = ReifiedIndices(method);
            var declarationId = Str(method[DeclarationIdentityBinding.Key]);
            if (semanticIndices.Length != 0 && declarationId != null)
                method[SemanticIndicesKey] = IntArray(semanticIndices);
            var indices = declarationId != null
                && demand.LocalDeclarations.TryGetValue(declarationId, out var required)
                ? required
                : Array.Empty<int>();
            Dictionary<int, JsonNode> methodWitnesses = null;
            if (indices.Length != 0)
            {
                if (declarationId == null)
                    throw new InvalidOperationException(
                        $"bir2cir: generated nullable-sensitive method '{Str(method["name"])}' has no materializing construction");
                var parameters = method["params"] as JsonArray ?? new JsonArray();
                method["params"] = parameters;
                var usedNames = parameters.OfType<JsonObject>().Select(p => Str(p["name"]))
                    .Where(n => n != null).ToHashSet(StringComparer.Ordinal);
                methodWitnesses = new Dictionary<int, JsonNode>();
                foreach (var index in indices)
                {
                    var name = Prefix + index;
                    while (!usedNames.Add(name)) name += "$";
                    parameters.Add(new JsonObject {
                        ["name"] = name,
                        ["type"] = Fqn("kotlin.Boolean"),
                    });
                    methodWitnesses[index] = new JsonObject { ["k"] = "local", ["name"] = name };
                }
                method[WitnessIndicesKey] = IntArray(indices);
            }
            Walk(method["body"], new WitnessFrame(methodWitnesses, typeWitnesses));
        }

        void Walk(JsonNode node, WitnessFrame witnesses)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (Str(obj["k"]) == "newClosure"
                        && obj["synthClass"] is JsonObject synthClass
                        && obj["typeArgs"] is JsonArray closureTypeArgs
                        && demand.MaterializedFrameIndices(synthClass, closureTypeArgs, generatedFrames)
                            is { Length: > 0 } closureIndices)
                    {
                        MaterializeExistingClosure(obj, synthClass, closureIndices, witnesses, Walk);
                        return;
                    }
                    if (Str(obj["k"]) == "newSam"
                        && obj["synthClass"] is JsonObject samClass
                        && obj["typeArgs"] is JsonArray samTypeArgs
                        && demand.MaterializedFrameIndices(samClass, samTypeArgs, generatedFrames)
                            is { Length: > 0 } samIndices)
                    {
                        MaterializeReifiedSam(obj, samClass, samIndices, witnesses, Walk);
                        return;
                    }
                    if (Str(obj["k"]) == "newSuspendLambda"
                        && Str(obj["typeFrame"]) != "dense"
                        && obj["typeArgs"] is JsonArray suspendTypeArgs
                        && demand.MaterializedFrameIndices(
                            obj["body"] ?? new JsonArray(), suspendTypeArgs, generatedFrames,
                            dense: Str(obj["typeFrame"]) == "dense")
                            is { Length: > 0 } suspendIndices)
                    {
                        MaterializeReifiedSuspendLambda(obj, suspendIndices, witnesses, Walk);
                        return;
                    }
                    if (Str(obj["k"]) == "newSuspendLambda" && Str(obj["typeFrame"]) == "dense")
                    {
                        var denseTypeArgs = obj["typeArgs"] as JsonArray ?? new JsonArray();
                        var denseIndices = demand.MaterializedFrameIndices(
                            obj["body"] ?? new JsonArray(), denseTypeArgs, generatedFrames, dense: true);
                        MaterializeDenseSuspendFrameWitnesses(obj, denseIndices, witnesses, Walk);
                        return;
                    }
                    if (Str(obj["k"]) == "newDelegate"
                        && Str(obj["method"]) is string targetName
                        && generatedTargets.TryGetValue(targetName, out var target))
                    {
                        MaterializeReifiedDelegate(obj, target, witnesses, Walk);
                        return;
                    }
                    foreach (var child in obj.Select(kv => kv.Value).ToList())
                        if (child != null) Walk(child, witnesses);
                    if (Str(obj["k"]) == "isInst"
                        && TypeJson.Read(obj["type"]) is TypeNode.Tv { Scope: "method" } tv
                        && witnesses?.Method != null && witnesses.Method.TryGetValue(tv.I, out var witness))
                        obj["nullWitness"] = witness.DeepClone();
                    if (Str(obj["k"]) == "isInst"
                        && TypeJson.Read(obj["type"]) is TypeNode.Tv { Scope: "type" } typeTv
                        && witnesses?.Type != null && witnesses.Type.TryGetValue(typeTv.I, out var typeWitness))
                        obj["nullWitness"] = typeWitness.DeepClone();
                    if (Str(obj["k"]) is "callStatic" or "callInstance" or "constrainedCall"
                        && Str(obj[DeclarationIdentityBinding.Key]) is string targetId)
                        PrepareCallWitnesses(obj, targetId, witnesses, demand.LocalDeclarations, refs);
                    if (Str(obj["k"]) == "new"
                        && TypeJson.Read(obj["type"]) is TypeNode.Fqn constructed
                        && generatedTypes.TryGetValue(constructed.Name, out var generatedType))
                        PrepareWitnesses(obj,
                            constructed.Args?.Select(TypeJson.Write).ToArray() ?? Array.Empty<JsonNode>(),
                            generatedType.Indices, witnesses,
                            $"generated type '{constructed.Name}'");
                    break;
                case JsonArray array:
                    foreach (var child in array.ToList()) if (child != null) Walk(child, witnesses);
                    break;
            }
        }

        Owner(file);
        RefuseSurvivingGeneratedTargetReferences(file, generatedTargets.Keys);
        DropReifiedFacts(file);
    }

    static void RefuseSurvivingGeneratedTargetReferences(JsonNode root, IEnumerable<string> targetNames)
    {
        var targets = targetNames.ToHashSet(StringComparer.Ordinal);
        void Walk(JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (Str(obj["method"]) is string method && targets.Contains(method))
                        throw new InvalidOperationException(
                            $"bir2cir: generated reified method '{method}' survives outside its delegate construction");
                    foreach (var child in obj.Select(property => property.Value))
                        if (child != null) Walk(child);
                    break;
                case JsonArray array:
                    foreach (var child in array) if (child != null) Walk(child);
                    break;
            }
        }
        Walk(root);
    }

    static Dictionary<string, GeneratedTarget> CollectGeneratedTargets(
        JsonObject file,
        IReadOnlyDictionary<string, int[]> demands)
    {
        var result = new Dictionary<string, GeneratedTarget>(StringComparer.Ordinal);
        var referencedTargets = new HashSet<string>(StringComparer.Ordinal);
        var ordinal = 0;

        void References(JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (Str(obj["k"]) == "newDelegate" && Str(obj["method"]) is string method)
                        referencedTargets.Add(method);
                    foreach (var child in obj.Select(property => property.Value))
                        if (child != null) References(child);
                    break;
                case JsonArray array:
                    foreach (var child in array) if (child != null) References(child);
                    break;
            }
        }
        References(file);

        void Owner(JsonObject owner, string ownerName)
        {
            if (owner["methods"] is JsonArray methods)
                foreach (var method in methods.OfType<JsonObject>())
                {
                    if (!Bool(method["generated"])
                        || method[DeclarationIdentityBinding.Key] != null
                        || Str(method["name"]) is not string candidate
                        || !referencedTargets.Contains(candidate)
                        || !demands.TryGetValue(candidate, out var indices)
                        || indices.Length == 0)
                        continue;
                    var methodName = candidate;
                    var closureName = $"dotkt${Sanitize(ownerName)}$ReifiedClosure{ordinal++}";
                    if (!result.TryAdd(methodName, new GeneratedTarget(method, closureName, ownerName, indices)))
                        throw new InvalidOperationException(
                            $"bir2cir: ambiguous generated reified delegate target '{methodName}'");
                }
            if (owner["types"] is JsonArray types)
                foreach (var type in types.OfType<JsonObject>())
                    Owner(type, Str(type["name"]) ?? ownerName);
        }

        Owner(file, Str(file["fileClass"]) ?? "file");
        return result;
    }

    static Dictionary<string, GeneratedType> CollectGeneratedTypes(
        JsonObject file,
        IReadOnlyDictionary<string, int[]> demands)
    {
        var result = new Dictionary<string, GeneratedType>(StringComparer.Ordinal);
        void Owner(JsonObject owner)
        {
            if (owner["types"] is not JsonArray types) return;
            foreach (var type in types.OfType<JsonObject>())
            {
                if (Bool(type["generated"])
                    && Str(type["name"]) is string demandedName
                    && demands.TryGetValue(demandedName, out var indices)
                    && indices.Length != 0)
                {
                    var name = demandedName;
                    if (!result.TryAdd(name, new GeneratedType(type, indices)))
                        throw new InvalidOperationException($"bir2cir: ambiguous generated reified type '{name}'");
                }
                Owner(type);
            }
        }
        Owner(file);
        return result;
    }

    static void MaterializeReifiedDelegate(
        JsonObject node,
        GeneratedTarget target,
        WitnessFrame callerWitnesses,
        Action<JsonNode, WitnessFrame> walk)
    {
        var typeArgs = node["typeArgs"] as JsonArray
            ?? throw new InvalidOperationException(
                $"bir2cir: generated reified delegate '{Str(node["method"])}' has no type arguments");
        var fields = new JsonArray();
        var captures = new JsonArray();
        var closureWitnesses = new Dictionary<int, JsonNode>();
        foreach (var index in target.Indices)
        {
            if (index < 0 || index >= typeArgs.Count)
                throw new InvalidOperationException(
                    $"bir2cir: generated reified delegate '{Str(node["method"])}' has no type argument at index {index}");
            var name = Prefix + index;
            fields.Add(new JsonObject { ["name"] = name, ["type"] = Fqn("kotlin.Boolean") });
            captures.Add(WitnessFor(typeArgs[index], callerWitnesses));
            closureWitnesses[index] = new JsonObject {
                ["k"] = "field",
                ["ownerType"] = Fqn(target.ClosureName),
                ["recv"] = new JsonObject { ["k"] = "this" },
                ["name"] = name,
            };
        }

        var body = target.Method["body"]?.DeepClone() ?? new JsonArray();
        walk(body, new WitnessFrame(closureWitnesses, null));
        var synthClass = ClosureSynthesis.PrebindDenseMethodFrame(new JsonObject {
            ["name"] = target.ClosureName,
            ["fields"] = fields,
            ["params"] = target.Method["params"]?.DeepClone() ?? new JsonArray(),
            ["ret"] = target.Method["ret"]?.DeepClone(),
            ["body"] = body,
            ["typeParams"] = target.Method["typeParams"]?.DeepClone(),
            ["semanticOwner"] = target.Owner,
        });
        var replacement = new JsonObject {
            ["k"] = "newClosure",
            ["closureType"] = Fqn(target.ClosureName),
            ["captures"] = captures,
            ["method"] = "invoke",
            ["funcType"] = node["funcType"]?.DeepClone(),
            ["typeArgs"] = typeArgs.DeepClone(),
            ["synthClass"] = synthClass,
        };
        node.Clear();
        foreach (var property in replacement) node[property.Key] = property.Value?.DeepClone();
    }

    static void MaterializeExistingClosure(
        JsonObject node,
        JsonObject synthClass,
        int[] indices,
        WitnessFrame callerWitnesses,
        Action<JsonNode, WitnessFrame> walk)
    {
        var typeArgs = node["typeArgs"] as JsonArray
            ?? throw new InvalidOperationException("bir2cir: reified closure has no type arguments");
        var closureName = TypeJson.OwnerName(node["closureType"])
            ?? throw new InvalidOperationException("bir2cir: reified closure has no closure type");
        var fields = synthClass["fields"] as JsonArray ?? new JsonArray();
        synthClass["fields"] = fields;
        var captures = node["captures"] as JsonArray ?? new JsonArray();
        node["captures"] = captures;
        foreach (var capture in captures.ToList())
            if (capture != null) walk(capture, callerWitnesses);

        var closureMethodWitnesses = new Dictionary<int, JsonNode>();
        var closureTypeWitnesses = new Dictionary<int, JsonNode>();
        var usedNames = fields.OfType<JsonObject>().Select(field => Str(field["name"]))
            .Where(name => name != null).ToHashSet(StringComparer.Ordinal);
        foreach (var index in indices)
        {
            if (index < 0 || index >= typeArgs.Count)
                throw new InvalidOperationException($"bir2cir: reified closure has no type argument at index {index}");
            var name = Prefix + index;
            while (!usedNames.Add(name)) name += "$";
            fields.Add(new JsonObject { ["name"] = name, ["type"] = Fqn("kotlin.Boolean") });
            captures.Add(WitnessFor(typeArgs[index], callerWitnesses));
            var field = new JsonObject {
                ["k"] = "field",
                ["ownerType"] = Fqn(closureName),
                ["recv"] = new JsonObject { ["k"] = "this" },
                ["name"] = name,
            };
            BindCorrespondingWitness(typeArgs[index], field, closureMethodWitnesses, closureTypeWitnesses);
        }
        if (synthClass["body"] is JsonNode body)
            walk(body, new WitnessFrame(closureMethodWitnesses, closureTypeWitnesses));
    }

    static void MaterializeReifiedSam(
        JsonObject node,
        JsonObject synthClass,
        int[] indices,
        WitnessFrame callerWitnesses,
        Action<JsonNode, WitnessFrame> walk)
    {
        var typeArgs = node["typeArgs"] as JsonArray
            ?? throw new InvalidOperationException("bir2cir: reified SAM has no type arguments");
        var className = Str(synthClass["name"])
            ?? throw new InvalidOperationException("bir2cir: reified SAM has no synthesized class name");
        var fields = synthClass["fields"] as JsonArray ?? new JsonArray();
        synthClass["fields"] = fields;
        var captures = node["captures"] as JsonArray ?? new JsonArray();
        node["captures"] = captures;
        foreach (var capture in captures.ToList())
            if (capture != null) walk(capture, callerWitnesses);

        var methodWitnesses = new Dictionary<int, JsonNode>();
        var typeWitnesses = new Dictionary<int, JsonNode>();
        var usedNames = fields.OfType<JsonObject>().Select(field => Str(field["name"]))
            .Where(name => name != null).ToHashSet(StringComparer.Ordinal);
        foreach (var index in indices)
        {
            if (index < 0 || index >= typeArgs.Count)
                throw new InvalidOperationException($"bir2cir: reified SAM has no type argument at index {index}");
            var name = Prefix + index;
            while (!usedNames.Add(name)) name += "$";
            fields.Add(new JsonObject { ["name"] = name, ["type"] = Fqn("kotlin.Boolean") });
            captures.Add(WitnessFor(typeArgs[index], callerWitnesses));
            AppendCapturedFieldToConstructors(synthClass, className, name);
            var field = new JsonObject {
                ["k"] = "field", ["ownerType"] = Fqn(className),
                ["recv"] = new JsonObject { ["k"] = "this" }, ["name"] = name,
            };
            BindCorrespondingWitness(typeArgs[index], field, methodWitnesses, typeWitnesses);
        }
        if (synthClass["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>())
                if (method["body"] is JsonNode body)
                    walk(body, new WitnessFrame(methodWitnesses, typeWitnesses));
    }

    static void MaterializeReifiedSuspendLambda(
        JsonObject node,
        int[] indices,
        WitnessFrame callerWitnesses,
        Action<JsonNode, WitnessFrame> walk)
    {
        var typeArgs = node["typeArgs"] as JsonArray
            ?? throw new InvalidOperationException("bir2cir: reified suspend lambda has no type arguments");
        var captures = node["captures"] as JsonArray ?? new JsonArray();
        node["captures"] = captures;
        var capValues = node["capValues"] as JsonArray ?? new JsonArray();
        node["capValues"] = capValues;
        foreach (var value in capValues.ToList())
            if (value != null) walk(value, callerWitnesses);
        while (capValues.Count < captures.Count) capValues.Add(null);

        var methodWitnesses = new Dictionary<int, JsonNode>();
        var typeWitnesses = new Dictionary<int, JsonNode>();
        var usedNames = captures.OfType<JsonObject>().Select(capture => Str(capture["name"]))
            .Where(name => name != null).ToHashSet(StringComparer.Ordinal);
        foreach (var index in indices)
        {
            if (index < 0 || index >= typeArgs.Count)
                throw new InvalidOperationException(
                    $"bir2cir: reified suspend lambda has no type argument at index {index}");
            var name = Prefix + index;
            while (!usedNames.Add(name)) name += "$";
            captures.Add(new JsonObject { ["name"] = name, ["type"] = Fqn("kotlin.Boolean") });
            capValues.Add(WitnessFor(typeArgs[index], callerWitnesses));
            var local = new JsonObject { ["k"] = "local", ["name"] = name };
            BindCorrespondingWitness(typeArgs[index], local, methodWitnesses, typeWitnesses);
        }
        if (node["body"] is JsonNode body)
            walk(body, new WitnessFrame(methodWitnesses, typeWitnesses));
    }

    // InlineSplice gives every materialized suspend carrier an explicit dense generic frame: body (scope, i) is bound
    // by typeArgs[i] in the enclosing frame, preserving whether the slot belongs to method or type scope. Reified
    // witnesses are values too, so a witness belonging to such a type argument must cross each synthesized frame through
    // the ordinary positional capture/capValue contract. Passing the caller WitnessFrame straight into the body conflates
    // distinct generic index spaces and leaves the caller's local dangling once the carrier becomes a state-machine method.
    static void MaterializeDenseSuspendFrameWitnesses(
        JsonObject node,
        IReadOnlyCollection<int> demandedIndices,
        WitnessFrame callerWitnesses,
        Action<JsonNode, WitnessFrame> walk)
    {
        var typeParameters = node["typeParams"] as JsonArray ?? new JsonArray();
        var typeArguments = node["typeArgs"] as JsonArray;
        if (typeArguments == null && typeParameters.Count == 0)
        {
            if (node["capValues"] is JsonArray nongenericValues)
                foreach (var value in nongenericValues.ToList())
                    if (value != null) walk(value, callerWitnesses);
            if (node["body"] is JsonNode nongenericBody)
                walk(nongenericBody, new WitnessFrame(null, null));
            return;
        }
        if (typeArguments == null)
            throw new InvalidOperationException("bir2cir: generic dense suspend frame has no type arguments");
        if (typeParameters.Count != typeArguments.Count)
            throw new InvalidOperationException("bir2cir: dense suspend frame has inconsistent type parameter bindings");

        var captures = node["captures"] as JsonArray ?? new JsonArray();
        node["captures"] = captures;
        var capValues = node["capValues"] as JsonArray ?? new JsonArray();
        node["capValues"] = capValues;
        foreach (var value in capValues.ToList())
            if (value != null) walk(value, callerWitnesses);
        while (capValues.Count < captures.Count) capValues.Add(null);

        var methodWitnesses = new Dictionary<int, JsonNode>();
        var typeWitnesses = new Dictionary<int, JsonNode>();
        var usedNames = captures.OfType<JsonObject>().Select(capture => Str(capture["name"]))
            .Where(name => name != null).ToHashSet(StringComparer.Ordinal);
        foreach (var index in demandedIndices)
        {
            if (index < 0 || index >= typeArguments.Count)
                throw new InvalidOperationException(
                    $"bir2cir: dense suspend frame has no demanded type argument at index {index}");
            if (!TryWitnessForExisting(typeArguments[index], callerWitnesses, out var value)) continue;
            var name = Prefix + index;
            while (!usedNames.Add(name)) name += "$";
            captures.Add(new JsonObject { ["name"] = name, ["type"] = Fqn("kotlin.Boolean") });
            capValues.Add(value);
            var destination = TypeJson.Read(typeArguments[index]) is TypeNode.Tv { Scope: "type" }
                ? typeWitnesses : methodWitnesses;
            destination[index] = new JsonObject { ["k"] = "local", ["name"] = name };
        }

        if (node["body"] is JsonNode body)
            walk(body, new WitnessFrame(methodWitnesses, typeWitnesses));
    }

    static IReadOnlyDictionary<int, JsonNode> MaterializeGeneratedType(JsonObject type, int[] indices)
    {
        var className = Str(type["name"])
            ?? throw new InvalidOperationException("bir2cir: generated reified type has no name");
        var fields = type["fields"] as JsonArray ?? new JsonArray();
        type["fields"] = fields;
        var witnesses = new Dictionary<int, JsonNode>();
        var usedNames = fields.OfType<JsonObject>().Select(field => Str(field["name"]))
            .Where(name => name != null).ToHashSet(StringComparer.Ordinal);
        foreach (var index in indices)
        {
            var name = Prefix + index;
            while (!usedNames.Add(name)) name += "$";
            fields.Add(new JsonObject { ["name"] = name, ["type"] = Fqn("kotlin.Boolean") });
            AppendCapturedFieldToConstructors(type, className, name);
            witnesses[index] = new JsonObject {
                ["k"] = "field", ["ownerType"] = Fqn(className),
                ["recv"] = new JsonObject { ["k"] = "this" }, ["name"] = name,
            };
        }
        return witnesses;
    }

    static void AppendCapturedFieldToConstructors(JsonObject type, string className, string name)
    {
        if (type["ctors"] is not JsonArray ctors || ctors.Count == 0)
            throw new InvalidOperationException($"bir2cir: reified generated type '{className}' has no constructor");
        foreach (var ctor in ctors.OfType<JsonObject>())
        {
            var parameters = ctor["params"] as JsonArray ?? new JsonArray();
            ctor["params"] = parameters;
            parameters.Add(new JsonObject { ["name"] = name, ["type"] = Fqn("kotlin.Boolean") });
            if (ctor["thisArgs"] is JsonArray thisArgs)
            {
                thisArgs.Add(new JsonObject { ["k"] = "local", ["name"] = name });
                var delegationSig = ctor["delegationSig"] as JsonArray ?? new JsonArray();
                ctor["delegationSig"] = delegationSig;
                delegationSig.Add(Fqn("kotlin.Boolean"));
                continue;
            }
            var body = ctor["body"] as JsonArray ?? new JsonArray();
            ctor["body"] = body;
            body.Add(new JsonObject {
                ["k"] = "setField", ["ownerType"] = Fqn(className),
                ["recv"] = new JsonObject { ["k"] = "this" }, ["name"] = name,
                ["value"] = new JsonObject { ["k"] = "local", ["name"] = name },
            });
        }
    }

    static void BindCorrespondingWitness(
        JsonNode typeArgument,
        JsonNode witness,
        IDictionary<int, JsonNode> methodWitnesses,
        IDictionary<int, JsonNode> typeWitnesses)
    {
        if (TypeJson.Read(typeArgument) is not TypeNode.Tv tv) return;
        var destination = tv.Scope == "method" ? methodWitnesses
            : tv.Scope == "type" ? typeWitnesses : null;
        if (destination == null) return;
        if (destination.TryGetValue(tv.I, out var existing)
            && existing.ToJsonString() != witness.ToJsonString())
            throw new InvalidOperationException(
                $"bir2cir: conflicting reified witness captures for {tv.Scope} type parameter {tv.I}");
        destination[tv.I] = witness.DeepClone();
    }

    static string Sanitize(string value) => new(value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());

    static void PrepareCallWitnesses(
        JsonObject call,
        string declarationId,
        WitnessFrame callerWitnesses,
        IReadOnlyDictionary<string, int[]> localDeclarations,
        ReferenceMetadataIndex refs)
    {
        var indices = localDeclarations.TryGetValue(declarationId, out var local)
            ? local
            : refs.NullableWitnessTypeParameterIndices(declarationId);
        if (indices == null || indices.Length == 0) return;
        if (call["typeArgs"] is not JsonArray typeArgs)
            throw new InvalidOperationException(
                $"bir2cir: reified call '{declarationId}' has no type arguments");
        PrepareWitnesses(call, typeArgs.Select(argument => argument).ToArray(), indices, callerWitnesses,
            $"reified call '{declarationId}'");
    }

    static void PrepareWitnesses(
        JsonObject call,
        IReadOnlyList<JsonNode> typeArguments,
        int[] indices,
        WitnessFrame callerWitnesses,
        string context)
    {
        if (call[PendingWitnessesKey] != null)
            throw new InvalidOperationException($"bir2cir: {context} was assigned reified witnesses twice");
        var pending = new JsonArray();
        foreach (var index in indices)
        {
            if (index < 0 || index >= typeArguments.Count)
                throw new InvalidOperationException(
                    $"bir2cir: {context} has no type argument at index {index}");
            pending.Add(WitnessFor(typeArguments[index], callerWitnesses));
        }
        call[PendingWitnessesKey] = pending;
    }

    public static void MaterializeCallWitnesses(JsonNode root)
    {
        void Walk(JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var property in obj.ToList())
                        if (property.Key != PendingWitnessesKey && property.Value != null) Walk(property.Value);
                    if (obj[PendingWitnessesKey] is not JsonArray pending) return;
                    if (Str(obj["k"]) is not ("callStatic" or "callInstance" or "constrainedCall" or "new"))
                        throw new InvalidOperationException(
                            $"bir2cir: reified witness survived on non-call node '{Str(obj["k"])}'");
                    var args = obj["args"] as JsonArray
                        ?? throw new InvalidOperationException("bir2cir: reified call lost its argument vector");
                    foreach (var witness in pending)
                        args.Add(witness?.DeepClone());
                    obj.Remove(PendingWitnessesKey);
                    obj[CallWitnessCountKey] = pending.Count;
                    FinalizeVector(obj, "sig", args.Count, pending.Count);
                    FinalizeVector(obj, "argTypes", args.Count, pending.Count);
                    FinalizeVector(obj, "memberSignature", args.Count, pending.Count);
                    break;
                case JsonArray array:
                    foreach (var child in array.ToList()) if (child != null) Walk(child);
                    break;
            }
        }

        Walk(root);
    }

    public static void FinalizeCallSignatures(JsonNode root)
    {
        void Walk(JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var child in obj.Select(property => property.Value).ToList())
                        if (child != null) Walk(child);
                    if (obj[CallWitnessCountKey] is not JsonValue countValue
                        || !countValue.TryGetValue<int>(out var count) || count <= 0)
                        return;
                    var argumentCount = (obj["args"] as JsonArray)?.Count
                        ?? throw new InvalidOperationException("bir2cir: reified call lost its argument vector");
                    FinalizeVector(obj, "sig", argumentCount, count);
                    FinalizeVector(obj, "argTypes", argumentCount, count);
                    FinalizeVector(obj, "memberSignature", argumentCount, count);
                    obj.Remove(CallWitnessCountKey);
                    break;
                case JsonArray array:
                    foreach (var child in array.ToList()) if (child != null) Walk(child);
                    break;
            }
        }
        Walk(root);
    }

    static void FinalizeVector(JsonObject call, string key, int argumentCount, int witnessCount)
    {
        if (call[key] is not JsonArray vector) return;
        if (vector.Count > argumentCount || argumentCount - vector.Count > witnessCount)
            throw new InvalidOperationException(
                $"bir2cir: reified call has inconsistent '{key}' ({vector.Count}) and argument ({argumentCount}) counts");
        while (vector.Count < argumentCount) AppendBooleanType(call, key);
    }

    static JsonNode WitnessFor(
        JsonNode type,
        WitnessFrame callerWitnesses)
    {
        return TypeJson.Read(type) switch
        {
            TypeNode.Nullable => ConstBool(true),
            TypeNode.Tv { Scope: "method" } tv when callerWitnesses?.Method != null
                && callerWitnesses.Method.TryGetValue(tv.I, out var witness)
                => witness.DeepClone(),
            TypeNode.Tv { Scope: "type" } tv when callerWitnesses?.Type != null
                && callerWitnesses.Type.TryGetValue(tv.I, out var witness)
                => witness.DeepClone(),
            // DotKt has always allowed an ordinary CLR method type parameter to be passed to a reified Kotlin
            // declaration. Such a parameter carries no Kotlin nullable-instantiation fact, so retain the historical
            // underlying-CLR-type behavior; a reified caller parameter takes the dynamic branch above instead.
            TypeNode.Tv { Scope: "method" } => ConstBool(false),
            TypeNode.Tv { Scope: "type" } => ConstBool(false),
            _ => ConstBool(false),
        };
    }

    static bool TryWitnessForExisting(JsonNode type, WitnessFrame callerWitnesses, out JsonNode witness)
    {
        switch (TypeJson.Read(type))
        {
            case TypeNode.Tv { Scope: "method" } tv when callerWitnesses?.Method != null
                && callerWitnesses.Method.TryGetValue(tv.I, out var methodWitness):
                witness = methodWitness.DeepClone();
                return true;
            case TypeNode.Tv { Scope: "type" } tv when callerWitnesses?.Type != null
                && callerWitnesses.Type.TryGetValue(tv.I, out var typeWitness):
                witness = typeWitness.DeepClone();
                return true;
            default:
                witness = null;
                return false;
        }
    }

    static void AppendBooleanType(JsonObject call, string key)
    {
        if (call[key] is JsonArray vector) vector.Add(Fqn("kotlin.Boolean"));
    }

    static JsonObject ConstBool(bool value) => new() {
        ["k"] = "const", ["type"] = Fqn("kotlin.Boolean"), ["value"] = value,
    };

    static JsonObject Fqn(string name) => new() { ["t"] = "fqn", ["name"] = name };

    static JsonArray IntArray(IEnumerable<int> indices) =>
        new(indices.Select(index => (JsonNode)JsonValue.Create(index)).ToArray());

    static int[] ReifiedIndices(JsonObject method, string key = "typeParams") => method[key] is JsonArray parameters
        ? parameters.Select((parameter, index) => (parameter, index))
            .Where(x => x.parameter is JsonObject obj && Bool(obj["reified"]))
            .Select(x => x.index).ToArray()
        : Array.Empty<int>();

    static void DropReifiedFacts(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("reified");
                foreach (var child in obj.Select(kv => kv.Value).ToList())
                    if (child != null) DropReifiedFacts(child);
                break;
            case JsonArray array:
                foreach (var child in array.ToList()) if (child != null) DropReifiedFacts(child);
                break;
        }
    }

    static string Str(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;
    static bool Bool(JsonNode node) => (node as JsonValue)?.TryGetValue<bool>(out var value) == true && value;
}
