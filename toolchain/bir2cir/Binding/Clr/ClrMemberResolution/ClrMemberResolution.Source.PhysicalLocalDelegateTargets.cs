using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    // Revisit constructions only at the physical clock: a declared Unit return
    // is void here, but a method variable instantiated at Unit is a value. This
    // is not a replay of the earlier call/argument target-stamping pass.
    internal static void ResolvePhysicalLocalDelegateTargets(IReadOnlyList<BirFileDeclaration> roots,
        ReferenceMetadataIndex refs, IReadOnlyDictionary<string, string> localCarriers = null)
    {
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
        _localCarriers = localCarriers;
        var owners = SourceLocalDelegateOwners(roots);
        var localTypes = owners.Keys.ToHashSet(StringComparer.Ordinal);
        void Bind(BirGraphValue node)
        {
            var closure = node as BirNewClosure;
            var direct = node as BirNewDelegate;
            if (closure == null && direct == null) return;
            var ownerType = closure?.ClosureType.OrDefault() ?? direct?.CalleeOwner.OrDefault();
            if (ownerType is not TypeNode.Fqn owner) return;
            if (!owners.TryGetValue(owner.Name, out var methods))
            {
                // The reference is already selected; close its exact physical
                // slots rather than resolve the requested function type again.
                if (direct?.MemberRef.OrDefault() is BirMemberReference reference
                    && reference.ParameterTypes.HasValue && reference.ReturnType.HasValue
                    && direct.FuncType.OrDefault() is TypeNode.Fn { Suspend: false } requested)
                {
                    var ownerArguments = (reference.DeclaringType.OrDefault() as TypeNode.Fqn)?.Args ?? Array.Empty<TypeNode>();
                    var methodArguments = direct.TypeArgs.OrEmpty();
                    var natural = BirTypeLowering.PhysicalDelegate(
                        PhysicalReferencedDelegateSlot(reference.ReturnType.Value, ownerArguments, methodArguments),
                        reference.ParameterTypes.Value.Select(type =>
                            PhysicalReferencedDelegateSlot(type, ownerArguments, methodArguments)).ToArray());
                    Reconcile(direct, natural, requested);
                }
                return;
            }
            var name = closure?.Method.OrDefault() ?? direct?.Method.OrDefault();
            if (name == null) return;
            var wanted = closure != null ? (closure.FuncType.OrDefault() as TypeNode.Fn)?.DelegateParams : direct.Sig.OrDefault();
            if (wanted == null || wanted.Any(type => type == null)) return;
            var suppliedArguments = closure != null ? closure.TypeArgs.OrEmpty() : direct.TypeArgs.OrEmpty();
            if (suppliedArguments.Any(type => type == null)) return;
            var methodArgs = closure != null ? Array.Empty<TypeNode>() : suppliedArguments;
            var ownerArgs = closure != null ? suppliedArguments : owner.Args ?? Array.Empty<TypeNode>();
            var matches = SelectLocalDelegateTargets(SourceLocalDelegateCandidates(methods), name,
                direct?.DeclarationId.OrDefault(), wanted, ownerArgs, methodArgs,
                functionSignature: false, closure: closure != null);
            if (matches.Length != 1)
                throw new InvalidOperationException($"bir2cir: {((BirGraphNode)node).Tag} target '{owner.Name}.{name}' resolves to {matches.Length} exact local methods");
            var chosen = matches[0];
            var declaration = methods[chosen.Index];
            if (declaration.Params.Value.Any(parameter => parameter.FunctionSignatureType.HasValue))
            {
                var signature = declaration.Params.Value.Select(FunctionSignatureIdentity.SignatureType).ToArray();
                if (direct != null) direct.CalleeParams = signature;
                else closure.CalleeParams = signature;
            }
            if (direct != null) direct.Sig = chosen.Parameters.Select(BirGraphValue.CopyType).ToArray();
            var desired = closure != null ? closure.FuncType.OrDefault() : direct.FuncType.OrDefault();
            if (desired is TypeNode.Fn { Suspend: false } target && declaration.Ret.HasValue)
            {
                TypeNode Close(TypeNode type) => SubstituteLocalDelegateMethodType(SupertypeGraph.SubstOwnerTvs(type, ownerArgs), methodArgs);
                var natural = BirTypeLowering.PhysicalDelegate(Close(declaration.Ret.Value), chosen.Parameters.Select(Close).ToArray());
                Reconcile(node, natural, target);
            }
            // Neither maintained construction has an args field. Captures are
            // not ordinary call arguments and must not acquire that slot policy.
        }
        void Reconcile(BirGraphValue node, TypeNode.Fn natural, TypeNode.Fn target)
        {
            if (!NeedsSlotAdapter(natural, target)) return;
            if (!node.HasDeclaredDelegateSlot) MarkDelegateSlot(node, target, refs, localTypes);
            if (node is BirNewDelegate direct) direct.FuncType = natural;
            else ((BirNewClosure)node).FuncType = natural;
        }
        void Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            node.VisitOwnChildSlots((_, role, slot) => {
                if (role != BirGenericFrameRole.FrozenSourceFact) Walk(slot.Value);
            });
            Bind(node);
        }
        foreach (var root in roots) Walk(root);
    }

    static TypeNode PhysicalReferencedDelegateSlot(TypeNode type, TypeNode[] ownerArguments, TypeNode[] methodArguments)
        => type is TypeNode.Mod modifier ? PhysicalReferencedDelegateSlot(modifier.Of, ownerArguments, methodArguments)
            : SubstituteLocalDelegateMethodType(SupertypeGraph.SubstOwnerTvs(type, ownerArguments), methodArguments);
}
