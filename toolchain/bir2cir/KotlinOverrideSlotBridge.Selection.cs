using DotKt.Bir;
using DotKt.Bir.Lowering;
using Def = SupertypeGraph.Def;

static partial class KotlinOverrideSlotBridge
{
    sealed record SelectedSlot(TypeNode.Fqn SemanticSpec, TypeNode.Fqn DescriptorSpec, bool IsInterface,
        bool Referenced, bool HasDefault, string IdentityName, string DescriptorMember, string PropertyAccessor,
        TypeNode[] Parameters, TypeNode Return, OverrideMethod Implementation, BirTypeParameter[] TypeParameters,
        bool UnitValueReturn = false, TypeNode[] Signature = null, string ReferenceMember = null,
        TypeNode SemanticReturn = null, OverrideMethod SelectedLocal = null, string SelectedReferencedDeclarationId = null);

    static void SelectSlots(OverrideOwner owner, IReadOnlyDictionary<string, Def> defs, OverrideViews views,
        IEnumerable<OverrideMethod> candidates, TypeNode[] ownArgs, ValueTypeOracle isValue, ReferenceMetadataIndex refs,
        Phase phase, bool refBuild, IReadOnlySet<string> localTypeNames, NullableRepresentationTypes sourceMapping,
        GenericRepresentationPolicy representations, Func<TypeNode,TypeNode,bool> carrierSeam, Action<SelectedSlot> fill,
        IReadOnlyDictionary<OverrideMethod,TypeNode[]> inheritedSignatures = null,
        IReadOnlyDictionary<OverrideMethod,TypeNode[]> sourceParameters = null)
    {
        var cls = owner.Definition;
        foreach (var (spec, supIsInterface) in SupertypeGraph.Reachable(cls, defs, refs))
        {
            // These declarations own Kotlin dispatch, not matching IDictionary MethodDefs.
            // The map capability and native-facade passes own the two independent obligations.
            if (!refBuild && KotlinCollectionSlotSynthesis.IsMapContractOwner(spec.Name)) continue;
            if (!defs.TryGetValue(spec.Name, out var sup))
            {
                // A referenced BASE CLASS reaches the same arm; only its wiring differs (a MethodImpl against the
                // constructed base rather than the interface), and the emitter resolves that base externally.
                SelectReferenceSlots(owner, defs, spec, supIsInterface, candidates, ownArgs, isValue, refs, phase, sourceMapping, representations,
                    fill, suspendValues: phase == Phase.SuspendValueBridges, inheritedSignatures: inheritedSignatures,
                    sourceParameters: sourceParameters, nativeAliases: !refBuild);
                continue;
            }
            var supArgs = SupertypeGraph.EffectiveArgs(spec, sup.Arity);
            if (supArgs == null) continue;

            foreach (var slot in views.Owner(sup).Methods.ToList())
            {
                // Physical MethodImpl bodies are not Kotlin declarations and therefore cannot introduce another
                // source override slot in a derived type. Re-consuming one here can make a derived interface target
                // the private/final bridge of its base interface, which is not a valid declaration MethodDef.
                if (slot.Static || slot.PhysicalBridge
                    || slot.Name is not string name
                    || !slot.ParamsPresent) continue;
                var methodArity = slot.Arity;
                var slotParamNodes = slot.Parameters;
                // `Subst(Erase(declared), typeArgs)` and never `Erase(Subst(...))` — a substituted `Nullable(kotlin.Int)`
                // has no type variable left to erase and would state the wrong slot. The erasure is idempotent, so
                // this reads the same slot before the sweep has run and after. Once physical bridges run,
                // generated signatures already own their CLR ABI: in particular Task<Nullable<V>> was introduced
                // by suspend lowering, not by a Kotlin generic instantiation, and must not be erased again.
                var rawSlotParams = slotParamNodes
                    .Select(p => p.Type).ToArray();
                var semanticSlotParams = rawSlotParams
                    .Select(t => t == null ? null : SupertypeGraph.SubstOwnerTvs(t, supArgs)).ToArray();
                var slotParams = rawSlotParams
                    .Select(t => t == null ? null : SupertypeGraph.SubstOwnerTvs(
                        DeclaredSlot(t, isValue, physical: phase == Phase.PhysicalBridges), supArgs)).ToArray();
                var slotRet0 = slot.Return;
                if (slotParams.Any(p => p == null) || slotRet0 == null) continue;
                var slotRet = SupertypeGraph.SubstOwnerTvs(
                    DeclaredSlot(slotRet0, isValue, physical: phase == Phase.PhysicalBridges), supArgs);
                var slotReturnsValue = !IsVoid(slotRet0) || slot.ReturnsValue;

                slot.Property(out var propertyName, out var accessorKind);
                var semanticName = propertyName ?? slot.KotlinSourceMember
                    ?? slot.ExistentialSourceMember ?? name;
                var selectionParams = slotParamNodes.Select((parameter, index) =>
                    parameter.Signature is TypeNode signature
                        ? SupertypeGraph.SubstOwnerTvs(signature, supArgs)
                        : slotParams[index]).ToArray();
                // A prior pass may already have materialized a final inherited method's forwarding declaration.
                // Its selected override owns this slot; the inherited fact must not make that declaration ambiguous.
                var impl = FindImplementer(owner, defs, owner.Methods, spec.Name, name, semanticName,
                    slot.DeclarationId, propertyName, accessorKind,
                    methodArity, selectionParams, slot.TypeParameters, supArgs, ownArgs,
                    semanticSlotTypeParams: SourceParameters(slot), carrierSeam: carrierSeam)
                    ?? FindImplementer(owner, defs, candidates.Where(candidate => inheritedSignatures?.ContainsKey(candidate) == true), spec.Name, name, semanticName,
                        slot.DeclarationId, propertyName, accessorKind,
                        methodArity, selectionParams, slot.TypeParameters, supArgs, ownArgs,
                        inheritedSignatures, SourceParameters(slot), carrierSeam);
                if (impl == null) continue;
                // A locally-emitted Kotlin interface may itself be @ClrTypeAlias-bound to a referenced CLR
                // interface. Its Kotlin accessor keeps the dedicated property name, while the MethodImpl descriptor
                // must name the exact external Property/MethodSemantics accessor (Collection.size -> get_Count).
                // Resolve that allocation from metadata here; never infer it from either accessor spelling.
                // Explicit naming of a concrete default-interface declaration is allocated later, after physical
                // type lowering. Its identity makes that MethodDef independently allocatable, while this exact
                // descriptor lets an overriding declaration keep its own Kotlin/CLR name. Consume the stated target
                // here; recovering it after allocation from the rewritten MethodDef spelling would be too late.
                // Suspend cold entries already carry their final role-suffixed name and no explicitClrName field.
                var explicitDescriptorMember = slot.ExplicitClrName;
                var descriptorMember = explicitDescriptorMember ?? name;
                var descriptorOwner = spec;
                var loweredDescriptorOwner = refs == null ? null : BirTypeLowering.LowerPhysicalType(
                    spec, refs.Aliases, isValue, refs.PhysicalTypeNames, typeArg: false, localTypeNames,
                    nullableFrames: refs.NullableTypeFrames) as TypeNode.Fqn;
                string aliasedPhysicalMember = null;
                if (propertyName == null && refs != null)
                {
                    if (!refs.TryExactMemberIntrinsic(spec.Name, semanticName, methodArity,
                            semanticSlotParams, out aliasedPhysicalMember)
                        && slot.Overrides.Length > 0)
                        aliasedPhysicalMember = ResolveSlotName(slot, refs);
                    if (aliasedPhysicalMember == null)
                    {
                        var inheritedPhysicalMembers = SupertypeGraph.Reachable(sup, defs, refs)
                            .Where(edge => edge.isInterface)
                            .Select(edge => refs.TryExactMemberIntrinsic(edge.spec.Name, semanticName, methodArity,
                                semanticSlotParams, out var inheritedMember) ? inheritedMember : null)
                            .Where(inheritedMember => inheritedMember != null)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray();
                        if (inheritedPhysicalMembers.Length > 1)
                            throw new InvalidOperationException(
                                $"bir2cir: aliased interface slot '{spec.Name}.{semanticName}`{methodArity}' "
                                + "inherits conflicting physical member bindings: "
                                + string.Join(", ", inheritedPhysicalMembers));
                        if (inheritedPhysicalMembers.Length == 1)
                            aliasedPhysicalMember = inheritedPhysicalMembers[0];
                    }
                }
                if (propertyName == null && refs != null && loweredDescriptorOwner != null
                    && loweredDescriptorOwner.Name != spec.Name
                    && refs.ResolveNetType(loweredDescriptorOwner.Name,
                        loweredDescriptorOwner.Args?.Length ?? 0) is { IsInterface: true }
                    && aliasedPhysicalMember != null)
                {
                    var comparableParams = slotParams.Select(parameter => BirTypeLowering.LowerPhysicalType(
                        parameter, refs.Aliases, isValue, refs.PhysicalTypeNames, typeArg: false,
                        localTypeNames, nullableFrames: refs.NullableTypeFrames)).ToArray();
                    if (!ClrMemberResolution.TryResolveAliasedInterfaceSlotByShape(
                            refs, loweredDescriptorOwner, aliasedPhysicalMember, methodArity, comparableParams,
                            SourceMethodTypeParameterShapeKey(slot.TypeParameters, supArgs),
                            out var declarationOwner, out var declarationMember,
                            out var physicalParams, out var physicalRet))
                        throw new InvalidOperationException(
                            $"bir2cir: aliased interface slot '{spec.Name}.{semanticName}`{methodArity}' "
                            + $"does not resolve to '{loweredDescriptorOwner.Name}.{aliasedPhysicalMember}'");
                    // Reflection reports the actual declaring interface. A derived semantic alias may repeat a
                    // source declaration while its CLR face only inherits the base slot; that base semantic edge is
                    // visited separately and owns the one valid MethodImpl.
                    if (ReferenceMetadataIndex.BareOwnerFqn(declarationOwner.Name)
                        != ReferenceMetadataIndex.BareOwnerFqn(loweredDescriptorOwner.Name))
                        continue;
                    descriptorOwner = declarationOwner;
                    descriptorMember = declarationMember;
                    slotParams = physicalParams;
                    slotRet = physicalRet;
                    slotReturnsValue = physicalRet is not TypeNode.Fqn { Name: "void" or "System.Void", Args: null };
                }
                if (propertyName != null && refs != null
                    && refs.TryExternalPropertyAccessor(spec.Name, propertyName, accessorKind,
                        slotParams.Length, methodArity, slotParams, spec.Args ?? Array.Empty<TypeNode>(),
                        out var physicalOwner, out _, out var externalAccessor))
                {
                    var currentPhysicalOwner = refs.ExactReflectedOwner(spec.Name, loweredDescriptorOwner.Args?.Length ?? 0);
                    // Reflection reports the MethodSemantics method's DECLARING interface. A Kotlin interface may
                    // redeclare a property while its CLR alias merely inherits that property (List.size over
                    // IReadOnlyList<T> -> IReadOnlyCollection<T>.Count). Such a spec owns no CLR slot; its reachable
                    // declaring supertype is visited separately and receives the one valid descriptor.
                    if (physicalOwner != currentPhysicalOwner) continue;
                    descriptorMember = externalAccessor;
                    descriptorOwner = new TypeNode.Fqn(physicalOwner, loweredDescriptorOwner.Args);
                }
                // A generated existential slot carries its source-property association only as metadata; its
                // compiler-assigned `$star$...` name is already the physical declaration and bypasses this ordinary
                // source-property allocation.
                else if (propertyName != null && explicitDescriptorMember == null
                    && !slot.PropertyCarrierPresent)
                {
                    // The local declaration is allocated later, but its accessor role is still an explicit semantic
                    // fact here. Project the descriptor through the same one-way name allocator now; waiting until
                    // final CIR would leave only the physical spelling and force a forbidden reverse parse.
                    descriptorMember = KotlinPropertyAccessors.PhysicalName(propertyName, accessorKind);
                }
                var slotHasDefault = supIsInterface && !slot.Abstract
                    && slot.BodyPresent;
                fill(new SelectedSlot(spec, descriptorOwner, supIsInterface, false, slotHasDefault,
                    semanticName, descriptorMember, accessorKind,
                    slotParams, slotRet, impl, slot.TypeParameters,
                    slotReturnsValue && IsUnitValueSlot(slotRet),
                    descriptorOwner.Name == spec.Name
                        ? slotParamNodes.Select((parameter, index) =>
                            parameter.Signature is TypeNode discriminator
                                ? (TypeNode)new TypeNode.Mod(false,
                                    SupertypeGraph.SubstOwnerTvs(
                                        FunctionSignatureIdentity.DeclarationType(discriminator), supArgs), slotParams[index])
                                : slotParams[index]).ToArray()
                        : null,
                    SelectedLocal: descriptorOwner.Name == spec.Name ? slot : null));
            }
        }

    }

