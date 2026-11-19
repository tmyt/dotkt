using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using Def = SupertypeGraph.Def;

static partial class KotlinOverrideSlotBridge
{
    // A fresh current physical view, not the original source-eligibility inventory.
    sealed class FinalSourceContext
    {
        internal readonly SourceSelectionIndex Index;
        internal readonly OverrideViews Views;
        internal readonly ReferenceMetadataIndex References;
        internal readonly ValueTypeOracle IsValue;
        internal readonly GenericRepresentationPolicy Representations;
        internal readonly IReadOnlySet<string> LocalNames;
        internal readonly bool ReferenceBuild;
        internal readonly IReadOnlySet<CovariantInterfaceReturnBridge.SourceBridgedSlot> Covariant;
        internal readonly Func<TypeNode, bool> HasClassifier;
        readonly IReadOnlyDictionary<string, string> carriers;
        readonly Dictionary<string, NullableRepresentationFrame> applications;
        readonly Dictionary<IBirTypeDeclaration, BirFileDeclaration> files = new(ReferenceEqualityComparer.Instance);
        readonly Action<BirFileDeclaration, IBirTypeDeclaration, BirFunctionDeclaration, BirGraphValue> materialized;
        internal readonly Dictionary<BirFunctionDeclaration, string> ExactSources = new(ReferenceEqualityComparer.Instance);

        internal FinalSourceContext(IReadOnlyList<BirFileDeclaration> roots, ValueTypeOracle isValue,
            ReferenceMetadataIndex references, IReadOnlySet<string> localNames, bool referenceBuild,
            GenericRepresentationPolicy representations, IReadOnlySet<CovariantInterfaceReturnBridge.SourceBridgedSlot> covariant,
            IReadOnlyDictionary<string, string> carriers, Func<TypeNode, bool> hasClassifier,
            Action<BirFileDeclaration, IBirTypeDeclaration, BirFunctionDeclaration, BirGraphValue> materialized)
        {
            Index = CreateSourceSelection(roots); Views = new(Index.Declarations);
            References = references; IsValue = isValue; LocalNames = localNames; ReferenceBuild = referenceBuild;
            Representations = representations; Covariant = covariant; this.carriers = carriers;
            HasClassifier = hasClassifier ?? throw new ArgumentNullException(nameof(hasClassifier));
            this.materialized = materialized;
            foreach (var file in roots)
                foreach (var declaration in file.Types.OrEmpty()) files[declaration] = file;
            var frames = references.NullableTypeFrames.ToDictionary(pair => pair.Key, pair => pair.Value);
            foreach (var definition in Index.Definitions.Values)
                if (Views.Owner(definition).Frame is { } frame) frames[definition.Name] = frame;
            applications = new InnerApplicationFrames(roots, references).Project(frames, retainPhysicalOrder: true);
            foreach (var definition in Index.Definitions.Values.Where(value => value.Kind == "interface"))
                foreach (var method in Index.Declarations[definition.Name].Methods.OrEmpty())
                    if (method.ClrInterfaceSlotBridge.OrDefault()
                        && method.KotlinPropertyAccessorCarrier.OrDefault()?.SourceAssociation.OrDefault() is string association)
                        ExactSources[method] = association;
        }

