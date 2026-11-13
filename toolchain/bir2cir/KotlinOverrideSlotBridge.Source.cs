using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using Def = SupertypeGraph.Def;

static partial class KotlinOverrideSlotBridge
{
    sealed record SlotFit(Fit[] Parameters, Fit Return);
    static SlotFit AssessFit(OverrideMethod impl, TypeNode[] ownArgs, TypeNode[] slotParams, TypeNode slotRet,
        bool unitValueReturn, TypeNode[] slotSignature, bool supIsInterface, bool referencedSlot,
        TypeNode.Fqn descriptorSpec, string propertyAccessor, Phase phase, bool inheritedImplementation,
        ValueTypeOracle isValue, ReferenceMetadataIndex refs, IReadOnlySet<string> localTypeNames,
        NullableRepresentationTypes sourceMapping, Func<TypeNode,TypeNode> PhysicalSlotType,
        Func<TypeNode,TypeNode,bool> CarrierParameterSeam, Func<TypeNode,TypeNode,bool> CanForwardPhysicalValue,
        Func<TypeNode,bool> canPassAsObject, Func<TypeNode,TypeNode,bool> canForwardClassReturn,
        Func<bool> existingAlreadyOwns, Func<bool> covariantAlreadyOwns, Func<TypeNode,bool> hasClassifier = null)
    {
        var declParams = impl.Parameters;
        var declRet = impl.Return;
        if (!impl.ParamsPresent || declRet == null || declParams.Length != slotParams.Length) return null;
        var differingSuspendResult = impl.Suspend
            && !BirTypeLowering.SamePhysicalSlotType(slotRet,
                SupertypeGraph.SubstOwnerTvs(declRet, ownArgs), refs.Aliases, isValue,
                refs.PhysicalTypeNames, returnPosition: true, localTypeNames,
                nullableFrames: refs.NullableTypeFrames);
        if (phase == Phase.SuspendValueBridges
            && !(differingSuspendResult
                || impl.Suspend && (unitValueReturn || !IsVoid(slotRet)) && IsUnit(declRet)
                    && !impl.ReturnsValue)) return null;
        if (phase == Phase.PhysicalBridges && existingAlreadyOwns?.Invoke() == true) return null;

        var fit = new Fit[slotParams.Length];
        for (var i = 0; i < slotParams.Length; i++)
        {
            var declT = declParams[i].Type;
            if (declT == null) { fit = null; break; }
            fit[i] = Classify(slotParams[i], SupertypeGraph.SubstOwnerTvs(declT, ownArgs), refs, isValue,
                returnPosition: false, referencedSlot, hasClassifier);
            if (fit[i] == Fit.Foreign && SameSourceRepresentationVariables(slotParams[i],
                    SupertypeGraph.SubstOwnerTvs(declT, ownArgs), sourceMapping.OwnerFrame))
                fit[i] = Fit.Bridge;
            if (fit[i] == Fit.Foreign && SameSourceFunctionRepresentation(slotParams[i],
                    SupertypeGraph.SubstOwnerTvs(declT, ownArgs), sourceMapping.OwnerFrame))
                fit[i] = Fit.Bridge;
            // The selected override may join an ordinary carrier slot to an exact native/generated slot,
            // or specialize a bare source T as G<X>. Preserve both declarations with an exact forwarder.
            if (fit[i] == Fit.Foreign && CarrierParameterSeam(slotParams[i],
                    SupertypeGraph.SubstOwnerTvs(declT, ownArgs))) fit[i] = Fit.Bridge;
            // A selected inherited implementation may accept an erased upper bound while the
            // interface redeclares a narrower argument. Forward the exact slot to that wider body,
            // boxing ordinary values but never native references or byref-like storage.
            if (fit[i] == Fit.Foreign && inheritedImplementation
                && (LowersToObject(SupertypeGraph.SubstOwnerTvs(declT, ownArgs), refs)
                        && canPassAsObject(slotParams[i])
                    || CanForwardPhysicalValue(PhysicalSlotType(slotParams[i]),
                        PhysicalSlotType(SupertypeGraph.SubstOwnerTvs(declT, ownArgs))))) fit[i] = Fit.Bridge;
            // Equal runtime function carriers do not imply equal CLR
            // signature discriminators. MethodImpl requires the selected
            // slot's modifier too; preserve the body row and forward from
            // a bridge carrying that exact selected signature.
            if (slotSignature?[i] is TypeNode.Mod { Req: false } slotIdentity
                && declParams[i].Signature is TypeNode bodyIdentity)
            {
                var declaredIdentity = SupertypeGraph.SubstOwnerTvs(
                    FunctionSignatureIdentity.DeclarationType(bodyIdentity), ownArgs);
                // The frontend has already selected this override. Equal value slots can be forwarded
                // unchanged, regardless of whether the discriminator contains a function at its head or
                // under a nullable/container type. Different modifiers still need an exact bridge row.
                if (fit[i] == Fit.Same
                    && !ClrMemberResolution.SameInterfaceSlotType(
                        PhysicalSlotType(slotIdentity.M), PhysicalSlotType(declaredIdentity)))
                    fit[i] = Fit.Bridge;
            }
            else if (fit[i] == Fit.Same
                && declParams[i].Signature != null)
            {
                // Specializing an ancestor's bare T as a Kotlin function adds a discriminator only to
                // the authored body. Even equal value carriers need a bridge to the unmodified base slot.
                fit[i] = Fit.Bridge;
            }
        }
        // A parameter difference this erasure did not create belongs to whatever pass did create it.
        if (fit == null || fit.Contains(Fit.Foreign)) return null;
        if (covariantAlreadyOwns?.Invoke() == true) return null;
        var retFit = unitValueReturn && IsVoid(declRet) && !impl.ReturnsValue
            ? Fit.Bridge
            : Classify(slotRet, SupertypeGraph.SubstOwnerTvs(declRet, ownArgs), refs, isValue,
                returnPosition: true, referencedSlot, hasClassifier);
        if (retFit == Fit.Foreign && SameSourceRepresentationVariables(slotRet,
                SupertypeGraph.SubstOwnerTvs(declRet, ownArgs), sourceMapping.OwnerFrame))
            retFit = Fit.Bridge;
        if (retFit == Fit.Foreign)
        {
            if (referencedSlot && NodeType.IsNothing(declRet)) retFit = Fit.Bridge;
            // A source-selected covariant class override still owes the base's exact CLR slot,
            // including when the narrow override is abstract. Reuse the ordinary virtual forwarder
            // only when the actual physical return is assignable; Kotlin variance alone is no proof.
            else if (!supIsInterface && phase == Phase.PhysicalBridges && canForwardClassReturn(
                    PhysicalSlotType(SupertypeGraph.SubstOwnerTvs(declRet, ownArgs)),
                    referencedSlot && !refs.HasDotKtOwner(descriptorSpec.Name)
                        ? slotRet : PhysicalSlotType(slotRet)))
                retFit = Fit.Bridge;
            else
            {
                // The covariance pass visits authored MethodDefs, not inherited implementation facts.
                // A frontend-selected inherited body therefore needs its exact interface adapter here,
                // including when only the return type differs. Keep the base declaration unchanged.
                if (!fit.Any(f => f is Fit.Bridge or Fit.Rewrite)
                    && !(supIsInterface && inheritedImplementation)
                    && !(referencedSlot && supIsInterface && propertyAccessor == "get")) return null;
                retFit = Fit.Bridge;
            }
        }


        return new(fit, retFit);
    }

