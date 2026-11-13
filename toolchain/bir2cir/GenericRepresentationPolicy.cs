using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;

// One binding-owned policy for both demand analysis and type materialization. Compiler-owned generic
// applications use their declared frame; readonly aliases retain the same face in ordinary and nested slots.
sealed partial class GenericRepresentationPolicy
{
    internal const string NativeLocationKey = "_nativeLocation";
    internal const string FieldAddressKey = "_nativeFieldAddress";
    internal const string PhysicalDeclarationSignatureKey = "_physicalDeclarationSignature";
    readonly Dictionary<string, string> _aliases;
    readonly ReferenceMetadataIndex _references;
    readonly Dictionary<string, JsonObject> _localDeclarations = new(StringComparer.Ordinal);
    readonly HashSet<string> _localOwners = new(StringComparer.Ordinal);
    readonly HashSet<string> _existentialOwners = new(StringComparer.Ordinal);
    readonly HashSet<JsonObject> _nativeFields = new();
    readonly HashSet<(string Owner, string Name, bool Static, string Receiver)> _nativeFieldNames = new();
    bool _sourceAwaitingDom;

    GenericRepresentationPolicy(IReadOnlyDictionary<string, string> aliases,
        ReferenceMetadataIndex references, bool sourceOwned)
    {
        _aliases = new Dictionary<string, string>(aliases, StringComparer.Ordinal);
        _references = references;
        _sourceAwaitingDom = sourceOwned;
    }

