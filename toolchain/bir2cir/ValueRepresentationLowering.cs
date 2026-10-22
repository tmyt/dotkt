using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

sealed record ValueRepresentationBinding(string Classifier, string ClassifierFactory, string ComponentClassifier, string InterfaceMarker);

// Declaration-owned Kotlin classifiers can be narrower than their CLR alias face.
// The trusted stdlib supplies all type/member/classifier choices. No source name selects a rule.
sealed class ValueRepresentationLowering
{
    internal const string TypeAttribute = "kotlin.clr.ClrValueRepresentation";
    internal const string MemberAttribute = "kotlin.clr.ClrValueMember";
    readonly Dictionary<string, ValueRepresentationBinding> bindings = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> aliases;
    readonly Dictionary<string, JsonArray> localParameters = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> adapters = new(StringComparer.Ordinal);
    readonly Dictionary<string, (string Owner, JsonObject Method, string Slot)> members = new(StringComparer.Ordinal);
    readonly Dictionary<string, (string Owner, JsonObject Method)> locals = new(StringComparer.Ordinal);
    readonly Dictionary<string, SupertypeGraph.Def> sourceHierarchy;
    IReadOnlyDictionary<string, IReadOnlyList<string>> interfaceMarkers;
    readonly ReferenceMetadataIndex refs;
    readonly bool referenceBuild;

