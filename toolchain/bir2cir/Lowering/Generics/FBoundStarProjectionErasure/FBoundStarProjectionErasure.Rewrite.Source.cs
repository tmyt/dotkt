using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using DeclarationBinding = CurrentDeclarationBindings.DeclarationBinding;

static partial class FBoundStarProjectionErasure
{
    sealed record SourceBodyScope
    {
        internal IReadOnlyList<BirTypeParameter> TypeParameters { get; init; }
        internal IReadOnlyList<BirTypeParameter> MethodParameters { get; init; }
        internal IReadOnlySet<int> ExistentialTypes { get; init; }
        internal IReadOnlySet<int> ExistentialMethods { get; init; }
        internal TypeNode.Fqn ThisType { get; init; }
        internal TypeNode.Fqn BaseType { get; init; }
        internal GenericArrayStorageLowering.SourceTypeOperationFrame OwnerFrame { get; init; }
        internal GenericArrayStorageLowering.SourceTypeOperationFrame MethodFrame { get; init; }
        internal bool Bound { get; init; }
        internal bool ArrayShape { get; init; }
        internal bool Descriptor { get; init; }
        internal DeclarationBinding ParameterOwner { get; init; }
        internal int ParameterIndex { get; init; } = -1;
    }

    internal static void RewriteCarrierBodiesSource(IReadOnlyList<BirFileDeclaration> roots,
        CarrierRealizationContext context, IReadOnlyList<SharedSyntheticSynthesis.CellStorage> cells,
        Func<TypeNode, IBirExpression, IBirExpression> componentPredicate)
    {
        var state = new SourceRewriteState(context, componentPredicate);
        foreach (var root in roots)
        {
            var names = new BirTemporaryNames(root);
            RewriteSource(new BirGraphSlot(() => root, replacement => {
                if (!ReferenceEquals(replacement, root)) throw new InvalidOperationException("Carrier rewriting replaced a file declaration");
            }), new(), state, names, typesOnly: false);
        }
        NormalizeSourceFixedPoint(roots, state, cells);
        state.ClearProjectionFacts();
    }

    internal static void ProjectMaterializedTypesSource(IReadOnlyList<BirClassDefinition> types,
        CarrierRealizationContext context)
        => ProjectMaterializedTypesSource(types, new SourceRewriteState(context, (_, _) => null));

    internal static void ProjectMaterializedTypesSource(IReadOnlyList<BirClassDefinition> types,
        CarrierRealizationContext context, CurrentPhysicalTypeView current, CarrierProjectionContext projection)
        => ProjectMaterializedTypesSource(types, new SourceRewriteState(context, (_, _) => null, current, projection));

    static void ProjectMaterializedTypesSource(IReadOnlyList<BirClassDefinition> types, SourceRewriteState state)
    {
        foreach (var type in types)
            RewriteSource(new BirGraphSlot(() => type, replacement => {
                if (!ReferenceEquals(replacement, type)) throw new InvalidOperationException("Late carrier projection replaced a type declaration");
            }), new(), state, null, typesOnly: true);
    }