        internal NullableRepresentationTypes Mapping(Def definition) => new(Views.Owner(definition).Frame,
            null, applications, IsValue, policy: Representations);
        internal TypeNode Physical(TypeNode type) => BirTypeLowering.LowerPhysicalType(type,
            References.Aliases, IsValue, References.PhysicalTypeNames, typeArg: false, LocalNames,
            nullableFrames: References.NullableTypeFrames);
        internal TypeNode[] Annotation(TypeNode.Fqn type) => BirTypeLowering.AnnotationArguments(type,
            References.Aliases, IsValue, References.PhysicalTypeNames,
            LocalNames ?? Index.Definitions.Keys.ToHashSet(StringComparer.Ordinal), ReferenceBuild, References.NullableTypeFrames);
        internal bool Assignable(TypeNode source, TypeNode target, bool boxing = false) =>
            FBoundStarProjectionErasure.IsClrAssignable(source, target, Index.Declarations, References,
                physicalTypes: true, allowBoxing: boxing);
        internal bool CarrierSeam(TypeNode left, TypeNode right)
        {
            static TypeNode Core(TypeNode type) => type switch {
                TypeNode.Nullable nullable => Core(nullable.Of), TypeNode.Oblivious oblivious => Core(oblivious.Of), _ => type,
            };
            bool Matches(TypeNode exact, TypeNode view) => Core(exact) is TypeNode.Fqn { Args.Length: > 0 } construction
                && Core(view) is TypeNode.Fqn carrier
                && (carriers?.TryGetValue(construction.Name, out var local) == true && local == carrier.Name
                    || References.TryExistentialPhysicalOwner(construction.Name, out var referenced) && referenced == carrier.Name);
            return Matches(left, right) || Matches(right, left);
        }
        internal void Append(IBirTypeDeclaration owner, BirFunctionDeclaration method)
        {
            owner.Methods = owner.Methods.OrEmpty().Append(method).ToArray();
            materialized?.Invoke(files[owner], owner, method, null);
        }
        internal void Attach(IBirTypeDeclaration owner, BirFunctionDeclaration method, BirMethodImpl descriptor, bool isInterface)
        {
            var previous = isInterface ? method.ClrInterfaceImpls : method.ClrBaseImpls;
            var attached = AddSourceImpl(method, descriptor, isInterface);
            if (!previous.OrEmpty().Any(value => ReferenceEquals(value, attached)))
                materialized?.Invoke(files[owner], owner, null, attached);
        }
    }

    internal static void ApplyAll(IReadOnlyList<BirFileDeclaration> roots, ValueTypeOracle isValue,
        ReferenceMetadataIndex refs, IReadOnlySet<string> localTypeNames, bool refBuild,
        GenericRepresentationPolicy representations,
        IReadOnlySet<CovariantInterfaceReturnBridge.SourceBridgedSlot> covariantBridgedSlots,
        IReadOnlyDictionary<string, string> localCarriers, Func<TypeNode, bool> hasClassifier,
        Action<BirFileDeclaration, IBirTypeDeclaration, BirFunctionDeclaration, BirGraphValue> onMaterialized = null)
    {
        var context = new FinalSourceContext(roots, isValue, refs, localTypeNames, refBuild,
            representations, covariantBridgedSlots, localCarriers, hasClassifier, onMaterialized);
        foreach (var definition in context.Index.Definitions.Values.Where(value => value.Kind is "class" or "interface").ToArray())
            ApplyFinalSourceOwner(definition, context);
        // Allocation on every interface must finish before any class consumes an exact DIM relation.
        foreach (var definition in context.Index.Definitions.Values.Where(value => value.Kind == "class").ToArray())
            RepairFinalSourceDefaults(definition, context);
        foreach (var declaration in context.Index.Declarations.Values)
            foreach (var method in declaration.Methods.OrEmpty()) method.SuspendTaskOnlyBridge = default;
    }

