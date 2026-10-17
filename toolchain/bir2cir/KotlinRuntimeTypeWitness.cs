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
    internal const string PredicateKey = "kotlinClassifierPredicate";
    internal static TypeNode PredicateType => new TypeNode.Fqn("System.Func", new TypeNode[] {
        new TypeNode.Fqn("System.Object"), new TypeNode.Fqn("System.Object"),
    });
    internal static ValueRepresentationLowering ValueRepresentations { get; set; }
    internal static TypeNode Type => new TypeNode.Fqn("System.ValueTuple", new TypeNode[] {
        new TypeNode.Fqn("System.Int32"), new TypeNode.Fqn("System.Type"), PredicateType,
    });
    internal static JsonNode TypeJsonNode() => TypeJson.Write(Type);

    internal static JsonObject Flags(JsonNode witness) => Field(witness, "Item1", new TypeNode.Fqn("System.Int32"));
    internal static JsonObject Classifier(JsonNode witness) => Field(witness, "Item2", new TypeNode.Fqn("System.Type"));
    internal static JsonObject Predicate(JsonNode witness) => Field(witness, "Item3", PredicateType);
    static JsonObject Field(JsonNode witness, string name, TypeNode result) => new() {
        ["k"] = "field", ["ownerType"] = TypeJsonNode(), ["recv"] = witness.DeepClone(),
        ["name"] = name, ["type"] = TypeJson.Write(result), ["sty"] = TypeJson.Write(result),
    };

    internal static JsonNode Create(JsonNode flags, JsonNode classifier, JsonNode predicate) => new JsonObject {
        ["k"] = "new", ["type"] = TypeJsonNode(),
        ["args"] = new JsonArray(flags, classifier, predicate),
        ["argTypes"] = new JsonArray(TypeJson.Fqn("System.Int32"), TypeJson.Fqn("System.Type"), TypeJson.Write(PredicateType)),
    };

    internal static JsonNode Known(TypeNode type) => Create(KotlinTypeWitness.Constant(KotlinTypeWitness.Flags(type)),
        new JsonObject { ["k"] = "classRef", ["type"] = TypeJson.Write(type),
            [ClassifierKey] = true },
        ValueRepresentations?.Predicate(type) ?? new JsonObject {
            ["k"] = "const", ["type"] = TypeJson.Write(PredicateType), ["value"] = null,
        });

    internal static JsonNode Nullable(JsonNode witness) => Create(
        KotlinTypeWitness.Binary("|", Flags(witness), KotlinTypeWitness.Constant(1)), Classifier(witness), Predicate(witness));

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
            var predicate = obj[PredicateKey]?.DeepClone()
                ?? throw new InvalidOperationException("Runtime classifier has no predicate descriptor");
            obj.Remove(PredicateKey);
            var flags = obj[KotlinTypeWitness.OperandKey]?.DeepClone()
                ?? throw new InvalidOperationException("Runtime classifier has no Kotlin type flags");
            string name;
            do name = "dotkt$runtimeClassifier$value$" + next++; while (!names.Add(name));
            var any = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
            JsonObject Local() => new() { ["k"] = "local", ["name"] = name, ["sty"] = TypeJson.Write(any) };
            JsonObject Null() => new() { ["k"] = "const", ["type"] = TypeJson.Write(any), ["value"] = null };
            JsonObject Bool(bool value) => new() { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Boolean"), ["value"] = value };
            JsonObject NativeCall(string owner, string method, JsonNode receiver, string result,
                string[] parameters, params JsonNode[] arguments) => new() {
                ["k"] = "clrInstance", ["type"] = TypeJson.Fqn(owner), ["method"] = method,
                ["recv"] = receiver.DeepClone(), ["args"] = new JsonArray(arguments),
                ["argTypes"] = new JsonArray(parameters.Select(TypeJson.Fqn).ToArray()),
                ["ret"] = TypeJson.Fqn(result), ["dispatch"] = "callvirt",
                ["memberRef"] = ClrMemberResolution.ArrayStorageMethodRef(refs, method, parameters, owner, false),
            };
            // A foreign opaque construction is represented by its open definition, not Object or G<Object>.
            // Use the existing raw-classifier membership operation for that token; exact tokens keep CLR is-instance.
            var test = new JsonObject {
                ["k"] = "cond", ["type"] = TypeJson.Fqn("kotlin.Boolean"),
                ["cond"] = NativeCall("System.Type", "get_IsGenericTypeDefinition", classifier, "System.Boolean", Array.Empty<string>()),
                ["then"] = new JsonObject {
                    ["k"] = "callStatic", ["owner"] = TypeJson.Fqn("DotKt.Runtime.CompilerServices.StarProjectionRuntimeKt"),
                    ["method"] = "starProjectionHasView",
                    ["sig"] = new JsonArray(TypeJson.Fqn("System.Type"), TypeJson.Fqn("System.Type")),
                    ["ret"] = TypeJson.Fqn("kotlin.Boolean"),
                    ["args"] = new JsonArray(
                        NativeCall("System.Object", "GetType", Local(), "System.Type", Array.Empty<string>()), classifier.DeepClone()),
                },
                ["else"] = NativeCall("System.Type", "IsInstanceOfType", classifier, "System.Boolean", new[] { "System.Object" }, Local()),
            };
            // The existing nominal/physical operation owns nullability, including checked casts' Kotlin exception.
            // Collection membership also belongs to that composite guard, not to a native interface token.
            var matches = new JsonObject {
                ["k"] = "cond", ["cond"] = new JsonObject { ["k"] = "objEq", ["lhs"] = Local(), ["rhs"] = Null() },
                ["then"] = Bool(true),
                ["else"] = new JsonObject {
                    ["k"] = "cond", ["cond"] = KotlinTypeWitness.Binary("!=",
                        KotlinTypeWitness.Binary("&", flags.DeepClone(), KotlinTypeWitness.Constant(-2)), KotlinTypeWitness.Constant(0)),
                    ["then"] = Bool(true), ["else"] = new JsonObject {
                        ["k"] = "cond", ["type"] = TypeJson.Fqn("kotlin.Boolean"),
                        ["cond"] = new JsonObject { ["k"] = "objEq", ["lhs"] = predicate.DeepClone(), ["rhs"] = Null() },
                        ["then"] = test,
                        ["else"] = new JsonObject { ["k"] = "cond", ["type"] = TypeJson.Fqn("kotlin.Boolean"),
                            ["cond"] = new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Fqn("kotlin.Boolean"),
                                ["e"] = new JsonObject {
                                    ["k"] = "delegateInvoke", ["funcType"] = TypeJson.Write(new TypeNode.Fn(false,
                                        new TypeNode.Fqn("System.Object"), new[] { new TypeNode.Fqn("System.Object") })),
                                    ["recv"] = predicate.DeepClone(), ["args"] = new JsonArray(Local()),
                                },
                            },
                            ["then"] = test.DeepClone(), ["else"] = Bool(false),
                        },
                    },
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
                var physicalTest = (JsonObject)physical.DeepClone();
                physicalTest["k"] = "isInst";
                physicalTest["sty"] = TypeJson.Fqn("kotlin.Boolean");
                // A semantic superclass predicate cannot promise an impossible physical array conversion.
                // Null rejection remains owned by the original cast's Kotlin nullability guard.
                var semanticCastMatches = new JsonObject {
                    ["k"] = "cond", ["type"] = TypeJson.Fqn("kotlin.Boolean"), ["cond"] = matches,
                    ["then"] = new JsonObject { ["k"] = "cond", ["type"] = TypeJson.Fqn("kotlin.Boolean"),
                        ["cond"] = new JsonObject { ["k"] = "objEq", ["lhs"] = Local(), ["rhs"] = Null() },
                        ["then"] = Bool(true), ["else"] = physicalTest },
                    ["else"] = Bool(false),
                };
                // Without a semantic predicate, preserve the original matches-only preguard exactly.
                // In particular the collection composite cast owns its membership exception, not its is-test.
                var castMatches = new JsonObject {
                    ["k"] = "cond", ["type"] = TypeJson.Fqn("kotlin.Boolean"),
                    ["cond"] = new JsonObject { ["k"] = "objEq", ["lhs"] = predicate.DeepClone(), ["rhs"] = Null() },
                    ["then"] = matches.DeepClone(), ["else"] = semanticCastMatches,
                };
                var nativeException = refs.ResolveRefType("System.InvalidCastException");
                statements.Add(new JsonObject {
                    ["k"] = "if", ["branches"] = new JsonArray(new JsonObject {
                        ["cond"] = new JsonObject { ["k"] = "unaryOp", ["op"] = "!", ["e"] = castMatches },
                        ["body"] = new JsonArray(new JsonObject {
                            // Existing CLR-token classifiers keep their native checked-cast exception contract.
                            // Only the explicit semantic-superclass predicate selects the new Kotlin guard.
                            ["k"] = "if", ["branches"] = new JsonArray(new JsonObject {
                                ["cond"] = new JsonObject { ["k"] = "objEq", ["lhs"] = predicate.DeepClone(), ["rhs"] = Null() },
                                ["body"] = new JsonArray(new JsonObject { ["k"] = "throw", ["value"] = new JsonObject {
                                    ["k"] = "newClr", ["type"] = TypeJson.Fqn("System.InvalidCastException"), ["args"] = new JsonArray(),
                                    ["memberRef"] = ClrMemberResolution.MemberRefJson(nativeException.GetConstructor(System.Type.EmptyTypes),
                                        "ctor", nativeException, null),
                                } }),
                            }),
                        }, new JsonObject { ["k"] = "throw", ["value"] = new JsonObject {
                            ["k"] = "new", ["type"] = TypeJson.Fqn("kotlin.ClassCastException"),
                            ["args"] = new JsonArray(), ["argTypes"] = new JsonArray(),
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