    static SourceBodyScope SourceChildScope(BirGraphValue node, SourceBodyScope scope, SourceRewriteState state,
        bool typesOnly)
    {
        BirTypeParameter[] Parameters(OptionalField<BirTypeParameter[]> vector)
            => vector.HasValue ? vector.Value.Select(value => (BirTypeParameter)value.DeepClone()).ToArray() : null;
        if (node is IBirTypeDeclaration type && IsTypeDefinition(type.Kind.OrDefault()))
        {
            var parameters = Parameters(type.TypeParams);
            if (!typesOnly) ProjectDependentTypeConstraintsSource(type, state.Projection);
            var facts = NullableRepresentationViews.TypeFacts(node);
            return scope with { TypeParameters = parameters, MethodParameters = null,
                BaseType = type.Base.OrDefault() as TypeNode.Fqn,
                ExistentialTypes = SourceExistentialParameters(parameters, "type"), ExistentialMethods = null,
                ThisType = type.Name.HasValue ? new TypeNode.Fqn(type.Name.Value,
                    Enumerable.Range(0, type.TypeParams.OrEmpty().Length).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray()) : scope.ThisType,
                OwnerFrame = new(parameters, facts?.NullableFrame, facts?.Bounds), MethodFrame = null };
        }
        if (!SourceCallableBody(node).HasValue || !SourceCallableHasParameters(node)) return scope;
        var methodParameters = node switch {
            BirFunctionDeclaration value => Parameters(value.TypeParams), BirNewSuspendLambda value => Parameters(value.TypeParams),
            BirClosureDefinition value => Parameters(value.TypeParams), _ => null,
        };
        // `newSuspendLambda` and inline construction bodies carry parameter
        // snapshots but are not k-less lexical method-frame declarations in the
        // original array-cast reader. Keep that distinction explicit.
        GenericArrayStorageLowering.SourceTypeOperationFrame frame = node switch {
            BirFunctionDeclaration value => new GenericArrayStorageLowering.SourceTypeOperationFrame(methodParameters,
                value.NullableRepresentationFrame.OrDefault(), value.MethodTypeParameterBoundsPre.OrDefault()?.Bounds),
            BirConstructor => new(methodParameters, null, null),
            BirClosureDefinition value => new(methodParameters, null, value.MethodTypeParameterBoundsPre.OrDefault()?.Bounds),
            _ => new(methodParameters, scope.MethodFrame?.Frame, scope.MethodFrame?.SourceBounds),
        };
        return scope with { MethodParameters = methodParameters,
            ExistentialMethods = SourceExistentialParameters(methodParameters, "method"), MethodFrame = frame };
    }

