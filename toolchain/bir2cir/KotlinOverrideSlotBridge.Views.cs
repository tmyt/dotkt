using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using Def = SupertypeGraph.Def;

static partial class KotlinOverrideSlotBridge
{
    // Pass-private live declaration views. They expose named facts, never an
    // executable representation or a string-keyed alternative IR.
    sealed class OverrideViews
    {
        readonly Dictionary<object, OverrideMethod> methods = new(ReferenceEqualityComparer.Instance);
        readonly IReadOnlyDictionary<string, IBirTypeDeclaration> source;
        internal OverrideViews(IReadOnlyDictionary<string, IBirTypeDeclaration> source = null) => this.source = source;
        internal OverrideMethod Method(JsonObject declaration)
        {
            if (!methods.TryGetValue(declaration, out var value)) methods[declaration] = value = new(declaration);
            return value;
        }
        internal OverrideMethod Method(BirFunctionDeclaration declaration)
        {
            if (!methods.TryGetValue(declaration, out var value)) methods[declaration] = value = new(declaration);
            return value;
        }
        internal OverrideOwner Owner(Def definition) => new(definition,
            source != null && source.TryGetValue(definition.Name, out var declaration) ? declaration : null, this);
    }

    sealed class OverrideOwner(Def definition, IBirTypeDeclaration source, OverrideViews views)
    {
        internal Def Definition => definition;
        internal NullableRepresentationFrame Frame => source == null ? KotlinSupertypesRecord.ReadNullableFrame(definition.Node)
            : NullableRepresentationViews.TypeFacts((BirGraphValue)source)?.NullableFrame;
        internal BirTypeParameter[] DeclaredParameters => source == null ? ReadParameters(definition.Node?["typeParams"] as JsonArray) : source.TypeParams.OrDefault();
        internal IEnumerable<OverrideMethod> Methods => source == null
            ? definition.Methods.OfType<JsonObject>().Select(views.Method)
            : source.Methods.OrEmpty().Select(views.Method);
        internal bool HasMethods => source == null ? definition.Node?["methods"] is JsonArray : source.Methods.HasValue;
    }

    sealed record OverrideEdge(TypeNode Owner, string Member, string Kind, int? Arity, string DeclarationId, string PhysicalDeclarationId);

    sealed class OverrideParameter
    {
        readonly JsonObject dom;
        readonly BirParameter source;
        internal OverrideParameter(JsonObject value) => dom = value;
        internal OverrideParameter(BirParameter value) => source = value;
        internal TypeNode Type => source == null ? TypeJson.Read(dom["type"]) : source.Type.OrDefault();
        internal TypeNode Signature => source == null ? TypeJson.Read(dom[FunctionSignatureIdentity.Key]) : source.FunctionSignatureType.OrDefault();
        internal TypeNode NullableSource => source == null ? FrozenType(dom["nullableGeneric"]) : source.NullableGeneric.OrDefault();
        internal void Move(TypeNode slot, ValueTypeOracle isValue, Func<TypeNode.Fqn, TypeNode[]> annotationArguments)
        {
            if (Type is not { } original || original.Equals(slot)) return;
            var preserveSignature = FunctionValueRepresentation.ContainsOrdinaryFunction(original);
            if (source != null)
            {
                if (preserveSignature && !source.FunctionSignatureType.HasValue) source.FunctionSignatureType = original;
                source.Type = slot;
                if (!source.NullableGeneric.HasValue) source.NullableGeneric = original;
                if (!source.NullableFlags.IsPresent && NullableFlags.ComputeValues(original, isValue, annotationArguments: annotationArguments) is { } flags)
                    source.NullableFlags = flags;
            }
            else
            {
                if (preserveSignature) dom[FunctionSignatureIdentity.Key] ??= TypeJson.Write(original);
                dom["type"] = TypeJson.Write(slot);
                dom["nullableGeneric"] ??= TypeNode.ToJson(original);
                if (!dom.ContainsKey("nullableFlags") && NullableFlags.ComputeValues(original, isValue, annotationArguments: annotationArguments) is { } flags)
                    dom["nullableFlags"] = new JsonArray(flags.Select(flag => (JsonNode)JsonValue.Create(flag)).ToArray());
            }
        }
    }

