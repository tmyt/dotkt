using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class FBoundStarProjectionErasure
{
    // A nominal Kotlin classifier does not imply its exact constructed native ancestors. Keep those proofs on
    // source-class witnesses, and propagate the proofs required by exact constructions/calls into their callers.
    // A pure Kotlin helper does not acquire native constraints merely because its source bound has native parents.
    sealed class NativeProofPlan
    {
        readonly ReferenceMetadataIndex references;
        readonly IReadOnlyDictionary<string, JsonObject> definitions;
        readonly Dictionary<JsonObject, Dictionary<int, List<TypeNode>>> rows = new();
        readonly Dictionary<string, JsonObject> methods;
        readonly Dictionary<string, Dictionary<int, List<TypeNode>>> foreignTypes = new(StringComparer.Ordinal);
        readonly Dictionary<string, Dictionary<int, List<TypeNode>>> foreignMethods = new(StringComparer.Ordinal);

        public NativeProofPlan(IReadOnlyList<JsonObject> roots,
            IReadOnlyDictionary<string, JsonObject> definitions, ReferenceMetadataIndex references)
        {
            this.references = references;
            this.definitions = definitions;
            methods = _localDeclarations.Values.GroupBy(method => Str(method[DeclarationIdentityBinding.Key]))
                .Where(group => group.Key != null && group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            foreach (var declaration in definitions.Values.Concat(roots))
            {
                Register(declaration, !Bool(declaration["generated"]));
                foreach (var method in (declaration["methods"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    Register(method, false);
            }
            bool changed;
            do
            {
                changed = false;
                foreach (var root in roots) Walk(root, root, null);
            } while (changed);

            void Walk(JsonNode node, JsonObject owner, JsonObject method)
            {
                if (node is JsonArray array)
                {
                    foreach (var child in array) Walk(child, owner, method);
                    return;
                }
                if (node is not JsonObject obj || obj["t"] != null) return;
                if (IsTypeDefinition(obj)) { owner = obj; method = null; }
                else if (obj["params"] is JsonArray && obj["body"] is JsonArray) method = obj;
                var kind = Str(obj["k"]);
                var construction = kind switch
                {
                    "new" or "newClr" => TypeJson.Read(obj["type"]) as TypeNode.Fqn,
                    "newClosure" => TypeJson.Read(obj["closureType"]) as TypeNode.Fqn,
                    "newSam" => TypeJson.Read(obj["samType"]) as TypeNode.Fqn,
                    _ => null,
                };
                if (construction?.Args is { } constructorArguments)
                    Require(TypeRows(construction.Name), constructorArguments, "type", null);
                if (obj["typeArgs"] is JsonArray methodArguments
                    && Str(obj[DeclarationIdentityBinding.Key]) is string identity)
                {
                    var selectedOwner = TypeJson.Read(obj["calleeOwner"] ?? obj["ownerType"] ?? obj["owner"]) as TypeNode.Fqn;
                    var selected = selectedOwner != null
                        && _localDeclarations.TryGetValue((selectedOwner.Name, identity), out var local) ? local
                        : LocalMethod(identity);
                    Require(selected != null ? Rows(selected)
                            : MethodRows(identity),
                        methodArguments.Select(TypeJson.Read).ToArray(), "method", selectedOwner?.Args);
                }
                foreach (var (key, child) in obj)
                    if (key is not ("attrs" or "typeParams" or "memberRef")) Walk(child, owner, method);

                void Require(IReadOnlyDictionary<int, List<TypeNode>> required, TypeNode[] arguments,
                    string scope, TypeNode[] ownerArguments)
                {
                    foreach (var (index, constraints) in required.ToArray())
                    {
                        if (index >= arguments.Length || arguments[index] is not TypeNode.Tv actual) continue;
                        var context = actual.Scope == "method" ? method : owner;
                        if (context == null) continue;
                        foreach (var constraint in constraints.ToArray())
                        {
                            var closed = scope == "type" ? SupertypeGraph.SubstOwnerTvs(constraint, arguments)
                                : SubstituteMethodTypeArguments(constraint, arguments);
                            if (scope == "method" && ownerArguments != null)
                                closed = SupertypeGraph.SubstOwnerTvs(closed, ownerArguments);
                            changed |= Add(context, actual.I, closed);
                        }
                    }
                }
            }
        }

        void Register(JsonObject declaration, bool nativeAncestors) =>
            rows[declaration] = ReadRows(declaration["typeParams"] as JsonArray, nativeAncestors,
                IsTypeDefinition(declaration) ? KotlinSupertypesRecord.ReadNullableFrame(declaration)
                    : OwnerConstrainedMethodLowering.ReadMethodFrame(declaration));

        Dictionary<int, List<TypeNode>> ReadRows(JsonArray parameters, bool nativeAncestors,
            NullableRepresentationFrame frame = null)
        {
            var result = new Dictionary<int, List<TypeNode>>();
            for (var index = 0; index < (parameters?.Count ?? 0); index++)
            {
                if (frame != null && frame.PhysicalSlot(index).Representation
                    is not (NullableRepresentationFrame.Role.Ordinary or NullableRepresentationFrame.Role.Nullable)) continue;
                if (parameters[index] is JsonObject parameter && parameter["constraints"] is JsonArray constraints)
                {
                    var bounds = constraints.Select(TypeJson.Read).OfType<TypeNode.Fqn>()
                        .SelectMany(bound => nativeAncestors
                            && OwnerConstrainedMethodLowering.HasConstructedDependency(bound, "type", index, frame)
                            ? NativeAncestors(bound) : ExactNative(bound)
                            ? new[] { NativeIdentity(bound) } : Enumerable.Empty<TypeNode.Fqn>()).Cast<TypeNode>().Distinct().ToList();
                    if (bounds.Count != 0) result[index] = bounds;
                }
            }
            return result;
        }

        bool ExactNative(TypeNode.Fqn bound)
        {
            if (IsObjectish(bound) || ContainsUseSiteProjection(bound)
                || definitions.ContainsKey(bound.Name) || references.HasDotKtOwner(bound.Name))
                return false;
            var type = references.ResolveForeignProjectionType(bound.Name, bound.Args);
            return type != null;
        }

        TypeNode.Fqn NativeIdentity(TypeNode.Fqn bound) => new(
            references.ResolveForeignProjectionType(bound.Name, bound.Args).FullName, bound.Args);

        IEnumerable<TypeNode.Fqn> NativeAncestors(TypeNode.Fqn bound)
        {
            var classBound = false;
            foreach (var edge in SupertypeGraph.Reachable(
                new SupertypeGraph.Def { Interfaces = new[] { bound } }, _sourceSupertypes, references))
            {
                if (!ExactNative(edge.spec)) continue;
                var type = references.ResolveForeignProjectionType(edge.spec.Name, edge.spec.Args);
                // ECMA-335 permits one class constraint; its ancestors are implied by that nearest class.
                if (!type.IsInterface)
                {
                    if (classBound) continue;
                    classBound = true;
                }
                yield return NativeIdentity(edge.spec);
            }
        }

        bool Add(JsonObject declaration, int index, TypeNode bound)
        {
            if (declaration["typeParams"] is not JsonArray parameters || index >= parameters.Count) return false;
            var frame = IsTypeDefinition(declaration) ? KotlinSupertypesRecord.ReadNullableFrame(declaration)
                : OwnerConstrainedMethodLowering.ReadMethodFrame(declaration);
            if (frame != null && frame.PhysicalSlot(index).Representation
                is not (NullableRepresentationFrame.Role.Ordinary or NullableRepresentationFrame.Role.Nullable)) return false;
            if (!rows.TryGetValue(declaration, out var slots)) rows[declaration] = slots = new();
            if (!slots.TryGetValue(index, out var constraints)) slots[index] = constraints = new();
            if (constraints.Contains(bound)) return false;
            constraints.Add(bound);
            return true;
        }

        IReadOnlyDictionary<int, List<TypeNode>> Rows(JsonObject declaration) =>
            rows.GetValueOrDefault(declaration) ?? new Dictionary<int, List<TypeNode>>();

        IReadOnlyDictionary<int, List<TypeNode>> TypeRows(string owner)
        {
            if (definitions.TryGetValue(owner, out var local)) return Rows(local);
            if (!foreignTypes.TryGetValue(owner, out var constraints))
                foreignTypes[owner] = constraints = ReadRows(references.OwnerTypeParamDeclarations(owner), false,
                    references.NullableTypeFrames.GetValueOrDefault(owner));
            return constraints;
        }

        IReadOnlyDictionary<int, List<TypeNode>> MethodRows(string identity)
        {
            if (identity == null) return new Dictionary<int, List<TypeNode>>();
            if (!foreignMethods.TryGetValue(identity, out var constraints))
                foreignMethods[identity] = constraints = ReadRows(references.MethodTypeParamDeclarations(identity), false,
                    references.NullableMethodFrame(identity));
            return constraints;
        }

        public IEnumerable<TypeNode> Bounds(JsonObject declaration, int index) =>
            Rows(declaration).TryGetValue(index, out var constraints) ? constraints : Enumerable.Empty<TypeNode>();

        public bool ExactTypeArgument(string owner, int index) => TypeRows(owner).ContainsKey(index);

        public JsonObject LocalMethod(string identity) => identity == null ? null : methods.GetValueOrDefault(identity);

        public bool ExactMethodArgument(JsonObject declaration, string identity, int index) => declaration != null
            ? Rows(declaration).ContainsKey(index)
            : MethodRows(identity).ContainsKey(index);
    }

    static NativeProofPlan _nativeProofs;

    static void NativeProofSelfTest()
    {
        var root = JsonNode.Parse("""
          {"fileClass":"ProofTests","methods":[
            {"name":"construct","declarationId":"construct","params":[],"typeParams":["Tag",
              {"name":"T","constraints":[{"t":"fqn","name":"Marker","args":[{"t":"tv","scope":"method","i":0}]}]}],
             "body":[{"k":"new","type":{"t":"fqn","name":"Owner","args":[
               {"t":"tv","scope":"method","i":0},{"t":"tv","scope":"method","i":1},{"t":"tv","scope":"method","i":1}]}}]},
            {"name":"pure","declarationId":"pure","params":[],"typeParams":["Tag",
              {"name":"T","constraints":[{"t":"fqn","name":"Marker","args":[{"t":"tv","scope":"method","i":0}]}]}],"body":[]}],
           "types":[
            {"kind":"interface","name":"Marker","typeParams":["Tag"],"interfaces":[{"t":"fqn","name":"System.IDisposable"}]},
            {"kind":"class","name":"Owner","typeParams":["Tag",
              {"name":"T","constraints":[{"t":"fqn","name":"Marker","args":[{"t":"tv","scope":"type","i":0}]}]},
              {"name":"Storage","constraints":[{"t":"fqn","name":"Marker","args":[{"t":"star"}]}]}]},
            {"kind":"class","name":"SelfBase","typeParams":["T"],"base":{"t":"fqn","name":"System.Exception"}},
            {"kind":"class","name":"SelfOwner","typeParams":[
              {"name":"Self","constraints":[{"t":"fqn","name":"SelfBase","args":[{"t":"tv","scope":"type","i":0}]}]}]}]}
          """).AsObject();
        var declaration = root["types"][1].AsObject();
        KotlinSupertypesRecord.Merge(declaration, new JsonObject {
            [NullableRepresentationFrame.MetadataKey] = new NullableRepresentationFrame(2,
                Array.Empty<int>(), storageIndices: new[] { 1 }).ToJson(),
        });
        var original = root.DeepClone();
        var oldGraph = _sourceSupertypes;
        var oldDeclarations = _localDeclarations;
        try
        {
            var roots = new[] { root };
            _sourceSupertypes = SupertypeGraph.Collect(roots);
            IndexLocalMethods(roots);
            var definitions = _sourceSupertypes.ToDictionary(pair => pair.Key, pair => pair.Value.Node);
            var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
            var plan = new NativeProofPlan(roots, definitions, refs);
            if (!plan.ExactTypeArgument("Owner", 1) || plan.ExactTypeArgument("Owner", 2)
                || plan.ExactTypeArgument("SelfOwner", 0)
                || !plan.Bounds(root["methods"][0].AsObject(), 1).Contains(new TypeNode.Fqn("System.IDisposable"))
                || plan.Bounds(root["methods"][1].AsObject(), 1).Any()
                || !JsonNode.DeepEquals(original, root))
                throw new InvalidOperationException("Native proof demand strengthened storage/pure forwarding or changed source facts");
        }
        finally { _sourceSupertypes = oldGraph; _localDeclarations = oldDeclarations; }
    }
}