    static IReadOnlySet<int> SourceExistentialParameters(IReadOnlyList<BirTypeParameter> parameters, string scope)
    {
        if (parameters == null || parameters.Count == 0) return null;
        var result = new HashSet<int>();
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var index = 0; index < parameters.Count; index++)
                if (!result.Contains(index) && parameters[index].Constraints.OrEmpty().Any(bound => ContainsExistential(bound,
                    scope == "type" ? result : null, scope == "method" ? result : null))) changed |= result.Add(index);
        }
        return result.Count == 0 ? null : result;
    }

    static void RewriteSource(BirGraphSlot slot, SourceBodyScope scope, SourceRewriteState state,
        BirTemporaryNames names, bool typesOnly)
    {
        var node = slot.Value;
        if (node is null or IBirExecutablePayload or BirMethodWitness) return;
        var projectedRead = node is BirArrayGet read && state.ProjectedRead(read);
        TypeNode.Fqn writableSemantic = null, writablePhysical = null;
        var writable = false;
        if (!typesOnly)
        {
            if (SourceCarrierExpressionType(node) is TypeNode.Array expression
                && TryWritableVariantArrayElement(expression.Elem, state.Projection, out var semantic, out _))
                state.ArrayProjection(node, semantic);
            var array = SourceArrayReceiver(node);
            if (SourceCarrierExpressionType(array as BirGraphValue) is TypeNode.Array receiver
                && TryWritableVariantArrayElement(receiver.Elem, state.Projection, out var receiverSemantic, out _))
                state.ArrayProjection((BirGraphValue)array, receiverSemantic);
            if (SourceArrayElement(node) is { } element)
                writable = TryWritableVariantArrayElement(element, state.Projection, out writableSemantic, out writablePhysical);
        }
        var childScope = SourceChildScope(node, scope, state, typesOnly);
        if (!typesOnly)
        {
            node = LowerProjectedConstructionSource(node, state, scope.ExistentialTypes, scope.ExistentialMethods);
            node = BindStarFieldThroughCanonicalGetterSource(node, state);
            node = BindProjectedFieldStorageSource(node, state);
            BindProjectedConstraintMemberSource(node, scope.TypeParameters, scope.MethodParameters, state);
            var exactDispatch = PrepareConstructedDispatch(node, state);
            node = BindInheritedStarMemberSource(node, state);
            if (exactDispatch != null) node = exactDispatch(node, names);
            BindLocalEventAccessorSource(node, state);
            if (!projectedRead) node = BindProjectedArrayReadSource(node, state);
            node = BindProjectedArrayGenericCallSource(node, state);
            RewriteSourceVariantArrays(node, writable, writableSemantic, writablePhysical, state);
            slot.Replace(node);
        }
        var (runtimeType, runtimeOperand, exact) = SourceRuntimeOperation(node);
        var arrayCastTarget = !typesOnly && !exact && node is (BirCast or BirIsInstance or BirInstanceOrNull) ? runtimeType : null;
        var earlyOperand = !typesOnly && node is (BirCast or BirInstanceOrNull) && runtimeOperand != null;
        if (earlyOperand)
            // The early operand walk enters the ordinary array-shape frame,
            // while retaining the enclosing declaration/bound scope.
            RewriteSource(SourceRuntimeOperandSlot(node), childScope with { ArrayShape = false }, state, names, typesOnly: false);
        if (!typesOnly) RewriteSourceRuntimeClassifier(node, scope.ThisType, state);
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        node.RewriteOwnFieldsInPlace((field, _, type) => {
            // A final MethodImpl describes a closed declaration, not independently erasable values.
            if (node is BirMethodImpl descriptor && state.SelectedSlotOwners?.Contains(descriptor) == true
                && field is "owner" or "params" or "ret")
                return type;
            var index = positions.GetValueOrDefault(field);
            positions[field] = index + 1;
            var facts = SourceTypeSlotFacts(node, state.Projection, scope.Descriptor, state.LocalClrAliases, state.SharedOuters);
            var bound = scope.Bound || BoundTypeSlot(facts, field) || field == "type" && (node is BirIsInstance { ExactDispatchOwner: true }
                || node is BirCast && exact && runtimeOperand is BirGraphValue operand && SourceExactOuter(operand));
            var shape = scope.ArrayShape || NativeArrayTypeSlot(facts, field);
            // An inner still captures its actual enclosing instance. Where
            // that declaration has two implementations, store the common
            // Kotlin carrier rather than asserting one exact CLR TypeDef.
            var sharedOuter = type is TypeNode.Fqn o && state.SharedOuters.Contains(o.Name);
            if (field == "typeArgs") return RewriteSourceMethodArgument(node, type, index, state, bound, shape);
            if (field is "argTypes" or "memberSignature" && SourceArguments(node) is { } arguments)
                return state.Projection.Rewrite(type, bound,
                    preserveConstructedHead: !sharedOuter && (field == "argTypes" && index < arguments.Length && SourceExactOuter((BirGraphValue)arguments[index])
                        || IsSourceInnerOuterSlot(node, index, state)), preserveArrayShape: shape);
            if (field == "delegationSig" && node is BirConstructor constructor)
            {
                var nativeBase = !constructor.ThisArgs.HasValue && constructor.BaseArgs.HasValue
                    && scope.BaseType != null && IsNativeArrayOwner(scope.BaseType, state.References, state.LocalClrAliases);
                return state.Projection.Rewrite(type, bound,
                    preserveConstructedHead: !sharedOuter && constructor.DelegationOuterSlot.HasValue && constructor.DelegationOuterSlot.Value == index
                        || nativeBase && IsExactNativeArrayWitness(type, state.Projection),
                    preserveArrayShape: nativeBase);
            }
            // The component on an allocation is a physical witness at every
            // array depth. A later carrier pass must not turn a concrete row
            // back into the Kotlin element view used for reads and writes.
            if (field == "elem" && node is BirNewArray or BirNewArrayInit or BirNewArraySized or BirSpreadConcat && type is TypeNode.Array)
            {
                TypeNode Row(TypeNode t) => t is TypeNode.Array a ? new TypeNode.Array(Row(a.Elem), a.Rank, a.SzArray)
                    : state.Projection.Rewrite(t, bound, preserveConstructedHead: true, preserveArrayShape: true);
                return Row(type);
            }
            if (field == "constraints" && scope.ParameterOwner != null
                && state.Demand.NativeProofs.IsPhysicalConstraint(scope.ParameterOwner, scope.ParameterIndex, type)) bound = true;
            return state.Projection.Rewrite(type, bound, preserveConstructedHead:
                ConstructionHeadSlot(facts, field)
                || (ArrayComponentTypeSlot(facts, field) || NativeLocationTypeSlot(facts, field)) && PreservesNativeWitnessHead(type, state.Projection)
                || ExactLexicalTypeSlot(facts, field) && !(facts.ExactOuter && sharedOuter && field != "ownerType")
                || facts.TypeDefinition && field is "base" or "interfaces"
                || facts.DeclarationDescriptor && field == "owner", preserveArrayShape: shape,
                sourceOverrideOwner: node is BirOverride);
        }, (field, role, child) => {
            // A use-site override edge selects a source declaration, not an
            // application of its physical frame. The expression's owner is
            // rewritten separately above.
            if (role == BirGenericFrameRole.OverrideDeclaration && node is IBirExpression) return;
            if (!typesOnly && earlyOperand && field == "e") return;
            var facts = SourceTypeSlotFacts(node, state.Projection, scope.Descriptor, state.LocalClrAliases);
            var next = childScope with { Bound = scope.Bound || BoundTypeSlot(facts, field),
                ArrayShape = scope.ArrayShape || NativeArrayTypeSlot(facts, field),
                Descriptor = field is "clrBaseImpls" or "clrInterfaceImpls" or "overrides" or "inheritedImplementation",
                ParameterOwner = null, ParameterIndex = -1 };
            if (field == "typeParams" && child.Value is BirTypeParameter)
            {
                var index = positions.GetValueOrDefault(field);
                positions[field] = index + 1;
                if (state.Demand.Preparation.Bindings.TryGetCapturedSource(node, out var binding))
                    next = next with { ParameterOwner = binding, ParameterIndex = index };
            }
            RewriteSource(child, next, state, names, typesOnly);
        });
        if (typesOnly) return;
        node = BindProjectedArrayReadSource(node, state);
        node = BindProjectedArrayGenericCallSource(node, state);
        if (arrayCastTarget != null)
            node = GenericArrayStorageLowering.LowerTypeOperation(node, arrayCastTarget,
                childScope.OwnerFrame, childScope.MethodFrame, state.References,
                type => type is TypeNode.Fqn { Args: { Length: > 0 } } application
                    && state.Projection.TryExistentialCarrier(application.Name, out var carrier)
                    ? new TypeNode.Fqn(carrier) : state.Projection.Rewrite(type),
                type => type is TypeNode.Fqn named && (state.Definition(named.Name) is { } definition
                    ? definition.Kind.OrDefault() == "interface" : state.References.IsInterfaceType(named)), names, state.ComponentPredicate);
        node = TypedGenericArrayStorageLowering.LowerOperation(node, state.References, names);
        if (node is BirNewArray or BirNewArrayInit or BirNewArraySized or BirSpreadConcat &&
            SourceArrayElement(node) is TypeNode.Tv variable &&
            (variable.Scope == "method" ? childScope.MethodFrame : childScope.OwnerFrame)?.Frame is { } frame &&
            variable.I < frame.PhysicalArity && frame.PhysicalSlot(variable.I) is { } componentSlot &&
            componentSlot.Representation is NullableRepresentationFrame.Role.Element or NullableRepresentationFrame.Role.NullableElement)
        {
            // An open factory must allocate for the caller's actual component,
            // but read/write values with the Kotlin element representation.
            // Keep both as owning graph operands across inline/closure lowering.
            var ordinary = new TypeNode.Tv(variable.Scope, frame.SourcePosition(componentSlot.SourceIndex));
            node = new BirValueBlock { FreshArrayWitness = true, Type = new TypeNode.Array(variable),
                Stmts = new IBirStatement[] { new BirExpressionStatement { Expr = new BirClassReference { Type = ordinary } } },
                Result = OptionalField<IBirExpression>.Of((IBirExpression)node) };
        }
        if (node is BirNewArray or BirNewArrayInit or BirNewArraySized or BirSpreadConcat
            && state.ArrayProjection(node) is TypeNode.Fqn { Args.Length: > 0 } component
            && !ContainsExistentialProjection(component) && state.Projection.TryExistentialCarrier(component.Name, out var elementCarrier))
        {
            // Preserve the allocation's component, not a future caller's requested
            // type. Its Kotlin value view stays carrier[] even across assemblies.
            SetSourceArrayElement(node, state.Projection.Rewrite(component, preserveConstructedHead: true, preserveArrayShape: true));
            node = new BirValueBlock { Type = new TypeNode.Array(new TypeNode.Fqn(elementCarrier)),
                Stmts = Array.Empty<IBirStatement>(), Result = OptionalField<IBirExpression>.Of((IBirExpression)node) };
            state.ArrayProjection(node, component);
        }
        node = BindProjectedFieldStorageSource(node, state);
        node = BindInheritedStarMemberSource(node, state);
        if (node is BirCallInstance call && call.OwnerType.OrDefault() is TypeNode.Fqn erased
            && (state.Owners.Values.Any(owner => owner.ErasedName == erased.Name) || state.References.IsExistentialPhysicalOwner(erased.Name)))
            call.Virtual = true;
        slot.Replace(node);
        if (SourceCallableBody(node).HasValue && SourceCallableHasParameters(node)) NormalizeInnerFactoryLocalsSource(node, state);
    }

    static TypeNode RewriteSourceMethodArgument(BirGraphValue call, TypeNode argument, int index,
        SourceRewriteState state, bool bound, bool preserveArray, HashSet<int> closing = null)
    {
        if (state.MethodWitnesses.TryGetValue(call, out var byIndex) && byIndex.TryGetValue(index, out var established) && established == argument)
            return established;
        var selection = state.SelectedMethodArgumentSlots.GetValueOrDefault(call);
        var identity = selection.Slot ?? call.DeclarationIdentityFact.OrDefault();
        var owner = SourceSelectedOwner(call) as TypeNode.Fqn;
        var selected = owner != null && identity != null
            && state.Demand.Preparation.Bindings.SourceLocalDeclarations.TryGetValue((owner.Name, identity), out var declaration)
            ? declaration : state.Demand.NativeProofs.LocalSourceMethodBinding(identity);
        var source = selection.Source == null ? selected : state.Demand.NativeProofs.LocalSourceMethodBinding(selection.Source);
        var sourceId = selection.Source ?? identity;
        var frame = source != null ? (source.Source as BirFunctionDeclaration)?.NullableRepresentationFrame.OrDefault()
            : state.References.NullableMethodFrame(sourceId);
        if (frame != null && frame.PhysicalArity != call.ApplicationTypeArguments.Value.Length)
            throw new InvalidOperationException("Selected method arguments disagree with the declaration's representation frame");
        var role = frame?.PhysicalSlot(index).Representation;
        var elementWitness = role is NullableRepresentationFrame.Role.Element or NullableRepresentationFrame.Role.NullableElement;
        var witness = frame == null || role
            is NullableRepresentationFrame.Role.Ordinary or NullableRepresentationFrame.Role.Nullable;
        var factory = call switch { BirCallInstance value => value._innerFactoryArguments.OrDefault(),
            BirCallStatic value => value._innerFactoryArguments.OrDefault(), _ => false };
        var projected = state.Projection.Rewrite(argument, bound, preserveArrayShape: preserveArray || elementWitness,
            preserveConstructedHead: witness && !ContainsExistentialProjection(argument)
                && (PreservesNativeWitnessHead(argument, state.Projection)
                    || preserveArray && IsExactNativeArrayWitness(argument, state.Projection) || factory
                    || state.Demand.NativeProofs.ExactMethodArgument(source, sourceId, index)
                    || StripSourceNullability(argument) is TypeNode.Fqn alias && state.Projection.RequiresOpaqueVariantAlias(alias)
                        && !ContainsExistentialProjection(alias) && !state.Projection.ProjectionCanChangeConstruction(alias)));
        if (!witness || StripSourceNullability(argument) is not TypeNode.Fqn { Args.Length: > 0 } application
            || !state.Projection.TryExistentialCarrier(application.Name, out var carrier)
            || StripSourceNullability(projected) is not TypeNode.Fqn p || p.Name != carrier) return projected;
        closing ??= new();
        if (!closing.Add(index)) return projected;
        var arguments = call.ApplicationTypeArguments.Value;
        var nativeParameters = call switch {
            BirGenericInstance c when c.ResolvedMemberParams.HasValue =>
                ClrMemberResolution.SelectedMethodTypeParametersInPhysicalFrame(null, c, state.References),
            BirGenericStatic c when c.ResolvedMemberParams.HasValue =>
                ClrMemberResolution.SelectedMethodTypeParametersInPhysicalFrame(null, c, state.References),
            BirCallInstance c when identity == null && c.MemberSignature.HasValue => c.MemberMethodTypeParams.OrDefault(),
            BirCallStatic c when identity == null && c.MemberSignature.HasValue => c.MemberMethodTypeParams.OrDefault(),
            _ => null,
        };
        var methodApplication = state.Projection.SelectPhysicalMethodApplication(selected?.Source as BirFunctionDeclaration,
            identity, frame, owner, arguments, nativeParameters);
        var proof = state.Projection.MethodClassWitness(methodApplication.Slot(index),
            i => RewriteSourceMethodArgument(call, arguments[i], i, state, bound, preserveArray, closing));
        closing.Remove(index);
        // Keep the split proof alongside the Kotlin argument. Projected
        // entries consume both parts separately; fixed CLR calls must close
        // their single slot at the invocation without changing the declaration.
        if (proof?.Required.Length > 0)
        {
            if (CapturedGenericInvocation.Invocation.Admits(call) ||
                call is BirNewDelegate or BirNewBoundDelegate or BirConstrainedCall &&
                !ProjectedCallableEntries.HasFixedAbi(selected?.Source as BirFunctionDeclaration, identity, state.References))
                call.MethodWitnessFacts = call.MethodWitnessFacts.OrEmpty().Where(w => w.Argument.Value != index)
                    .Append(new BirMethodWitness { Argument = index, Primary = proof.Class,
                        Interfaces = proof.Interfaces.Cast<TypeNode>().ToArray() }).ToArray();
            return projected;
        }
        var result = proof?.Class ?? projected;
        if (result != projected && identity != null && call is (BirCallStatic or BirCallInstance or
            BirNewDelegate or BirNewBoundDelegate or BirConstrainedCall))
        {
            var material = call.MaterialMethodArguments?.ToDictionary() ?? [];
            material[index] = projected;
            call.MaterialMethodArguments = material;
        }
        if (result != projected && result is TypeNode.Fqn { Args.Length: > 0 })
        {
            if (byIndex == null) state.MethodWitnesses[call] = byIndex = new();
            byIndex[index] = result;
        }
        return result;
    }

    internal static TypeNode SourceSelectedOwner(BirGraphValue node) => node switch {
        BirCallStatic value => value.CalleeOwner.OrDefault() ?? value.OwnerType.OrDefault() ?? value.Owner.OrDefault(),
        BirCallInstance value => value.OwnerType.OrDefault(), BirNewDelegate value => value.CalleeOwner.OrDefault(),
        BirGenericInstance value => value.Type.OrDefault(), BirGenericStatic value => value.Type.OrDefault(),
        BirNewBoundDelegate value => value.CalleeOwner.OrDefault() ?? value.OwnerType.OrDefault(),
        BirConstrainedCall value => value.Iface.OrDefault(), _ => null,
    };

    static IBirExpression[] SourceArguments(BirGraphValue node) => node switch {
        BirCallStatic value => value.Args.OrDefault(), BirCallInstance value => value.Args.OrDefault(),
        BirNew value => value.Args.OrDefault(), BirNewClr value => value.Args.OrDefault(),
        BirGenericInstance value => value.Args.OrDefault(), BirGenericStatic value => value.Args.OrDefault(),
        BirNativeInstance value => value.Args.OrDefault(), BirNativeStatic value => value.Args.OrDefault(), _ => null,
    };

    static bool IsSourceInnerOuterSlot(BirGraphValue node, int index, SourceRewriteState state)
        => index == 0 && node is BirNew construction && construction.Type.OrDefault() is TypeNode.Fqn owner
            && (state.Definition(owner.Name)?.Mods.OrDefault()?.Inner.OrDefault() == true
                || state.References.TryInnerSemanticOwner(owner.Name, out _));

    static (TypeNode Type, IBirExpression Operand, bool Exact) SourceRuntimeOperation(BirGraphValue node) => node switch {
        BirCast value => (value.Type.OrDefault(), value.E.OrDefault(), value._exactBridgeCast.OrDefault()),
        BirInstanceOrNull value => (value.Type.OrDefault(), value.E.OrDefault(), false),
        BirIsInstance value => (value.Type.OrDefault(), value.E.OrDefault(), value.ExactDispatchOwner),
        BirClassReference value => (value.Type.OrDefault(), null, false), _ => (null, null, false),
    };

    static BirGraphSlot SourceRuntimeOperandSlot(BirGraphValue node) => node switch {
        BirCast value => new(() => (BirGraphValue)value.E.Value, replacement => value.E = OptionalField<IBirExpression>.Of((IBirExpression)replacement)),
        BirInstanceOrNull value => new(() => (BirGraphValue)value.E.Value, replacement => value.E = OptionalField<IBirExpression>.Of((IBirExpression)replacement)),
        _ => throw new InvalidOperationException("Runtime operand does not belong to an owning cast"),
    };

    static void RewriteSourceRuntimeClassifier(BirGraphValue node, TypeNode.Fqn thisType, SourceRewriteState state)
    {
        var (target, _, exact) = SourceRuntimeOperation(node);
        var classifier = node is BirClassReference reference && reference.KotlinClassifierRef.OrDefault();
        if (node is BirClassReference classReference) classReference.KotlinClassifierRef = default;
        TypeNode physical = null;
        if (classifier && StripSourceNullability(target) is TypeNode.Fqn foreign
            && ForeignStarProjectionBinding.OpenClassifierType(foreign, state.References) is { } open)
        {
            physical = new TypeNode.Fqn(open);
            ForeignStarProjectionBinding.RequireRuntimeFallback();
        }
        else if (!exact && (node is BirCast or BirIsInstance or BirInstanceOrNull || classifier)
            && StripSourceNullability(target) is TypeNode.Fqn { Args: { } } application
            && state.Projection.TryExistentialCarrier(application.Name, out var carrier)
            && (node is BirIsInstance || classifier || !IsSourceConstructedUpcast(node, application, thisType, state)))
            physical = ReplaceProjectionCore(target, application, new TypeNode.Fqn(carrier));
        if (physical == null) return;
        switch (node)
        {
            case BirCast cast:
                cast.Type = BirGraphValue.CopyType(physical);
                if (cast.Sty.HasValue && StripSourceNullability(target) is TypeNode.Fqn source)
                    cast.Sty = ReplaceProjectionCore(cast.Sty.Value, source, StripSourceNullability(physical));
                break;
            case BirInstanceOrNull value: value.Type = BirGraphValue.CopyType(physical); break;
            case BirIsInstance value: value.Type = BirGraphValue.CopyType(physical); break;
            case BirClassReference value: value.Type = BirGraphValue.CopyType(physical); break;
        }
    }

    static bool IsSourceConstructedUpcast(BirGraphValue cast, TypeNode.Fqn target, TypeNode.Fqn thisType, SourceRewriteState state)
    {
        var operand = SourceRuntimeOperation(cast).Operand;
        var source = operand switch {
            BirThis => thisType, BirCast value => value.Type.OrDefault(), BirIsInstance value => value.Type.OrDefault(),
            BirCallInstance value => value.DynRet.OrDefault() ?? value.Sty.OrDefault(),
            BirCallStatic value => value.DynRet.OrDefault() ?? value.Sty.OrDefault(),
            _ => BirNodeType.Of(operand as BirGraphNode),
        };
        if (target.Args is not { } arguments || arguments.Any(ContainsExplicitExistential)
            || StripSourceNullability(source) is not TypeNode.Fqn application) return false;
        var values = application.Args ?? Array.Empty<TypeNode>();
        if (SameDeclarationOwner(application.Name, target.Name))
            return values.Length == arguments.Length && values.Select(SupertypeGraph.TypeKey).SequenceEqual(arguments.Select(SupertypeGraph.TypeKey));
        var projected = state.ProjectArguments(application, target.Name);
        return projected != null && projected.Count == arguments.Length
            && projected.Select(SupertypeGraph.TypeKey).SequenceEqual(arguments.Select(SupertypeGraph.TypeKey));
    }
}
