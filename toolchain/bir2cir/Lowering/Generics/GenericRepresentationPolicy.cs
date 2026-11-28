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
    readonly HashSet<string> _localOwners = new(StringComparer.Ordinal);
    readonly Dictionary<string, (string Visibility, string Enclosing)> _sourceVisibility = new(StringComparer.Ordinal);
    IReadOnlyDictionary<string, SupertypeGraph.Def> _sourceHierarchy = new Dictionary<string, SupertypeGraph.Def>();
    readonly HashSet<string> _existentialOwners = new(StringComparer.Ordinal);
    readonly HashSet<(string Owner, string Name, bool Static, string Receiver)> _nativeFieldNames = new();
    bool _sourceContinuationPending;

    GenericRepresentationPolicy(IReadOnlyDictionary<string, string> aliases,
        ReferenceMetadataIndex references, bool sourceOwned)
    {
        _aliases = new Dictionary<string, string>(aliases, StringComparer.Ordinal);
        _references = references;
        _sourceContinuationPending = sourceOwned;
    }

    public GenericRepresentationPolicy(IReadOnlyDictionary<string, string> aliases,
        ReferenceMetadataIndex references = null)
        : this(aliases, references, sourceOwned: false) { }

    // The current physical inventory can grow without granting source carrier
    // eligibility. Source preparation and its owning continuation share this set.
    internal void RegisterCurrentPhysicalOwners(IEnumerable<string> names, IReadOnlyDictionary<string, string> aliases)
    {
        foreach (var name in names) _localOwners.Add(name);
        foreach (var (name, target) in aliases) _aliases[name] = target;
    }

    internal string BindingAlias(string owner) => owner == null ? null
        : _aliases.GetValueOrDefault(ReferenceMetadataIndex.BareOwnerFqn(owner));

    // The late refresh consumes selected declaration vectors, not inferred
    // operand types. Keep its precedence independent of the early source reader.
    static T NativeRefreshParameters<T>(T shapes, T signature, T memberSignature, T argumentTypes) where T : class
        => shapes ?? signature ?? memberSignature ?? argumentTypes;

    static bool NativeRefreshCall(string kind) => kind is "callStatic" or "callInstance" or "constrainedCall"
        or "callLocal" or "new" || ClrBoundNode.IsAny(kind);

    static bool NativeRefreshAddress(TypeNode parameter) => SignatureValueTypes.Of(parameter) is TypeNode.ByRef;

    static bool NativeRefreshLocation(string kind, out bool isStatic)
    {
        isStatic = kind == "staticField";
        return isStatic || kind == "field";
    }
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
        string owner, string name, bool isStatic, TypeNode receiver)
    {
        if (receiver == null) return (owner, name, isStatic, null);
        var classifier = CompanionExtensionBinding.ReceiverClassifier(receiver)
            ?? throw new InvalidOperationException("companion extension receiver is not a classifier type: " + TypeNode.ToJson(receiver));
        return (owner, name, isStatic, TypeNode.ToJson(new TypeNode.Fqn(classifier)));
    }


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

    // The storage witness may be an existential interface even when the value's concrete
    // class derives from this bound. A projected generic Kotlin bound is itself an
    // existential interface; it is the nominal class constraints that cannot be copied.
    bool IsNominalStorageBound(TypeNode bound)
    {
        if (bound is not TypeNode.Fqn named || OwnsValueSlots(named.Name) && named.Args is { Length: > 0 }) return false;
        var physical = BindingAlias(named.Name);
        if (physical == null && _references?.TryResolveClrOwner(named.Name, out var alias, out _) == true) physical = alias;
        if (physical != null) return _references?.IsClassType(new TypeNode.Fqn(physical, named.Args)) == true;
        return _sourceHierarchy.TryGetValue(named.Name, out var local) ? local.Kind == "class"
            : _references?.IsClassType(named) == true;
    }

    // A native generic argument needs one witness for the conjunction, even
    // when the Kotlin declaration has only interface bounds. Publish that
    // storage demand so a generic caller can forward it without a value operand.
    internal bool HasJointStorageBounds(TypeNode[] bounds, bool nativeApplication = false)
    {
        var proofs = bounds.OfType<TypeNode.Fqn>().Where(b => !IsNominalStorageBound(b) &&
            CanReferenceStorageProof(b)).Distinct().Take(2).Count();
        return proofs != 0 && (bounds.Any(IsNominalStorageBound) || nativeApplication && proofs == 2);
    }

    // A virtual implementation may be compiled after the declaring assembly. When a
    // class bound is separated from multiple storage-safe interface proofs, publish
    // one storage witness for their conjunction independently of the current body.
    internal bool NeedsVirtualStorageWitness(IEnumerable<TypeNode.Fqn> constraints)
    {
        var bounds = constraints.Select(StorageConstraint)
            .Where(bound => bound != null).Distinct().ToArray();
        if (!bounds.Any(IsNominalStorageBound)) return false;
        // An interface can supply both proofs through inheritance. The virtual
        // convention must not depend on whether its parents were spelled out.
        var contracts = bounds.OfType<TypeNode.Fqn>().Where(b => !IsNominalStorageBound(b)
            && !NeedsNativeWitness(b) && CanReferenceStorageProof(b)).ToArray();
        return SupertypeGraph.Reachable(new SupertypeGraph.Def { Interfaces = contracts }, _sourceHierarchy, _references, sourceFrames: true)
            .Where(edge => edge.isInterface).Select(edge => StorageConstraint(edge.spec)).OfType<TypeNode.Fqn>()
            .Where(proof => !NeedsNativeWitness(proof) && CanReferenceStorageProof(proof))
            .Distinct().Take(2).Count() == 2;
    }

    // A Kotlin virtual method cannot know which native generic its future
    // override will construct. A single class or interface bound may already
    // entail the joint storage proofs without spelling those interfaces here.
    internal bool NeedsOpenMethodStorage(IEnumerable<TypeNode.Fqn> constraints)
    {
        var proofs = new HashSet<TypeNode>();
        foreach (var constraint in constraints)
        {
            if (StorageConstraint(constraint) is not TypeNode.Fqn bound) continue;
            var nominal = IsNominalStorageBound(bound);
            var root = nominal ? new SupertypeGraph.Def { Base = bound } : new SupertypeGraph.Def { Interfaces = [bound] };
            foreach (var (parent, isInterface) in SupertypeGraph.Reachable(root, _sourceHierarchy, _references, sourceFrames: true))
                if (isInterface && StorageConstraint(parent) is {} proof && !NeedsNativeWitness(proof) &&
                    CanReferenceStorageProof(proof) && proofs.Add(proof) && proofs.Count == 2) return true;
        }
        return false;
    }

    // Closed native interfaces (and invariant native applications even when open)
    // are proven by the native argument, not by the Kotlin value's storage type.
    internal bool NeedsNativeWitness(TypeNode proof)
    {
        if (proof is not TypeNode.Fqn { Args.Length: > 0 } named || !HasNativeConstraint(named)
            || _references?.IsInterfaceType(named) != true || !CanReferenceStorageProof(named)) return false;
        if (_references.IsInvariantForeignConstruction(named)) return true;
        var dependent = false;
        BirGraphNameFields.VisitTypeNodes(named, t => dependent |= t is TypeNode.Tv or TypeNode.Star or TypeNode.Projection);
        return !dependent;
    }

    internal IEnumerable<TypeNode> InheritedNativeWitnesses(TypeNode bound)
    {
        if (bound is not TypeNode.Fqn named) yield break;
        var start = new SupertypeGraph.Def { Base = named };
        foreach (var (parent, isInterface) in SupertypeGraph.Reachable(start, _sourceHierarchy, _references, sourceFrames: true))
            if (isInterface && NeedsNativeWitness(parent)) yield return parent;
    }

    internal bool JoinsNativeWitness(TypeNode bound) => NeedsNativeWitness(bound)
        || CanReferenceStorageProof(bound) && InheritedNativeWitnesses(bound).Any();

    internal IEnumerable<TypeNode> NativeProofsFromClass(TypeNode[] bounds) =>
        bounds.Where(IsNominalStorageBound).SelectMany(InheritedNativeWitnesses);

    internal IEnumerable<(TypeNode Bound, bool Erased)> StorageBoundRows(TypeNode bound)
    {
        // A Kotlin value can use an existential storage type that implements no single
        // closed construction of this native interface. Its proof belongs to the native use.
        bool StorageProof(TypeNode proof) => CanReferenceStorageProof(proof) && !NeedsNativeWitness(proof);
        if (!IsNominalStorageBound(bound) && StorageProof(bound)) { yield return (bound, false); yield break; }
        yield return (bound, true);
        var start = new SupertypeGraph.Def { Base = (TypeNode.Fqn)bound };
        foreach (var (parent, isInterface) in SupertypeGraph.Reachable(start, _sourceHierarchy, _references, sourceFrames: true))
            if (isInterface && StorageConstraint(parent) is { } proof && StorageProof(proof)) yield return (proof, false);
    }

    internal void SetStorageBounds(BirTypeParameter parameter, TypeNode[] bounds)
    {
        var rows = bounds.SelectMany(StorageBoundRows).ToArray();
        if (rows.Any(row => !row.Erased)) parameter.Constraints = rows.Where(row => !row.Erased).Select(row => row.Bound).Distinct().ToArray();
        if (rows.Any(row => row.Erased)) parameter.ErasedInnerConstraints = rows.Where(row => row.Erased).Select(row => row.Bound).ToArray();
    }

    // Inferred storage evidence is part of the callable ABI. It must be usable
    // both here and in another DLL forwarding its own open storage parameter.
    bool CanReferenceStorageProof(TypeNode proof)
    {
        var accessible = true;
        BirGraphNameFields.VisitTypeNodes(proof, node => {
            if (!accessible || node is not TypeNode.Fqn named) return;
            if (!_localOwners.Contains(named.Name))
            {
                if (_references?.IsNonPublicStorageType(named) == true) accessible = false;
                return;
            }
            for (var owner = named.Name; owner != null && _sourceVisibility.TryGetValue(owner, out var scope); owner = scope.Enclosing)
                if (scope.Visibility is not (null or "public")) { accessible = false; break; }
        });
        return accessible;
    }

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

    public bool UsesElementArguments(string owner) =>
        BindingAlias(owner) == "System.Collections.Generic.List";

    public NullableRepresentationFrame.Role ApplicationRole(string owner, NullableRepresentationFrame.Role role) =>
        UsesElementArguments(owner) ? role switch {
            NullableRepresentationFrame.Role.Ordinary => NullableRepresentationFrame.Role.Element,
            NullableRepresentationFrame.Role.Nullable => NullableRepresentationFrame.Role.NullableElement,
            _ => role,
        } : role;

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
        NativeRefreshSelfTest();
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

        var sourceOverride = new TypeNode.Fqn("Outer.Inner", new TypeNode[] { new TypeNode.Tv("type", 0) });
        var overrideEdge = new BirOverride { Owner = sourceOverride, Member = "run", Kind = "method", Arity = 0 };
        BirTypeParameter[] OwnerParameters() => new[] {
            new BirTypeParameter { Name = "T" }, new BirTypeParameter { Name = "$storage0" },
        };
        var innerRoot = new BirFileDeclaration { FileClass = "InnerRoles", Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Kind = "class", Name = "Outer", TypeParams = OwnerParameters() },
            new BirClassDefinition { Kind = "class", Name = "Outer.Inner", SemanticOwner = "Outer",
                TypeParams = OwnerParameters(), OuterTypeParamCount = 2, Mods = new BirModifiers { Inner = true },
                Methods = new[] { new BirFunctionDeclaration { Name = "run", Params = Array.Empty<BirParameter>(),
                    Ret = new TypeNode.Fqn("kotlin.Unit"), Overrides = new[] { overrideEdge } } } },
        } };
        TypeOwnershipLowering.ProjectInnerApplications(new[] { innerRoot }, null);
        if (!ReferenceEquals(overrideEdge.Owner.Value, sourceOverride)
            || overrideEdge.Owner.Value is not TypeNode.Fqn { Args: [TypeNode.Tv { Scope: "type", I: 0 }] })
            throw new InvalidOperationException("Physical inner capture projection rewrote a source override edge");
        Console.WriteLine("[generic binding roles] self-test OK (array root, map storage, exact source override frames)");
    }
}
