using System.Text.Json.Nodes;
using DotKt.Bir;

sealed partial class ValueRepresentationLowering
{
    // Newly synthesized collection bridge bodies enter here in the continuing physical graph.
    // Initial source operations are owned exclusively by the typed BindOperations entry.
    internal void BindMaterializedOperations(JsonObject root)
    {
        if (referenceBuild || bindings.Count == 0) return;
        if (!domBound) throw new InvalidOperationException("Materialized value operations precede the declaration handoff");
        var suffix = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Names(JsonNode current)
        {
            if (current is JsonObject obj)
            {
                if (Text(obj["name"]) is string name) names.Add(name);
                if (Text(obj["var"]) is string variable) names.Add(variable);
                foreach (var child in obj) Names(child.Value);
            }
            else if (current is JsonArray array) foreach (var child in array) Names(child);
        }
        Names(root);
        void Walk(JsonNode node)
        {
            if (node is JsonArray array) { foreach (var child in array.ToArray()) Walk(child); return; }
            if (node is not JsonObject operation) return;
            foreach (var (key, child) in operation.ToArray())
                if (key is not ("attrs" or "overrides" or DeclarationIdentityBinding.SemanticSignatureKey)) Walk(child);
            var kind = Text(operation["k"]);
            if (kind == "callInstance" && operation["super"]?.GetValue<bool>() == true
                && Text(operation[DeclarationIdentityBinding.Key]) is string selected
                && members.TryGetValue(selected, out var body) && !body.Method.Abstract)
            {
                ReplaceMaterialized(operation, DefaultCall(body.Owner, body.Method.Dom, operation["recv"], operation["args"] as JsonArray));
                return;
            }
            if (kind == "callInstance" && operation["super"]?.GetValue<bool>() != true
                && SelectedAdapter(operation) is string target)
            {
                var arguments = new[] { operation["recv"] }.Concat(operation["args"] as JsonArray ?? new JsonArray()).ToArray();
                var signature = new[] { new TypeNode.Fqn("kotlin.Any") }.Concat(
                    (operation["sig"] as JsonArray ?? new JsonArray()).Select(TypeJson.Read)).ToArray();
                var memberResult = TypeJson.Read(operation["dynRet"] ?? operation["ret"] ?? operation["sty"])
                    ?? throw new InvalidOperationException("Value member adapter has no selected return type");
                ReplaceMaterialized(operation, Helper(target, arguments, signature, memberResult));
                return;
            }
            if (kind is not ("cast" or "isInst" or "isInstRef") || operation["e"] == null) return;
            var source = TypeJson.Read(operation["type"]);
            var scalar = source is TypeNode.Nullable n ? n.Of : source;
            if (kind is "cast" or "isInstRef" && scalar is TypeNode.Tv variable
                && !KotlinTypeWitness.NeedsWitness(operation)
                && SemanticBound(operation, variable, new HashSet<TypeNode.Tv>()) is { } bound)
            {
                // An unchecked generic cast still checks its erased source bound. Keep that
                // guard inside the operand so deferred Object-return/narrowing remains unchanged.
                // Like an ordinary raw class-bound cast, this preguard admits null; the original
                // generic cast and its eventual typed consumer retain ownership of null handling.
                var guard = (JsonObject)operation.DeepClone();
                guard["type"] = TypeJson.Write(bound is TypeNode.Nullable ? bound : new TypeNode.Nullable(bound));
                Walk(guard);
                operation["e"] = new JsonObject { ["k"] = "cast",
                    ["type"] = TypeJson.Write(new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"))), ["e"] = guard };
                return;
            }
            var nullable = source is TypeNode.Nullable;
            var (binding, shape) = Classifier(source);
            if (binding == null) return;
            // A logical variant alias has no single native interface that every admitted
            // value implements. Its declaration-owned classifier is the entire scalar guard.
            var opaque = OpaqueScalar(source);
            var physical = ClassifierType(source);
            string temp;
            do temp = "$valueClassifier$" + suffix++; while (!names.Add(temp));
            var any = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
            JsonObject Local() => new() { ["k"] = "local", ["name"] = temp, ["sty"] = TypeJson.Write(any) };
            JsonObject Null() => new() { ["k"] = "const", ["type"] = TypeJson.Write(any), ["value"] = null };
            JsonObject Bool(bool value) => new() { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Boolean"), ["value"] = value };
            var matches = new JsonObject {
                ["k"] = "cond", ["type"] = TypeJson.Fqn("kotlin.Boolean"),
                ["cond"] = new JsonObject { ["k"] = "objEq", ["lhs"] = Local(), ["rhs"] = Null() },
                ["then"] = Bool(nullable),
                ["else"] = new JsonObject {
                    ["k"] = "cond", ["type"] = TypeJson.Fqn("kotlin.Boolean"),
                    ["cond"] = Helper(binding.Classifier, new[] { Local(), Shape(shape) },
                        new TypeNode[] { any, new TypeNode.Fqn("kotlin.String") }, new TypeNode.Fqn("kotlin.Boolean")),
                    ["then"] = new JsonObject { ["k"] = "isInst", ["type"] = TypeJson.Write(physical), ["e"] = Local() },
                    ["else"] = Bool(false),
                },
            };
            var statements = new JsonArray(new JsonObject { ["k"] = "var", ["name"] = temp,
                ["type"] = TypeJson.Write(any), ["init"] = operation["e"].DeepClone() });
            JsonNode result;
            if (kind == "cast")
            {
                if (!nullable)
                    statements.Add(new JsonObject { ["k"] = "if", ["branches"] = new JsonArray(new JsonObject {
                        ["cond"] = new JsonObject { ["k"] = "objEq", ["lhs"] = Local(), ["rhs"] = Null() },
                        ["body"] = new JsonArray(new JsonObject { ["k"] = "throw", ["value"] = new JsonObject {
                            ["k"] = "new", ["type"] = TypeJson.Fqn("kotlin.NullPointerException"),
                            ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
                        } }),
                    }) });
                statements.Add(new JsonObject { ["k"] = "if", ["branches"] = new JsonArray(new JsonObject {
                    ["cond"] = new JsonObject { ["k"] = "unaryOp", ["op"] = "!", ["e"] = matches },
                    ["body"] = new JsonArray(new JsonObject { ["k"] = "throw", ["value"] = new JsonObject {
                        ["k"] = "new", ["type"] = TypeJson.Fqn("kotlin.ClassCastException"),
                        ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
                    } }),
                }) });
                result = new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Write(opaque ? any : source), ["e"] = Local() };
            }
            else if (kind == "isInst") result = matches;
            else result = new JsonObject {
                ["k"] = "cond", ["type"] = TypeJson.Write(source is TypeNode.Nullable ? source : new TypeNode.Nullable(source)),
                ["cond"] = matches, ["then"] = new JsonObject { ["k"] = kind, ["type"] = TypeJson.Write(physical), ["e"] = Local() },
                ["else"] = Null(),
            };
            ReplaceMaterialized(operation, new JsonObject { ["k"] = "valueBlock", ["stmts"] = statements, ["result"] = result });
        }
        Walk(root);
    }


    TypeNode SemanticBound(JsonObject operation, TypeNode.Tv variable, HashSet<TypeNode.Tv> visited)
        => ResolveSemanticBound(variable, scope => {
            JsonObject declaration = null;
            for (JsonNode parent = operation.Parent; parent != null; parent = parent.Parent)
                if (parent is JsonObject owner && (scope == "method"
                    ? owner["k"] == null && owner["params"] is JsonArray && owner["body"] is JsonArray
                    : Text(owner["kind"]) is "class" or "interface" or "enum" or "struct"))
                { declaration = owner; break; }
            if (declaration == null) return null;
            var frame = scope == "method" ? NullableRepresentationTypes.DeclarationMethodFrame(declaration)
                : KotlinSupertypesRecord.ReadNullableFrame(declaration);
            var encoded = Text(declaration[scope == "method"
                ? NullableGenericErasure.MethodTypeParameterBoundsPre : KotlinSupertypesRecord.PreKey]);
            var bounds = encoded == null ? null : scope == "method"
                ? SourceBoundsSnapshot.ReadFrozenText(encoded).Bounds : SourceTypeFacts.ReadFrozenText(encoded).Bounds;
            var constraints = (declaration["typeParams"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>().Select(parameter => (parameter["constraints"] as JsonArray)?.Select(TypeJson.Read).ToArray()).ToArray();
            return new SemanticBoundFrame(frame, bounds, constraints);
        }, visited);

    static void ReplaceMaterialized(JsonObject target, JsonObject replacement)
    {
        var position = target["pos"]?.DeepClone();
        target.Clear();
        foreach (var (key, value) in replacement.ToArray()) target[key] = value?.DeepClone();
        if (position != null) target["pos"] = position;
    }
}
