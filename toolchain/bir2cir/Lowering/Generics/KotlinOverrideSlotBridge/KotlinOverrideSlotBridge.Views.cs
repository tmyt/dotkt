using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using Def = SupertypeGraph.Def;

static partial class KotlinOverrideSlotBridge
{
    // Pass-private live declaration views; metadata-only inherited candidates use explicit headers.
    sealed class OverrideViews(IReadOnlyDictionary<string, IBirTypeDeclaration> source)
    {
        readonly Dictionary<object, OverrideMethod> methods = new(ReferenceEqualityComparer.Instance);
        internal OverrideMethod Method(BirFunctionDeclaration declaration)
        {
            if (!methods.TryGetValue(declaration, out var value)) methods[declaration] = value = new(declaration);
            return value;
        }
        internal OverrideMethod Method(SelectedMethodHeader declaration)
        {
            if (!methods.TryGetValue(declaration, out var value)) methods[declaration] = value = new(declaration);
            return value;
        }
        internal OverrideOwner Owner(Def definition) => new(definition, source[definition.Name], this);
    }

    sealed class OverrideOwner(Def definition, IBirTypeDeclaration source, OverrideViews views)
    {
        internal IBirTypeDeclaration Source => source;
        internal Def Definition => definition;
        internal NullableRepresentationFrame Frame => NullableRepresentationViews.TypeFacts((BirGraphValue)source)?.NullableFrame;
        internal BirTypeParameter[] DeclaredParameters => source.TypeParams.OrDefault();
        internal IEnumerable<OverrideMethod> Methods => source.Methods.OrEmpty().Select(views.Method);
        internal bool HasMethods => source.Methods.HasValue;
    }

    sealed record OverrideEdge(TypeNode Owner, string Member, string Kind, int? Arity, string DeclarationId, string PhysicalDeclarationId);

    sealed class OverrideParameter
    {
        readonly BirParameter source;
        readonly SelectedParameterHeader header;
        internal OverrideParameter(BirParameter value) => source = value;
        internal OverrideParameter(SelectedParameterHeader value) => header = value;
        internal TypeNode Type => header != null ? header.Type.OrDefault() : source.Type.OrDefault();
        internal TypeNode Signature => header != null ? header.FunctionSignatureType.OrDefault() : source.FunctionSignatureType.OrDefault();
        internal TypeNode NullableSource => header != null ? header.NullableGeneric.OrDefault() : source.NullableGeneric.OrDefault();
        internal void Move(TypeNode slot, ValueTypeOracle isValue, Func<TypeNode.Fqn, TypeNode[]> annotationArguments)
        {
            if (Type is not { } original || original.Equals(slot)) return;
            var preserveSignature = FunctionValueRepresentation.ContainsOrdinaryFunction(original);
            if (header != null)
            {
                if (preserveSignature && !header.FunctionSignatureType.HasValue) header.FunctionSignatureType = original;
                header.Type = slot;
                if (!header.NullableGeneric.HasValue) header.NullableGeneric = original;
                if (!header.NullableFlags.IsPresent && NullableFlags.ComputeValues(original, isValue, annotationArguments: annotationArguments) is { } flags)
                    header.NullableFlags = flags;
            }
            else
            {
                if (preserveSignature && !source.FunctionSignatureType.HasValue) source.FunctionSignatureType = original;
                source.Type = slot;
                if (!source.NullableGeneric.HasValue) source.NullableGeneric = original;
                if (!source.NullableFlags.IsPresent && NullableFlags.ComputeValues(original, isValue, annotationArguments: annotationArguments) is { } flags)
                    source.NullableFlags = flags;
            }
        }
    }

