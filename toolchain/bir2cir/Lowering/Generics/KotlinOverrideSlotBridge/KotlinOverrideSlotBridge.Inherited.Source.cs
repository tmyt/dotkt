using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using Def = SupertypeGraph.Def;

static partial class KotlinOverrideSlotBridge
{
    // Selection headers are named metadata, not fabricated source MethodDefs. A local
    // selection keeps its exact source record separately; only BuildBridgeSource authors a declaration.
    internal sealed class SelectedParameterHeader
    {
        internal OptionalField<string> Name { get; set; }
        internal OptionalField<TypeNode> Type { get; set; }
        internal OptionalField<TypeNode> FunctionSignatureType { get; set; }
        internal OptionalField<TypeNode> KotlinType { get; set; }
        internal OptionalField<TypeNode> NullableGeneric { get; set; }
        internal OptionalField<int[]> NullableFlags { get; set; }
        internal SelectedParameterHeader Copy() => (SelectedParameterHeader)MemberwiseClone();
        internal static SelectedParameterHeader FromSource(BirParameter value) => new() {
            Name = value.Name, Type = value.Type, FunctionSignatureType = value.FunctionSignatureType,
            KotlinType = value.KotlinType, NullableGeneric = value.NullableGeneric, NullableFlags = value.NullableFlags,
        };
    }

    internal sealed class SelectedMethodHeader
    {
        internal BirFunctionDeclaration Source { get; init; }
        internal OptionalField<string> Name { get; set; }
        internal OptionalField<string> ExplicitClrName { get; set; }
        internal OptionalField<string> DeclarationId { get; set; }
        internal OptionalField<SelectedParameterHeader[]> Params { get; set; }
        internal OptionalField<TypeNode> Ret { get; set; }
        internal OptionalField<BirTypeParameter[]> TypeParams { get; set; }
        internal OptionalField<BirModifiers> Mods { get; set; }
        internal OptionalField<BirOverride[]> Overrides { get; set; }
        internal OptionalField<string> PropertyName { get; set; }
        internal OptionalField<string> PropertyAccessor { get; set; }
        internal OptionalField<string> PropertyAssociation { get; set; }
        internal OptionalField<NullableRepresentationFrame> NullableRepresentationFrame { get; set; }
        internal OptionalField<SourceBoundsSnapshot> MethodTypeParameterBoundsPre { get; set; }
        internal OptionalField<TypeNode> NullableGenericRet { get; set; }
        internal OptionalField<TypeNode> RetKotlinType { get; set; }
        internal OptionalField<int[]> RetNullableFlags { get; set; }
        internal OptionalField<TypeNode> SuspendResult { get; set; }
        internal OptionalField<bool> DotktValueReturn { get; set; }
        internal OptionalField<bool> Static { get; set; }
        internal OptionalField<bool> Abstract { get; set; }
        internal OptionalField<string> Vis { get; set; }
        internal OptionalField<string> KotlinSourceMember { get; set; }
        internal OptionalField<string> ExistentialSourceMember { get; set; }
        internal OptionalField<bool> PhysicalSlotBridge { get; set; }
        internal OptionalField<TypeNode[]> SuspendSourceParameters { get; set; }
        internal OptionalField<TypeNode> SuspendSourceReturn { get; set; }
        internal SelectedMethodHeader Copy() => (SelectedMethodHeader)MemberwiseClone();
        internal bool Property(out string name, out string kind)
        {
            name = PropertyName.OrDefault(); kind = PropertyAccessor.OrDefault();
            return KotlinPropertyAccessors.HasIdentity(name, kind);
        }
        internal static SelectedMethodHeader FromSource(BirFunctionDeclaration value) => new() {
            Source = value, Name = value.Name, ExplicitClrName = value.ExplicitClrName, DeclarationId = value.DeclarationId,
            Params = !value.Params.IsPresent ? default : value.Params.IsNull ? OptionalField<SelectedParameterHeader[]>.Null
                : value.Params.Value.Select(SelectedParameterHeader.FromSource).ToArray(),
            Ret = value.Ret, TypeParams = value.TypeParams, Mods = value.Mods, Overrides = value.Overrides,
            PropertyName = value.PropertyName, PropertyAccessor = value.PropertyAccessor, PropertyAssociation = value.PropertyAssociation,
            NullableRepresentationFrame = value.NullableRepresentationFrame, MethodTypeParameterBoundsPre = value.MethodTypeParameterBoundsPre,
            NullableGenericRet = value.NullableGenericRet, RetKotlinType = value.RetKotlinType, RetNullableFlags = value.RetNullableFlags,
            SuspendResult = value.SuspendResult, DotktValueReturn = value.DotktValueReturn, Static = value.Static,
            Abstract = value.Abstract, Vis = value.Vis, KotlinSourceMember = value.KotlinSourceMember,
            ExistentialSourceMember = value.ExistentialSourceMember, PhysicalSlotBridge = value.PhysicalSlotBridge,
            SuspendSourceParameters = value.SuspendSourceParams, SuspendSourceReturn = value.SuspendSourceRet,
        };
    }

