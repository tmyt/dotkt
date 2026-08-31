using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Computes the CLR-only demand for a nullable-instantiation witness.  Kotlin `reified` is deliberately absent from
// this analysis: it is a source/declaration fact, whereas a witness is needed only when a nullable-sensitive operation
// consumes a type variable.  Demand flows backwards through exact declaration identities and through the explicit
// positional type-argument correspondence on every lifted/materialized frame.
sealed class NullableWitnessDemand
{
    readonly IReadOnlyDictionary<string, int[]> _localDeclarations;
    readonly ReferenceMetadataIndex _refs;

    NullableWitnessDemand(IReadOnlyDictionary<string, int[]> localDeclarations, ReferenceMetadataIndex refs)
    {
        _localDeclarations = localDeclarations;
        _refs = refs;
    }

    public IReadOnlyDictionary<string, int[]> LocalDeclarations => _localDeclarations;

    public static NullableWitnessDemand Collect(IEnumerable<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var rootList = roots.ToList();
        var methods = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

        void Owner(JsonObject owner)
        {
            if (owner["methods"] is JsonArray declarations)
                foreach (var method in declarations.OfType<JsonObject>())
                    if (Str(method[DeclarationIdentityBinding.Key]) is string id)
                    {
                        if (!methods.TryAdd(id, method) && !ReferenceEquals(methods[id], method))
                            throw new InvalidOperationException(
                                $"bir2cir: duplicate declaration identity '{id}' in nullable-witness analysis");
                    }
            if (owner["types"] is JsonArray types)
                foreach (var type in types.OfType<JsonObject>()) Owner(type);
        }

        foreach (var root in rootList.OfType<JsonObject>()) Owner(root);

        var demands = methods.Keys.ToDictionary(
            id => id, _ => new HashSet<int>(), StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            var snapshot = Freeze(demands);
            var analyzer = new NullableWitnessDemand(snapshot, refs);
            var generated = rootList.OfType<JsonObject>()
                .Select(analyzer.AnalyzeGeneratedFrames)
                .ToArray();

            foreach (var (id, method) in methods)
            {
                var required = analyzer.Analyze(method, generated)
                    .Where(variable => variable.Scope == "method")
                    .Select(variable => variable.Index)
                    .Distinct()
                    .OrderBy(index => index)
                    .ToArray();
                var arity = (method["typeParams"] as JsonArray)?.Count ?? 0;
                foreach (var index in required)
                {
                    if (index < 0 || index >= arity)
                        throw new InvalidOperationException(
                            $"bir2cir: nullable-witness demand for declaration '{id}' names method type parameter " +
                            $"{index}, but its generic arity is {arity}");
                    if (demands[id].Add(index)) changed = true;
                }
            }
        } while (changed);

        return new NullableWitnessDemand(Freeze(demands), refs);
    }

    public static void SelfTest()
    {
        var directId = "selftest:direct";
        var forwardId = "selftest:forward";
        var unusedId = "selftest:unused";
        JsonObject TypeParameter(string name) => new() { ["name"] = name, ["reified"] = true };
        JsonObject Tv(int index) => new() { ["t"] = "tv", ["scope"] = "method", ["i"] = index };
        JsonObject Method(string name, string id, JsonNode body) => new() {
            ["name"] = name,
            [DeclarationIdentityBinding.Key] = id,
            ["typeParams"] = new JsonArray(TypeParameter("T")),
            ["params"] = new JsonArray(),
            ["ret"] = new JsonObject { ["t"] = "fqn", ["name"] = "kotlin.Boolean" },
            ["body"] = new JsonArray(body),
        };
        var direct = Method("direct", directId, new JsonObject {
            ["k"] = "return",
            ["value"] = new JsonObject {
                ["k"] = "isInst", ["type"] = Tv(0),
                ["e"] = new JsonObject { ["k"] = "const", ["value"] = null },
            },
        });
        var forward = Method("forward", forwardId, new JsonObject {
            ["k"] = "return",
            ["value"] = new JsonObject {
                ["k"] = "callStatic", [DeclarationIdentityBinding.Key] = directId,
                ["typeArgs"] = new JsonArray(Tv(0)), ["args"] = new JsonArray(),
            },
        });
        var unused = Method("unused", unusedId, new JsonObject {
            ["k"] = "return",
            ["value"] = new JsonObject { ["k"] = "const", ["value"] = true },
        });
        var root = new JsonObject {
            ["fileClass"] = "WitnessSelfTest",
            ["methods"] = new JsonArray(direct, forward, unused),
            ["types"] = new JsonArray(),
        };

        var withMarkers = Collect(new[] { root.DeepClone() }, null).LocalDeclarations;
        var withoutMarkersRoot = root.DeepClone();
        DropReifiedMarkers(withoutMarkersRoot);
        var withoutMarkers = Collect(new[] { withoutMarkersRoot }, null).LocalDeclarations;
        if (!withMarkers[directId].SequenceEqual(new[] { 0 })
            || !withMarkers[forwardId].SequenceEqual(new[] { 0 })
            || withMarkers[unusedId].Length != 0
            || withMarkers.Any(entry => !entry.Value.SequenceEqual(withoutMarkers[entry.Key])))
            throw new InvalidOperationException(
                "nullable-witness demand self-test failed: demand depends on Kotlin reified markers or call propagation");
    }

