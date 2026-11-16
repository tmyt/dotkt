using System.IO;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class MemberCallSubstitution
{
    // A view of the two owning semantic call records, not a copied executable graph.
    sealed class SourceCall
    {
        internal readonly BirCallStatic Static;
        internal readonly BirCallInstance Instance;
        internal BirGraphValue Graph => (BirGraphValue)Static ?? Instance;
        internal SourceCall(BirCallStatic call) { Static = call; }
        internal SourceCall(BirCallInstance call) { Instance = call; }
        internal OptionalField<string> Method { get => Static != null ? Static.Method : Instance.Method; set { if (Static != null) Static.Method = value; else Instance.Method = value; } }
        internal OptionalField<string> Prop { get => Static != null ? Static.Prop : Instance.Prop; set { if (Static != null) Static.Prop = value; else Instance.Prop = value; } }
        internal OptionalField<TypeNode[]> Sig { get => Static != null ? Static.Sig : Instance.Sig; set { if (Static != null) Static.Sig = value; else Instance.Sig = value; } }
        internal OptionalField<TypeNode[]> TypeArgs { get => Static != null ? Static.TypeArgs : Instance.TypeArgs; set { if (Static != null) Static.TypeArgs = value; else Instance.TypeArgs = value; } }
        internal OptionalField<TypeNode[]> ShapeTypes { get => Static != null ? Static.ShapeTypes : Instance.ShapeTypes; set { if (Static != null) Static.ShapeTypes = value; else Instance.ShapeTypes = value; } }
        internal OptionalField<TypeNode[]> ArgTypes { get => Static != null ? Static.ArgTypes : Instance.ArgTypes; set { if (Static != null) Static.ArgTypes = value; else Instance.ArgTypes = value; } }
        internal OptionalField<TypeNode> Ret { get => Static != null ? Static.Ret : Instance.Ret; set { if (Static != null) Static.Ret = value; else Instance.Ret = value; } }
        internal OptionalField<TypeNode> DynRet { get => Static != null ? Static.DynRet : Instance.DynRet; set { if (Static != null) Static.DynRet = value; else Instance.DynRet = value; } }
        internal OptionalField<TypeNode> Sty { get => Static != null ? Static.Sty : Instance.Sty; set { if (Static != null) Static.Sty = value; else Instance.Sty = value; } }
        internal OptionalField<string> DeclarationId { get => Static != null ? Static.DeclarationId : Instance.DeclarationId; set { if (Static != null) Static.DeclarationId = value; else Instance.DeclarationId = value; } }
        internal OptionalField<string> PropertyName { get => Static != null ? Static.PropertyName : Instance.PropertyName; set { if (Static != null) Static.PropertyName = value; else Instance.PropertyName = value; } }
        internal OptionalField<string> PropertyAccessor { get => Static != null ? Static.PropertyAccessor : Instance.PropertyAccessor; set { if (Static != null) Static.PropertyAccessor = value; else Instance.PropertyAccessor = value; } }
        internal OptionalField<bool> Virtual { get => Static != null ? Static.Virtual : Instance.Virtual; set { if (Static != null) Static.Virtual = value; else Instance.Virtual = value; } }
        internal OptionalField<TypeNode> Owner { get => Static != null ? Static.Owner : Instance.OwnerType; set { if (Static != null) Static.Owner = value; else Instance.OwnerType = value; } }
        internal OptionalField<IBirExpression> Receiver { get => Instance?.Recv ?? default; set { if (Instance != null) Instance.Recv = value; } }
        internal OptionalField<IBirExpression[]> Args => Static != null ? Static.Args : Instance.Args;
        internal BirOverride[] Overrides => Instance?.Overrides.OrEmpty() ?? Array.Empty<BirOverride>();
        internal OptionalField<bool> Super => Instance?.Super ?? default;
        internal bool HasIdentity(out string name, out string kind) => KotlinPropertyAccessors.TryCallIdentity(
            PropertyName.OrDefault(), PropertyAccessor.OrDefault(), Method.OrDefault(), Prop.OrDefault(), out name, out kind);
        internal void PreserveIdentity(string name, string kind)
        {
            if (!KotlinPropertyAccessors.HasIdentity(name, kind)) throw new InvalidOperationException("incomplete Kotlin property call identity");
            PropertyName = name; PropertyAccessor = kind;
        }
    }

    internal static BirGraphValue TransformCall(BirCallStatic node, ReferenceMetadataIndex refs,
        CollectionHelperBinding.CollectionSourceContext context) => TransformCall(new SourceCall(node), refs, false, context);
    internal static BirGraphValue TransformCall(BirCallInstance node, ReferenceMetadataIndex refs,
        CollectionHelperBinding.CollectionSourceContext context) => TransformCall(new SourceCall(node), refs, true, context);

    static BirGraphValue TransformCall(SourceCall node, ReferenceMetadataIndex refs, bool instance,
        CollectionHelperBinding.CollectionSourceContext context)
    {
        var owner = node.Owner.OrDefault() as TypeNode.Fqn;
        var ownerToken = owner?.Name;
        if (string.IsNullOrEmpty(ownerToken))
        {
            var call = node.Static;
            var member = node.Method.OrDefault();
            if (instance || string.IsNullOrEmpty(member)) return null;
            if (node.Prop.OrDefault() is ("get" or "set") and var injectedKind
                && call.OwnerType.OrDefault() is TypeNode.Fqn injectedOwner
                && ResolveInjectedPropertyOwnerValue(refs, injectedOwner) is var injected
                && injected.Type != null && NetInteropBinding.MemberIsPropertyOrField(injected.Type, member))
            {
                var closed = NetInteropBinding.CloseStaticOwner(injected.Owner, injected.Type);
                return injectedKind == "get"
                    ? new BirPropertyGet { Type = closed, Name = member, Static = true, Ret = CopyCallType(node.Ret) }
                    : new BirPropertySet { Type = closed, Name = member, Static = true,
                        Value = CopyCallExpression(node.Args.OrEmpty().FirstOrDefault()) };
            }
            var injectedSignature = call.ArgTypes.OrDefault() ?? call.Sig.OrDefault();
            if (!call.Prop.HasValue && injectedSignature != null && call.OwnerType.OrDefault() is TypeNode.Fqn classOwner
                && refs.HasDotKtOwner(classOwner.Name) && !refs.IsFileClassOwner(classOwner.Name))
            {
                call.Owner = CopyCallType(classOwner);
                if (!call.Sig.HasValue) call.Sig = injectedSignature.Select(CopyCallType).ToArray();
                return call;
            }
            var propertyKind = node.Prop.OrDefault();
            var exactFactory = call.DeclarationReferencedFactory.OrDefault();
            if (TryFactorySubst(call, refs, context) is { } factory) return (BirGraphValue)factory;
            if (exactFactory) return null;
            var args = node.Args.OrEmpty();
            var signature = node.Sig.OrEmpty().ToList();
            if (call.DeclarationReferencedIntrinsic.OrDefault() && node.DeclarationId.HasValue)
            {
                var id = node.DeclarationId.Value;
                if (!refs.TryDeclarationIdentity(id, out _, out _, out var intrinsic, out var byref)
                    || string.IsNullOrEmpty(intrinsic))
                    throw new InvalidOperationException($"bir2cir: selected referenced declaration identity '{id}' lost its intrinsic binding");
                if (CompilerMetadataIntrinsicValue(intrinsic, refs) is { } metadata) return (BirGraphValue)metadata;
                var dot = intrinsic.LastIndexOf('.');
                if (dot > 0) return ClrCallNode(node, new TypeNode.Fqn(intrinsic[..dot]), intrinsic[(dot + 1)..],
                    intrinsic[(dot + 1)..], args, refs, false, byref);
                if (signature.Count >= 1)
                    return TopLevelExtensionInstance(node, refs, intrinsic, args, signature, context)
                        ?? throw new InvalidOperationException($"bir2cir: selected intrinsic declaration '{id}' has no extension receiver");
                throw new InvalidOperationException($"bir2cir: selected intrinsic declaration '{id}' has unsupported binding '{intrinsic}'");
            }
            if (propertyKind is "get" or "set")
            {
                node.PreserveIdentity(member, propertyKind);
                if (call.CalleeOwner.OrDefault() is TypeNode.Fqn) return null;
                var projectedOwner = (call.OwnerType.OrDefault() as TypeNode.Fqn)?.Name;
                if (projectedOwner != null && !refs.IsFileClassOwner(projectedOwner)) projectedOwner = null;
                var propertySignature = signature.Count == args.Length ? signature.ToArray() : null;
                if (_attributeTopLevelOwner && refs.TryResolveTopLevelProperty(member, propertyKind, projectedOwner,
                    args.Length, call.TypeArgs.OrEmpty().Length, propertySignature, out var propertyOwner, out var accessor))
                {
                    node.Prop = default;
                    node.Method = member = accessor;
                    node.Owner = new TypeNode.Fqn(propertyOwner);
                    PromoteGenericShapeToSig(call);
                }
                else if (!_attributeTopLevelOwner) return null;
                else throw new InvalidOperationException($"bir2cir: unresolved top-level property accessor '{member}' ({propertyKind}) — "
                    + "the frontend-resolved property has no exact reference MethodSemantics association");
            }
            var signatureKey = new ReferenceMetadataIndex.SignatureKey(signature.Select(ReferenceMetadataIndex.ParamKey));
            if ((refs.TryTopLevelIntrinsicBySig(member, signatureKey, out var qualified)
                    || !refs.IsAmbiguousTopLevelIntrinsic(member) && !refs.HasNonIntrinsicTopLevel(member)
                        && refs.TryTopLevelIntrinsic(member, out qualified))
                && qualified.LastIndexOf('.') is var separator && separator > 0)
                return (BirGraphValue)CompilerMetadataIntrinsicValue(qualified, refs)
                    ?? ClrCallNode(node, new TypeNode.Fqn(qualified[..separator]), qualified[(separator + 1)..],
                        qualified[(separator + 1)..], args, refs, false, refs.TopLevelByrefPositions(member));
            if (signature.Count >= 1 && refs.TryExtMemberIntrinsic(member, signatureKey, out var extension))
                return TopLevelExtensionInstance(node, refs, extension, args, signature, context);
            if (_attributeTopLevelOwner && call.CalleeOwner.OrDefault() is not TypeNode.Fqn)
            {
                var receiverKey = signature.Count >= 1 ? RecvKeyOf(signature[0]) : "";
                var firstKey = signature.Count >= 1 ? ReferenceMetadataIndex.ReceiverParamKey(signature[0]) : null;
                if (refs.TryResolveTopLevelStatic(member, receiverKey, firstKey, out var fileClass))
                {
                    node.Owner = new TypeNode.Fqn(fileClass);
                    PromoteGenericShapeToSig(call);
                    return call;
                }
                if (call.ShapeTypes.HasValue && !call.Sig.HasValue
                    && call.OwnerType.OrDefault() is TypeNode.Fqn { Name.Length: > 0 } genericOwner)
                {
                    node.Owner = new TypeNode.Fqn(genericOwner.Name);
                    PromoteGenericShapeToSig(call);
                    return call;
                }
                var staticSignature = call.ArgTypes.OrDefault() ?? call.Sig.OrDefault();
                if (staticSignature != null && call.OwnerType.OrDefault() is TypeNode.Fqn staticOwner)
                {
                    node.Owner = CopyCallType(staticOwner);
                    if (!call.Sig.HasValue) call.Sig = staticSignature.Select(CopyCallType).ToArray();
                    return call;
                }
            }
            return null;
        }

        var memberOwner = ownerToken;
        var companionMember = node.Method.OrDefault();
        var companionArguments = node.Args.OrEmpty();
        var methodArity = node.TypeArgs.OrEmpty().Length;
        IReadOnlyList<TypeNode> declarationSignature = node.ArgTypes.OrDefault() ?? node.ShapeTypes.OrDefault() ?? node.Sig.OrDefault();
        if (declarationSignature?.Any(type => type == null) == true) declarationSignature = null;
        if (declarationSignature == null && companionArguments.Length == 0) declarationSignature = Array.Empty<TypeNode>();
        var carrier = refs.TryCompanionPhysicalOwner(memberOwner, out var mappedCarrier) ? mappedCarrier : memberOwner;
        var companionOwner = ReferenceMetadataIndex.BareOwnerFqn(carrier);
        var selectedId = node.DeclarationId.OrDefault();
        var hasCompanionBinding = refs.TryDeclarationClrBinding(selectedId, out var companionBinding)
            || refs.TryExactMemberClrBinding(companionOwner, companionMember, methodArity, declarationSignature, out companionBinding);
        ExactClrMemberBinding inheritedBinding = null;
        IBirExpression mappedReceiver = null;
        if (instance && hasCompanionBinding && refs.TryCompanionSemanticOwner(carrier, out var semanticOwner)
            && refs.TryResolveClrOwner(semanticOwner, out _, out _))
        {
            mappedReceiver = CloneCallExpression(node.Receiver.OrDefault());
            ownerToken = semanticOwner;
            owner = new TypeNode.Fqn(semanticOwner, owner.Args);
            instance = false;
        }
        if (instance && IsSourceProperty(node, "storage", "get")
            && UnsignedArraySignedElem.TryGetValue(ownerToken, out var signedElement))
            return new BirCast { Type = new TypeNode.Array(new TypeNode.Fqn(signedElement)), E = CopyCallExpression(node.Receiver.OrDefault()) };
        // The inherited interface's exact slot, closed in the direct owner's frame, owns this call.
        if (instance && !node.Super.HasValue && !refs.TryResolveClrOwner(ownerToken, out _, out _)
            && node.Instance.Overrides.HasValue && !node.HasIdentity(out _, out _))
        {
            var renamed = node.Method.OrDefault();
            var directArguments = owner.Args ?? Array.Empty<TypeNode>();
            var inheritedSignature = (refs.DeclarationOverrideParameters(selectedId) ?? declarationSignature)?
                .Select(type => SupertypeGraph.SubstOwnerTvs(type, directArguments)).ToArray();
            var inherited = new List<(TypeNode.Fqn Owner, string Member, ExactClrMemberBinding Binding)>();
            foreach (var candidate in node.Overrides)
            {
                if (candidate.Kind.OrDefault() != "method" || candidate.Owner.OrDefault() is not TypeNode.Fqn candidateOwner
                    || !candidate.Member.HasValue || !candidate.Arity.HasValue || candidate.Arity.Value != companionArguments.Length) continue;
                var constructed = candidateOwner.Args == null ? candidateOwner : new TypeNode.Fqn(candidateOwner.Name,
                    candidateOwner.Args.Select(type => SupertypeGraph.SubstOwnerTvs(type, directArguments)).ToArray());
                if (!refs.TryResolveClrOwner(constructed.Name, out _, out var candidateKind) || candidateKind != "interface"
                    || refs.OwnerArity(constructed.Name) != (constructed.Args?.Length ?? 0)
                    || !refs.TryExactMemberClrBinding(ReferenceMetadataIndex.BareOwnerFqn(constructed.Name),
                        candidate.Member.Value, methodArity, inheritedSignature, constructed.Args ?? Array.Empty<TypeNode>(), out var binding)
                    || binding.Intrinsic == null || binding.Intrinsic != renamed) continue;
                inherited.Add((constructed, candidate.Member.Value, binding));
            }
            var distinct = inherited.GroupBy(candidate => string.Join("\u001f", candidate.Member, candidate.Binding.Intrinsic,
                candidate.Binding.CountStart, candidate.Binding.CountEnd,
                string.Join(",", candidate.Binding.ByrefPositions ?? Array.Empty<int>())), StringComparer.Ordinal).Select(group => group.First()).ToList();
            if (distinct.Count > 1)
                throw new InvalidOperationException($"bir2cir: inherited CLR interface call '{ownerToken}.{renamed}' has "
                    + $"{distinct.Count} distinct exact slot bindings: " + string.Join(", ", distinct.Select(candidate =>
                        $"{SupertypeGraph.TypeKey(candidate.Owner)}.{candidate.Member}->{candidate.Binding.Intrinsic}")));
            if (distinct.Count == 1)
            {
                var chosen = distinct[0];
                owner = new TypeNode.Fqn(chosen.Owner.Name, chosen.Owner.Args?.Select(type => NullableGenericErasure.EraseArgument(type, _isValue)).ToArray());
                ownerToken = memberOwner = chosen.Owner.Name;
                node.Method = chosen.Member;
                inheritedBinding = chosen.Binding;
                declarationSignature = inheritedSignature;
                if (inheritedSignature != null) node.Sig = inheritedSignature.Select(BirTypeLowering.CanonicalPhysicalSlotType).ToArray();
            }
        }
        if (instance && !refs.TryResolveClrOwner(ownerToken, out _, out _)
            && node.HasIdentity(out var localProperty, out var localKind))
        {
            var signature = node.Sig.OrDefault() ?? node.ArgTypes.OrDefault();
            if (signature != null && (signature.Length != companionArguments.Length || signature.Any(type => type == null))) signature = null;
            if (TryResolveLocalPropertyAccessor(_localPropertyAccessors, ReferenceMetadataIndex.BareOwnerFqn(ownerToken),
                localProperty, localKind, methodArity, companionArguments.Length, signature,
                owner.Args ?? Array.Empty<TypeNode>(), out var accessor)) return null;
            if (refs.TryKotlinPropertyAccessor(ReferenceMetadataIndex.BareOwnerFqn(ownerToken), localProperty, localKind,
                companionArguments.Length, methodArity, signature, owner.Args ?? Array.Empty<TypeNode>(), out accessor, out var virtualAccessor))
            {
                node.PreserveIdentity(localProperty, localKind);
                node.Prop = default;
                node.Method = accessor;
                if (virtualAccessor) node.Virtual = true;
                return null;
            }
        }
        {
            var propertyMember = node.Method.OrDefault();
            node.HasIdentity(out var property, out var kind);
            var directProperty = instance && !string.IsNullOrEmpty(propertyMember)
                && refs.TryResolveClrOwner(ownerToken, out _, out _) && property != null
                && refs.TryExternalPropertyAccessor(memberOwner, property, kind, companionArguments.Length, methodArity,
                    declarationSignature, owner.Args ?? Array.Empty<TypeNode>(), out _, out _, out _);
            if (instance && !directProperty && !string.IsNullOrEmpty(propertyMember))
                foreach (var candidate in node.Overrides)
                    if (candidate.Owner.OrDefault() is TypeNode.Fqn propertyOwner
                        && candidate.Member.OrDefault() == property && candidate.Kind.OrDefault() == (kind == "set" ? "setter" : "getter")
                        && !_localPropertyOwners.Contains(propertyOwner.Name)
                        && refs.TryExternalPropertyAccessor(propertyOwner.Name, property, kind, companionArguments.Length, methodArity,
                            declarationSignature, Array.Empty<TypeNode>(), out var bclPropertyOwner, out var physicalProperty, out _))
                        return ClrPropNode(node, ClrValueOwnerType(refs, owner) ?? ClrOwnerType(refs, new TypeNode.Fqn(propertyOwner.Name))
                            ?? new TypeNode.Fqn(bclPropertyOwner), physicalProperty, kind == "set" ? ClrPropWrite : ClrPropRead,
                            propertyMember, companionArguments, kind);
        }
        if (instance && node.Method.OrDefault() == "iterator" && node.Args.HasValue && companionArguments.Length == 0
            && !refs.TryResolveClrOwner(ownerToken, out _, out _))
        {
            var plan = SelectMissingCollectionIterator(node.Method.OrDefault(), companionArguments.Length,
                HasDeclaredIterator(owner, _typesWithDeclaredIterator, _iteratorTypeDefinitions, refs, refs.Aliases),
                node.HasIdentity(out _, out _), node.TypeArgs.OrEmpty().Length, node.Sty.OrDefault() ?? node.Ret.OrDefault());
            if (plan != null) return CollectionHelperBinding.RenderCollectionOperation(plan, node.Instance, null, CollectionOperationPhase.Physical);
        }
        if (instance && AliasHelperHoist.IsNativeArrayOwner(owner.Name))
        {
            if (AliasHelperHoist.UsesNativeArrayObjectSlot(owner.Name, node.Instance.AnySlot.OrDefault()))
                return new BirObjectMethod { Method = node.Method, Recv = CopyCallExpression(node.Receiver.OrDefault()),
                    Arg = companionArguments.Length == 1 ? CopyCallExpression(companionArguments[0]) : default };
            if (refs.IsRule3Member(owner.Name, node.Method.OrDefault()))
                return Rule3HelperCall(node, refs, owner, node.Method.OrDefault(), companionArguments, true);
        }
        if (!refs.TryResolveClrOwner(ownerToken, out var bcl, out var ownerKind))
        {
            if (node.Prop.OrDefault() is ("get" or "set") and var propertyKind && node.Method.HasValue)
            {
                node.PreserveIdentity(node.Method.Value, propertyKind);
                node.Prop = default;
                node.Method = KotlinPropertyAccessors.PhysicalName(node.Method.Value, propertyKind);
            }
            return null;
        }
        var name = node.Method.OrDefault();
        if (string.IsNullOrEmpty(name)) return null;
        var ownerName = ReferenceMetadataIndex.BareOwnerFqn(memberOwner);
        var arguments = node.Args.OrEmpty();
        var staticProperty = !instance ? node.Prop.OrDefault() : null;
        if (staticProperty != null) { node.PreserveIdentity(name, staticProperty); node.Prop = default; }
        var bindingExact = inheritedBinding;
        var hasExact = bindingExact != null || refs.TryDeclarationClrBinding(selectedId, out bindingExact)
            || refs.TryExactMemberClrBinding(ownerName, name, methodArity, declarationSignature, out bindingExact);
        if (instance && arguments.Length == 0 && hasExact && bindingExact.Conv && bindingExact.ConvTo != null)
            return new BirConvert { To = CopyCallType(bindingExact.ConvTo), E = CopyCallExpression(node.Receiver.OrDefault()) };
        var inlineGetter = node.HasIdentity(out var inlineProperty, out var inlineKind)
            ? refs.TryInlineFieldGetter(ownerName, inlineProperty, inlineKind, out var inlineConversion)
            : refs.TryInlineFieldGetter(ownerName, name, out inlineConversion);
        if (instance && inlineGetter)
            return new BirConvert { To = new TypeNode.Fqn(inlineConversion), E = CopyCallExpression(node.Receiver.OrDefault()) };
        if (instance && CollectionCall(node, owner, refs, context, CollectionCallKind.Static) is { } collection)
        { BindAuthoredHelper(collection, refs, context); return collection; }
        var clrOwner = (instance ? ClrValueOwnerType(refs, owner) : ClrOwnerType(refs, owner)) ?? new TypeNode.Fqn(bcl);
        BirGraphValue PreserveMapped(BirGraphValue result, string site) => (BirGraphValue)TypedCallEvalLowering.PreserveUnreadValueBefore(
            mappedReceiver, (IBirExpression)result, site);
        if (mappedReceiver != null && companionBinding.PropertyName != null)
        {
            var marker = staticProperty;
            if (marker == null)
            {
                var read = (companionBinding.PropertyAccess & ClrPropRead) != 0;
                var write = (companionBinding.PropertyAccess & ClrPropWrite) != 0;
                marker = write && (!read || arguments.Length == 1) ? "set" : "get";
            }
            return PreserveMapped(ClrPropNode(node, clrOwner, companionBinding.PropertyName, companionBinding.PropertyAccess,
                name, arguments, marker, true), $"mapped companion property '{companionOwner}.{name}'");
        }
        var semanticProperty = node.HasIdentity(out var sourceProperty, out var sourceKind);
        if (semanticProperty && refs.TryExternalPropertyAccessor(ownerName, sourceProperty, sourceKind, arguments.Length,
            methodArity, declarationSignature, owner.Args ?? Array.Empty<TypeNode>(), out _, out var physical, out _))
            return PreserveMapped(ClrPropNode(node, clrOwner, physical, sourceKind == "set" ? ClrPropWrite : ClrPropRead,
                name, arguments, sourceKind, mappedReceiver != null), $"mapped companion property '{companionOwner}.{name}'");
        if (!semanticProperty && (instance || staticProperty is "get" or "set") && hasExact && bindingExact.PropertyName != null)
            return PreserveMapped(ClrPropNode(node, clrOwner, bindingExact.PropertyName, bindingExact.PropertyAccess,
                name, arguments, staticProperty, mappedReceiver != null), $"mapped companion property '{companionOwner}.{name}'");
        if (instance && ownerKind == "interface" && CollectionCall(node, owner, refs, context, CollectionCallKind.Mutation) is { } mutation)
        { BindAuthoredHelper(mutation, refs, context); return mutation; }
        if (instance && name == "compareTo" && arguments.Length == 1
            && (CompareToBclTarget(ownerName) ?? (context?.Receiver(node.Receiver.OrDefault(), true) is TypeNode.Fqn receiver
                ? CompareToBclTarget(receiver.Name) : null)) is string primitive)
            return new BirNativeInstance { Type = new TypeNode.Fqn(primitive), Method = "CompareTo",
                ArgTypes = new TypeNode[] { new TypeNode.Fqn(primitive) }, Ret = new TypeNode.Fqn("System.Int32"),
                Recv = CopyCallExpression(node.Receiver.OrDefault()), Args = CloneCallExpressions(arguments) };
        if (mappedReceiver != null && companionBinding.Intrinsic != null)
            return PreserveMapped(ExactIntrinsicCall(node, clrOwner, name, arguments, false, companionBinding, refs, context, ownerToken),
                $"mapped companion intrinsic '{companionOwner}.{name}'");
        if (hasExact && bindingExact.Intrinsic != null)
            return PreserveMapped(ExactIntrinsicCall(node, clrOwner, name, arguments, instance, bindingExact, refs, context, ownerToken),
                $"mapped companion intrinsic '{companionOwner}.{name}'");
        if (ownerKind != "interface" && node.HasIdentity(out var rule3Property, out var rule3Kind)
            && refs.TryRule3PropertyAccessor(ownerName, rule3Property, rule3Kind, out var helperAccessor))
            return Rule3HelperCall(node, refs, owner, helperAccessor, arguments, instance);
        if (ownerKind != "interface" && refs.IsRule3Member(ownerName, name)) return Rule3HelperCall(node, refs, owner, name, arguments, instance);
        if (ownerKind != "interface" && instance)
            foreach (var candidate in node.Overrides)
                if (candidate.Owner.OrDefault() is TypeNode.Fqn ancestor && candidate.Member.HasValue
                    && refs.TryResolveClrOwner(ancestor.Name, out _, out var ancestorKind) && ancestorKind != "interface")
                {
                    var accessorKind = candidate.Kind.OrDefault() switch { "getter" => "get", "setter" => "set", _ => null };
                    if (accessorKind != null && refs.TryRule3PropertyAccessor(ancestor.Name, candidate.Member.Value, accessorKind, out var inheritedAccessor))
                        return Rule3HelperCall(node, refs, new TypeNode.Fqn(ancestor.Name), inheritedAccessor, arguments, true);
                    if (accessorKind == null && refs.IsRule3Member(ancestor.Name, candidate.Member.Value))
                        return Rule3HelperCall(node, refs, new TypeNode.Fqn(ancestor.Name), candidate.Member.Value, arguments, true);
                }
        if (instance && ownerKind == "interface" && ownerName is "kotlin.collections.Map" or "kotlin.collections.MutableMap")
        {
            var helper = MapDefaultHelper(name, node.PropertyAccessor.OrDefault() ?? node.Prop.OrDefault(), arguments.Length,
                ownerName == "kotlin.collections.MutableMap");
            if (helper != null) return MapDefaultCall(node, helper, owner, arguments, refs, context);
        }
        else if (instance && ownerKind == "interface" && (ownerName.StartsWith("kotlin.collections.", StringComparison.Ordinal)
            || ownerName == "kotlin.sequences.Sequence") && CollectionCall(node, owner, refs, context, CollectionCallKind.Default) is { } defaults)
        { BindAuthoredHelper(defaults, refs, context); return defaults; }
        if (semanticProperty) throw new InvalidOperationException($"bir2cir: unresolved CLR property accessor '{ownerName}.{sourceProperty}' ({sourceKind}) — "
            + "the frontend-resolved property has no exact CLR property, intrinsic, or helper binding");
        if (instance && ownerKind == "interface") AssertInterfaceMemberRouted(ownerName, owner, name, refs);
        if (instance && ownerKind != "interface" && !string.IsNullOrEmpty(name)
            && refs.TryDeclaresAccessibleInstanceMethod(ownerName, owner.Args?.Length ?? 0, name, out var declared) && !declared)
            throw new InvalidOperationException($"bir2cir: unresolved CLR member '{ownerName}.{name}' — the CLR-bound "
                + $"{ownerKind} owner '{ownerToken}' has no @ClrIntrinsic/@ClrProperty/rule-3 binding and is not a BCL member "
                + "declared by that exact name. This is a routing MISS: fix the stdlib binding or the owner alias, "
                + "do not let it fall to a silent runtime dynamic-dispatch NRE.");
        return Constrainify(ClrCallNode(node, clrOwner, name, name, arguments, refs, instance), node, refs, context, ownerToken);
    }

    static TypeNode CopyCallType(TypeNode type) => BirGraphValue.CopyType(type);
    static OptionalField<TypeNode> CopyCallType(OptionalField<TypeNode> type) => type.HasValue
        ? OptionalField<TypeNode>.Of(CopyCallType(type.Value)) : type;
    static IBirExpression CloneCallExpression(IBirExpression value) => value == null ? null
        : (IBirExpression)((BirGraphValue)value).DeepClone();
    static OptionalField<IBirExpression> CopyCallExpression(IBirExpression value) => value == null ? default
        : OptionalField<IBirExpression>.Of(CloneCallExpression(value));
    static IBirExpression[] CloneCallExpressions(IEnumerable<IBirExpression> expressions) => expressions.Select(CloneCallExpression).ToArray();
    static bool IsSourceProperty(SourceCall call, string name, string kind) =>
        call.PropertyName.OrDefault() == name && call.PropertyAccessor.OrDefault() == kind
        || call.Method.OrDefault() == name && call.Prop.OrDefault() == kind;

    // These are the original own-slot priorities, not expression inference or Sty fallback.
    internal static TypeNode InferArgType(IBirExpression expression)
    {
        TypeNode type = null, result = null, suspendResult = null, dynamicResult = null;
        (expression as BirGraphValue)?.VisitOwnTypeFields((field, value) => {
            switch (field) {
                case "type": type = value; break;
                case "ret": result = value; break;
                case "suspendRet": suspendResult = value; break;
                case "dynRet": dynamicResult = value; break;
            }
        });
        return CopyCallType(type ?? result ?? suspendResult ?? dynamicResult ?? ObjType);
    }
    internal static TypeNode RetToken(BirGraphValue node) => node switch {
        BirCallStatic value => CopyCallType(value.DynRet.OrDefault() ?? value.Ret.OrDefault()),
        BirCallInstance value => CopyCallType(value.DynRet.OrDefault() ?? value.Ret.OrDefault()),
        _ => null,
    };
    static TypeNode RetToken(SourceCall node) => RetToken(node.Graph);
    static TypeNode[] InferArgTypes(SourceCall node, IBirExpression[] arguments) =>
        node.Sig.HasValue && node.Sig.Value.Length > 0 && node.Sig.Value.Length == arguments.Length
            ? node.Sig.Value.Select(CopyCallType).ToArray() : arguments.Select(InferArgType).ToArray();
    static void PromoteGenericShapeToSig(BirCallStatic node)
    {
        if (node.Sig.HasValue || !node.ShapeTypes.HasValue) return;
        node.Sig = node.ShapeTypes.Value.Select(CopyCallType).ToArray();
        node.ArgTypes = node.ShapeTypes.Value.Select(type => SubstMethodTv(type, node.TypeArgs.OrDefault())).ToArray();
        node.ShapeTypes = default;
    }

    static (TypeNode Owner, Type Type) ResolveInjectedPropertyOwnerValue(ReferenceMetadataIndex refs, TypeNode.Fqn owner) =>
        refs.TryGenericStaticCarrier(owner.Name, out var carrier)
            ? (new TypeNode.Fqn(carrier), refs.ResolveRefType(carrier, 0))
            : (CopyCallType(owner), refs.ResolveRefType(owner.Name, owner.Args?.Length ?? 0));

    static BirGraphValue Constrainify(BirGraphValue built, SourceCall source, ReferenceMetadataIndex refs,
        CollectionHelperBinding.CollectionSourceContext context, string ownerToken)
    {
        if (context == null || built is not BirNativeInstance call || !source.Receiver.HasValue) return built;
        var compare = call.Method.OrDefault() == "CompareTo";
        var receiver = context.Receiver(source.Receiver.Value, compare);
        if (receiver is not TypeNode.Tv variable
            || !refs.TryResolveClrOwner(ownerToken, out var bcl, out var kind) || kind != "interface") return built;
        TypeNode[] arguments;
        if (compare) arguments = new[] { receiver };
        else
        {
            if (!context.Constraints.TryGetValue(variable.Scope + ":" + variable.I, out var constraints)) return built;
            arguments = null;
            foreach (var bound in constraints)
                if (bound is TypeNode.Fqn { Args: not null } named
                    && refs.TryResolveClrOwner(named.Name, out _, out var boundKind) && boundKind == "interface")
                { arguments = named.Args; break; }
            if (arguments == null) return built;
        }
        return new BirConstrainedCall {
            RecvType = CopyCallType(receiver), Iface = new TypeNode.Fqn(bcl, arguments.Select(CopyCallType).ToArray()),
            Method = call.Method, Recv = CopyCallExpression(call.Recv.OrDefault()), Args = CloneCallExpressions(call.Args.OrEmpty()),
            ArgTypes = call.ArgTypes.HasValue ? OptionalField<TypeNode[]>.Of(call.ArgTypes.Value.Select(CopyCallType).ToArray()) : call.ArgTypes,
            Ret = CopyCallType(call.Ret),
        };
    }

    enum CollectionCallKind { Static, Mutation, Default }
    static BirCallStatic CollectionCall(SourceCall node, TypeNode.Fqn owner, ReferenceMetadataIndex refs,
        CollectionHelperBinding.CollectionSourceContext context, CollectionCallKind kind)
    {
        var receiver = context?.Receiver(node.Receiver.OrDefault());
        var element = CollectionElement(owner, receiver, context?.Constraints, refs);
        var projectedLocal = node.Receiver.OrDefault() is BirLocal { Name.HasValue: true } local
            && context?.ProjectedCollectionVars.Contains(local.Name.Value) == true;
        var projected = ProjectedCollectionReceiver(owner, receiver, projectedLocal, context?.Constraints, refs);
        var plan = kind switch {
            CollectionCallKind.Static => SelectStaticCollectionOperation(node.Method.OrDefault(), node.Prop.OrDefault(),
                node.Args.OrEmpty().Length, node.Receiver.HasValue, owner, element, null),
            CollectionCallKind.Mutation => SelectCollectionMutation(node.Method.OrDefault(), node.Args.OrEmpty().Length,
                owner, element, projected, null, RetToken(node)),
            _ => SelectCollectionDefault(node.Method.OrDefault(), node.Prop.OrDefault(), node.Args.OrEmpty().Length,
                node.Receiver.HasValue, owner, element, projected, null),
        };
        return plan == null ? null : CollectionHelperBinding.RenderCollectionOperation(plan, node.Instance, refs, CollectionOperationPhase.Physical);
    }

    static BirCallStatic MapDefaultCall(SourceCall node, string helper, TypeNode.Fqn owner, IBirExpression[] arguments,
        ReferenceMetadataIndex refs, CollectionHelperBinding.CollectionSourceContext context)
    {
        var operands = new List<IBirExpression>();
        if (node.Receiver.HasValue) operands.Add(CloneCallExpression(node.Receiver.Value));
        operands.AddRange(CloneCallExpressions(arguments));
        var (key, value) = MapTypeArguments(owner, context?.Receiver(node.Receiver.OrDefault()), context?.Constraints, refs);
        operands.Add(StaticMapPreferenceValue(new TypeNode.Fqn(owner.Name, new[] { key, value }), refs));
        if (helper == "clrMapPutAll")
        {
            var source = CollectionMapBound(context?.Receiver(arguments[0]), context?.Constraints);
            operands.Add(source != null ? StaticMapPreferenceValue(source, refs)
                : new BirConstant { Type = new TypeNode.Nullable(ObjType), Value = new BirLiteral.NullLiteral() });
        }
        var call = new BirCallStatic {
            Owner = new TypeNode.Fqn("kotlin.collections.ClrMapDefaultsKt"), Method = helper,
            Sig = MapHelperTypes(helper), TypeArgs = new[] { key, value }, Args = operands.ToArray(),
        };
        BindAuthoredHelper(call, refs, context);
        if (RetToken(node) is { } result && result is not TypeNode.Tv) call.Ret = result;
        return call;
    }

    static BirCallStatic Rule3HelperCall(SourceCall node, ReferenceMetadataIndex refs, TypeNode.Fqn owner,
        string member, IBirExpression[] arguments, bool instance)
    {
        var operands = new List<IBirExpression>();
        if (instance && node.Receiver.HasValue) operands.Add(CloneCallExpression(node.Receiver.Value));
        operands.AddRange(CloneCallExpressions(arguments));
        var knownReturn = RetToken(node);
        var projectedReturn = knownReturn;
        if (instance && ForeignStarProjectionBinding.IsForeignStarType(owner, refs))
            projectedReturn ??= BirNodeType.Of(node.Graph as BirGraphNode);
        var plan = PlanRule3Helper(refs, owner, member, node.DeclarationId.OrDefault(), instance, node.Receiver.HasValue,
            arguments.Length, node.Sig.OrDefault(), node.TypeArgs.OrDefault(), knownReturn, projectedReturn,
            _localDeclarationIds, _representations);
        return new BirCallStatic {
            Owner = CopyCallType(plan.Owner), Method = plan.Method, Args = operands.ToArray(),
            DeclarationId = plan.DeclarationId == null ? default : OptionalField<string>.Of(plan.DeclarationId),
            TypeArgs = plan.TypeArgs == null ? default : OptionalField<TypeNode[]>.Of(plan.TypeArgs.Select(CopyCallType).ToArray()),
            Sig = plan.Signature == null ? default : OptionalField<TypeNode[]>.Of(plan.Signature.Select(CopyCallType).ToArray()),
            CalleeParams = plan.CalleeParams == null ? default : OptionalField<TypeNode[]>.Of(plan.CalleeParams.Select(CopyCallType).ToArray()),
            CalleeRet = plan.CalleeReturn == null ? default : OptionalField<TypeNode>.Of(CopyCallType(plan.CalleeReturn)),
            Ret = plan.Return == null ? default : OptionalField<TypeNode>.Of(CopyCallType(plan.Return)),
            ProjectedHelperReceiver = plan.ProjectedHelperReceiver is int index ? OptionalField<int>.Of(index) : default,
        };
    }

    static BirGraphValue ClrPropNode(SourceCall node, TypeNode owner, string property, int access, string member,
        IBirExpression[] arguments, string marker = null, bool forceStatic = false)
    {
        var read = (access & ClrPropRead) != 0;
        var write = (access & ClrPropWrite) != 0;
        var explicitKind = marker;
        if (explicitKind == null && node.HasIdentity(out _, out var preservedKind)) explicitKind = preservedKind;
        var writes = explicitKind is "get" or "set" ? explicitKind == "set" : read && write ? arguments.Length == 1 : write;
        var staticAxis = forceStatic || node.Static != null;
        var extension = staticAxis && marker is ("get" or "set") && arguments.Length > (writes ? 1 : 0) ? arguments[0] : null;
        var isStatic = staticAxis && marker != null && extension == null;
        var receiver = !isStatic ? CopyCallExpression(extension ?? node.Receiver.OrDefault()) : default;
        var hasIdentity = node.HasIdentity(out var sourceProperty, out var sourceKind);
        var propertyName = hasIdentity ? OptionalField<string>.Of(sourceProperty) : default;
        var propertyAccessor = hasIdentity ? OptionalField<string>.Of(sourceKind) : default;
        var super = marker == null || extension != null ? node.Super : default;
        return writes
            ? new BirPropertySet { Type = CopyCallType(owner), Name = property, Static = isStatic, Recv = receiver,
                PropertyName = propertyName, PropertyAccessor = propertyAccessor, DeclarationId = node.DeclarationId,
                Value = arguments.Length > 0 ? CopyCallExpression(extension != null ? arguments[^1] : arguments[0]) : default, Super = super }
            : new BirPropertyGet { Type = CopyCallType(owner), Name = property, Static = isStatic, Recv = receiver,
                PropertyName = propertyName, PropertyAccessor = propertyAccessor, DeclarationId = node.DeclarationId,
                Ret = RetToken(node) is { } result ? OptionalField<TypeNode>.Of(result) : default, Sty = CopyCallType(node.Sty), Super = super };
    }

    static BirGraphValue ClrCallNode(SourceCall node, TypeNode owner, string intrinsic, string member, IBirExpression[] arguments,
        ReferenceMetadataIndex refs, bool instance, int[] byref = null, TypeNode[] exactTypes = null)
    {
        var types = exactTypes?.Select(CopyCallType).ToArray() ?? InferArgTypes(node, arguments);
        if (byref != null)
            foreach (var index in byref)
                if (index >= 0 && index < types.Length && types[index] is not TypeNode.ByRef && types[index] != null)
                    types[index] = new TypeNode.ByRef(types[index]);
        if (instance && node.HasIdentity(out _, out var accessor) && arguments.Length == (accessor == "set" ? 1 : 0))
            return ClrPropNode(node, owner, intrinsic, accessor == "set" ? ClrPropWrite : ClrPropRead, member, arguments, accessor);
        var values = CloneCallExpressions(arguments);
        CoerceCharSeqArgsToString(types, values);
        var result = RetToken(node);
        var ret = result == null ? default : OptionalField<TypeNode>.Of(result);
        var typeArguments = default(OptionalField<TypeNode[]>);
        if (node.TypeArgs is { HasValue: true, Value.Length: > 0 })
        {
            var frame = refs.NullableMethodFrame(node.DeclarationId.OrDefault());
            typeArguments = (frame?.OrdinaryArguments(node.TypeArgs.Value) ?? node.TypeArgs.Value).Select(CopyCallType).ToArray();
        }
        return instance
            ? new BirNativeInstance { Type = CopyCallType(owner), Method = intrinsic, ArgTypes = types,
                Ret = ret, Sty = CopyCallType(node.Sty), Recv = CopyCallExpression(node.Receiver.OrDefault()),
                Args = values, Super = node.Super, TypeArgs = typeArguments }
            : new BirNativeStatic { Type = CopyCallType(owner), Method = intrinsic, ArgTypes = types,
                Ret = ret, Sty = CopyCallType(node.Sty), Args = values, TypeArgs = typeArguments };
    }

    static void CoerceCharSeqArgsToString(TypeNode[] types, IBirExpression[] values)
    {
        if (types == null || values == null) return;
        for (var index = 0; index < types.Length && index < values.Length; index++)
            if ((UnwrapNullableOblivious(types[index]) as TypeNode.Fqn)?.Name is { } name
                && name.Contains("dotkt$CharSequence", StringComparison.Ordinal) && values[index] != null)
            {
                values[index] = new BirCallStatic { Owner = new TypeNode.Fqn("kotlin.LibraryKt"), Method = "toString",
                    Sig = new TypeNode[] { ObjType }, Args = new[] { CloneCallExpression(values[index]) } };
                types[index] = new TypeNode.Fqn("kotlin.String");
            }
    }

    static BirGraphValue TopLevelExtensionInstance(SourceCall node, ReferenceMetadataIndex refs, string intrinsic,
        IBirExpression[] arguments, List<TypeNode> signature, CollectionHelperBinding.CollectionSourceContext context)
    {
        if (arguments.Length == 0) return null;
        var first = signature.Count > 0 ? signature[0] : null;
        TypeNode receiverType = null;
        if (context?.Receiver(arguments[0], false) is TypeNode.Fqn structured && ClrOwnerType(refs, structured) is { } physical)
            receiverType = physical;
        else if (first is TypeNode.Fqn declared && ClrOwnerType(refs, new TypeNode.Fqn(ReferenceMetadataIndex.BareOwnerFqn(declared.Name))) is { } bare)
            receiverType = bare;
        receiverType ??= first != null ? CopyCallType(first) : InferArgType(arguments[0]);
        var call = new BirNativeInstance {
            Type = CopyCallType(receiverType), Method = intrinsic,
            ArgTypes = signature.Skip(1).Select(CopyCallType).ToArray(),
            Recv = CopyCallExpression(arguments[0]), Args = CloneCallExpressions(arguments.Skip(1)),
        };
        if (RetToken(node) is { } result) call.Ret = result;
        if (refs.DeclarationSourceParameters(node.DeclarationId.OrDefault()) is { Length: > 0 } source
            && source[0] is TypeNode.Fqn { Args.Length: > 0 } sourceReceiver
            && refs.Aliases.TryGetValue(sourceReceiver.Name, out var sourceClrOwner)
            && SubstMethodTv(first, node.TypeArgs.OrDefault()) is TypeNode.Fqn declaredReceiver
            && ClrOwnerType(refs, declaredReceiver) is TypeNode.Fqn physicalReceiver
            && ClrMemberResolution.TryResolveExternalMethodForDefaults(refs, new TypeNode.Fqn(sourceClrOwner, sourceReceiver.Args),
                intrinsic, 0, false, source.Skip(1).ToArray(), out var target))
        {
            var openOwner = ClrMemberResolution.ResolveOwnerType(physicalReceiver, refs);
            var targetOwner = ClrMemberResolution.DeclaringTypeRef(target, openOwner, physicalReceiver.Args) as TypeNode.Fqn
                ?? throw new InvalidOperationException("Intrinsic extension has no constructed declaring owner");
            TypeNode Close(TypeNode type) => SupertypeGraph.SubstOwnerTvs(type, targetOwner.Args ?? Array.Empty<TypeNode>());
            var parameters = target.GetParameters().Select(parameter => Close(ClrMemberResolution.MemberSigOf(parameter.ParameterType))).ToArray();
            call.Type = CopyCallType(physicalReceiver);
            call.ArgTypes = signature.Skip(1).Select(type => SubstMethodTv(type, node.TypeArgs.OrDefault())).ToArray();
            return (BirGraphValue)IntrinsicExtensionRepresentation.Adapt(call, parameters, Close(ClrMemberResolution.MemberSigOf(target.ReturnType)));
        }
        return call;
    }

    static BirGraphValue ExactIntrinsicCall(SourceCall node, TypeNode owner, string member, IBirExpression[] arguments,
        bool instance, ExactClrMemberBinding binding, ReferenceMetadataIndex refs,
        CollectionHelperBinding.CollectionSourceContext context, string ownerToken)
    {
        if (binding.CountStart < 0 && binding.CountEnd < 0)
            return Constrainify(ClrCallNode(node, owner, binding.Intrinsic, member, arguments, refs, instance,
                binding.ByrefPositions), node, refs, context, ownerToken);
        if (binding.CountStart < 0 || binding.CountEnd <= binding.CountStart || binding.CountEnd >= arguments.Length)
            throw new InvalidDataException($"invalid CLR count adapter on {ownerToken}.{member}: start={binding.CountStart}, "
                + $"end={binding.CountEnd}, args={arguments.Length}");
        var bindings = new List<BirCallBinding>();
        BirBindingReference Bind(IBirExpression expression, TypeNode type, bool address = false)
        {
            var id = CallEvalLowering.FreshBindingId();
            bindings.Add(new BirCallBinding {
                Id = id, Expr = CopyCallExpression(expression),
                Stable = ValueStability.IsReReadable((expression as BirGraphNode)?.Tag,
                    expression is BirEnumValue { Underlying.HasValue: true, PhysicalValue.HasValue: true }),
                Kind = address ? OptionalField<string>.Of("address") : default,
                Type = type == null ? default : OptionalField<TypeNode>.Of(CopyCallType(type)),
            });
            return new BirBindingReference { Id = id };
        }
        var receiver = instance ? Bind(node.Receiver.OrDefault(), owner) : null;
        var exactTypes = InferArgTypes(node, arguments);
        var physicalArguments = CloneCallExpressions(arguments);
        CoerceCharSeqArgsToString(exactTypes, physicalArguments);
        var adapted = new IBirExpression[physicalArguments.Length];
        for (var index = 0; index < physicalArguments.Length; index++)
        {
            TypeNode actual = null;
            if (physicalArguments[index] is BirLocal { Name.HasValue: true } local)
                context?.Variables.TryGetValue(local.Name.Value, out actual);
            actual ??= context?.Receiver(physicalArguments[index], true);
            actual ??= TypedCallEvalLowering.StaticTypeOf((BirGraphValue)physicalArguments[index]);
            adapted[index] = Bind(physicalArguments[index], index < exactTypes.Length ? exactTypes[index] : actual,
                binding.ByrefPositions.Contains(index));
        }
        adapted[binding.CountEnd] = new BirBinaryOperation {
            Op = "-", Lhs = CopyCallExpression(adapted[binding.CountEnd]), Rhs = CopyCallExpression(adapted[binding.CountStart]),
        };
        var source = node.Static != null ? new SourceCall((BirCallStatic)node.Static.DeepClone())
            : new SourceCall((BirCallInstance)node.Instance.DeepClone());
        if (instance) source.Receiver = receiver;
        var lowered = Constrainify(ClrCallNode(source, owner, binding.Intrinsic, member, adapted, refs, instance,
            binding.ByrefPositions, exactTypes), node, refs, context, ownerToken);
        var (statements, replacements) = TypedCallEvalLowering.Materialise(bindings.ToArray(), new[] { lowered },
            $"@ClrCountFromExclusiveEnd call {ownerToken}.{member}", _isValue);
        var result = TypedCallEvalLowering.Substitute(lowered, replacements);
        AssertNoPlanVocabulary(result);
        if (statements.Length == 0) return result;
        var block = new BirValueBlock { Stmts = statements, Result = OptionalField<IBirExpression>.Of((IBirExpression)result) };
        if (RetToken(node) is { } returnType) block.Type = returnType;
        return block;
    }
}
