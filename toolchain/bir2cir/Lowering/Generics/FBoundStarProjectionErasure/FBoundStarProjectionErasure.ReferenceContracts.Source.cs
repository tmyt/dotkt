using System.Text;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    static SourceReferenceContract CreateReferenceContractSource(Owner owner, BirParameter[] originals,
        OptionalField<string> declaration, BirFunctionDeclaration slot, CarrierRealizationContext context)
    {
        var dependencies = new HashSet<int>();
        foreach (var parameter in originals)
            if (SignatureValueTypes.Of(parameter.Type.OrDefault()) is TypeNode.ByRef reference)
                VisitSourceReferenceVariables(reference.Of, dependencies);
        if (dependencies.Count == 0) return null;
        var ownerParameters = context.Definition(owner).TypeParams.OrEmpty();
        var indices = dependencies.OrderBy(index => index).ToArray();
        if (indices.Any(index => index < 0 || index >= ownerParameters.Length))
            throw new InvalidOperationException("Managed-reference contract has no exact owner parameter frame");
        var mapping = indices.Select((index, position) => (index, position)).ToDictionary(pair => pair.index, pair => pair.position);
        var parameters = indices.Select(index => {
            var parameter = (BirTypeParameter)ownerParameters[index].DeepClone();
            if (parameter.Constraints.HasValue)
                parameter.Constraints = parameter.Constraints.Value.SelectMany(bound => ReferenceBoundsSource(bound,
                    ownerParameters, dependencies, context)).Distinct().Select(BirGraphValue.CopyType).ToArray();
            parameter.RewriteOwnTypeFieldsInPlace((_, _, type) => ReindexSourceReferenceVariables(type, mapping));
            parameter.Variance = default;
            return parameter;
        }).ToArray();
        RestoreReferenceParametersSource(originals, slot.Params.Value);
        foreach (var parameter in slot.Params.Value)
        {
            if (parameter.Type.HasValue) parameter.Type = ReindexSourceReferenceVariables(parameter.Type.Value, mapping);
            if (parameter.FunctionSignatureType.HasValue)
                parameter.FunctionSignatureType = ReindexSourceReferenceVariables(parameter.FunctionSignatureType.Value, mapping);
        }
        foreach (var parameter in slot.TypeParams.OrEmpty())
            parameter.RewriteOwnTypeFieldsInPlace((_, _, type) => ReindexSourceReferenceVariables(type, mapping));
        var name = owner.ErasedName + "$ref$" + Convert.ToHexString(Encoding.UTF8.GetBytes(slot.Name.OrDefault()));
        var allocated = context.Demand.AllocateTypeName(name);
        var definition = new BirInterfaceDefinition {
            Name = allocated, Kind = "interface", Generated = true, TypeParams = parameters,
            ManagedReferenceContract = new BirManagedReferenceContract {
                Owner = owner.Name, Declaration = declaration.IsPresent ? declaration : OptionalField<string>.Null,
                Invoke = "$ref$invoke", Slots = indices.ToArray(),
            },
            Interfaces = Array.Empty<TypeNode>(), Fields = Array.Empty<BirFieldDeclaration>(), Ctors = Array.Empty<BirConstructor>(),
            Methods = new[] { slot, ReferenceInvokerSource(allocated, indices.Length, slot) },
            Properties = Array.Empty<BirPropertyDeclaration>(), Attrs = Array.Empty<BirAttribute>(),
        };
        if (PublishedApiTypeVisibilityLowering.ResolveVisibility(context.Definition(owner)) is { } visibility) definition.Vis = visibility;
        context.AttachType(context.Root(owner), definition);
        context.Bindings.GetSource(slot);
        var implemented = new TypeNode.Fqn(allocated, indices.Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray());
        var source = context.Definition(owner);
        source.Interfaces = source.Interfaces.OrEmpty().Append(BirGraphValue.CopyType(implemented)).ToArray();
        return new(implemented, slot, "$ref$invoke") { SlotBinding = context.Bindings.GetCapturedSource(slot) };
    }

    static IEnumerable<TypeNode> ReferenceBoundsSource(TypeNode bound, BirTypeParameter[] parameters,
        IReadOnlySet<int> retained, CarrierRealizationContext context)
    {
        var visited = new HashSet<int>();
        bool Fits(TypeNode type)
        {
            var variables = new HashSet<int>();
            VisitSourceReferenceVariables(type, variables);
            return variables.IsSubsetOf(retained);
        }
        IEnumerable<TypeNode> Visit(TypeNode current)
        {
            if (StripSourceNullability(current) is TypeNode.Tv { Scope: "type" } variable && !retained.Contains(variable.I))
            {
                if (!visited.Add(variable.I)) yield break;
                foreach (var next in parameters[variable.I].Constraints.OrEmpty())
                    foreach (var result in Visit(next)) yield return result;
            }
            else if (current != null)
            {
                if (Fits(current)) yield return current;
                else if (StripSourceNullability(current) is TypeNode.Fqn nominal)
                    foreach (var (ancestor, _) in SupertypeGraph.Reachable(new SupertypeGraph.Def { Base = nominal },
                        context.Preparation.SourceSupertypes, context.References))
                        if (Fits(ancestor)) yield return ancestor;
            }
        }
        return Visit(bound);
    }

    static BirFunctionDeclaration ReferenceInvokerSource(string name, int arity, BirFunctionDeclaration slot)
    {
        var owner = new TypeNode.Fqn(name, Enumerable.Range(0, arity).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray());
        var parameters = new List<BirParameter> { new() { Name = "$receiver", Type = new TypeNode.Fqn("System.Object") } };
        var arguments = new List<IBirExpression>();
        foreach (var parameter in slot.Params.Value)
        {
            var parameterName = "p" + arguments.Count;
            parameters.Add(new BirParameter { Name = parameterName, Type = BirGraphValue.CopyType(parameter.Type.Value) });
            arguments.Add(new BirLocal { Name = parameterName });
        }
        var call = new BirCallInstance {
            OwnerType = BirGraphValue.CopyType(owner), Virtual = true,
            Recv = OptionalField<IBirExpression>.Of(new BirCast {
                Type = BirGraphValue.CopyType(owner), _exactBridgeCast = true,
                E = OptionalField<IBirExpression>.Of(new BirLocal { Name = "$receiver" }),
            }),
            Method = slot.Name, Sig = slot.Params.Value.Select(FunctionSignatureIdentity.SignatureType).ToArray(),
            Ret = BirGraphValue.CopyType(slot.Ret.Value), Args = arguments.ToArray(),
            _exactExistentialBridgeOwnerCall = true, ClrOwnerResolved = true,
        };
        if (slot.TypeParams.OrEmpty().Length > 0)
            call.TypeArgs = Enumerable.Range(0, slot.TypeParams.Value.Length).Select(index => (TypeNode)new TypeNode.Tv("method", index)).ToArray();
        return new BirFunctionDeclaration {
            Name = "$ref$invoke", Static = true, Vis = "public", Generated = true,
            Override = false, Virtual = false, Abstract = false, ObjectOverride = false,
            Params = parameters.ToArray(), Ret = BirGraphValue.CopyType(slot.Ret.Value),
            TypeParams = slot.TypeParams.OrEmpty().Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray(),
            Body = new IBirStatement[] { IsVoidResult(slot.Ret.Value)
                ? new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(call) }
                : new BirReturn { Value = OptionalField<IBirExpression>.Of(call) } },
        };
    }

    static void RestoreReferenceParametersSource(BirParameter[] original, BirParameter[] projected)
    {
        for (var index = 0; index < original.Length; index++)
            if (SignatureValueTypes.Of(original[index].Type.OrDefault()) is TypeNode.ByRef)
            {
                projected[index].Type = BirGraphValue.CopyType(original[index].Type.Value);
                if (original[index].FunctionSignatureType.HasValue)
                    projected[index].FunctionSignatureType = BirGraphValue.CopyType(original[index].FunctionSignatureType.Value);
            }
    }

    static void VisitSourceReferenceVariables(TypeNode type, HashSet<int> result) =>
        TypedExecutableFrames.RewriteType(type, variable => { if (variable.Scope == "type") result.Add(variable.I); return null; });

    static TypeNode ReindexSourceReferenceVariables(TypeNode type, IReadOnlyDictionary<int, int> mapping) =>
        TypedExecutableFrames.RewriteType(type, variable => variable.Scope != "type" ? null
            : new TypeNode.Tv("type", mapping.TryGetValue(variable.I, out var index) ? index
                : throw new InvalidOperationException("Managed-reference contract dropped a required owner parameter")));
}
