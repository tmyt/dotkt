using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A generic value's storage type is not its Kotlin runtime classifier: Int? can be stored as Object.
// Keep the classifier beside the existing nullability/nominal flags in one declaration-owned ABI value.
static class KotlinRuntimeTypeWitness
{
    internal const string OperandKey = "kotlinRuntimeTypeWitness";
    internal const string ClassifierKey = "kotlinClassifierRef";
    internal static TypeNode Type => new TypeNode.Fqn("System.ValueTuple", new TypeNode[] {
        new TypeNode.Fqn("System.Int32"), new TypeNode.Fqn("System.Type"),
    });
    internal static JsonNode TypeJsonNode() => TypeJson.Write(Type);

    internal static JsonObject Flags(JsonNode witness) => Field(witness, "Item1", new TypeNode.Fqn("System.Int32"));
    internal static JsonObject Classifier(JsonNode witness) => Field(witness, "Item2", new TypeNode.Fqn("System.Type"));
    static JsonObject Field(JsonNode witness, string name, TypeNode result) => new() {
        ["k"] = "field", ["ownerType"] = TypeJsonNode(), ["recv"] = witness.DeepClone(),
        ["name"] = name, ["type"] = TypeJson.Write(result), ["sty"] = TypeJson.Write(result),
    };

    internal static JsonNode Create(JsonNode flags, JsonNode classifier) => new JsonObject {
        ["k"] = "new", ["type"] = TypeJsonNode(),
        ["args"] = new JsonArray(flags, classifier),
        ["argTypes"] = new JsonArray(TypeJson.Fqn("System.Int32"), TypeJson.Fqn("System.Type")),
    };

    internal static JsonNode Known(TypeNode type) => Create(KotlinTypeWitness.Constant(KotlinTypeWitness.Flags(type)),
        new JsonObject { ["k"] = "classRef", ["type"] = TypeJson.Write(type), [ClassifierKey] = true });

    internal static JsonNode Nullable(JsonNode witness) => Create(
        KotlinTypeWitness.Binary("|", Flags(witness), KotlinTypeWitness.Constant(1)), Classifier(witness));

    internal static void LowerOperations(JsonNode root, ReferenceMetadataIndex refs)
    {
        var names = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        void Names(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (obj["name"] is JsonValue value && value.TryGetValue<string>(out var name)) names.Add(name);
                foreach (var child in obj.Select(pair => pair.Value)) Names(child);
            }
            else if (node is JsonArray array) foreach (var child in array) Names(child);
        }
        Names(root);
        var next = 0;
        void Walk(JsonNode node)
        {
            if (node is JsonArray array) { foreach (var child in array.ToArray()) Walk(child); return; }
            if (node is not JsonObject obj) return;
            foreach (var child in obj.Select(pair => pair.Value).ToArray()) Walk(child);
            if (obj[OperandKey] is not JsonNode classifier) return;
            classifier = classifier.DeepClone();
            obj.Remove(OperandKey);
            var flags = obj[KotlinTypeWitness.OperandKey]?.DeepClone()
                ?? throw new InvalidOperationException("Runtime classifier has no Kotlin type flags");
            string name;
            do name = "dotkt$runtimeClassifier$value$" + next++; while (!names.Add(name));
            var any = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
            JsonObject Local() => new() { ["k"] = "local", ["name"] = name, ["sty"] = TypeJson.Write(any) };
            JsonObject Null() => new() { ["k"] = "const", ["type"] = TypeJson.Write(any), ["value"] = null };
            JsonObject Bool(bool value) => new() { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Boolean"), ["value"] = value };
            var test = new JsonObject {
                ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.Type"), ["method"] = "IsInstanceOfType",
                ["recv"] = classifier, ["args"] = new JsonArray(Local()),
                ["argTypes"] = new JsonArray(TypeJson.Fqn("System.Object")), ["ret"] = TypeJson.Fqn("System.Boolean"),
                ["dispatch"] = "callvirt",
                ["memberRef"] = ClrMemberResolution.ArrayStorageMethodRef(refs, "IsInstanceOfType",
                    new[] { "System.Object" }, "System.Type", false),
            };
            // Collection membership is owned by the nominal flags and its existing composite guard.
            // Its CLR token may deliberately be Object; do not replace the guard with a native interface test.
            var matches = new JsonObject {
                ["k"] = "cond", ["cond"] = new JsonObject { ["k"] = "objEq", ["lhs"] = Local(), ["rhs"] = Null() },
                ["then"] = KotlinTypeWitness.AllowsNull(flags),
                ["else"] = new JsonObject {
                    ["k"] = "cond", ["cond"] = KotlinTypeWitness.Binary("!=",
                        KotlinTypeWitness.Binary("&", flags.DeepClone(), KotlinTypeWitness.Constant(-2)), KotlinTypeWitness.Constant(0)),
                    ["then"] = Bool(true), ["else"] = test,
                },
            };
            var statements = new JsonArray(new JsonObject {
                ["k"] = "var", ["name"] = name, ["type"] = TypeJson.Write(any), ["init"] = obj["e"]?.DeepClone(),
            });
            var physical = (JsonObject)obj.DeepClone();
            physical["e"] = Local();
            JsonNode result;
            if (obj["k"]?.GetValue<string>() == "cast")
            {
                var exception = refs.ResolveRefType("System.InvalidCastException");
                statements.Add(new JsonObject {
                    ["k"] = "if", ["branches"] = new JsonArray(new JsonObject {
                        ["cond"] = new JsonObject { ["k"] = "unaryOp", ["op"] = "!", ["e"] = matches },
                        ["body"] = new JsonArray(new JsonObject { ["k"] = "throw", ["value"] = new JsonObject {
                            ["k"] = "newClr", ["type"] = TypeJson.Fqn("System.InvalidCastException"), ["args"] = new JsonArray(),
                            ["memberRef"] = ClrMemberResolution.MemberRefJson(exception.GetConstructor(System.Type.EmptyTypes), "ctor", exception, null),
                        } }),
                    }),
                });
                result = physical;
            }
            else result = new JsonObject {
                // isInstRef returns a boxed nullable value, not an unboxed T at a value-type instantiation.
                ["k"] = "cond", ["type"] = TypeJson.Fqn(obj["k"]?.GetValue<string>() == "isInst" ? "kotlin.Boolean" : "object"),
                ["cond"] = matches, ["then"] = physical,
                ["else"] = obj["k"]?.GetValue<string>() == "isInst" ? Bool(false) : Null(),
            };
            obj.Clear();
            obj["k"] = "valueBlock"; obj["stmts"] = statements; obj["result"] = result;
        }
        Walk(root);
    }
}
