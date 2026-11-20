using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal sealed class SourceRewriteState
    {
        internal CarrierDemandPlan Demand { get; }
        internal IReadOnlySet<BirMethodImpl> SelectedSlotOwners { get; init; }
        internal SourceCarrierDeclarations Declarations { get; }
        internal CarrierProjectionContext Projection { get; }
        internal ReferenceMetadataIndex References { get; }
        internal IReadOnlyDictionary<string, Owner> Owners => Projection.Owners;
        internal IReadOnlyDictionary<string, string> LocalClrAliases => Projection.LocalClrAliases;
        internal IReadOnlySet<string> Carriers { get; }
        internal Func<TypeNode, IBirExpression, IBirExpression> ComponentPredicate { get; }
        readonly CarrierTypeContext types;
        readonly CurrentPhysicalTypeView currentPhysical;
        readonly Dictionary<BirGraphValue, TypeNode> projections = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<BirGraphValue, TypeNode> arrayElements = new(ReferenceEqualityComparer.Instance);
        readonly HashSet<BirArrayGet> projectedReads = new(ReferenceEqualityComparer.Instance);
        readonly HashSet<BirFunctionDeclaration> normalizedMethods = new(ReferenceEqualityComparer.Instance);
        internal Dictionary<BirGraphValue, string> SelectedMethodArgumentSlots { get; } = new(ReferenceEqualityComparer.Instance);

        internal SourceRewriteState(CarrierRealizationContext realization,
            Func<TypeNode, IBirExpression, IBirExpression> componentPredicate,
            CurrentPhysicalTypeView currentPhysical = null, CarrierProjectionContext projection = null)
        {
            Demand = realization.Demand;
            Declarations = realization.Declarations;
            References = realization.References;
            Projection = projection ?? realization.Projection;
            this.currentPhysical = currentPhysical;
            ComponentPredicate = componentPredicate;
            types = currentPhysical == null ? CarrierTypeContext.ForSource(realization.Preparation, realization.References)
                : CarrierTypeContext.ForSource(currentPhysical.Definitions, realization.References);
            Carriers = Owners.Values.Select(owner => owner.ErasedName).ToHashSet(StringComparer.Ordinal);
        }

        internal IBirTypeDeclaration Definition(string name) => currentPhysical != null
            ? currentPhysical.Definitions.GetValueOrDefault(name)
            : Demand.Preparation.Bindings.SourceDefinitions.TryGetValue(name, out var binding) ? (IBirTypeDeclaration)binding.Source : null;
        internal IReadOnlyList<TypeNode> ProjectArguments(TypeNode.Fqn source, string target)
            => ProjectConstructedArguments(source, target, types);
        internal bool ClrVariance(TypeNode.Fqn target, IReadOnlyList<TypeNode> arguments, bool physical = false)
            => CanUseClrVariance(target, arguments, types, physical);
        internal bool Assignable(TypeNode source, TypeNode target, bool physical = false, bool allowBoxing = false)
            => IsClrAssignable(source, target, types, physical, allowBoxing);
        internal bool TrySpreadOwner(BirSpreadConcat spread, out TypeNode.Fqn owner)
            => TrySourceSpreadOwner(spread, types, out owner);
        internal bool TryInitializerOwner(BirNewArrayInit initializer, out TypeNode.Fqn owner)
            => TrySourceInitializerOwner(initializer, Demand.Preparation, types, out owner);
        internal bool TryArrayOwner(BirNewArray array, out TypeNode.Fqn owner)
            => TryVarianceProjectedArrayOwner(array.Elem.OrDefault(), array.Elems.OrEmpty().Select(value =>
                (value is BirConstant constant && constant.Value.OrDefault() is null or BirLiteral.NullLiteral,
                    SourceCarrierExpressionType(value as BirGraphValue))).ToArray(), types, out owner);
        internal TypeNode ArrayProjection(BirGraphValue node) => node != null ? arrayElements.GetValueOrDefault(node) : null;
        internal void ArrayProjection(BirGraphValue node, TypeNode type) => arrayElements[node] = type;
        internal void ProjectionFact(BirGraphValue node, TypeNode type) => projections[node] = type;
        internal bool ProjectedRead(BirArrayGet node) => projectedReads.Contains(node);
        internal void MarkProjectedRead(BirArrayGet node) => projectedReads.Add(node);
        internal void MarkNormalized(BirFunctionDeclaration node) => normalizedMethods.Add(node);
        internal bool TakeNormalized(BirFunctionDeclaration node) => normalizedMethods.Remove(node);
        internal void ClearProjectionFacts() { projections.Clear(); arrayElements.Clear(); projectedReads.Clear(); }

        internal TypeNode.Fqn ProjectedType(BirGraphValue expression)
        {
            if (expression == null) return null;
            if (StripProjectionShell(projections.GetValueOrDefault(expression)) is TypeNode.Fqn direct) return direct;
            if (expression is BirValueBlock block) return ProjectedType(block.Result.OrDefault() as BirGraphValue);
            if (expression is BirConditional conditional)
            {
                var left = ProjectedType(conditional.Then.OrDefault() as BirGraphValue);
                var right = ProjectedType(conditional.Else.OrDefault() as BirGraphValue);
                if (left != null && (conditional.Else.OrDefault() is BirThrowExpression || left == right)) return left;
                if (right != null && conditional.Then.OrDefault() is BirThrowExpression) return right;
            }
            return null;
        }

        internal BirGraphValue AlignExistential(IBirExpression expression, TypeNode physical, TypeNode semantic)
        {
            SetSourceCallResult((BirGraphValue)expression, physical);
            var result = new BirCast { Type = BirGraphValue.CopyType(physical),
                E = OptionalField<IBirExpression>.Of(expression), _exactBridgeCast = true };
            ProjectionFact(result, semantic);
            return result;
        }
    }

    internal static OptionalField<IBirStatement[]> SourceCallableBody(BirGraphValue declaration) => declaration switch {
        BirFunctionDeclaration value => value.Body, BirConstructor value => value.Body,
        BirNewSuspendLambda value => value.Body, BirInlineLambda value => value.Body,
        BirClosureDefinition value => value.Body, _ => default,
    };

    internal static bool SourceCallableHasParameters(BirGraphValue declaration) => declaration switch {
        BirFunctionDeclaration value => value.Params.HasValue, BirConstructor value => value.Params.HasValue,
        BirNewSuspendLambda value => value.Params.HasValue, BirInlineLambda value => value.Params.HasValue,
        BirClosureDefinition value => value.Params.HasValue, _ => false,
    };

    internal static bool SourceNativeSignature(BirGraphValue declaration, SourceRewriteState state)
        => SourceTypeSlotFacts(declaration, state.Projection, declarationDescriptor: false, state.LocalClrAliases).NativeMethodSignature;

    internal static void PreserveNormalizedSourceReturn(BirFunctionDeclaration declaration,
        TypeNode original, TypeNode physical, SourceRewriteState state)
    {
        if (!declaration.RetKotlinType.HasValue)
            declaration.RetKotlinType = BirGraphValue.CopyType(declaration.NullableGenericRet.OrDefault() ?? original);
        declaration.Ret = BirGraphValue.CopyType(physical);
        state.MarkNormalized(declaration);
    }

    internal readonly record struct TypeSlotFacts
    {
        internal string Kind { get; init; }
        internal bool PhysicalSignature { get; init; }
        internal bool HasParameters { get; init; }
        internal bool NativeMethodSignature { get; init; }
        internal bool DeclarationDescriptor { get; init; }
        internal bool NativeDescriptorOwner { get; init; }
        internal bool ExactBridgeOwnerCall { get; init; }
        internal bool ClrBound { get; init; }
        internal bool ArrayFactory { get; init; }
        internal bool Witness { get; init; }
        internal bool NativeConstruction { get; init; }
        internal bool AttributeClass { get; init; }
        internal bool PhysicalNativePropertyBridge { get; init; }
        internal bool NativeLocation { get; init; }
        internal bool FieldAddress { get; init; }
        internal bool ValueTemporaryConstruction { get; init; }
        internal bool LexicalReceiver { get; init; }
        internal bool ExactOuter { get; init; }
        internal bool TypeDefinition { get; init; }
    }

    // These scalar policies are also the authority for genuine late DOM type
    // rewriting. DOM and owning readers supply facts, not alternate decisions.
    internal static bool BoundTypeSlot(TypeSlotFacts facts, string field) =>
        facts.PhysicalSignature && (facts.Kind == "callInstance" && field is "ownerType" or "sig" or "calleeParams" or "ret" or "dynRet" or "sty"
            || facts.Kind == "cast" && field == "type" || facts.Kind is "nullableWrap" or "nullableValue" && field == "elem")
        || facts.Kind == null && facts.HasParameters && field is "params" or "ret" && facts.NativeMethodSignature
        || facts.DeclarationDescriptor && field is "params" or "ret" && facts.NativeDescriptorOwner
        || facts.Kind is "callStatic" or "newDelegate" && field is "calleeOwner" or "ownerType" or "owner"
        || facts.Kind == "callInstance" && field == "ownerType" && facts.ExactBridgeOwnerCall
        || field is "resolvedMemberParams" or ClrMemberResolution.ResolvedMemberReturnKey or "memberRef"
        || field == "argTypes" && facts.ClrBound
        || field == "ret" && facts.ArrayFactory
        || field == "type" && facts.Witness
        || field == "memberSignature" && facts.Kind is "new" or "newClr" && facts.NativeConstruction;

    internal static bool NativeArrayTypeSlot(TypeSlotFacts facts, string field) =>
        field is "attrs" or "retAttrs" or "assemblyAttrs"
        || field is "ctors" or "fields" or "properties" or "methods" && facts.AttributeClass
        || field == "typeArgs" && facts.ClrBound
        || field == "elem" && facts.Kind == "newList"
        || field is "params" or "ret" && (facts.PhysicalNativePropertyBridge
            || facts.DeclarationDescriptor && facts.NativeDescriptorOwner);

    internal static bool ConstructionHeadSlot(TypeSlotFacts facts, string field) =>
        field is "type" or "ownerType" && facts.ClrBound
        || field == "ownerType" && facts.FieldAddress
        || field == "iface" && facts.Kind == "constrainedCall"
        || field == "type" && (facts.Kind is "new" or "newClr" || facts.ValueTemporaryConstruction);

    internal static bool ArrayComponentTypeSlot(TypeSlotFacts facts, string field) => field == "elem"
        && facts.Kind is "newArray" or "newArrayInit" or "newArraySized" or "spreadConcat" or "arrayGet" or "arraySet" or "forArray";

    internal static bool NativeLocationTypeSlot(TypeSlotFacts facts, string field) =>
        facts.NativeLocation && field is "type" or "elem" or "memberType"
        || facts.NativeLocation && field is "ret" or "sty" && facts.Kind is "field" or "staticField" or "lateinitGet"
        || facts.Kind is "byrefLoad" or "byrefStore" && field == "elem";

    internal static bool ExactLexicalTypeSlot(TypeSlotFacts facts, string field) =>
        (field == "ownerType" || facts.Kind == "newBoundDelegate" && field == "calleeOwner") && facts.LexicalReceiver
        || field is "type" or "sty" or "memberType" or "ret" or "ownerType" && (facts.ExactOuter || facts.Kind == "this");

    static bool SourceExactOuter(BirGraphValue node) => node switch {
        BirFieldGet value => value.Outer.OrDefault(), BirFieldSetExpression value => value.Outer.OrDefault(),
        BirSetField value => value.Outer.OrDefault(), BirLocal value => value.Outer.OrDefault(),
        BirThis value => value.Outer.OrDefault(), BirVariable value => value.Outer.OrDefault(),
        BirFieldDeclaration value => value.Outer.OrDefault(), BirParameter value => value.Outer.OrDefault(),
        BirLambdaParameter value => value.Outer.OrDefault(), BirCaptureDescriptor value => value.Outer.OrDefault(),
        BirInlineCapture value => value.Outer.OrDefault(), _ => false,
    };

    static TypeSlotFacts SourceTypeSlotFacts(BirGraphValue node, CarrierProjectionContext context,
        bool declarationDescriptor, IReadOnlyDictionary<string, string> currentAliases = null)
    {
        var kind = (node as BirGraphNode)?.Tag;
        var refs = context.References;
        var aliases = currentAliases ?? context.Preparation.LocalClrAliases;
        bool NativeOwner(TypeNode owner) => owner is TypeNode.Fqn named
            && context.Values?.IsBindingAlias(named.Name) != true && !aliases.ContainsKey(named.Name)
            && IsNativeArrayOwner(named, refs, aliases);
        var attrs = new List<BirAttribute>();
        BirGraphValue receiver = null;
        node.VisitOwnChildSlots((field, _, slot) => {
            if (field == "attrs" && slot.Value is BirAttribute attribute) attrs.Add(attribute);
            if (field == "recv") receiver = slot.Value;
        });
        var descriptorOwner = node switch {
            BirMethodImpl value => value.Owner.OrDefault(), BirOverride value => value.Owner.OrDefault(),
            BirInheritedImplementation value => value.Owner.OrDefault(), _ => null,
        };
        var physicalSignature = node switch {
            BirCallInstance value => value._physicalDeclarationSignature.OrDefault(),
            BirFunctionDeclaration value => value._physicalDeclarationSignature.OrDefault(),
            BirCast value => value._physicalDeclarationSignature.OrDefault(),
            BirNullableWrap value => value._physicalDeclarationSignature.OrDefault(),
            BirNullableValue value => value._physicalDeclarationSignature.OrDefault(), _ => false,
        };
        var parameters = node switch {
            BirFunctionDeclaration value => value.Params.HasValue, BirConstructor value => value.Params.HasValue,
            BirInlineLambda value => value.Params.HasValue, BirClosureDefinition value => value.Params.HasValue,
            BirNewSuspendLambda value => value.Params.HasValue, BirMethodImpl value => value.Params.HasValue,
            IBirTypeDeclaration value => value.Params.HasValue, _ => false,
        };
        var function = node as BirFunctionDeclaration;
        var nativeMethod = context.Values?.HasNativeMethodSignature(physicalSignature,
            attrs.Any(attribute => attribute.Attr.OrDefault() is TypeNode.Fqn { Name: "kotlin.clr.ClrIntrinsic" }),
            function?.Overrides.OrEmpty().Select(edge => edge.Owner.OrDefault()) ?? Enumerable.Empty<TypeNode>()) == true;
        var reified = node switch { BirCast value => value.ReifiedTypeOperand.OrDefault(),
            BirInstanceOrNull value => value.ReifiedTypeOperand.OrDefault(), _ => false };
        return new() {
            Kind = kind, PhysicalSignature = physicalSignature, HasParameters = parameters, NativeMethodSignature = nativeMethod,
            DeclarationDescriptor = declarationDescriptor, NativeDescriptorOwner = NativeOwner(descriptorOwner),
            ExactBridgeOwnerCall = node is BirCallInstance call && call._exactExistentialBridgeOwnerCall.OrDefault(),
            ClrBound = ClrBoundNode.IsAny(kind), ArrayFactory = attrs.Any(attribute => attribute.Attr.OrDefault() is TypeNode.Fqn { Name: "kotlin.clr.ClrArrayFactory" }),
            Witness = KotlinTypeWitness.NeedsWitness(kind, reified),
            NativeConstruction = node switch { BirNew value => value.Type.OrDefault() is TypeNode.Fqn named && IsNativeArrayOwner(named, refs, aliases),
                BirNewClr value => value.Type.OrDefault() is TypeNode.Fqn named && IsNativeArrayOwner(named, refs, aliases), _ => false },
            AttributeClass = node is IBirTypeDeclaration type && (type.Mods.OrDefault()?.Annotation.OrDefault() == true
                || type.Base.OrDefault() is TypeNode.Fqn baseType && baseType.Name.EndsWith("System.Attribute", StringComparison.Ordinal)),
            PhysicalNativePropertyBridge = function?.PhysicalSlotBridge.OrDefault() == true
                && function.ClrInterfaceImpls.OrEmpty().Concat(function.ClrBaseImpls.OrEmpty()).Any(edge => NativeOwner(edge.Owner.OrDefault())),
            NativeLocation = context.Values?.IsNativeField(node) == true,
            FieldAddress = node switch { BirFieldGet value => value._nativeFieldAddress.OrDefault(),
                BirStaticFieldGet value => value._nativeFieldAddress.OrDefault(), _ => false },
            ValueTemporaryConstruction = node is BirVariable temporary && temporary._callEvalTemporary.OrDefault()
                && temporary.Init.OrDefault() is BirNew construction && construction.Type.OrDefault() == temporary.Type.OrDefault(),
            LexicalReceiver = receiver is BirThis || receiver != null && SourceExactOuter(receiver),
            ExactOuter = SourceExactOuter(node), TypeDefinition = node is IBirTypeDeclaration definition && IsTypeDefinition(definition.Kind.OrDefault()),
        };
    }

    // The constraint predicates below are the same scalar predicates consumed
    // by the physical continuation. Only their owning declaration reader differs.
    internal static HashSet<int> DirectOwnerDependentInnerParametersSource(Owner inner, int capturedCount,
        CarrierRealizationContext context) => DirectOwnerDependentInnerParameters(
            context.Definition(inner).TypeParams.OrEmpty().Select(parameter => parameter.Constraints.OrEmpty()).ToArray(), capturedCount);

    internal static HashSet<int> OwnerDependentInnerParametersSource(Owner inner, int capturedCount,
        CarrierRealizationContext context) => OwnerDependentInnerParameters(
            context.Definition(inner).TypeParams.OrEmpty().Select(parameter => parameter.Constraints.OrEmpty()).ToArray(), capturedCount);

    static HashSet<int> DirectOwnerDependentInnerParameters(IReadOnlyList<TypeNode[]> parameters, int capturedCount)
    {
        var result = new HashSet<int>();
        for (var index = capturedCount; index < parameters.Count; index++)
            if (parameters[index].Any(bound => ContainsCapturedInnerParameter(bound, capturedCount))) result.Add(index);
        return result;
    }

    static HashSet<int> OwnerDependentInnerParameters(IReadOnlyList<TypeNode[]> parameters, int capturedCount)
    {
        var result = new HashSet<int>();
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var index = capturedCount; index < parameters.Count; index++)
                if (!result.Contains(index) && parameters[index].Any(bound => ContainsInnerDependency(bound, capturedCount, result)))
                    changed |= result.Add(index);
        }
        return result;
    }
}
