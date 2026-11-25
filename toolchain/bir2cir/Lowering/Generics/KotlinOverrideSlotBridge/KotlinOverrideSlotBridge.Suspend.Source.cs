using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinOverrideSlotBridge
{
    internal static void PrepareSuspendValueBridges(IReadOnlyList<BirFileDeclaration> roots, ValueTypeOracle isValue,
        ReferenceMetadataIndex refs, IReadOnlySet<string> localTypeNames, bool refBuild, GenericRepresentationPolicy representations,
        IReadOnlySet<CovariantInterfaceReturnBridge.SourceBridgedSlot> covariantBridgedSlots,
        IReadOnlyDictionary<BirFunctionDeclaration, TypeNode[]> sourceParameters,
        Func<TypeNode, bool> hasClassifier,
        Action<BirFileDeclaration, IBirTypeDeclaration, BirFunctionDeclaration, BirGraphValue> onMaterialized = null)
    {
        var index = CreateSourceSelection(roots);
        var views = new OverrideViews(index.Declarations);
        var files = new Dictionary<IBirTypeDeclaration, BirFileDeclaration>(ReferenceEqualityComparer.Instance);
        foreach (var file in roots)
            foreach (var declaration in file.Types.OrEmpty()) files[declaration] = file;
        var frames = refs.NullableTypeFrames.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var definition in index.Definitions.Values)
            if (views.Owner(definition).Frame is { } frame) frames[definition.Name] = frame;
        var applications = new InnerApplicationFrames(roots, refs).Project(frames, retainPhysicalOrder: true);
        var annotationNames = localTypeNames ?? index.Definitions.Keys.ToHashSet(StringComparer.Ordinal);
        TypeNode[] AnnotationArguments(TypeNode.Fqn type) => BirTypeLowering.AnnotationArguments(type,
            refs.Aliases, isValue, refs.PhysicalTypeNames, annotationNames, refBuild, refs.NullableTypeFrames);
        TypeNode Physical(TypeNode type) => BirTypeLowering.LowerPhysicalType(type, refs.Aliases, isValue,
            refs.PhysicalTypeNames, typeArg: false, localTypeNames, nullableFrames: refs.NullableTypeFrames);
        var originals = sourceParameters?.ToDictionary(pair => views.Method(pair.Key), pair => pair.Value);
        foreach (var definition in index.Definitions.Values.Where(definition => definition.Kind is "class" or "interface").ToArray())
        {
            var owner = views.Owner(definition);
            var declaration = index.Declarations[definition.Name];
            if (!owner.HasMethods) continue;
            var ownArguments = ClassOwnArgs(definition);
            var mapping = new NullableRepresentationTypes(owner.Frame, null, applications, isValue, policy: representations);
            var selectedInherited = SelectInheritedMethods(owner, index.Definitions, views, refs, isValue,
                SourceInheritedFacts(declaration, false), suspendValues: true, defaults: false, collectionQueries: false);
            var inherited = new Dictionary<OverrideMethod, SelectedInheritedSourceMethod>();
            foreach (var selected in selectedInherited) inherited.Add(views.Method(selected.Method), selected);
            var candidates = owner.Methods.Concat(inherited.Keys).ToArray();
            var signatures = inherited.ToDictionary(pair => pair.Key, pair => pair.Value.SelectionParameters);
            var bridges = new Dictionary<string, BirFunctionDeclaration>(StringComparer.Ordinal);
            var claims = new Dictionary<(OverrideMethod Body, string Shape), List<OverrideMethod>>();
            var ordinal = 0;
            SelectSlots(owner, index.Definitions, views, candidates, ownArguments, isValue, refs,
                Phase.SuspendValueBridges, refBuild, localTypeNames, mapping, representations, (_, _) => false,
                Fill, signatures, originals);

            bool CanPassAsObject(TypeNode type, OverrideMethod implementation)
            {
                type = SignatureValueTypes.Of(Physical(type));
                if (type is TypeNode.Nullable nullable) return CanPassAsObject(nullable.Of, implementation);
                if (type is TypeNode.Oblivious oblivious) return CanPassAsObject(oblivious.Of, implementation);
                if (type is TypeNode.Fqn named) return !refs.IsByRefLikeFqn(named);
                if (type is TypeNode.Tv variable)
                {
                    var parameters = variable.Scope == "type" ? TypeParameterFrame.Declarations(declaration).ToArray()
                        : implementation.TypeParameters ?? [];
                    return variable.I >= 0 && variable.I < parameters.Length
                        && !parameters[variable.I].SpecialConstraints.OrEmpty().Contains("allowsRefStruct");
                }
                return type is TypeNode.Array or TypeNode.Fn;
            }

            void Fill(SelectedSlot slot)
            {
                var implementation = slot.Implementation;
                inherited.TryGetValue(implementation, out var selectedBody);
                if (selectedBody != null && !slot.IsInterface) return;
                var reimplements = slot.IsInterface && definition.Kind == "class"
                    && (ReachesFromDeclaredInterface(definition, slot.SemanticSpec, index.Definitions, refs)
                        || slot.Referenced && ReachesFromDeclaredPhysicalInterface(definition, slot.DescriptorSpec,
                            index.Definitions, refs, isValue, localTypeNames));
                // Inheriting both the class body and its interface map introduces no new obligation.
                var inheritedDefault = slot.IsInterface && definition.Kind == "class" && !reimplements
                    && slot.HasDefault && (selectedBody?.IsDefault != false || slot.Referenced
                        && refs.IntroducesBodyOutsideInterfaceMap(selectedBody.Owner, selectedBody.MetadataToken, slot.DescriptorSpec));
                if (slot.IsInterface && definition.Kind == "class" && !reimplements && !inheritedDefault) return;
                var fit = AssessFit(implementation, ownArguments, slot.Parameters, slot.Return, slot.UnitValueReturn,
                    slot.Signature, slot.IsInterface, slot.Referenced, slot.DescriptorSpec, slot.PropertyAccessor,
                    Phase.SuspendValueBridges, selectedBody != null, isValue, refs, localTypeNames, mapping,
                    Physical, (_, _) => false,
                    (source, target) => FBoundStarProjectionErasure.IsClrAssignable(source, target, index.Declarations, refs, physicalTypes: true),
                    type => CanPassAsObject(type, implementation), (_, _) => false, null,
                    () => implementation.Source != null && covariantBridgedSlots?.Contains(
                        new CovariantInterfaceReturnBridge.SourceBridgedSlot(implementation.Source,
                            CovariantInterfaceReturnBridge.DescriptorKey(slot.DescriptorSpec, slot.DescriptorMember,
                            implementation.Arity, slot.Parameters, slot.Return, refs, isValue))) == true,
                    hasClassifier);
                if (fit == null) return;
                ApplyDeclarationEdits(implementation, ownArguments, slot.Parameters, slot.Return, fit,
                    Phase.SuspendValueBridges, isValue, AnnotationArguments);
                var needsSignature = fit.Parameters.Contains(Fit.Bridge) || fit.Return == Fit.Bridge;
                var needsProperty = slot.IsInterface && slot.PropertyAccessor != null
                    && slot.DescriptorMember != implementation.Name
                    && (definition.Kind != "class" || reimplements || inheritedDefault);
                var needsExactInterface = slot.IsInterface && (selectedBody != null
                    || implementation.Parameters.Any(parameter => parameter.Signature != null)
                    || definition.Kind == "interface" && !implementation.Abstract
                    || definition.Kind == "class" && reimplements && slot.DescriptorMember != implementation.Name
                    || definition.Kind == "class" && inheritedDefault);
                var needsExactBase = !slot.IsInterface && !slot.Referenced && slot.DescriptorMember != implementation.Name;
                if (!needsSignature && !needsProperty && !needsExactInterface && !needsExactBase) return;
                var typeParams = SubstituteParameters(slot.TypeParameters, slot.SemanticSpec.Args ?? []);
                if (!slot.IsInterface)
                {
                    string Key(TypeNode type) => SupertypeGraph.TypeKey(BirTypeLowering.CanonicalPhysicalSlotType(Physical(type)));
                    var shape = slot.DescriptorMember + "`" + implementation.Arity + "("
                        + string.Join(",", (slot.Signature ?? slot.Parameters).Select(Key)) + ")->" + Key(slot.Return)
                        + MethodTypeParameterShapeKey(typeParams, ownArguments,
                            type => BirTypeLowering.CanonicalPhysicalSlotType(Physical(type)))
                        + (slot.UnitValueReturn ? "[value-return]" : "");
                    var claim = (implementation, shape);
                    var selectedId = slot.SelectedReferencedDeclarationId ?? slot.SelectedLocal?.DeclarationId;
                    if (claims.TryGetValue(claim, out var declarations) && selectedId != null
                        && declarations.Any(previous => OverridesInto(previous, index.Definitions, slot.SemanticSpec.Name,
                            slot.IdentityName, slot.PropertyAccessor switch { "get" => "getter", "set" => "setter", _ => "method" }, selectedId))) return;
                    if (slot.SelectedLocal != null)
                    {
                        if (declarations == null) claims[claim] = declarations = new List<OverrideMethod>();
                        declarations.Add(slot.SelectedLocal);
                    }
                }
                if (!needsSignature && definition.Kind != "interface" && selectedBody == null)
                {
                    implementation.SetVirtual();
                    var exact = SourceImplDescriptor(slot.DescriptorSpec, slot.DescriptorMember, implementation.Arity,
                        slot.Signature ?? slot.Parameters, slot.Return, typeParams, slot.UnitValueReturn);
                    if (slot.Signature == null)
                        for (var at = 0; at < implementation.Parameters.Length; at++)
                            if (implementation.Parameters[at].Signature is { } discriminator)
                                exact.Params.Value[at] = new TypeNode.Mod(false, SupertypeGraph.SubstOwnerTvs(
                                    FunctionSignatureIdentity.DeclarationType(discriminator), ownArguments), slot.Parameters[at]);
                    exact = AddSourceImpl(implementation.Source, exact, slot.IsInterface);
                    if (slot.SelectedLocal != null)
                        DeclarationSignatureIdentity.RecordSelectedImplementationSlot(implementation.Source, slot.SelectedLocal.Source);
                    onMaterialized?.Invoke(files[declaration], declaration, null, exact);
                    return;
                }
                if (definition.Kind == "interface" && implementation.Abstract) return;
                implementation.SetVirtual();
                string PhysicalKey(TypeNode type) => SupertypeGraph.TypeKey(Physical(type));
                var body = string.Join(",", implementation.Parameters.Select(parameter => parameter.Type is { } type ? SupertypeGraph.TypeKey(type) : "?"));
                var bridgeKey = slot.IdentityName + "`" + implementation.Arity + "("
                    + string.Join(",", (slot.Signature ?? slot.Parameters).Select(PhysicalKey)) + ")->" + PhysicalKey(slot.Return)
                    + "{" + implementation.Name + "<" + MethodTypeParameterShapeKey(implementation.TypeParameters, ownArguments)
                    + ">(" + body + ")}";
                if (selectedBody != null) bridgeKey += "[body-owner:" + SupertypeGraph.TypeKey(selectedBody.Owner) + "]";
                if (definition.Kind == "interface") bridgeKey += "[slot:" + SupertypeGraph.TypeKey(slot.DescriptorSpec) + "::" + slot.DescriptorMember + "]";
                if (slot.UnitValueReturn) bridgeKey += "[value-return]";
                var authored = false;
                if (!bridges.TryGetValue(bridgeKey, out var bridge))
                {
                    bridge = selectedBody == null
                        ? BuildBridgeSource(declaration, implementation.Source, slot.Parameters, slot.Return,
                            $"dotkt$ovslot${SafeName(slot.IdentityName)}${ordinal++}", isValue, refs,
                            unitValueReturn: slot.UnitValueReturn)
                        : BuildBridgeSource(declaration, selectedBody, slot.Parameters, slot.Return,
                            $"dotkt$ovslot${SafeName(slot.IdentityName)}${ordinal++}", isValue, refs,
                            callOwner: selectedBody.Owner, callMember: implementation.ExplicitClrName,
                            unitValueReturn: slot.UnitValueReturn);
                    if (slot.Signature != null)
                        for (var at = 0; at < slot.Signature.Length; at++)
                            if (slot.Signature[at] is TypeNode.Mod { Req: false } signature)
                                bridge.Params.Value[at].FunctionSignatureType = BirGraphValue.CopyType(signature.M);
                    if (!fit.Parameters.Contains(Fit.Bridge)) bridge.SuspendTaskOnlyBridge = true;
                    if (slot.PropertyAccessor == null)
                        RoundtripMetadata.AddSourceMethodIdentity(bridge, slot.IdentityName, implementation.DeclarationId);
                    bridges.Add(bridgeKey, bridge);
                    declaration.Methods = declaration.Methods.Value.Append(bridge).ToArray();
                    authored = true;
                    if (slot.PropertyAccessor != null)
                    {
                        var association = implementation.Source?.PropertyAssociation.OrDefault()
                            ?? implementation.Header?.PropertyAssociation.OrDefault();
                        if (definition.Kind == "interface")
                            KotlinPropertyAccessors.MarkExactInterfaceBridgeProperty(bridge, slot.IdentityName, slot.PropertyAccessor, association);
                        else KotlinPropertyAccessors.AssociateBridgeProperty(declaration, bridge, slot.IdentityName, slot.PropertyAccessor,
                            association, slot.Parameters, slot.Return);
                    }
                }
                var descriptor = SourceImplDescriptor(slot.DescriptorSpec, slot.DescriptorMember, implementation.Arity,
                    slot.Signature ?? slot.Parameters, slot.Return, typeParams, slot.UnitValueReturn);
                if (slot.ReferenceMember != null && slot.ReferenceMember != slot.DescriptorMember) descriptor.ReferenceMember = slot.ReferenceMember;
                descriptor = AddSourceImpl(bridge, descriptor, slot.IsInterface);
                if (!slot.IsInterface && implementation.Source is { } source
                    && source.PendingOverrideOwner.OrDefault() is { } pending
                    && (slot.SemanticReturn == null ? ReferenceMetadataIndex.SourceDeclarationDescribesCall(pending, slot.DescriptorSpec)
                        : pending is TypeNode.Fqn pendingSpec && SupertypeGraph.Reachable(new SupertypeGraph.Def { Base = pendingSpec }, index.Definitions, refs)
                            .Any(edge => !edge.isInterface && ReferenceMetadataIndex.SourceDeclarationDescribesCall(edge.spec, slot.SemanticSpec)))
                    && (source.PendingOverrideMember.OrDefault() ?? source.Name.OrDefault()) == slot.DescriptorMember
                    && source.PendingOverrideReturn.OrDefault() is { } pendingReturn
                    && ReferenceMetadataIndex.SourceDeclarationDescribesCall(pendingReturn, slot.SemanticReturn ?? slot.Return))
                {
                    source.PendingOverrideOwner = default; source.PendingOverrideMember = default; source.PendingOverrideReturn = default;
                }
                // Publish the exact attached MethodDef/MethodImpl after its original append/property/descriptor clock.
                // A shared body can acquire additional exact descriptors, which are independently admitted.
                onMaterialized?.Invoke(files[declaration], declaration, authored ? bridge : null, descriptor);
            }
        }
    }

    static BirMethodImpl SourceImplDescriptor(TypeNode.Fqn owner, string member, int arity, TypeNode[] parameters,
        TypeNode result, BirTypeParameter[] typeParameters, bool unitValueReturn) => new() {
        Owner = BirGraphValue.CopyType(owner), Member = member, Arity = arity,
        Params = parameters.Select(BirGraphValue.CopyType).ToArray(), Ret = BirGraphValue.CopyType(result),
        TypeParams = typeParameters == null ? default(OptionalField<BirTypeParameter[]>)
            : OptionalField<BirTypeParameter[]>.Of(typeParameters.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray()),
        DotktValueReturn = unitValueReturn ? OptionalField<bool>.Of(true) : default,
    };

    static BirMethodImpl AddSourceImpl(BirFunctionDeclaration method, BirMethodImpl descriptor, bool isInterface)
    {
        var existing = isInterface ? method.ClrInterfaceImpls : method.ClrBaseImpls;
        if (existing.OrEmpty().FirstOrDefault(value => value.StructurallyEquals(descriptor)) is { } attached) return attached;
        if (isInterface) method.ClrInterfaceImpls = existing.OrEmpty().Append(descriptor).ToArray();
        else method.ClrBaseImpls = existing.OrEmpty().Append(descriptor).ToArray();
        return descriptor;
    }
}
