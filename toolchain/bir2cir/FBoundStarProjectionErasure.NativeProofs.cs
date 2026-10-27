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
        readonly Dictionary<string, Dictionary<int, List<TypeNode>>> lateLocalTypes = new(StringComparer.Ordinal);

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
                {
                    Register(method, false);
                    RegisterNativeOverrides(method, declaration);
                }
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
                if (obj["typeArgs"] is JsonArray methodArguments)
                {
                    var selectedOwner = TypeJson.Read(obj["calleeOwner"] ?? obj["ownerType"] ?? obj["owner"] ?? obj["type"]) as TypeNode.Fqn;
                    var identity = Str(obj[DeclarationIdentityBinding.Key]);
                    var selected = identity != null && selectedOwner != null
                        && _localDeclarations.TryGetValue((selectedOwner.Name, identity), out var local) ? local
                        : LocalMethod(identity);
                    var required = kind is "clrGenericStatic" or "clrGenericInstance"
                        ? ReadRows(ClrMemberResolution.SelectedGenericTypeParameters(obj, references), false,
                            physicalDeclaration: true)
                        : selected != null ? Rows(selected) : MethodRows(identity);
                    Require(required,
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

        void RegisterNativeOverrides(JsonObject method, JsonObject owner)
        {
            if (method["typeParams"] is not JsonArray { Count: > 0 } parameters) return;
            foreach (var edge in (method["overrides"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                if (TypeJson.Read(edge["owner"]) is not TypeNode.Fqn selectedOwner
                    || _valueSlots.IsBindingAlias(selectedOwner.Name)
                    || _valueSlots.OwnsValueSlots(selectedOwner.Name)) continue;
                if (!references.TrySelectedOverrideDeclaration(selectedOwner.Name, Str(edge["member"]),
                    Str(edge["kind"]) switch { "getter" => "get", "setter" => "set", _ => null },
                    parameters.Count, (method["params"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                        .Select(parameter => TypeJson.Read(parameter["type"])).ToArray(),
                    selectedOwner.Args, parameters,
                    Enumerable.Range(0, (owner["typeParams"] as JsonArray)?.Count ?? 0)
                        .Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray(),
                    IsSuspend(method), out var selected, selectedDeclarationId: Str(edge[DeclarationIdentityBinding.Key])))
                    throw new InvalidOperationException("Native generic override proof lacks its selected declaration");
                foreach (var (index, bounds) in ReadRows(selected.TypeParams, false, physicalDeclaration: true))
                    foreach (var bound in bounds)
                        Add(method, index, SupertypeGraph.SubstOwnerTvs(bound, selectedOwner.Args ?? Array.Empty<TypeNode>()));
            }
        }

        void Register(JsonObject declaration, bool nativeAncestors) =>
            rows[declaration] = ReadRows(declaration["typeParams"] as JsonArray, nativeAncestors,
                IsTypeDefinition(declaration) ? KotlinSupertypesRecord.ReadNullableFrame(declaration)
                    : OwnerConstrainedMethodLowering.ReadMethodFrame(declaration),
                projectsBound: !IsTypeDefinition(declaration)
                    ? (index, bound) => OwnerConstrainedMethodLowering.ProjectsBound(declaration, index, bound)
                    : Bool(declaration["generated"]) ? (index, bound) => ProjectsCapturedBound(declaration, index, bound) : null);

        internal Dictionary<int, List<TypeNode>> ReadRows(JsonArray parameters, bool nativeAncestors,
            NullableRepresentationFrame frame = null, bool physicalDeclaration = false,
            Func<int, TypeNode, bool> projectsBound = null)
        {
            var result = new Dictionary<int, List<TypeNode>>();
            for (var index = 0; index < (parameters?.Count ?? 0); index++)
            {
                if (frame != null && frame.PhysicalSlot(index).Representation
                    is not (NullableRepresentationFrame.Role.Ordinary or NullableRepresentationFrame.Role.Nullable)) continue;
                if (parameters[index] is JsonObject parameter && parameter["constraints"] is JsonArray constraints)
                {
                    var bounds = constraints.Select(TypeJson.Read).OfType<TypeNode.Fqn>()
                        // These rows seed the declaration's *physical* contract, not every source bound.
                        // Owner/dependent method bounds are projected by OwnerConstrainedMethodLowering; only
                        // a selected exact construction/call may demand their exact native proof again.
                        .Where(bound => projectsBound?.Invoke(index, bound) != true)
                        .SelectMany(bound => nativeAncestors
                            && OwnerConstrainedMethodLowering.HasConstructedDependency(bound, "type", index, frame)
                            ? NativeAncestors(bound) : (physicalDeclaration ? ExactPhysicalProof(bound) : ExactNative(bound))
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

        bool ExactPhysicalProof(TypeNode.Fqn bound) => !IsObjectish(bound) && !ContainsUseSiteProjection(bound)
            && !references.IsExistentialPhysicalOwner(bound.Name) && ProofType(bound) != null;

        // A physical GenericParamConstraint can name a DotKt TypeDef. Reading that exact metadata row must not
        // reclassify the Kotlin declaration as a foreign value operation, which ResolveNetType deliberately refuses.
        System.Type ProofType(TypeNode.Fqn bound) => references.ResolveForeignProjectionType(bound.Name, bound.Args)
            ?? references.ResolveRefType(bound.Name, bound.Args?.Length ?? 0);

        TypeNode.Fqn NativeIdentity(TypeNode.Fqn bound) => new(
            ProofType(bound).FullName, bound.Args);

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

        internal bool Add(JsonObject declaration, int index, TypeNode bound)
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

        // A physical slot/forwarder is authored from this exact method and retains its method frame. Transfer only
        // proofs that the new non-generic owner can name; a private storage body has its own, separate requirements.
        public void TransferIndependentMethodProofs(JsonObject source, JsonObject projection)
        {
            foreach (var (index, proofs) in Rows(source).ToArray())
                foreach (var proof in proofs.Where(proof => !ContainsOwnerTv(proof)).ToArray())
                    Add(projection, index, proof);
            if (Str(projection[DeclarationIdentityBinding.Key]) is string identity)
                methods[identity] = projection;
        }

        // Required native GenericParamConstraint rows are physical descriptors, not source value bounds.
        // Resolve cloned declarations by their authored identity, retaining the same proof across late walks.
        public bool IsPhysicalConstraint(JsonArray constraints, TypeNode bound)
        {
            if (constraints.Parent is not JsonObject parameter || !ReferenceEquals(parameter["constraints"], constraints)
                || parameter.Parent is not JsonArray parameters || parameters.Parent is not JsonObject declaration
                || !ReferenceEquals(declaration["typeParams"], parameters)) return false;
            var index = parameters.IndexOf(parameter);
            IReadOnlyDictionary<int, List<TypeNode>> required;
            if (rows.TryGetValue(declaration, out var current)) required = current;
            else if (IsTypeDefinition(declaration) && Str(declaration["name"]) is string owner)
                required = lateLocalTypes.TryGetValue(owner, out var physical) ? physical
                    : definitions.TryGetValue(owner, out var original) ? Rows(original)
                    : new Dictionary<int, List<TypeNode>>();
            else required = LocalMethod(Str(declaration[DeclarationIdentityBinding.Key])) is JsonObject method
                ? Rows(method) : new Dictionary<int, List<TypeNode>>();
            return required.TryGetValue(index, out var proofs) && proofs.Any(proof =>
                references.PhysicalConstraintType(proof) == references.PhysicalConstraintType(bound));
        }

        IReadOnlyDictionary<int, List<TypeNode>> TypeRows(string owner)
        {
            if (lateLocalTypes.TryGetValue(owner, out var physicalRows)) return physicalRows;
            if (definitions.TryGetValue(owner, out var local)) return Rows(local);
            if (!foreignTypes.TryGetValue(owner, out var constraints))
                foreignTypes[owner] = constraints = ReadRows(references.PhysicalOwnerTypeParamDeclarations(owner), false,
                    references.NullableTypeFrames.GetValueOrDefault(owner), physicalDeclaration: true);
            return constraints;
        }

        public void RegisterPhysicalLocalDefinitions(IReadOnlyDictionary<string, JsonObject> physicalDefinitions)
        {
            foreach (var (name, declaration) in physicalDefinitions)
                if (!definitions.ContainsKey(name))
                    // Late synthesis owns these already-physical rows. A same-named implementation in a reference
                    // assembly cannot supply their constraints; do not rerun source proof demand or projection.
                    lateLocalTypes[name] = ReadRows(declaration["typeParams"] as JsonArray, false,
                        KotlinSupertypesRecord.ReadNullableFrame(declaration), physicalDeclaration: true);
        }

        IReadOnlyDictionary<int, List<TypeNode>> MethodRows(string identity)
        {
            if (identity == null) return new Dictionary<int, List<TypeNode>>();
            if (!foreignMethods.TryGetValue(identity, out var constraints))
                foreignMethods[identity] = constraints = ReadRows(references.PhysicalMethodTypeParamDeclarations(identity), false,
                    references.NullableMethodFrame(identity), physicalDeclaration: true);
            return constraints;
        }

        public IEnumerable<TypeNode> Bounds(JsonObject declaration, int index) =>
            Rows(declaration).TryGetValue(index, out var constraints)
                ? constraints.Where(bound => !RetainedSourceProof(declaration, index, bound)
                    && !(bound is TypeNode.Fqn named && ProofType(named)?.IsClass == true
                        && constraints.OfType<TypeNode.Fqn>().Any(other => other != named
                            && ProofType(other)?.IsClass == true
                            && SupertypeGraph.Reaches(other, named, _sourceSupertypes, references))))
                : Enumerable.Empty<TypeNode>();

        bool RetainedSourceProof(JsonObject declaration, int index, TypeNode required)
        {
            if (required is not TypeNode.Fqn target || declaration["typeParams"]?[index] is not JsonObject parameter
                || parameter["constraints"] is not JsonArray constraints) return false;
            var typeDeclaration = IsTypeDefinition(declaration);
            var frame = typeDeclaration ? KotlinSupertypesRecord.ReadNullableFrame(declaration)
                : OwnerConstrainedMethodLowering.ReadMethodFrame(declaration);
            foreach (var bound in constraints.Select(TypeJson.Read).OfType<TypeNode.Fqn>())
            {
                var ownsClassifier = definitions.TryGetValue(bound.Name, out var local) && IsSourceGeneric(local)
                    || references.TryExistentialPhysicalOwner(bound.Name, out _);
                if (ContainsUseSiteProjection(bound)
                    || !typeDeclaration && ContainsOwnerTv(bound)
                    || OwnerConstrainedMethodLowering.HasConstructedDependency(bound,
                        typeDeclaration ? "type" : "method", index, frame) && (ownsClassifier || !typeDeclaration)) continue;
                var alias = references.ResolveForeignProjectionType(bound.Name, bound.Args);
                var existing = alias == null ? bound : new TypeNode.Fqn(alias.FullName,
                    alias.IsGenericTypeDefinition ? bound.Args : null);
                if (SupertypeGraph.Reaches(existing, target, _sourceSupertypes, references)) return true;
            }
            return false;
        }

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
            {"kind":"class","name":"SelfBase","typeParams":["T"],"base":{"t":"fqn","name":"System.IO.MemoryStream"}},
            {"kind":"class","name":"SelfOwner","typeParams":[
              {"name":"Self","constraints":[{"t":"fqn","name":"SelfBase","args":[{"t":"tv","scope":"type","i":0}]}]}]},
            {"kind":"class","name":"System.IO.MemoryStream"}]}
          """).AsObject();
        var declaration = root["types"][1].AsObject();
        KotlinSupertypesRecord.Merge(declaration, new JsonObject {
            [NullableRepresentationFrame.MetadataKey] = new NullableRepresentationFrame(2,
                Array.Empty<int>(), storageIndices: new[] { 1 }).ToJson(),
        });
        var original = root.DeepClone();
        var oldGraph = _sourceSupertypes;
        var oldDeclarations = _localDeclarations;
        var oldProofs = _nativeProofs;
        var oldValueSlots = _valueSlots;
        try
        {
            var roots = new[] { root };
            _sourceSupertypes = SupertypeGraph.Collect(roots);
            IndexLocalMethods(roots);
            var definitions = _sourceSupertypes.ToDictionary(pair => pair.Key, pair => pair.Value.Node);
            var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
            var plan = new NativeProofPlan(roots, definitions, refs);
            _nativeProofs = plan;
            _valueSlots = new GenericRepresentationPolicy(refs.Aliases, refs, roots);
            var constructionOwners = definitions.ToDictionary(pair => pair.Key, pair => new Owner {
                Name = pair.Key, ErasedName = pair.Key + "View", Def = pair.Value, Needed = true,
            });
            var projectedMarker = new TypeNode.Fqn("Marker", new TypeNode[] { new TypeNode.Star() });
            var requiredConstruction = new TypeNode.Fqn("Owner", new TypeNode[] {
                new TypeNode.Fqn("System.String"), projectedMarker, projectedMarker,
            });
            if (RewriteType(projectedMarker, constructionOwners, refs) != new TypeNode.Fqn("MarkerView")
                || !ConstructionHasHiddenWitness(requiredConstruction, constructionOwners, refs))
                throw new InvalidOperationException("A known carrier discarded its demanded native construction witness");
            var physical = new JsonArray(new JsonObject { ["name"] = "T", ["constraints"] =
                new JsonArray(TypeJson.Fqn("System.IO.MemoryStream")) });
            var dependentMethods = JsonNode.Parse("""
              ["Tag",{"name":"R","constraints":[{"t":"fqn","name":"System.Collections.Generic.IComparer`1",
                "args":[{"t":"tv","scope":"method","i":0}]}]},
               {"name":"S","constraints":[{"t":"fqn","name":"System.Collections.Generic.IComparer`1",
                "args":[{"t":"tv","scope":"type","i":0}]}]}]
              """).AsArray();
            var dependentMethod = new JsonObject { ["typeParams"] = dependentMethods.DeepClone() };
            var capturedFrame = new JsonObject { ["generated"] = true, ["typeParams"] = new JsonArray("Tag",
                new JsonObject { ["name"] = "R", ["constraints"] = new JsonArray(TypeJson.Write(
                    new TypeNode.Fqn("System.Collections.Generic.IComparer`1", new TypeNode[] { new TypeNode.Tv("type", 0) }))) }) };
            var overrideBound = new TypeNode.Fqn("System.IO.Stream");
            var overridingMethod = new JsonObject { [OwnerConstrainedMethodLowering.OverrideBoundsKey] =
                new JsonArray(new JsonObject { ["index"] = 0, ["bound"] = TypeJson.Write(overrideBound) }).ToJsonString() };
            var lateLocal = JsonNode.Parse("""
              {"kind":"class","name":"LateLocal","typeParams":["Tag",
                {"name":"T","constraints":[{"t":"fqn","name":"System.Collections.Generic.IEnumerable`1",
                  "args":[{"t":"tv","scope":"type","i":0}]}]},
                {"name":"Storage","constraints":[{"t":"fqn","name":"System.IDisposable"}]}]}
              """).AsObject();
            KotlinSupertypesRecord.Merge(lateLocal, new JsonObject {
                [NullableRepresentationFrame.MetadataKey] = new NullableRepresentationFrame(2,
                    Array.Empty<int>(), storageIndices: new[] { 1 }).ToJson(),
            });
            plan.RegisterPhysicalLocalDefinitions(new Dictionary<string, JsonObject> { ["LateLocal"] = lateLocal });
            var requiredBound = new TypeNode.Fqn("Marker", new TypeNode[] { new TypeNode.Tv("type", 0) });
            plan.Add(declaration, 1, requiredBound);
            var copiedOwner = declaration.DeepClone().AsObject();
            var copiedBounds = copiedOwner["typeParams"][1]["constraints"].AsArray();
            if (!plan.IsPhysicalConstraint(copiedBounds, requiredBound)
                || plan.IsPhysicalConstraint(copiedBounds, new TypeNode.Fqn("Unrequired"))
                || plan.IsPhysicalConstraint(new JsonArray(TypeJson.Write(requiredBound)), requiredBound))
                throw new InvalidOperationException("Native proof rows lost declaration/index ownership across cloning");
            var sourceMethod = root["methods"][0].AsObject();
            var methodProof = new TypeNode.Fqn("Marker", new TypeNode[] { new TypeNode.Tv("method", 0) });
            plan.Add(sourceMethod, 1, methodProof);
            var projectedMethod = new JsonObject { [DeclarationIdentityBinding.Key] = "authored-physical-slot",
                ["typeParams"] = sourceMethod["typeParams"].DeepClone() };
            plan.TransferIndependentMethodProofs(sourceMethod, projectedMethod);
            var copiedMethod = projectedMethod.DeepClone().AsObject();
            var privateBody = new JsonObject { [DeclarationIdentityBinding.Key] = "separate-private-storage-body",
                ["typeParams"] = sourceMethod["typeParams"].DeepClone() };
            plan.TransferIndependentMethodProofs(root["methods"][1].AsObject(), privateBody);
            if (!plan.IsPhysicalConstraint(projectedMethod["typeParams"][1]["constraints"].AsArray(), methodProof)
                || !plan.IsPhysicalConstraint(copiedMethod["typeParams"][1]["constraints"].AsArray(), methodProof)
                || plan.IsPhysicalConstraint(privateBody["typeParams"][1]["constraints"].AsArray(), methodProof))
                throw new InvalidOperationException("Authored physical method proof transfer changed a separate storage contract");
            if (!plan.ExactTypeArgument("Owner", 1) || plan.ExactTypeArgument("Owner", 2)
                || !plan.ExactTypeArgument("LateLocal", 1) || plan.ExactTypeArgument("LateLocal", 2)
                || plan.ExactTypeArgument("SelfOwner", 0)
                || plan.ReadRows(physical, false).Count != 0
                || !plan.ReadRows(physical, false, physicalDeclaration: true)[0]
                    .Contains(new TypeNode.Fqn("System.IO.MemoryStream"))
                || plan.ReadRows(dependentMethods, false, projectsBound:
                    (index, bound) => OwnerConstrainedMethodLowering.ProjectsBound(dependentMethod, index, bound)).Count != 0
                || plan.ReadRows(dependentMethods, false, physicalDeclaration: true).Count != 2
                || plan.ReadRows(capturedFrame["typeParams"].AsArray(), false, projectsBound:
                    (index, bound) => ProjectsCapturedBound(capturedFrame, index, bound)).Count != 0
                || !OwnerConstrainedMethodLowering.ProjectsBound(overridingMethod, 0, overrideBound)
                || !plan.Bounds(root["methods"][0].AsObject(), 1).Contains(new TypeNode.Fqn("System.IDisposable"))
                || plan.Bounds(root["methods"][1].AsObject(), 1).Any()
                || !JsonNode.DeepEquals(original, root))
                throw new InvalidOperationException("Native proof demand strengthened storage/pure forwarding or changed source facts");
        }
        finally {
            _sourceSupertypes = oldGraph; _localDeclarations = oldDeclarations;
            _nativeProofs = oldProofs; _valueSlots = oldValueSlots;
        }
    }
}