    sealed class OverrideMethod
    {
        internal JsonObject Dom { get; }
        internal BirFunctionDeclaration Source { get; }
        internal OverrideMethod(JsonObject value) => Dom = value;
        internal OverrideMethod(BirFunctionDeclaration value) => Source = value;
        internal string Name => Source == null ? Str(Dom["name"]) : Source.Name.OrDefault();
        internal string DeclarationId => Source == null ? Str(Dom[DeclarationIdentityBinding.Key]) : Source.DeclarationId.OrDefault();
        internal string KotlinSourceMember => Source == null ? Str(Dom[DeclarationRename.SourceMemberKey]) : null;
        internal string ExistentialSourceMember => Source == null ? Str(Dom[FBoundStarProjectionErasure.SourceMemberKey]) : null;
        internal string ExplicitClrName => Source == null ? Str(Dom[DeclarationIdentityBinding.ExplicitNameKey]) : Source.ExplicitClrName.OrDefault();
        internal string Visibility => Source == null ? Str(Dom["vis"]) : Source.Vis.OrDefault();
        internal bool Static => Source == null ? Bool(Dom["static"]) : Source.Static.OrDefault();
        internal bool Abstract => Source == null ? Bool(Dom["abstract"]) : Source.Abstract.OrDefault();
        internal bool Suspend => Source == null ? IsSuspendMethod(Dom) : Source.Mods.OrDefault()?.Suspend.OrDefault() == true;
        internal bool ReturnsValue => Source == null ? Bool(Dom[BirTypeLowering.ValueReturnKey]) : Source.DotktValueReturn.OrDefault();
        internal bool BodyPresent => Source == null ? Dom["body"] is JsonArray : Source.Body.HasValue;
        internal bool ParamsPresent => Source == null ? Dom["params"] is JsonArray : Source.Params.HasValue;
        internal bool PhysicalBridge => Source == null ? KotlinPropertyAccessors.IsPhysicalSlotBridge(Dom) : Source.PhysicalSlotBridge.OrDefault();
        internal bool PropertyCarrierPresent => Source == null ? Dom[KotlinPropertyAccessors.MetadataCarrierKey] != null : Source.KotlinPropertyAccessorCarrier.HasValue;
        internal bool SuspendSourceParametersPresent => Source == null && Dom[KotlinPropertyAccessors.SuspendSourceParamsKey] is JsonArray;
        internal TypeNode Return => Source == null ? TypeJson.Read(Dom["ret"]) : Source.Ret.OrDefault();
        internal TypeNode NullableReturn => Source == null ? FrozenType(Dom["nullableGenericRet"]) : Source.NullableGenericRet.OrDefault();
        internal BirTypeParameter[] TypeParameters => Source == null ? ReadParameters(Dom["typeParams"] as JsonArray) : Source.TypeParams.OrDefault();
        internal int Arity => TypeParameters?.Length ?? 0;
        internal NullableRepresentationFrame Frame => Source == null ? NullableRepresentationTypes.DeclarationMethodFrame(Dom) : Source.NullableRepresentationFrame.OrDefault();
        internal IReadOnlyDictionary<int, TypeNode[]> Bounds => Source == null
            ? Str(Dom[NullableGenericErasure.MethodTypeParameterBoundsPre]) is string encoded ? SourceBoundsSnapshot.ReadFrozenText(encoded).Bounds : null
            : Source.MethodTypeParameterBoundsPre.OrDefault()?.Bounds;
        internal OverrideParameter[] Parameters => Source == null
            ? (Dom["params"] as JsonArray)?.OfType<JsonObject>().Select(parameter => new OverrideParameter(parameter)).ToArray() ?? []
            : Source.Params.OrEmpty().Select(parameter => new OverrideParameter(parameter)).ToArray();
        internal OverrideEdge[] Overrides => Source == null
            ? (Dom["overrides"] as JsonArray)?.OfType<JsonObject>().Select(edge => new OverrideEdge(TypeJson.Read(edge["owner"]), Str(edge["member"]), Str(edge["kind"]),
                edge["arity"] is JsonValue value ? value.GetValue<int>() : null, Str(edge[DeclarationIdentityBinding.Key]), Str(edge["physicalDeclarationId"]))).ToArray() ?? []
            : Source.Overrides.OrEmpty().Select(edge => new OverrideEdge(edge.Owner.OrDefault(), edge.Member.OrDefault(), edge.Kind.OrDefault(),
                edge.Arity.HasValue ? edge.Arity.Value : null, edge.DeclarationId.OrDefault(), null)).ToArray();
        internal bool Property(out string name, out string kind) => Source == null
            ? KotlinPropertyAccessors.TryIdentity(Dom, out name, out kind) : KotlinPropertyAccessors.TryIdentity(Source, out name, out kind);
        internal void MoveReturn(TypeNode slot, ValueTypeOracle isValue, Func<TypeNode.Fqn, TypeNode[]> annotationArguments)
        {
            if (Return is not { } original || original.Equals(slot)) return;
            if (Source != null)
            {
                Source.Ret = slot;
                if (!Source.NullableGenericRet.HasValue) Source.NullableGenericRet = original;
                if (!Source.RetNullableFlags.IsPresent && NullableFlags.ComputeValues(original, isValue, annotationArguments: annotationArguments) is { } flags)
                    Source.RetNullableFlags = flags;
            }
            else
            {
                Dom["ret"] = TypeJson.Write(slot); Dom["nullableGenericRet"] ??= TypeNode.ToJson(original);
                if (!Dom.ContainsKey("retNullableFlags") && NullableFlags.ComputeValues(original, isValue, annotationArguments: annotationArguments) is { } flags)
                    Dom["retNullableFlags"] = new JsonArray(flags.Select(flag => (JsonNode)JsonValue.Create(flag)).ToArray());
            }
        }
        internal void SetVirtual()
        {
            if (Source != null) Source.Virtual = true; else Dom["virtual"] = true;
        }
    }