    sealed class OverrideMethod
    {
        internal BirFunctionDeclaration Source { get; }
        internal SelectedMethodHeader Header { get; }
        internal OverrideMethod(BirFunctionDeclaration value) => Source = value;
        internal OverrideMethod(SelectedMethodHeader value) => Header = value;
        internal string Name => Header != null ? Header.Name.OrDefault() : Source.Name.OrDefault();
        internal string DeclarationId => Header != null ? Header.DeclarationId.OrDefault() : Source.DeclarationId.OrDefault();
        internal string KotlinSourceMember => Header != null ? Header.KotlinSourceMember.OrDefault() : Source.KotlinSourceMember.OrDefault();
        internal string ExistentialSourceMember => Header != null ? Header.ExistentialSourceMember.OrDefault() : Source.ExistentialSourceMember.OrDefault();
        internal string ExplicitClrName => Header != null ? Header.ExplicitClrName.OrDefault() : Source.ExplicitClrName.OrDefault();
        internal string Visibility => Header != null ? Header.Vis.OrDefault() : Source.Vis.OrDefault();
        internal bool Static => Header != null ? Header.Static.OrDefault() : Source.Static.OrDefault();
        internal bool Abstract => Header != null ? Header.Abstract.OrDefault() : Source.Abstract.OrDefault();
        internal bool Suspend => Header != null ? Header.Mods.OrDefault()?.Suspend.OrDefault() == true : Source.Mods.OrDefault()?.Suspend.OrDefault() == true;
        internal bool ReturnsValue => Header != null ? Header.DotktValueReturn.OrDefault() : Source.DotktValueReturn.OrDefault();
        internal bool BodyPresent => Header == null && Source.Body.HasValue;
        internal bool ParamsPresent => Header != null ? Header.Params.HasValue : Source.Params.HasValue;
        internal bool PhysicalBridge => Header != null ? Header.PhysicalSlotBridge.OrDefault() : Source.PhysicalSlotBridge.OrDefault();
        internal bool PropertyCarrierPresent => Header == null && Source.KotlinPropertyAccessorCarrier.HasValue;
        internal bool SuspendSourceParametersPresent => Header != null ? Header.SuspendSourceParameters.HasValue : Source.SuspendSourceParams.HasValue;
        internal TypeNode[] SuspendSourceParameters => Header != null ? Header.SuspendSourceParameters.OrDefault() : Source.SuspendSourceParams.OrDefault();
        internal TypeNode SuspendSourceReturn => Header != null ? Header.SuspendSourceReturn.OrDefault() : Source.SuspendSourceRet.OrDefault();
        internal TypeNode Return => Header != null ? Header.Ret.OrDefault() : Source.Ret.OrDefault();
        internal TypeNode NullableReturn => Header != null ? Header.NullableGenericRet.OrDefault() : Source.NullableGenericRet.OrDefault();
        internal BirTypeParameter[] TypeParameters => Header != null ? Header.TypeParams.OrDefault() : Source.TypeParams.OrDefault();
        internal int Arity => TypeParameters?.Length ?? 0;
        internal NullableRepresentationFrame Frame => Header != null ? Header.NullableRepresentationFrame.OrDefault() : Source.NullableRepresentationFrame.OrDefault();
        internal IReadOnlyDictionary<int, TypeNode[]> Bounds => Header != null ? Header.MethodTypeParameterBoundsPre.OrDefault()?.Bounds
            : Source.MethodTypeParameterBoundsPre.OrDefault()?.Bounds;
        internal OverrideParameter[] Parameters => Header != null ? Header.Params.OrEmpty().Select(parameter => new OverrideParameter(parameter)).ToArray()
            : Source.Params.OrEmpty().Select(parameter => new OverrideParameter(parameter)).ToArray();
        internal OverrideEdge[] Overrides => (Header != null ? Header.Overrides : Source.Overrides).OrEmpty()
            .Select(edge => new OverrideEdge(edge.Owner.OrDefault(), edge.Member.OrDefault(), edge.Kind.OrDefault(),
                edge.Arity.HasValue ? edge.Arity.Value : null, edge.DeclarationId.OrDefault(), edge.PhysicalDeclarationId.OrDefault())).ToArray();
        internal bool Property(out string name, out string kind) => Header != null ? Header.Property(out name, out kind)
            : KotlinPropertyAccessors.TryIdentity(Source, out name, out kind);
        internal void MoveReturn(TypeNode slot, ValueTypeOracle isValue, Func<TypeNode.Fqn, TypeNode[]> annotationArguments)
        {
            if (Return is not { } original || original.Equals(slot)) return;
            if (Header != null)
            {
                Header.Ret = slot;
                if (!Header.NullableGenericRet.HasValue) Header.NullableGenericRet = original;
                if (!Header.RetNullableFlags.IsPresent && NullableFlags.ComputeValues(original, isValue, annotationArguments: annotationArguments) is { } flags)
                    Header.RetNullableFlags = flags;
            }
            else
            {
                Source.Ret = slot;
                if (!Source.NullableGenericRet.HasValue) Source.NullableGenericRet = original;
                if (!Source.RetNullableFlags.IsPresent && NullableFlags.ComputeValues(original, isValue, annotationArguments: annotationArguments) is { } flags)
                    Source.RetNullableFlags = flags;
            }
        }
        internal void SetVirtual()
        {
            if (Header == null) Source.Virtual = true;
        }
    }