    public GeneratedFrameDemands AnalyzeGeneratedFrames(JsonObject file)
    {
        var types = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var targets = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var referencedTargets = new HashSet<string>(StringComparer.Ordinal);

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

        void Owner(JsonObject owner)
        {
            if (owner["methods"] is JsonArray methods)
                foreach (var method in methods.OfType<JsonObject>())
                    if (Bool(method["generated"])
                        && method[DeclarationIdentityBinding.Key] == null
                        && Str(method["name"]) is string name
                        && referencedTargets.Contains(name))
                    {
                        if (!targets.TryAdd(name, method) && !ReferenceEquals(targets[name], method))
                            throw new InvalidOperationException(
                                $"bir2cir: ambiguous generated delegate target '{name}'");
                    }
            if (owner["types"] is not JsonArray nested) return;
            foreach (var type in nested.OfType<JsonObject>())
            {
                if (Bool(type["generated"]) && Str(type["name"]) is string name)
                {
                    if (!types.TryAdd(name, type) && !ReferenceEquals(types[name], type))
                        throw new InvalidOperationException(
                            $"bir2cir: ambiguous generated type '{name}'");
                }
                Owner(type);
            }
        }

        References(file);
        Owner(file);

        var typeDemands = types.Keys.ToDictionary(
            name => name, _ => new HashSet<int>(), StringComparer.Ordinal);
        var targetDemands = targets.Keys.ToDictionary(
            name => name, _ => new HashSet<int>(), StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            var frame = new GeneratedFrameDemands(
                Freeze(typeDemands), Freeze(targetDemands), types, targets);
            foreach (var (name, type) in types)
            {
                var arity = (type["typeParams"] as JsonArray)?.Count ?? 0;
                foreach (var index in Analyze(type, new[] { frame })
                    .Where(variable => variable.Scope == "type")
                    .Select(variable => variable.Index).Distinct())
                {
                    if (index < 0 || index >= arity)
                        throw new InvalidOperationException(
                            $"bir2cir: generated type '{name}' demands generic position {index}, " +
                            $"but its generic arity is {arity}");
                    if (typeDemands[name].Add(index)) changed = true;
                }
            }
            foreach (var (name, target) in targets)
            {
                var arity = (target["typeParams"] as JsonArray)?.Count ?? 0;
                foreach (var index in Analyze(target, new[] { frame })
                    .Where(variable => variable.Scope == "method")
                    .Select(variable => variable.Index).Distinct())
                {
                    if (index < 0 || index >= arity)
                        throw new InvalidOperationException(
                            $"bir2cir: generated delegate target '{name}' demands generic position {index}, " +
                            $"but its generic arity is {arity}");
                    if (targetDemands[name].Add(index)) changed = true;
                }
            }
        } while (changed);

        return new GeneratedFrameDemands(
            Freeze(typeDemands), Freeze(targetDemands), types, targets);
    }

    public int[] MaterializedFrameIndices(
        JsonNode frame,
        JsonArray typeArguments,
        GeneratedFrameDemands generated,
        bool dense = false) => ResolveFramePositions(
            Analyze(frame, new[] { generated }), typeArguments, "materialized frame", dense);

