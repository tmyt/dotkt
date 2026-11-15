using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;

sealed record ValueRepresentationBinding(string Classifier, string ClassifierFactory, string ComponentClassifier, string InterfaceMarker);

// Declaration-owned Kotlin classifiers can be narrower than their CLR alias face.
// The trusted stdlib supplies all type/member/classifier choices. No source name selects a rule.
sealed partial class ValueRepresentationLowering
{
    internal const string TypeAttribute = "kotlin.clr.ClrValueRepresentation";
    internal const string MemberAttribute = "kotlin.clr.ClrValueMember";
    readonly IReadOnlyDictionary<string, ValueRepresentationBinding> bindings;
    readonly IReadOnlyDictionary<string, string> aliases;
    readonly ValueRepresentationSourcePolicy sourcePolicy;
    readonly Dictionary<string, IReadOnlyList<string>> localParameters = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> adapters = new(StringComparer.Ordinal);
    readonly Dictionary<string, (string Owner, LiveMethod Method, string Slot)> members = new(StringComparer.Ordinal);
    readonly Dictionary<string, (string Owner, LiveMethod Method)> locals = new(StringComparer.Ordinal);
    readonly IReadOnlyDictionary<string, SupertypeGraph.Def> sourceHierarchy;
    IReadOnlyDictionary<string, IReadOnlyList<string>> interfaceMarkers;
    readonly ReferenceMetadataIndex refs;
    readonly bool referenceBuild;

