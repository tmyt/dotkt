using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Cir;

// CLR variance is a property of the complete physical declaration, not just its method returns.
// Source variance is preserved by RoundtripMetadata before this pass weakens the GenericParam flags.
// The same oracle serves representation decisions and final CIR so consumers cannot rely on a
// variant conversion that the emitted interface's inherited invariant faces prohibit.
static class ClrTypeVariance
{
    internal static CirFileDeclaration[] ApplyAll(IReadOnlyList<CirFileDeclaration> roots, ReferenceMetadataIndex refs)
    {
        var definitions = new Dictionary<string, ClrVarianceFacts.Declaration>(StringComparer.Ordinal);
        foreach (var definition in roots.SelectMany(root => root.Types))
            definitions.TryAdd(definition.Name, ClrVarianceFacts.FromCir(definition));
        var analysis = new Analysis(definitions, refs);
        CirTypeDeclaration Project(CirTypeDeclaration definition)
        {
            var offset = definition.CapturedTypeParams.OrEmpty().Length;
            var parameters = definition.TypeParams.OrEmpty();
            return definition with {
                CapturedTypeParams = definition.CapturedTypeParams.HasValue
                    ? definition.CapturedTypeParams.Value.Select(parameter => parameter with { Variance = default }).ToArray()
                    : definition.CapturedTypeParams,
                TypeParams = definition.TypeParams.HasValue ? parameters.Select((parameter, index) =>
                    parameter.Variance.OrDefault() is "in" or "out"
                    && analysis.Get(definition.Name, offset + parameters.Length, offset + index) == null
                        ? parameter with { Variance = default } : parameter).ToArray() : definition.TypeParams,
            };
        }
        return roots.Select(root => root with { Types = root.Types.Select(Project).ToArray() }).ToArray();
    }

    internal static string Get(string name, int arity, int index,
        IReadOnlyDictionary<string, JsonObject> definitions, ReferenceMetadataIndex refs) =>
        new Analysis(owner => definitions.TryGetValue(owner, out var definition)
            ? ClrVarianceFacts.FromDom(definition) : null, refs).Get(name, arity, index);

    internal sealed class Analysis
    {
        readonly Func<string, ClrVarianceFacts.Declaration> resolve;
        readonly ReferenceMetadataIndex refs;
        readonly Dictionary<string, ClrVarianceFacts.Declaration> definitions = new(StringComparer.Ordinal);

        internal Analysis(IReadOnlyDictionary<string, ClrVarianceFacts.Declaration> definitions, ReferenceMetadataIndex refs)
            : this(name => definitions.TryGetValue(name, out var value) ? value : null, refs) { }

        internal Analysis(Func<string, ClrVarianceFacts.Declaration> resolve, ReferenceMetadataIndex refs)
        {
            this.resolve = resolve;
            this.refs = refs;
        }

        ClrVarianceFacts.Declaration Definition(string name)
        {
            if (!definitions.TryGetValue(name, out var value)) definitions[name] = value = resolve(name);
            return value;
        }

        sealed class Candidate(ClrVarianceFacts.Declaration definition, int position, int variance)
        {
            public readonly ClrVarianceFacts.Declaration Definition = definition;
            public readonly int Position = position, Variance = variance;
            public readonly HashSet<Candidate> Dependents = new();
            public bool Invalid;
        }

        readonly Dictionary<(string, int), Candidate> candidates = new();
        readonly Dictionary<(string, int, int), int> external = new();
        readonly Queue<Candidate> pending = new(), invalid = new();