    HashSet<TypeVariable> Analyze(JsonNode root, IReadOnlyList<GeneratedFrameDemands> generatedFrames)
    {
        var required = new HashSet<TypeVariable>();

        int[] DeclarationDemand(string id) => _localDeclarations.TryGetValue(id, out var local)
            ? local
            : _refs?.NullableWitnessTypeParameterIndices(id) ?? Array.Empty<int>();

        int[] GeneratedTypeDemand(string name) => generatedFrames
            .Select(frames => frames.TypeDemands.TryGetValue(name, out var demand) ? demand : null)
            .FirstOrDefault(demand => demand != null) ?? Array.Empty<int>();

        int[] GeneratedTargetDemand(string name) => generatedFrames
            .Select(frames => frames.TargetDemands.TryGetValue(name, out var demand) ? demand : null)
            .FirstOrDefault(demand => demand != null) ?? Array.Empty<int>();

        void Map(IEnumerable<int> positions, JsonArray typeArguments, string context)
        {
            foreach (var index in positions)
            {
                if (index < 0 || index >= typeArguments.Count)
                    throw new InvalidOperationException(
                        $"bir2cir: {context} has no type argument at demanded nullable-witness position {index}");
                if (TypeJson.Read(typeArguments[index]) is TypeNode.Tv tv)
                    required.Add(new TypeVariable(tv.Scope, tv.I));
            }
        }

        void MapFrame(IEnumerable<TypeVariable> variables, JsonArray typeArguments, string context, bool dense = false)
        {
            foreach (var position in ResolveFramePositions(variables, typeArguments, context, dense))
                if (TypeJson.Read(typeArguments[position]) is TypeNode.Tv tv)
                    required.Add(new TypeVariable(tv.Scope, tv.I));
        }

        HashSet<TypeVariable> Nested(JsonNode node)
        {
            var nested = new HashSet<TypeVariable>();
            // Keep the recursive implementation single-sourced by analyzing the detached frame independently.
            foreach (var variable in Analyze(node, generatedFrames)) nested.Add(variable);
            return nested;
        }

        void WalkOperands(JsonObject obj, params string[] excluded)
        {
            var skip = excluded.ToHashSet(StringComparer.Ordinal);
            foreach (var property in obj)
                if (!skip.Contains(property.Key) && property.Value != null)
                    Walk(property.Value);
        }

        void Walk(JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                {
                    var kind = Str(obj["k"]);
                    if (kind == "newClosure" && obj["synthClass"] is JsonObject closure)
                    {
                        WalkOperands(obj, "synthClass", "typeArgs", "closureType", "funcType");
                        var nested = Nested(closure);
                        if (nested.Count == 0) return;
                        var typeArgs = obj["typeArgs"] as JsonArray
                            ?? throw new InvalidOperationException(
                                "bir2cir: nullable-sensitive closure has no type arguments");
                        MapFrame(nested, typeArgs,
                            $"closure '{Str(closure["name"])}'");
                        return;
                    }
                    if (kind == "newSam" && obj["synthClass"] is JsonObject sam)
                    {
                        WalkOperands(obj, "synthClass", "typeArgs", "samType");
                        var nested = Nested(sam);
                        if (nested.Count == 0) return;
                        var typeArgs = obj["typeArgs"] as JsonArray
                            ?? throw new InvalidOperationException(
                                "bir2cir: nullable-sensitive SAM has no type arguments");
                        MapFrame(nested, typeArgs,
                            $"SAM '{Str(sam["name"])}'");
                        return;
                    }
                    if (kind == "newSuspendLambda")
                    {
                        WalkOperands(obj, "body", "typeArgs", "typeParams", "typeParamDecls", "funcType");
                        var nested = Nested(obj["body"] ?? new JsonArray());
                        if (nested.Count == 0) return;
                        var typeArgs = obj["typeArgs"] as JsonArray
                            ?? throw new InvalidOperationException(
                                "bir2cir: nullable-sensitive suspend lambda has no type arguments");
                        MapFrame(nested, typeArgs, "suspend lambda", dense: Str(obj["typeFrame"]) == "dense");
                        return;
                    }
                    if (kind == "newDelegate" && Str(obj["method"]) is string target)
                    {
                        WalkOperands(obj, "typeArgs", "funcType");
                        var positions = GeneratedTargetDemand(target);
                        if (positions.Length == 0) return;
                        var typeArgs = obj["typeArgs"] as JsonArray
                            ?? throw new InvalidOperationException(
                                $"bir2cir: nullable-sensitive generated delegate '{target}' has no type arguments");
                        Map(positions, typeArgs, $"generated delegate '{target}'");
                        return;
                    }
                    if (kind == "new" && TypeJson.Read(obj["type"]) is TypeNode.Fqn constructed)
                    {
                        WalkOperands(obj, "type");
                        var positions = GeneratedTypeDemand(constructed.Name);
                        if (positions.Length == 0) return;
                        var typeArgs = new JsonArray(
                            (constructed.Args ?? Array.Empty<TypeNode>()).Select(TypeJson.Write).ToArray());
                        Map(positions, typeArgs, $"generated type '{constructed.Name}'");
                        return;
                    }

                    WalkOperands(obj, "typeArgs");
                    if (kind == "isInst" && TypeJson.Read(obj["type"]) is TypeNode.Tv tested)
                        required.Add(new TypeVariable(tested.Scope, tested.I));
                    if (kind is "callStatic" or "callInstance" or "constrainedCall" or "callInline"
                        && Str(obj[DeclarationIdentityBinding.Key]) is string id)
                    {
                        var positions = DeclarationDemand(id);
                        if (positions.Length == 0) return;
                        var typeArgs = obj["typeArgs"] as JsonArray
                            ?? throw new InvalidOperationException(
                                $"bir2cir: nullable-sensitive call '{id}' has no type arguments");
                        Map(positions, typeArgs, $"call '{id}'");
                    }
                    break;
                }
                case JsonArray array:
                    foreach (var child in array) if (child != null) Walk(child);
                    break;
            }
        }

