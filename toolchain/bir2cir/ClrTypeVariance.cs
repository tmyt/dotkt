using System.Text.Json.Nodes;
using DotKt.Bir;

// CLR variance is a property of the complete physical declaration, not just its method returns.
// Source variance is preserved by RoundtripMetadata before this pass weakens the GenericParam flags.
// The same oracle serves representation decisions and final CIR so consumers cannot rely on a
// variant conversion that the emitted interface's inherited invariant faces prohibit.
static class ClrTypeVariance
{
    public static void ApplyAll(IEnumerable<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var definitions = roots.OfType<JsonObject>().SelectMany(Types).ToArray();
        var byName = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var definition in definitions)
            byName.TryAdd(Str(definition["name"]), definition);

        // Compute before mutating: recursive local edges must see the same declarations regardless
        // of source file or type order. Invalidity propagates over dependencies rather than
        // enumerating every simple path through mutually recursive declarations.
        var analysis = new Analysis(byName, refs);
        var removals = new List<JsonObject>();
        foreach (var definition in definitions)
        {
            if (definition["capturedTypeParams"] is JsonArray captured)
                removals.AddRange(captured.OfType<JsonObject>());
            if (definition["typeParams"] is not JsonArray parameters) continue;
            var offset = (definition["capturedTypeParams"] as JsonArray)?.Count ?? 0;
            for (var index = 0; index < parameters.Count; index++)
                if (parameters[index] is JsonObject parameter && Str(parameter["variance"]) is "in" or "out"
                    && analysis.Get(Str(definition["name"]), offset + parameters.Count, offset + index) == null)
                    removals.Add(parameter);
        }
        foreach (var parameter in removals) parameter.Remove("variance");
    }

    static IEnumerable<JsonObject> Types(JsonObject container)
    {
        if (container["types"] is not JsonArray types) yield break;
        foreach (var type in types.OfType<JsonObject>())
        {
            yield return type;
            foreach (var nested in Types(type)) yield return nested;
        }
    }

    internal static string Get(string name, int arity, int index,
        IReadOnlyDictionary<string, JsonObject> definitions, ReferenceMetadataIndex refs) =>
        new Analysis(definitions, refs).Get(name, arity, index);

    sealed class Analysis(IReadOnlyDictionary<string, JsonObject> definitions, ReferenceMetadataIndex refs)
    {
        sealed class Candidate(JsonObject definition, int position, int variance)
        {
            public readonly JsonObject Definition = definition;
            public readonly int Position = position, Variance = variance;
            public readonly HashSet<Candidate> Dependents = new();
            public bool Invalid;
        }

        readonly Dictionary<(string, int), Candidate> candidates = new();
        readonly Dictionary<(string, int, int), int> external = new();
        readonly Queue<Candidate> pending = new(), invalid = new();

        int Declared(string name, int arity, int index)
        {
            if (definitions.TryGetValue(name, out var local))
            {
                var ownIndex = index - ((local["capturedTypeParams"] as JsonArray)?.Count ?? 0);
                if (Str(local["kind"]) is not ("interface" or "delegate")
                    || local["typeParams"] is not JsonArray parameters
                    || ownIndex < 0 || ownIndex >= parameters.Count
                    || parameters[ownIndex] is not JsonObject parameter) return 0;
                return Str(parameter["variance"]) switch { "out" => 1, "in" => -1, _ => 0 };
            }
            var key = (name, arity, index);
            if (!external.TryGetValue(key, out var value))
                external[key] = value = refs?.PhysicalOwnerTypeParameterVariance(name, arity, index) switch
                { "out" => 1, "in" => -1, _ => 0 };
            return value;
        }

        Candidate Require(string name, int position, int variance)
        {
            var key = (name, position);
            if (!candidates.TryGetValue(key, out var candidate))
            {
                candidates.Add(key, candidate = new Candidate(definitions[name], position, variance));
                pending.Enqueue(candidate);
            }
            return candidate;
        }

        void Invalidate(Candidate candidate)
        {
            if (candidate.Invalid) return;
            candidate.Invalid = true;
            invalid.Enqueue(candidate);
        }

        public string Get(string name, int arity, int index)
        {
            var variance = Declared(name, arity, index);
            if (variance == 0) return null;
            if (definitions.ContainsKey(name))
            {
                var root = Require(name, index, variance);
                while (pending.TryDequeue(out var candidate))
                    if (!candidate.Invalid && Conflicts(candidate)) Invalidate(candidate);
                while (invalid.TryDequeue(out var candidate))
                    foreach (var dependent in candidate.Dependents) Invalidate(dependent);
                if (root.Invalid) return null;
            }
            return variance == 1 ? "out" : "in";
        }

        bool Conflicts(Candidate candidate)
        {
            var definition = candidate.Definition;
            bool Slot(JsonNode slot, int context) => TypeJson.Read(slot) is TypeNode type && Conflicts(type, candidate, context);
            if (Slot(definition["base"], 1)) return true;
            if (definition["interfaces"] is JsonArray interfaces && interfaces.Any(type => Slot(type, 1))) return true;
            var methods = (definition["methods"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();
            if (Str(definition["kind"]) == "delegate") methods = methods.Prepend(definition);
            foreach (var method in methods)
            {
                if (method["static"]?.GetValue<bool>() == true
                    && method["virtual"]?.GetValue<bool>() != true && method["abstract"]?.GetValue<bool>() != true) continue;
                if (method["params"] is JsonArray parameters
                    && parameters.OfType<JsonObject>().Any(parameter => Slot(parameter["type"], -1))) return true;
                if (Slot(method["ret"], 1)) return true;
                // ECMA-335 II.9.7: method generic constraints are input positions; the type's own
                // generic constraints impose no variance restriction.
                if (method["typeParams"] is JsonArray genericParameters
                    && genericParameters.OfType<JsonObject>().Any(parameter =>
                        parameter["constraints"] is JsonArray constraints && constraints.Any(type => Slot(type, -1)))) return true;
            }
            return false;
        }

        bool Conflicts(TypeNode type, Candidate candidate, int context)
        {
            if (!Mentions(type, candidate.Position)) return false;
            if (context == 0) return true;
            bool Nested(TypeNode slot, int nestedContext) => Conflicts(slot, candidate, nestedContext);
            switch (type)
            {
                case TypeNode.Tv: return context != candidate.Variance;
                case TypeNode.Fqn { Args: { } arguments } application:
                    for (var index = 0; index < arguments.Length; index++)
                    {
                        if (!Mentions(arguments[index], candidate.Position)) continue;
                        var variance = Declared(application.Name, arguments.Length, index);
                        if (variance == 0 || Nested(arguments[index], context * variance)) return true;
                        if (definitions.ContainsKey(application.Name))
                        {
                            var dependency = Require(application.Name, index, variance);
                            dependency.Dependents.Add(candidate);
                            if (dependency.Invalid) Invalidate(candidate);
                        }
                    }
                    return false;
                case TypeNode.Projection projection: return Nested(projection.Of, context);
                case TypeNode.Nullable nullable: return Nested(nullable.Of, context);
                case TypeNode.Oblivious oblivious: return Nested(oblivious.Of, context);
                case TypeNode.Array array: return Nested(array.Elem, 0);
                case TypeNode.ByRef byRef: return Nested(byRef.Of, 0);
                case TypeNode.Ptr pointer: return Nested(pointer.Of, 0);
                case TypeNode.Mod modifier: return Nested(modifier.Of, context);
                case TypeNode.Fn function:
                    return Nested(function.Ret, context) || function.DelegateParams.Any(parameter => Nested(parameter, -context));
                default: return false;
            }
        }
    }

    static bool Mentions(TypeNode type, int position) => type switch
    {
        TypeNode.Tv variable => variable.Scope == "type" && variable.I == position,
        TypeNode.Fqn { Args: { } arguments } => arguments.Any(argument => Mentions(argument, position)),
        TypeNode.Projection projection => Mentions(projection.Of, position),
        TypeNode.Nullable nullable => Mentions(nullable.Of, position),
        TypeNode.Oblivious oblivious => Mentions(oblivious.Of, position),
        TypeNode.Array array => Mentions(array.Elem, position),
        TypeNode.ByRef byRef => Mentions(byRef.Of, position),
        TypeNode.Ptr pointer => Mentions(pointer.Of, position),
        TypeNode.Mod modifier => Mentions(modifier.Of, position),
        TypeNode.Fn function => Mentions(function.Ret, position) || function.DelegateParams.Any(parameter => Mentions(parameter, position)),
        _ => false,
    };

    internal static void SelfTest()
    {
        const string declarations = """
            {"types":[
              {"name":"Invariant","kind":"interface","typeParams":[{"name":"T"}]},
              {"name":"ClassVariance","kind":"class","typeParams":[{"name":"T","variance":"out"}]},
              {"name":"InheritedConflict","kind":"interface","typeParams":[{"name":"T","variance":"out"}],
               "interfaces":[{"t":"fqn","name":"Invariant","args":[{"t":"tv","scope":"type","i":0}]}]},
              {"name":"TransitiveConflict","kind":"interface","typeParams":[{"name":"T","variance":"out"}],
               "interfaces":[{"t":"fqn","name":"InheritedConflict","args":[{"t":"tv","scope":"type","i":0}]}]},
              {"name":"InputConflict","kind":"interface","typeParams":[{"name":"T","variance":"out"}],
               "methods":[{"params":[{"type":{"t":"tv","scope":"type","i":0}}]}]},
              {"name":"NestedConflict","kind":"interface","typeParams":[{"name":"T","variance":"out"}],
               "methods":[{"ret":{"t":"fqn","name":"InputConflict","args":[{"t":"tv","scope":"type","i":0}]}}]},
              {"name":"SafeOutput","kind":"interface","typeParams":[{"name":"T","variance":"out"}],
               "methods":[{"ret":{"t":"tv","scope":"type","i":0}}]},
              {"name":"SafeInherited","kind":"interface","typeParams":[{"name":"T","variance":"out"}],
               "interfaces":[{"t":"fqn","name":"SafeOutput","args":[{"t":"tv","scope":"type","i":0}]}]},
              {"name":"RecursiveOutput","kind":"interface","typeParams":[{"name":"T","variance":"out"}],
               "methods":[{"ret":{"t":"fqn","name":"RecursiveOutput","args":[{"t":"tv","scope":"type","i":0}]}}]},
              {"name":"ConstraintConflict","kind":"interface","typeParams":[{"name":"T","variance":"out"}],
               "methods":[{"typeParams":[{"name":"U","constraints":[{"t":"tv","scope":"type","i":0}]}]}]},
              {"name":"SafeConstraint","kind":"interface","typeParams":[{"name":"T","variance":"in"}],
               "methods":[{"typeParams":[{"name":"U","constraints":[{"t":"tv","scope":"type","i":0}]}]}]}
            ]}
            """;
        foreach (var reverse in new[] { false, true })
        {
            var root = JsonNode.Parse(declarations).AsObject();
            if (reverse) root["types"] = new JsonArray(((JsonArray)root["types"]).Reverse().Select(type => type.DeepClone()).ToArray());
            ApplyAll(new[] { root }, null);
            foreach (var type in ((JsonArray)root["types"]).OfType<JsonObject>())
            {
                var name = Str(type["name"]);
                var expected = name is "SafeOutput" or "SafeInherited" or "RecursiveOutput" ? "out"
                    : name == "SafeConstraint" ? "in" : null;
                if (Str(type["typeParams"][0]["variance"]) != expected)
                    throw new InvalidOperationException($"CLR variance self-test failed for {name} (reverse={reverse})");
            }
        }
        // A dense, legal recursive graph must be checked as dependencies, not factorially many
        // simple paths. One invalid input in the same graph must invalidate every dependent.
        foreach (var broken in new[] { false, true })
        {
            var types = new JsonArray();
            for (var index = 0; index < 12; index++)
            {
                var methods = new JsonArray();
                for (var target = 0; target < 12; target++)
                    if (target != index) methods.Add(new JsonObject
                    {
                        ["ret"] = TypeJson.Write(new TypeNode.Fqn("Cycle" + target, new TypeNode[] { new TypeNode.Tv("type", 0) })),
                    });
                if (broken && index == 11) methods.Add(new JsonObject
                {
                    ["params"] = new JsonArray(new JsonObject { ["type"] = TypeJson.Write(new TypeNode.Tv("type", 0)) }),
                });
                types.Add(new JsonObject
                {
                    ["name"] = "Cycle" + index, ["kind"] = "interface",
                    ["typeParams"] = new JsonArray(new JsonObject { ["name"] = "T", ["variance"] = "out" }),
                    ["methods"] = methods,
                });
            }
            ApplyAll(new[] { new JsonObject { ["types"] = types } }, null);
            if (types.Any(type => Str(type["typeParams"][0]["variance"]) != (broken ? null : "out")))
                throw new InvalidOperationException("CLR variance dense recursive graph self-test failed");
        }
        Console.WriteLine("[CLR variance] self-test OK (inherited invariance, transitive signatures, order, dense recursion, method constraints)");
    }

    static string Str(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;
}