    internal sealed class SourceSelectionIndex
    {
        internal IReadOnlyDictionary<string, Def> Definitions { get; }
        internal IReadOnlyDictionary<string, IBirTypeDeclaration> Declarations { get; }
        internal SourceSelectionIndex(IReadOnlyList<BirFileDeclaration> roots)
        {
            Definitions = SupertypeGraph.CollectSource(roots);
            var declarations = new Dictionary<string, IBirTypeDeclaration>(StringComparer.Ordinal);
            foreach (var declaration in roots.SelectMany(root => root.Types.OrEmpty()))
                if (declaration.Name.OrDefault() is string name) declarations[name] = declaration;
            Declarations = declarations;
        }
    }
    internal static SourceSelectionIndex CreateSourceSelection(IReadOnlyList<BirFileDeclaration> roots) => new(roots);

    internal sealed record SelectedInheritedSourceMethod(TypeNode.Fqn Owner, SelectedMethodHeader Method,
        BirGraphValue Fact, IReadOnlyList<BirOverride> Overrides, bool IsDefault, TypeNode[] DeclarationSignature,
        string DeclarationId, TypeNode[] QueryBounds, TypeNode[] SelectionParameters)
    {
        internal BirFunctionDeclaration Source => Method.Source;
    }

    sealed record InheritedFact(SelectedMethodHeader Header, TypeNode ImplementationOwner, string ImplementationMember,
        int ImplementationArity, BirTypeParameter[] ImplementationParameters, SourceBoundsSnapshot ImplementationBounds,
        BirGraphValue Source, string DeclarationId);

    static InheritedFact InheritedSourceFact(BirGraphValue fact)
    {
        SelectedMethodHeader header;
        BirInheritedImplementation implementation;
        switch (fact)
        {
            case BirInheritedClassMethod method:
                header = new() { Name = method.Member, Params = method.Params.HasValue
                    ? method.Params.Value.Select(SelectedParameterHeader.FromSource).ToArray() : default,
                    Ret = method.Ret, TypeParams = method.TypeParams, Mods = method.Mods, Overrides = method.Overrides,
                    PropertyName = method.PropertyName, PropertyAccessor = method.PropertyAccessor,
                    PropertyAssociation = method.PropertyAssociation, NullableGenericRet = method.NullableGenericRet,
                    RetKotlinType = method.RetKotlinType, MethodTypeParameterBoundsPre = method.MethodTypeParameterBoundsPre };
                implementation = method.InheritedImplementation.OrDefault(); break;
            case BirInheritedDefaultAccessor accessor:
                header = new() { Name = accessor.PropertyName, PropertyName = accessor.PropertyName,
                    PropertyAccessor = accessor.PropertyAccessor, Params = DefaultParameters(accessor.Params.OrEmpty()),
                    Ret = accessor.Ret, Overrides = accessor.Overrides, NullableGenericRet = accessor.NullableGenericRet };
                implementation = accessor.Implementation.OrDefault(); break;
            case BirInheritedDefaultMethod method:
                header = new() { Name = method.Member, Params = DefaultParameters(method.Params.OrEmpty()),
                    Ret = method.Ret, Overrides = method.Overrides, NullableGenericRet = method.NullableGenericRet };
                implementation = method.Implementation.OrDefault(); break;
            default: throw new InvalidOperationException("Unexpected inherited selection fact: " + fact.GetType().Name);
        }
        return implementation == null ? null : new(header, implementation.Owner.OrDefault(), implementation.Member.OrDefault(),
            implementation.Arity.HasValue ? implementation.Arity.Value : -1, implementation.TypeParams.OrDefault(),
            implementation.MethodTypeParameterBoundsPre.OrDefault(), fact, implementation.DeclarationId.OrDefault());
        static SelectedParameterHeader[] DefaultParameters(TypeNode[] types) => types.Select((type, index) =>
            new SelectedParameterHeader { Name = "p" + index, Type = type }).ToArray();
    }

