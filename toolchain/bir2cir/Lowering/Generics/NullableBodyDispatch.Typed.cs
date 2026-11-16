using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableBodyDispatch
{
    internal static void Build(NullableRepresentationViews.Owner owner, BirFunctionDeclaration source, BirFunctionDeclaration entry,
        NullableRepresentationFrame ownerFrame, NullableRepresentationFrame entryFrame,
        NullableRepresentationDemand.Variables bodyDemand, Action<BirFunctionDeclaration, NullableRepresentationFrame> prepare)
    {
        var roles = new[] { NullableRepresentationFrame.Role.Nullable, NullableRepresentationFrame.Role.Storage, NullableRepresentationFrame.Role.NullableStorage };
        IEnumerable<int> Declared(NullableRepresentationFrame.Role role) => role switch {
            NullableRepresentationFrame.Role.Nullable => entryFrame.NullableIndices,
            NullableRepresentationFrame.Role.Storage => entryFrame.StorageIndices, _ => entryFrame.NullableStorageIndices,
        };
        var missing = roles.SelectMany(role => bodyDemand.For("method", role).Except(Declared(role))
            .OrderBy(index => index).Select(index => (Index: index, Role: role))).ToArray();
        if (missing.Length == 0) return;
        var ownerType = new TypeNode.Fqn(owner.Name, ownerFrame.PhysicalArity == 0 ? null
            : Enumerable.Range(0, ownerFrame.PhysicalArity).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray());
        var methods = owner.Methods.Value.ToList();
        var names = methods.Select(method => method.Name.OrDefault()).ToHashSet(StringComparer.Ordinal);
        var chain = new List<(BirFunctionDeclaration Method, NullableRepresentationFrame Frame)> { (entry, entryFrame) };
        var indices = roles.ToDictionary(role => role, role => Declared(role).ToList());
        foreach (var demand in missing)
        {
            indices[demand.Role].Add(demand.Index);
            var frame = new NullableRepresentationFrame(entryFrame.SourceArity, indices[NullableRepresentationFrame.Role.Nullable].OrderBy(index => index),
                storageIndices: indices[NullableRepresentationFrame.Role.Storage].OrderBy(index => index),
                nullableStorageIndices: indices[NullableRepresentationFrame.Role.NullableStorage].OrderBy(index => index));
            var suffix = 0;
            string name;
            do { name = "$nullableBody$" + suffix++; } while (!names.Add(name));
            var helper = new BirFunctionDeclaration {
                Name = name, Vis = "private", Static = source.Static.HasValue ? source.Static : OptionalField<bool>.Of(false),
                Virtual = false, Override = false, Abstract = false,
                Params = source.Params.HasValue ? source.Params.Value.Select(parameter => (BirParameter)parameter.DeepClone()).ToArray() : source.Params,
                Ret = source.Ret, TypeParams = source.TypeParams.HasValue
                    ? source.TypeParams.Value.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray() : source.TypeParams,
                Attrs = Array.Empty<BirAttribute>(), Body = demand == missing[^1] ? source.Body.OrEmpty()
                    .Select(statement => (IBirStatement)((BirGraphValue)statement).DeepClone()).ToArray() : Array.Empty<IBirStatement>(),
                DeclarationId = DeclarationIdentityBinding.PhysicalOnlyId(source.DeclarationId.OrDefault() ?? "nullable-body:" + owner.Name, name),
                SuspendRet = source.SuspendRet, SuspendResult = source.SuspendResult,
            };
            prepare(helper, frame);
            methods.Add(helper);
            chain.Add((helper, frame));
        }
        owner.Methods = methods.ToArray();
        for (var step = 0; step < missing.Length; step++)
        {
            var (current, currentFrame) = chain[step];
            var (target, targetFrame) = chain[step + 1];
            var (index, role) = missing[step];
            BirCallInstance Call(TypeNode chosen)
            {
                var sources = Enumerable.Range(0, entryFrame.SourceArity).Select(index => (TypeNode)new TypeNode.Tv("method", index)).ToArray();
                var arguments = targetFrame.Close(sources,
                    type => currentFrame.Variable((TypeNode.Tv)type, NullableRepresentationFrame.Role.Ordinary),
                    type => Argument((TypeNode.Tv)type, NullableRepresentationFrame.Role.Nullable),
                    type => Argument((TypeNode.Tv)type, NullableRepresentationFrame.Role.Storage),
                    type => Argument((TypeNode.Tv)type, NullableRepresentationFrame.Role.NullableStorage));
                TypeNode Argument(TypeNode.Tv variable, NullableRepresentationFrame.Role requested) => variable.I == index && requested == role
                    ? chosen : currentFrame.Variable(variable, requested);
                return new BirCallInstance {
                    OwnerType = ownerType, Virtual = false, Recv = new BirThis(), Method = target.Name,
                    DeclarationId = target.DeclarationId, TypeArgs = arguments,
                    Sig = target.Params.Value.Select(parameter => parameter.Type.Value).ToArray(),
                    Args = current.Params.Value.Select(parameter => (IBirExpression)new BirLocal { Name = parameter.Name }).ToArray(),
                    DynRet = current.Ret,
                };
            }
            if (role != NullableRepresentationFrame.Role.Nullable)
                current.Body = new IBirStatement[] { new BirReturn { Value = Call(new TypeNode.Fqn("object")) } };
            else current.Body = new IBirStatement[] { new BirReturn { Value = new BirConditional {
                Type = current.Ret,
                Cond = new BirPropertyGet {
                    Type = new TypeNode.Fqn("System.Type"), Name = "IsValueType", Static = false, Ret = new TypeNode.Fqn("kotlin.Boolean"),
                    Recv = new BirClassReference { Type = currentFrame.Variable(new TypeNode.Tv("method", index), NullableRepresentationFrame.Role.Ordinary) },
                }, Then = Call(new TypeNode.Fqn("object")),
                Else = Call(currentFrame.Variable(new TypeNode.Tv("method", index), NullableRepresentationFrame.Role.Ordinary)),
            } } };
        }
    }
}