        int Declared(string name, int arity, int index)
        {
            if (Definition(name) is { } local)
            {
                var ownIndex = index - local.CapturedCount;
                if (local.Kind is not ("interface" or "delegate")
                    || ownIndex < 0 || ownIndex >= local.Parameters.Count) return 0;
                return local.Parameters[ownIndex] switch { "out" => 1, "in" => -1, _ => 0 };
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
                candidates.Add(key, candidate = new Candidate(Definition(name), position, variance));
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
            if (Definition(name) != null)
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
            return candidate.Definition.Occurrences.Any(slot => Conflicts(slot.Type, candidate, slot.Context));
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
                        if (Definition(application.Name) != null)
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
        CirFileDeclaration Fixture(JsonObject root)
        {
            // Supply complete physical declarations around these signature-only
            // test facts. These are fixture defaults, not compiler inference.
            root["fileClass"] = "VarianceFixture";
            root["hasMain"] = false;
            root["methods"] = new JsonArray();
            root["fields"] = new JsonArray();
            foreach (var owner in root["types"].AsArray().OfType<JsonObject>())
                if (owner["methods"] is JsonArray methods)
                    for (var index = 0; index < methods.Count; index++)
                    {
                        var method = methods[index].AsObject();
                        method["name"] = "slot" + index;
                        method["ret"] ??= TypeJson.Fqn("void");
                        method["params"] ??= new JsonArray();
                    }
            return CirDeclarationProjection.ReadCirFileDeclaration(root);
        }

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
               "methods":[{"typeParams":[{"name":"U","constraints":[{"t":"tv","scope":"type","i":0}]}]}]},
              {"name":"CapturedOutput","kind":"interface","capturedTypeParams":[{"name":"Outer","variance":"out"}],
               "typeParams":[{"name":"T","variance":"out"}],"methods":[{"ret":{"t":"tv","scope":"type","i":1}}]},
              {"name":"StaticInput","kind":"interface","typeParams":[{"name":"T","variance":"out"}],
               "methods":[{"static":true,"params":[{"type":{"t":"tv","scope":"type","i":0}}]}]},
              {"name":"StaticVirtualInput","kind":"interface","typeParams":[{"name":"T","variance":"out"}],
               "methods":[{"static":true,"virtual":true,"params":[{"type":{"t":"tv","scope":"type","i":0}}]}]},
              {"name":"StaticAbstractInput","kind":"interface","typeParams":[{"name":"T","variance":"out"}],
               "methods":[{"static":true,"abstract":true,"params":[{"type":{"t":"tv","scope":"type","i":0}}]}]},
              {"name":"DelegateInput","kind":"delegate","typeParams":[{"name":"T","variance":"in"}],
               "params":[{"type":{"t":"tv","scope":"type","i":0}}]},
              {"name":"DelegateConflict","kind":"delegate","typeParams":[{"name":"T","variance":"out"}],
               "params":[{"type":{"t":"tv","scope":"type","i":0}}]}
            ]}
            """;
        foreach (var reverse in new[] { false, true })
        {
            var root = JsonNode.Parse(declarations).AsObject();
            if (reverse) root["types"] = new JsonArray(((JsonArray)root["types"]).Reverse().Select(type => type.DeepClone()).ToArray());
            var projected = ApplyAll(new[] { Fixture(root) }, null).Single();
            foreach (var type in projected.Types)
            {
                var name = type.Name;
                var expected = name is "SafeOutput" or "SafeInherited" or "RecursiveOutput" or "CapturedOutput" or "StaticInput" ? "out"
                    : name is "SafeConstraint" or "DelegateInput" ? "in" : null;
                if (type.TypeParams.Value[0].Variance.OrDefault() != expected)
                    throw new InvalidOperationException($"CLR variance self-test failed for {name} (reverse={reverse})");
                if (type.CapturedTypeParams.OrEmpty().Any(parameter => parameter.Variance.IsPresent))
                    throw new InvalidOperationException("Captured CLR type parameters retained variance");
            }
        }
        // An analysis owns signature facts, not a view over its still-mutable declaration.
        var source = JsonNode.Parse(declarations)["types"].AsArray().OfType<JsonObject>()
            .Single(type => Str(type["name"]) == "SafeOutput");
        var facts = ClrVarianceFacts.FromDom(source);
        source["typeParams"][0]["variance"] = "in";
        source["methods"][0]["ret"] = TypeJson.Write(new TypeNode.Fqn("System.Int32"));
        var reads = 0;
        var snapshot = new Analysis(name => { reads++; return name == "SafeOutput" ? facts : null; }, null);
        if (snapshot.Get("SafeOutput", 1, 0) != "out" || snapshot.Get("SafeOutput", 1, 0) != "out" || reads != 1)
            throw new InvalidOperationException("CLR variance facts were not independent, cached signature snapshots");
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
            var projected = ApplyAll(new[] { Fixture(new JsonObject { ["types"] = types }) }, null).Single();
            if (projected.Types.Any(type => type.TypeParams.Value[0].Variance.OrDefault() != (broken ? null : "out")))
                throw new InvalidOperationException("CLR variance dense recursive graph self-test failed");
        }
        Console.WriteLine("[CLR variance] self-test OK (inherited invariance, transitive signatures, order, dense recursion, method constraints, captured/static/delegate slots, owning snapshots)");
    }

    static string Str(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;
}
