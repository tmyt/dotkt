using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static BirGraphValue BindSourceNormalizedNode(BirGraphValue node, SourceRewriteState state)
    {
        node = BindProjectedArrayReadSource(node, state);
        node = BindProjectedArrayGenericCallSource(node, state);
        node = BindProjectedFieldStorageSource(node, state);
        return BindInheritedStarMemberSource(node, state);
    }

    internal static BirGraphValue BindSourceConsumerNode(BirGraphValue node, SourceRewriteState state)
    {
        node = BindProjectedArrayReadSource(node, state);
        node = BindProjectedArrayGenericCallSource(node, state);
        BindCarrierArgumentMergeSource(node, state);
        node = BindCarrierPropertyResultSource(node, state);
        node = BindDeclarationCarrierResultSource(node, state);
        return BindInheritedStarMemberSource(node, state);
    }

    static void BindProjectedConstraintMemberSource(BirGraphValue node,
        IReadOnlyList<BirTypeParameter> typeParameters, IReadOnlyList<BirTypeParameter> methodParameters,
        SourceRewriteState state)
    {
        if (node is not (BirCallInstance or BirNewBoundDelegate)) return;
        var call = new SourceMemberUse(node);
        if (call.Owner is not TypeNode.Fqn selected
            || SourceCarrierExpressionType(call.Receiver as BirGraphValue) is not TypeNode.Tv receiver) return;
        var declarations = receiver.Scope == "type" ? typeParameters : receiver.Scope == "method" ? methodParameters : null;
        if (declarations == null || receiver.I < 0 || receiver.I >= declarations.Count) return;
        var matches = new Dictionary<string, TypeNode.Fqn>(StringComparer.Ordinal);
        var seen = new HashSet<(string, int)>();
        void Visit(TypeNode type)
        {
            if (type is TypeNode.Tv variable)
            {
                var parameters = variable.Scope == "type" ? typeParameters : variable.Scope == "method" ? methodParameters : null;
                if (parameters == null || variable.I < 0 || variable.I >= parameters.Count || !seen.Add((variable.Scope, variable.I))) return;
                foreach (var constraint in parameters[variable.I].Constraints.OrEmpty().Concat(parameters[variable.I].ErasedInnerConstraints.OrEmpty())) Visit(constraint);
                return;
            }
            if (type is not TypeNode.Fqn bound) return;
            var arguments = SameDeclarationOwner(bound.Name, selected.Name) ? bound.Args : state.ProjectArguments(bound, selected.Name);
            if (arguments == null || !arguments.Any(ContainsExistentialProjection)) return;
            var view = new TypeNode.Fqn(selected.Name, arguments.ToArray());
            matches.TryAdd(SupertypeGraph.TypeKey(view), view);
        }
        Visit(receiver);
        if (matches.Count > 1) throw new InvalidOperationException($"projected generic constraint gives ambiguous owner views for `{selected.Name}`");
        if (matches.Count == 1) call.Owner = matches.Values.Single();
    }

    static void BindLocalEventAccessorSource(BirGraphValue node, SourceRewriteState state)
    {
        var call = node switch {
            BirEventAdd value when value.LocalAccessor.OrDefault() => new BirCallInstance {
                OwnerType = value.AccessorOwner, DeclarationId = value.DeclarationId, Method = value.Accessor,
                Sig = value.Sig, Recv = value.Recv, Args = new[] { value.Handler.OrDefault() }, Ret = new TypeNode.Fqn("kotlin.Unit") },
            BirEventRemove value when value.LocalAccessor.OrDefault() => new BirCallInstance {
                OwnerType = value.AccessorOwner, DeclarationId = value.DeclarationId, Method = value.Accessor,
                Sig = value.Sig, Recv = value.Recv, Args = new[] { value.Handler.OrDefault() }, Ret = new TypeNode.Fqn("kotlin.Unit") },
            _ => null,
        };
        if (call == null) return;
        var selected = (BirCallInstance)BindInheritedStarMemberSource(call, state, alignResult: false);
        // This is the already-selected event edge, not an invocation to insert.
        // The event continues owning its original receiver and handler.
        if (node is BirEventAdd add) {
            add.AccessorOwner = selected.OwnerType; add.Accessor = selected.Method;
            add.Sig = selected.Sig; add.DeclarationId = selected.DeclarationId;
        } else {
            var remove = (BirEventRemove)node;
            remove.AccessorOwner = selected.OwnerType; remove.Accessor = selected.Method;
            remove.Sig = selected.Sig; remove.DeclarationId = selected.DeclarationId;
        }
    }

    static BirGraphValue BindDeclarationCarrierResultSource(BirGraphValue node, SourceRewriteState state)
    {
        if (node is not (BirCallStatic or BirCallInstance) || node.DeclarationIdentityFact.OrDefault() is not string identity
            || SourceSelectedOwner(node) is not TypeNode.Fqn owner) return node;
        var arguments = node.ApplicationTypeArguments.OrEmpty();
        var declared = state.Demand.Preparation.Bindings.SourceLocalDeclarations.TryGetValue((owner.Name, identity), out var selected)
            ? (selected.Source as BirFunctionDeclaration)?.Ret.OrDefault()
            : state.References.DeclarationIdentityReturn(identity, owner.Name, arguments.Length, node is BirCallStatic);
        if (declared == null) return node;
        var result = SubstituteDeclarationTypeArguments(declared, owner.Args ?? Array.Empty<TypeNode>(), arguments);
        var semantic = node.ResultTypeStamp;
        return IsExistentialPhysicalCarrier(result, state.Owners, state.References) && semantic != null && result != semantic
            ? state.AlignExistential((IBirExpression)node, result, semantic) : node;
    }

    static BirGraphValue BindCarrierPropertyResultSource(BirGraphValue node, SourceRewriteState state)
    {
        if (node is not BirCallInstance call || !new SourceMemberUse(call).Property(out var property, out var accessor)
            || accessor != "get" || call.OwnerType.OrDefault() is not TypeNode.Fqn owner
            || state.Demand.Preparation.Bindings.SourceDefinitions.ContainsKey(owner.Name) || !state.References.HasDotKtOwner(owner.Name)
            || !call.Sig.HasValue || !state.References.TrySelectedSourceMethodDeclaration(owner.Name, property, 0,
                call.Sig.Value, call.ResultTypeStamp, owner.Args ?? Array.Empty<TypeNode>(), Array.Empty<BirTypeParameter>(),
                Array.Empty<TypeNode>(), out var selected, accessor)) return node;
        var result = SubstituteDeclarationTypeArguments(selected.PhysicalReturn, owner.Args ?? Array.Empty<TypeNode>(), Array.Empty<TypeNode>());
        var semantic = call.ResultTypeStamp;
        return IsExistentialPhysicalCarrier(result, state.Owners, state.References) && semantic != null && !result.Equals(semantic)
            ? state.AlignExistential(call, result, semantic) : node;
    }

    static void BindCarrierArgumentMergeSource(BirGraphValue node, SourceRewriteState state)
    {
        var signature = node switch {
            BirCallStatic value => value.Sig.OrDefault() ?? value.ShapeTypes.OrDefault() ?? value.ResolvedMemberParams.OrDefault(),
            BirCallInstance value => value.Sig.OrDefault() ?? value.ShapeTypes.OrDefault() ?? value.ResolvedMemberParams.OrDefault(),
            BirGenericStatic value => value.ResolvedMemberParams.OrDefault(),
            BirGenericInstance value => value.ResolvedMemberParams.OrDefault(),
            BirNew value => value.ResolvedMemberParams.OrDefault(),
            _ => null,
        };
        var arguments = SourceArguments(node);
        if (signature == null || arguments == null || signature.Length != arguments.Length) return;
        var owner = node switch {
            BirGenericStatic value => value.Type.OrDefault() as TypeNode.Fqn,
            BirGenericInstance value => value.Type.OrDefault() as TypeNode.Fqn,
            BirNew value => value.Type.OrDefault() as TypeNode.Fqn,
            _ => SourceSelectedOwner(node) as TypeNode.Fqn,
        };
        var methodArguments = node.ApplicationTypeArguments.OrEmpty();
        for (var index = 0; index < arguments.Length; index++)
            if (arguments[index] is BirConditional or BirValueBlock
                && StripSourceNullability(SourceCarrierExpressionType((BirGraphValue)arguments[index])) is TypeNode.Fqn semantic
                && SubstituteDeclarationTypeArguments(signature[index], owner?.Args ?? Array.Empty<TypeNode>(), methodArguments) is TypeNode.Fqn physical
                && state.Projection.TryExistentialCarrier(semantic.Name, out var carrier) && physical.Name == carrier
                && IsExistentialPhysicalCarrier(physical, state.Owners, state.References))
                arguments[index] = RetypeSourceCovariantResult(arguments[index], semantic, physical, state);
    }
}