    public GenericRepresentationPolicy(IReadOnlyDictionary<string, string> aliases,
        ReferenceMetadataIndex references = null, IEnumerable<JsonNode> roots = null)
        : this(aliases, references, sourceOwned: false)
    {
        void Collect(JsonNode node)
        {
            if (node is not JsonObject declaration) return;
            if (declaration["kind"] != null && declaration["name"] is JsonValue name
                && name.TryGetValue<string>(out var owner))
            {
                _localDeclarations[owner] = declaration;
                _localOwners.Add(owner);
                if (FBoundStarProjectionErasure.IsSourceGeneric(declaration)) _existentialOwners.Add(owner);
            }
            foreach (var child in declaration["types"] as JsonArray ?? new JsonArray()) Collect(child);
            foreach (var cell in (declaration["refTypes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (Text(cell["name"]) is string cellOwner)
                {
                    _localDeclarations[cellOwner] = cell;
                    _localOwners.Add(cellOwner);
                }
        }
        var inputs = (roots ?? Array.Empty<JsonNode>()).ToArray();
        foreach (var root in inputs) Collect(root);
        RecordNativeFields(inputs);
    }

    // Local ownership and declaration facts include aliases and generated physical types. Carrier eligibility is
    // a separate source declaration decision; discovering a late physical type must never allocate it a carrier.
    internal void RegisterLocalDefinitions(IReadOnlyDictionary<string, JsonObject> declarations,
        IReadOnlyDictionary<string, string> aliases)
    {
        foreach (var (name, declaration) in declarations)
        {
            _localDeclarations[name] = declaration;
            _localOwners.Add(name);
        }
        foreach (var (name, target) in aliases) _aliases[name] = target;
    }

    internal JsonObject LocalDeclaration(string owner) => _localDeclarations.GetValueOrDefault(owner);
    internal string BindingAlias(string owner) => owner == null ? null
        : _aliases.GetValueOrDefault(ReferenceMetadataIndex.BareOwnerFqn(owner));

    // Revisit newly materialized source/capture fields before value projection. Selected managed-reference
    // parameter shapes own these locations; the late value-flow pass separately allocates exact native locals.
    internal void RecordNativeFields(IEnumerable<JsonNode> roots)
    {
        var inputs = roots.ToArray();
        var fields = new Dictionary<(string Owner, string Name, bool Static, string Receiver), JsonObject>();
        void IndexFields(JsonObject declaration)
        {
            var owner = Text(declaration["name"]) ?? Text(declaration["fileClass"]);
            foreach (var field in (declaration["fields"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (owner != null && Text(field["name"]) is string name
                    && TypeJson.Read(field["type"]) != null)
                {
                    var key = FieldKey(owner, name, field["static"]?.GetValue<bool>() == true, field);
                    fields.Add(key, field);
                    // An explicit field export requests native typed storage even without an address use.
                    // An ordinary Kotlin property can also be published as a CLR field (notably a file value);
                    // visibility alone does not turn its source-owned value slot into a native location.
                    if ((field["attrs"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                        .Any(attribute => TypeJson.OwnerName(attribute["attr"]) == "kotlin.clr.ClrField"))
                    {
                        _nativeFields.Add(field);
                        _nativeFieldNames.Add(key);
                    }
                }
            foreach (var cell in (declaration["refTypes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (Text(cell["name"]) is string cellOwner) fields.Add(FieldKey(cellOwner, "v", false, cell), cell);
            foreach (var child in (declaration["types"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                IndexFields(child);
        }
        foreach (var root in inputs.OfType<JsonObject>()) IndexFields(root);
        void FindAddresses(JsonNode node)
        {
            if (node is JsonArray array)
                foreach (var child in array) FindAddresses(child);
            else if (node is JsonObject obj)
            {
                // The selected declaration supplies the managed-reference shape.
                // Decide field layout while source bodies still exist in both
                // reference and runtime builds, not after reference-body removal.
                var kind = Text(obj["k"]);
                if ((kind is "callStatic" or "callInstance" or "constrainedCall" or "callLocal" or "new"
                        || ClrBoundNode.IsAny(kind))
                    && (obj["shapeTypes"] ?? obj["sig"] ?? obj["memberSignature"] ?? obj["argTypes"]) is JsonArray parameters
                    && obj["args"] is JsonArray arguments && parameters.Count == arguments.Count)
                    for (var i = 0; i < parameters.Count; i++)
                    {
                        if (SignatureValueTypes.Of(TypeJson.Read(parameters[i])) is not TypeNode.ByRef
                            || arguments[i] is not JsonObject argument) continue;
                        var location = Text(argument["k"]) == "byrefOf" ? argument["inner"] as JsonObject : argument;
                        if (location == null || Text(location["k"]) is not ("field" or "staticField")
                            || TypeJson.OwnerName(location["ownerType"] ?? location["owner"]) is not string owner
                            || Text(location["name"]) is not string name) continue;
                        var key = FieldKey(owner, name, Text(location["k"]) == "staticField", location);
                        if (fields.TryGetValue(key, out var declaration))
                        {
                            _nativeFields.Add(declaration);
                            _nativeFieldNames.Add(key);
                        }
                        else if (_localOwners.Contains(owner)
                            || _references?.HasReferencedFieldLocation(TypeJson.Read(location["ownerType"] ?? location["owner"]) as TypeNode.Fqn,
                                name, key.Item3) != true) continue;
                        // An address use is not a value read. Keep this selected FieldDef through value projection;
                        // other reads of the same referenced field may still use its ordinary carrier getter.
                        location[FieldAddressKey] = true;
                        location[NativeLocationKey] = true;
                    }
                foreach (var (key, child) in obj)
                    if (key != "attrs") FindAddresses(child);
            }
        }
        foreach (var root in inputs) FindAddresses(root);
        foreach (var field in _nativeFields) field[NativeLocationKey] = true;
    }

    internal bool IsNativeField(JsonObject node) => node[NativeLocationKey]?.GetValue<bool>() == true || _nativeFields.Contains(node)
        || IsNativeFieldUse(Text(node["k"]), TypeJson.Read(node["ownerType"] ?? node["owner"]), Text(node["name"]),
            Text(node["companionReceiver"]) is string receiver ? TypeNode.Parse(receiver) : null);

    bool IsNativeFieldUse(string kind, TypeNode ownerType, string name, TypeNode companionReceiver) =>
        kind is "field" or "staticField" or "setField" or "setStaticField" && ownerType is TypeNode.Fqn owner && name != null
        && _nativeFieldNames.Contains(FieldKey(owner.Name, name, kind is "staticField" or "setStaticField", companionReceiver));

    internal bool IsNativeField(BirGraphValue node) => node switch {
        BirFieldDeclaration field => field._nativeLocation.OrDefault(),
        BirRefTypeDeclaration cell => cell._nativeLocation.OrDefault(),
        BirFieldGet field => field._nativeLocation.OrDefault()
            || IsNativeFieldUse(field.Tag, field.OwnerType.OrDefault(), field.Name.OrDefault(), null),
        BirStaticFieldGet field => field._nativeLocation.OrDefault()
            || IsNativeFieldUse(field.Tag, field.OwnerType.OrDefault(), field.Name.OrDefault(), field.CompanionReceiver.OrDefault()),
        BirSetField field => IsNativeFieldUse(field.Tag, field.OwnerType.OrDefault(), field.Name.OrDefault(), null),
        _ => false,
    };

    // Before companion binding allocates physical storage names, the explicit association is part of a
    // field's semantic identity. The same declaration object retains its location role after binding.
    static (string Owner, string Name, bool Static, string Receiver) FieldKey(
        string owner, string name, bool isStatic, JsonObject declarationOrUse) =>
        FieldKey(owner, name, isStatic, Text(declarationOrUse["companionReceiver"]) is string receiver
            ? TypeNode.Parse(receiver) : null);

    static (string Owner, string Name, bool Static, string Receiver) FieldKey(
        string owner, string name, bool isStatic, TypeNode receiver)
    {
        if (receiver == null) return (owner, name, isStatic, null);
        var classifier = CompanionExtensionBinding.ReceiverClassifier(receiver)
            ?? throw new InvalidOperationException("companion extension receiver is not a classifier type: " + TypeNode.ToJson(receiver));
        return (owner, name, isStatic, TypeNode.ToJson(new TypeNode.Fqn(classifier)));
    }

    static string Text(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var text) == true ? text : null;

    internal bool OwnsValueSlots(string owner) => owner != null && !UsesNativeOwnerSlots(owner)
        && (_localOwners.Contains(owner) || _references?.HasDotKtOwner(owner) == true);

    internal bool IsBindingAlias(string owner) => owner != null
        && BindingAlias(owner) != null;

    internal bool UsesNativeOwnerSlots(string owner) => owner != null
        && (IsBindingAlias(owner)
            || _references?.TryResolveClrOwner(owner, out _, out _) == true);

    internal bool HasNativeConstraint(TypeNode.Fqn bound) => UsesNativeOwnerSlots(bound.Name)
        || !_localOwners.Contains(bound.Name)
            && _references?.ResolveNetType(bound.Name, bound.Args?.Length ?? 0) != null;

    internal TypeNode StorageConstraint(TypeNode.Fqn bound) => OwnsValueSlots(bound.Name)
        ? bound.Args is { Length: > 0 }
            ? new TypeNode.Fqn(bound.Name, bound.Args.Select(_ => (TypeNode)new TypeNode.Star()).ToArray()) : bound
        : HasNativeConstraint(bound) ? bound : null;

    internal bool OwnsMethodValueSlots(bool intrinsic, IEnumerable<TypeNode> overrideOwners) =>
        !intrinsic && !overrideOwners.Any(owner => !OwnsValueSlots((owner as TypeNode.Fqn)?.Name));

    // A Kotlin binding alias owns its own value projection; merely overriding that
    // semantic surface does not select an exact imported CLR method signature.
    internal bool HasNativeMethodSignature(JsonObject declaration) =>
        HasNativeMethodSignature(declaration[PhysicalDeclarationSignatureKey]?.GetValue<bool>() == true,
            (declaration["attrs"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Any(attribute => TypeJson.OwnerName(attribute["attr"]) == "kotlin.clr.ClrIntrinsic"),
            (declaration["overrides"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(edge => TypeJson.Read(edge["owner"])));

    internal bool HasNativeMethodSignature(bool physicalSignature, bool intrinsic, IEnumerable<TypeNode> overrideOwners) =>
        physicalSignature || intrinsic || overrideOwners.Any(type => type is TypeNode.Fqn { Name: { } owner }
                && !IsBindingAlias(owner)
                && !OwnsValueSlots(owner));

    public bool UsesStorageArguments(string owner) => false;

    public NullableRepresentationFrame.Role ApplicationRole(string owner, NullableRepresentationFrame.Role role) =>
        role;

    // Frame expansion must retain the source owner until declaration matching is complete. Lowering only a
    // call's type arguments here would compare CLR IReadOnlyList<T> with a selected Kotlin List<T> descriptor.
    // BirTypeLowering owns the eventual alias substitution for both positions.
    public TypeNode ProjectArgumentHead(TypeNode.Fqn source, bool storage, NullableRepresentationFrame frame) =>
        storage && OwnsValueSlots(source.Name) && source.Args is { Length: > 0 }
            && (_localOwners.Contains(source.Name) ? _existentialOwners.Contains(source.Name)
                : _references?.TryExistentialPhysicalOwner(source.Name, out _) == true)
            ? new TypeNode.Fqn(source.Name, source.Args.Select(_ => (TypeNode)new TypeNode.Star()).ToArray())
            : source;

    public static void SelfTest()
    {
        SourcePolicySelfTest();
        var heads = BirGraphDom.ReadBirFileDeclaration(JsonNode.Parse("""
        {"types":[
          {"kind":"class","name":"SourceHead","typeParams":[{"name":"T"}]},
          {"kind":"class","name":"GeneratedHead","generated":true,"typeParams":[{"name":"T"}]}]}
        """));
        var headPolicy = CreateSource(new[] { heads }, new Dictionary<string, string>(), null);
        var headArguments = new TypeNode[] { new TypeNode.Tv("method", 0) };
        var sourceHead = new TypeNode.Fqn("SourceHead", headArguments);
        var generatedHead = new TypeNode.Fqn("GeneratedHead", headArguments);
        if (headPolicy.ProjectArgumentHead(sourceHead, true, null)
                != new TypeNode.Fqn("SourceHead", new TypeNode[] { new TypeNode.Star() })
            || headPolicy.ProjectArgumentHead(sourceHead, false, null) != sourceHead
            || headPolicy.ProjectArgumentHead(generatedHead, true, null) != generatedHead)
            throw new InvalidOperationException("Storage heads must use only declaration-owned existential views");

        var fieldRoot = BirGraphDom.ReadBirFileDeclaration(JsonNode.Parse("""
        {"fileClass":"FieldFrames","types":[{"kind":"class","name":"FieldFrame","typeParams":[{"name":"T"}],
          "fields":[{"name":"native","type":{"t":"tv","scope":"type","i":0},
                     "attrs":[{"attr":{"t":"fqn","name":"kotlin.clr.ClrField"},"args":[]}]},
                    {"name":"ordinary","type":{"t":"tv","scope":"type","i":0}}],
          "methods":[{"name":"pass","params":[],"ret":{"t":"fqn","name":"kotlin.Unit"},"body":[
            {"k":"exprStmt","expr":{"k":"callStatic","sig":[{"t":"byRef","of":{"t":"tv","scope":"method","i":0}}],
             "typeArgs":[{"t":"tv","scope":"type","i":0}],"args":[
              {"k":"field","ownerType":{"t":"fqn","name":"FieldFrame","args":[{"t":"tv","scope":"type","i":0}]},
               "recv":{"k":"this"},"name":"native","memberType":{"t":"tv","scope":"type","i":0},
               "memberOwnerTypeParams":[{"name":"T"}]}]}}]}]}]}
        """));
        fieldRoot.Types = fieldRoot.Types.Value.Append(new BirClassDefinition {
            Kind = "class", Name = "FieldSingleton", Fields = new[] {
                new BirFieldDeclaration { Name = "INSTANCE", Vis = "private", Type = new TypeNode.Fqn("kotlin.String") },
                new BirFieldDeclaration { Name = "INSTANCE", Static = true, Type = new TypeNode.Fqn("FieldSingleton") },
            },
        }).ToArray();
        var fieldPolicy = CreateSource(new[] { fieldRoot }, new Dictionary<string, string>(), null);
        NullableRepresentationMaterialization.Apply(new[] { fieldRoot }, _ => false, policy: fieldPolicy);
        var fieldOwner = (BirClassDefinition)fieldRoot.Types.Value[0];
        var addressedField = (BirFieldGet)((BirCallStatic)((BirExpressionStatement)fieldOwner.Methods.Value[0].Body.Value[0]).Expr.Value).Args.Value[0];
        if (fieldOwner.Fields.Value[0].Type.Value != new TypeNode.Tv("type", 0)
            || fieldOwner.Fields.Value[1].Type.Value != new TypeNode.Tv("type", 1)
            || addressedField.MemberType.Value != new TypeNode.Tv("type", 0)
            || ((TypeNode.Fqn)addressedField.OwnerType.Value).Args.Length != 2)
            throw new InvalidOperationException("Native field allocation changed an unrelated field or lost declaration correspondence");
        var importedAddressRoot = BirGraphDom.ReadBirFileDeclaration(JsonNode.Parse("""
          {"fileClass":"ImportedFieldAddresses","methods":[{"name":"pass","typeParams":[{"name":"T"}],"params":[],"body":[
            {"k":"exprStmt","expr":{"k":"callStatic","shapeTypes":[{"t":"byRef","of":{"t":"tv","scope":"method","i":0}}],"args":[
              {"k":"field","ownerType":{"t":"fqn","name":"System.Runtime.CompilerServices.StrongBox`1","args":[{"t":"tv","scope":"method","i":0}]},
               "name":"Value","recv":{"k":"local","name":"box"},"ret":{"t":"tv","scope":"method","i":0}}]}},
            {"k":"exprStmt","expr":{"k":"field","ownerType":{"t":"fqn","name":"System.Runtime.CompilerServices.StrongBox`1","args":[{"t":"tv","scope":"method","i":0}]},
             "name":"Value","recv":{"k":"local","name":"box"},"ret":{"t":"tv","scope":"method","i":0}}}]}]}
          """));
        var importedPolicy = CreateSource(new[] { importedAddressRoot }, new Dictionary<string, string>(),
            ReferenceMetadataIndex.Build(new[] { typeof(System.Runtime.CompilerServices.StrongBox<>).Assembly.Location }));
        var importedBody = importedAddressRoot.Methods.Value[0].Body.Value;
        var addressedImport = (BirFieldGet)((BirCallStatic)((BirExpressionStatement)importedBody[0]).Expr.Value).Args.Value[0];
        if (!importedPolicy.IsNativeField(addressedImport) || !addressedImport._nativeFieldAddress.OrDefault()
            || importedPolicy.IsNativeField((BirGraphValue)((BirExpressionStatement)importedBody[1]).Expr.Value))
            throw new InvalidOperationException("A selected referenced FieldDef address was lost or changed an ordinary value read");
        NullableRepresentationMaterialization.Apply(new[] { importedAddressRoot }, _ => false, policy: importedPolicy);
        if (addressedImport.Ret.Value != new TypeNode.Tv("method", 0))
            throw new InvalidOperationException("Referenced field address result lost its native witness frame");
        var companionFields = BirGraphDom.ReadBirFileDeclaration(JsonNode.Parse("""
        {"fileClass":"CompanionFieldFrames","fields":[
          {"name":"slot","static":true,"type":{"t":"fqn","name":"kotlin.String"},
           "companionReceiver":"{\"t\":\"fqn\",\"name\":\"First\"}",
           "attrs":[{"attr":{"t":"fqn","name":"kotlin.clr.ClrField"}}]},
          {"name":"slot","static":true,"type":{"t":"fqn","name":"kotlin.String"},
           "companionReceiver":"{\"t\":\"fqn\",\"name\":\"Second\"}"},
          {"name":"slot","static":true,"type":{"t":"fqn","name":"kotlin.String"},
           "companionReceiver":"{\"t\":\"fqn\",\"name\":\"Third\"}"},
          {"name":"slot","static":true,"type":{"t":"fqn","name":"kotlin.String"}}],
         "methods":[{"body":[{"k":"exprStmt","expr":{"k":"callStatic","sig":[{"t":"byRef","of":{"t":"fqn","name":"kotlin.String"}}],
          "args":[{"k":"byrefOf","inner":{"k":"staticField","ownerType":{"t":"fqn","name":"CompanionFieldFrames"},
           "name":"slot","companionReceiver":"{\"t\":\"fqn\",\"name\":\"Second\"}"}}]}}]}]}
        """));
        var companionPolicy = CreateSource(new[] { companionFields }, new Dictionary<string, string>(), null);
        foreach (var (index, native) in new[] { (0, true), (1, true), (2, false), (3, false) })
        {
            var field = companionFields.Fields.Value[index];
            var use = new BirStaticFieldGet { OwnerType = new TypeNode.Fqn("CompanionFieldFrames"),
                Name = "slot", CompanionReceiver = field.CompanionReceiver };
            if (companionPolicy.IsNativeField(field) != native || companionPolicy.IsNativeField(use) != native)
                throw new InvalidOperationException("Native companion storage selection leaked into a sibling field");
        }
        var nullableRead = BirGraphDom.ReadBirFileDeclaration(JsonNode.Parse("""
        {"types":[{"name":"NativeNullableRead","kind":"class","typeParams":[{"name":"T"}],"fields":[
          {"name":"value","type":{"t":"nullable","of":{"t":"tv","scope":"type","i":0}},
           "attrs":[{"attr":{"t":"fqn","name":"kotlin.clr.ClrField"}}]}],"methods":[
          {"name":"read","params":[],"ret":{"t":"nullable","of":{"t":"fqn","name":"kotlin.Any"}},
           "body":[{"k":"return","value":{"k":"field","ownerType":{"t":"fqn","name":"NativeNullableRead","args":[{"t":"tv","scope":"type","i":0}]},
             "name":"value","recv":{"k":"this"},"ret":{"t":"nullable","of":{"t":"tv","scope":"type","i":0}},
             "sty":{"t":"nullable","of":{"t":"tv","scope":"type","i":0}}}}]}]}]}
        """));
        var readPolicy = CreateSource(new[] { nullableRead }, new Dictionary<string, string>(), null);
        NullableRepresentationMaterialization.Apply(new[] { nullableRead }, _ => false, policy: readPolicy);
        var readOwner = (BirClassDefinition)nullableRead.Types.Value[0];
        var physicalRead = (BirFieldGet)((BirReturn)readOwner.Methods.Value[0].Body.Value[0]).Value.Value;
        var physicalField = readOwner.Fields.Value[0].Type.Value;
        if (physicalField is not TypeNode.Tv || physicalRead.Ret.Value != physicalField || physicalRead.Sty.Value != physicalField)
            throw new InvalidOperationException("Native nullable field read lost its physical result frame");
        var policy = new GenericRepresentationPolicy(new Dictionary<string, string> {
            ["kotlin.collections.Map"] = "System.Collections.Generic.IDictionary",
            ["kotlin.collections.Collection"] = "System.Collections.Generic.IReadOnlyCollection",
        });
        if (!policy.UsesNativeOwnerSlots("kotlin.collections.Map`2")
            || !policy.UsesNativeOwnerSlots("kotlin.collections.Collection`1")
            || policy.UsesNativeOwnerSlots("Unrelated`1"))
            throw new InvalidOperationException("Native owner roles must use the selected alias identity, including exact metadata owners");
        var root = BirGraphDom.ReadBirFileDeclaration(JsonNode.Parse("""
        {"fileClass":"BindingRoles","methods":[
          {"name":"choose","declarationId":"choose","typeParams":[{"name":"T"}],"params":[
            {"name":"array","type":{"t":"array","elem":{"t":"tv","scope":"method","i":0}}},
            {"name":"map","type":{"t":"fqn","name":"kotlin.collections.Map","args":[
              {"t":"fqn","name":"kotlin.String"},{"t":"tv","scope":"method","i":0}]}}],
           "ret":{"t":"tv","scope":"method","i":0},"body":[{"k":"return","value":
             {"k":"arrayGet","array":{"k":"local","name":"array"},"index":{"k":"const","type":{"t":"fqn","name":"kotlin.Int"},"value":0},
              "elem":{"t":"tv","scope":"method","i":0}}}]},
          {"name":"use","params":[],"ret":{"t":"fqn","name":"kotlin.Unit"},"body":[
            {"k":"exprStmt","expr":{"k":"callStatic","declarationId":"choose","typeArgs":[
              {"t":"fqn","name":"kotlin.collections.Collection","args":[{"t":"fqn","name":"kotlin.String"}]}],"args":[]}}]}]}
        """));
        NullableRepresentationMaterialization.Apply(new[] { root }, _ => false, policy: policy);
        var choose = root.Methods.Value[0];
        if (choose.TypeParams.Value.Length != 2
            || choose.Ret.Value != new TypeNode.Tv("method", 1)
            || choose.Params.Value[0].Type.Value != new TypeNode.Array(new TypeNode.Tv("method", 0))
            || choose.Params.Value[1].Type.Value is not TypeNode.Fqn { Args: { } mapArguments }
            || mapArguments[1] != new TypeNode.Tv("method", 0))
            throw new InvalidOperationException("Binding policy changed a native array or map element's canonical representation");
        var arguments = ((BirCallStatic)((BirExpressionStatement)root.Methods.Value[1].Body.Value[0]).Expr.Value).TypeArgs.Value;
        if (arguments.Length != 2 || arguments.Any(argument => argument is not TypeNode.Fqn { Name: "kotlin.collections.Collection" }))
            throw new InvalidOperationException("Frame expansion changed a collection's source declaration identity");

        var sourceOverride = TypeJson.Write(new TypeNode.Fqn("Outer.Inner", new TypeNode[] { new TypeNode.Tv("type", 0) }));
        var innerRoot = new JsonObject { ["fileClass"] = "InnerRoles", ["types"] = new JsonArray(new JsonObject {
            ["kind"] = "class", ["name"] = "Outer.Inner", ["semanticOwner"] = "Outer",
            ["typeParams"] = new JsonArray(new JsonObject { ["name"] = "T" }, new JsonObject { ["name"] = "$storage0" }),
            ["outerTypeParamCount"] = 2, ["mods"] = new JsonObject { ["inner"] = true },
            ["methods"] = new JsonArray(new JsonObject { ["overrides"] = new JsonArray(new JsonObject {
                ["owner"] = sourceOverride.DeepClone(), ["member"] = "run",
            }) }),
        }) };
        TypeOwnershipLowering.ProjectInnerApplications(new[] { innerRoot }, null);
        if (!JsonNode.DeepEquals(innerRoot["types"][0]["methods"][0]["overrides"][0]["owner"], sourceOverride))
            throw new InvalidOperationException("Physical inner capture projection rewrote a source override edge");
        Console.WriteLine("[generic binding roles] self-test OK (array root, map storage, exact source override frames)");
    }
}