    static void SelectReferenceSlots(OverrideOwner implementingTypeView, IReadOnlyDictionary<string, Def> defs, TypeNode.Fqn spec,
        bool supIsInterface, IEnumerable<OverrideMethod> methods, TypeNode[] ownArgs, ValueTypeOracle isValue,
        ReferenceMetadataIndex refs, Phase phase, NullableRepresentationTypes sourceMapping, GenericRepresentationPolicy representations,
        Action<SelectedSlot> fill, bool suspendValues = false,
        IReadOnlyDictionary<OverrideMethod, TypeNode[]> inheritedSignatures = null,
        IReadOnlyDictionary<OverrideMethod, TypeNode[]> sourceParameters = null, bool nativeAliases = false)
    {
        var cls = implementingTypeView.Definition;
        if (refs == null) return;
        var supArgs = spec.Args ?? Array.Empty<TypeNode>();
        var ownedSlots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var impl in methods.ToList())
        {
            if (suspendValues && !impl.Suspend) continue;
            if (impl.Static || impl.Name is not string ownName) continue;
            if (impl.Visibility is not (null or "public" or "protected")) continue;
            if (!impl.ParamsPresent) continue;
            var ps = impl.Parameters;
            var overrides = impl.Overrides;
            var methodArity = impl.Arity;
            foreach (var o in overrides)
            {
                if (nativeAliases && KotlinCollectionSlotSynthesis.OwnsMapOverride(o.Owner, o.Member, o.Kind, o.Arity)) continue;
                // THE MARKER SAYS WHAT WAS OVERRIDDEN; THE SPEC SAYS WHERE THE SLOT IS REACHED. Those differ the
                // moment a referenced supertype inherits the member — `class C : Derived<Int>` where `accept` is
                // declared on `Sink` — so requiring the marker's owner to EQUAL the spec skipped the slot entirely.
                // The question is asked of the SPEC instead, and the reader walks the referenced supertype graph
                // itself, mapping the declaration through each supertype's arguments into the spec's own frame.
                //
                // But the two must still be RELATED, or a marker for one supertype answers against an unrelated one
                // that merely exposes the same erased shape — `class C : A<Int>, B<Int>` with `accept(T?)` on each
                // wires both slots to whichever body the first marker named. "Not declared here" establishes only
                // externality; reachability from the spec is what makes this marker THIS spec's business.
                if (o.Owner is not TypeNode.Fqn owner || defs.ContainsKey(owner.Name)) continue;
                // This alias has no CLR declaration slots on Object. Its explicit value-member adapters own the
                // nominal MethodImpls, including inherited source properties with flattened override markers.
                if (nativeAliases && refs.UsesObjectValueSlots(owner.Name)) continue;
                if (o.Member is not string member) continue;
                // A PROPERTY marker names the Kotlin property and getter/setter role. Reference metadata supplies the
                // exact physical CLR slot independently of the implementation MethodDef name.
                var overrideKind = o.Kind;
                var accessorKind = overrideKind switch { "getter" => "get", "setter" => "set", _ => null };
                // dll2klib preserves every flattened property override owner even when the emitted CLR interface
                // hierarchy cannot preserve the semantic @ClrTypeAlias edge (Collection -> IReadOnlyCollection).
                // That exact frontend-selected owner is sufficient for a property slot. Ordinary methods need
                // declaration reachability or the exact trusted alias-to-TypeDef binding below; shape alone cannot
                // let an unrelated interface capture a marker while walking another referenced spec.
                // The graph already contains physical companion arguments; the override marker retains
                // source arity. Compare declarations in the recorded frame, not by mismatched arities.
                var selectedSlotSpec = spec;
                if (accessorKind == null && !SupertypeGraph.ReachesDeclaration(spec,
                        (TypeNode.Fqn)sourceMapping.Slot(owner), defs, refs))
                {
                    // A referenced interface records its physical native parents, while the frontend's override
                    // still names the exact Kotlin alias declaration. That explicit representation binding proves
                    // this native obligation only when both resolve to the same CLR TypeDef. Read the selected
                    // declaration in its own argument frame; do not turn aliases into general source identities.
                    if (!nativeAliases || !supIsInterface
                        || !refs.Aliases.ContainsKey(ReferenceMetadataIndex.BareOwnerFqn(owner.Name))) continue;
                    var nativeDeclaration = BirTypeLowering.LowerPhysicalType(
                        (TypeNode.Fqn)sourceMapping.Slot(owner), refs.Aliases, isValue, refs.PhysicalTypeNames,
                        typeArg: false, localTypeNames: null, nullableFrames: refs.NullableTypeFrames) as TypeNode.Fqn;
                    var reached = refs.ResolveNetType(spec.Name, supArgs.Length);
                    if (nativeDeclaration == null || reached == null
                        || reached != refs.ResolveNetType(nativeDeclaration.Name, nativeDeclaration.Args?.Length ?? 0))
                        continue;
                    selectedSlotSpec = owner;
                }
                var selectedSlotArgs = selectedSlotSpec.Args ?? Array.Empty<TypeNode>();
                // A referenced interface can redeclare a Kotlin property while its flattened override facts also name
                // an inherited semantic declaration whose CLR slot has a different identity. Resolve each property
                // marker against the declaration OWNER it names, not only against the directly-listed interface spec:
                // one implementing accessor may owe MethodImpls to both the redeclared Kotlin accessor and (for
                // example) Collection.size's IReadOnlyCollection<T>.get_Count slot.
                var selectedSpec = accessorKind != null ? owner : spec;
                var selectedArgs = selectedSpec.Args ?? Array.Empty<TypeNode>();
                var physicalSelectedSpec = accessorKind != null
                    ? (TypeNode.Fqn)sourceMapping.Slot(selectedSpec) : selectedSpec;
                var nativeOwner = accessorKind != null && !refs.HasDotKtOwner(selectedSpec.Name)
                    ? refs.ResolveNetType(selectedSpec.Name, selectedArgs.Length) : null;
                if (nativeOwner != null)
                {
                    // Wait for the actual erased graph, then close this declaration through its reached
                    // application. A native ancestor can fix Nullable<V> independently of Kotlin's arguments.
                    if (phase != Phase.PhysicalBridges
                        || refs.ResolveNetType(spec.Name, supArgs.Length) != nativeOwner) continue;
                    physicalSelectedSpec = spec;
                }
                // Kotlin-owned applications close V? as object, while the implementing scalar getter keeps
                // Nullable<V>. Close the owner and slot together in that same argument frame.
                else if (accessorKind != null && representations?.OwnsValueSlots(selectedSpec.Name) == true)
                    physicalSelectedSpec = (TypeNode.Fqn)NullableGenericErasure.EraseArgument(physicalSelectedSpec, isValue);
                var physicalSelectedArgs = physicalSelectedSpec.Args ?? Array.Empty<TypeNode>();
                var implementationSignature = suspendValues && sourceParameters != null
                    && sourceParameters.TryGetValue(impl, out var sourceSignature)
                    ? sourceSignature : impl.Parameters.Select(parameter => parameter.Type).ToArray();
                if (implementationSignature.Length != ps.Length || implementationSignature.Any(type => type == null))
                    continue;
                if (inheritedSignatures != null
                    && inheritedSignatures.TryGetValue(impl, out var inheritedSignature))
                    implementationSignature = inheritedSignature;
                string selectedPhysicalMember = null;
                BirTypeParameter[] selectedSlotTypeParams = null;
                var slotReturnsValue = false;
                TypeNode slotRet0;
                TypeNode[] slotParams0;
                TypeNode[] signatureParameters = null;
                bool[] refused;
                var physicalProjection = !suspendValues && accessorKind == null
                    && impl.SuspendSourceParametersPresent ? ownName : null;
                var bindingReturn = impl.Return;
                if (!suspendValues && physicalProjection == null)
                {
                    var methodFrame = impl.Frame;
                    implementationSignature = implementationSignature.Select((type, index) =>
                        NullableRepresentationTypes.BindingScalarFromSource(type, ps[index].NullableSource,
                            sourceMapping.OwnerFrame, methodFrame)).ToArray();
                    bindingReturn = NullableRepresentationTypes.BindingScalarFromSource(bindingReturn,
                        impl.NullableReturn, sourceMapping.OwnerFrame, methodFrame);
                }
                // Reachable enumerates the declaration's ancestors independently. A
                // class can expose an abstract interface member without declaring a
                // class MethodDef for it. An inherited lookup must not manufacture
                // a class MethodImpl against that absent declaration.
                var semanticSelection = phase == Phase.DeclarationMoves;
                ReferencedMethodDeclaration selectedClassDeclaration = null;
                if (!supIsInterface && accessorKind == null && physicalProjection == null
                    && refs.HasDotKtOwner(spec.Name)
                    && !refs.TrySelectedOverrideDeclarationByShape(spec.Name, member, null,
                        semanticSelection
                            ? impl.Frame?.SourceArity ?? methodArity
                            : methodArity,
                        implementationSignature, supArgs,
                        SourceMethodTypeParameterShapeKey(semanticSelection ? AuthoredParameters(impl) : impl.TypeParameters, ownArgs,
                            semanticSelection ? null : refs.PhysicalConstraintType),
                        selectedSuspend: suspendValues || semanticSelection && impl.Suspend, out selectedClassDeclaration,
                        semanticConstraints: semanticSelection,
                        selectedDeclarationId: o.DeclarationId))
                    continue;
                if (suspendValues)
                {
                    if (accessorKind != null || !refs.TrySelectedOverrideDeclarationByShape(
                        spec.Name, member, null, methodArity, implementationSignature, supArgs,
                        SourceMethodTypeParameterShapeKey(impl.TypeParameters, ownArgs, refs.PhysicalConstraintType), selectedSuspend: true, out var suspendDeclaration, semanticConstraints: false, selectedDeclarationId: null))
                        continue;
                    // The selected suspend declaration supplies its logical open return independently from its
                    // already-emitted Task signature. Decide value-ness before closing the owner's type arguments.
                    slotRet0 = suspendDeclaration.Return;
                    slotParams0 = suspendDeclaration.PhysicalParameters;
                    signatureParameters = suspendDeclaration.SignatureParameters;
                    refused = null;
                    selectedPhysicalMember = suspendDeclaration.PhysicalMember;
                    selectedSlotTypeParams = ReferenceMetadataIndex.ReadReferencedTypeParameters(suspendDeclaration.TypeParams);
                    slotReturnsValue = !IsVoid(slotRet0);
                }
                else if (accessorKind != null && phase == Phase.PhysicalBridges)
                {
                    // The late MethodImpl must use the referenced MethodDef, not
                    // the property's semantic return (which can contain a star
                    // application whose CLR value is an existential carrier).
                    if (!refs.TrySelectedOverrideDeclarationByShape(selectedSpec.Name, member, accessorKind,
                            methodArity, implementationSignature, selectedArgs,
                            SourceMethodTypeParameterShapeKey(impl.TypeParameters, ownArgs, refs.PhysicalConstraintType), selectedSuspend: false,
                            out var propertyDeclaration,
                            semanticConstraints: false, selectedDeclarationId: o.PhysicalDeclarationId
                                ?? o.DeclarationId)) continue;
                    slotRet0 = propertyDeclaration.PhysicalReturn;
                    slotParams0 = propertyDeclaration.PhysicalParameters;
                    selectedPhysicalMember = propertyDeclaration.PhysicalMember;
                    signatureParameters = propertyDeclaration.SignatureParameters;
                    selectedSlotTypeParams = propertyDeclaration.TypeParams is { Count: > 0 }
                        ? ReferenceMetadataIndex.ReadReferencedTypeParameters(propertyDeclaration.TypeParams) : null;
                    refused = null;
                }
                else
                {
                    var foundSlot = accessorKind != null
                        ? refs.TryNullableGenericPropertySlot(selectedSpec.Name, member, accessorKind, isStatic: false,
                            ps.Length, methodArity, implementationSignature, selectedArgs,
                            out slotRet0, out slotParams0, out refused, includeUnchanged: true)
                        : refs.TrySelectedNullableGenericSlotByShape(selectedSlotSpec.Name, member, isStatic: false, ps.Length, methodArity,
                            implementationSignature, bindingReturn,
                            selectedSlotArgs,
                            SourceMethodTypeParameterShapeKey(phase == Phase.DeclarationMoves ? AuthoredParameters(impl) : impl.TypeParameters, ownArgs,
                                phase == Phase.DeclarationMoves ? null : refs.PhysicalConstraintType),
                            out slotRet0, out slotParams0, out refused,
                            out selectedPhysicalMember, out selectedSlotTypeParams, out slotReturnsValue,
                            out signatureParameters,
                            phase == Phase.DeclarationMoves,
                            // Suspend lowering supplied the physical MethodDef and preserved the unchanged source
                            // override marker. Match its cold/Task projection exactly, as the local-slot arm does;
                            // a cold entry is not another Kotlin method with the marker's source name.
                            physicalProjection, o.PhysicalDeclarationId ?? o.DeclarationId);
                    if (!foundSlot) continue;
                }
                if (slotParams0 == null || slotParams0.Length != ps.Length) continue;
                if (refused != null && refused.Any(r => r)) continue;
                // A null PARAMETER fact is a slot this reader cannot state, and inventing one from the physical
                // signature is the derivation its silence exists to prevent — so the member is left alone.
                if (slotParams0.Any(t => t == null)) continue;
                // A native accessor and an explicitly selected hot/cold MethodDef state exact CLR signatures.
                // Ordinary Kotlin reference lookups may restore nullable-generic carriers even in the late phase.
                // Never erase a native Nullable<V> nested in a reified slot into an unrelated Kotlin object slot.
                var slotParams = slotParams0
                    .Select(t => SupertypeGraph.SubstOwnerTvs(DeclaredSlot(t, isValue, physical: physicalProjection != null || nativeOwner != null),
                        accessorKind != null ? physicalSelectedArgs : selectedSlotArgs))
                    .ToArray();
                // A null RETURN fact is the opposite: the reader states a return only while it still says something a
                // call site completes (a carrier, or a physical type still holding a type variable), so `null` means
                // the slot's return is not one the erasure moved and the override's own return already IS it.
                // `compareTo(T): Int` is exactly that — the parameter is the whole divergence.
                var slotRet = slotRet0 == null
                    ? SupertypeGraph.SubstOwnerTvs(impl.Return, ownArgs)
                    : SupertypeGraph.SubstOwnerTvs(DeclaredSlot(slotRet0, isValue, physical: physicalProjection != null || nativeOwner != null),
                        accessorKind != null ? physicalSelectedArgs : selectedSlotArgs);
                if (slotRet == null) continue;
                if (accessorKind != null) slotReturnsValue = slotRet0 != null && !IsVoid(slotRet0);
                // The CLR slot's own NAME. A referenced Kotlin interface that is `@ClrTypeAlias`'d onto a BCL one
                // fills a differently-named member (`compareTo` -> `CompareTo`), and the MethodImpl has to name the
                // member the interface actually declares.
                var descriptorOwner = accessorKind != null ? physicalSelectedSpec : spec;
                var descriptorMember = selectedPhysicalMember ?? ownName;
                if (accessorKind != null)
                {
                    if (refs.TryExternalPropertyAccessor(selectedSpec.Name, member, accessorKind,
                            ps.Length, methodArity, implementationSignature, selectedArgs,
                            out var physicalOwner, out _, out var externalAccessor))
                    {
                        var projectedOwner = (TypeNode.Fqn)BirTypeLowering.LowerPhysicalType(
                            descriptorOwner, refs.Aliases, isValue, refs.PhysicalTypeNames, typeArg: false,
                            nullableFrames: refs.NullableTypeFrames);
                        var currentPhysicalOwner = refs.ExactReflectedOwner(
                            selectedSpec.Name, projectedOwner.Args?.Length ?? 0);
                        if (physicalOwner != currentPhysicalOwner) continue;
                        descriptorOwner = new TypeNode.Fqn(physicalOwner, projectedOwner.Args);
                        descriptorMember = externalAccessor;
                    }
                }
                else
                {
                    // The override marker names the exact semantic declaration. A derived @ClrTypeAlias interface
                    // may redeclare that Kotlin member even though its physical CLR face only INHERITS the base slot
                    // (MutableList.add on IList<T>, whose actual declaration is ICollection<T>.Add(T): void). In that
                    // case the reference-surface MethodDef name is not a physical slot name. Consume the marker's
                    // exact @ClrIntrinsic binding when it has one, lower that declaration owner, and resolve the
                    // actual CLR declaration before authoring the table row. A marker whose physical face declares no
                    // such member is not this row; the transitive override closure supplies the base marker next.
                    var selectedDescriptorOwner = spec;
                    TypeNode[] selectionOwnerArgs = null;
                    if (refs.TryExactMemberClrBinding(owner.Name, member, methodArity,
                            implementationSignature, owner.Args ?? Array.Empty<TypeNode>(), out var markerBinding)
                        && markerBinding.Intrinsic != null)
                    {
                        // `owner` remains the pristine Kotlin declaration identity so the exact lookup above can
                        // distinguish nullable constructed overloads. The MethodImpl descriptor is physical: erase
                        // each reified owner argument before lowering its alias. In particular,
                        // Comparable<Int?> selects compareTo(Int?) semantically but implements the collapsed
                        // non-generic System.IComparable.CompareTo(object) face physically.
                        var materializedOwner = (TypeNode.Fqn)sourceMapping.Slot(owner);
                        selectedDescriptorOwner = new TypeNode.Fqn(materializedOwner.Name, materializedOwner.Args?.Select(argument =>
                            NullableGenericErasure.EraseArgument(argument, isValue)).ToArray());
                        descriptorMember = markerBinding.Intrinsic;
                        selectionOwnerArgs = owner.Args?.Select(sourceMapping.Argument).Select(argument =>
                            BirTypeLowering.LowerPhysicalType(argument, refs.Aliases, isValue,
                                refs.PhysicalTypeNames, typeArg: true, nullableFrames: refs.NullableTypeFrames)).ToArray();
                    }
                    else if (refs.TryExactMemberIntrinsic(spec.Name, member, methodArity,
                            implementationSignature, spec.Args ?? Array.Empty<TypeNode>(), out var clrName))
                        descriptorMember = clrName;
                    else if (physicalProjection == null && descriptorMember == member
                        && refs.HasDotKtOwner(spec.Name)
                        && !refs.TryResolveClrOwner(spec.Name, out _, out _))
                    {
                        // Metadata-only stdlib declarations retain Kotlin names,
                        // while their runtime overrides adopt an intrinsic ancestor's
                        // allocation. Resolve the same exact inherited declaration
                        // binding; keep the Kotlin class as the MethodImpl owner.
                        var inheritedMembers = SupertypeGraph.Reachable(new Def { Base = spec }, defs, refs)
                            .Where(edge => edge.isInterface)
                            .Select(edge => refs.TryExactMemberIntrinsic(edge.spec.Name, member, methodArity,
                                implementationSignature, edge.spec.Args ?? Array.Empty<TypeNode>(),
                                out var inheritedMember) ? inheritedMember : null)
                            .Where(inheritedMember => inheritedMember != null)
                            .Distinct(StringComparer.Ordinal).ToArray();
                        if (inheritedMembers.Length > 1)
                            throw new InvalidOperationException(
                                $"bir2cir: referenced override '{spec.Name}.{member}`{methodArity}' inherits conflicting CLR names");
                        if (inheritedMembers.Length == 1) descriptorMember = inheritedMembers[0];
                    }

                    var loweredOwner = BirTypeLowering.LowerPhysicalType(
                        selectedDescriptorOwner, refs.Aliases, isValue, refs.PhysicalTypeNames,
                        typeArg: false, localTypeNames: null, nullableFrames: refs.NullableTypeFrames) as TypeNode.Fqn;
                    if (loweredOwner != null
                        && refs.ResolveNetType(loweredOwner.Name, loweredOwner.Args?.Length ?? 0)
                            is { IsInterface: true })
                    {
                        var usesSelectionFrame = selectionOwnerArgs != null
                            && selectionOwnerArgs.Length == (loweredOwner.Args?.Length ?? 0);
                        var comparableParams = (usesSelectionFrame ? implementationSignature : slotParams).Select(parameter => BirTypeLowering.LowerPhysicalType(
                            parameter, refs.Aliases, isValue, refs.PhysicalTypeNames,
                            typeArg: false, localTypeNames: null, nullableFrames: refs.NullableTypeFrames)).ToArray();
                        if (!ClrMemberResolution.TryResolveAliasedInterfaceSlotByShape(
                                refs, loweredOwner, descriptorMember, methodArity, comparableParams,
                                SourceMethodTypeParameterShapeKey(selectedSlotTypeParams, selectedSlotArgs),
                                out var declarationOwner, out var declarationMember,
                                out var physicalParams, out var physicalRet,
                                // Bindings such as Comparable deliberately collapse their owner arguments.
                                // Their existing physical selection remains authoritative.
                                usesSelectionFrame ? selectionOwnerArgs : null))
                            continue;
                        descriptorOwner = declarationOwner;
                        descriptorMember = declarationMember;
                        slotParams = physicalParams;
                        slotRet = physicalRet;
                        signatureParameters = null;
                        slotReturnsValue = physicalRet is not TypeNode.Fqn { Name: "void" or "System.Void", Args: null };
                    }
                }
                // Property overrides carry their Kotlin identity in the override marker. An ordinary declaration may
                // already have adopted the referenced CLR slot name in DeclarationRename; keep using the explicit
                // pre-rename identity it handed off instead of reflecting meaning back out of that physical spelling.
                // This identity also becomes the round-trip carrier on an exact interface MethodImpl bridge.
                var sourceIdentity = impl.KotlinSourceMember ?? ownName;
                // A flattened property closure may name both a class body and an interface declaration. The actual
                // descriptor owner determines the CLR table kind, not the supertype currently being traversed.
                var descriptorIsInterface = accessorKind == null ? supIsInterface
                    : refs.IsInterfaceType(descriptorOwner);
                // Keep the selected reference MethodDef's modifiers separate from argument storage.
                // Alias resolution above replaces the declaration and therefore owns its signature instead.
                TypeNode WithModifiers(TypeNode signature, TypeNode value) => signature is TypeNode.Mod modifier
                    ? new TypeNode.Mod(modifier.Req,
                        SupertypeGraph.SubstOwnerTvs(modifier.M, accessorKind != null ? physicalSelectedArgs : selectedSlotArgs),
                        WithModifiers(modifier.Of, value))
                    : value;
                var slotSignature = signatureParameters?.Select((signature, index) =>
                    WithModifiers(signature, slotParams[index])).ToArray();
                var slotHasDefault = descriptorIsInterface && refs.IsPublicConcreteInstanceMethod(
                    descriptorOwner.Name, descriptorMember, methodArity, slotParams, slotRet);
                // Own declarations precede inherited candidates. A materialized final-method forwarder
                // owns its selected slot, just as in the local-declaration arm; the inherited fact is
                // not a second implementation. Compare the complete resolved slot, not its name alone.
                var slotKey = SupertypeGraph.TypeKey(descriptorOwner) + "::" + descriptorMember
                    + "`" + methodArity + "(" + string.Join(",", (slotSignature ?? slotParams).Select(SupertypeGraph.TypeKey))
                    + ")->" + SupertypeGraph.TypeKey(slotRet)
                    + SourceMethodTypeParameterShapeKey(selectedSlotTypeParams, supArgs);
                if (inheritedSignatures?.ContainsKey(impl) == true)
                {
                    if (ownedSlots.Contains(slotKey)) continue;
                }
                else ownedSlots.Add(slotKey);
                fill(new SelectedSlot(selectedSpec, descriptorOwner, descriptorIsInterface, true, slotHasDefault,
                    accessorKind != null ? member : sourceIdentity, descriptorMember, accessorKind, slotParams, slotRet,
                    impl, selectedSlotTypeParams, slotReturnsValue && IsUnitValueSlot(slotRet), slotSignature,
                    !descriptorIsInterface && refs.HasDotKtOwner(selectedSpec.Name) ? selectedPhysicalMember : null,
                    nativeOwner != null && !descriptorIsInterface && slotRet0 != null
                        ? SupertypeGraph.SubstOwnerTvs(slotRet0, selectedArgs) : null,
                    SelectedReferencedDeclarationId: selectedClassDeclaration?.DeclarationId ?? o.DeclarationId));
                // Flattened property override facts can name several distinct CLR obligations (a redeclared Kotlin
                // accessor and its aliased BCL ancestor). Let each exact owner contribute its descriptor; the common
                // Fill/AddImplDescriptor path deduplicates genuinely identical rows. Ordinary methods retain their
                // selected-slot behavior.
                if (accessorKind == null) break;
            }
        }
    }

    static OverrideMethod FindImplementer(OverrideOwner owner, IReadOnlyDictionary<string,Def> defs,
        IEnumerable<OverrideMethod> methods, string supName, string physicalName, string semanticName, string slotDeclarationId,
        string propertyName, string accessorKind, int methodArity, TypeNode[] slotParams, BirTypeParameter[] slotTypeParams,
        TypeNode[] slotOwnerArgs, TypeNode[] ownArgs,
        IReadOnlyDictionary<OverrideMethod,TypeNode[]> inheritedSignatures = null,
        BirTypeParameter[] semanticSlotTypeParams = null, Func<TypeNode,TypeNode,bool> carrierSeam = null)
    {
        OverrideMethod found = null;
        var ownerFrame = owner.Frame;
        foreach (var m in methods)
        {
            if (m.Static || m.PhysicalBridge) continue;
            if (propertyName != null)
            {
                if (!m.Property(out var candidateProperty, out var candidateKind)
                    || candidateProperty != propertyName || candidateKind != accessorKind) continue;
            }
            // Suspend lowering has already split one declaration into hot/cold MethodDefs. An explicitly named DIM
            // gives the base projection its final explicit spelling, while the overriding projection keeps its own
            // physical name; `kotlinSourceMember` is the carried frontend identity that relates those two projections.
            // An unchanged implementation still has that semantic name directly even when its local base was renamed.
            // The override closure below remains the independent proof that this candidate actually fills this slot.
            else if ((m.Name != physicalName
                      && (slotDeclarationId == null
                          || m.Name != semanticName
                              && m.KotlinSourceMember != semanticName))
                     || m.Property(out _, out _)) continue;
            if ((m.Arity) != methodArity) continue;
            // Generic constraints are part of a CLR MethodDef's implementation contract even when name, arity,
            // parameters, and return are otherwise identical. Kotlin's frontend has already selected the override;
            // compare that selected declaration's constraints in the two constructed owner frames so the A-bounded
            // slot cannot be attached to the B-bounded body (which makes the containing type unloadable).
            if (!SameSourceMethodTypeParameterShape(semanticSlotTypeParams ?? slotTypeParams,
                    semanticSlotTypeParams == null ? m.TypeParameters : SourceParameters(m),
                    slotOwnerArgs, ownArgs)) continue;
            if (!m.ParamsPresent || m.Parameters.Length != slotParams.Length) continue;
            var ps = m.Parameters;
            if (m.Visibility is not (null or "public" or "protected")) continue;
            if (!OverridesInto(m, defs, supName, propertyName ?? semanticName,
                accessorKind switch { "get" => "getter", "set" => "setter", _ => "method" },
                slotDeclarationId)) continue;
            // Every position must either already BE the slot or differ from it exactly where the slot was erased —
            // the only positions an override is free to narrow, and so the only ones whose types legitimately differ.
            var ok = true;
            TypeNode[] inheritedSignature = null;
            inheritedSignatures?.TryGetValue(m, out inheritedSignature);
            for (var i = 0; i < ps.Length && ok; i++)
            {
                var t = inheritedSignature?[i] ?? ps[i].Signature ?? ps[i].Type;
                var declared = t == null ? null : SupertypeGraph.SubstOwnerTvs(t, ownArgs);
                ok = declared != null && (ErasureAligned(slotParams[i], declared)
                    || carrierSeam?.Invoke(slotParams[i], declared) == true
                    || IsCompanionScalarSeam(slotParams[i], declared)
                    || SameSourceRepresentationVariables(slotParams[i], declared, ownerFrame)
                    || SameSourceFunctionRepresentation(slotParams[i], declared, ownerFrame));
            }
            if (!ok) continue;
            if (found != null) return null;   // ambiguous overload set: never guess which declaration owns the slot
            found = m;
        }
        return found;
    }


    static bool OverridesInto(OverrideMethod method, IReadOnlyDictionary<string,Def> definitions,
        string owner, string member, string kind, string declarationId)
    {
        var selectedId = SourceDeclarationId(declarationId);
        foreach (var edge in method.Overrides)
            if (edge.Owner is TypeNode.Fqn from && edge.Member == member && edge.Kind == kind
                && (selectedId == null || edge.DeclarationId == selectedId)
                && (from.Name == owner || IsAncestor(definitions, owner, from.Name))) return true;
        return false;
    }

    static string ResolveSlotName(OverrideMethod method, ReferenceMetadataIndex references) => method.Source != null
        ? DeclarationRename.ResolveSlot(method.Source, references)
        : DeclarationRename.ResolveSlot(method.Dom, method.Dom["overrides"].AsArray(), references);
}