    static string Text(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;
    static bool Bool(JsonNode node) => node is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    static JsonObject Attribute(JsonObject declaration, string name) =>
        (declaration["attrs"] as JsonArray)?.OfType<JsonObject>()
            .SingleOrDefault(attribute => TypeJson.OwnerName(attribute["attr"]) == name);
    static string Argument(JsonObject attribute, int index) => Text(attribute?["args"]?[index]?["value"]);

    internal ValueRepresentationLowering(IReadOnlyList<JsonNode> roots, ReferenceMetadataIndex references, bool refBuild)
    {
        refs = references;
        referenceBuild = refBuild;
        sourceHierarchy = SupertypeGraph.Collect(roots);
        aliases = new Dictionary<string, string>(refs?.Aliases ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var assembly in refs?.Assemblies ?? Array.Empty<ReferenceAssembly>())
            foreach (var (name, binding) in assembly.DotKt.ValueRepresentations) bindings[name] = binding;
        foreach (var member in refs?.ValueMembers ?? Array.Empty<MemberBinding>())
        {
            members[member.DeclarationId] = (member.Owner, new JsonObject {
                ["name"] = member.SourcePropertyName ?? member.DeclarationSourceName ?? member.Name,
                [KotlinPropertyAccessors.KindKey] = member.AccessorKind,
                [DeclarationIdentityBinding.Key] = member.DeclarationId,
                ["abstract"] = member.IsAbstract,
                ["params"] = new JsonArray((member.DeclarationSemanticParams ?? member.ParamTypeNodes)
                    .Select((type, index) => (JsonNode)new JsonObject { ["name"] = "p" + index, ["type"] = TypeJson.Write(type) }).ToArray()),
                ["ret"] = TypeJson.Write(member.DeclarationSemanticReturn ?? member.ReturnTypeNode),
            }, member.ValueSlot);
        }
        void Index(JsonObject declaration)
        {
            if (Text(declaration["name"]) is string name)
            {
                if (declaration["typeParams"] is JsonArray parameters) localParameters[name] = parameters;
                var attribute = Attribute(declaration, TypeAttribute);
                var source = attribute == null ? name : Argument(attribute, 4) switch { "" => name, var target => target };
                if (Attribute(declaration, "kotlin.clr.ClrTypeAlias") is { } alias)
                {
                    aliases[name] = Argument(alias, 0);
                    aliases[source] = Argument(alias, 0);
                }
                if (attribute != null)
                    bindings[source] = new(Argument(attribute, 0), Argument(attribute, 1), Argument(attribute, 2), Argument(attribute, 3));
            }
            var owner = Text(declaration["fileClass"]) ?? Text(declaration["name"]);
            foreach (var method in (declaration["methods"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (Text(method[DeclarationIdentityBinding.Key]) is string id)
                {
                    locals[id] = (owner, method);
                    if (Attribute(method, MemberAttribute) is { } marker)
                    {
                        adapters[id] = Argument(marker, 0);
                        members[id] = (owner, method, Argument(marker, 1));
                    }
                }
            foreach (var type in (declaration["types"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()) Index(type);
        }
        foreach (var root in roots.OfType<JsonObject>()) Index(root);
    }

    internal JsonNode Predicate(TypeNode type)
    {
        var (binding, shape) = Classifier(type);
        return !referenceBuild && binding != null
            ? Helper(binding.ClassifierFactory, new[] { Shape(shape) }, new[] { new TypeNode.Fqn("kotlin.String") },
                KotlinRuntimeTypeWitness.PredicateType) : null;
    }

    internal bool HasClassifier(TypeNode type) => Classifier(type).Binding != null;

    internal IReadOnlyDictionary<string, IReadOnlyList<string>> InterfaceMarkers
    {
        get
        {
            if (interfaceMarkers != null) return interfaceMarkers;
            var direct = bindings.Where(entry => !string.IsNullOrEmpty(entry.Value.InterfaceMarker))
                .ToDictionary(entry => entry.Key, entry => entry.Value.InterfaceMarker, StringComparer.Ordinal);
            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var source in direct.Keys)
            {
                var arity = sourceHierarchy.TryGetValue(source, out var declaration) ? declaration.Arity : refs?.OwnerArity(source) ?? 0;
                var arguments = arity == 0 ? null : Enumerable.Range(0, arity).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray();
                // Seed the shared source-graph walk with the declaration itself. A logical interface edge owes
                // every mapped ancestor facet, not a fresh copy of an ancestor's CLR method declarations.
                var seed = new SupertypeGraph.Def { Name = source,
                    Interfaces = new[] { new TypeNode.Fqn(source, arguments) } };
                result[source] = SupertypeGraph.Reachable(seed, sourceHierarchy, refs)
                    .Select(edge => direct.GetValueOrDefault(edge.spec.Name)).Where(marker => marker != null)
                    .Distinct(StringComparer.Ordinal).ToArray();
            }
            return interfaceMarkers = result;
        }
    }

    string SelectedAdapter(JsonObject operation)
    {
        string Adapter(string id) => id == null ? null : adapters.GetValueOrDefault(id) ?? refs?.ValueAdapter(id);
        if (Text(operation[DeclarationIdentityBinding.Key]) is string selected) return Adapter(selected);
        if (operation["overrides"] is not JsonArray contracts) return null;
        // A fake override has no declaration of its own. The frontend states its exact inherited contracts;
        // use a unique adapter among those identities, never reconstruct a member from its owner/name.
        var inherited = contracts.OfType<JsonObject>()
            .Select(marker => Adapter(Text(marker[DeclarationIdentityBinding.Key])))
            .Where(target => target != null).Distinct(StringComparer.Ordinal).ToArray();
        return inherited.Length == 1 ? inherited[0] : null;
    }

    bool OpaqueScalar(TypeNode type)
    {
        var (binding, shape) = Classifier(type);
        if (binding == null || shape.Length != 0) return false;
        while (type is TypeNode.Nullable or TypeNode.Oblivious)
            type = type is TypeNode.Nullable nullable ? nullable.Of : ((TypeNode.Oblivious)type).Of;
        return type is TypeNode.Fqn application && AliasVarianceRepresentation.RequiresErasure(application, refs,
            localParameters.GetValueOrDefault(application.Name), aliases.GetValueOrDefault(application.Name));
    }

    // The predicate owns logical membership; the token describes only the admitted
    // value's physical slot. Reified checks must not reopen a native generic face.
    internal TypeNode ClassifierType(TypeNode type) => OpaqueScalar(type) ? new TypeNode.Fqn("kotlin.Any") : type;

    internal JsonNode ComponentPredicate(TypeNode type, JsonNode actualType)
    {
        var (binding, shape) = Classifier(type);
        return !referenceBuild && binding != null && shape.Length == 0
            ? Helper(binding.ComponentClassifier, new[] { actualType },
                new TypeNode[] { new TypeNode.Fqn("DotKt.Runtime.CompilerServices.StarProjectionType") }, new TypeNode.Fqn("kotlin.Boolean"))
            : null;
    }

    static JsonObject GenericDeclaration(JsonObject operation, string scope)
    {
        for (JsonNode parent = operation.Parent; parent != null; parent = parent.Parent)
            if (parent is JsonObject owner && (scope == "method"
                ? owner["k"] == null && owner["params"] is JsonArray && owner["body"] is JsonArray
                : Text(owner["kind"]) is "class" or "interface" or "enum" or "struct"))
                return owner;
        return null;
    }

    static NullableRepresentationFrame GenericFrame(JsonObject declaration, string scope) => declaration == null ? null
        : scope == "method" ? NullableRepresentationTypes.DeclarationMethodFrame(declaration)
            : KotlinSupertypesRecord.ReadNullableFrame(declaration);

    TypeNode SemanticBound(JsonObject operation, TypeNode.Tv variable, HashSet<TypeNode.Tv> visited)
    {
        if (!visited.Add(variable)) return null;
        var declaration = GenericDeclaration(operation, variable.Scope);
        if (declaration == null) return null;
        var frame = GenericFrame(declaration, variable.Scope);
        var sourceIndex = frame?.PhysicalSlot(variable.I).SourceIndex ?? variable.I;
        var sourceKey = variable.Scope == "method" ? NullableGenericErasure.MethodTypeParameterBoundsPre : KotlinSupertypesRecord.PreKey;
        var bounds = Text(declaration[sourceKey]) is string encoded
            ? JsonNode.Parse(encoded)?["bounds"]?[sourceIndex.ToString()] as JsonArray : null;
        var parameter = (declaration["typeParams"] as JsonArray)?[variable.I] as JsonObject;
        bounds ??= parameter?["constraints"] as JsonArray;
        foreach (var bound in (bounds ?? new JsonArray()).Select(TypeJson.Read))
        {
            if (HasClassifier(bound)) return bound;
            if (bound is TypeNode.Tv parent)
            {
                var parentFrame = GenericFrame(GenericDeclaration(operation, parent.Scope), parent.Scope);
                var physical = parentFrame?.Variable(parent, NullableRepresentationFrame.Role.Ordinary) as TypeNode.Tv ?? parent;
                if (SemanticBound(operation, physical, visited) is { } inherited) return inherited;
            }
        }
        return null;
    }

    (ValueRepresentationBinding Binding, string Shape) Classifier(TypeNode type)
    {
        var shape = "";
        for (;;)
            switch (type)
            {
                case TypeNode.Nullable nullable: type = nullable.Of; break;
                case TypeNode.Oblivious oblivious: type = oblivious.Of; break;
                case TypeNode.Projection { Variance: "out" } projection: type = projection.Of; break;
                case TypeNode.Array { SzArray: true, Rank: 1 } array:
                    shape += GenericArrayStorageLowering.NeedsStorage(array) ? "o" : "a";
                    type = array.Elem;
                    break;
                case TypeNode.Fqn f when bindings.TryGetValue(f.Name, out var binding): return (binding, shape);
                default: return (null, "");
            }
    }

    // One character per vector, outermost first: open System.Array storage or exact array storage.
    static JsonObject Shape(string shape) => new() { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.String"), ["value"] = shape };

    JsonObject Helper(string target, IReadOnlyList<JsonNode> arguments, IReadOnlyList<TypeNode> parameters, TypeNode result)
    {
        var split = target.LastIndexOf('.');
        var owner = target[..split];
        var name = target[(split + 1)..];
        var local = locals.Where(entry => entry.Value.Owner == owner && Text(entry.Value.Method["name"]) == name)
            .Where(entry => entry.Value.Method["params"] is JsonArray declared
                && declared.Count == parameters.Count
                && declared.OfType<JsonObject>().Select((parameter, index) =>
                    ReferenceMetadataIndex.SourceDeclarationDescribesCall(TypeJson.Read(parameter["type"]), parameters[index])).All(match => match))
            .Select(entry => entry.Key).ToArray();
        var identity = local.Length == 1 ? local[0]
            : refs.AuthoredKotlinHelper(owner, name, 0, parameters.ToArray()).DeclarationId;
        return new JsonObject {
            ["k"] = "callStatic", ["owner"] = TypeJson.Fqn(owner), ["method"] = name,
            [DeclarationIdentityBinding.Key] = identity,
            ["sig"] = new JsonArray(parameters.Select(TypeJson.Write).ToArray()),
            ["args"] = new JsonArray(arguments.Select(argument => argument.DeepClone()).ToArray()),
            ["ret"] = TypeJson.Write(result), ["sty"] = TypeJson.Write(result),
        };
    }

    // Freeze source constraints before companion frames are materialized. The physical
    // alias is not the semantic numeric union and cannot express its Kotlin upper bound.
    internal void EraseBounds(JsonNode root)
    {
        if (referenceBuild || bindings.Count == 0) return;
        void Walk(JsonNode node)
        {
            if (node is JsonArray array) { foreach (var child in array) Walk(child); return; }
            if (node is not JsonObject declaration) return;
            if (declaration["k"] == null && declaration["typeParams"] is JsonArray parameters)
            {
                var bounds = new JsonObject();
                var changed = false;
                for (var index = 0; index < parameters.Count; index++)
                    if (parameters[index] is JsonObject parameter && parameter["constraints"] is JsonArray constraints)
                    {
                        bounds[index.ToString()] = constraints.DeepClone();
                        changed |= constraints.Select(TypeJson.Read).Any(type => type != null && !type.Equals(AliasFace(type)));
                        var retained = constraints.Where(bound => TypeJson.Read(bound) is not TypeNode.Fqn f
                            || !bindings.ContainsKey(f.Name)).Select(bound => bound.DeepClone()).ToArray();
                        if (retained.Length == constraints.Count) continue;
                        parameter["constraints"] = new JsonArray(retained);
                        changed = true;
                    }
                if (changed)
                {
                    if (declaration["kind"] != null)
                        KotlinSupertypesRecord.Merge(declaration, new JsonObject { ["bounds"] = bounds });
                    else
                    {
                        var payload = Text(declaration[NullableGenericErasure.MethodTypeParameterBoundsPre]) is string encoded
                            ? JsonNode.Parse(encoded).AsObject() : new JsonObject();
                        var saved = payload["bounds"] as JsonObject ?? new JsonObject();
                        if (payload["bounds"] == null) payload["bounds"] = saved;
                        foreach (var (index, value) in bounds)
                            if (!saved.ContainsKey(index)) saved[index] = value.DeepClone();
                        declaration[NullableGenericErasure.MethodTypeParameterBoundsPre] = payload.ToJsonString();
                    }
                }
            }
            foreach (var (key, child) in declaration.ToArray())
                if (key is not ("attrs" or "overrides" or DeclarationIdentityBinding.SemanticSignatureKey)) Walk(child);
        }
        Walk(root);
    }

    internal void BindOperations(JsonNode root)
    {
        if (referenceBuild || bindings.Count == 0) return;
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
                && members.TryGetValue(selected, out var body) && !(body.Method["abstract"]?.GetValue<bool>() ?? false))
            {
                Replace(operation, DefaultCall(body.Owner, body.Method, operation["recv"], operation["args"] as JsonArray));
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
                Replace(operation, Helper(target, arguments, signature, memberResult));
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
            Replace(operation, new JsonObject { ["k"] = "valueBlock", ["stmts"] = statements, ["result"] = result });
        }
        Walk(root);
    }

    TypeNode AliasFace(TypeNode type) => type switch {
        TypeNode.Fqn f when bindings.ContainsKey(f.Name) =>
            BirTypeLowering.PhysicalHead(f.Name, aliases[f.Name], f.Args?.Select(AliasFace).ToArray()),
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args?.Select(AliasFace).ToArray()),
        TypeNode.Array a => new TypeNode.Array(AliasFace(a.Elem), a.Rank, a.SzArray),
        TypeNode.Nullable n => new TypeNode.Nullable(AliasFace(n.Of)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(AliasFace(o.Of)),
        TypeNode.ByRef b => new TypeNode.ByRef(AliasFace(b.Of)),
        TypeNode.Projection p => new TypeNode.Projection(p.Variance, AliasFace(p.Of)),
        TypeNode.Fn f => f with { Ret = AliasFace(f.Ret), Params = f.Params.Select(AliasFace).ToArray(),
            Recv = f.Recv == null ? null : AliasFace(f.Recv), Ctx = f.Ctx?.Select(AliasFace).ToArray() },
        _ => type,
    };

    internal void RecordSourceTypes(JsonNode root)
    {
        if (referenceBuild || bindings.Count == 0) return;
        void Slot(JsonObject declaration, string key, string carrier)
        {
            if (TypeJson.Read(declaration[key]) is not TypeNode original || original.Equals(AliasFace(original))) return;
            declaration[carrier] ??= TypeNode.ToJson(original);
        }
        void Walk(JsonNode node)
        {
            if (node is JsonArray array)
            {
                for (var index = 0; index < array.Count; index++)
                    Walk(array[index]);
                return;
            }
            if (node is not JsonObject obj) return;
            if (obj["k"] == null)
            {
                Slot(obj, "ret", "retKotlinType");
                Slot(obj, "type", "kotlinType");
                if (obj["kind"] != null)
                {
                    var pre = new JsonObject();
                    if (obj["interfaces"] is JsonArray interfaces)
                    {
                        var changed = interfaces.Where(edge => TypeJson.Read(edge) is TypeNode type
                            && !type.Equals(AliasFace(type))).Select(edge => edge.DeepClone()).ToArray();
                        if (changed.Length > 0) pre["interfaces"] = new JsonArray(changed);
                    }
                    if (TypeJson.Read(obj["base"]) is TypeNode parent && !parent.Equals(AliasFace(parent)))
                        pre["base"] = obj["base"].DeepClone();
                    KotlinSupertypesRecord.Merge(obj, pre);
                }
            }
            foreach (var (key, child) in obj.ToArray())
            {
                // MethodImpl descriptors state exact physical slots, not source declarations. Their
                // signatures still undergo type lowering, but must never acquire source metadata carriers.
                if (key is "attrs" or "overrides" or "memberRef" or "clrInterfaceImpls" or "clrBaseImpls"
                    or DeclarationIdentityBinding.SemanticSignatureKey) continue;
                Walk(child);
            }
        }
        Walk(root);
    }

    JsonObject DefaultCall(string owner, JsonObject source, JsonNode receiver, JsonArray arguments)
    {
        var call = new JsonObject {
            [DeclarationIdentityBinding.Key] = source[DeclarationIdentityBinding.Key]?.DeepClone(),
            ["recv"] = receiver.DeepClone(), ["ret"] = source["ret"]?.DeepClone(),
            ["sig"] = new JsonArray((source["params"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(parameter => parameter["type"]?.DeepClone()).ToArray()),
        };
        return (JsonObject)MemberCallSubstitution.Rule3HelperCall(call, refs, new TypeNode.Fqn(owner),
            Text(source["name"]), arguments ?? new JsonArray(), instance: true, locals.Keys.ToHashSet(StringComparer.Ordinal));
    }

    // Alias classes have no runtime base TypeDef. Give their semantic virtual slots a real
    // nominal interface map. Bridges are protected virtual implementation details: descendants
    // override the selected bridge, or call its inherited body NONvirtually. Thus a default
    // followed by a descendant override works across DLLs without rediscovering any body.
    internal void SynthesizeSlots(IReadOnlyList<JsonNode> roots, ValueTypeOracle isValue)
    {
        if (referenceBuild || members.Count == 0) return;
        var defs = SupertypeGraph.Collect(roots);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        var bridges = new Dictionary<(string Owner, string Slot), JsonObject>();
        void Apply(SupertypeGraph.Def cls)
        {
            if (completed.Contains(cls.Name) || cls.Kind != "class") return;
            if (!active.Add(cls.Name)) throw new InvalidOperationException("Cyclic nominal-slot base graph");
            if (cls.Base != null && defs.TryGetValue(cls.Base.Name, out var parent)) Apply(parent);
            var reachable = SupertypeGraph.Reachable(cls, defs, refs).Select(edge => edge.spec.Name).ToHashSet(StringComparer.Ordinal);
            // Referenced physical bases can erase their Kotlin alias edge. The frontend's
            // flattened exact override identities still state which semantic family is selected.
            var inheritedFacts = new[] { "inheritedClassMethods", KotlinPropertyAccessors.InheritedDefaultAccessorsKey,
                    KotlinPropertyAccessors.InheritedDefaultMethodsKey }
                .SelectMany(key => (cls.Node[key] as JsonArray ?? new JsonArray()).OfType<JsonObject>());
            foreach (var method in cls.Methods.OfType<JsonObject>().Concat(inheritedFacts))
                foreach (var marker in (method["overrides"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    if (Text(marker[DeclarationIdentityBinding.Key]) is string id && members.TryGetValue(id, out var selected))
                        reachable.Add(selected.Owner);
            var sourceMembers = members.Where(member => reachable.Contains(member.Value.Owner)).ToArray();
            bool Selects(JsonObject fact, string sourceId) =>
                (fact["overrides"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                    .Any(marker => Text(marker[DeclarationIdentityBinding.Key]) == sourceId);
            var inheritedImplementations = sourceMembers.Length == 0
                ? Array.Empty<(TypeNode.Fqn Owner, JsonObject Method, JsonObject Fact, bool IsDefault)>()
                : KotlinOverrideSlotBridge.SelectedInheritedImplementations(cls, defs, refs, isValue,
                    (fact, _) => sourceMembers.Any(source => Selects(fact, source.Key)));
            foreach (var (sourceId, source) in sourceMembers)
            {
                var split = source.Slot.LastIndexOf('.');
                var slotOwner = source.Slot[..split];
                var slotMember = source.Slot[(split + 1)..];
                var own = cls.Methods.OfType<JsonObject>().Where(method => !Bool(method["static"])
                    && method["overrides"] is JsonArray overrides && overrides.OfType<JsonObject>()
                        .Any(marker => Text(marker[DeclarationIdentityBinding.Key]) == sourceId)).ToArray();
                if (own.Length > 1) throw new InvalidOperationException("Ambiguous selected nominal-slot implementation");
                JsonObject inherited = null;
                TypeNode.Fqn inheritedOwner = null;
                // An intermediate class need not declare any semantic overrides or new bridges.
                // Resolve the actual inherited body along the constructed class chain, not just its first edge.
                foreach (var (baseType, _) in SupertypeGraph.Reachable(cls, defs, refs).Where(edge => !edge.isInterface))
                {
                    if (bridges.TryGetValue((baseType.Name, source.Slot), out var local))
                    {
                        inherited = local;
                        inheritedOwner = baseType;
                    }
                    else if (refs?.ValueSlotBridge(baseType.Name, slotOwner, slotMember) is { } external)
                    {
                        inherited = new JsonObject { ["name"] = external.Name, ["abstract"] = external.IsAbstract,
                            ["ret"] = TypeJson.Write(external.ReturnTypeNode),
                            ["params"] = new JsonArray(external.ParamTypeNodes.Select((type, index) => (JsonNode)new JsonObject {
                                ["name"] = "p" + index, ["type"] = TypeJson.Write(type) }).ToArray()) };
                        inheritedOwner = baseType;
                    }
                    if (inherited != null) break;
                }
                var declaration = source.Method;
                var parameters = declaration["params"] as JsonArray ?? new JsonArray();
                var signature = new JsonArray(parameters.OfType<JsonObject>().Select(parameter => parameter["type"]?.DeepClone()).ToArray());
                var arguments = new JsonArray(parameters.Select((parameter, index) => (JsonNode)new JsonObject {
                    ["k"] = "local", ["name"] = "p" + index, ["sty"] = parameter["type"]?.DeepClone() }).ToArray());
                var target = own.SingleOrDefault();
                var selectedBodies = target == null && (inherited == null || Bool(inherited["abstract"]))
                    ? inheritedImplementations.Where(selected => refs?.UsesObjectValueSlots(selected.Owner.Name) != true
                        && Selects(selected.Fact, sourceId)).ToArray()
                    : Array.Empty<(TypeNode.Fqn Owner, JsonObject Method, JsonObject Fact, bool IsDefault)>();
                if (selectedBodies.Length > 1) throw new InvalidOperationException("Ambiguous selected inherited value implementation");
                var hasSelectedBody = selectedBodies.Length == 1;
                if (target?["overrides"] is JsonArray selectedOverrides
                    && selectedOverrides.OfType<JsonObject>().Select(marker => Text(marker[DeclarationIdentityBinding.Key]))
                        .Where(id => id != null).All(members.ContainsKey))
                    target[DeclarationIdentityBinding.IndependentAliasSlotKey] = true;
                var abstractSlot = target != null ? Bool(target["abstract"])
                    : hasSelectedBody ? false : inherited != null ? Bool(inherited["abstract"]) : Bool(declaration["abstract"]);
                JsonObject forwarded = null;
                if (!abstractSlot && !hasSelectedBody)
                {
                    if (target != null || inherited != null)
                    {
                        var selected = target ?? inherited;
                        forwarded = new JsonObject {
                            ["k"] = "callInstance", ["ownerType"] = TypeJson.Write(target != null
                                ? new TypeNode.Fqn(cls.Name, cls.Arity == 0 ? null
                                    : Enumerable.Range(0, cls.Arity).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray())
                                : inheritedOwner),
                            ["method"] = selected[DeclarationIdentityBinding.ExplicitNameKey]?.DeepClone() ?? selected["name"]?.DeepClone(),
                            ["recv"] = new JsonObject { ["k"] = "this" }, ["args"] = arguments,
                            ["sig"] = signature.DeepClone(), ["ret"] = declaration["ret"]?.DeepClone(),
                            ["virtual"] = target != null, ["clrOwnerResolved"] = true,
                        };
                        if (target == null) forwarded["super"] = true;
                        else if (target[DeclarationIdentityBinding.Key] is JsonNode id) forwarded[DeclarationIdentityBinding.Key] = id.DeepClone();
                    }
                    else forwarded = DefaultCall(source.Owner, declaration, new JsonObject { ["k"] = "this" }, arguments);
                }
                var name = "dotkt$value$" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(source.Slot))).ToLowerInvariant()[..16];
                var bridge = new JsonObject {
                    ["name"] = name, ["static"] = false, ["override"] = inherited != null,
                    ["virtual"] = true, ["abstract"] = abstractSlot, ["objectOverride"] = false,
                    ["vis"] = "protected", ["generated"] = true,
                    ["params"] = new JsonArray(parameters.Select((parameter, index) => (JsonNode)new JsonObject {
                        ["name"] = "p" + index, ["type"] = parameter["type"]?.DeepClone() }).ToArray()),
                    ["ret"] = declaration["ret"]?.DeepClone(),
                    ["body"] = abstractSlot || hasSelectedBody ? new JsonArray() : new JsonArray(new JsonObject { ["k"] = "return", ["value"] = forwarded }),
                    ["attrs"] = new JsonArray(),
                    ["clrInterfaceImpls"] = new JsonArray(new JsonObject {
                        ["owner"] = TypeJson.Fqn(slotOwner), ["member"] = slotMember, ["arity"] = 0,
                        ["params"] = signature, ["ret"] = declaration["ret"]?.DeepClone(),
                    }),
                };
                if (hasSelectedBody)
                {
                    var selected = selectedBodies[0];
                    var body = KotlinOverrideSlotBridge.BuildBridge(cls, selected.Method,
                        parameters.OfType<JsonObject>().Select(parameter => TypeJson.Read(parameter["type"])).ToArray(),
                        TypeJson.Read(declaration["ret"]), name, isValue, refs, selected.Owner,
                        virtualCall: !selected.IsDefault,
                        callMember: Text(selected.Method[DeclarationIdentityBinding.ExplicitNameKey]) ?? Text(selected.Method["name"]));
                    bridge["params"] = body["params"]?.DeepClone();
                    bridge["body"] = body["body"]?.DeepClone();
                }
                cls.Methods.Add(MaterializedExecutable.Normalize(bridge));
                bridges[(cls.Name, source.Slot)] = bridge;
                var interfaces = cls.Node["interfaces"] as JsonArray ?? new JsonArray();
                if (cls.Node["interfaces"] == null) cls.Node["interfaces"] = interfaces;
                if (!interfaces.Any(type => TypeJson.OwnerName(type) == slotOwner)) interfaces.Add(TypeJson.Fqn(slotOwner));
            }
            // Inherited accessor placeholders are not new source declarations. Their value-family
            // obligations are the exact facet slots above, not a second abstract CLR property surface.
            var placeholders = cls.Methods.OfType<JsonObject>().Where(method => Bool(method["abstract"])
                && method[DeclarationIdentityBinding.Key] == null && SelectedAdapter(method) != null
                && (method["overrides"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                    .Select(marker => Text(marker[DeclarationIdentityBinding.Key])).Where(id => id != null)
                    .All(members.ContainsKey)).ToArray();
            var associations = placeholders.Select(method => Text(method[KotlinPropertyAccessors.AssociationKey]))
                .Where(value => value != null).ToHashSet(StringComparer.Ordinal);
            foreach (var placeholder in placeholders) cls.Methods.Remove(placeholder);
            if (cls.Node["properties"] is JsonArray properties)
                foreach (var property in properties.OfType<JsonObject>()
                             .Where(property => associations.Contains(Text(property[KotlinPropertyAccessors.AssociationKey]))).ToArray())
                    properties.Remove(property);
            active.Remove(cls.Name);
            completed.Add(cls.Name);
        }
        foreach (var cls in defs.Values) Apply(cls);
    }

    static void Replace(JsonObject target, JsonObject replacement)
    {
        var position = target["pos"]?.DeepClone();
        target.Clear();
        foreach (var (key, value) in replacement.ToArray()) target[key] = value?.DeepClone();
        if (position != null) target["pos"] = position;
    }

    internal static void SelfTest()
    {
        var root = JsonNode.Parse("""
        {"fileClass":"Helpers","types":[{"name":"Union","kind":"class","attrs":[
          {"attr":{"t":"fqn","name":"kotlin.clr.ClrTypeAlias"},"args":[{"value":"System.Object"}]},
          {"attr":{"t":"fqn","name":"kotlin.clr.ClrValueRepresentation"},"args":[
           {"value":"Helpers.matches"},{"value":"Helpers.classifier"},{"value":"Helpers.component"},{"value":""},{"value":""}]}],
          "methods":[{"name":"convert","declarationId":"convert","params":[],"ret":{"t":"fqn","name":"kotlin.Int"},"abstract":true,"attrs":[
           {"attr":{"t":"fqn","name":"kotlin.clr.ClrValueMember"},"args":[{"value":"Helpers.convert"},{"value":"Slots.convert"}]}]}]},
          {"name":"Child","kind":"class","base":{"t":"fqn","name":"Union"},"methods":[
           {"name":"differentName","declarationId":"child-convert","ret":{"t":"fqn","name":"kotlin.Int"},"params":[],
            "overrides":[{"declarationId":"convert"}],"abstract":false}]}],"methods":[
          {"name":"matches","declarationId":"matches","params":[{"type":{"t":"nullable","of":{"t":"fqn","name":"kotlin.Any"}}},{"type":{"t":"fqn","name":"kotlin.String"}}]},
          {"name":"classifier","declarationId":"classifier","params":[{"type":{"t":"fqn","name":"kotlin.String"}}]},
          {"name":"convert","declarationId":"helper-convert","params":[{"type":{"t":"fqn","name":"kotlin.Any"}}]},
          {"name":"use","params":[{"name":"value","type":{"t":"fqn","name":"Union"}}],
           "ret":{"t":"array","elem":{"t":"fqn","name":"Union"}},
           "typeParams":[{"name":"T","constraints":[{"t":"fqn","name":"Union"}]}],"body":[
            {"k":"return","value":{"k":"callInstance","declarationId":"convert",
             "recv":{"k":"local","name":"value"},"args":[],"sig":[],"ret":{"t":"fqn","name":"kotlin.Int"}}},
            {"k":"exprStmt","expr":{"k":"isInst","type":{"t":"fqn","name":"Union"},"e":{"k":"local","name":"value"}}},
            {"k":"exprStmt","expr":{"k":"classRef","type":{"t":"fqn","name":"Union"}}},
            {"k":"exprStmt","expr":{"k":"callStatic","sig":[{"t":"fqn","name":"Union"}]}}]}]}
        """);
        var policy = new ValueRepresentationLowering(new[] { root }, null, false);
        var inheritedCall = JsonNode.Parse("""
            {"k":"callInstance","overrides":[{"declarationId":"convert"}],
             "recv":{"k":"local","name":"value"},"args":[],"sig":[],"ret":{"t":"fqn","name":"kotlin.Int"}}
            """);
        policy.BindOperations(inheritedCall);
        if (Text(inheritedCall[DeclarationIdentityBinding.Key]) != "helper-convert")
            throw new InvalidOperationException("A frontend-selected inherited contract lost its value adapter");
        var nullCast = JsonNode.Parse("""
            {"k":"cast","type":{"t":"fqn","name":"Union"},
             "e":{"k":"const","type":{"t":"nullable","of":{"t":"fqn","name":"kotlin.Any"}},"value":null}}
            """);
        policy.BindOperations(nullCast);
        if (TypeJson.OwnerName(nullCast["stmts"]?[1]?["branches"]?[0]?["body"]?[0]?["value"]?["type"])
            != "kotlin.NullPointerException")
            throw new InvalidOperationException("A non-null classified cast lost its Kotlin null exception");
        var scalar = policy.Predicate(new TypeNode.Fqn("Union"));
        var vector = policy.Predicate(new TypeNode.Array(new TypeNode.Fqn("Union")));
        var projected = policy.Predicate(new TypeNode.Array(new TypeNode.Array(
            new TypeNode.Projection("out", new TypeNode.Fqn("Union")))));
        policy.EraseBounds(root);
        policy.BindOperations(root);
        var method = root["methods"][3];
        foreach (var key in new[] { "clrInterfaceImpls", "clrBaseImpls" })
            method[key] = JsonNode.Parse("""
            [{"owner":{"t":"fqn","name":"Source"},"member":"value","arity":0,"params":[],
              "ret":{"t":"fqn","name":"Union"}}]
            """);
        policy.RecordSourceTypes(root);
        policy.SynthesizeSlots(new[] { root }, _ => false);
        foreach (var key in new[] { "clrInterfaceImpls", "clrBaseImpls" })
            if (method[key][0].AsObject().ContainsKey("retKotlinType"))
                throw new InvalidOperationException("Physical MethodImpl descriptor acquired source metadata");
        if (TypeJson.OwnerName(method["params"][0]["type"]) != "Union"
            || TypeJson.Read(method["ret"]) != new TypeNode.Array(new TypeNode.Fqn("Union"))
            || method["params"][0]["kotlinType"] == null || method["retKotlinType"] == null
            || method["typeParams"][0]["constraints"].AsArray().Count != 0
            || Text(method[NullableGenericErasure.MethodTypeParameterBoundsPre])?.Contains("Union") != true
            || Text(method["body"][0]["value"][DeclarationIdentityBinding.Key]) != "helper-convert"
            || Text(method["body"][1]["expr"]["k"]) != "valueBlock"
            || TypeJson.OwnerName(method["body"][2]["expr"]["type"]) != "Union"
            || TypeJson.OwnerName(method["body"][3]["expr"]["sig"][0]) != "Union"
            || TypeJson.OwnerName(root["types"][1]["base"]) != "Union"
            || Text(scalar["args"][0]["value"]) != ""
            || Text(vector["args"][0]["value"]) != "a"
            || Text(projected["args"][0]["value"]) != "oo")
            throw new InvalidOperationException("Value representation lost source types, bounds, exact heads, or classifier shape");
        var child = root["types"][1];
        var bridge = child["methods"].AsArray().OfType<JsonObject>().Single(method => method["generated"]?.GetValue<bool>() == true);
        if (TypeJson.OwnerName(child["interfaces"][0]) != "Slots"
            || Text(bridge["body"][0]["value"][DeclarationIdentityBinding.Key]) != "child-convert"
            || Text(bridge["clrInterfaceImpls"][0]["member"]) != "convert")
            throw new InvalidOperationException("Nominal slots lost exact override/member bindings");
        var markerRoot = JsonNode.Parse("""
            {"types":[{"name":"Tag","kind":"interface","attrs":[
              {"attr":{"t":"fqn","name":"kotlin.clr.ClrTypeAlias"},"args":[{"value":"System.Object"}]},
              {"attr":{"t":"fqn","name":"kotlin.clr.ClrValueRepresentation"},"args":[
                {"value":"Helpers.matches"},{"value":"Helpers.classifier"},
                {"value":"Helpers.component"},{"value":"Slots.Tag"},{"value":""}]}]},
              {"name":"Implementation","kind":"class","interfaces":[{"t":"fqn","name":"Tag"},{"t":"fqn","name":"Slots.Tag"}],
                "fields":[{"name":"value","type":{"t":"fqn","name":"Tag"}}]}]}
            """);
        var markers = new ValueRepresentationLowering(new[] { markerRoot }, null, false).InterfaceMarkers;
        var projectedMarker = BirTypeLowering.Lower(markerRoot, false,
            new Dictionary<string, string> { ["Tag"] = "System.Object" }, interfaceMarkers: markers);
        if (TypeJson.OwnerName(projectedMarker["types"][1]["interfaces"][0]) != "Slots.Tag"
            || projectedMarker["types"][1]["interfaces"].AsArray().Count != 1
            || TypeJson.OwnerName(projectedMarker["types"][1]["fields"][0]["type"]) != "System.Object"
            || TypeJson.OwnerName(markerRoot["types"][1]["interfaces"][0]) != "Tag")
            throw new InvalidOperationException("Interface marker projection changed the value alias or source identity");
        var externalRoot = markerRoot.DeepClone();
        externalRoot["types"][0]["name"] = "Carrier.Tag";
        externalRoot["types"][0]["attrs"][1]["args"][4]["value"] = "Tag";
        var external = new ValueRepresentationLowering(new[] { externalRoot }, null, false);
        if (!external.InterfaceMarkers["Tag"].SequenceEqual(new[] { "Slots.Tag" })
            || external.InterfaceMarkers.ContainsKey("Carrier.Tag")
            || external.AliasFace(new TypeNode.Fqn("Tag")) != new TypeNode.Fqn("System.Object"))
            throw new InvalidOperationException("CLR-only binding carrier lost its explicit common classifier target");
        var inheritedMarkerRoot = markerRoot.DeepClone();
        var derivedMarker = (JsonObject)inheritedMarkerRoot["types"][0].DeepClone();
        derivedMarker["name"] = "DerivedTag";
        derivedMarker["interfaces"] = new JsonArray(TypeJson.Fqn("Tag"));
        derivedMarker["attrs"][1]["args"][3]["value"] = "Slots.DerivedTag";
        inheritedMarkerRoot["types"].AsArray().Add(derivedMarker);
        var inheritedMarkers = new ValueRepresentationLowering(new[] { inheritedMarkerRoot }, null, false).InterfaceMarkers;
        if (!inheritedMarkers["DerivedTag"].SequenceEqual(new[] { "Slots.DerivedTag", "Slots.Tag" }))
            throw new InvalidOperationException("A mapped interface lost its source ancestor facets");
        var arguments = new TypeNode[] { new TypeNode.Fqn("kotlin.String") };
        if (BirTypeLowering.PhysicalHead("Logical", "System.Object", arguments) != new TypeNode.Fqn("System.Object")
            || BirTypeLowering.ProjectedAliasHasReifiedGenericHead("Logical", "System.Object", null,
                new Dictionary<string, string>())
            || BirTypeLowering.ProjectedAliasHasReifiedGenericHead("Logical", "System.Object", arguments,
                new Dictionary<string, string>())
            || BirTypeLowering.PhysicalHead("Native", "System.Collections.Generic.List", arguments)
                is not TypeNode.Fqn { Args.Length: 1 })
            throw new InvalidOperationException("Non-generic object alias retained semantic type arguments or erased a native construction");
        Console.WriteLine("[value representation] self-test OK (source carriers, bounds, exact adapters, nominal slots, array classifiers)");
    }
}