    static void ApplyFinalSourceOwner(Def definition, FinalSourceContext context)
    {
        var declaration = context.Index.Declarations[definition.Name];
        if (!declaration.Methods.HasValue) return;
        var owner = context.Views.Owner(definition);
        var ownArguments = ClassOwnArgs(definition);
        var mapping = context.Mapping(definition);
        var refs = context.References; var isValue = context.IsValue; var defs = context.Index.Definitions;
        var inherited = new Dictionary<OverrideMethod, SelectedInheritedSourceMethod>();
        foreach (var selected in SelectInheritedMethods(owner, defs, context.Views, refs, isValue,
                     SourceInheritedFacts(declaration, false), false, false, false))
            inherited.Add(context.Views.Method(selected.Method), selected);
        var candidates = owner.Methods.Concat(inherited.Keys).ToArray();
        var signatures = inherited.ToDictionary(pair => pair.Key, pair => pair.Value.SelectionParameters);
        var bridges = new Dictionary<string, BirFunctionDeclaration>(StringComparer.Ordinal);
        var claims = new Dictionary<(OverrideMethod Body, string Shape), List<OverrideMethod>>();
        var ordinal = 0;
        var enumeration = context.ReferenceBuild ? null : ReverseEnumeratorBridgeSynthesis.ReserveSlots(
            definition, defs, refs, context.Index.Declarations);
        bool CanPassAsObject(TypeNode type, OverrideMethod method)
        {
            type = SignatureValueTypes.Of(context.Physical(type));
            if (type is TypeNode.Nullable nullable) return CanPassAsObject(nullable.Of, method);
            if (type is TypeNode.Oblivious oblivious) return CanPassAsObject(oblivious.Of, method);
            if (type is TypeNode.Fqn named) return !refs.IsByRefLikeFqn(named);
            if (type is TypeNode.Tv variable)
            {
                var parameters = variable.Scope == "type" ? TypeParameterFrame.Declarations(declaration).ToArray() : method.TypeParameters ?? [];
                return variable.I >= 0 && variable.I < parameters.Length
                    && !parameters[variable.I].SpecialConstraints.OrEmpty().Contains("allowsRefStruct");
            }
            return type is TypeNode.Array or TypeNode.Fn;
        }
        bool CanReturn(TypeNode source, TypeNode target, OverrideMethod method)
        {
            var semantic = method.Source?.RetKotlinType.OrDefault() ?? method.Header?.RetKotlinType.OrDefault();
            if (LowersToObject(source, refs) && semantic is TypeNode.Tv variable)
                source = (variable.Scope == "method" ? method.Frame : mapping.OwnerFrame)?.Variable(variable,
                    NullableRepresentationFrame.Role.Ordinary) ?? variable;
            var seen = new HashSet<TypeNode.Tv>();
            bool Prove(TypeNode value)
            {
                if (!CanPassAsObject(value, method)) return false;
                if (context.Assignable(value, target, boxing: true)) return true;
                if (value is not TypeNode.Tv tv || !seen.Add(tv)) return false;
                var parameters = tv.Scope == "type" ? TypeParameterFrame.Declarations(declaration).ToArray() : method.TypeParameters ?? [];
                return tv.I >= 0 && tv.I < parameters.Length
                    && parameters[tv.I].Constraints.OrEmpty().Any(bound => Prove(context.Physical(bound)));
            }
            return Prove(source);
        }
        SelectSlots(owner, defs, context.Views, candidates, ownArguments, isValue, refs,
            Phase.PhysicalBridges, context.ReferenceBuild, context.LocalNames, mapping, context.Representations,
            context.CarrierSeam, Fill, signatures);

        void Fill(SelectedSlot slot)
        {
            var implementation = slot.Implementation;
            if (slot.IsInterface && enumeration?.Owns(slot.DescriptorSpec, slot.DescriptorMember,
                    implementation.Arity, slot.Parameters, slot.Return, context.Physical, refs) == true) return;
            inherited.TryGetValue(implementation, out var selectedBody);
            if (selectedBody != null && !slot.IsInterface) return;
            var reimplements = slot.IsInterface && definition.Kind == "class"
                && (ReachesFromDeclaredInterface(definition, slot.SemanticSpec, defs, refs)
                    || slot.Referenced && ReachesFromDeclaredPhysicalInterface(definition, slot.DescriptorSpec,
                        defs, refs, isValue, context.LocalNames));
            var inheritedDefault = slot.IsInterface && definition.Kind == "class" && !reimplements && slot.HasDefault;
            if (slot.IsInterface && definition.Kind == "class" && !reimplements && !inheritedDefault) return;
            var typeParameters = SubstituteParameters(slot.TypeParameters, slot.SemanticSpec.Args ?? []);
            var fit = AssessFit(implementation, ownArguments, slot.Parameters, slot.Return, slot.UnitValueReturn,
                slot.Signature, slot.IsInterface, slot.Referenced, slot.DescriptorSpec, slot.PropertyAccessor,
                Phase.PhysicalBridges, selectedBody != null, isValue, refs, context.LocalNames, mapping,
                context.Physical, context.CarrierSeam, (source,target) => context.Assignable(source,target),
                type => CanPassAsObject(type, implementation), (source,target) => CanReturn(source,target,implementation),
                () => HasExistingFinalSourceBridge(declaration, slot, implementation.Arity, typeParameters, context),
                () => implementation.Source != null && context.Covariant?.Contains(new(
                    implementation.Source, CovariantInterfaceReturnBridge.DescriptorKey(slot.DescriptorSpec,
                        slot.DescriptorMember, implementation.Arity, slot.Parameters, slot.Return, refs, isValue))) == true,
                context.HasClassifier);
            if (fit == null) return;
            ApplyDeclarationEdits(implementation, ownArguments, slot.Parameters, slot.Return, fit,
                Phase.PhysicalBridges, isValue, context.Annotation);
            var needsSignature = fit.Parameters.Contains(Fit.Bridge) || fit.Return == Fit.Bridge;
            var needsProperty = slot.IsInterface && slot.PropertyAccessor != null && slot.DescriptorMember != implementation.Name
                && (definition.Kind != "class" || reimplements || inheritedDefault);
            var needsInterface = slot.IsInterface && (selectedBody != null
                || implementation.Parameters.Any(parameter => parameter.Signature != null)
                || definition.Kind == "interface" && !implementation.Abstract
                || definition.Kind == "class" && reimplements && slot.DescriptorMember != implementation.Name
                || definition.Kind == "class" && inheritedDefault);
            var needsBase = !slot.IsInterface && !slot.Referenced && slot.DescriptorMember != implementation.Name;
            if (!needsSignature && definition.Kind == "class" && selectedBody == null
                && IsNativeSlotOwner(slot.DescriptorSpec.Name, defs, refs.HasDotKtOwner))
                DeclarationSignatureIdentity.RecordSelectedPhysicalSlot(implementation.Source,
                    SourceImplDescriptor(slot.DescriptorSpec,slot.DescriptorMember,implementation.Arity,
                        slot.Signature ?? slot.Parameters,slot.Return,typeParameters,slot.UnitValueReturn),slot.IsInterface);
            if (!needsSignature && !needsProperty && !needsInterface && !needsBase) return;
            if (!slot.IsInterface)
            {
                string Key(TypeNode type) => SupertypeGraph.TypeKey(BirTypeLowering.CanonicalPhysicalSlotType(context.Physical(type)));
                var shape = slot.DescriptorMember + "`" + implementation.Arity + "("
                    + string.Join(",",(slot.Signature ?? slot.Parameters).Select(Key)) + ")->" + Key(slot.Return)
                    + MethodTypeParameterShapeKey(typeParameters,ownArguments,
                        type => BirTypeLowering.CanonicalPhysicalSlotType(context.Physical(type)))
                    + (slot.UnitValueReturn ? "[value-return]" : "");
                var claim = (implementation,shape);
                var selectedId = slot.SelectedReferencedDeclarationId ?? slot.SelectedLocal?.DeclarationId;
                if (claims.TryGetValue(claim,out var declarations) && selectedId != null
                    && declarations.Any(previous => OverridesInto(previous,defs,slot.SemanticSpec.Name,slot.IdentityName,
                        slot.PropertyAccessor switch { "get" => "getter", "set" => "setter", _ => "method" },selectedId))) return;
                if (slot.SelectedLocal != null)
                {
                    if (declarations == null) claims[claim] = declarations = new();
                    declarations.Add(slot.SelectedLocal);
                }
            }
            // An inherited selection is metadata, not a MethodDef here: even an exact slot needs
            // a real forwarding body rather than a descriptor attached to that temporary header.
            if (!needsSignature && definition.Kind != "interface" && selectedBody == null)
            {
                implementation.SetVirtual();
                var descriptor = SourceImplDescriptor(slot.DescriptorSpec,slot.DescriptorMember,implementation.Arity,
                    slot.Signature ?? slot.Parameters,slot.Return,typeParameters,slot.UnitValueReturn);
                if (slot.Signature == null)
                    for (var i=0;i<implementation.Parameters.Length;i++)
                        if (implementation.Parameters[i].Signature is { } discriminator)
                            descriptor.Params.Value[i] = new TypeNode.Mod(false,SupertypeGraph.SubstOwnerTvs(
                                FunctionSignatureIdentity.DeclarationType(discriminator),ownArguments),slot.Parameters[i]);
                context.Attach(declaration,implementation.Source,descriptor,slot.IsInterface);
                if (slot.SelectedLocal?.Source != null)
                    DeclarationSignatureIdentity.RecordSelectedImplementationSlot(implementation.Source,slot.SelectedLocal.Source);
                return;
            }
            // Interfaces implement inherited slots through private-final MethodImpl bodies. Do not
            // replace an authored overridable DIM, or invent a forwarding body for an abstract one.
            if (definition.Kind == "interface" && implementation.Abstract) return;
            implementation.SetVirtual();
            string PhysicalKey(TypeNode type) => SupertypeGraph.TypeKey(context.Physical(type));
            // CLR method generic arity is part of the slot identity (ECMA-335 I.8.6.1.6).
            // The selected BODY is part of sharing identity too: equal erased A<Int>/B<String>
            // slots can forward to different overloads, and must not share the first body's cast.
            var key = slot.IdentityName + "`" + implementation.Arity + "("
                + string.Join(",",(slot.Signature ?? slot.Parameters).Select(PhysicalKey)) + ")->" + PhysicalKey(slot.Return)
                + "{" + implementation.Name + "<" + MethodTypeParameterShapeKey(implementation.TypeParameters,ownArguments)
                + ">(" + string.Join(",",implementation.Parameters.Select(parameter => parameter.Type is {} type ? SupertypeGraph.TypeKey(type) : "?")) + ")}";
            if (selectedBody != null) key += "[body-owner:" + SupertypeGraph.TypeKey(selectedBody.Owner) + "]";
            // CoreCLR does not permit one private-final interface body to implement both declarations
            // in an inherited interface chain. Classes may share a body across independent slots.
            if (definition.Kind == "interface") key += "[slot:" + SupertypeGraph.TypeKey(slot.DescriptorSpec) + "::" + slot.DescriptorMember + "]";
            if (slot.UnitValueReturn) key += "[value-return]";
            if (!bridges.TryGetValue(key,out var bridge))
            {
                var name = $"dotkt$ovslot${SafeName(slot.IdentityName)}${ordinal++}";
                bridge = selectedBody == null
                    ? BuildBridgeSource(declaration,implementation.Source,slot.Parameters,slot.Return,name,isValue,refs,
                        unitValueReturn:slot.UnitValueReturn,physicalImplementation:true)
                    : BuildBridgeSource(declaration,selectedBody,slot.Parameters,slot.Return,name,isValue,refs,
                        unitValueReturn:slot.UnitValueReturn,physicalImplementation:true);
                if (slot.Signature != null)
                    for (var i=0;i<slot.Signature.Length;i++)
                        if (slot.Signature[i] is TypeNode.Mod { Req:false } signature)
                            bridge.Params.Value[i].FunctionSignatureType = BirGraphValue.CopyType(signature.M);
                if (slot.PropertyAccessor == null)
                {
                    RoundtripMetadata.AddSourceMethodIdentity(bridge,slot.IdentityName,implementation.DeclarationId);
                    if (definition.Kind == "interface" && implementation.DeclarationId is {} id) context.ExactSources[bridge] = id;
                }
                bridges[key] = bridge;
                context.Append(declaration,bridge);
                if (slot.PropertyAccessor != null)
                {
                    var association = implementation.Source?.PropertyAssociation.OrDefault() ?? implementation.Header?.PropertyAssociation.OrDefault();
                    if (definition.Kind == "interface")
                        KotlinPropertyAccessors.MarkExactInterfaceBridgeProperty(bridge,slot.IdentityName,slot.PropertyAccessor,association);
                    else KotlinPropertyAccessors.AssociateBridgeProperty(declaration,bridge,slot.IdentityName,
                        slot.PropertyAccessor,association,slot.Parameters,slot.Return);
                    if (definition.Kind == "interface") context.ExactSources[bridge] = association;
                }
            }
            var exact = SourceImplDescriptor(slot.DescriptorSpec,slot.DescriptorMember,implementation.Arity,
                slot.Signature ?? slot.Parameters,slot.Return,typeParameters,slot.UnitValueReturn);
            if (slot.ReferenceMember != null && slot.ReferenceMember != slot.DescriptorMember) exact.ReferenceMember = slot.ReferenceMember;
            context.Attach(declaration,bridge,exact,slot.IsInterface);
            bool PendingOwner(TypeNode pending) => slot.SemanticReturn == null
                ? ReferenceMetadataIndex.SourceDeclarationDescribesCall(pending,slot.DescriptorSpec)
                : pending is TypeNode.Fqn pendingSpec && SupertypeGraph.Reachable(new Def { Base=pendingSpec },defs,refs)
                    .Any(edge => !edge.isInterface && ReferenceMetadataIndex.SourceDeclarationDescribesCall(edge.spec,slot.SemanticSpec));
            if (!slot.IsInterface && implementation.Source is {} actual
                && actual.PendingOverrideOwner.OrDefault() is {} pendingOwner && PendingOwner(pendingOwner)
                && (actual.PendingOverrideMember.OrDefault() ?? actual.Name.OrDefault()) == slot.DescriptorMember
                && actual.PendingOverrideReturn.OrDefault() is {} pendingReturn
                && ReferenceMetadataIndex.SourceDeclarationDescribesCall(pendingReturn,slot.SemanticReturn ?? slot.Return))
            { actual.PendingOverrideOwner=default; actual.PendingOverrideMember=default; actual.PendingOverrideReturn=default; }
        }
    }

