using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Saved executable occurrences are instantiated at their use site. Declaration
// descriptors keep their own generic frame; applications use one simultaneous
// substitution. Schema-owned roles identify the edge, not a property-name scan.
static partial class TypedExecutableFrames
{
    internal static BirGraphValue Prebind(BirGraphValue root)
    {
        if (root == null || root is IBirExecutablePayload) return root;
        switch (root)
        {
            case BirNewClosure { SynthClass.HasValue: true, TypeArgs.HasValue: true } closure:
            {
                var payload = (BirClosureDefinition)Prebind(closure.SynthClass.Value);
                if (!payload._syntheticFrameBound.HasValue)
                    payload = ((BirClosureDefinition)Rebind(payload, closure.TypeArgs.Value)) with { _syntheticFrameBound = true };
                return closure with { SynthClass = payload };
            }
            case BirNewSam { SynthClass.HasValue: true, TypeArgs.HasValue: true } sam:
            {
                var payload = (BirClassDefinition)Prebind(sam.SynthClass.Value);
                if (!payload._syntheticFrameBound.HasValue)
                    payload = ((BirClassDefinition)Rebind(payload, sam.TypeArgs.Value)) with { _syntheticFrameBound = true };
                return sam with { SynthClass = payload };
            }
            default:
                return root.RewriteChildren(Prebind);
        }
    }

    internal static BirGraphValue PrepareSynthetic(BirGraphValue source, TypeNode[] arguments)
    {
        var (name, parameters, bound) = source switch {
            BirClosureDefinition value => (value.Name.OrDefault(), value.TypeParams.OrEmpty(), value._syntheticFrameBound),
            BirClassDefinition value => (value.Name.OrDefault(), value.TypeParams.OrEmpty(), value._syntheticFrameBound),
            _ => throw new InvalidOperationException("Unaccounted synthetic frame owner: " + source.GetType().Name),
        };
        if (!bound.HasValue) return Rebind(source, arguments);
        var clone = source.DeepClone();
        if (clone is BirClosureDefinition closure) closure._syntheticFrameBound = default;
        if (clone is BirClassDefinition type) type._syntheticFrameBound = default;
        if (parameters.Length != 0 && arguments?.Length != parameters.Length)
            throw new InvalidOperationException(
                $"generic synthetic class `{name}` has {parameters.Length} type params but {arguments?.Length ?? 0} construction type args");
        return clone;
    }

    internal static BirGraphValue Rebind(BirGraphValue source, TypeNode[] arguments)
    {
        var (name, arity) = source switch {
            BirClosureDefinition value => (value.Name.OrDefault(), value.TypeParams.OrEmpty().Length),
            BirClassDefinition value => (value.Name.OrDefault(), value.TypeParams.OrEmpty().Length),
            _ => throw new InvalidOperationException("Unaccounted synthetic frame owner: " + source.GetType().Name),
        };
        var clone = source.DeepClone();
        if (arity == 0) return clone;
        if (arguments == null || arguments.Length != arity)
            throw new InvalidOperationException(
                $"generic synthetic class `{name}` has {arity} type params but {arguments?.Length ?? 0} construction type args");
        var positions = new Dictionary<(string Scope, int Index), int>();
        for (var index = 0; index < arguments.Length; index++)
            if (arguments[index] is TypeNode.Tv variable)
                positions.TryAdd((variable.Scope, variable.I), index);

        return Walk(clone);
        BirGraphValue Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return node;
            return node.RewriteGenericFrame(
                (role, type) => Rebinds(role) ? RewriteType(type, variable =>
                    positions.TryGetValue((variable.Scope, variable.I), out var position)
                        ? new TypeNode.Tv("type", position) : null) : type,
                (role, child) => Rebinds(role) ? Walk(child) : child);
        }
    }

    static bool Rebinds(BirGenericFrameRole role) => role is not (
        BirGenericFrameRole.ReferencedDeclaration or BirGenericFrameRole.SyntheticDeclaration
        or BirGenericFrameRole.SyntheticApplication or BirGenericFrameRole.FrozenSourceFact);

    static bool Substitutes(BirGenericFrameRole role) => role is not (
        BirGenericFrameRole.ReferencedDeclaration or BirGenericFrameRole.ResolvedDeclaration
        or BirGenericFrameRole.OverrideDeclaration or BirGenericFrameRole.SharedCaptureDeclaration
        or BirGenericFrameRole.LocalDeclaration or BirGenericFrameRole.FrozenSourceFact
        or BirGenericFrameRole.StorageSourceType);

    internal static BirGraphValue Substitute(BirGraphValue root, TypeNode[] methodArgs, int arity,
        TypeNode[] ownerArgs = null, bool typeScope = true)
    {
        if (root == null || root is IBirExecutablePayload) return root;
        methodArgs ??= Array.Empty<TypeNode>();
        if (root is BirNewSuspendLambda suspend
            && (suspend.TypeFrame.OrDefault() != "dense" || suspend._suspendDeclarationTypeArgs.HasValue))
        {
            // Its body/constraints/capture descriptors still name the authored
            // declaration. Only construction facts belong to the importing site.
            var declaration = suspend._suspendDeclarationTypeArgs;
            if (!declaration.HasValue && suspend.TypeArgs.HasValue)
                declaration = suspend.TypeArgs.Value.Select(CopyType).ToArray();
            return suspend with {
                _suspendDeclarationTypeArgs = declaration,
                TypeArgs = suspend.TypeArgs.HasValue ? OptionalField<TypeNode[]>.Of(suspend.TypeArgs.Value.Select(type =>
                    SubstituteType(type, methodArgs, arity, ownerArgs, typeScope)).ToArray()) : suspend.TypeArgs,
                CapValues = suspend.CapValues.HasValue ? OptionalField<IBirExpression[]>.Of(suspend.CapValues.Value.Select(value =>
                    value == null ? null : (IBirExpression)Substitute((BirGraphValue)value, methodArgs, arity, ownerArgs, typeScope)).ToArray())
                    : suspend.CapValues,
                FuncType = suspend.FuncType.HasValue ? OptionalField<TypeNode>.Of(
                    SubstituteType(suspend.FuncType.Value, methodArgs, arity, ownerArgs, typeScope)) : suspend.FuncType,
                Sty = suspend.Sty.HasValue ? OptionalField<TypeNode>.Of(
                    SubstituteType(suspend.Sty.Value, methodArgs, arity, ownerArgs, typeScope)) : suspend.Sty,
            };
        }
        // A dense suspend body owns its class frame, as does a synthClass edge.
        // Method substitution still applies unless that edge is a declaration.
        var descendTypeScope = typeScope && root is not BirNewSuspendLambda;
        return root.RewriteGenericFrame(
            (role, type) => Substitutes(role)
                ? SubstituteType(type, methodArgs, arity, ownerArgs, descendTypeScope) : type,
            (role, child) => Substitutes(role) ? Substitute(child, methodArgs, arity, ownerArgs,
                descendTypeScope && role != BirGenericFrameRole.SyntheticDeclaration) : child);
    }
}
