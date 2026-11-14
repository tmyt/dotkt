using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static CarrierDemandPlan PrepareCarrierDemand(CurrentDeclarationPreparation preparation,
        NativeProofPlan nativeProofs, ReferenceMetadataIndex refs, GenericRepresentationPolicy values)
        => new(preparation, nativeProofs, refs, values);

    // Names, eligibility and demand belong to this input clock. The continuation
    // takes these SAME owners after their declaration bindings cross the frontier;
    // it must not allocate names or discover demand a second time from physical IR.
    internal sealed class CarrierDemandPlan
    {
        readonly Dictionary<string, Owner> owners;
        readonly HashSet<string> usedNames;
        internal CurrentDeclarationPreparation Preparation { get; }
        internal NativeProofPlan NativeProofs { get; }
        internal GenericRepresentationPolicy Values { get; }
        internal IReadOnlyDictionary<string, Owner> Owners => owners;
        internal HashSet<string> ReservedNames => usedNames;
        internal IReadOnlyDictionary<string, (TypeNode.Fqn spec, bool isInterface)[]> BaseContracts { get; }

        internal CarrierDemandPlan(CurrentDeclarationPreparation preparation, NativeProofPlan nativeProofs,
            ReferenceMetadataIndex refs, GenericRepresentationPolicy values)
        {
            Preparation = preparation;
            NativeProofs = nativeProofs;
            Values = values;
            owners = preparation.GenericOwners.ToDictionary(row => row.Name, row => new Owner(row), StringComparer.Ordinal);
            usedNames = AllocateCarrierNameState(owners, preparation.Bindings.SourceDefinitions.Keys);
            BaseContracts = owners.Values.ToDictionary(owner => owner.Name, owner => SupertypeGraph.Reachable(
                new SupertypeGraph.Def { Base = owner.BaseType }, preparation.SourceSupertypes, refs).ToArray(), StringComparer.Ordinal);
            var context = CarrierTypeContext.ForSource(preparation, refs);
            var ordinary = OrdinarySourceParameters(preparation, refs);
            foreach (var root in preparation.Bindings.SourceRoots) MarkSource(root.Source);
            MarkNeededClosure(owners, BaseContracts);

            void MarkSource(BirGraphValue node)
            {
                if (node is IBirExecutablePayload) return;
                if (node is BirParameter parameter && ordinary.Contains(parameter)
                    && StripSourceNullability(parameter.Type.OrDefault()) is TypeNode.Fqn { Args: { Length: > 0 } } type
                    && owners.TryGetValue(type.Name, out var owner)
                    && owner.Kind is "class" or "interface" && !refs.IsValueType(type)) owner.Needed = true;
                if (node is BirNewArray array && array.Elems.HasValue
                    && TryVarianceProjectedArrayOwner(array.Elem.OrDefault(), array.Elems.Value.Select(element =>
                        (element is BirConstant constant && constant.Value.OrDefault() is null or BirLiteral.NullLiteral,
                            SourceCarrierExpressionType(element as BirGraphValue))).ToArray(), context, out var arrayOwner)
                    && owners.TryGetValue(arrayOwner.Name, out var projected)) projected.Needed = true;
                if (node is BirSpreadConcat spread && TrySourceSpreadOwner(spread, context, out var spreadOwner)
                    && owners.TryGetValue(spreadOwner.Name, out var projectedSpread)) projectedSpread.Needed = true;
                if (node is BirNewArrayInit initializer
                    && TrySourceInitializerOwner(initializer, preparation, context, out var initializerOwner)
                    && owners.TryGetValue(initializerOwner.Name, out var projectedInitializer)) projectedInitializer.Needed = true;

                var runtime = node is BirIsInstance or BirInstanceOrNull or BirCast
                    || node is BirClassReference classifier && classifier.KotlinClassifierRef.OrDefault();
                node.VisitOwnTypeFields((field, value) => MarkNeededType(value, owners,
                    runtime && field == "type" || field is "constraints" or ErasedInnerConstraintKey));
                node.VisitChildren(MarkSource);
            }
        }

        // Called only after BindProjected. Source-backed access cannot guess a
        // missing continuation; each getter below requires its observed address.
        internal Dictionary<string, Owner> BindOwners()
        {
            foreach (var owner in owners.Values) { _ = owner.Def; _ = owner.Root; }
            _usedCarrierNames = usedNames;
            return owners;
        }
    }

    static HashSet<BirParameter> OrdinarySourceParameters(CurrentDeclarationPreparation preparation, ReferenceMetadataIndex refs)
    {
        var result = new HashSet<BirParameter>(ReferenceEqualityComparer.Instance);
        bool HasAlias(IEnumerable<BirAttribute> attrs) => attrs.Any(attribute =>
            attribute.Attr.OrDefault() is TypeNode.Fqn { Name: "kotlin.clr.ClrTypeAlias" });
        void Methods(IEnumerable<BirFunctionDeclaration> methods, bool generatedOwner, bool aliasOwner)
        {
            foreach (var method in methods)
                if (method.DeclarationId.HasValue && !KotlinPropertyAccessors.TryIdentity(method, out _, out _)
                    && OrdinaryKotlinParameterPolicy(method.Generated.OrDefault(), generatedOwner, aliasOwner,
                        method.Attrs.OrEmpty().Any(attribute =>
                            attribute.Attr.OrDefault() is TypeNode.Fqn { Name: "kotlin.clr.ClrIntrinsic" }),
                        method.Overrides.OrEmpty().Select(edge => (edge.Owner.OrDefault() as TypeNode.Fqn)?.Name),
                        name => preparation.Bindings.SourceDefinitions.TryGetValue(name, out var binding)
                            ? HasAlias(((IBirTypeDeclaration)binding.Source).Attrs.OrEmpty()) : null, refs))
                    foreach (var parameter in method.Params.OrEmpty()) result.Add(parameter);
        }
        foreach (var rootBinding in preparation.Bindings.SourceRoots)
        {
            var root = (BirFileDeclaration)rootBinding.Source;
            Methods(root.Methods.OrEmpty(), false, HasAlias(root.Attrs.OrEmpty()));
            foreach (var type in root.Types.OrEmpty())
                Methods(type.Methods.OrEmpty(), type.Generated.OrDefault(), HasAlias(type.Attrs.OrEmpty()));
        }
        return result;
    }

    // This reader deliberately has ExpressionType's narrow precedence, not a
    // broader source-type inference policy. In particular dynRet and delegate
    // function types are not a fallback for a missing authored expression stamp.
    static TypeNode SourceCarrierExpressionType(BirGraphValue expression)
    {
        if (expression == null) return null;
        TypeNode element = expression switch {
            BirNewArray value => value.Elem.OrDefault(), BirNewArrayInit value => value.Elem.OrDefault(),
            BirNewArraySized value => value.Elem.OrDefault(), BirSpreadConcat value => value.Elem.OrDefault(), _ => null,
        };
        if (element != null) return new TypeNode.Array(element);
        TypeNode sty = null, ret = null, type = null;
        expression.VisitOwnTypeFields((field, value) => {
            if (field == "sty") sty = value;
            else if (field == "ret") ret = value;
            else if (field == "type") type = value;
        });
        return sty ?? ret ?? type ?? (expression is BirValueBlock or BirConditional
            ? BirNodeType.Of((BirGraphNode)expression, value => SourceCarrierExpressionType(value)) : null);
    }

    static bool TrySourceSpreadOwner(BirSpreadConcat spread, CarrierTypeContext context, out TypeNode.Fqn targetOwner)
    {
        targetOwner = null;
        if (StripSourceNullability(spread.Elem.OrDefault()) is not TypeNode.Fqn { Args: { Length: > 0 } } target
            || !spread.Parts.HasValue) return false;
        bool Contains(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return false;
            if (SourceCarrierExpressionType(node) is TypeNode.Array source
                && RequiresExistentialConversion(source.Elem, target, context)) return true;
            if (node is BirNewArray array && array.Elems.OrEmpty().Any(element =>
                RequiresExistentialConversion(SourceCarrierExpressionType(element as BirGraphValue), target, context))) return true;
            var found = false;
            node.VisitChildren(child => { if (!found) found = Contains(child); });
            return found;
        }
        if (!spread.Parts.Value.Any(Contains)) return false;
        targetOwner = target;
        return true;
    }

    static bool TrySourceInitializerOwner(BirNewArrayInit initializer, CurrentDeclarationPreparation preparation,
        CarrierTypeContext context, out TypeNode.Fqn targetOwner)
    {
        targetOwner = null;
        if (StripSourceNullability(initializer.Elem.OrDefault()) is not TypeNode.Fqn { Args: { Length: > 0 } } target)
            return false;
        var selection = initializer.Init.OrDefault() switch {
            BirNewDelegate value => (value.CalleeOwner.OrDefault() as TypeNode.Fqn, value.Method.OrDefault()),
            BirNewClosure value => (value.ClosureType.OrDefault() as TypeNode.Fqn, value.Method.OrDefault()),
            _ => default,
        };
        if (selection.Item1 == null || selection.Item2 == null
            || !preparation.Bindings.SourceLocalMethods.TryGetValue(selection.Item1.Name + "\0" + selection.Item2, out var candidates)
            || UniqueLocalDelegateTarget(candidates) is not { Source: BirFunctionDeclaration { Body.HasValue: true } method }) return false;
        bool Inspect(BirGraphValue node) => node switch {
            BirValueBlock value => Inspect(value.Result.OrDefault() as BirGraphValue),
            BirConditional value => Inspect(value.Then.OrDefault() as BirGraphValue) || Inspect(value.Else.OrDefault() as BirGraphValue),
            BirCast value => Inspect(value.E.OrDefault() as BirGraphValue),
            _ => RequiresExistentialConversion(SourceCarrierExpressionType(node), target, context),
        };
        bool Returns(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return false;
            if (node is BirReturn value) return Inspect(value.Value.OrDefault() as BirGraphValue);
            var found = false;
            node.VisitChildren(child => found |= Returns(child));
            return found;
        }
        if (!method.Body.Value.Any(statement => Returns((BirGraphValue)statement))) return false;
        targetOwner = target;
        return true;
    }

    internal sealed partial class Owner
    {
        internal readonly CurrentDeclarationBindings.DeclarationBinding DefinitionBinding;
        internal readonly CurrentDeclarationBindings.DeclarationBinding RootBinding;
        internal Owner() { } // Explicit already-physical late owner.
        internal Owner(GenericOwner source)
        {
            Name = source.Name; Arity = source.Arity; Needed = source.Needed;
            DefinitionBinding = source.Definition; RootBinding = source.Root;
        }

        T Read<T>(Func<IBirTypeDeclaration, T> source, Func<JsonObject, T> physical) =>
            DefinitionBinding is { IsPhysical: false } binding
                ? source((IBirTypeDeclaration)binding.Source) : physical(Def);

        internal string Kind => Read(type => type.Kind.OrDefault(), definition => Str(definition?["kind"]));
        internal bool HasDefinition => DefinitionBinding != null || physicalDefinition != null;
        internal string SemanticOwner => Read(type => type.SemanticOwner.OrDefault(), definition => Str(definition?["semanticOwner"]));
        internal bool Inner => Read(type => type.Mods.OrDefault()?.Inner.OrDefault() == true, definition => definition != null && IsInner(definition));
        internal int OuterCount => Read(type => (type as BirClassDefinition)?.OuterTypeParamCount.OrDefault() ?? 0,
            definition => Int(definition?["outerTypeParamCount"]) ?? 0);
        internal bool SourceGeneric => Read(type => IsSourceGeneric(type.Name.OrDefault(), type.Generated.OrDefault(),
                type.Attrs.OrEmpty().Any(attribute => attribute.Attr.OrDefault() is TypeNode.Fqn { Name: "kotlin.clr.ClrTypeAlias" }),
                type.TypeParams.OrEmpty().Length), definition => definition != null && IsSourceGeneric(definition));
        internal IReadOnlyList<string> Variances => Read(type => type.TypeParams.HasValue
                ? type.TypeParams.Value.Select(parameter => parameter.Variance.OrDefault()).ToArray() : null,
            definition => (definition?["typeParams"] as JsonArray)?.Select(parameter => Str((parameter as JsonObject)?["variance"])).ToArray());
        internal TypeNode.Fqn BaseType => Read(type => type.Base.OrDefault() as TypeNode.Fqn,
            definition => TypeJson.Read(definition?["base"]) as TypeNode.Fqn);
        internal TypeNode.Fqn[] InterfaceTypes => Read(type => type.Interfaces.OrEmpty().OfType<TypeNode.Fqn>().ToArray(),
            definition => (definition?["interfaces"] as JsonArray)?.Select(TypeJson.Read).OfType<TypeNode.Fqn>().ToArray() ?? Array.Empty<TypeNode.Fqn>());
        internal IEnumerable<(TypeNode Return, TypeNode[] Parameters)> Signatures => Read(
            type => type.Methods.OrEmpty().Select(method => (method.Ret.OrDefault(), method.Params.OrEmpty().Select(parameter => parameter.Type.OrDefault()).ToArray())).ToArray(),
            definition => (definition?["methods"] as JsonArray)?.OfType<JsonObject>().Select(method =>
                (TypeJson.Read(method["ret"]), (method["params"] as JsonArray)?.OfType<JsonObject>().Select(parameter => TypeJson.Read(parameter["type"])).ToArray()
                    ?? Array.Empty<TypeNode>())).ToArray() ?? Array.Empty<(TypeNode, TypeNode[])>());
    }
}
