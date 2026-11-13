using System.Reflection;
using DotKt.Bir;

static partial class ClrMemberResolution
{
    internal static void TypedDelegateValueSelfTest(Action<ReferenceMetadataIndex> test)
    {
        var prior = _refs;
        try { test(ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location })); }
        finally { _refs = prior; }
    }

    internal static DelegateAdapterPlan UnitDelegateValuePlan(ReferenceMetadataIndex references,
        TypeNode source, TypeNode target, out TypeNode.Fqn slot)
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
        _refs = references ?? throw new ArgumentNullException(nameof(references));
        if (PhysicalFunctionShape(source) is not TypeNode.Fn {
                Ret: TypeNode.Fqn { Name: "void" or "System.Void", Args: null } } natural
            || PhysicalFunctionShape(target) is not TypeNode.Fn {
                Ret: TypeNode.Fqn { Name: "kotlin.Unit" or "object" or "System.Object", Args: null } } wanted)
            return null;
        slot = BirTypeLowering.DelegateFqnOf(wanted);
        return VoidDelegateAdapterPlan(natural, slot, wanted.Ret);
    }

    internal static MemberRefNode DelegateInvokeReference(ReferenceMetadataIndex references, TypeNode stated)
    {
        _refs = references ?? throw new ArgumentNullException(nameof(references));
        return DelegateInvokeReference(stated);
    }

    static MemberRefNode DelegateInvokeReference(TypeNode stated)
    {
        var physical = BirTypeLowering.LowerType(stated, refBuild: false, force: false, typeArg: false);
        if (physical is TypeNode.Fn fnNode)
            physical = BirTypeLowering.DelegateFqnOf(
                (TypeNode.Fn)BirTypeLowering.LowerFnDelegate(fnNode, refBuild: false, force: false));
        if (physical is not TypeNode.Fqn delegateFqn)
            throw new InvalidOperationException(
                $"bir2cir: a function-type call lowers to {TypeNode.ToJson(physical)}, which is not a named type");
        var open = ResolveOwnerType(delegateFqn)
            ?? throw new InvalidOperationException(
                $"bir2cir: the delegate '{delegateFqn.Name}' does not resolve to a .NET type");
        var invoke = open.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "Invoke").ToList();
        if (invoke.Count != 1)
            throw new InvalidOperationException(
                $"bir2cir: '{delegateFqn.Name}' has {invoke.Count} Invoke declarations, not one");
        return MemberRefOf(invoke[0], MemberRefNode.Kinds.Method, open, delegateFqn.Args,
            ownerArgumentsAreMethodSlots: IsFunctionShape(stated));
    }

    internal static MemberRefNode DelegateConstructorReference(ReferenceMetadataIndex references, TypeNode stated)
    {
        _refs = references ?? throw new ArgumentNullException(nameof(references));
        return DelegateConstructorReference(stated);
    }

    static MemberRefNode DelegateConstructorReference(TypeNode stated)
    {
        var physical = BirTypeLowering.LowerType(stated, refBuild: false, force: false, typeArg: false);
        if (physical is TypeNode.Fn fnNode)
            physical = BirTypeLowering.DelegateFqnOf(
                (TypeNode.Fn)BirTypeLowering.LowerFnDelegate(fnNode, refBuild: false, force: false));
        if (physical is not TypeNode.Fqn delegateFqn)
            throw new InvalidOperationException(
                $"bir2cir: a delegate construction lowers to {TypeNode.ToJson(physical)}, which is not a named type");
        var open = ResolveOwnerType(delegateFqn)
            ?? throw new InvalidOperationException(
                $"bir2cir: the delegate '{delegateFqn.Name}' does not resolve to a .NET type");
        var ctors = open.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.GetParameters().Length == 2).ToList();
        var win = TryPickUniqueCtor(ctors,
            new List<TypeNode> { new TypeNode.Fqn("System.Object"), new TypeNode.Fqn("System.IntPtr") },
            delegateFqn.Args ?? Array.Empty<TypeNode>())
            ?? throw new InvalidOperationException(
                $"bir2cir: '{delegateFqn.Name}' has no unique (object, native int) constructor — "
                + $"{ctors.Count} two-argument candidate(s)");
        return MemberRefOf(win, MemberRefNode.Kinds.Ctor, open,
            delegateFqn.Args ?? Array.Empty<TypeNode>(), ownerArgumentsAreMethodSlots: IsFunctionShape(stated));
    }
}