    static bool HasExistingFinalSourceBridge(IBirTypeDeclaration declaration,SelectedSlot slot,int arity,
        BirTypeParameter[] parameters,FinalSourceContext context)
    {
        foreach (var method in declaration.Methods.OrEmpty().Where(value => value.PhysicalSlotBridge.OrDefault()))
            foreach (var descriptor in (slot.IsInterface ? method.ClrInterfaceImpls : method.ClrBaseImpls).OrEmpty())
                if (descriptor.Owner.OrDefault() is {} owner && descriptor.Member.OrDefault()==slot.DescriptorMember
                    && descriptor.Arity.HasValue && descriptor.Arity.Value==arity && descriptor.Params.HasValue && descriptor.Ret.HasValue
                    && BirTypeLowering.SamePhysicalSlotType(owner,slot.DescriptorSpec,context.References.Aliases,
                        context.IsValue,context.References.PhysicalTypeNames,false,nullableFrames:context.References.NullableTypeFrames)
                    && SameSourceMethodTypeParameterShape(descriptor.TypeParams.OrDefault(),parameters,[],[])
                    && SamePhysicalSignature(descriptor.Params.Value,descriptor.Ret.Value,slot.Parameters,slot.Return,
                        context.References,context.IsValue,context.HasClassifier)) return true;
        return false;
    }
}
