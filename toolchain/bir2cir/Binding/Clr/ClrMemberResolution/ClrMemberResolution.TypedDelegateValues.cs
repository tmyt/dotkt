using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class ClrMemberResolution
{
    internal static BirStaticFieldGet UnitSingletonRead(IReadOnlySet<string> bindingLocalTypes, ReferenceMetadataIndex references)
    {
        references = references ?? throw new ArgumentNullException(nameof(references));
        var owner = new TypeNode.Fqn(UnitFqn);
        var result = new BirStaticFieldGet { OwnerType = owner, Name = UnitInstance };
        if (StaticFieldReference(bindingLocalTypes, references, owner, UnitInstance) is { } reference)
            result.FieldRef = BirMemberReference.FromPhysical(reference);
        return result;
    }

    internal static void TypedDelegateValueSelfTest(Action<ReferenceMetadataIndex> test)
        => test(ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location }));

    internal static DelegateAdapterPlan FunctionViewPlan(ReferenceMetadataIndex references, IReadOnlySet<string> localTypes,
        TypeNode.Fn selected, TypeNode.Fqn slot, TypeNode.Fn knownView, out TypeNode.Fn natural, out TypeNode.Fqn compatible, bool nativeSource = false)
    {
        var view = knownView ?? FunctionValueRepresentation.OrdinaryView(selected, references.IsByRefLikeFqn);
        natural = (TypeNode.Fn)BirTypeLowering.LowerFnDelegate(view, DelegatePhysicalContext(localTypes, references), false);
        compatible = slot;
        if (SameInterfaceSlotType(BirTypeLowering.DelegateFqnOf(natural), slot)) return null;
        var target = PhysicalFunctionShape(references, slot, includeNominal: true);
        var result = ReturnedFunctionType(selected);
        TypeNode.Fqn ordinaryResult = null;
        if (nativeSource && result is { Suspend: false } && PhysicalFunctionShape(references, DelegateSlotCore(natural.Ret)) is {} nested)
        {
            ordinaryResult = OrdinarySlot(nested, localTypes, references);
            TypeNode.Fqn CompatibleResult(TypeNode.Fn shape, TypeNode.Fn exact, TypeNode.Fqn plain)
            {
                // Test the closed chain, rather than just its erased outer result (object).
                // CLR variance can then retain the original value whenever every Invoke fits.
                return ReturnedFunctionType(shape) is {} next && PhysicalFunctionShape(references, DelegateSlotCore(exact.Ret)) is {} nextExact
                    ? WithResult(plain, CompatibleResult(next, nextExact, OrdinarySlot(nextExact, localTypes, references))) : plain;
            }
            compatible = WithResult(slot, CompatibleResult(result, nested, ordinaryResult));
        }
        var plan = ReturnedFunctionPlan(references, result, natural, target, ordinaryResult);
        if (plan != null && !nativeSource) return plan;
        return (plan ?? BoxedDelegateAdapterPlan(natural, target)) with {
            PublishReturn = !nativeSource && TypedPhysicalValueCoercion.MayHoldArray(target.Ret, references)
                && TypedPhysicalValueCoercion.MayHoldArray(natural.Ret, references),
            PublishArguments = nativeSource && natural.DelegateParams.Any(t => TypedPhysicalValueCoercion.MayHoldArray(t, references)) };
    }

    static TypeNode.Fqn WithResult(TypeNode.Fqn slot, TypeNode result) =>
        slot with { Args = [.. slot.Args[..^1], result] };

    internal static TypeNode.Fqn OrdinarySlot(TypeNode.Fn physical, IReadOnlySet<string> locals, ReferenceMetadataIndex refs) =>
        BirTypeLowering.DelegateFqnOf((TypeNode.Fn)BirTypeLowering.LowerFnDelegate(
            FunctionValueRepresentation.OrdinaryView(physical, refs.IsByRefLikeFqn), DelegatePhysicalContext(locals, refs), false));

    internal static TypeNode.Fn FunctionTypeOf(TypeNode type) => DelegateSlotCore(type) as TypeNode.Fn;
    internal static TypeNode.Fn ReturnedFunctionType(TypeNode.Fn function) => FunctionTypeOf(function?.Ret);

    static DelegateAdapterPlan ReturnedFunctionPlan(ReferenceMetadataIndex references, TypeNode.Fn result, TypeNode.Fn natural, TypeNode.Fn target,
        TypeNode.Fqn ordinaryResult = null)
    {
        if (result is { Suspend: false }
            && (ordinaryResult ?? DelegateSlotCore(target.Ret)) is TypeNode.Fqn resultSlot && references.IsClrDelegate(resultSlot))
        {
            // Keep the delegate shapes that Invoke must construct, but capture
            // complete slots: Box<T> must not become Box<!0> without T's constraints.
            var captures = new List<TypeNode>();
            TypeNode Bind(TypeNode type)
            {
                if (type == null) return null;
                if (type is TypeNode.Fn fn) return fn with { Ret = Bind(fn.Ret), Params = fn.Params.Select(Bind).ToArray(),
                    Recv = Bind(fn.Recv), Ctx = fn.Ctx?.Select(Bind).ToArray() };
                if (type is TypeNode.Nullable n) return n with { Of = Bind(n.Of) };
                if (type is TypeNode.Oblivious o) return o with { Of = Bind(o.Of) };
                if (type is TypeNode.ByRef b) return b with { Of = Bind(b.Of) };
                if (type is TypeNode.Fqn { Args: not null } f && (f == resultSlot ||
                    references.IsClrDelegate(f) && PhysicalFunctionShape(references, f) != null))
                    return f with { Args = f.Args.Select(Bind).ToArray() };
                var open = false;
                BirGraphNameFields.VisitTypeNodes(type, part => open |= part is TypeNode.Tv);
                if (!open) return type;
                var i = captures.IndexOf(type);
                if (i < 0) { i = captures.Count; captures.Add(type); }
                return new TypeNode.Tv("type", i);
            }
            var frame = (TypeNode.Fn)Bind(natural);
            var parameters = target.DelegateParams.Select(Bind).ToArray();
            var ret = (TypeNode.Fqn)Bind(resultSlot);
            var source = PhysicalFunctionShape(references, DelegateSlotCore(natural.Ret));
            var nested = new ReturnedFunction((TypeNode.Fn)Bind(result), ret, (TypeNode.Fn)Bind(source), ordinaryResult != null);
            return new(frame, parameters.Length, ordinaryResult == null ? ret : Bind(target.Ret), parameters, captures.Count, captures.ToArray(), ResultFunction: nested);
        }
        return null;
    }

    // A CLR variant generic argument may vary only through reference conversions.
    // Kotlin's boxed value conversion also applies inside covariant delegate results
    // and contravariant delegate parameters. Keep the *named* source Invoke slot.
    internal static DelegateAdapterPlan NominalDelegateValuePlan(ReferenceMetadataIndex refs, IReadOnlySet<string> localTypes,
        TypeNode source, TypeNode target, ValueTypeOracle isValue, BirTypeParameter[] owners, BirTypeParameter[] methods,
        out TypeNode.Fqn slot)
    {
        slot = DelegateSlotCore(target) as TypeNode.Fqn;
        if (refs == null || DelegateSlotCore(source) is not TypeNode.Fqn from || slot == null || !NeedsBridge(from, slot))
            return null;
        bool NeedsBridge(TypeNode.Fqn a, TypeNode.Fqn b)
        {
            if (a == b || a.Args is not { Length: > 0 } aa || b.Args is not {} bb || aa.Length != bb.Length
                || WithoutArity(a.Name) != WithoutArity(b.Name)) return false;
            var definition = ResolveOwnerType(a, refs);
            if (definition == null || !IsDelegate(definition) || ResolveOwnerType(b, refs) != definition) return false;
            var parameters = definition.GetGenericArguments();
            var needed = false;
            for (var i = 0; i < aa.Length; i++)
            {
                if (aa[i] == bb[i]) continue;
                if ((parameters[i].GenericParameterAttributes & GenericParameterAttributes.VarianceMask) == 0) return false;
                var l = DelegateSlotCore(aa[i]); var r = DelegateSlotCore(bb[i]);
                needed |= l is TypeNode.Tv || r is TypeNode.Tv || l is TypeNode.Fqn x && isValue(x)
                    || r is TypeNode.Fqn y && isValue(y) || l is TypeNode.Fqn ld && r is TypeNode.Fqn rd && NeedsBridge(ld, rd);
            }
            return needed;
        }

        var open = false;
        BirGraphNameFields.VisitTypeNodes(from, t => open |= t is TypeNode.Tv);
        BirGraphNameFields.VisitTypeNodes(slot, t => open |= t is TypeNode.Tv);
        owners = open ? owners ?? [] : [];
        methods = open ? methods ?? [] : [];
        TypeNode InAdapter(TypeNode t) => open ? CapturedGenericInvocation.Map(t,
            v => new TypeNode.Tv("type", v.I + (v.Scope == "method" ? owners.Length : 0))) : t;
        var frame = owners.Concat(methods).Select((p, i) => {
            var q = CapturedGenericInvocation.CloneParameter(p, InAdapter);
            q.Name = "T" + i; q.Variance = default;
            return q;
        }).ToArray();
        var actual = owners.Select((_, i) => (TypeNode)new TypeNode.Tv("type", i))
            .Concat(methods.Select((_, i) => new TypeNode.Tv("method", i))).ToArray();
        var capture = (TypeNode.Fqn)InAdapter(from);
        TypeNode.Fn InvokeShape(TypeNode.Fqn owner)
        {
            var reference = PhysicalDelegateMember(localTypes, refs, owner, constructor: false);
            TypeNode Close(TypeNode type) => SupertypeGraph.SubstOwnerTvs(type,
                ((TypeNode.Fqn)reference.DeclaringType).Args ?? []);
            return new(false, Close(reference.ReturnType), reference.ParameterTypes.Select(Close).ToArray());
        }
        var natural = InvokeShape(capture);
        var dest = InvokeShape((TypeNode.Fqn)InAdapter(slot));
        return new(natural, dest.DelegateParams.Length, dest.Ret, dest.DelegateParams, frame.Length, actual,
            NominalSource: capture, LexicalFrame: frame, PublishReturn: dest.Ret is TypeNode.Array);
    }

    // The selected nominal instantiation is already physical, including local
    // value types. Bind the open member, then close only its declaring type.
    internal static MemberRefNode PhysicalDelegateMember(IReadOnlySet<string> localTypes, ReferenceMetadataIndex refs,
        TypeNode.Fqn owner, bool constructor)
    {
        var args = owner.Args ?? [];
        var open = args.Length == 0 ? owner : owner with {
            Args = Enumerable.Range(0, args.Length).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray() };
        var member = constructor ? DelegateConstructorReference(localTypes, refs, open) : DelegateInvokeReference(localTypes, refs, open);
        return args.Length == 0 ? member : member with { DeclaringType = SupertypeGraph.SubstOwnerTvs(member.DeclaringType, args) };
    }

    internal static DelegateAdapterPlan UnitDelegateValuePlan(ReferenceMetadataIndex references,
        IReadOnlyDictionary<string, string> localCarriers, TypeNode source, TypeNode target, out TypeNode.Fqn slot)
    {
        slot = null;
        // Scalar-only focused passes do not need a reference universe. A value
        // that cannot be a function seam must not manufacture that dependency.
        if (source is TypeNode.Fn sourceFunction && sourceFunction.Ret is not
                TypeNode.Fqn { Args: null, Name: "void" or "System.Void" }
            || target is TypeNode.Fn targetFunction && targetFunction.Ret is not
                TypeNode.Fqn { Args: null, Name: "kotlin.Unit" or "object" or "System.Object" })
            return null;
        if (references == null && (source is not TypeNode.Fn || target is not TypeNode.Fn)) return null;
        references = references ?? throw new ArgumentNullException(nameof(references));
        if (PhysicalFunctionShape(references, source) is not TypeNode.Fn {
                Ret: TypeNode.Fqn { Name: "void" or "System.Void", Args: null } } natural
            || PhysicalFunctionShape(references, target) is not TypeNode.Fn {
                Ret: TypeNode.Fqn { Name: "kotlin.Unit" or "object" or "System.Object", Args: null } } wanted)
            return null;
        slot = BirTypeLowering.DelegateFqnOf(wanted);
        return VoidDelegateAdapterPlan(references, localCarriers, natural, slot, wanted.Ret);
    }

    internal static MemberRefNode DelegateInvokeReference(IReadOnlySet<string> bindingLocalTypes, ReferenceMetadataIndex references, TypeNode stated)
    {
        references = references ?? throw new ArgumentNullException(nameof(references));
        var context = DelegatePhysicalContext(bindingLocalTypes, references);
        var physical = BirTypeLowering.LowerSlot(stated, context, BirTypeLowering.PhysicalSlotRole.Value);
        if (physical is TypeNode.Fn fnNode)
            physical = BirTypeLowering.DelegateFqnOf(
                (TypeNode.Fn)BirTypeLowering.LowerSlot(fnNode, context, BirTypeLowering.PhysicalSlotRole.Delegate));
        if (physical is not TypeNode.Fqn delegateFqn)
            throw new InvalidOperationException(
                $"bir2cir: a function-type call lowers to {TypeNode.ToJson(physical)}, which is not a named type");
        var open = ResolveOwnerType(delegateFqn, refs: references)
            ?? throw new InvalidOperationException(
                $"bir2cir: the delegate '{delegateFqn.Name}' does not resolve to a .NET type");
        var invoke = open.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "Invoke").ToList();
        if (invoke.Count != 1)
            throw new InvalidOperationException(
                $"bir2cir: '{delegateFqn.Name}' has {invoke.Count} Invoke declarations, not one");
        return ClrMemberReferences.MemberRefOf(references, bindingLocalTypes, invoke[0], MemberRefNode.Kinds.Method, open, delegateFqn.Args,
            ownerArgumentsAreMethodSlots: IsFunctionShape(stated));
    }

    internal static MemberRefNode DelegateConstructorReference(IReadOnlySet<string> bindingLocalTypes, ReferenceMetadataIndex references, TypeNode stated)
    {
        references = references ?? throw new ArgumentNullException(nameof(references));
        var context = DelegatePhysicalContext(bindingLocalTypes, references);
        var physical = BirTypeLowering.LowerSlot(stated, context, BirTypeLowering.PhysicalSlotRole.Value);
        if (physical is TypeNode.Fn fnNode)
            physical = BirTypeLowering.DelegateFqnOf(
                (TypeNode.Fn)BirTypeLowering.LowerSlot(fnNode, context, BirTypeLowering.PhysicalSlotRole.Delegate));
        if (physical is not TypeNode.Fqn delegateFqn)
            throw new InvalidOperationException(
                $"bir2cir: a delegate construction lowers to {TypeNode.ToJson(physical)}, which is not a named type");
        var open = ResolveOwnerType(delegateFqn, refs: references)
            ?? throw new InvalidOperationException(
                $"bir2cir: the delegate '{delegateFqn.Name}' does not resolve to a .NET type");
        var ctors = open.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.GetParameters().Length == 2).ToList();
        var win = ClrDeclarationMatching.TryPickUniqueCtor(references, ctors,
            new List<TypeNode> { new TypeNode.Fqn("System.Object"), new TypeNode.Fqn("System.IntPtr") },
            delegateFqn.Args ?? Array.Empty<TypeNode>())
            ?? throw new InvalidOperationException(
                $"bir2cir: '{delegateFqn.Name}' has no unique (object, native int) constructor — "
                + $"{ctors.Count} two-argument candidate(s)");
        return ClrMemberReferences.MemberRefOf(references, bindingLocalTypes, win, MemberRefNode.Kinds.Ctor, open,
            delegateFqn.Args ?? Array.Empty<TypeNode>(), ownerArgumentsAreMethodSlots: IsFunctionShape(stated));
    }

    // These leaf bindings already have the resolver's reference universe and
    // local/external boundary. They must not inherit a last lowered file/shadow.
    static BirTypeLowering.PhysicalTypeContext DelegatePhysicalContext(IReadOnlySet<string> bindingLocalTypes, ReferenceMetadataIndex bindingReferences) => new(
        refBuild: false, aliases: bindingReferences.Aliases, isValue: bindingReferences.IsValueType,
        physicalNames: bindingReferences.PhysicalTypeNames, localTypes: bindingLocalTypes,
        nullableFrames: bindingReferences.NullableTypeFrames);
}
