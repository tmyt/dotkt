using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    internal static void ResolveInheritedExternalCalls(IReadOnlySet<string> bindingLocalTypes, IReadOnlyList<BirFileDeclaration> roots,
        ReferenceMetadataIndex refs)
    {
        refs = refs ?? throw new ArgumentNullException(nameof(refs));
        var definitions = SupertypeGraph.CollectSource(roots);
        var methods = new Dictionary<string, InheritedLocalMethod[]>(StringComparer.Ordinal);
        foreach (var owner in roots.SelectMany(root => root.Types.OrEmpty()))
            if (owner.Name.HasValue)
                methods[owner.Name.Value] = owner.Methods.OrEmpty().Select(method => new InheritedLocalMethod(
                    method.Name.OrDefault(), method.Static.OrDefault(), method.TypeParams.OrEmpty().Length,
                    method.Params.OrDefault()?.Select(parameter => parameter.Type.OrDefault()).ToArray(),
                    method.DeclarationId.OrDefault())).ToArray();

        BirGraphValue Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return node;
            node.VisitOwnChildSlots((_, role, slot) => {
                if (role != BirGenericFrameRole.FrozenSourceFact) slot.Replace(Walk(slot.Value));
            });
            // Explicit-null is an already-present descriptor, not an absent
            // selection. Preserve this distinction at both physical clocks.
            if (node is not BirCallInstance call || call.MemberRef.IsPresent
                || call.OwnerType.OrDefault() is not TypeNode.Fqn owner
                || !call.Method.HasValue || !call.Sig.HasValue || call.Sig.Value.Any(type => type == null)) return node;
            if ((call.SelectedGenericCallable ?? call.DeclarationId.OrDefault()) is string selected && methods.TryGetValue(owner.Name, out var local)
                && local.Any(method => method.Identity == selected && method.Name == call.Method.Value
                    && method.Arity == call.TypeArgs.OrEmpty().Length && !method.Static)) return node;
            var plan = PlanInheritedExternalCall(bindingLocalTypes, owner, call.Method.Value, call.TypeArgs.OrEmpty().Length,
                call.Sig.Value, definitions, methods, refs);
            if (plan == null) return node;
            call.MemberRef = BirMemberReference.FromPhysical(plan.Reference);
            var declared = ResolvedMethodTypeParameters(plan.Declaration, call.TypeArgs.OrEmpty().Length);
            call._resolvedMethodTypeParams = declared == null ? default(OptionalField<BirTypeParameter[]>)
                : OptionalField<BirTypeParameter[]>.Of(declared.Select(ReferenceMetadataReader.GenericParamSourceDeclaration).ToArray());
            // This is the same selected-parameter writer used by referenced
            // binding, with the existing module delegate/carrier authorities.
            StampSourceReferencedDelegateTargets(bindingLocalTypes, refs, call.Args.OrDefault(), call.TypeArgs.OrEmpty(),
                new ReferencedCallPlan(Declaration: plan.Declaration, DelegateOwnerArguments: plan.OwnerArgs));
            return (BirGraphValue)AdaptInheritedGenericResultSource(call);
        }
        foreach (var root in roots) Walk(root);
    }

    static IBirExpression AdaptInheritedGenericResultSource(BirCallInstance call)
    {
        var reference = call.MemberRef.OrDefault();
        var physical = PlanInheritedGenericResult(call.DynRet.OrDefault(), reference?.DeclaringType.OrDefault(),
            reference?.ReturnType.OrDefault(), call.TypeArgs.OrEmpty());
        if (physical == null) return call;
        var logical = call.DynRet.Value;
        call.Ret = BirGraphValue.CopyType(physical);
        call.DynRet = BirGraphValue.CopyType(physical);
        if (call.Sty.HasValue) call.Sty = BirGraphValue.CopyType(physical);
        // The continuing call (and any registered descendant) moves once into
        // the new cast. Its receiver/arguments are not fresh evaluations.
        return new BirCast { Type = BirGraphValue.CopyType(logical), E = OptionalField<IBirExpression>.Of(call) };
    }
}
