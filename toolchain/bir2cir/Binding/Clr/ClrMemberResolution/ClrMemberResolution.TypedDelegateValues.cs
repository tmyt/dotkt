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
        TypeNode.Fn selected, TypeNode.Fqn slot, TypeNode.Fn knownView, out TypeNode.Fn natural, bool nativeSource = false)
    {
        var view = knownView ?? FunctionValueRepresentation.OrdinaryView(selected, references.IsByRefLikeFqn);
        natural = (TypeNode.Fn)BirTypeLowering.LowerFnDelegate(view, DelegatePhysicalContext(localTypes, references), false);
        if (SameInterfaceSlotType(BirTypeLowering.DelegateFqnOf(natural), slot)) return null;
        var target = PhysicalFunctionShape(references, slot, includeNominal: true);
        return ReturnedFunctionPlan(references, ReturnedFunctionType(selected), natural, target) ?? BoxedDelegateAdapterPlan(natural, target) with {
            PublishReturn = !nativeSource && TypedPhysicalValueCoercion.MayHoldArray(target.Ret, references)
                && TypedPhysicalValueCoercion.MayHoldArray(natural.Ret, references),
            PublishArguments = nativeSource && natural.DelegateParams.Any(t => TypedPhysicalValueCoercion.MayHoldArray(t, references)) };
    }

    internal static TypeNode.Fn ReturnedFunctionType(TypeNode.Fn function) =>
        DelegateSlotCore(function?.Ret) as TypeNode.Fn;

    static DelegateAdapterPlan ReturnedFunctionPlan(ReferenceMetadataIndex references, TypeNode.Fn result, TypeNode.Fn natural, TypeNode.Fn target)
    {
        if (result is { Suspend: false }
            && DelegateSlotCore(target.Ret) is TypeNode.Fqn resultSlot && references.IsClrDelegate(resultSlot))
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
                if (type is TypeNode.Fqn { Args: not null } f && references.IsClrDelegate(f))
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
            var nested = new ReturnedFunction((TypeNode.Fn)Bind(result), ret, (TypeNode.Fn)Bind(source));
            return new(frame, parameters.Length, ret, parameters, captures.Count, captures.ToArray(), ResultFunction: nested);
        }
        return null;
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