    static void ApplyDeclarationEdits(OverrideMethod implementation, TypeNode[] ownArgs, TypeNode[] slotParameters,
        TypeNode slotReturn, SlotFit fit, Phase phase, ValueTypeOracle isValue,
        Func<TypeNode.Fqn,TypeNode[]> annotationArguments)
    {
        if (phase == Phase.DeclarationMoves && implementation.Suspend && fit.Return == Fit.Bridge
            && ErasureAligned(slotReturn, SupertypeGraph.SubstOwnerTvs(implementation.Return, ownArgs)))
            implementation.SetSuspendTaskResult(slotReturn);
        var parameters = implementation.Parameters;
        for (var index = 0; index < slotParameters.Length; index++)
            if (fit.Parameters[index] == Fit.Rewrite) parameters[index].Move(slotParameters[index], isValue, annotationArguments);
        if (fit.Return == Fit.Rewrite) implementation.MoveReturn(slotReturn, isValue, annotationArguments);
    }

    public static void PropagateErasedSlots(IReadOnlyList<BirFileDeclaration> roots, ValueTypeOracle isValue,
        ReferenceMetadataIndex references, bool refBuild, GenericRepresentationPolicy representations, Func<TypeNode,bool> hasClassifier)
    {
        var definitions = SupertypeGraph.CollectSource(roots);
        var declarations = new Dictionary<string,IBirTypeDeclaration>(StringComparer.Ordinal);
        foreach (var declaration in roots.SelectMany(root => root.Types.OrEmpty()))
            if (declaration.Name.OrDefault() is string name) declarations[name] = declaration;
        var views = new OverrideViews(declarations);
        var frames = references.NullableTypeFrames.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var definition in definitions.Values)
            if (views.Owner(definition).Frame is { } frame) frames[definition.Name] = frame;
        var applications = new InnerApplicationFrames(roots, references).Project(frames, retainPhysicalOrder: true);
        var localNames = definitions.Keys.ToHashSet(StringComparer.Ordinal);
        TypeNode[] AnnotationArguments(TypeNode.Fqn type) => BirTypeLowering.AnnotationArguments(type, references.Aliases, isValue,
            references.PhysicalTypeNames, localNames, refBuild, references.NullableTypeFrames);
        foreach (var definition in definitions.Values.Where(value => value.Kind is "class" or "interface").ToArray())
        {
            var owner = views.Owner(definition);
            if (!owner.HasMethods) continue;
            var ownArguments = ClassOwnArgs(definition);
            var mapping = new NullableRepresentationTypes(owner.Frame, null, applications, isValue, policy: representations);
            TypeNode Physical(TypeNode type) => BirTypeLowering.LowerPhysicalType(type, references.Aliases, isValue,
                references.PhysicalTypeNames, typeArg: false, localTypeNames: null, nullableFrames: references.NullableTypeFrames);
            SelectSlots(owner, definitions, views, owner.Methods.ToArray(), ownArguments, isValue, references,
                Phase.DeclarationMoves, refBuild, null, mapping, representations, (_, _) => false, slot => {
                    OwnerConstrainedMethodLowering.RecordOverride(slot.Implementation.Source, slot.TypeParameters, slot.SemanticSpec.Args ?? Array.Empty<TypeNode>(),
                        definitions.TryGetValue(slot.SemanticSpec.Name, out var declarationOwner)
                            ? views.Owner(declarationOwner).DeclaredParameters
                            : ReferenceMetadataIndex.ReadReferencedTypeParameters(references.OwnerTypeParamDeclarations(slot.SemanticSpec.Name)),
                        owner.DeclaredParameters, representations == null ? null : representations.StorageConstraint, slot.Referenced);
                    var reimplements = slot.IsInterface && definition.Kind == "class"
                        && (ReachesFromDeclaredInterface(definition, slot.SemanticSpec, definitions, references)
                            || slot.Referenced && ReachesFromDeclaredPhysicalInterface(definition, slot.DescriptorSpec, definitions, references, isValue, null));
                    if (slot.IsInterface && definition.Kind == "class" && !reimplements && !slot.HasDefault) return;
                    var fit = AssessFit(slot.Implementation, ownArguments, slot.Parameters, slot.Return, slot.UnitValueReturn, slot.Signature,
                        slot.IsInterface, slot.Referenced, slot.DescriptorSpec, slot.PropertyAccessor, Phase.DeclarationMoves, false,
                        isValue, references, null, mapping, Physical, (_, _) => false, (_, _) => false, _ => false, (_, _) => false,
                        null, null, hasClassifier);
                    if (fit == null) return;
                    ApplyDeclarationEdits(slot.Implementation, ownArguments, slot.Parameters, slot.Return, fit, Phase.DeclarationMoves, isValue, AnnotationArguments);
                    var needsSignatureBridge = fit.Parameters.Contains(Fit.Bridge) || fit.Return == Fit.Bridge;
                    var needsPropertySlot = slot.IsInterface && slot.PropertyAccessor != null
                        && slot.DescriptorMember != slot.Implementation.Name
                        && (definition.Kind != "class" || reimplements || slot.HasDefault);
                    if (!needsSignatureBridge && needsPropertySlot && definition.Kind != "interface")
                    {
                        slot.Implementation.SetVirtual();
                        var descriptor = new BirMethodImpl { Owner = slot.DescriptorSpec, Member = slot.DescriptorMember,
                            Arity = slot.Implementation.Arity, Params = slot.Signature ?? slot.Parameters, Ret = slot.Return,
                            TypeParams = slot.TypeParameters == null ? default
                                : OptionalField<BirTypeParameter[]>.Of(SubstituteParameters(slot.TypeParameters, slot.SemanticSpec.Args ?? Array.Empty<TypeNode>())),
                            DotktValueReturn = slot.UnitValueReturn ? OptionalField<bool>.Of(true) : default };
                        var method = slot.Implementation.Source;
                        if (!method.ClrInterfaceImpls.OrEmpty().Any(existing => existing.StructurallyEquals(descriptor)))
                            method.ClrInterfaceImpls = method.ClrInterfaceImpls.OrEmpty().Append(descriptor).ToArray();
                    }
                });
        }
    }
}
