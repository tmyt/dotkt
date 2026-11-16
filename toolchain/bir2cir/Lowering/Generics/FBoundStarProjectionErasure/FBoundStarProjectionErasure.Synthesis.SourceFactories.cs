using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    static SourceInnerFactory InnerConstructorFactorySource(Owner outer, Owner inner, BirConstructor constructor,
        int ordinal, CarrierRealizationContext context)
    {
        var captured = inner.OuterCount;
        if (captured <= 0 || captured > inner.Arity)
            throw new InvalidOperationException($"inner type '{inner.Name}' has an invalid captured generic frame");
        var original = constructor.Params.OrEmpty();
        if (original.Length == 0 || original[0].Type.OrDefault() is not TypeNode.Fqn hidden || hidden.Name != outer.Name)
            throw new InvalidOperationException($"inner constructor '{inner.Name}/{original.Length}' has no exact '{outer.Name}' outer slot");
        var name = InnerConstructorFactoryName(inner, ordinal);
        var parameters = original.Skip(1).Select(parameter => (BirParameter)parameter.DeepClone()).ToArray();
        var exact = original.Skip(1).Select((parameter, index) =>
            RequiredParamTypeSource(parameter, index + 1, inner.Name + ".<init>")).ToArray();
        var direct = DirectOwnerDependentInnerParametersSource(inner, captured, context);
        var witnesses = direct.ToDictionary(index => index, _ => (TypeNode)new TypeNode.Fqn("kotlin.Nothing"));
        var positions = Enumerable.Range(captured, inner.Arity - captured).Where(index => !direct.Contains(index)).ToArray();
        var methodIndex = positions.Select((source, index) => (source, index)).ToDictionary(pair => pair.source, pair => pair.index);
        TypeNode Project(TypeNode type) => ProjectInnerFactoryType(type, captured, witnesses, methodIndex);
        for (var index = 0; index < exact.Length; index++) exact[index] = Project(exact[index]);
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var physical = SignatureValueTypes.Of(exact[index]) is TypeNode.ByRef ? exact[index]
                : EraseOwnerTv(exact[index], context.Owners, context.References);
            parameter.Type = BirGraphValue.CopyType(physical);
            if (parameter.FunctionSignatureType.HasValue)
                parameter.FunctionSignatureType = BirGraphValue.CopyType(SignatureValueTypes.Of(exact[index]) is TypeNode.ByRef
                    ? Project(parameter.FunctionSignatureType.Value)
                    : EraseOwnerTv(Project(parameter.FunctionSignatureType.Value), context.Owners, context.References));
            if (!physical.Equals(exact[index]) && !parameter.KotlinType.HasValue) parameter.KotlinType = BirGraphValue.CopyType(exact[index]);
        }
        var typeParameters = InnerFactoryTypeParamsSource(inner, captured, direct, witnesses, methodIndex, context);
        var result = new TypeNode.Fqn(inner.ErasedName);
        var declaration = constructor.DeclarationId.OrDefault()
            ?? throw new InvalidOperationException($"Inner constructor '{inner.Name}' has no declaration identity");
        var typeArgs = Enumerable.Range(captured, inner.Arity - captured)
            .Select(index => direct.Contains(index) ? InnerFactoryBottomTypeArgument : methodIndex[index]).ToArray();
        var slot = new BirFunctionDeclaration {
            Name = name, Static = false, Override = false, Virtual = true, Abstract = true,
            ObjectOverride = false, Vis = "public", Params = parameters.Select(parameter => (BirParameter)parameter.DeepClone()).ToArray(),
            Ret = result, Body = Array.Empty<IBirStatement>(), Attrs = Array.Empty<BirAttribute>(), Generated = true,
            ExistentialInnerConstructorFactory = new BirInnerConstructorFactory {
                Inner = inner.Name, Declaration = declaration, TypeArgs = typeArgs.ToArray(),
            },
        };
        if (typeParameters.Length > 0) slot.TypeParams = typeParameters.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
        var arguments = new List<IBirExpression> { new BirThis() };
        for (var index = 0; index < parameters.Length; index++)
        {
            IBirExpression value = new BirLocal { Name = parameters[index].Name.OrDefault() ?? "p" + index };
            if (!exact[index].Equals(parameters[index].Type.Value))
                value = new BirCast { Type = BirGraphValue.CopyType(exact[index]), E = OptionalField<IBirExpression>.Of(value), _exactBridgeCast = true };
            arguments.Add(value);
        }
        var construction = new BirNew {
            Type = new TypeNode.Fqn(inner.Name, Enumerable.Range(0, inner.Arity).Select(index => Project(new TypeNode.Tv("type", index))).ToArray()),
            ConstructorDeclarationId = declaration,
            ArgTypes = original.Select(parameter => BirGraphValue.CopyType(Project(RequiredParamTypeSource(parameter, 0, inner.Name + ".<init>")))).ToArray(),
            // This selection signature stays in the constructor's declaration
            // frame, independently of the factory's projected method frame.
            MemberSignature = original.Select(parameter => BirGraphValue.CopyType(parameter.FunctionSignatureType.OrDefault()
                ?? RequiredParamTypeSource(parameter, 0, inner.Name + ".<init>"))).ToArray(),
            Args = arguments.ToArray(),
        };
        var implementation = new BirMethodImpl {
            Owner = new TypeNode.Fqn(outer.ErasedName), Member = name, Arity = typeParameters.Length,
            Params = parameters.Select(FunctionSignatureIdentity.SignatureType).ToArray(), Ret = result,
        };
        if (typeParameters.Length > 0) implementation.TypeParams = typeParameters.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
        var bridge = new BirFunctionDeclaration {
            Name = name, Static = false, Override = false, Virtual = true, Abstract = false, ObjectOverride = false,
            Vis = "private", Params = parameters, Ret = result,
            Body = new IBirStatement[] { new BirReturn { Value = OptionalField<IBirExpression>.Of(construction) } },
            Attrs = Array.Empty<BirAttribute>(), Generated = true, PhysicalSlotBridge = true,
            ClrInterfaceImpls = new[] { implementation },
        };
        if (typeParameters.Length > 0) bridge.TypeParams = typeParameters;
        return new(outer, inner, context.Bindings.GetCapturedSource(constructor), slot, bridge, typeArgs);
    }

    static BirTypeParameter[] InnerFactoryTypeParamsSource(Owner inner, int captured, IReadOnlySet<int> direct,
        IReadOnlyDictionary<int, TypeNode> witnesses, IReadOnlyDictionary<int, int> methodIndex, CarrierRealizationContext context)
    {
        return context.Definition(inner).TypeParams.OrEmpty().Select((parameter, index) => (parameter, index))
            .Where(row => row.index >= captured && !direct.Contains(row.index)).Select(row => {
                var copy = (BirTypeParameter)row.parameter.DeepClone();
                if (copy.Constraints.HasValue)
                    copy.Constraints = copy.Constraints.Value.Where(type => InnerConstraintIsRepresentable(row.index, type, captured, direct))
                        .Select(type => EraseOwnerTv(ProjectInnerFactoryType(type, captured, witnesses, methodIndex), context.Owners, context.References))
                        .Where(type => !IsObjectish(type)).Select(BirGraphValue.CopyType).ToArray();
                return copy;
            }).ToArray();
    }
}
