using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    internal static void ResolveReferencedStaticCalls(BirGraphValue root, ReferenceMetadataIndex refs,
        IReadOnlySet<string> localTypes, IReadOnlySet<string> externalCanonicalTypes,
        IReadOnlySet<string> localDeclarationIds, Func<BirCallStatic, TypeNode[]> readSourceSignature,
        Action<BirGraphValue> releaseSourceSignatures, string file = null)
    {
        _refs = refs;
        _localTypes = localTypes ?? new HashSet<string>();
        _externalCanonicalTypes = externalCanonicalTypes ?? new HashSet<string>();
        _localDeclarationIds = localDeclarationIds ?? new HashSet<string>();
        void Walk(BirGraphValue node, string context)
        {
            if (node is IBirExecutablePayload) return;
            var childContext = ReferencedSourceDeclarationContext(node, context);
            node.VisitOwnChildSlots((_, _, slot) => Walk(slot.Value, childContext));
            var input = ReadSourceReferencedCall(node, readSourceSignature);
            if (input != null)
                WriteSourceReferencedCall(node, PlanReferencedCall(input, context, refs,
                    _localTypes, _externalCanonicalTypes, _localDeclarationIds));
        }
        Walk(root, file ?? "<unknown>");
        releaseSourceSignatures(root);
    }

    static string ReferencedSourceDeclarationContext(BirGraphValue node, string fallback)
    {
        var (name, body, position) = node switch {
            BirFunctionDeclaration method => (method.Name.OrDefault(), method.Body.HasValue, method.Pos.OrDefault()),
            BirClosureDefinition closure => (closure.Name.OrDefault(), closure.Body.HasValue, (BirDeclarationPosition)null),
            _ => (null, false, null),
        };
        if (!body || name == null) return fallback;
        if (position?.F.OrDefault() is { } file && position.L.HasValue)
            return $"{file}:{position.L.Value}" + (position.C.HasValue ? $":{position.C.Value}" : "") + $": {name}";
        return $"{fallback}: {name}";
    }

    static ReferencedCallInput ReadSourceReferencedCall(BirGraphValue node,
        Func<BirCallStatic, TypeNode[]> readSourceSignature) => node switch {
        BirCallStatic call => new(call.Tag, call.Owner.OrDefault() ?? call.CalleeOwner.OrDefault() ?? call.OwnerType.OrDefault(),
            call.Method.OrDefault(), call.Sig.OrDefault(), call.TypeArgs.OrEmpty().Length, call.DeclarationId.OrDefault(),
            call.MemberRef.HasValue && call.DotktDeclarationIdentityBound.OrDefault(), readSourceSignature(call),
            call.PropertyName.OrDefault(), call.PropertyAccessor.OrDefault(), call.Prop.OrDefault(), false),
        BirCallInstance call => new(call.Tag, call.OwnerType.OrDefault(), call.Method.OrDefault(), call.Sig.OrDefault(),
            call.TypeArgs.OrEmpty().Length, call.DeclarationId.OrDefault(), call.MemberRef.HasValue && call.DotktDeclarationIdentityBound.OrDefault(),
            null, call.PropertyName.OrDefault(), call.PropertyAccessor.OrDefault(), call.Prop.OrDefault(), call.Super.OrDefault()),
        BirConstrainedCall call when call.DeclarationId.HasValue => new(call.Tag, call.Iface.OrDefault(), call.Method.OrDefault(), call.Sig.OrDefault(),
            call.TypeArgs.OrEmpty().Length, call.DeclarationId.Value, call.MemberRef.HasValue && call.DotktDeclarationIdentityBound.OrDefault(),
            null, call.PropertyName.OrDefault(), call.PropertyAccessor.OrDefault(), call.Prop.OrDefault(), call.Super.OrDefault()),
        BirNewDelegate call => new(call.Tag, call.CalleeOwner.OrDefault(), call.Method.OrDefault(), call.Sig.OrDefault(),
            call.TypeArgs.OrEmpty().Length, call.DeclarationId.OrDefault(), call.MemberRef.HasValue && call.DotktDeclarationIdentityBound.OrDefault(),
            null, null, null, null, false),
        BirNewBoundDelegate call => new(call.Tag, call.CalleeOwner.OrDefault() ?? call.OwnerType.OrDefault(), call.Method.OrDefault(), call.Sig.OrDefault(),
            call.TypeArgs.OrEmpty().Length, call.DeclarationId.OrDefault(), call.MemberRef.HasValue && call.DotktDeclarationIdentityBound.OrDefault(),
            null, null, null, null, false),
        _ => null,
    };

    static void WriteSourceReferencedCall(BirGraphValue node, ReferencedCallPlan plan)
    {
        if (plan.PropertySelected)
            switch (node)
            {
                case BirCallStatic call:
                    call.PropertyName = plan.PropertyName; call.PropertyAccessor = plan.PropertyKind;
                    call.Prop = default; call.Method = plan.PhysicalName;
                    if (plan.SetVirtual) call.Virtual = true;
                    break;
                case BirCallInstance call:
                    call.PropertyName = plan.PropertyName; call.PropertyAccessor = plan.PropertyKind;
                    call.Prop = default; call.Method = plan.PhysicalName;
                    if (plan.SetVirtual) call.Virtual = true;
                    break;
                case BirConstrainedCall call:
                    call.PropertyName = plan.PropertyName; call.PropertyAccessor = plan.PropertyKind;
                    call.Prop = default; call.Method = plan.PhysicalName;
                    if (plan.SetVirtual) call.Virtual = true;
                    break;
            }
        if (plan.ClearIdentity)
            switch (node)
            {
                case BirCallStatic call: call.DeclarationId = default; break;
                case BirCallInstance call: call.DeclarationId = default; break;
                case BirConstrainedCall call: call.DeclarationId = default; break;
                case BirNewDelegate call: call.DeclarationId = default; break;
                case BirNewBoundDelegate call: call.DeclarationId = default; break;
            }
        if (plan.Signature == null) return;
        var signature = plan.Signature.Select(BirGraphValue.CopyType).ToArray();
        var reference = BirMemberReference.FromPhysical(plan.Reference);
        var declared = ResolvedMethodTypeParameters(plan.Declaration, node.ApplicationTypeArguments.OrEmpty().Length);
        var parameters = declared == null ? default(OptionalField<BirTypeParameter[]>)
            : OptionalField<BirTypeParameter[]>.Of(declared.Select(ReferenceMetadataReader.GenericParamSourceDeclaration).ToArray());
        switch (node)
        {
            case BirCallStatic call:
                call.Sig = signature; call.MemberRef = reference; call._resolvedMethodTypeParams = parameters;
                call.ArgTypes = default;
                if (plan.IdentityBound) call.DotktDeclarationIdentityBound = true;
                StampSourceReferencedDelegateTargets(call.Args.OrDefault(), call.TypeArgs.OrEmpty(), plan);
                break;
            case BirCallInstance call:
                call.Sig = signature; call.MemberRef = reference; call._resolvedMethodTypeParams = parameters;
                call.ArgTypes = default;
                if (plan.IdentityBound) call.DotktDeclarationIdentityBound = true;
                StampSourceReferencedDelegateTargets(call.Args.OrDefault(), call.TypeArgs.OrEmpty(), plan);
                break;
            case BirConstrainedCall call:
                call.Sig = signature; call.MemberRef = reference; call._resolvedMethodTypeParams = parameters;
                call.ArgTypes = default;
                if (plan.IdentityBound) call.DotktDeclarationIdentityBound = true;
                if (plan.StampReturn) call.ResolvedMemberReturn = plan.Declaration.ReturnType == typeof(void)
                    ? new TypeNode.Fqn("void") : ClrDeclarationMatching.MemberSigOf(_refs, plan.Declaration.ReturnType);
                StampSourceReferencedDelegateTargets(call.Args.OrDefault(), call.TypeArgs.OrEmpty(), plan);
                break;
            case BirNewDelegate call:
                call.Sig = signature; call.MemberRef = reference; call._resolvedMethodTypeParams = parameters;
                if (plan.IdentityBound) call.DotktDeclarationIdentityBound = true;
                break;
            case BirNewBoundDelegate call:
                call.Sig = signature; call.MemberRef = reference; call._resolvedMethodTypeParams = parameters;
                call.ArgTypes = default;
                if (plan.IdentityBound) call.DotktDeclarationIdentityBound = true;
                break;
        }
    }

    static void StampSourceReferencedDelegateTargets(IBirExpression[] arguments, TypeNode[] methodArguments,
        ReferencedCallPlan plan)
        => StampNativeDelegateArgumentTargets(arguments, methodArguments,
            plan.Declaration, plan.DelegateOwnerArguments);

}