    static IEnumerable<InheritedFact> SourceInheritedFacts(IBirTypeDeclaration owner, bool defaults)
    {
        if (owner is not BirClassDefinition type) yield break;
        IEnumerable<BirGraphValue> facts = defaults
            ? type.InheritedDefaultAccessors.OrEmpty().Cast<BirGraphValue>().Concat(type.InheritedDefaultMethods.OrEmpty())
            : type.InheritedClassMethods.OrEmpty();
        foreach (var fact in facts)
            if (InheritedSourceFact(fact) is { } selected) yield return selected;
    }

    internal static Dictionary<string, List<SelectedInheritedSourceMethod>> SelectedInheritedCollectionMethods(
        IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs, ValueTypeOracle isValue)
    {
        var index = CreateSourceSelection(roots);
        var views = new OverrideViews(index.Declarations);
        var result = new Dictionary<string, List<SelectedInheritedSourceMethod>>(StringComparer.Ordinal);
        foreach (var cls in index.Definitions.Values.Where(definition => definition.Kind == "class"))
        {
            var selected = SelectInheritedMethods(views.Owner(cls), index.Definitions, views, refs, isValue,
                SourceInheritedFacts(index.Declarations[cls.Name], false).Where(fact =>
                    fact.Header.Overrides.OrEmpty().Any(KotlinCollectionSlotSynthesis.OwnsSupplementalOverride)), false, false, true);
            if (selected.Count != 0) result[cls.Name] = selected;
        }
        return result;
    }

    internal static IReadOnlyList<SelectedInheritedSourceMethod> SelectedInheritedImplementationsSource(
        IBirTypeDeclaration cls, SourceSelectionIndex index, ReferenceMetadataIndex refs, ValueTypeOracle isValue,
        Func<IReadOnlyList<BirOverride>, bool, bool> includeFact)
    {
        var views = new OverrideViews(index.Declarations);
        var owner = views.Owner(index.Definitions[cls.Name.Value]);
        var result = new List<SelectedInheritedSourceMethod>();
        foreach (var defaults in new[] { false, true })
            result.AddRange(SelectInheritedMethods(owner, index.Definitions, views, refs, isValue,
                SourceInheritedFacts(cls, defaults).Where(fact => includeFact(fact.Header.Overrides.OrEmpty(), defaults)),
                false, defaults, false));
        return result;
    }

