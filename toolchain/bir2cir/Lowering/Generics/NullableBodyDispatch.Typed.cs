using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableBodyDispatch
{
    internal static void Build(NullableRepresentationViews.Owner owner, BirFunctionDeclaration source, BirFunctionDeclaration entry,
        NullableRepresentationFrame ownerFrame, NullableRepresentationFrame entryFrame,
        NullableRepresentationDemand.Variables bodyDemand, Action<BirFunctionDeclaration, NullableRepresentationFrame> prepare,
        Action<Action> finishStorage = null)
    {
        var roles = Enum.GetValues<NullableRepresentationFrame.Role>().Where(role => role != NullableRepresentationFrame.Role.Ordinary);
        IEnumerable<int> Declared(NullableRepresentationFrame.Role role) => role switch {
            NullableRepresentationFrame.Role.Nullable => entryFrame.NullableIndices,
            NullableRepresentationFrame.Role.Storage => entryFrame.StorageIndices, NullableRepresentationFrame.Role.NullableStorage => entryFrame.NullableStorageIndices,
            NullableRepresentationFrame.Role.Element => entryFrame.ElementIndices, NullableRepresentationFrame.Role.NullableElement => entryFrame.NullableElementIndices,
            _ => throw new InvalidOperationException("Unknown companion role"),
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
                nullableStorageIndices: indices[NullableRepresentationFrame.Role.NullableStorage].OrderBy(index => index),
                elementIndices: indices[NullableRepresentationFrame.Role.Element].OrderBy(index => index),
                nullableElementIndices: indices[NullableRepresentationFrame.Role.NullableElement].OrderBy(index => index));
            var suffix = 0;
            string name;
            do { name = "$nullableBody$" + suffix++; } while (!names.Add(name));
            var helper = new BirFunctionDeclaration {
                Name = name, Vis = "private", Static = source.Static.HasValue ? source.Static : OptionalField<bool>.Of(false),
                Virtual = false, Override = false, Abstract = false,
                Params = source.Params.HasValue ? source.Params.Value.Select(parameter => (BirParameter)parameter.DeepClone()).ToArray() : source.Params,
                Ret = source.Ret, TypeParams = source.TypeParams.HasValue
                    ? source.TypeParams.Value.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray() : source.TypeParams,
                Attrs = Array.Empty<BirAttribute>(), Body = demand == missing[^1] ? source.Body.OrEmpty() : Array.Empty<IBirStatement>(),
                DeclarationId = DeclarationIdentityBinding.PhysicalOnlyId(source.DeclarationId.OrDefault() ?? "nullable-body:" + owner.Name, name),
                SuspendRet = source.SuspendRet, SuspendResult = source.SuspendResult,
            };
            prepare(helper, frame);
            helper.LocalBodyEntry = (helper.DeclarationId.Value, frame);
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
                    type => Argument((TypeNode.Tv)type, NullableRepresentationFrame.Role.NullableStorage),
                    type => Argument((TypeNode.Tv)type, NullableRepresentationFrame.Role.Element),
                    type => Argument((TypeNode.Tv)type, NullableRepresentationFrame.Role.NullableElement));
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
            if (role is NullableRepresentationFrame.Role.Storage or NullableRepresentationFrame.Role.NullableStorage)
            {
                var call = Call(new TypeNode.Fqn("object"));
                if (role == NullableRepresentationFrame.Role.Storage && finishStorage != null)
                {
                    // A body-only slot defaults to erased storage. Use its ordinary
                    // witness only if the private helper actually needs a physical proof.
                    var variable = new TypeNode.Tv("method", index);
                    var slot = targetFrame.Variable(variable, role).I;
                    finishStorage(() => {
                        if (target.TypeParams.Value[slot].Constraints.OrEmpty().Length != 0)
                            call.TypeArgs.Value[slot] = currentFrame.Variable(variable, NullableRepresentationFrame.Role.Ordinary);
                    });
                }
                current.Body = new IBirStatement[] { new BirReturn { Value = call } };
            }
            else
            {
                var ordinary = currentFrame.Variable(new TypeNode.Tv("method", index), NullableRepresentationFrame.Role.Ordinary);
                var token = new BirClassReference { Type = ordinary };
                var boolean = new TypeNode.Fqn("kotlin.Boolean");
                IBirExpression box = role == NullableRepresentationFrame.Role.Nullable ? new BirPropertyGet {
                    Type = new TypeNode.Fqn("System.Type"), Name = "IsValueType", Static = false, Ret = new TypeNode.Fqn("kotlin.Boolean"),
                    Recv = token,
                } : new BirCallStatic {
                    Owner = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.EnumRuntimeKt"), Method = "kotlinNeedsErasedListElement",
                    Sig = new TypeNode[] { new TypeNode.Fqn("kotlin.Any"), boolean }, Ret = boolean,
                    Args = new IBirExpression[] { token, new BirConstant { Type = boolean,
                        Value = new BirLiteral.BooleanLiteral(role == NullableRepresentationFrame.Role.NullableElement) } },
                };
                current.Body = new IBirStatement[] { new BirReturn { Value = new BirConditional {
                    Type = current.Ret, Cond = OptionalField<IBirExpression>.Of(box),
                    Then = Call(new TypeNode.Fqn("object")), Else = Call(ordinary),
                } } };
            }
        }
    }
}
