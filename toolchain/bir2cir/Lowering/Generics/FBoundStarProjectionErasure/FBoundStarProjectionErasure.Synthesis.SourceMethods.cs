using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    static BirFunctionDeclaration InterfaceSlotSource(BirFunctionDeclaration method, string replacement,
        string semanticOwner, CarrierRealizationContext context)
    {
        var slot = new BirFunctionDeclaration {
            ExistentialCallable = ExistentialCallableRole.Slot,
            HasNativeOverrideSlot = method.HasNativeOverrideSlot || method.RequiresClrOverride.OrDefault(),
            Name = replacement ?? method.Name.OrDefault(), Static = false, Override = false,
            Virtual = true, Abstract = true, ObjectOverride = false, Vis = "public",
            Params = EraseParamsSource(method.Params.OrDefault(), method.Name.OrDefault() ?? "<unnamed>", context),
            Ret = BirGraphValue.CopyType(EraseOwnerTv(method.Ret.OrDefault() ?? new TypeNode.Fqn("kotlin.Unit"), context.Owners, context.References)),
            Body = Array.Empty<IBirStatement>(), Attrs = Array.Empty<BirAttribute>(),
        };
        if (method.TypeParams.OrEmpty().Length > 0)
            slot.TypeParams = EraseOwnerTypeParamConstraintsSource(method, semanticOwner, context);
        if (replacement == null && method.ExplicitClrName.HasValue) slot.ExplicitClrName = method.ExplicitClrName;
        if (method.DeclarationId.OrDefault() is string identity)
        {
            slot.DeclarationId = ExistentialSlotIdentity(identity, semanticOwner);
            slot.DeclarationSourceName = method.DeclarationSourceName.HasValue ? method.DeclarationSourceName : method.Name;
        }
        if (KotlinPropertyAccessors.TryIdentity(method, out var property, out var accessor))
        {
            var association = method.PropertyAssociation.OrDefault()
                ?? throw new InvalidOperationException($"property accessor '{method.Name.OrDefault()}' has no source association");
            slot.PropertyName = property; slot.PropertyAccessor = accessor; slot.PropertyAssociation = association;
            slot.KotlinPropertyAccessorCarrier = new EventPropertySourceFacts { Name = property, Kind = accessor, Association = association };
        }
        else if (replacement != null || method.DeclarationId.HasValue)
        {
            slot.ExistentialSourceMember = method.Name;
            RoundtripMetadata.AddSourceMethodIdentity(slot, method.DeclarationSourceName.OrDefault() ?? method.Name.OrDefault()
                ?? throw new InvalidOperationException("existential interface slot has no source method name"), method.DeclarationId.OrDefault());
        }
        if (method.Mods.HasValue) slot.Mods = (BirModifiers)method.Mods.Value.DeepClone();
        CopyResultFactsSource(method, slot, context);
        return slot;
    }

    static BirFunctionDeclaration BridgeMethodSource(Owner owner, BirFunctionDeclaration method,
        CarrierRealizationContext context, string bridgeName = null, TypeNode.Fqn callOwner = null,
        string slotMemberName = null, BirTypeParameter[] slotTypeParams = null, SourceReferenceContract referenceContract = null)
    {
        var originalParams = method.Params.OrEmpty();
        var display = owner.Name + "." + (method.Name.OrDefault() ?? "<unnamed>");
        var parameters = EraseParamsSource(originalParams, display, context);
        if (referenceContract != null) RestoreReferenceParametersSource(originalParams, parameters);
        var arguments = new List<IBirExpression>();
        var signature = new List<TypeNode>();
        for (var index = 0; index < originalParams.Length; index++)
        {
            var parameter = originalParams[index];
            var type = RequiredParamTypeSource(parameter, index, display);
            IBirExpression value = new BirLocal { Name = parameter.Name.OrDefault() ?? "p" + index };
            if (ContainsOwnerTv(type) && SignatureValueTypes.Of(type) is not TypeNode.ByRef)
                value = new BirCast { Type = BirGraphValue.CopyType(type), E = OptionalField<IBirExpression>.Of(value), _exactBridgeCast = true };
            arguments.Add(value); signature.Add(BirGraphValue.CopyType(type));
        }
        var originalRet = method.Ret.OrDefault() ?? new TypeNode.Fqn("kotlin.Unit");
        var call = new BirCallInstance {
            OwnerType = BirGraphValue.CopyType(callOwner ?? new TypeNode.Fqn(owner.Name,
                Enumerable.Range(0, owner.Arity).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray())),
            Virtual = method.Virtual.OrDefault() || method.Abstract.OrDefault(),
            Recv = OptionalField<IBirExpression>.Of(new BirThis()), Method = method.Name,
            Sig = signature.ToArray(), Ret = BirGraphValue.CopyType(originalRet), Args = arguments.ToArray(),
            _exactExistentialBridgeOwnerCall = true, ClrOwnerResolved = true,
        };
        if (method.DeclarationId.HasValue) call.DeclarationId = method.DeclarationId;
        if (method.TypeParams.OrEmpty().Length > 0)
            call.TypeArgs = Enumerable.Range(0, method.TypeParams.Value.Length).Select(index => (TypeNode)new TypeNode.Tv("method", index)).ToArray();
        if (method.Mods.OrDefault()?.Suspend.OrDefault() == true) call.SuspendCall = true;
        var result = BirGraphValue.CopyType(EraseOwnerTv(originalRet, context.Owners, context.References));
        var bridge = new BirFunctionDeclaration {
            ExistentialCallable = ExistentialCallableRole.Forwarder,
            HasNativeOverrideSlot = method.HasNativeOverrideSlot || method.RequiresClrOverride.OrDefault(),
            Name = bridgeName ?? StarMethodNameSource(context.Definition(owner), method),
            Static = false, Override = false, Virtual = true, Abstract = false, ObjectOverride = false,
            Vis = "private", Params = parameters, Ret = result,
            Body = new IBirStatement[] { originalRet is TypeNode.Fqn { Name: "kotlin.Unit" }
                ? new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(call) }
                : new BirReturn { Value = OptionalField<IBirExpression>.Of(call) } },
            Attrs = Array.Empty<BirAttribute>(), Generated = true, PhysicalSlotBridge = true,
        };
        if (slotTypeParams is { Length: > 0 }) bridge.TypeParams = slotTypeParams.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
        else if (method.TypeParams.OrEmpty().Length > 0) bridge.TypeParams = method.TypeParams.Value.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
        var implementation = new BirMethodImpl {
            Owner = BirGraphValue.CopyType(referenceContract?.Owner ?? new TypeNode.Fqn(owner.ErasedName)),
            Member = slotMemberName ?? bridge.Name.OrDefault(), Arity = method.TypeParams.OrEmpty().Length,
            Params = parameters.Select(FunctionSignatureIdentity.SignatureType).ToArray(), Ret = BirGraphValue.CopyType(result),
        };
        if (slotTypeParams is { Length: > 0 }) implementation.TypeParams = slotTypeParams.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
        bridge.ClrInterfaceImpls = new[] { implementation };
        if (context.Definition(owner).Kind.OrDefault() == "interface") bridge.ClrInterfaceSlotBridge = true;
        if (method.Mods.HasValue) bridge.Mods = (BirModifiers)method.Mods.Value.DeepClone();
        CopyResultFactsSource(method, bridge, context);
        return bridge;
    }

    static TypeNode RequiredParamTypeSource(BirParameter parameter, int index, string display) => parameter.Type.OrDefault()
        ?? throw new NotSupportedException($"bir2cir: star-projection erasure: parameter `{parameter.Name.OrDefault() ?? "p" + index}` of `{display}` carries no type — an earlier lowering dropped it.");

    static BirParameter[] EraseParamsSource(BirParameter[] parameters, string display, CarrierRealizationContext context)
    {
        return (parameters ?? Array.Empty<BirParameter>()).Select((parameter, index) => {
            var copy = (BirParameter)parameter.DeepClone();
            var original = RequiredParamTypeSource(parameter, index, display);
            var erased = EraseOwnerTv(original, context.Owners, context.References);
            if (TypeNode.ToJson(erased) != TypeNode.ToJson(original) && !copy.KotlinType.HasValue)
                copy.KotlinType = BirGraphValue.CopyType(original);
            copy.Type = BirGraphValue.CopyType(erased);
            if (copy.FunctionSignatureType.HasValue)
                copy.FunctionSignatureType = BirGraphValue.CopyType(EraseOwnerTv(copy.FunctionSignatureType.Value, context.Owners, context.References));
            DeclarationSignatureIdentity.RewriteParameterFacts(copy, type => EraseOwnerTv(type, context.Owners, context.References));
            return copy;
        }).ToArray();
    }

    static BirTypeParameter[] EraseOwnerTypeParamConstraintsSource(BirFunctionDeclaration method, string semanticOwner,
        CarrierRealizationContext context)
    {
        var ownerParameters = context.Owners.TryGetValue(semanticOwner, out var owner) ? context.Definition(owner).TypeParams.OrDefault() : null;
        var binding = context.Bindings.GetCapturedSource(method);
        return method.TypeParams.Value.Select((parameter, index) => {
            var copy = (BirTypeParameter)parameter.DeepClone();
            if (!parameter.Constraints.HasValue) return copy;
            var erased = new List<TypeNode>();
            foreach (var original in parameter.Constraints.Value)
            {
                var physical = context.NativeProofs.IsPhysicalConstraint(binding, index, original);
                var bound = context.Projection.Rewrite(original, boundDeclaration: physical);
                if (ContainsOwnerTv(bound) || OwnerConstrainedMethodLowering.HasConstructedDependency(bound, "method", index,
                    method.NullableRepresentationFrame.OrDefault(), context.References))
                {
                    if (PhysicalBoundClassifier(bound, context.Projection) is { } classifier && !erased.Contains(classifier)) erased.Add(classifier);
                    foreach (var consequence in ContainsOwnerTv(bound)
                        ? OwnerConstrainedMethodLowering.IndependentBounds(bound, ownerParameters) : Enumerable.Empty<TypeNode>())
                        if (!IsObjectish(consequence) && !erased.Contains(consequence)) erased.Add(consequence);
                    continue;
                }
                var result = physical ? bound : EraseOwnerTv(bound, context.Owners, context.References);
                if (!IsObjectish(result)) erased.Add(result);
            }
            foreach (var proof in context.NativeProofs.Bounds(binding, index).Where(proof => !ContainsOwnerTv(proof)))
                if (!erased.Any(row => context.References.PhysicalConstraintType(row) == context.References.PhysicalConstraintType(proof))) erased.Add(proof);
            copy.Constraints = erased.DistinctBy(context.References.PhysicalConstraintType).Select(BirGraphValue.CopyType).ToArray();
            return copy;
        }).ToArray();
    }

    static void CopyResultFactsSource(BirFunctionDeclaration source, BirFunctionDeclaration target, CarrierRealizationContext context)
    {
        OptionalField<TypeNode> Project(OptionalField<TypeNode> fact) => fact.HasValue
            ? OptionalField<TypeNode>.Of(BirGraphValue.CopyType(EraseOwnerTv(fact.Value, context.Owners, context.References))) : default;
        target.SuspendRet = Project(source.SuspendRet);
        target.SuspendResult = Project(source.SuspendResult);
        target.NullableGenericRet = Project(source.NullableGenericRet);
        target.NullableGenericSuspendRet = Project(source.NullableGenericSuspendRet);
        target.RetSuspendFnTypePre = Project(source.RetSuspendFnTypePre);
        target.RetKotlinType = Project(source.RetKotlinType);
    }
}