    // The actual source and physical readers share this one selected-declaration algorithm.
    static List<SelectedInheritedSourceMethod> SelectInheritedMethods(OverrideOwner cls,
        IReadOnlyDictionary<string, Def> definitions, OverrideViews views, ReferenceMetadataIndex refs, ValueTypeOracle isValue,
        IEnumerable<InheritedFact> facts, bool suspendValues, bool defaults, bool collectionQueries)
    {
        var result = new List<SelectedInheritedSourceMethod>();
        var bases = SupertypeGraph.Reachable(cls.Definition, definitions, refs)
            .Where(edge => edge.isInterface == defaults).ToArray();
        foreach (var fact in facts)
        {
            if (fact == null || fact.ImplementationOwner is not TypeNode.Fqn implementationOwner
                || fact.ImplementationMember is not string member || !fact.Header.Params.HasValue
                || fact.Header.Ret.OrDefault() is not TypeNode factReturn) continue;
            var parameters = fact.Header.Params.Value;
            var factParams = parameters.Select(parameter => parameter.Type.OrDefault()).ToArray();
            if (factParams.Any(type => type == null)) continue;
            var arity = fact.ImplementationArity;
            var accessor = fact.Header.PropertyAccessor.OrDefault();
            foreach (var (spec, _) in bases.Where(edge => edge.spec.Name == implementationOwner.Name)
                .GroupBy(edge => SupertypeGraph.TypeKey(edge.spec)).Select(group => group.First()))
            {
                var args = spec.Args ?? [];
                if (definitions.TryGetValue(spec.Name, out var sourceDefinition))
                {
                    var owner = views.Owner(sourceDefinition);
                    var sources = owner.Methods.Where(source => !source.Static && !source.PhysicalBridge
                        && (accessor == null ? !source.Property(out _, out _)
                            && (source.KotlinSourceMember ?? source.Name) == member
                            : source.Property(out var name, out var kind) && name == member && kind == accessor)
                        && source.Arity == arity
                        && SameSourceMethodTypeParameterShape(SemanticParameters(source),
                            SemanticParameters(fact.ImplementationParameters, fact.ImplementationBounds?.Bounds, null), args, args)
                        && (fact.Header.Overrides.OrEmpty().Any(edge => edge.DeclarationId.OrDefault() is string id
                                && id == SourceDeclarationId(source.DeclarationId))
                            || SignatureMatches(source, factParams, factReturn, args, refs, isValue))).ToArray();
                    if (sources.Length == 0 || sources.Length > 1 && !SameSelectedSuspendDeclaration(sources)) continue;
                    foreach (var source in sources)
                    {
                        var header = SelectedMethodHeader.FromSource(source.Source);
                        var signature = source.Parameters.Select(parameter => parameter.Signature == null ? parameter.Type
                            : FunctionSignatureIdentity.SignatureType(parameter.Type, parameter.Signature)).ToArray();
                        var queryBounds = !collectionQueries ? null : parameters.Select((_, index) =>
                            !KotlinCollectionSlotSynthesis.IsErasedQueryParameter(fact.Header.Overrides.OrEmpty(), index)
                                || !KotlinCollectionSlotSynthesis.UsesErasedQueryCarrier(source.Parameters[index].Type) ? null
                                : KotlinCollectionSlotSynthesis.GenericQueryBound(header.Params.Value[index].KotlinType.OrDefault()
                                    ?? header.Params.Value[index].NullableGeneric.OrDefault(), HeaderMethodBounds(header), OwnerBounds(owner),
                                    type => type is TypeNode.Fqn named && (definitions.TryGetValue(named.Name, out var definition)
                                        ? definition.Kind == "interface" : refs?.IsInterfaceType(named) == true), refs?.NullableTypeFrames, isValue)).ToArray();
                        var physical = source.Parameters.Select(parameter => SupertypeGraph.SubstOwnerTvs(parameter.Type, args)).ToArray();
                        var openRet = source.Return;
                        var logical = suspendValues && source.Suspend
                            ? SupertypeGraph.SubstOwnerTvs(header.SuspendResult.OrDefault()
                                ?? throw new InvalidOperationException("inherited suspend source has no logical result"), args) : null;
                        Publish(header, physical, SupertypeGraph.SubstOwnerTvs(openRet, args), !IsVoid(openRet) || source.ReturnsValue,
                            source.ExplicitClrName ?? source.Name, source.TypeParameters, source.Frame, null,
                            signature, source.DeclarationId, queryBounds, logical, spec, owner);
                    }
                }
                else
                {
                    if (refs == null) continue;
                    ReferencedMethodDeclaration source;
                    TypeNode logical = null;
                    if (suspendValues && fact.Header.Mods.OrDefault()?.Suspend.OrDefault() == true)
                    {
                        if (!refs.TrySelectedOverrideDeclarationByShape(spec.Name, member, accessor, arity, factParams, args,
                            MethodTypeParameterShapeKey(fact.ImplementationParameters, args, refs.PhysicalConstraintType),
                            true, out source, false, fact.DeclarationId)) continue;
                        logical = SupertypeGraph.SubstOwnerTvs(source.Return, args);
                    }
                    else
                    {
                        var declarationParams = parameters.Select((parameter, index) => parameter.KotlinType.OrDefault() ?? factParams[index]).ToArray();
                        var ownArgs = ClassOwnArgs(cls.Definition);
                        var selectedArgs = cls.Frame?.OrdinaryArguments(ownArgs) ?? ownArgs;
                        var semanticParameters = SemanticParameters(fact.Header.TypeParams.OrDefault(), fact.Header.MethodTypeParameterBoundsPre.OrDefault()?.Bounds, null);
                        var found = accessor != null
                            ? refs.TrySelectedSourceOverrideDeclaration(spec.Name, member, fact.Header.TypeParams.OrEmpty().Length,
                                declarationParams, args, semanticParameters, selectedArgs, false, out source, accessor, fact.DeclarationId)
                            : refs.TrySelectedSourceMethodDeclaration(spec.Name, member, fact.Header.TypeParams.OrEmpty().Length,
                                declarationParams, factReturn, args, semanticParameters, selectedArgs, out source,
                                selectedDeclarationId: fact.DeclarationId);
                        if (!found) continue;
                    }
                    Publish(null, source.PhysicalParameters.Select(type => SupertypeGraph.SubstOwnerTvs(type, args)).ToArray(),
                        SupertypeGraph.SubstOwnerTvs(logical != null ? source.Return : source.PhysicalReturn, args),
                        logical != null ? !IsVoid(source.Return) : source.ReturnsValue, source.PhysicalMember,
                        source.TypeParams, source.NullableFrame,
                        source.ParameterNullableFlags, source.SignatureParameters, source.DeclarationId,
                        !collectionQueries ? null : Enumerable.Range(0, source.PhysicalParameters.Length).Select(index =>
                            KotlinCollectionSlotSynthesis.IsErasedQueryParameter(fact.Header.Overrides.OrEmpty(), index)
                                ? source.QueryParameterBound?.Invoke(index) : null).ToArray(), logical,
                        new TypeNode.Fqn(refs.ExactReflectedOwner(spec.Name, args.Length), spec.Args), null,
                        collectionQueries ? SupertypeGraph.SubstOwnerTvs(source.Return, args) : null);
                }

                void Publish(SelectedMethodHeader selectedSource, TypeNode[] physicalParams, TypeNode physicalRet,
                    bool returnsValue, string physicalMember, BirTypeParameter[] typeParams, NullableRepresentationFrame frame,
                    byte[][] nullableFlags, TypeNode[] signature, string declarationId, TypeNode[] queryBounds,
                    TypeNode logicalResult, TypeNode.Fqn callOwner, OverrideOwner sourceOwner, TypeNode selectedKotlinReturn = null)
                {
                    var projected = selectedSource?.SuspendSourceParameters.HasValue == true;
                    var header = new SelectedMethodHeader {
                        Source = selectedSource?.Source,
                        Name = projected ? selectedSource.Name : fact.Header.Name,
                        Params = (projected ? selectedSource.Params.Value : parameters).Select(parameter => parameter.Copy()).ToArray(),
                        TypeParams = SubstituteParameters(typeParams, args), Ret = physicalRet, ExplicitClrName = physicalMember,
                        Mods = logicalResult != null ? fact.Header.Mods : default,
                        Overrides = fact.Header.Overrides, PropertyName = fact.Header.PropertyName,
                        PropertyAccessor = fact.Header.PropertyAccessor, PropertyAssociation = fact.Header.PropertyAssociation,
                        NullableGenericRet = fact.Header.NullableGenericRet,
                        RetKotlinType = selectedKotlinReturn != null ? OptionalField<TypeNode>.Of(selectedKotlinReturn) : fact.Header.RetKotlinType,
                        MethodTypeParameterBoundsPre = fact.Header.MethodTypeParameterBoundsPre,
                        KotlinSourceMember = projected ? fact.Header.Name : default,
                    };
                    if (frame is { RequiresMetadata: true }) header.NullableRepresentationFrame = frame;
                    if (fact.ImplementationBounds is { } bounds)
                    {
                        var ownerFrame = sourceOwner != null ? sourceOwner.Frame : refs?.NullableTypeFrames.GetValueOrDefault(spec.Name);
                        var ownerArgs = ownerFrame?.OrdinaryArguments(args) ?? args;
                        header.MethodTypeParameterBoundsPre = bounds.RewriteTypes(type => SupertypeGraph.SubstOwnerTvs(type, ownerArgs));
                    }
                    if (logicalResult != null) header.SuspendResult = logicalResult;
                    for (var index = 0; index < physicalParams.Length; index++)
                    {
                        header.Params.Value[index].Type = physicalParams[index];
                        if (nullableFlags?[index] is { } flags) header.Params.Value[index].NullableFlags = flags.Select(flag => (int)flag).ToArray();
                    }
                    if (returnsValue && IsUnitValueSlot(physicalRet)) header.DotktValueReturn = true;
                    result.Add(new(callOwner, header, fact.Source, fact.Header.Overrides.OrEmpty(), defaults,
                        signature, declarationId, queryBounds?.Select(type => type == null ? null : SupertypeGraph.SubstOwnerTvs(type, args)).ToArray(),
                        physicalParams.Select((type, index) => index < factParams.Length ? factParams[index] : type).ToArray()));
                }
            }
        }
        return result;
    }

    static ErasedElementValues.BoundDeclaration HeaderMethodBounds(SelectedMethodHeader method) => new(
        method.NullableRepresentationFrame.OrDefault(), method.TypeParams.OrEmpty().Select(parameter => parameter.Constraints.OrEmpty()).ToArray(),
        method.MethodTypeParameterBoundsPre.OrDefault()?.Bounds);
    static ErasedElementValues.BoundDeclaration OwnerBounds(OverrideOwner owner) => new(owner.Frame,
        TypeParameterFrame.Declarations(owner.Source).Select(parameter => parameter.Constraints.OrEmpty()).ToArray(),
        NullableRepresentationViews.TypeFacts((BirGraphValue)owner.Source)?.Bounds);

    static bool SameSelectedSuspendDeclaration(IReadOnlyList<OverrideMethod> projections)
    {
        var identity = projections.Count == 0 ? null : SourceDeclarationId(projections[0].DeclarationId);
        return identity != null && projections.All(projection => SourceDeclarationId(projection.DeclarationId) == identity
            && projection.SuspendSourceParametersPresent && projection.SuspendSourceReturn != null);
    }

}