        Walk(root);
        return required;
    }

    static IReadOnlyDictionary<string, int[]> Freeze(
        IReadOnlyDictionary<string, HashSet<int>> source) => source.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.OrderBy(index => index).ToArray(),
            StringComparer.Ordinal);

    static int[] ResolveFramePositions(
        IEnumerable<TypeVariable> variables,
        JsonArray typeArguments,
        string context,
        bool dense)
    {
        var positions = new HashSet<int>();
        foreach (var variable in variables)
        {
            if (dense)
            {
                if (variable.Index < 0 || variable.Index >= typeArguments.Count)
                    throw new InvalidOperationException(
                        $"bir2cir: {context} has no dense type argument at nullable-witness position {variable.Index}");
                positions.Add(variable.Index);
                continue;
            }

            var matches = typeArguments.Select((argument, index) => (argument, index))
                .Where(entry => TypeJson.Read(entry.argument) is TypeNode.Tv tv
                    && tv.Scope == variable.Scope && tv.I == variable.Index)
                .Select(entry => entry.index)
                .ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(
                    $"bir2cir: {context} maps nullable-sensitive {variable.Scope} type parameter {variable.Index} " +
                    $"to {matches.Length} materialized type arguments");
            positions.Add(matches[0]);
        }
        return positions.OrderBy(index => index).ToArray();
    }

    static string Str(JsonNode node) =>
        (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;
    static bool Bool(JsonNode node) =>
        (node as JsonValue)?.TryGetValue<bool>(out var value) == true && value;

    static void DropReifiedMarkers(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("reified");
                foreach (var child in obj.Select(property => property.Value).ToList())
                    if (child != null) DropReifiedMarkers(child);
                break;
            case JsonArray array:
                foreach (var child in array) if (child != null) DropReifiedMarkers(child);
                break;
        }
    }

    readonly record struct TypeVariable(string Scope, int Index);

    public sealed record GeneratedFrameDemands(
        IReadOnlyDictionary<string, int[]> TypeDemands,
        IReadOnlyDictionary<string, int[]> TargetDemands,
        IReadOnlyDictionary<string, JsonObject> Types,
        IReadOnlyDictionary<string, JsonObject> Targets);
}