    static string Text(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;
    static bool Bool(JsonNode node) => node is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    ValueRepresentationLowering(ReferenceMetadataIndex references, bool refBuild,
        ValueRepresentationSourcePolicy sourcePolicy)
    {
        refs = references;
        referenceBuild = refBuild;
        this.sourcePolicy = sourcePolicy;
        sourceHierarchy = sourcePolicy.Hierarchy;
        aliases = sourcePolicy.Aliases;
        bindings = sourcePolicy.Bindings;
        foreach (var member in refs?.ValueMembers ?? Array.Empty<MemberBinding>())
        {
            members[member.DeclarationId] = (member.Owner, new LiveMethod(member), member.ValueSlot);
        }
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

    string SelectedAdapter(string declarationId, IEnumerable<string> overrides)
    {
        string Adapter(string id) => id == null ? null : adapters.GetValueOrDefault(id) ?? refs?.ValueAdapter(id);
        if (declarationId != null) return Adapter(declarationId);
        // A fake override has no declaration of its own. The frontend states its exact inherited contracts;
        // use a unique adapter among those identities, never reconstruct a member from its owner/name.
        var inherited = overrides.Select(Adapter)
            .Where(target => target != null).Distinct(StringComparer.Ordinal).ToArray();
        return inherited.Length == 1 ? inherited[0] : null;
    }

    bool OpaqueScalar(TypeNode type)
    {
        var (binding, shape) = Classifier(type);
        if (binding == null || shape.Length != 0) return false;
        while (type is TypeNode.Nullable or TypeNode.Oblivious)
            type = type is TypeNode.Nullable nullable ? nullable.Of : ((TypeNode.Oblivious)type).Of;
        return type is TypeNode.Fqn application && AliasVarianceRepresentation.RequiresErasureFromSource(application, refs,
            localParameters.GetValueOrDefault(application.Name), aliases.GetValueOrDefault(application.Name));
    }

    // The predicate owns logical membership; the token describes only the admitted
    // value's physical slot. Reified checks must not reopen a native generic face.
    internal TypeNode ClassifierType(TypeNode type) => OpaqueScalar(type) ? new TypeNode.Fqn("kotlin.Any") : type;

    internal JsonNode ComponentPredicate(TypeNode type, JsonNode actualType)
        => ComponentClassifier(type) is { } target
            ? Helper(target, new[] { actualType },
                new TypeNode[] { new TypeNode.Fqn("DotKt.Runtime.CompilerServices.StarProjectionType") }, new TypeNode.Fqn("kotlin.Boolean"))
            : null;

    string ComponentClassifier(TypeNode type)
    {
        var (binding, shape) = Classifier(type);
        return !referenceBuild && binding != null && shape.Length == 0 ? binding.ComponentClassifier : null;
    }

    (ValueRepresentationBinding Binding, string Shape) Classifier(TypeNode type) => sourcePolicy.Classifier(type);

    // One character per vector, outermost first: open System.Array storage or exact array storage.
    static JsonObject Shape(string shape) => new() { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.String"), ["value"] = shape };

    JsonObject Helper(string target, IReadOnlyList<JsonNode> arguments, IReadOnlyList<TypeNode> parameters, TypeNode result)
    {
        var split = target.LastIndexOf('.');
        var owner = target[..split];
        var name = target[(split + 1)..];
        var identity = HelperIdentity(owner, name, parameters);
        return new JsonObject {
            ["k"] = "callStatic", ["owner"] = TypeJson.Fqn(owner), ["method"] = name,
            [DeclarationIdentityBinding.Key] = identity,
            ["sig"] = new JsonArray(parameters.Select(TypeJson.Write).ToArray()),
            ["args"] = new JsonArray(arguments.Select(argument => argument.DeepClone()).ToArray()),
            ["ret"] = TypeJson.Write(result), ["sty"] = TypeJson.Write(result),
        };
    }

    TypeNode AliasFace(TypeNode type) => sourcePolicy.AliasFace(type);

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


    internal static void SelfTest()
    {
        KClassMemberBinding.SelfTest();
        SourceMembershipSelfTest();
        var root = JsonNode.Parse("""
        {"fileClass":"Helpers","types":[{"name":"Union","kind":"class","attrs":[
          {"attr":{"t":"fqn","name":"kotlin.clr.ClrTypeAlias"},"args":[{"k":"const","value":"System.Object"}]},
          {"attr":{"t":"fqn","name":"kotlin.clr.ClrValueRepresentation"},"args":[
           {"k":"const","value":"Helpers.matches"},{"k":"const","value":"Helpers.classifier"},{"k":"const","value":"Helpers.component"},{"k":"const","value":""},{"k":"const","value":""}]}],
          "methods":[{"name":"convert","declarationId":"convert","params":[],"ret":{"t":"fqn","name":"kotlin.Int"},"abstract":true,"attrs":[
           {"attr":{"t":"fqn","name":"kotlin.clr.ClrValueMember"},"args":[{"k":"const","value":"Helpers.convert"},{"k":"const","value":"Slots.convert"}]}]}]},
          {"name":"Child","kind":"class","base":{"t":"fqn","name":"Union"},"methods":[
           {"name":"differentName","declarationId":"child-convert","ret":{"t":"fqn","name":"kotlin.Int"},"params":[],
            "overrides":[{"declarationId":"convert"}],"abstract":false}]}],"methods":[
          {"name":"matches","declarationId":"matches","params":[{"type":{"t":"nullable","of":{"t":"fqn","name":"kotlin.Any"}}},{"type":{"t":"fqn","name":"kotlin.String"}}]},
          {"name":"classifier","declarationId":"classifier","params":[{"type":{"t":"fqn","name":"kotlin.String"}}]},
          {"name":"component","declarationId":"component","params":[{"type":{"t":"fqn","name":"DotKt.Runtime.CompilerServices.StarProjectionType"}}]},
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
        root["types"].AsArray().Add(JsonNode.Parse("""
            {"name":"AbstractChild","kind":"class","abstract":true,"base":{"t":"fqn","name":"Union"},
             "methods":[{"name":"convert","declarationId":"abstract-convert","abstract":true,
              "params":[],"ret":{"t":"fqn","name":"kotlin.Int"},"body":[],
              "attrs":[{"attr":{"t":"fqn","name":"kotlin.clr.ClrValueMember"},
                "args":[{"k":"const","value":"Helpers.convert"},{"k":"const","value":"Slots.convert"}]}],
              "overrides":[{"declarationId":"convert"}]}]}
            """));
        root["types"].AsArray().Add(JsonNode.Parse("""
            {"name":"AliasMiddle","kind":"class","base":{"t":"fqn","name":"Union"},"methods":[],
             "attrs":[{"attr":{"t":"fqn","name":"kotlin.clr.ClrTypeAlias"},"args":[{"k":"const","value":"System.Object"}]}]}
            """));
        root["types"].AsArray().Add(JsonNode.Parse("""
            {"name":"AliasLeaf","kind":"class","base":{"t":"fqn","name":"AliasMiddle"},"methods":[
             {"name":"convert","declarationId":"leaf-convert","params":[],"ret":{"t":"fqn","name":"kotlin.Int"},
              "overrides":[{"declarationId":"convert"}],"abstract":false}]}
            """));
        var referenceRoot = root.DeepClone();
        var source = ReadFixture(root);
        var sourcePolicy = new ValueRepresentationSourcePolicy(new[] { source }, null, false);
        source = sourcePolicy.EraseBounds(source);
        var sourceIndex = CaptureSource(new[] { source });
        var policy = CreateSource(new[] { source }, null, false, sourcePolicy, sourceIndex, null);
        var actualComponent = new BirLocal { Name = "componentType",
            Sty = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.StarProjectionType") };
        var component = policy.ComponentPredicate(new TypeNode.Fqn("Union"), (IBirExpression)actualComponent);
        var componentDom = policy.ComponentPredicate(new TypeNode.Fqn("Union"), actualComponent.ToDom());
        if (component is not BirCallStatic componentCall || !ReferenceEquals(componentCall.Args.Value[0], actualComponent)
            || componentCall.DeclarationId.OrDefault() != "component"
            || !JsonNode.DeepEquals(componentCall.ToDom(), componentDom)
            || policy.ComponentPredicate(new TypeNode.Array(new TypeNode.Fqn("Union")), (IBirExpression)actualComponent) != null)
            throw new InvalidOperationException("Owning component predicate changed classifier selection, exact helper or operand identity");
        var inheritedCall = JsonNode.Parse("""
            {"k":"callInstance","overrides":[{"declarationId":"convert"}],
             "recv":{"k":"local","name":"value"},"args":[],"sig":[],"ret":{"t":"fqn","name":"kotlin.Int"}}
            """);
        inheritedCall = BindFixtureOperation(policy, inheritedCall);
        if (Text(inheritedCall[DeclarationIdentityBinding.Key]) != "helper-convert")
            throw new InvalidOperationException("A frontend-selected inherited contract lost its value adapter");
        var abstractCall = JsonNode.Parse("""
            {"k":"callInstance","declarationId":"abstract-convert","overrides":[{"declarationId":"convert"}],
             "recv":{"k":"local","name":"value"},"args":[],"sig":[],"ret":{"t":"fqn","name":"kotlin.Int"}}
            """);
        abstractCall = BindFixtureOperation(policy, abstractCall);
        if (Text(abstractCall[DeclarationIdentityBinding.Key]) != "helper-convert")
            throw new InvalidOperationException("A retained abstract obligation lost its selected ancestor adapter");
        var nullCast = JsonNode.Parse("""
            {"k":"cast","type":{"t":"fqn","name":"Union"},
             "e":{"k":"const","type":{"t":"nullable","of":{"t":"fqn","name":"kotlin.Any"}},"value":null}}
            """);
        nullCast = BindFixtureOperation(policy, nullCast);
        if (TypeJson.OwnerName(nullCast["stmts"]?[1]?["branches"]?[0]?["body"]?[0]?["value"]?["type"])
            != "kotlin.NullPointerException")
            throw new InvalidOperationException("A non-null classified cast lost its Kotlin null exception");
        var scalar = ((BirGraphValue)policy.PredicateValue(new TypeNode.Fqn("Union"))).ToDom();
        var vector = ((BirGraphValue)policy.PredicateValue(new TypeNode.Array(new TypeNode.Fqn("Union")))).ToDom();
        var projected = ((BirGraphValue)policy.PredicateValue(new TypeNode.Array(new TypeNode.Array(
            new TypeNode.Projection("out", new TypeNode.Fqn("Union")))))).ToDom();
        var boundMethod = new BirFunctionDeclaration { Name = "boundBridge", Params = Array.Empty<BirParameter>(),
            TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = Array.Empty<TypeNode>() } },
            MethodTypeParameterBoundsPre = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> {
                [0] = new TypeNode[] { new TypeNode.Fqn("Union") } }),
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = new BirCast {
                Type = new TypeNode.Tv("method", 0), ReifiedTypeOperand = false,
                E = new BirLocal { Name = "value", Sty = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")) } } } } };
        var materializedBound = (BirFunctionDeclaration)boundMethod.DeepClone();
        NestedBoundGuardSelfTest(policy);
        policy.BindOperations(new BirFileDeclaration { Methods = new[] { boundMethod } });
        policy.BindMaterializedOperations(materializedBound);
        if (!boundMethod.StructurallyEquals(materializedBound)
            || materializedBound.Body.Value[0] is not BirExpressionStatement {
                Expr.Value: BirCast { E.Value: BirCast { E.Value: BirValueBlock } } })
            throw new InvalidOperationException("Source and materialized value operations diverged on their shared saved-bound decision");
        policy.BindOperations(source);
        var declarations = FixtureProjection(source);
        policy.SynthesizeSlots(new[] { source }, _ => false, (_, removed, _) => {
            foreach (var declaration in removed)
            {
                policy.RetireSourceDeclaration(declaration);
                declarations.RetireForest(declaration);
            }
        }, null);
        root = source.ToDom(declarations.ObserveProjection);
        declarations.ProjectRetiredForests();
        policy.BindDomDeclarations(new[] { root }, declarations.RetiredMethods, declarations);
        var method = root["methods"].AsArray().OfType<JsonObject>().Single(method => Text(method["name"]) == "use");
        foreach (var key in new[] { "clrInterfaceImpls", "clrBaseImpls" })
            method[key] = JsonNode.Parse("""
            [{"owner":{"t":"fqn","name":"Source"},"member":"value","arity":0,"params":[],
              "ret":{"t":"fqn","name":"Union"}}]
            """);
        policy.RecordSourceTypes(root);
        if (root["types"][2]["methods"].AsArray().OfType<JsonObject>().Count() != 1
            || !Bool(root["types"][2]["methods"][0]["generated"]))
            throw new InvalidOperationException("An inherited binding annotation duplicated a nominal facet slot");
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
        var referenceSource = ReadFixture(referenceRoot);
        var referencePolicy = CreateSource(new[] { referenceSource }, null, true,
            new ValueRepresentationSourcePolicy(new[] { referenceSource }, null, true), CaptureSource(new[] { referenceSource }), null);
        referencePolicy.SynthesizeSlots(new[] { referenceSource }, _ => false, null, null);
        referenceRoot = referenceSource.ToDom();
        foreach (var face in new[] { root, referenceRoot })
        {
            if (face["types"][3]["methods"].AsArray().Count != 0
                || (face["types"][3]["interfaces"] as JsonArray)?.Count > 0)
                throw new InvalidOperationException("An erased alias acquired a nonexistent physical slot bridge");
            var leafBridge = face["types"][4]["methods"].AsArray().OfType<JsonObject>()
                .Single(method => Bool(method["generated"]));
            if (Bool(leafBridge["override"])
                || Text(leafBridge["body"][0]["value"][DeclarationIdentityBinding.Key]) != "leaf-convert")
                throw new InvalidOperationException("A concrete descendant inherited an erased alias's phantom bridge");
        }
        var referenceBridge = referenceRoot["types"][1]["methods"].AsArray().OfType<JsonObject>()
            .SingleOrDefault(method => Bool(method["generated"]));
        if (referenceBridge == null || Bool(referenceBridge["abstract"])
            || Text(referenceBridge["name"]) != Text(bridge["name"])
            || referenceBridge["params"].ToJsonString() != bridge["params"].ToJsonString()
            || referenceBridge["ret"].ToJsonString() != bridge["ret"].ToJsonString()
            || referenceBridge["clrInterfaceImpls"].ToJsonString() != bridge["clrInterfaceImpls"].ToJsonString())
            throw new InvalidOperationException("Reference face dropped a runtime nominal-slot declaration");
        var markerRoot = JsonNode.Parse("""
            {"types":[{"name":"Tag","kind":"interface","attrs":[
              {"attr":{"t":"fqn","name":"kotlin.clr.ClrTypeAlias"},"args":[{"k":"const","value":"System.Object"}]},
              {"attr":{"t":"fqn","name":"kotlin.clr.ClrValueRepresentation"},"args":[
                {"k":"const","value":"Helpers.matches"},{"k":"const","value":"Helpers.classifier"},
                {"k":"const","value":"Helpers.component"},{"k":"const","value":"Slots.Tag"},{"k":"const","value":""}]}]},
              {"name":"Implementation","kind":"class","interfaces":[{"t":"fqn","name":"Tag"},{"t":"fqn","name":"Slots.Tag"}],
                "fields":[{"name":"value","type":{"t":"fqn","name":"Tag"}}]}]}
            """);
        var markers = FixturePolicy(markerRoot, false).InterfaceMarkers;
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
        var external = FixturePolicy(externalRoot, false);
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
        var inheritedMarkers = FixturePolicy(inheritedMarkerRoot, false).InterfaceMarkers;
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

    // Sparse authored tests enter through the current typed input codec. This is
    // a test adapter, not a production pass hand-off or an older-artifact reader.
    static BirFileDeclaration ReadFixture(JsonNode root) =>
        BirGraphJson.ReadFile(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString()));

    static ValueRepresentationLowering FixturePolicy(JsonNode root, bool referenceBuild)
    {
        var source = ReadFixture(root);
        var result = CreateSource(new[] { source }, null, referenceBuild,
            new ValueRepresentationSourcePolicy(new[] { source }, null, referenceBuild), CaptureSource(new[] { source }), null);
        var declarations = FixtureProjection(source);
        var physical = source.ToDom(declarations.ObserveProjection);
        declarations.ProjectRetiredForests();
        result.BindDomDeclarations(new[] { physical }, null, declarations);
        return result;
    }

    static SourceDeclarationProjection FixtureProjection(params BirFileDeclaration[] roots)
        => new(roots, new CurrentDeclarationBindings(roots));

    static JsonNode BindFixtureOperation(ValueRepresentationLowering policy, JsonNode expression)
    {
        var source = ReadFixture(new JsonObject { ["fileClass"] = "OperationFixture", ["methods"] = new JsonArray(
            new JsonObject { ["name"] = "use", ["params"] = new JsonArray(), ["body"] = new JsonArray(
                new JsonObject { ["k"] = "exprStmt", ["expr"] = expression.DeepClone() }) }) });
        policy.BindOperations(source);
        return ((BirGraphValue)((BirExpressionStatement)source.Methods.Value[0].Body.Value[0]).Expr.Value).ToDom();
    }

    static void SourceMembershipSelfTest()
    {
        var source = new BirFileDeclaration { FileClass = "GeneratedOwner", Methods = new[] {
            new BirFunctionDeclaration { Name = "lifted", DeclarationId = "existing-generated", Generated = true,
                Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("kotlin.String") },
        } };
        var membership = new SourceModuleIndex(new[] { source }, new Dictionary<string, string>()).DeclarationIds;
        var policy = new ValueRepresentationSourcePolicy(new[] { source }, null, false);
        var index = CaptureSource(new[] { source }, membership);
        var indexed = CreateSource(new[] { source }, null, false, policy, index, null);
        var declarations = FixtureProjection(source);
        var forwarder = new BirFunctionDeclaration {
            Name = "forwarder", DeclarationId = "new-physical-only", Generated = true,
            Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("kotlin.String"),
        };
        source.Methods = source.Methods.Value.Append(forwarder).ToArray();
        declarations.PublishMethods(new[] { forwarder });
        var root = source.ToDom(declarations.ObserveProjection);
        declarations.ProjectRetiredForests();
        indexed.BindDomDeclarations(new[] { root }, null, declarations);
        var method = root["methods"][0].AsObject();
        method["ret"] = TypeJson.Fqn("kotlin.Int");
        if (!indexed.locals.ContainsKey("existing-generated") || indexed.locals.ContainsKey("new-physical-only")
            || !ReferenceEquals(indexed.locals["existing-generated"].Method.Dom, method)
            || indexed.locals["existing-generated"].Method.Return is not TypeNode.Fqn { Name: "kotlin.Int" })
            throw new InvalidOperationException("Source membership lost preexisting generated declarations or live representation signatures");
        SourceIndexSelfTest();
    }
}
