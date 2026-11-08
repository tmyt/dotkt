using System.Reflection;
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
        // of source file or type order. A cycle provisionally retains its declaration variance;
        // every edge and signature on the path is still checked before that result can escape.
        var removals = new List<JsonObject>();
        foreach (var definition in definitions)
        {
            if (definition["capturedTypeParams"] is JsonArray captured)
                removals.AddRange(captured.OfType<JsonObject>());
            if (definition["typeParams"] is not JsonArray parameters) continue;
            var offset = (definition["capturedTypeParams"] as JsonArray)?.Count ?? 0;
            for (var index = 0; index < parameters.Count; index++)
                if (parameters[index] is JsonObject parameter && Str(parameter["variance"]) is "in" or "out"
                    && Get(Str(definition["name"]), offset + parameters.Count, offset + index, byName, refs) == null)
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
        Variance(name, arity, index, definitions, refs, new HashSet<string>(StringComparer.Ordinal)) switch
        {
            1 => "out",
            -1 => "in",
            _ => null,
        };

    static int Variance(string name, int arity, int index,
        IReadOnlyDictionary<string, JsonObject> definitions, ReferenceMetadataIndex refs, HashSet<string> visiting)
    {
        if (definitions.TryGetValue(name, out var local))
        {
            var offset = (local["capturedTypeParams"] as JsonArray)?.Count ?? 0;
            var ownIndex = index - offset;
            if (Str(local["kind"]) is not ("interface" or "delegate")
                || local["typeParams"] is not JsonArray parameters
                || ownIndex < 0 || ownIndex >= parameters.Count
                || parameters[ownIndex] is not JsonObject parameter)
                return 0;
            var declared = Str(parameter["variance"]) switch { "out" => 1, "in" => -1, _ => 0 };
            if (declared == 0) return 0;
            var key = name + "\0" + index;
            if (!visiting.Add(key)) return declared;
            var conflict = Conflicts(local, index, declared, definitions, refs, visiting);
            visiting.Remove(key);
            return conflict ? 0 : declared;
        }
        var reflected = refs.ResolveNetType(name, arity);
        var arguments = reflected?.IsGenericTypeDefinition == true ? reflected.GetGenericArguments() : null;
        if (arguments == null || index < 0 || index >= arguments.Length) return 0;
        return (arguments[index].GenericParameterAttributes & GenericParameterAttributes.VarianceMask) switch
        {
            GenericParameterAttributes.Covariant => 1,
            GenericParameterAttributes.Contravariant => -1,
            _ => 0,
        };
    }

    static bool Conflicts(JsonObject definition, int position, int declared,
        IReadOnlyDictionary<string, JsonObject> definitions, ReferenceMetadataIndex refs, HashSet<string> visiting)
    {
        bool Slot(JsonNode slot, int context) => TypeJson.Read(slot) is TypeNode type
            && Conflicts(type, position, context, declared, definitions, refs, visiting);

        if (Slot(definition["base"], 1)) return true;
        if (definition["interfaces"] is JsonArray interfaces && interfaces.Any(type => Slot(type, 1))) return true;
        var methods = (definition["methods"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();
        if (Str(definition["kind"]) == "delegate") methods = methods.Prepend(definition);
        foreach (var method in methods)
        {
            if (method["static"]?.GetValue<bool>() == true
                && method["virtual"]?.GetValue<bool>() != true && method["abstract"]?.GetValue<bool>() != true)
                continue;
            if (method["params"] is JsonArray parameters
                && parameters.OfType<JsonObject>().Any(parameter => Slot(parameter["type"], -1))) return true;
            if (Slot(method["ret"], 1)) return true;
            // ECMA-335 II.9.7: method generic constraints are input positions; the type's own
            // generic constraints impose no variance restriction.
            if (method["typeParams"] is JsonArray genericParameters
                && genericParameters.OfType<JsonObject>().Any(parameter =>
                    parameter["constraints"] is JsonArray constraints && constraints.Any(type => Slot(type, -1))))
                return true;
        }
        return false;
    }

    static bool Conflicts(TypeNode type, int position, int context, int declared,
        IReadOnlyDictionary<string, JsonObject> definitions, ReferenceMetadataIndex refs, HashSet<string> visiting)
    {
        bool Nested(TypeNode slot, int nestedContext) =>
            Conflicts(slot, position, nestedContext, declared, definitions, refs, visiting);
        switch (type)
        {
            case TypeNode.Tv variable:
                return variable.Scope == "type" && variable.I == position && (context == 0 || context != declared);
            case TypeNode.Fqn { Args: { } arguments } application:
                for (var index = 0; index < arguments.Length; index++)
                    if (Nested(arguments[index], context * Variance(application.Name, arguments.Length, index,
                            definitions, refs, visiting))) return true;
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
        Console.WriteLine("[CLR variance] self-test OK (inherited/native-shaped invariance, transitive signatures, source order, recursion, method constraints)");
    }

    static string Str(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;
}