    static TypeNode FrozenType(JsonNode value) => Str(value) is string encoded ? TypeNode.Parse(encoded) : null;
    static BirTypeParameter[] ReadParameters(JsonArray parameters) => parameters?.OfType<JsonObject>().Select(ReadParameter).ToArray();
    static BirTypeParameter ReadParameter(JsonObject parameter) => new() {
        Name = Scalar<string>(parameter, "name"), Reified = Scalar<bool>(parameter, "reified"), Variance = Scalar<string>(parameter, "variance"),
        Constraints = TypeVector(parameter, "constraints"), ErasedInnerConstraints = TypeVector(parameter, "erasedInnerConstraints"),
        SpecialConstraints = !parameter.ContainsKey("specialConstraints") ? default : parameter["specialConstraints"] is not JsonArray specials
            ? OptionalField<string[]>.Null : OptionalField<string[]>.Of(specials.Select(Str).ToArray()),
    };
    static OptionalField<T> Scalar<T>(JsonObject source, string key) => !source.ContainsKey(key) ? default
        : source[key] is null ? OptionalField<T>.Null : OptionalField<T>.Of(source[key].GetValue<T>());
    static OptionalField<TypeNode[]> TypeVector(JsonObject source, string key) => !source.ContainsKey(key) ? default
        : source[key] is not JsonArray values ? OptionalField<TypeNode[]>.Null : OptionalField<TypeNode[]>.Of(values.Select(TypeJson.Read).ToArray());
    static JsonArray ParametersDom(BirTypeParameter[] parameters) => parameters == null ? null : new JsonArray(parameters.Select(parameter =>
        (JsonNode)parameter.ToDom()).ToArray());

    static BirTypeParameter[] SemanticParameters(OverrideMethod method)
    {
        var parameters = method.TypeParameters?.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray() ?? [];
        foreach (var pair in method.Bounds ?? new Dictionary<int, TypeNode[]>())
            parameters[method.Frame?.SourcePosition(pair.Key) ?? pair.Key].Constraints = pair.Value.Select(BirGraphValue.CopyType).ToArray();
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