    internal static BirTypeParameter[] SemanticMethodTypeParameters(BirFunctionDeclaration method)
        => SemanticParameters(new OverrideMethod(method));

    internal static BirTypeParameter[] SemanticMethodTypeParameters(BirInheritedImplementation method)
        => SemanticParameters(method.TypeParams.OrDefault(), method.MethodTypeParameterBoundsPre.OrDefault()?.Bounds, null);

    internal static BirTypeParameter[] SubstituteOwnerTypeParameterConstraints(BirTypeParameter[] parameters, TypeNode[] ownerArguments)
        => SubstituteParameters(parameters, ownerArguments);

    static BirTypeParameter[] SemanticParameters(OverrideMethod method)
        => SemanticParameters(method.TypeParameters, method.Bounds, method.Frame);

    static BirTypeParameter[] SemanticParameters(BirTypeParameter[] declared,
        IReadOnlyDictionary<int, TypeNode[]> bounds, NullableRepresentationFrame frame)
    {
        var parameters = declared?.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray() ?? [];
        foreach (var pair in bounds ?? new Dictionary<int, TypeNode[]>())
            parameters[frame?.SourcePosition(pair.Key) ?? pair.Key].Constraints = pair.Value.Select(BirGraphValue.CopyType).ToArray();
        return parameters;
    }
    static BirTypeParameter[] SourceParameters(OverrideMethod method)
    {
        var parameters = SemanticParameters(method);
        if (method.Frame is { } frame)
            for (var index = 0; index < frame.PhysicalArity; index++)
                if (frame.PhysicalSlot(index).Representation != NullableRepresentationFrame.Role.Ordinary)
                { parameters[index].Constraints = default; parameters[index].SpecialConstraints = default; }
        return parameters;
    }
    static BirTypeParameter[] AuthoredParameters(OverrideMethod method)
    {
        var parameters = SemanticParameters(method);
        return method.Frame is not { } frame ? parameters
            : Enumerable.Range(0, frame.SourceArity).Select(index => parameters[frame.SourcePosition(index)]).ToArray();
    }
    static BirTypeParameter[] SubstituteParameters(BirTypeParameter[] parameters, TypeNode[] ownerArguments)
    {
        if (parameters == null) return null;
        return parameters.Select(parameter => {
            var clone = (BirTypeParameter)parameter.DeepClone();
            if (ownerArguments is { Length: > 0 } && clone.Constraints.HasValue)
                clone.Constraints = clone.Constraints.Value.Select(type => SupertypeGraph.SubstOwnerTvs(type, ownerArguments)).ToArray();
            return clone;
        }).ToArray();
    }
}
